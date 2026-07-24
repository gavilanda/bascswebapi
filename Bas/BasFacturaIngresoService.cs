using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Construye y envía un INGRESO como FACTURA de compra a una base BAS, contra el
// endpoint /api/ComprobantesCompra.
//
// Cálculo de importes (según la letra de la factura):
//   - Factura A: el precio cargado es NETO. Por renglón: gravado = cantidad×neto;
//     IVA = gravado × tasa%; total = gravado + IVA. (IVA = crédito fiscal.)
//   - Factura B: el precio cargado es FINAL (con IVA). Por decisión, va TODO como
//     gravado, sin discriminar IVA: gravado = cantidad×precio; IVA = 0.
//   - Factura C: precio FINAL sin IVA: gravado = cantidad×precio; IVA = 0.
//
// CUADRE EXACTO: cada importe de renglón se redondea a 2 decimales y los totales
// de cabecera son la SUMA de esos renglones ya redondeados (no un recálculo
// aparte), para que el neto y el IVA de cabecera coincidan al centavo con los
// renglones. Si viene TotalDeclarado y no coincide con el total calculado, NO se
// envía nada (se devuelve un error para corregir antes de grabar).
//
// Letra -> código de comprobante BAS: A->MA, B->MB, C->MC.
//
// Reutiliza el record GrabadoResultado (definido en BasRemitoIngresoService.cs).
public class BasFacturaIngresoService
{
    private readonly BasDestinosService _destinos;
    private readonly BasWebApiOptions _cred;
    private readonly ILogger<BasFacturaIngresoService> _log;

    // Valores de cabecera (a confirmar contra BAS; hoy constantes).
    private const string MetodoPago = "C";                // C = cuenta corriente
    private const string MonedaComprobante = "L";         // L = moneda local
    private const string MonedaCtaCte = "L";
    private const bool FacturaRemiteStock = true;         // la factura también ingresa stock
    private const string RutaFactura = "/api/ComprobantesCompra";

    public BasFacturaIngresoService(
        BasDestinosService destinos,
        IOptions<BasWebApiOptions> cred,
        ILogger<BasFacturaIngresoService> log)
    {
        _destinos = destinos;
        _cred = cred.Value;
        _log = log;
    }

    // Renglón resuelto contra la base destino, con precio y alícuota.
    public record RenglonFactura(
        string ProductoCodigo,
        decimal Cantidad,
        decimal PrecioUnitario,
        decimal TasaIva,
        string? Partida,
        string? Series,
        BienInfo? Articulo,
        // Referencia a la OC pendiente que este renglón consume (null si es libre).
        long? OcNrotrans = null,
        int? OcSecuencia = null,
        DateTime? OcFecha = null,
        string? OcPrefijo = null,
        int? OcNumero = null);

    // Percepción de IIBB por provincia (el % ya viene calculado del controlador).
    public record PercepcionIngBrItem(
        string? Provincia,
        decimal BaseImponible,
        decimal Importe,
        decimal Porcentaje,
        string? Regimen);

    public async Task<GrabadoResultado> GrabarAsync(
        string destino,
        DateTime fecha,
        DateTime? fechaComprobante,
        string? prefijoComprobante,
        long? numeroComprobante,
        string proveedorCodigo,
        string? observaciones,
        string? letra,
        decimal percepcionIva,
        IReadOnlyList<PercepcionIngBrItem> percepcionesIngBr,
        decimal? totalDeclarado,
        IReadOnlyList<RenglonFactura> renglones,
        string usuarioPortal,
        CancellationToken ct = default)
    {
        var cfg = _destinos.Config(destino);
        if (cfg is null)
            return Error($"Destino BAS desconocido: {destino}");

        var letraNorm = (letra ?? "").Trim().ToUpperInvariant();
        var comprobante = ComprobantePorLetra(letraNorm);
        if (comprobante is null)
            return Error("Falta o es inválida la letra de la factura (debe ser A, B o C).");

        var usuarioBas = !string.IsNullOrWhiteSpace(_cred.Usuario) ? _cred.Usuario : usuarioPortal;

        // ---- Items + totales (sumando renglones ya redondeados) ----
        var items = new List<Dictionary<string, object?>>(renglones.Count);
        var gravados = new List<decimal>(renglones.Count);   // peso de cada renglón para prorratear percepciones
        decimal totalGravado = 0m, totalIva = 0m;

        foreach (var r in renglones)
        {
            var (c1, c2) = CalcularCantidades(r.Cantidad, r.Articulo);
            var (gravado, iva) = CalcularImportesItem(letraNorm, r.Cantidad, r.PrecioUnitario, r.TasaIva);
            totalGravado += gravado;
            totalIva += iva;
            gravados.Add(gravado);

            var item = new Dictionary<string, object?>
            {
                ["CodigoItem"] = r.ProductoCodigo,
                ["NumeroUnidadMedida"] = "1",
                // OJO: importes y cantidades van como NÚMERO JSON (no string), igual
                // que TasaIva. El schema de ComprobantesCompra los define numéricos;
                // mandarlos como string arriesga reparseo con cultura es-AR.
                ["CantidadPrimeraUnidad"] = c1,
                ["CantidadSegundaUnidad"] = c2,
                ["PrecioUnitario"] = r.PrecioUnitario,
                ["ImporteGravado"] = gravado,
                ["ImporteIva"] = iva,
                // El ImporteTotal del ítem es el NETO gravado (SIN IVA), no gravado+IVA.
                // BAS valida el renglón contra el neto; mandar gravado+IVA da
                // "importes del ítem inconsistentes" en SP_GENEROASI. En B y C iva=0,
                // así que total = gravado igual.
                ["ImporteTotal"] = gravado,
                // Sólo la A discrimina IVA; B y C van con tasa 0.
                // OJO: va como NÚMERO JSON (no string). Si se manda "10.5" como
                // string, BAS lo parsea con cultura es-AR y el '.' lo toma como
                // separador de miles ("10.5"->105), y el IVA recalculado no cuadra
                // (SP_GENEROASI: "importes de iva no consistentes").
                ["TasaIva"] = (letraNorm == "A" ? r.TasaIva : 0m)
            };

            // La partida se envía SÓLO si el producto administra partidas EN ESTA BASE.
            // Si la base no maneja partidas para ese producto, se graba igual sin
            // partida (evita el rechazo de BAS al mandar partida en un bien que no
            // la administra). Si no se pudo resolver el artículo, se omite por las dudas.
            if (!string.IsNullOrWhiteSpace(r.Partida) && (r.Articulo?.AdministraPartidas ?? false))
                item["Partida"] = r.Partida!.Trim();

            // Referencia a la OC pendiente: hace que BAS descuente el ítem de esa OC.
            BasRemitoIngresoService.AgregarReferenciaOC(item, r.OcNrotrans, r.OcSecuencia, r.OcFecha, r.OcPrefijo, r.OcNumero);

            var series = PartirSeries(r.Series);
            if (series.Count > 0)
                item["ExplosionSeries"] = series.Select(s => new Dictionary<string, object?>
                {
                    ["NumeroSerie"] = s,
                    ["CantidadPrimeraUnidad"] = 1m,
                    ["CantidadSegundaUnidad"] = 1m
                }).ToList();

            items.Add(item);
        }

        var percIva = Redondear(percepcionIva);
        var totalPercIIBB = Redondear(percepcionesIngBr.Sum(x => x.Importe));
        var total = totalGravado + totalIva + percIva + totalPercIIBB;

        // ---- Percepciones itemizadas ----
        // BAS exige que las percepciones (IVA e IIBB) estén reflejadas en el detalle
        // de ítems y que la suma cuadre con el total de cabecera. Repartimos cada
        // total entre los renglones en proporción al gravado; el último renglón con
        // gravado absorbe la diferencia de redondeo para cuadrar al centavo.
        var partesPercIva = RepartirProporcional(percIva, gravados);
        var partesPercIIBB = RepartirProporcional(totalPercIIBB, gravados);
        for (int i = 0; i < items.Count; i++)
        {
            if (partesPercIva[i] != 0m) items[i]["ImportePercepcionIva"] = partesPercIva[i];
            if (partesPercIIBB[i] != 0m) items[i]["ImportePercepcionIngBr"] = partesPercIIBB[i];
        }

        // ---- Cuadre exacto contra el total impreso (si se cargó) ----
        if (totalDeclarado.HasValue && Redondear(totalDeclarado.Value) != total)
            return Error(
                $"El total declarado ({Str(Redondear(totalDeclarado.Value))}) no coincide con el calculado " +
                $"({Str(total)}). Revisá precios, IVA y percepciones antes de grabar.");

        // ---- Datos de compra del proveedor (condición + cuenta contable) ----
        // BAS necesita la Condición de Compra del proveedor para calcular los
        // vencimientos, y la cuenta contable para el asiento. Las traemos del
        // proveedor completo: la lista CuentasCorrientes trae la cuenta marcada
        // PorDefecto. Es una llamada extra, pero queda siempre fresco.
        var (condicionProv, imputacionProv) =
            await ObtenerDatosCompraProveedorAsync(destino, proveedorCodigo, ct);
        var condicion = string.IsNullOrWhiteSpace(condicionProv) ? null : condicionProv!.Trim();
        if (condicion is null)
            return Error(
                $"El proveedor {proveedorCodigo} no tiene Condición de Compra configurada en BAS; " +
                "sin ella no se pueden calcular los vencimientos de la factura.");
        // La cuenta sale del proveedor (la marcada PorDefecto). Si no trae ninguna,
        // cae a la cuenta configurada por base.
        var imputacion = imputacionProv ?? cfg.FacturaImputacionContable;

        // ---- Cabecera ----
        var body = new Dictionary<string, object?>
        {
            ["Fecha"] = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Comprobante"] = comprobante,             // MA / MB / MC
            ["Prefijo"] = cfg.FacturaPrefijo,          // talonario -> numeración automática (no enviamos Numero)
            ["Proveedor"] = proveedorCodigo,
            // Cuenta contable del proveedor (la marcada PorDefecto en sus
            // CuentasCorrientes), con fallback a la cuenta configurada por base.
            // FK a dbo.CUENTAS.CODCUE.
            ["ImputacionContable"] = imputacion,
            ["Empresa"] = cfg.Empresa,
            ["Sucursal"] = cfg.Sucursal,
            ["Deposito"] = cfg.FacturaDeposito,
            ["MetodoPago"] = MetodoPago,
            ["MonedaComprobante"] = MonedaComprobante,
            ["MonedaCtaCte"] = MonedaCtaCte,
            ["FacturaRemito"] = FacturaRemiteStock,
            ["TotalGravado"] = totalGravado,
            ["TotalIva"] = totalIva,
            ["TotalPercepcionIva"] = percIva,
            ["TotalPercepcionIngBr"] = totalPercIIBB,
            ["Total"] = total,
            ["Usuario"] = usuarioBas,
            // Condición de compra del proveedor (BAS no la toma sola; la necesita
            // para calcular los vencimientos en SP_ICR_COMPROB_VTOS).
            ["CondicionVentaCompra"] = condicion,
            ["Items"] = items
        };

        // Percepciones de IIBB por provincia.
        if (percepcionesIngBr.Count > 0)
            body["CoparticipacionesIngresosBrutos"] = percepcionesIngBr.Select(x => new Dictionary<string, object?>
            {
                ["Provincia"] = string.IsNullOrWhiteSpace(x.Provincia) ? null : x.Provincia!.Trim(),
                ["ImporteSujeto"] = Redondear(x.BaseImponible),
                ["ImportePercepcion"] = Redondear(x.Importe),
                ["PorcentajePercepcion"] = x.Porcentaje,
                ["Regimen"] = string.IsNullOrWhiteSpace(x.Regimen) ? null : x.Regimen!.Trim()
            }).ToList();

        // Comprobante externo del proveedor (la factura real del proveedor).
        if (fechaComprobante.HasValue)
            body["FechaComprobanteExterno"] = fechaComprobante.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(prefijoComprobante))
            body["PrefijoComprobanteExterno"] = prefijoComprobante!.Trim();
        if (numeroComprobante.HasValue)
            body["NumeroComprobanteExterno"] = numeroComprobante.Value;
        if (!string.IsNullOrWhiteSpace(observaciones))
            body["ObservacionComprobante"] = observaciones!.Trim();

        var json = JsonSerializer.Serialize(body);

        // ---- POST a BAS ----
        string? respuesta;
        try
        {
            respuesta = await _destinos.PostAsync(destino, RutaFactura, json, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Grabado de FACTURA en BAS ({Base}) falló: {Msg}", destino, ex.Message);
            return Error(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(respuesta))
            return new GrabadoResultado(true, null, cfg.FacturaPrefijo, null, comprobante, null, null);

        return ParsearRespuesta(respuesta, cfg.FacturaPrefijo, comprobante);
    }

    // Trae del proveedor la Condición de Compra y la cuenta contable por defecto.
    // La cuenta viene en la lista CuentasCorrientes; se elige la que tiene
    // PorDefecto=true. Si algo falla o no hay datos devuelve (null, null) y el
    // llamador decide (la condición es obligatoria; la cuenta cae a la config).
    private async Task<(string? condicion, long? imputacion)> ObtenerDatosCompraProveedorAsync(
        string destino, string proveedorCodigo, CancellationToken ct)
    {
        string? json;
        try
        {
            json = await _destinos.GetAsync(
                destino, "/api/Proveedores/" + Uri.EscapeDataString(proveedorCodigo.Trim()), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning("No se pudo traer el proveedor {Cod} de {Base}: {Msg}",
                proveedorCodigo, destino, ex.Message);
            return (null, null);
        }
        if (string.IsNullOrWhiteSpace(json)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var condicion = BienInfo.Prop(root, "CondicionCompra");
            var imputacion = ImputacionPorDefecto(root);
            return (condicion, imputacion);
        }
        catch
        {
            return (null, null);
        }
    }

    // De la lista CuentasCorrientes del proveedor, devuelve la ImputacionContable
    // marcada PorDefecto=true. Si ninguna está marcada, devuelve null (el llamador
    // cae a la cuenta configurada por base).
    private static long? ImputacionPorDefecto(JsonElement proveedor)
    {
        if (proveedor.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in proveedor.EnumerateObject())
        {
            if (!string.Equals(p.Name, "CuentasCorrientes", StringComparison.OrdinalIgnoreCase)
                || p.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var cc in p.Value.EnumerateArray())
            {
                if (!BienInfo.PropBool(cc, "PorDefecto")) continue;
                var imp = BienInfo.PropDecimal(cc, "ImputacionContable");
                if (imp > 0) return (long)imp;
            }
            break;  // ya recorrimos la lista
        }
        return null;
    }

    // ---- Cálculo de importes por renglón según la letra ----
    private static (decimal gravado, decimal iva) CalcularImportesItem(
        string letra, decimal cantidad, decimal precio, decimal tasa)
    {
        if (letra == "A")
        {
            var gravado = Redondear(cantidad * precio);
            var iva = Redondear(gravado * tasa / 100m);
            return (gravado, iva);
        }
        // B y C: todo gravado, sin IVA discriminado.
        return (Redondear(cantidad * precio), 0m);
    }

    private static string? ComprobantePorLetra(string letra) => letra switch
    {
        "A" => "MA",
        "B" => "MB",
        "C" => "MC",
        _ => null
    };

    // ---- Helpers ----
    private static GrabadoResultado Error(string msg)
        => new(false, null, null, null, null, null, msg);

    private static decimal Redondear(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    // Reparte 'total' entre los renglones en proporción a 'pesos' (el gravado de
    // cada uno), redondeando cada parte a 2 decimales. El ÚLTIMO renglón con peso
    // > 0 absorbe la diferencia, de modo que la suma de las partes sea EXACTAMENTE
    // 'total' (cuadre al centavo, que es lo que controla BAS). Si total es 0 o no
    // hay pesos, devuelve todo en cero.
    private static decimal[] RepartirProporcional(decimal total, IReadOnlyList<decimal> pesos)
    {
        var n = pesos.Count;
        var partes = new decimal[n];
        if (n == 0 || total == 0m) return partes;

        var sumaPesos = pesos.Sum();
        if (sumaPesos <= 0m) { partes[0] = total; return partes; }   // sin base: todo al primero

        var ultimoConPeso = -1;
        for (int i = 0; i < n; i++)
            if (pesos[i] > 0m) ultimoConPeso = i;

        decimal acumulado = 0m;
        for (int i = 0; i < n; i++)
        {
            if (pesos[i] <= 0m) continue;                       // renglón sin base -> 0
            if (i == ultimoConPeso)
            {
                partes[i] = total - acumulado;                  // el último cuadra la diferencia
            }
            else
            {
                partes[i] = Redondear(total * pesos[i] / sumaPesos);
                acumulado += partes[i];
            }
        }
        return partes;
    }

    private static (decimal c1, decimal c2) CalcularCantidades(decimal cantidad, BienInfo? art)
    {
        if (art is { DobleUnidadMedida: true } && art.RelacionStock > 0)
        {
            var c2 = Math.Round(cantidad / art.RelacionStock, 4, MidpointRounding.AwayFromZero);
            return (cantidad, c2);
        }
        return (cantidad, cantidad);
    }

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

    private GrabadoResultado ParsearRespuesta(string respuesta, string? prefijoFallback, string? comprobanteFallback)
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
            string? prefijo = prefijoFallback, numero = null, comprobante = comprobanteFallback;
            if (comp.HasValue)
            {
                prefijo = BuscarProp(comp.Value, "Prefijo") ?? prefijoFallback;
                numero = BuscarProp(comp.Value, "Numero") ?? BuscarProp(comp.Value, "NumeroFinal");
                comprobante = BuscarProp(comp.Value, "Comprobante") ?? comprobanteFallback;
            }

            var sinDatos = idt is null && numero is null && !comp.HasValue;
            if (sinDatos && !string.IsNullOrWhiteSpace(motivo))
                return new GrabadoResultado(false, null, null, null, null, Recortar(respuesta), motivo);

            return new GrabadoResultado(true, idt, prefijo, numero, comprobante, Recortar(respuesta), null);
        }
        catch
        {
            return new GrabadoResultado(true, null, prefijoFallback, null, comprobanteFallback, Recortar(respuesta), null);
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
