using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PortalClientes.Bas;

// Se encarga de obtener (y cachear) el token de servicio de BAS CS WebAPI.
// Replica el flujo OAuth que probaste en el Swagger: POST /auth/token con
// grant_type=password. Guarda el token y lo renueva antes de que expire.
// Se registra como singleton para que el cache viva entre pedidos.
public class BasAuthService
{
    private readonly IHttpClientFactory _factory;
    private readonly BasWebApiOptions _opt;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expira = DateTimeOffset.MinValue;

    public BasAuthService(IHttpClientFactory factory, IOptions<BasWebApiOptions> opt)
    {
        _factory = factory;
        _opt = opt.Value;
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        // Si el token sigue valido (con 60s de margen), lo reusamos.
        if (_token is not null && DateTimeOffset.UtcNow < _expira.AddSeconds(-60))
            return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expira.AddSeconds(-60))
                return _token;

            var http = _factory.CreateClient("bas");

            using var form = new MultipartFormDataContent
            {
                { new StringContent("password"), "grant_type" },
                { new StringContent(_opt.ClientId), "client_id" },
                { new StringContent(_opt.ClientSecret), "client_secret" },
                { new StringContent(_opt.Usuario), "username" },
                { new StringContent(_opt.Password), "password" }
            };

            using var resp = await http.PostAsync("/auth/token", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"No se pudo autenticar contra BAS ({(int)resp.StatusCode}). {err}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var tok = JsonSerializer.Deserialize<BasAccessToken>(json, JsonOpts);
            if (tok is null || string.IsNullOrEmpty(tok.access_token))
                throw new InvalidOperationException("BAS no devolvio un access_token valido.");

            _token = tok.access_token;
            _expira = DateTimeOffset.UtcNow.AddSeconds(tok.expires_in > 0 ? tok.expires_in : 3600);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Descarta el token cacheado (fuerza re-login en el próximo pedido).
    public void InvalidarToken()
    {
        _token = null;
        _expira = DateTimeOffset.MinValue;
    }

    // Envía la request con el token y, si BAS responde 401 (token rechazado — típico cuando
    // la WebAPI de BAS se REINICIÓ e invalidó los tokens viejos), INVALIDA el token cacheado
    // y reintenta UNA vez con uno fresco. Así el servicio se auto-recupera al toque cuando BAS
    // vuelve, sin esperar a que el token venza (~1 h). Mismo criterio que BasDestinosService.
    // Devuelve el cuerpo (string) o null si 404/204. 'armarReq' se llama en cada intento
    // (una HttpRequestMessage no se puede reenviar); la cabecera Authorization la ponemos acá.
    public async Task<string?> EnviarConReintentoAuthAsync(
        Func<HttpRequestMessage> armarReq, CancellationToken ct = default)
    {
        var http = _factory.CreateClient("bas");
        for (int intento = 0; intento < 2; intento++)
        {
            var token = await GetTokenAsync(ct);
            using var req = armarReq();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && intento == 0)
            {
                InvalidarToken();
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.NoContent)
                return null;
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"BAS devolvió {(int)resp.StatusCode} en {req.RequestUri?.PathAndQuery}. {err}");
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        throw new InvalidOperationException("BAS no completó la solicitud (auth).");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Campos del JSON que devuelve /auth/token.
    private sealed class BasAccessToken
    {
        public string access_token { get; set; } = string.Empty;
        public string token_type { get; set; } = string.Empty;
        public int expires_in { get; set; }
        public string? refresh_token { get; set; }
    }
}
