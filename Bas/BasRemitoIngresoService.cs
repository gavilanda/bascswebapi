using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Resultado del grabado de un remito de ingreso en BAS.
//   Ok            -> se creó el comprobante.
//   IdTransaccion -> identificador que devuelve BAS (si lo informa, distinto de 0).
//   Prefijo/Numero-> comprobante asignado por el talonario (si lo informa).
//   Comprobante   -> tipo/letra del comprobante (si lo informa).
//   RespuestaCruda-> el JSON completo que devolvió BAS (para guardar/diagnosticar).
//   Error         -> mensaje si falló.
public record GrabadoResultado(
    bool Ok,
    string? IdTransaccion,
    string? Prefijo,
    string? Numero,
    string? Comprobante,
    string? RespuestaCruda,
    string? Error);

// Construye y envía un remito de ingreso (compra) a una base BAS.
// El body se arma con el subconjunto MÍNIMO de campos que BAS necesita; el resto
// del esquema es opcional. BAS espera la mayoría de los valores como strings.
//
// Cabecera fija (por destino, configurable en DestinoBas):
//   Prefijo="1" (talonario 15, numeración automática -> NO enviamos Numero),
//   Tipo="N", Concepto="com", DepositoIngreso=1, PrefijoCuentaCorriente="P".
// Por ítem:
//   CantidadPrimeraUnidad = cantidad tipeada,
//   CantidadSegundaUnidad = cantidad ÷ relación (si doble unidad) ó = cantidad,
//   NumeroUnidadMedida="1", Partida y ExplosionSeries cuando corresponde.
//
// Usuario que se manda a BAS: el usuario de INTEGRACIÓN (BasWebApi.Usuario), no
// el del portal. El usuario del portal no necesariamente existe en BAS; la
// trazabilidad de quién cargó el remito queda en la auditoría del portal.
//
// Respuesta de BAS (201 Created), forma confirmada:
//   { IdTransaccion, Eliminable, Motivo, Comprobantes: [ { Comprobante, Prefijo,
//     Numero, NumeroFinal, ... } ] }
public class BasRemitoIngresoService
{
    private readonly BasDestinosService _destinos;
    private readonly BasWebApiOptions _cred;
    private readonly ILogger<BasRemitoIngresoService> _log;

    public BasRemitoIngresoService(
        BasDestinosService destinos,
        IOptions<BasWebApiOptions> cred,
        ILogger<BasRemitoIngresoService> log)
    {
        _destinos = destinos;
        _cred = cred.Value;
        _log = log;
    }

    // Datos de un renglón ya resueltos contra la base destino (trae el BienInfo
    // para poder calcular la segunda unidad). 'Articulo' puede ser null si la
    // base no devolvió datos del artículo (igual se graba con la 1ª unidad).
    public record RenglonGrabado(
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
        IReadOnlyList<RenglonGrabado> renglones,
        string usuarioPortal,
        CancellationToken ct = default)
    {
        var cfg = _destinos.Config(destino);
        if (cfg is null)
            return new GrabadoResultado(false, null, null, null, null, null, $"Destino BAS desconocido: {destino}");

        // Usuario que registra el comprobante en BAS: el de integración. Ese sí
        // existe en BAS. Si por config estuviera vacío, caemos al del portal.
        var usuarioBas = !string.IsNullOrWhiteSpace(_cred.Usuario) ? _cred.Usuario : usuarioPortal;

        // ---- Items ----
        var items = new List<object>(renglones.Count);
        foreach (var r in renglones)
        {
            var (c1, c2) = CalcularCantidades(r.Cantidad, r.Articulo);

            var item = new Dictionary<string, object?>
            {
                ["CodigoItem"] = r.ProductoCodigo,
                ["CantidadPrimeraUnidad"] = Str(c1),
                ["CantidadSegundaUnidad"] = Str(c2),
                ["NumeroUnidadMedida"] = "1"
            };

            // La partida se envía SÓLO si el producto administra partidas EN ESTA BASE.
            // Si la base no maneja partidas para ese producto, se graba igual sin
            // partida (evita el rechazo de BAS al mandar partida en un bien que no
            // la administra). Si no se pudo resolver el artículo, se omite por las dudas.
            if (!string.IsNullOrWhiteSpace(r.Partida) && (r.Articulo?.AdministraPartidas ?? false))
                item["Partida"] = r.Partida!.Trim();

            // Series: texto libre separado por coma/; -> ExplosionSeries.
            var series = PartirSeries(r.Series);
            if (series.Count > 0)
            {
                // Repartimos la cantidad entre las series informadas (1 por serie si
                // coincide la cantidad; si no, mandamos cada serie con cantidad 1 y
                // BAS valida). Mantenemos simple: una unidad por serie.
                item["ExplosionSeries"] = series.Select(s => new Dictionary<string, object?>
                {
                    ["NumeroSerie"] = s,
                    ["CantidadPrimeraUnidad"] = Str(1m),
                    ["CantidadSegundaUnidad"] = Str(1m)
                }).ToList();
            }

            items.Add(item);
        }

        // ---- Cabecera ----
        var body = new Dictionary<string, object?>
        {
            ["Fecha"] = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["PrefijoCuentaCorriente"] = "P",
            ["CodigoCuentaCorriente"] = proveedorCodigo,
            ["Concepto"] = cfg.RemitoConcepto,
            ["Tipo"] = cfg.RemitoTipo,
            ["Prefijo"] = cfg.RemitoPrefijo,           // talonario 15 -> numeración automática
            ["DepositoIngreso"] = cfg.RemitoDeposito,
            ["Empresa"] = cfg.Empresa,
            ["Sucursal"] = cfg.Sucursal,
            ["Usuario"] = usuarioBas,
            ["Items"] = items
        };

        if (fechaComprobante.HasValue)
            body["FechaExterna"] = fechaComprobante.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(prefijoComprobante))
            body["PrefijoExterno"] = prefijoComprobante!.Trim();
        if (numeroComprobante.HasValue)
            body["NumeroExterno"] = numeroComprobante.Value;
        if (!string.IsNullOrWhiteSpace(observaciones))
            body["ObservacionComprobante"] = observaciones!.Trim();

        var json = JsonSerializer.Serialize(body);

        // ---- POST a BAS ----
        string? respuesta;
        try
        {
            respuesta = await _destinos.PostAsync(destino, "/api/RemitosIngreso", json, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Grabado en BAS ({Base}) falló: {Msg}", destino, ex.Message);
            return new GrabadoResultado(false, null, null, null, null, null, ex.Message);
        }

        // Respuesta vacía pero sin excepción: BAS aceptó (204/sin cuerpo).
        if (string.IsNullOrWhiteSpace(respuesta))
            return new GrabadoResultado(true, null, cfg.RemitoPrefijo, null, null, null, null);

        // ---- Parseo de la respuesta (forma confirmada del 201) ----
        try
        {
            using var doc = JsonDocument.Parse(respuesta);
            var root = doc.RootElement;

            // IdTransaccion en la raíz. 0 = sin transacción real (lo tratamos como vacío).
            string? idt = null;
            var idtRaw = BuscarProp(root, "IdTransaccion");
            if (!string.IsNullOrWhiteSpace(idtRaw) && idtRaw != "0") idt = idtRaw;

            // Motivo: texto que BAS puede devolver (suele explicar un rechazo).
            var motivo = BuscarProp(root, "Motivo");

            // Primer comprobante del array "Comprobantes".
            var comp = PrimerComprobante(root);
            string? prefijo = cfg.RemitoPrefijo, numero = null, comprobante = null;
            if (comp.HasValue)
            {
                prefijo = BuscarProp(comp.Value, "Prefijo") ?? cfg.RemitoPrefijo;
                numero = BuscarProp(comp.Value, "Numero") ?? BuscarProp(comp.Value, "NumeroFinal");
                comprobante = BuscarProp(comp.Value, "Comprobante");
            }

            // Heurística de éxito: BAS respondió 2xx (si no, PostAsync habría tirado).
            // Consideramos grabado OK. Si no vino ningún comprobante NI transacción
            // pero sí un "Motivo", lo tratamos como fallo informado por BAS.
            var sinDatos = idt is null && numero is null && !comp.HasValue;
            if (sinDatos && !string.IsNullOrWhiteSpace(motivo))
                return new GrabadoResultado(false, null, null, null, null, Recortar(respuesta), motivo);

            return new GrabadoResultado(true, idt, prefijo, numero, comprobante, Recortar(respuesta), null);
        }
        catch
        {
            // No es JSON parseable, pero el POST no tiró error -> lo damos por ok
            // y guardamos la respuesta cruda para revisar.
            return new GrabadoResultado(true, null, cfg.RemitoPrefijo, null, null, Recortar(respuesta), null);
        }
    }

    // CantidadPrimeraUnidad / CantidadSegundaUnidad.
    // - Si el artículo es de doble unidad y la relación es > 0: 2ª = 1ª ÷ relación.
    // - Si no: 2ª = 1ª (BAS suele esperar ambas iguales cuando hay una sola unidad).
    private static (decimal c1, decimal c2) CalcularCantidades(decimal cantidad, BienInfo? art)
    {
        if (art is { DobleUnidadMedida: true } && art.RelacionStock > 0)
        {
            var c2 = Math.Round(cantidad / art.RelacionStock, 4, MidpointRounding.AwayFromZero);
            return (cantidad, c2);
        }
        return (cantidad, cantidad);
    }

    // Convierte un texto de series ("123, 456; 789") en lista de números de serie.
    private static List<string> PartirSeries(string? series)
    {
        if (string.IsNullOrWhiteSpace(series)) return new();
        return series
            .Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    // BAS espera la mayoría de los numéricos como string con punto decimal.
    private static string Str(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    // Busca una propiedad (case-insensitive) en un objeto JSON y la devuelve como string.
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

    // Devuelve el primer elemento del array "Comprobantes" (o un objeto "Comprobante").
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
