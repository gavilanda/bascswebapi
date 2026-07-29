using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PortalClientes.Bas;

// Emisión de echeqs por la API del Banco Credicoop, MULTI-EMPRESA. Cada método recibe las
// credenciales de la empresa (BieCredenciales); así el mismo servicio opera contra BARK o
// XARDO según qué credenciales se le pasen. Trabaja sobre las mismas filas que saca
// BasEchequesService del SQL de BAS (ChequeRow). La firma queda "Enviada a la firma".
//
// Endpoints (scope echeqConFirma / beneficiarioEcheq), verificados en homologación:
//   POST {BaseUrl}/api/echeq/v1/beneficiario         -> alta de beneficiario en la agenda
//   POST {BaseUrl}/api/echeq/v1/ConFirma/emision     -> emisión (individual)
//   GET  {BaseUrl}/api/echeq/v1/emision?idOperacion= -> consulta de una emisión
public class BancoBieEcheqService
{
    private readonly IHttpClientFactory _factory;
    private readonly BancoBieAuthService _auth;

    public BancoBieEcheqService(IHttpClientFactory factory, BancoBieAuthService auth)
    {
        _factory = factory;
        _auth = auth;
    }

    // Resultado de emitir UN echeq. Ok=true => el banco lo aceptó (Estado + idOperacion).
    public sealed record ResultadoEmision(
        long NumeroCheque, bool Ok, string? Estado, long? IdOperacion, string? IdCheque,
        string IdOrigen, string? ErrorCodigo, string? ErrorDescripcion)
    {
        // Motivo del error: descripción LEGIBLE del banco + el código APIE-xxxx entre
        // paréntesis (útil para consultar con el banco si hiciera falta).
        public string ErrorTexto => !string.IsNullOrWhiteSpace(ErrorDescripcion)
            ? (string.IsNullOrWhiteSpace(ErrorCodigo) ? ErrorDescripcion! : $"{ErrorDescripcion} ({ErrorCodigo})")
            : (!string.IsNullOrWhiteSpace(ErrorCodigo) ? ErrorCodigo! : "No se pudo emitir.");
    }

    // Un echeq EMITIDO (gestion=GENERADOS) tal como lo lista el banco. Es lo que muestra
    // "Ver E-Cheques". El banco NO devuelve beneficiario/CUIT del beneficiario (solo emisor):
    // esos se completan aparte, best-effort, desde BAS (por numeroCheque).
    public sealed record EcheqGenerado(
        long NumeroCheque, string ChequeId, string Estado, string Cmc7,
        string FechaEmision, string FechaPago, decimal Monto, string Moneda,
        string Caracter, string MotivoPago, string Cuenta)
    {
        public string Beneficiario { get; set; } = "";
        public string Cuit { get; set; } = "";
    }

    // Asegura que cada CUIT esté en la agenda de beneficiarios del adherente. Idempotente:
    // el banco responde APIE-8010 si ya existe (lo tomamos como OK). Devuelve las fallas
    // reales (cuit -> motivo), si las hubiera (ej. APIE-8011 "no bancarizado").
    public async Task<IReadOnlyDictionary<string, string>> AsegurarBeneficiariosAsync(
        BieCredenciales cred, IEnumerable<string> cuits, CancellationToken ct = default)
    {
        var fallas = new Dictionary<string, string>();
        foreach (var cuit in cuits.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct())
        {
            var body = new
            {
                numeroAdherente = cred.NumeroAdherente,
                idOrigen = Guid.NewGuid().ToString(),
                beneficiarios = new[]
                {
                    new { orden = "0", documento = cuit.Trim(), documentoTipo = "CUIT" }
                }
            };
            var (status, doc) = await PostAsync(cred, "/api/echeq/v1/beneficiario", body, ct);
            if (status is >= 200 and < 300) continue;

            var (codigo, desc) = LeerError(doc);
            if (codigo == "APIE-8010") continue;   // ya está en la agenda => OK
            fallas[cuit] = !string.IsNullOrWhiteSpace(desc)
                ? (string.IsNullOrWhiteSpace(codigo) ? desc! : $"{desc} ({codigo})")
                : (!string.IsNullOrWhiteSpace(codigo) ? codigo! : $"HTTP {status}");
        }
        return fallas;
    }

    // Emite UN echeq. No lanza: cualquier error del banco vuelve como Ok=false con el
    // código APIE. El idOrigen se genera acá (idempotencia) y se devuelve para persistir.
    public async Task<ResultadoEmision> EmitirAsync(
        BieCredenciales cred, BasEchequesService.ChequeRow r, CancellationToken ct = default)
    {
        var idOrigen = Guid.NewGuid().ToString();

        var echeq = new Dictionary<string, object?>
        {
            ["monto"] = r.Importe.ToString("0.00", CultureInfo.InvariantCulture),
            ["fechaPago"] = FechaApi(r.FechaPago),                 // dd/MM/yyyy -> yyyyMMdd
            ["motivoPago"] = string.IsNullOrWhiteSpace(r.MotivoPago) ? "PAGO" : r.MotivoPago,
            ["caracter"] = r.Caracter.ToString(CultureInfo.InvariantCulture),
            ["modo"] = r.Modo.ToString(CultureInfo.InvariantCulture),
            ["beneficiarioNombre"] = LimpiarNombre(r.Beneficiario),
            ["beneficiarioDocumentoTipo"] = string.IsNullOrWhiteSpace(r.TipoCuiCdi) ? "CUIT" : r.TipoCuiCdi,
            ["beneficiarioDocumento"] = r.NroCuiCdi,
            ["concepto"] = cred.Concepto,                          // código válido (VAR, FAC…)
            ["tipoCheque"] = cred.TipoCheque,                      // ECHD / ECHC
            ["mails"] = new[] { r.Mail },
            ["numeroCheque"] = r.NumEcheq,                         // número de BAS (<=8 díg.)
        };

        var body = new Dictionary<string, object?>
        {
            ["numeroAdherente"] = cred.NumeroAdherente,
            ["idOrigen"] = idOrigen,
            ["cbuCuentaDebito"] = cred.CbuDebito,
            ["echeqs"] = new[] { echeq },
        };
        // Si la empresa tiene firmantes configurados, se emite FIRMADO (ConFirma): el banco
        // aplica la firma de esos operadores en vez de dejar la operación "Enviada a la firma".
        var firmantes = ParsearFirmantes(cred.Firmantes);
        if (firmantes.Count > 0) body["operadoresFirmantes"] = firmantes;

        var (status, doc) = await PostAsync(cred, "/api/echeq/v1/ConFirma/emision", body, ct);

        if (status is >= 200 and < 300 && doc is not null
            && doc.RootElement.TryGetProperty("data", out var data))
        {
            long? idOp = data.TryGetProperty("idOperacion", out var op) && op.TryGetInt64(out var v) ? v : null;
            string? estado = data.TryGetProperty("estadoOperacion", out var eo)
                && eo.TryGetProperty("descripcion", out var d) ? d.GetString() : null;
            string? idCheque = data.TryGetProperty("echeq", out var ech)
                && ech.TryGetProperty("idCheque", out var idc) ? idc.GetString() : null;
            return new ResultadoEmision(r.NumEcheq, true, estado, idOp, idCheque, idOrigen, null, null);
        }

        var (codigo, desc) = LeerError(doc);
        if (string.IsNullOrEmpty(codigo) && string.IsNullOrEmpty(desc))
            desc = $"El banco respondió {status} sin detalle.";
        return new ResultadoEmision(r.NumEcheq, false, null, null, null, idOrigen, codigo, desc);
    }

    // Consulta el estado de una emisión ya enviada (para trazabilidad). Devuelve el JSON crudo.
    public async Task<string?> ConsultarEmisionAsync(
        BieCredenciales cred, long idOperacion, CancellationToken ct = default)
    {
        var token = await _auth.GetTokenAsync(cred, ct);
        var http = _factory.CreateClient("bancobie");
        var url = $"{cred.BaseUrl}/api/echeq/v1/emision?idOperacion={idOperacion}&numeroAdherente={cred.NumeroAdherente}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // Números de cheque YA GENERADOS en el banco por este adherente (gestion=GENERADOS), en una
    // ventana de fecha de emisión. Sirve para no re-emitir algo que ya se subió (Excel o API) y
    // se firmó. Best-effort: cualquier error devuelve lo que se haya juntado (no frena la emisión;
    // quedan la tabla local + la fecha de corte como respaldo). Chunkea en tramos <=30 días (el
    // banco limita el rango) y pagina.
    public async Task<HashSet<long>> NumerosGeneradosAsync(
        BieCredenciales cred, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        var set = new HashSet<long>();
        try
        {
            var ini = desde;
            while (ini <= hasta)
            {
                var fin = ini.AddDays(29);
                if (fin > hasta) fin = hasta;
                await LeerGeneradosAsync(cred, ini, fin, set, ct);
                ini = fin.AddDays(1);
            }
        }
        catch { /* best-effort */ }
        return set;
    }

    private async Task LeerGeneradosAsync(
        BieCredenciales cred, DateOnly desde, DateOnly hasta, HashSet<long> set, CancellationToken ct)
    {
        const int limite = 20;   // el banco rechaza limites altos (APIE-3006); 20 verificado OK
        for (int pagina = 1; pagina <= 500; pagina++)   // tope duro por las dudas
        {
            var filtro = new Dictionary<string, object?>
            {
                ["gestion"] = "GENERADOS",
                ["estado"] = "TODOS",
                ["cbuEmisor"] = cred.CbuDebito,
                ["fechaEmisionDesde"] = desde.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                ["fechaEmisionHasta"] = hasta.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                ["pagina"] = pagina,
                ["limite"] = limite,
            };
            var body = new Dictionary<string, object?>
            {
                ["numeroAdherente"] = cred.NumeroAdherente,
                ["idOrigen"] = Guid.NewGuid().ToString(),
                ["filtro"] = filtro,
            };
            var (status, doc) = await PostAsync(cred, "/api/echeq/v1/lista-cheques", body, ct);
            if (status is < 200 or >= 300 || doc is null
                || !doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("echeqs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            int n = 0;
            foreach (var e in arr.EnumerateArray())
            {
                n++;
                if (!e.TryGetProperty("numeroCheque", out var nc)) continue;
                if (nc.ValueKind == JsonValueKind.Number && nc.TryGetInt64(out var v)) set.Add(v);
                else if (nc.ValueKind == JsonValueKind.String && long.TryParse(nc.GetString(), out var v2)) set.Add(v2);
            }
            if (n < limite) return;   // última página
        }
    }

    // Lista COMPLETA de echeqs emitidos (para "Ver E-Cheques"): mismos criterios que
    // NumerosGeneradosAsync (chunk ≤30 días + paginado) pero mapeando TODOS los campos.
    public async Task<List<EcheqGenerado>> ListarGeneradosAsync(
        BieCredenciales cred, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        var lista = new List<EcheqGenerado>();
        var vistos = new HashSet<long>();
        var ini = desde;
        while (ini <= hasta)
        {
            var fin = ini.AddDays(29);
            if (fin > hasta) fin = hasta;
            await LeerGeneradosDetalleAsync(cred, ini, fin, lista, vistos, ct);
            ini = fin.AddDays(1);
        }
        return lista.OrderBy(e => e.NumeroCheque).ToList();
    }

    private async Task LeerGeneradosDetalleAsync(
        BieCredenciales cred, DateOnly desde, DateOnly hasta,
        List<EcheqGenerado> acc, HashSet<long> vistos, CancellationToken ct)
    {
        const int limite = 20;   // el banco rechaza limites altos (APIE-3006); 20 verificado OK
        for (int pagina = 1; pagina <= 500; pagina++)
        {
            var filtro = new Dictionary<string, object?>
            {
                ["gestion"] = "GENERADOS",
                ["estado"] = "TODOS",
                ["cbuEmisor"] = cred.CbuDebito,
                ["fechaEmisionDesde"] = desde.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                ["fechaEmisionHasta"] = hasta.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                ["pagina"] = pagina,
                ["limite"] = limite,
            };
            var body = new Dictionary<string, object?>
            {
                ["numeroAdherente"] = cred.NumeroAdherente,
                ["idOrigen"] = Guid.NewGuid().ToString(),
                ["filtro"] = filtro,
            };
            var (status, doc) = await PostAsync(cred, "/api/echeq/v1/lista-cheques", body, ct);
            if (status is < 200 or >= 300)
            {
                var (ec, ed) = LeerError(doc);
                throw new InvalidOperationException(
                    $"El banco respondió {status} al listar cheques"
                    + (ed != null ? $": {ed}" : "") + (ec != null ? $" ({ec})" : "") + ".");
            }
            if (doc is null
                || !doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("echeqs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return;

            int n = 0;
            foreach (var e in arr.EnumerateArray())
            {
                n++;
                var num = JLong(e, "numeroCheque");
                if (num == 0 || !vistos.Add(num)) continue;   // dedup por número
                acc.Add(new EcheqGenerado(
                    num, JStr(e, "chequeId"), JStr(e, "estado"), JStr(e, "cmc7completo"),
                    JFecha(e, "fechaEmision"), JFecha(e, "fechaPago"), JDec(e, "monto"),
                    JStr(e, "moneda"), JStr(e, "caracter"), JStr(e, "motivoPago"), JCuenta(e)));
            }
            if (n < limite) return;
        }
    }

    // ---- parseo de campos del echeq (lista-cheques) ----
    private static string JStr(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    private static long JLong(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n2)) return n2;
        return 0;
    }
    private static decimal JDec(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return 0m;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }
    // { "value": "2026-07-08T16:31:38" } -> dd/MM/yyyy
    private static string JFecha(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("value", out var vv)) return "";
        var s = vv.GetString();
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            ? f.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : (s ?? "");
    }
    private static string JCuenta(JsonElement e)
        => e.TryGetProperty("cmc7", out var c) && c.ValueKind == JsonValueKind.Object ? JStr(c, "numeroCuenta") : "";

    // ---- helpers ----

    // POST JSON con Bearer y UN reintento ante 401 (token vencido/rechazado). URL absoluta
    // = cred.BaseUrl + ruta (el host depende del entorno de la empresa). Devuelve el código
    // HTTP y el cuerpo parseado (o null si no era JSON).
    private async Task<(int status, JsonDocument? doc)> PostAsync(
        BieCredenciales cred, string ruta, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        var http = _factory.CreateClient("bancobie");

        for (int intento = 0; intento < 2; intento++)
        {
            var token = await _auth.GetTokenAsync(cred, ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, cred.BaseUrl + ruta)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 401 && intento == 0) { _auth.InvalidarToken(cred.ClientId); continue; }

            var texto = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument? doc = null;
            if (!string.IsNullOrWhiteSpace(texto))
            {
                try { doc = JsonDocument.Parse(texto); } catch { /* no era JSON */ }
            }
            return ((int)resp.StatusCode, doc);
        }
        return (0, null);
    }

    // Lee { "error": { "codigo": "...", "descripcion": "..." } } si está presente.
    private static (string? codigo, string? desc) LeerError(JsonDocument? doc)
    {
        if (doc is not null && doc.RootElement.TryGetProperty("error", out var err))
        {
            string? c = err.TryGetProperty("codigo", out var cc) ? cc.GetString() : null;
            string? d = err.TryGetProperty("descripcion", out var dd) ? dd.GetString() : null;
            return (c, d);
        }
        return (null, null);
    }

    // Nombre del beneficiario: el banco es MUY restrictivo (APIE-1013, verificado en
    // homologación). Sólo acepta letras/números/espacio y punto y coma; rechaza "&", la Ñ,
    // los acentos (Á/É/Í/Ó/Ú) y la ü — y por las dudas cualquier otro símbolo (#, °, /, (, )…).
    // Estrategia LISTA BLANCA para no depender de probar cada carácter:
    //   1) "&" -> " Y " (se lee "y" en español, en vez de perderlo).
    //   2) Quitar acentos/diacríticos (Ñ->N, á->a, ü->u) descomponiendo en FormD.
    //   3) Dejar SÓLO [A-Za-z0-9 .,]; cualquier otro carácter -> espacio.
    //   4) Colapsar espacios y recortar.
    private static string LimpiarNombre(string s)
    {
        var t = (s ?? "").Replace("&", " Y ");
        var d = t.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        t = sb.ToString().Normalize(NormalizationForm.FormC);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[^A-Za-z0-9 .,]", " ");
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
    }

    // dd/MM/yyyy (como viene de ChequeRow.FechaPago) -> yyyyMMdd (como pide el banco).
    private static string FechaApi(string ddMMyyyy)
        => DateTime.TryParseExact(ddMMyyyy, "dd/MM/yyyy", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var f)
            ? f.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : ddMMyyyy;

    // Config "12345678, 20100794889:CUIT" -> [{documento, documentoTipo}]. Tipo por defecto DNI.
    private static List<Dictionary<string, object?>> ParsearFirmantes(string? cfg)
    {
        var lista = new List<Dictionary<string, object?>>();
        foreach (var parte in (cfg ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = parte.Trim();
            if (item.Length == 0) continue;
            var doc = item; var tipo = "DNI";
            var i = item.IndexOf(':');
            if (i > 0) { doc = item[..i].Trim(); tipo = item[(i + 1)..].Trim().ToUpperInvariant(); }
            if (doc.Length == 0) continue;
            lista.Add(new Dictionary<string, object?> { ["documento"] = doc, ["documentoTipo"] = string.IsNullOrEmpty(tipo) ? "DNI" : tipo });
        }
        return lista;
    }
}
