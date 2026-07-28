using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PortalClientes.Bas;

// Cuentas y movimientos del Banco Credicoop (scope cuentas). Base de la CONCILIACIÓN
// bancaria: lista las cuentas del adherente y trae sus movimientos en un rango, y genera
// el TXT posicional que importa BAS. Usa las credenciales por empresa (BieCredenciales).
public class BancoBieCuentasService
{
    private readonly IHttpClientFactory _factory;
    private readonly BancoBieAuthService _auth;

    public BancoBieCuentasService(IHttpClientFactory factory, BancoBieAuthService auth)
    {
        _factory = factory;
        _auth = auth;
    }

    public sealed record CuentaInfo(string NroCuenta, string Denominacion, string Moneda,
        string TipoCuenta, decimal Saldo, string Cbu);

    public sealed record Movimiento(string Fecha, string Descripcion, string IndDBCR, decimal Monto,
        string NroComprobante, string CodOperativo, decimal Saldo, string IdTransaccion);

    public async Task<IReadOnlyList<CuentaInfo>> ListarCuentasAsync(BieCredenciales cred, CancellationToken ct = default)
    {
        var (status, doc) = await GetAsync(cred, "/api/cuentas/v1/listaCuentas", ct);
        var list = new List<CuentaInfo>();
        if (status is >= 200 and < 300 && doc is not null
            && doc.RootElement.TryGetProperty("clarifCuentas", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
                list.Add(new CuentaInfo(S(c, "nroCuenta"), S(c, "denominacionCuenta"), S(c, "moneda"),
                    S(c, "tipoCuenta"), D(c, "saldo"), S(c, "CBU")));
        }
        return list;
    }

    // Movimientos de una cuenta en un rango. Chunkea en tramos <=31 días (el banco limita el
    // rango) y descarta el ENCABEZADO (indDBCR vacío). tope 1000 por tramo.
    public async Task<IReadOnlyList<Movimiento>> MovimientosAsync(
        BieCredenciales cred, string nroCuenta, DateOnly desde, DateOnly hasta, CancellationToken ct = default)
    {
        var movs = new List<Movimiento>();
        var ini = desde;
        while (ini <= hasta)
        {
            var fin = ini.AddDays(30);
            if (fin > hasta) fin = hasta;
            var url = $"/api/cuentas/v1/{nroCuenta}/movimientos"
                + $"?fechaDesde={ini.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}"
                + $"&fechaHasta={fin.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}&topeMovimientos=1000";
            var (status, doc) = await GetAsync(cred, url, ct);
            if (status is < 200 or >= 300)
            {
                var detalle = "";
                if (doc is not null && doc.RootElement.TryGetProperty("error", out var err))
                    detalle = " " + (err.TryGetProperty("descripcion", out var de) ? de.GetString() : "")
                        + (err.TryGetProperty("codigo", out var co) ? $" ({co.GetString()})" : "");
                throw new InvalidOperationException(
                    $"El banco devolvió {status} al pedir movimientos de la cuenta {nroCuenta} ({ini:yyyy-MM-dd} a {fin:yyyy-MM-dd}).{detalle}");
            }
            if (doc is not null && doc.RootElement.TryGetProperty("consMovCtas", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var ind = S(m, "indDBCR");
                    if (ind != "DB" && ind != "CR") continue;   // saltea ENCABEZADO / no-movimientos
                    movs.Add(new Movimiento(S(m, "fecha"), S(m, "descripcion"), ind, D(m, "monto"),
                        S(m, "nroComprobante"), S(m, "codOperativo"), D(m, "saldo"), S(m, "idTransaccion")));
                }
            }
            ini = fin.AddDays(1);
        }
        return movs;
    }

    // ---- TXT posicional para BAS (128 caracteres por línea) ----
    // fecha (col 1-10, dd/mm/aaaa) · descripción (col 11-100) · nº operación (col 103-110) ·
    // importe (col 114-128, coma decimal, signo '-' en débitos, ceros a la izquierda).
    // Latin1: 1 byte por carácter, para que los acentos NO descoloquen las columnas. CRLF.
    public static byte[] ArmarTxt(IReadOnlyList<Movimiento> movs)
    {
        var sb = new StringBuilder();
        foreach (var m in movs)
        {
            var linea = new char[128];
            Array.Fill(linea, ' ');
            Poner(linea, 0, FechaTxt(m.Fecha));                       // col 1
            Poner(linea, 10, Trunc(m.Descripcion, 90));               // col 11
            Poner(linea, 102, NumOp(m.NroComprobante));               // col 103
            Poner(linea, 113, Importe(m.Monto, m.IndDBCR == "DB"));   // col 114
            sb.Append(linea).Append("\r\n");
        }
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static void Poner(char[] buf, int start, string s)
    {
        for (int i = 0; i < s.Length && start + i < buf.Length; i++)
            buf[start + i] = s[i];
    }

    private static string Trunc(string s, int n) => (s ?? "").Length <= n ? (s ?? "") : s.Substring(0, n);

    // yyyymmdd -> dd/mm/aaaa
    private static string FechaTxt(string yyyymmdd)
    {
        var s = (yyyymmdd ?? "").Trim();
        return s.Length == 8 ? $"{s.Substring(6, 2)}/{s.Substring(4, 2)}/{s.Substring(0, 4)}" : "";
    }

    // Nº de operación (nroComprobante): 8 caracteres, derecha con ceros a la izquierda.
    // Vacío = queda en blanco (sin comprobante: impuestos, etc.).
    private static string NumOp(string nro)
    {
        nro = (nro ?? "").Trim();
        if (nro.Length == 0) return "";
        if (nro.Length >= 8) return nro.Substring(nro.Length - 8);
        return new string('0', 8 - nro.Length) + nro;
    }

    // Importe: 15 caracteres, coma decimal, 2 decimales, ceros a la izquierda; signo '-' al
    // frente en los débitos (créditos sin signo).
    private static string Importe(decimal monto, bool debito)
    {
        var nucleo = Math.Abs(monto).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
        var signo = debito ? "-" : "";
        var zeros = 15 - signo.Length - nucleo.Length;
        var s = signo + new string('0', Math.Max(0, zeros)) + nucleo;
        return s.Length > 15 ? s.Substring(s.Length - 15) : s;
    }

    // ---- HTTP GET con Bearer y 1 reintento ante 401 ----
    private async Task<(int status, JsonDocument? doc)> GetAsync(BieCredenciales cred, string ruta, CancellationToken ct)
    {
        var http = _factory.CreateClient("bancobie");
        for (int intento = 0; intento < 2; intento++)
        {
            var token = await _auth.GetTokenAsync(cred, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, cred.BaseUrl + ruta);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 401 && intento == 0) { _auth.InvalidarToken(cred.ClientId); continue; }
            var texto = await resp.Content.ReadAsStringAsync(ct);
            JsonDocument? doc = null;
            if (!string.IsNullOrWhiteSpace(texto)) { try { doc = JsonDocument.Parse(texto); } catch { } }
            return ((int)resp.StatusCode, doc);
        }
        return (0, null);
    }

    private static string S(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) ? (v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString()) : "";
    private static decimal D(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : 0m;
}
