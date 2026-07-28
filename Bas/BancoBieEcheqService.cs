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
        public string ErrorTexto => string.IsNullOrEmpty(ErrorCodigo)
            ? (ErrorDescripcion ?? "Error")
            : $"{ErrorCodigo}: {ErrorDescripcion}";
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
            fallas[cuit] = string.IsNullOrEmpty(codigo) ? (desc ?? $"HTTP {status}") : $"{codigo}: {desc}";
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
            ["beneficiarioNombre"] = r.Beneficiario,
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

    // dd/MM/yyyy (como viene de ChequeRow.FechaPago) -> yyyyMMdd (como pide el banco).
    private static string FechaApi(string ddMMyyyy)
        => DateTime.TryParseExact(ddMMyyyy, "dd/MM/yyyy", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var f)
            ? f.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : ddMMyyyy;
}
