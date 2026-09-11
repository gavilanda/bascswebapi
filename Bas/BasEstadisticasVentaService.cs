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

    // ---- KILOS vendidos (desde la vista BI_VENTAS) ----
    // Los kilos NO están en la cabecera del comprobante ni en ComprobanteCliente: viven en el
    // renglón (Cantidad2 = segunda unidad de medida = kg). La vista BI_VENTAS los expone por
    // línea de venta, con las notas de crédito YA en negativo, así que un SUM(Cantidad2) da el
    // kg NETO sin aplicar signos. Agregamos por cliente y mes, con el MISMO esquema de tramos
    // mensuales + caché que ComprobantesAsync (los meses cerrados no cambian).

    public sealed record VentaKg(string Mes, string CodCliente, decimal Kilos);

    public async Task<List<VentaKg>> KilosPorClienteMesAsync(
        string baseNombre, DateOnly desde, DateOnly hasta,
        IReadOnlyList<string>? codigos = null, bool forzar = false, CancellationToken ct = default)
    {
        var lista = new List<VentaKg>();
        var hoy = DateOnly.FromDateTime(DateTime.Today);
        var filtraCliente = codigos is { Count: > 0 };

        var tramoDesde = desde;
        while (tramoDesde <= hasta)
        {
            var finMes = new DateOnly(tramoDesde.Year, tramoDesde.Month, 1).AddMonths(1).AddDays(-1);
            var tramoHasta = finMes < hasta ? finMes : hasta;
            var mesCompleto = tramoDesde.Day == 1 && tramoHasta == finMes;
            var mesCerrado = finMes < hoy;
            var mesStr = tramoDesde.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var key = $"estKgMes|{baseNombre}|{mesStr}";

            Dictionary<string, decimal>? delTramo = null;

            // Mes cerrado y COMPLETO, sin filtro de cliente: se cachea (igual que el importe).
            if (mesCerrado && mesCompleto && !filtraCliente && !forzar)
            {
                if (!_cache.TryGetValue(key, out delTramo) || delTramo is null)
                {
                    delTramo = LeerKgDisco(baseNombre, tramoDesde);
                    if (delTramo is not null) _cache.Set(key, delTramo, TimeSpan.FromHours(12));
                }
            }

            if (delTramo is null)
            {
                delTramo = await TraerKgTramoAsync(baseNombre, tramoDesde, tramoHasta, codigos, ct);
                if (mesCerrado && mesCompleto && !filtraCliente)
                {
                    _cache.Set(key, delTramo, TimeSpan.FromHours(12));
                    EscribirKgDisco(baseNombre, tramoDesde, delTramo);
                }
            }

            foreach (var kv in delTramo)
                lista.Add(new VentaKg(mesStr, kv.Key, kv.Value));

            tramoDesde = tramoHasta.AddDays(1);
        }
        return lista;
    }

    // ---- Estadística COMPLETA desde BI_VENTAS (fuente única: importe + kg + comprobantes) ----
    // Una sola consulta por rango, agrupada por mes+cliente. Reconciliado al centavo contra
    // ComprobanteCliente (SUM(ImporteNeto+Impuestos) neteado = Total×signo). Si la base no tiene
    // la vista BI_VENTAS, ConsultarSqlAsync lanza y el controller cae al camino ComprobanteCliente.
    public sealed record VentaBiFila(string Mes, string CodCliente, decimal Importe, decimal Kilos, int Comprobantes);

    public async Task<List<VentaBiFila>> VentasBiAsync(
        string baseNombre, DateOnly desde, DateOnly hasta,
        IReadOnlyList<string>? codigos = null, CancellationToken ct = default)
    {
        var d = desde.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hExcl = hasta.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var filtro = "";
        if (codigos is { Count: > 0 })
        {
            var ins = string.Join(",", codigos
                .Select(c => (c ?? "").Trim().Replace("'", "''"))
                .Where(c => c.Length > 0).Select(c => $"'{c}'"));
            if (ins.Length > 0) filtro = $" AND LTRIM(RTRIM(CodCliente)) IN ({ins})";
        }
        // TOP 100000 OBLIGATORIO (sin TOP corta en 100 filas en silencio). ImporteNeto+Impuestos
        // = Total con IVA; las NC ya vienen negativas, así que SUM netea sola.
        var sql =
            "SELECT TOP 100000 CONVERT(char(7), Fecha, 120) AS Mes, LTRIM(RTRIM(CodCliente)) AS Cod, " +
            "SUM(ImporteNeto + Impuestos) AS Imp, SUM(Cantidad2) AS Kg, COUNT(DISTINCT NroTransaccion) AS NComp " +
            $"FROM BI_VENTAS WHERE Fecha >= '{d}' AND Fecha < '{hExcl}'{filtro} " +
            "GROUP BY CONVERT(char(7), Fecha, 120), LTRIM(RTRIM(CodCliente))";

        var xml = await ConsultarSqlAsync(baseNombre, sql, ct);
        var res = new List<VentaBiFila>();
        foreach (var f in LeerFilasSql(xml))
        {
            var mes = f.TryGetValue("Mes", out var m) ? m.Trim() : "";
            var cod = f.TryGetValue("Cod", out var c) ? c.Trim() : "";
            if (mes.Length != 7 || cod.Length == 0) continue;
            decimal imp = f.TryGetValue("Imp", out var i) && decimal.TryParse(i, NumberStyles.Any, CultureInfo.InvariantCulture, out var vi) ? vi : 0m;
            decimal kg = f.TryGetValue("Kg", out var k) && decimal.TryParse(k, NumberStyles.Any, CultureInfo.InvariantCulture, out var vk) ? vk : 0m;
            int nc = f.TryGetValue("NComp", out var n) && int.TryParse(n, NumberStyles.Any, CultureInfo.InvariantCulture, out var vn) ? vn : 0;
            res.Add(new VentaBiFila(mes, cod, imp, kg, nc));
        }
        return res;
    }

    // Un tramo de kilos: SUM(Cantidad2) por cliente en [desde, hasta] desde BI_VENTAS.
    private async Task<Dictionary<string, decimal>> TraerKgTramoAsync(
        string baseNombre, DateOnly desde, DateOnly hasta, IReadOnlyList<string>? codigos, CancellationToken ct)
    {
        var d = desde.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hExcl = hasta.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);   // < día siguiente = inclusive
        var filtro = "";
        if (codigos is { Count: > 0 })
        {
            var ins = string.Join(",", codigos
                .Select(c => (c ?? "").Trim().Replace("'", "''"))
                .Where(c => c.Length > 0)
                .Select(c => $"'{c}'"));
            if (ins.Length > 0) filtro = $" AND LTRIM(RTRIM(CodCliente)) IN ({ins})";
        }
        // TOP 100000 OBLIGATORIO: sin TOP, CONSULTAGRAL/SQL corta en 100 filas EN SILENCIO
        // (subcontaría si hay >100 clientes en el tramo). Ver memoria bas-webapi-consultas-sql.
        var sql =
            "SELECT TOP 100000 LTRIM(RTRIM(CodCliente)) AS Cod, SUM(Cantidad2) AS Kg FROM BI_VENTAS " +
            $"WHERE Fecha >= '{d}' AND Fecha < '{hExcl}'{filtro} " +
            "GROUP BY LTRIM(RTRIM(CodCliente)) HAVING SUM(Cantidad2) <> 0";

        var xml = await ConsultarSqlAsync(baseNombre, sql, ct);
        var res = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var fila in LeerFilasSql(xml))
        {
            var cod = fila.TryGetValue("Cod", out var c) ? c.Trim() : "";
            if (cod.Length == 0) continue;
            var kg = fila.TryGetValue("Kg", out var k) &&
                     decimal.TryParse(k, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
            res[cod] = res.TryGetValue(cod, out var x) ? x + kg : kg;
        }
        return res;
    }

    // POST a CONSULTAGRAL/SQL -> devuelve el XML de "Cuerpo" ("" si no hubo filas).
    private async Task<string> ConsultarSqlAsync(string baseNombre, string sql, CancellationToken ct)
    {
        var cfg = _destinos.Config(baseNombre)
            ?? throw new InvalidOperationException($"Destino BAS desconocido: {baseNombre}");
        var body = JsonSerializer.Serialize(new
        {
            HEADER = new
            {
                ETIQUETA = "CONSULTAGRAL",
                CODEMP = cfg.Empresa,
                CODSUC = cfg.Sucursal,
                FECHA = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            },
            ConsultaGral = new { SQL = sql, Timeout = 120 }
        });
        var respuesta = await _destinos.PostAsync(baseNombre, "/api/CONSULTAGRAL/SQL", body, ct);
        if (string.IsNullOrWhiteSpace(respuesta)) return "";
        using var doc = JsonDocument.Parse(respuesta);
        if (!doc.RootElement.TryGetProperty("Cuerpo", out var cuerpoEl)) return "";
        var cuerpo = cuerpoEl.GetString() ?? "";
        if (cuerpo.Length == 0) return "";
        if (!cuerpo.Contains('<'))
        {
            // "Data is Null" = sin filas (válido). Cualquier otro texto = error de BAS
            // (p.ej. la base no tiene la vista BI_VENTAS) -> lanza para que el caller haga fallback.
            if (cuerpo.Contains("Data is Null", StringComparison.OrdinalIgnoreCase)) return "";
            throw new InvalidOperationException("BAS rechazó la consulta: " + cuerpo);
        }
        return cuerpo;
    }

    // El XML es <ConsultaGral><fila><CAMPO>valor</CAMPO>...</fila>...</ConsultaGral>.
    private static IEnumerable<Dictionary<string, string>> LeerFilasSql(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) yield break;
        System.Xml.Linq.XDocument doc;
        try { doc = System.Xml.Linq.XDocument.Parse(xml); }
        catch { yield break; }
        var root = doc.Root;
        if (root is null) yield break;
        foreach (var fila in root.Elements())
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var campo in fila.Elements())
                d[campo.Name.LocalName] = campo.Value;
            if (d.Count > 0) yield return d;
        }
    }

    // Caché en disco de los kilos por mes (cod cliente -> kg), meses cerrados.
    private string RutaMesKg(string baseNombre, DateOnly mes)
        => Path.Combine(_env.ContentRootPath, "cache-ventas", $"{baseNombre}-{mes:yyyy-MM}-kg.json");

    private Dictionary<string, decimal>? LeerKgDisco(string baseNombre, DateOnly mes)
    {
        try
        {
            var ruta = RutaMesKg(baseNombre, mes);
            if (!File.Exists(ruta)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, decimal>>(File.ReadAllText(ruta), DiscoOpts);
        }
        catch { return null; }
    }

    private void EscribirKgDisco(string baseNombre, DateOnly mes, Dictionary<string, decimal> datos)
    {
        try
        {
            var dir = Path.Combine(_env.ContentRootPath, "cache-ventas");
            Directory.CreateDirectory(dir);
            File.WriteAllText(RutaMesKg(baseNombre, mes), JsonSerializer.Serialize(datos, DiscoOpts));
        }
        catch { /* best-effort */ }
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
