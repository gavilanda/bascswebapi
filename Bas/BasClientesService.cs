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
    private readonly BasDestinosService _destinos;

    private static readonly TimeSpan DuracionCache = TimeSpan.FromMinutes(30);

    public BasClientesService(
        IHttpClientFactory factory, BasAuthService auth, IMemoryCache cache, BasDestinosService destinos)
    {
        _factory = factory;
        _auth = auth;
        _cache = cache;
        _destinos = destinos;
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
        var cliente = ElegirCasaCentral(lista);

        // Solo cacheamos cuando se encontro (no cacheamos "no encontrado").
        if (cliente is not null)
            _cache.Set(cacheKey, cliente, DuracionCache);

        return cliente;
    }

    // Igual que BuscarPorCuitAsync pero contra una BASE puntual (BARK, PRUEBAB...),
    // ruteando por BasDestinosService. El codigo de cliente difiere por base, por
    // eso la cache incluye el destino en la clave.
    public async Task<ClienteBas?> BuscarPorCuitEnBaseAsync(
        string destino, string cuit, CancellationToken ct = default)
    {
        var cacheKey = $"clientePorCuit|{destino}|{cuit}";
        if (_cache.TryGetValue(cacheKey, out ClienteBas? enCache))
            return enCache;

        var json = await _destinos.GetAsync(
            destino, $"/api/Clientes/documento={Uri.EscapeDataString(cuit)}", ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        var lista = JsonSerializer.Deserialize<List<ClienteBas>>(json, JsonOpts);
        var cliente = ElegirCasaCentral(lista);
        if (cliente is not null)
            _cache.Set(cacheKey, cliente, DuracionCache);
        return cliente;
    }

    // Todas las cuentas "casa central / independientes" de un CUIT en una base:
    // las que NO están administradas por otra (AdministradaPor vacío). Una sola
    // regla cubre los dos escenarios:
    //  - Cabecera con sucursales: solo la cabecera tiene AdministradaPor vacío, así
    //    que devuelve SOLO la cabecera (las administradas quedan afuera; su cuenta
    //    corriente vive en la cabecera, no se cuenta doble).
    //  - Varias cuentas INDEPENDIENTES con el mismo CUIT: todas tienen
    //    AdministradaPor vacío -> las devuelve TODAS (cada una con su cta cte).
    // La cache incluye el destino en la clave (el código difiere por base).
    public async Task<List<ClienteBas>> BuscarCuentasPorCuitEnBaseAsync(
        string destino, string cuit, CancellationToken ct = default)
    {
        var cacheKey = $"cuentasPorCuit|{destino}|{cuit}";
        if (_cache.TryGetValue(cacheKey, out List<ClienteBas>? enCache))
            return enCache!;

        var json = await _destinos.GetAsync(
            destino, $"/api/Clientes/documento={Uri.EscapeDataString(cuit)}", ct);
        if (string.IsNullOrWhiteSpace(json)) return new();

        var lista = JsonSerializer.Deserialize<List<ClienteBas>>(json, JsonOpts) ?? new();
        var cuentas = lista.Where(c => string.IsNullOrWhiteSpace(c.AdministradaPor)).ToList();
        // Si por algún motivo ninguna viene sin marcar, caemos a la primera (no romper).
        if (cuentas.Count == 0 && lista.Count > 0) cuentas.Add(lista[0]);

        _cache.Set(cacheKey, cuentas, DuracionCache);
        return cuentas;
    }

    // Cuando un CUIT tiene sucursales, BAS devuelve varios clientes con el mismo
    // documento. La cuenta corriente real es la de la CASA CENTRAL: la que NO está
    // administrada por otra (campo AdministradaPor vacío). Si por algún motivo
    // ninguna viniera sin marcar, caemos al primero para no romper.
    // (Usado por endpoints que necesitan UNA sola cuenta representativa, ej. "datos".)
    private static ClienteBas? ElegirCasaCentral(List<ClienteBas>? lista)
    {
        if (lista is null || lista.Count == 0) return null;
        return lista.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.AdministradaPor)) ?? lista[0];
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
