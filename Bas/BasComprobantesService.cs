using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Consulta el detalle de un comprobante de venta en BAS.
// Cachea el resultado en memoria: el detalle de un comprobante no cambia,
// asi que evitamos re-preguntarle a BAS lo mismo.
public class BasComprobantesService
{
    private readonly IHttpClientFactory _factory;
    private readonly BasAuthService _auth;
    private readonly BasWebApiOptions _opt;
    private readonly IMemoryCache _cache;

    // Cuanto tiempo recordamos un comprobante ya consultado.
    private static readonly TimeSpan DuracionCache = TimeSpan.FromMinutes(30);

    public BasComprobantesService(
        IHttpClientFactory factory, BasAuthService auth,
        IOptions<BasWebApiOptions> opt, IMemoryCache cache)
    {
        _factory = factory;
        _auth = auth;
        _opt = opt.Value;
        _cache = cache;
    }

    // Detalle de un comprobante de venta por tipo/prefijo/numero.
    public async Task<ComprobanteVentaBas?> ConsultaVentaAsync(
        string tipo, string prefijo, string numero, CancellationToken ct = default)
    {
        var cacheKey = $"compVenta|{_opt.Empresa}|{_opt.Sucursal}|{tipo}|{prefijo}|{numero}";
        if (_cache.TryGetValue(cacheKey, out ComprobanteVentaBas? enCache))
            return enCache;

        var token = await _auth.GetTokenAsync(ct);
        var http = _factory.CreateClient("bas");

        var url = $"/api/ConsultaComprobanteVenta"
                + $"?Empresa={_opt.Empresa}"
                + $"&Sucursal={_opt.Sucursal}"
                + $"&Comprobante={Uri.EscapeDataString(tipo)}"
                + $"&Prefijo={Uri.EscapeDataString(prefijo)}"
                + $"&Numero={Uri.EscapeDataString(numero)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"BAS devolvio {(int)resp.StatusCode} al consultar el comprobante. {err}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var resultado = JsonSerializer.Deserialize<ComprobanteVentaBas>(json, JsonOpts);

        // Solo cacheamos resultados validos.
        if (resultado is not null)
            _cache.Set(cacheKey, resultado, DuracionCache);

        return resultado;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
