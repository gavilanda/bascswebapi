using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Conexión a varias bases BAS (BARK, PRUEBAB, ...).
// Mismas credenciales (de BasWebApi), distinta URL por destino.
// Cachea un token por destino (cada servidor emite el suyo) y renueva
// antes de que expire. Singleton para que el cache viva entre pedidos.
public class BasDestinosService
{
    private readonly IHttpClientFactory _factory;
    private readonly BasWebApiOptions _cred;                       // credenciales compartidas
    private readonly Dictionary<string, DestinoBas> _destinos;

    private readonly Dictionary<string, (string token, DateTimeOffset expira)> _tokens = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BasDestinosService(
        IHttpClientFactory factory,
        IOptions<BasWebApiOptions> cred,
        Dictionary<string, DestinoBas> destinos)
    {
        _factory = factory;
        _cred = cred.Value;
        _destinos = destinos;
    }

    public IReadOnlyCollection<string> Nombres => _destinos.Keys;
    public bool Existe(string destino) => _destinos.ContainsKey(destino);
    public DestinoBas? Config(string destino) => _destinos.TryGetValue(destino, out var d) ? d : null;

    // GET autenticado contra un destino. Devuelve el cuerpo, o null si 404/204.
    public async Task<string?> GetAsync(string destino, string path, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(destino, ct);
        var http = _factory.CreateClient("bas-" + destino);

        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.NoContent)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"BAS ({destino}) devolvió {(int)resp.StatusCode} en {path}. {err}");
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // POST autenticado con cuerpo JSON (para CONSULTAGRAL). Devuelve el cuerpo,
    // o null si 404/204.
    public async Task<string?> PostAsync(string destino, string path, string jsonBody, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(destino, ct);
        var http = _factory.CreateClient("bas-" + destino);

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.NoContent)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"BAS ({destino}) devolvió {(int)resp.StatusCode} en {path}. {err}");
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> GetTokenAsync(string destino, CancellationToken ct)
    {
        if (_tokens.TryGetValue(destino, out var t) && DateTimeOffset.UtcNow < t.expira.AddSeconds(-60))
            return t.token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(destino, out var t2) && DateTimeOffset.UtcNow < t2.expira.AddSeconds(-60))
                return t2.token;

            if (!Existe(destino))
                throw new InvalidOperationException($"Destino BAS desconocido: {destino}");

            var http = _factory.CreateClient("bas-" + destino);

            using var form = new MultipartFormDataContent
            {
                { new StringContent("password"), "grant_type" },
                { new StringContent(_cred.ClientId), "client_id" },
                { new StringContent(_cred.ClientSecret), "client_secret" },
                { new StringContent(_cred.Usuario), "username" },
                { new StringContent(_cred.Password), "password" }
            };

            using var resp = await http.PostAsync("/auth/token", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"No se pudo autenticar contra BAS ({destino}, {(int)resp.StatusCode}). {err}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var tok = JsonSerializer.Deserialize<BasAccessToken>(json, JsonOpts);
            if (tok is null || string.IsNullOrEmpty(tok.access_token))
                throw new InvalidOperationException($"BAS ({destino}) no devolvió un access_token válido.");

            _tokens[destino] = (tok.access_token, DateTimeOffset.UtcNow.AddSeconds(tok.expires_in > 0 ? tok.expires_in : 3600));
            return tok.access_token;
        }
        finally { _gate.Release(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class BasAccessToken
    {
        public string access_token { get; set; } = string.Empty;
        public int expires_in { get; set; }
    }
}
