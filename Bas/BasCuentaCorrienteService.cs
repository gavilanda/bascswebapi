using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Consultas de cuenta corriente a BAS. Por ahora: estado de cuenta del cliente.
public class BasCuentaCorrienteService
{
    private readonly IHttpClientFactory _factory;
    private readonly BasAuthService _auth;
    private readonly BasWebApiOptions _opt;

    public BasCuentaCorrienteService(
        IHttpClientFactory factory, BasAuthService auth, IOptions<BasWebApiOptions> opt)
    {
        _factory = factory;
        _auth = auth;
        _opt = opt.Value;
    }

    // Estado de cuenta del cliente a una fecha dada.
    // fecha se pasa tal cual a BAS (string), para poder probar formatos.
    public async Task<EstadoCtaCteBas?> EstadoClienteAsync(
        string codCliente, string fecha, CancellationToken ct = default)
    {
        var token = await _auth.GetTokenAsync(ct);
        var http = _factory.CreateClient("bas");

        var url = $"/api/EstadoCtaCteCliente"
                + $"?Empresa={_opt.Empresa}"
                + $"&Sucursal={_opt.Sucursal}"
                + $"&CodCliente={Uri.EscapeDataString(codCliente)}"
                + $"&Fecha={Uri.EscapeDataString(fecha)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"BAS devolvio {(int)resp.StatusCode} al consultar la cuenta corriente. {err}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<EstadoCtaCteBas>(json, JsonOpts);
    }

    // Tolerante: acepta numeros que vengan como texto (BAS lo hace en varios campos).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
