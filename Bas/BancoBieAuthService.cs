using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace PortalClientes.Bas;

// Autenticación contra la API del Banco Credicoop (BIE), MULTI-EMPRESA. OAuth2
// client_credentials con "private_key_jwt": se manda un JWT firmado con la clave privada
// RSA de la empresa (client_assertion). El banco lo valida con la pública que tiene
// registrada para ese client_id.
//
// Como cada empresa (BARK, XARDO) tiene su propio client_id + PEM + adherente, el token
// y la clave RSA se cachean POR client_id / POR archivo PEM. Singleton: los caches viven
// entre pedidos.
public class BancoBieAuthService
{
    private readonly IHttpClientFactory _factory;
    private readonly BiePayloadLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // token cacheado por client_id, y clave RSA cacheada por ruta de PEM.
    private readonly ConcurrentDictionary<string, (string token, DateTimeOffset expira)> _tokens = new();
    private readonly ConcurrentDictionary<string, RsaSecurityKey> _claves = new();

    public BancoBieAuthService(IHttpClientFactory factory, BiePayloadLogger log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<string> GetTokenAsync(BieCredenciales cred, CancellationToken ct = default)
    {
        if (_tokens.TryGetValue(cred.ClientId, out var v) && DateTimeOffset.UtcNow < v.expira.AddSeconds(-60))
            return v.token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(cred.ClientId, out var v2) && DateTimeOffset.UtcNow < v2.expira.AddSeconds(-60))
                return v2.token;

            var assertion = FirmarClientAssertion(cred);
            var http = _factory.CreateClient("bancobie");

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = cred.Scopes,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
            });

            using var resp = await http.PostAsync(cred.TokenUrl, form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (_log.Habilitado)
            {
                var reqForm = await form.ReadAsStringAsync(ct);
                _log.Registrar(cred.BaseNombre, "00-token", "POST", cred.TokenUrl, reqForm, (int)resp.StatusCode, body);
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"No se pudo obtener el token del banco para '{cred.BaseNombre}' ({(int)resp.StatusCode}). {body}");

            var tok = JsonSerializer.Deserialize<TokenResp>(body, JsonOpts);
            if (tok is null || string.IsNullOrEmpty(tok.access_token))
                throw new InvalidOperationException("El banco no devolvió un access_token válido.");

            var expira = DateTimeOffset.UtcNow.AddSeconds(tok.expires_in > 0 ? tok.expires_in : 1800);
            _tokens[cred.ClientId] = (tok.access_token, expira);
            return tok.access_token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvalidarToken(string clientId) => _tokens.TryRemove(clientId, out _);

    // Arma el JWT firmado (RS256) que va como client_assertion, con las credenciales de la
    // empresa (iss/sub = su client_id, aud = su TokenUrl del entorno correspondiente).
    private string FirmarClientAssertion(BieCredenciales cred)
    {
        var creds = new SigningCredentials(ObtenerClave(cred.PemPath), SecurityAlgorithms.RsaSha256);
        var ahora = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, cred.ClientId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, ahora.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };
        var jwt = new JwtSecurityToken(
            issuer: cred.ClientId,
            audience: cred.TokenUrl,
            claims: claims,
            notBefore: ahora.UtcDateTime,
            expires: ahora.AddMinutes(5).UtcDateTime,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    // Lee la PEM una sola vez por ruta y cachea la clave RSA. RSA.ImportFromPem acepta
    // PKCS#1 ("BEGIN RSA PRIVATE KEY") y PKCS#8 ("BEGIN PRIVATE KEY").
    private RsaSecurityKey ObtenerClave(string pemPath)
    {
        return _claves.GetOrAdd(pemPath, ruta =>
        {
            if (string.IsNullOrWhiteSpace(ruta) || !File.Exists(ruta))
                throw new InvalidOperationException($"No se encuentra la clave privada del banco (PemPath: '{ruta}').");
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(ruta));
            return new RsaSecurityKey(rsa);
        });
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class TokenResp
    {
        public string access_token { get; set; } = "";
        public int expires_in { get; set; }
        public string token_type { get; set; } = "";
    }
}
