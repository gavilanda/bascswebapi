using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

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

    public BasEstadisticasVentaService(BasDestinosService destinos, IMemoryCache cache)
    {
        _destinos = destinos;
        _cache = cache;
    }

    // Un comprobante de venta traído de BAS (cabecera).
    public sealed record VentaComprobante(string CodCliente, string Tipo, DateOnly Fecha, decimal Total);

    // Signo de estadística de venta por tipo de comprobante, cacheado por base (~1h).
    // El catálogo de tipos cambia poquísimo, así que no vale la pena pedirlo cada vez.
    public async Task<IReadOnlyDictionary<string, int>> SignosPorTipoAsync(string baseNombre, CancellationToken ct = default)
    {
        var key = $"estadVtaTipos|{baseNombre}";
        if (_cache.TryGetValue(key, out IReadOnlyDictionary<string, int>? cached) && cached is not null)
            return cached;

        var json = await _destinos.GetAsync(baseNombre, "/api/TiposComprobantes?pageSize=500&pageNumber=1", ct);
        var mapa = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

        _cache.Set(key, (IReadOnlyDictionary<string, int>)mapa, TimeSpan.FromHours(1));
        return mapa;
    }

    // Trae TODOS los comprobantes de venta de clientes en el rango [desde, hasta].
    // CONSULTAGRAL/ComprobanteCliente pagina por cliente, así que iteramos páginas
    // hasta que una traiga menos clientes que el pageSize (última).
    // codigos: si viene, filtra a esos códigos de cliente (para la consulta de UN
    // cliente puntual, resuelto por CUIT). null/vacío = todos los clientes.
    public async Task<List<VentaComprobante>> ComprobantesAsync(
        string baseNombre, DateOnly desde, DateOnly hasta,
        IReadOnlyList<string>? codigos = null, CancellationToken ct = default)
    {
        var cfg = _destinos.Config(baseNombre)
            ?? throw new InvalidOperationException($"Destino BAS desconocido: {baseNombre}");

        var lista = new List<VentaComprobante>();

        // BAS se vuelve MUY lento (hasta timeout) con rangos amplios en bases de alto
        // volumen: su SQL procesa TODO el rango, sin importar el pageSize. Un mes solo,
        // en cambio, es rápido. Por eso partimos el rango en tramos MENSUALES y los
        // combinamos: cada comprobante cae en un único mes, así que no hay doble conteo.
        var tramoDesde = desde;
        while (tramoDesde <= hasta)
        {
            var finMes = new DateOnly(tramoDesde.Year, tramoDesde.Month, 1).AddMonths(1).AddDays(-1);
            var tramoHasta = finMes < hasta ? finMes : hasta;
            await TraerTramoAsync(cfg, baseNombre, tramoDesde, tramoHasta, codigos, lista, ct);
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
