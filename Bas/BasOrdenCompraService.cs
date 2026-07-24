using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Graba una Orden de Compra en BAS (POST /api/OrdenesCompra) y trae del proveedor los
// datos que la OC necesita (Comprador y CondicionCompra). Espejo (más simple) del
// grabado de ingresos. La respuesta de BAS es la misma forma que RemitosIngreso
// (RespuestaComprobantes: IdTransaccion + Comprobantes[]), así que el número interno
// (Nrotrans) sale de IdTransaccion. Reusa el record GrabadoResultado.
public class BasOrdenCompraService
{
    private readonly BasDestinosService _destinos;
    private readonly BasWebApiOptions _cred;
    private readonly ILogger<BasOrdenCompraService> _log;

    public BasOrdenCompraService(
        BasDestinosService destinos, IOptions<BasWebApiOptions> cred, ILogger<BasOrdenCompraService> log)
    {
        _destinos = destinos;
        _cred = cred.Value;
        _log = log;
    }

    // Datos del proveedor en una base para la OC. null si no se pudo traer.
    public async Task<(string razonSocial, string? comprador, string? condicionCompra)?> TraerProveedorAsync(
        string destino, string codigo, CancellationToken ct = default)
    {
        var json = await _destinos.GetAsync(destino, "/api/Proveedores/" + Uri.EscapeDataString(codigo.Trim()), ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (BuscarProp(root, "RazonSocial") ?? "", BuscarProp(root, "Comprador"), BuscarProp(root, "CondicionCompra"));
    }

    // ---- Órdenes de compra PENDIENTES de entrega (para el ingreso contra OC) ----
    // Cabecera + ítems pendientes de una OC. El saldo pendiente lo calcula BAS
    // (el endpoint /api/OrdenesCompraPendientes ya descuenta lo remitido/facturado).
    public record PendienteOC(
        long Nrotrans, DateTime? Fecha, string? Prefijo, int? Numero,
        string? ProveedorCodigo, string? ProveedorNombre, List<PendienteOCItem> Items);

    public record PendienteOCItem(
        int Secuencia, string CodItm, string? Descripcion, decimal Cantidad, string? Unidad, DateTime? FechaEntrega);

    // Trae las OC pendientes de un proveedor en una base. Vacío si no hay o falla.
    public async Task<IReadOnlyList<PendienteOC>> TraerPendientesAsync(
        string destino, string proveedorCodigo, CancellationToken ct = default)
    {
        var cfg = _destinos.Config(destino);
        if (cfg is null) return Array.Empty<PendienteOC>();

        var ruta = $"/api/OrdenesCompraPendientes?Codemp={cfg.Empresa}&Codsuc={cfg.Sucursal}"
            + $"&Filtro={Uri.EscapeDataString(proveedorCodigo.Trim())}&TipoBusqueda=C&FiltrarSucursal=N";

        var json = await _destinos.GetAsync(destino, ruta, ct);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PendienteOC>();

        var lista = new List<PendienteOC>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return lista;

        foreach (var oc in doc.RootElement.EnumerateArray())
        {
            var items = new List<PendienteOCItem>();
            if (TryArray(oc, "Items", out var arr))
                foreach (var it in arr.EnumerateArray())
                {
                    var cod = BuscarProp(it, "CodItm");
                    if (string.IsNullOrWhiteSpace(cod)) continue;
                    var cant = ParseDec(BuscarProp(it, "Cantidad1"));
                    if (cant <= 0) continue;   // sin saldo pendiente, no interesa
                    items.Add(new PendienteOCItem(
                        ParseInt(BuscarProp(it, "Secuencia")) ?? 0,
                        cod!.Trim(),
                        BuscarProp(it, "Descripcion"),
                        cant,
                        BuscarProp(it, "NroUniMed"),
                        ParseDate(BuscarProp(it, "FechaEnt"))));
                }
            if (items.Count == 0) continue;    // OC sin ítems pendientes -> se omite

            lista.Add(new PendienteOC(
                ParseLong(BuscarProp(oc, "NroTrans")) ?? 0,
                ParseDate(BuscarProp(oc, "Fecha")),
                BuscarProp(oc, "Prefijo"),
                ParseInt(BuscarProp(oc, "Numero")),
                BuscarProp(oc, "Codctacte"),
                BuscarProp(oc, "Nombre"),
                items));
        }
        return lista;
    }

    private static bool TryArray(JsonElement el, string nombre, out JsonElement arr)
    {
        arr = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, nombre, StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.Array)
            { arr = p.Value; return true; }
        return false;
    }

    private static long? ParseLong(string? s)
        => long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static int? ParseInt(string? s)
        => int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static decimal ParseDec(string? s)
        => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    private static DateTime? ParseDate(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;

    // Un renglón ya listo para grabar.
    public record RenglonOC(string ProductoCodigo, decimal Cantidad, decimal PrecioUnitario, decimal TasaIva);

    public async Task<GrabadoResultado> GrabarAsync(
        string destino, DateTime fecha, DateTime? fechaExpiracion, int codigoMoneda,
        string proveedorCodigo, string? comprador, string? condicionCompra,
        string? observaciones, string? observacionEntrega,
        IReadOnlyList<RenglonOC> renglones, string usuarioPortal, CancellationToken ct = default)
    {
        var cfg = _destinos.Config(destino);
        if (cfg is null)
            return new GrabadoResultado(false, null, null, null, null, null, $"Destino BAS desconocido: {destino}");

        var usuarioBas = !string.IsNullOrWhiteSpace(_cred.Usuario) ? _cred.Usuario : usuarioPortal;

        decimal totGravado = 0m, totIva = 0m, total = 0m;
        var items = new List<object>(renglones.Count);
        foreach (var r in renglones)
        {
            var gravado = Math.Round(r.Cantidad * r.PrecioUnitario, 2, MidpointRounding.AwayFromZero);
            var iva = Math.Round(gravado * r.TasaIva / 100m, 2, MidpointRounding.AwayFromZero);
            var totLinea = gravado + iva;
            totGravado += gravado; totIva += iva; total += totLinea;

            items.Add(new Dictionary<string, object?>
            {
                ["CodigoItem"] = r.ProductoCodigo,
                ["CantidadPrimeraUnidad"] = Str(r.Cantidad),
                ["CantidadSegundaUnidad"] = Str(r.Cantidad),   // 1ª = 2ª (doble unidad a resolver a futuro)
                ["NumeroUnidadMedida"] = "1",
                ["PrecioUnitario"] = Str(r.PrecioUnitario),
                ["TasaIva"] = Str(r.TasaIva),
                ["ImporteGravado"] = Str(gravado),
                ["ImporteIva"] = Str(iva),
                ["ImporteTotal"] = Str(totLinea)
            });
        }

        var body = new Dictionary<string, object?>
        {
            ["Fecha"] = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Prefijo"] = cfg.OrdenCompraPrefijo,      // talonario -> numeración automática de BAS
            ["Proveedor"] = proveedorCodigo,
            ["CodigoMoneda"] = codigoMoneda,
            ["Empresa"] = cfg.Empresa,
            ["Sucursal"] = cfg.Sucursal,
            ["Usuario"] = usuarioBas,
            ["TotalGravado"] = Str(totGravado),
            ["TotalIva"] = Str(totIva),
            ["Total"] = Str(total),
            ["Items"] = items
        };
        if (!string.IsNullOrWhiteSpace(comprador)) body["Comprador"] = comprador!.Trim();
        if (!string.IsNullOrWhiteSpace(condicionCompra)) body["CondicionVentaCompra"] = condicionCompra!.Trim();
        if (fechaExpiracion.HasValue)
            body["FechaExpiracion"] = fechaExpiracion.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(observaciones)) body["ObservacionComprobante"] = observaciones!.Trim();
        if (!string.IsNullOrWhiteSpace(observacionEntrega)) body["ObservacionEntrega"] = observacionEntrega!.Trim();

        var json = JsonSerializer.Serialize(body);

        string? respuesta;
        try
        {
            respuesta = await _destinos.PostAsync(destino, "/api/OrdenesCompra?IgnoraAdvertencias=true", json, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Grabado de OC en BAS ({Base}) falló: {Msg}", destino, ex.Message);
            return new GrabadoResultado(false, null, null, null, null, null, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(respuesta))
            return new GrabadoResultado(true, null, cfg.OrdenCompraPrefijo, null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(respuesta);
            var root = doc.RootElement;

            string? idt = null;
            var idtRaw = BuscarProp(root, "IdTransaccion");
            if (!string.IsNullOrWhiteSpace(idtRaw) && idtRaw != "0") idt = idtRaw;

            var motivo = BuscarProp(root, "Motivo");

            var comp = PrimerComprobante(root);
            string? prefijo = cfg.OrdenCompraPrefijo, numero = null, comprobante = null;
            if (comp.HasValue)
            {
                prefijo = BuscarProp(comp.Value, "Prefijo") ?? cfg.OrdenCompraPrefijo;
                numero = BuscarProp(comp.Value, "Numero") ?? BuscarProp(comp.Value, "NumeroFinal");
                comprobante = BuscarProp(comp.Value, "Comprobante");
            }

            var sinDatos = idt is null && numero is null && !comp.HasValue;
            if (sinDatos && !string.IsNullOrWhiteSpace(motivo))
                return new GrabadoResultado(false, null, null, null, null, Recortar(respuesta), motivo);

            return new GrabadoResultado(true, idt, prefijo, numero, comprobante, Recortar(respuesta), null);
        }
        catch
        {
            return new GrabadoResultado(true, null, cfg.OrdenCompraPrefijo, null, null, Recortar(respuesta), null);
        }
    }

    private static string Str(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    private static string? BuscarProp(JsonElement el, string nombre)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, nombre, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => p.Value.ToString()
                };
        return null;
    }

    private static JsonElement? PrimerComprobante(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, "Comprobantes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, "Comprobante", StringComparison.OrdinalIgnoreCase))
            {
                if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                    return p.Value[0];
                if (p.Value.ValueKind == JsonValueKind.Object)
                    return p.Value;
            }
        return null;
    }

    private static string Recortar(string s) => s.Length > 4000 ? s.Substring(0, 4000) : s;
}
