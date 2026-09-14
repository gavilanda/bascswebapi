using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;

namespace PortalClientes.Bas;

// Estadísticas de venta contra BAS. Dos piezas:
//  1) Los comprobantes de venta de clientes de un período, vía
//     POST /api/CONSULTAGRAL/ComprobanteCliente (pagina POR CLIENTE), filtrando por
//     el campo Fecha del comprobante (dos filtros: >= desde y <= hasta, dd/mm/yyyy).
//  2) El signo de estadística de cada tipo de comprobante, de
//     GET /api/TiposComprobantes -> campo EstadisticaVta: +1 factura/nota débito,
//     -1 nota crédito, 0/null no cuenta (recibos, etc.).
// El neto de cada comprobante para la estadística es Total * EstadisticaVta, así la
// suma netea automáticamente créditos e ignora lo que no es venta (igual criterio
// que la vista vstaestvtas de BAS).
public class BasEstadisticasVentaService
{
    private readonly BasDestinosService _destinos;
    private readonly IMemoryCache _cache;
    private readonly IHostEnvironment _env;

    public BasEstadisticasVentaService(BasDestinosService destinos, IMemoryCache cache, IHostEnvironment env)
    {
        _destinos = destinos;
        _cache = cache;
        _env = env;
    }

    private static readonly JsonSerializerOptions DiscoOpts = new() { PropertyNameCaseInsensitive = true };

    // Un comprobante de venta traído de BAS (cabecera).
    public sealed record VentaComprobante(string CodCliente, string Tipo, DateOnly Fecha, decimal Total);

    // Signo de estadística de venta por tipo de comprobante, cacheado por base (~1h).
    // El catálogo de tipos cambia poquísimo, así que no vale la pena pedirlo cada vez.
    public async Task<IReadOnlyDictionary<string, int>> SignosPorTipoAsync(string baseNombre, CancellationToken ct = default)
    {
        var key = $"estadVtaTipos|{baseNombre}";
        if (_cache.TryGetValue(key, out IReadOnlyDictionary<string, int>? cached) && cached is not null)
            return cached;

        Dictionary<string, int>? mapa = null;
        try
        {
            var json = await _destinos.GetAsync(baseNombre, "/api/TiposComprobantes?pageSize=500&pageNumber=1", ct);
            mapa = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var arr = PrimerArray(doc.RootElement);
                if (arr is not null)
                    foreach (var t in arr.Value.EnumerateArray())
                    {
                        var cod = LeerString(t, "Comprobante").Trim();
                        if (cod.Length > 0) mapa[cod] = LeerInt(t, "EstadisticaVta");   // null -> 0
                    }
            }
            EscribirSignosDisco(baseNombre, mapa);   // fallback para cuando la base no responda
        }
        // Si la base NO responde (timeout/caída) —pero NO si el usuario canceló— caemos al
        // último catálogo conocido en disco, así los meses YA CACHEADOS se sirven igual sin
        // colgarse esperando la base. El catálogo de tipos cambia poquísimo.
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            mapa = LeerSignosDisco(baseNombre);
            if (mapa is null) throw;   // sin disco no podemos interpretar los comprobantes
        }

        _cache.Set(key, (IReadOnlyDictionary<string, int>)mapa!, TimeSpan.FromHours(1));
        return mapa!;
    }

    // Trae TODOS los comprobantes de venta de clientes en el rango [desde, hasta].
    // CONSULTAGRAL/ComprobanteCliente pagina por cliente, así que iteramos páginas
    // hasta que una traiga menos clientes que el pageSize (última).
    // codigos: si viene, filtra a esos códigos de cliente (para la consulta de UN
    // cliente puntual, resuelto por CUIT). null/vacío = todos los clientes.
    // forzar: ignora el caché (memoria + disco) del período y repega a BAS, reescribiendo
    // el caché de los meses cerrados. Es lo que dispara el botón "Refrescar" del front
    // (p. ej. si en BAS se cargó/corrigió un comprobante con fecha de un mes ya cacheado).
    public async Task<List<VentaComprobante>> ComprobantesAsync(
        string baseNombre, DateOnly desde, DateOnly hasta,
        IReadOnlyList<string>? codigos = null, bool forzar = false, CancellationToken ct = default)
    {
        var cfg = _destinos.Config(baseNombre)
            ?? throw new InvalidOperationException($"Destino BAS desconocido: {baseNombre}");

        var lista = new List<VentaComprobante>();
        var hoy = DateOnly.FromDateTime(DateTime.Today);
        var filtraCliente = codigos is { Count: > 0 };
        var codigosSet = filtraCliente ? new HashSet<string>(codigos!, StringComparer.OrdinalIgnoreCase) : null;

        // BAS se vuelve MUY lento (hasta timeout) con rangos amplios en bases de alto
        // volumen: su SQL procesa TODO el rango, sin importar el pageSize. Un mes solo,
        // en cambio, es rápido. Por eso partimos el rango en tramos MENSUALES y los
        // combinamos: cada comprobante cae en un único mes, así que no hay doble conteo.
        //
        // CACHEAMOS cada mes CERRADO y COMPLETO (memoria ~12 h + disco permanente): un mes
        // que ya terminó no cambia. El caché guarda TODOS los clientes de ese mes, así:
        //  - reconsultar cambiando fechas NO repega a BAS por los meses ya traídos;
        //  - buscar UN cliente sobre un rango ya cacheado se resuelve FILTRANDO EN MEMORIA
        //    (no se vuelve a pegar a BAS). Sólo se consulta lo que falte y el mes en curso.
        var tramoDesde = desde;
        while (tramoDesde <= hasta)
        {
            var finMes = new DateOnly(tramoDesde.Year, tramoDesde.Month, 1).AddMonths(1).AddDays(-1);
            var tramoHasta = finMes < hasta ? finMes : hasta;

            var mesCompleto = tramoDesde.Day == 1 && tramoHasta == finMes;   // el tramo cubre el mes entero
            var mesCerrado = finMes < hoy;                                   // el mes ya terminó
            var key = $"estVtaMes|{baseNombre}|{tramoDesde:yyyy-MM}";

            // LECTURA: cualquier tramo de un mes CERRADO se sirve del caché del MES COMPLETO
            // (recortando por fecha al rango del tramo), sea el mes entero o un pedazo — así un
            // rango con bordes parciales (ej. hasta el 15) igual sale del caché. Con 'forzar'
            // salteamos la lectura para repegar a BAS. El mes EN CURSO nunca se cachea.
            List<VentaComprobante>? mesAll = null;
            if (mesCerrado && !forzar)
            {
                if (!_cache.TryGetValue(key, out mesAll) || mesAll is null)
                {
                    mesAll = LeerDisco(baseNombre, tramoDesde);
                    if (mesAll is not null) _cache.Set(key, mesAll, TimeSpan.FromHours(12));
                }
            }

            if (mesAll is not null)
            {
                // Recortamos por fecha (si es un pedazo del mes) y por cliente (si se filtró).
                var dT = tramoDesde; var hT = tramoHasta;
                IEnumerable<VentaComprobante> q = mesAll;
                if (!mesCompleto) q = q.Where(c => c.Fecha >= dT && c.Fecha <= hT);
                if (filtraCliente) q = q.Where(c => codigosSet!.Contains(c.CodCliente));
                lista.AddRange(q);
            }
            else if (mesCerrado && !filtraCliente)
            {
                // Mes cerrado sin filtro y sin caché (o forzando): traemos el MES COMPLETO
                // —a BAS le cuesta lo mismo que un pedazo— lo cacheamos, y recortamos al tramo.
                var primerDia = new DateOnly(tramoDesde.Year, tramoDesde.Month, 1);
                var delMes = new List<VentaComprobante>();
                await TraerTramoAsync(cfg, baseNombre, primerDia, finMes, codigos, delMes, ct);
                _cache.Set(key, delMes, TimeSpan.FromHours(12));
                EscribirDisco(baseNombre, tramoDesde, delMes);
                lista.AddRange(mesCompleto ? delMes : delMes.Where(c => c.Fecha >= tramoDesde && c.Fecha <= tramoHasta));
            }
            else
            {
                // Mes EN CURSO, o filtrado por un cliente puntual: traemos solo el tramo pedido
                // (no se cachea; con cliente va liviano por FiltroCodigos).
                var delTramo = new List<VentaComprobante>();
                await TraerTramoAsync(cfg, baseNombre, tramoDesde, tramoHasta, codigos, delTramo, ct);
                // Al refrescar con filtro un mes cerrado, invalidamos su caché de mes completo
                // para que la próxima consulta sin filtro lo traiga fresco.
                if (mesCerrado && filtraCliente && forzar)
                {
                    _cache.Remove(key);
                    BorrarDisco(baseNombre, tramoDesde);
                }
                lista.AddRange(delTramo);
            }

            tramoDesde = tramoHasta.AddDays(1);
        }

        return lista;
    }

    // Trae (paginando por cliente) los comprobantes de un tramo acotado y los agrega a
    // 'lista'. CONSULTAGRAL/ComprobanteCliente pagina por cliente: iteramos hasta que
    // una página traiga menos clientes que el pageSize.
    private async Task TraerTramoAsync(
        DestinoBas cfg, string baseNombre, DateOnly desde, DateOnly hasta,
        IReadOnlyList<string>? codigos, List<VentaComprobante> lista, CancellationToken ct)
    {
        const int pageSize = 200;

        for (int page = 1; page <= 500; page++)   // tope de seguridad
        {
            var body = ConstruirConsulta(cfg, desde, hasta, codigos, pageSize, page);
            var json = await _destinos.PostAsync(baseNombre, "/api/CONSULTAGRAL/ComprobanteCliente", body, ct);
            if (string.IsNullOrWhiteSpace(json)) break;

            using var doc = JsonDocument.Parse(json);
            VerificarError(doc.RootElement);   // lanza si BAS devolvió un error en Cuerpo

            var clientes = ArrayEnCuerpo(doc.RootElement);
            if (clientes is null) break;

            int cuenta = 0;
            foreach (var cli in clientes.Value.EnumerateArray())
            {
                cuenta++;
                var cod = LeerString(cli, "Codigo").Trim();
                if (!cli.TryGetProperty("Comprobantes", out var comps) || comps.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var c in comps.EnumerateArray())
                {
                    var tipo = LeerString(c, "Comprobante").Trim();
                    var f = ParseFecha(LeerString(c, "Fecha"));
                    if (f is null) continue;
                    lista.Add(new VentaComprobante(cod, tipo, f.Value, LeerDecimal(c, "Total")));
                }
            }

            if (cuenta < pageSize) break;   // última página del tramo
        }
    }

    // ---- Armado del cuerpo CONSULTAGRAL ----
    private static string ConstruirConsulta(
        DestinoBas cfg, DateOnly desde, DateOnly hasta, IReadOnlyList<string>? codigos, int pageSize, int page)
    {
        // ConsultaGral como diccionario para poder incluir FiltroCodigos sólo cuando aplica.
        var consulta = new Dictionary<string, object?>
        {
            // La entidad principal es el cliente; los datos del comprobante son un
            // grupo de información (por eso van en SelectGrupoInformacion).
            ["SelectDatosPrimarios"] = new[] { new { Nombre = "Codigo" } },
            ["SelectGrupoInformacion"] = new[]
            {
                new { Nombre = "Comprobante" }, new { Nombre = "Fecha" }, new { Nombre = "Total" }
            },
            // Rango por fecha de comprobante. Comparacion 4 = >=, 5 = <=. Las fechas van
            // en dd/mm/yyyy (el modo "entre" con coma rompe la conversión en BAS).
            ["FiltrosAdicionales"] = new[]
            {
                new { TagEntidad = "ComprobanteCliente", NombreCampo = "Fecha", Comparacion = "4", Valor = desde.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) },
                new { TagEntidad = "ComprobanteCliente", NombreCampo = "Fecha", Comparacion = "5", Valor = hasta.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) }
            },
            ["pageSize"] = pageSize,
            ["pageNumber"] = page
        };

        // Filtro por código de cliente (para consultar UN cliente puntual).
        if (codigos is { Count: > 0 })
            consulta["FiltroCodigos"] = codigos.Select(c => new { Codigo = c }).ToArray();

        var body = new
        {
            HEADER = new
            {
                ETIQUETA = "CONSULTAGRAL",
                CODEMP = cfg.Empresa,
                CODSUC = cfg.Sucursal,
                FECHA = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            },
            ConsultaGral = consulta
        };
        return JsonSerializer.Serialize(body);
    }

    // ---- Caché en disco (meses cerrados; sobrevive al reinicio del servicio) ----
    // Un archivo por base y mes en {ContentRoot}/cache-ventas/. Sólo se escriben meses
    // cerrados+completos (no cambian). Para forzar un refresco: borrar esa carpeta.
    private string RutaMes(string baseNombre, DateOnly mes)
        => Path.Combine(_env.ContentRootPath, "cache-ventas", $"{baseNombre}-{mes:yyyy-MM}.json");

    private List<VentaComprobante>? LeerDisco(string baseNombre, DateOnly mes)
    {
        try
        {
            var ruta = RutaMes(baseNombre, mes);
            if (!File.Exists(ruta)) return null;
            return JsonSerializer.Deserialize<List<VentaComprobante>>(File.ReadAllText(ruta), DiscoOpts);
        }
        catch { return null; }
    }

    private void EscribirDisco(string baseNombre, DateOnly mes, List<VentaComprobante> datos)
    {
        try
        {
            var dir = Path.Combine(_env.ContentRootPath, "cache-ventas");
            Directory.CreateDirectory(dir);
            File.WriteAllText(RutaMes(baseNombre, mes), JsonSerializer.Serialize(datos, DiscoOpts));
        }
        catch { /* best-effort: si falla el disco, seguimos con memoria */ }
    }

    private void BorrarDisco(string baseNombre, DateOnly mes)
    {
        try { var ruta = RutaMes(baseNombre, mes); if (File.Exists(ruta)) File.Delete(ruta); }
        catch { /* best-effort */ }
    }

    // Signos de tipos de comprobante en disco (fallback cuando la base no responde): así un
    // mes ya cacheado se sirve completo sin colgarse pidiendo el catálogo a una base caída.
    private string RutaSignos(string baseNombre)
        => Path.Combine(_env.ContentRootPath, "cache-ventas", $"{baseNombre}-tipos.json");

    private Dictionary<string, int>? LeerSignosDisco(string baseNombre)
    {
        try
        {
            var ruta = RutaSignos(baseNombre);
            if (!File.Exists(ruta)) return null;
            var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ruta), DiscoOpts);
            return d is null ? null : new Dictionary<string, int>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    private void EscribirSignosDisco(string baseNombre, Dictionary<string, int> datos)
    {
        try
        {
            var dir = Path.Combine(_env.ContentRootPath, "cache-ventas");
            Directory.CreateDirectory(dir);
            File.WriteAllText(RutaSignos(baseNombre), JsonSerializer.Serialize(datos, DiscoOpts));
        }
        catch { /* best-effort */ }
    }

    // ---- Helpers de parseo tolerante ----

    // Si CONSULTAGRAL falló, "Cuerpo" viene como string con el mensaje. Lo detectamos
    // y lanzamos para que el controller marque esa base como fallida (sin tumbar el resto).
    private static void VerificarError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("Cuerpo", out var cuerpo) && cuerpo.ValueKind == JsonValueKind.String)
        {
            var msg = cuerpo.GetString();
            if (!string.IsNullOrWhiteSpace(msg))
                throw new InvalidOperationException("BAS: " + msg);
        }
    }

    // Primer array dentro de "Cuerpo" (respuesta de CONSULTAGRAL: Cuerpo.CLIENTES).
    private static JsonElement? ArrayEnCuerpo(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("Cuerpo", out var cuerpo) || cuerpo.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in cuerpo.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array) return p.Value;
        return null;
    }

    // Para respuestas REST que son un array directo (TiposComprobantes) o un objeto
    // con un array adentro.
    private static JsonElement? PrimerArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array) return p.Value;
        return null;
    }

    private static string LeerString(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString())
            : "";

    private static int LeerInt(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : (int)Math.Round(v.GetDouble()),
            JsonValueKind.String => int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
            _ => 0
        };
    }

    private static decimal LeerDecimal(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return 0m;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m,
            _ => 0m
        };
    }

    private static DateOnly? ParseFecha(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = s.Split('T')[0].Split(' ')[0];   // "2026-07-01T..." -> "2026-07-01"
        return DateOnly.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;
    }
}
