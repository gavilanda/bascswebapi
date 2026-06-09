using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace PortalClientes.Bas;

// Consultas al maestro de Clientes de BAS. Por ahora: buscar por CUIT.
// Cachea el resultado: los datos del cliente cambian poco.
public class BasClientesService
{
    private readonly IHttpClientFactory _factory;
    private readonly BasAuthService _auth;
    private readonly IMemoryCache _cache;

    private static readonly TimeSpan DuracionCache = TimeSpan.FromMinutes(30);

    public BasClientesService(IHttpClientFactory factory, BasAuthService auth, IMemoryCache cache)
    {
        _factory = factory;
        _auth = auth;
        _cache = cache;
    }

    // Busca un cliente por CUIT (documento). Devuelve el primero que matchee,
    // o null si no hay ninguno. Lanza si BAS responde con error.
    public async Task<ClienteBas?> BuscarPorCuitAsync(string cuit, CancellationToken ct = default)
    {
        var cacheKey = $"clientePorCuit|{cuit}";
        if (_cache.TryGetValue(cacheKey, out ClienteBas? enCache))
            return enCache;

        var token = await _auth.GetTokenAsync(ct);
        var http = _factory.CreateClient("bas");

        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/api/Clientes/documento={Uri.EscapeDataString(cuit)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"BAS devolvio {(int)resp.StatusCode} al buscar el CUIT. {err}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var lista = JsonSerializer.Deserialize<List<ClienteBas>>(json, JsonOpts);
        var cliente = lista is { Count: > 0 } ? lista[0] : null;

        // Solo cacheamos cuando se encontro (no cacheamos "no encontrado").
        if (cliente is not null)
            _cache.Set(cacheKey, cliente, DuracionCache);

        return cliente;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
