using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Construye y envía un INGRESO como FACTURA de compra a una base BAS.
//
// OJO: la API de BAS para la factura de compra es DISTINTA de la del remito de
// ingreso. Este servicio es análogo a BasRemitoIngresoService pero apunta a ese
// otro endpoint y arma el body con los campos que la factura requiere (que
// pueden incluir precios/importes/impuestos por renglón, a diferencia del
// remito, que es sólo movimiento de mercadería).
//
// >>> PENDIENTE DE COMPLETAR con el schema real de BAS <<<
//   - RUTA: el path del endpoint de factura de compra (placeholder abajo).
//   - BODY: los campos de cabecera y de cada item que pide la factura.
//   - PARSEO: ajustar a la forma real de la respuesta (por ahora reusa la misma
//     lógica que el remito, que sirve si la respuesta tiene forma parecida).
//
// Reutiliza el record GrabadoResultado (definido en BasRemitoIngresoService.cs)
// para que el controlador maneje remito y factura de la misma forma.
public class BasFacturaIngresoService
{
    private readonly BasDestinosService _destinos;
    private readonly BasWebApiOptions _cred;
    private readonly ILogger<BasFacturaIngresoService> _log;

    // TODO: confirmar el path real del endpoint de factura de compra en BAS.
    private const string RutaFactura = "/api/FacturasCompra";   // <-- PLACEHOLDER

    public BasFacturaIngresoService(
        BasDestinosService destinos,
        IOptions<BasWebApiOptions> cred,
        ILogger<BasFacturaIngresoService> log)
    {
        _destinos = destinos;
        _cred = cred.Value;
        _log = log;
    }

    // Renglón resuelto contra la base destino. La factura probablemente necesite
    // además precio unitario / importe por renglón; cuando tengamos el schema
    // sumamos esos campos a este record y al body.
    public record RenglonFactura(
        string ProductoCodigo,
        decimal Cantidad,
        string? Partida,
        string? Series,
        BienInfo? Articulo);

    public async Task<GrabadoResultado> GrabarAsync(
        string destino,
        DateTime fecha,
        DateTime? fechaComprobante,
        string? prefijoComprobante,
        long? numeroComprobante,
        string proveedorCodigo,
        string? observaciones,
        IReadOnlyList<RenglonFactura> renglones,
        string usuarioPortal,
        CancellationToken ct = default)
    {
        var cfg = _destinos.Config(destino);
        if (cfg is null)
            return new GrabadoResultado(false, null, null, null, null, null, $"Destino BAS desconocido: {destino}");

        // Usuario de integración (existe en BAS), igual que en el remito.
        var usuarioBas = !string.IsNullOrWhiteSpace(_cred.Usuario) ? _cred.Usuario : usuarioPortal;

        // ================== PENDIENTE: ARMADO DEL BODY DE LA FACTURA ==================
        // Cuando tengamos el schema real, armamos acá la cabecera y los items con
        // los campos que la factura de compra requiere (precios, importes,
        // impuestos, condición, etc.). Por ahora devolvemos un error claro para
        // que, si alguien intenta grabar una factura antes de completar esto, sepa
        // por qué no se grabó (en vez de mandar un body inválido a BAS).
        _log.LogWarning("Intento de grabar FACTURA en {Base}: el servicio de factura todavía no está implementado.", destino);
        return new GrabadoResultado(false, null, null, null, null, null,
            "El grabado de ingresos como FACTURA todavía no está implementado (falta el contrato de la API de factura de BAS).");

        // ----------------------------------------------------------------------------
        // ESQUELETO listo para cuando tengamos el schema (descomentar y completar):
        //
        // var items = new List<object>(renglones.Count);
        // foreach (var r in renglones)
        // {
        //     var (c1, c2) = CalcularCantidades(r.Cantidad, r.Articulo);
        //     var item = new Dictionary<string, object?>
        //     {
        //         ["CodigoItem"] = r.ProductoCodigo,
        //         ["CantidadPrimeraUnidad"] = Str(c1),
        //         ["CantidadSegundaUnidad"] = Str(c2),
        //         ["NumeroUnidadMedida"] = "1",
        //         // ["PrecioUnitario"] = ...,   // <-- campos propios de la factura
        //         // ["Importe"] = ...,
        //     };
        //     if (!string.IsNullOrWhiteSpace(r.Partida)) item["Partida"] = r.Partida!.Trim();
        //     items.Add(item);
        // }
        //
        // var body = new Dictionary<string, object?>
        // {
        //     ["Fecha"] = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        //     ["PrefijoCuentaCorriente"] = "P",
        //     ["CodigoCuentaCorriente"] = proveedorCodigo,
        //     ["Empresa"] = cfg.Empresa,
        //     ["Sucursal"] = cfg.Sucursal,
        //     ["Usuario"] = usuarioBas,
        //     ["Items"] = items
        //     // + tipo/talonario/concepto/impuestos propios de la factura
        // };
        // if (fechaComprobante.HasValue) body["FechaExterna"] = fechaComprobante.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // if (!string.IsNullOrWhiteSpace(prefijoComprobante)) body["PrefijoExterno"] = prefijoComprobante!.Trim();
        // if (numeroComprobante.HasValue) body["NumeroExterno"] = numeroComprobante.Value;
        // if (!string.IsNullOrWhiteSpace(observaciones)) body["ObservacionComprobante"] = observaciones!.Trim();
        //
        // var json = JsonSerializer.Serialize(body);
        // string? respuesta;
        // try { respuesta = await _destinos.PostAsync(destino, RutaFactura, json, ct); }
        // catch (Exception ex) { return new GrabadoResultado(false, null, null, null, null, null, ex.Message); }
        //
        // if (string.IsNullOrWhiteSpace(respuesta))
        //     return new GrabadoResultado(true, null, null, null, null, null, null);
        // return ParsearRespuesta(respuesta);
    }

    // ---- Helpers compartidos (mismos que el remito) ----

    private static (decimal c1, decimal c2) CalcularCantidades(decimal cantidad, BienInfo? art)
    {
        if (art is { DobleUnidadMedida: true } && art.RelacionStock > 0)
        {
            var c2 = Math.Round(cantidad / art.RelacionStock, 4, MidpointRounding.AwayFromZero);
            return (cantidad, c2);
        }
        return (cantidad, cantidad);
    }

    private static string Str(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    // Parseo de la respuesta. Por ahora replica la lógica del remito (IdTransaccion
    // en raíz, Comprobantes[0] con Comprobante/Prefijo/Numero). Se ajustará cuando
    // sepamos la forma real de la respuesta de la factura.
    private GrabadoResultado ParsearRespuesta(string respuesta)
    {
        try
        {
            using var doc = JsonDocument.Parse(respuesta);
            var root = doc.RootElement;

            string? idt = null;
            var idtRaw = BuscarProp(root, "IdTransaccion");
            if (!string.IsNullOrWhiteSpace(idtRaw) && idtRaw != "0") idt = idtRaw;

            var motivo = BuscarProp(root, "Motivo");
            var comp = PrimerComprobante(root);
            string? prefijo = null, numero = null, comprobante = null;
            if (comp.HasValue)
            {
                prefijo = BuscarProp(comp.Value, "Prefijo");
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
            return new GrabadoResultado(true, null, null, null, null, Recortar(respuesta), null);
        }
    }

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
