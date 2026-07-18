using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PortalClientes.Bas;

// Consultas de cuenta corriente a BAS. Por ahora: estado de cuenta del cliente.
public class BasCuentaCorrienteService
{
    private readonly BasAuthService _auth;
    private readonly BasWebApiOptions _opt;
    private readonly BasDestinosService _destinos;

    public BasCuentaCorrienteService(
        BasAuthService auth, IOptions<BasWebApiOptions> opt,
        BasDestinosService destinos)
    {
        _auth = auth;
        _opt = opt.Value;
        _destinos = destinos;
    }

    // Estado de cuenta del cliente a una fecha dada.
    // fecha se pasa tal cual a BAS (string), para poder probar formatos.
    public async Task<EstadoCtaCteBas?> EstadoClienteAsync(
        string codCliente, string fecha, CancellationToken ct = default)
    {
        var url = $"/api/EstadoCtaCteCliente"
                + $"?Empresa={_opt.Empresa}"
                + $"&Sucursal={_opt.Sucursal}"
                + $"&CodCliente={Uri.EscapeDataString(codCliente)}"
                + $"&Fecha={Uri.EscapeDataString(fecha)}";

        var json = await _auth.EnviarConReintentoAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<EstadoCtaCteBas>(json, JsonOpts);
    }

    // Estado de cuenta del cliente en una BASE puntual (BARK, PRUEBAB...), ruteando
    // por BasDestinosService y usando la Empresa/Sucursal de ese destino. Devuelve
    // null si la base no responde con datos (404/204).
    public async Task<EstadoCtaCteBas?> EstadoClienteEnBaseAsync(
        string destino, string codCliente, string fecha, CancellationToken ct = default)
    {
        var cfg = _destinos.Config(destino)
            ?? throw new InvalidOperationException($"Destino BAS desconocido: {destino}");

        var url = $"/api/EstadoCtaCteCliente"
                + $"?Empresa={cfg.Empresa}"
                + $"&Sucursal={cfg.Sucursal}"
                + $"&CodCliente={Uri.EscapeDataString(codCliente)}"
                + $"&Fecha={Uri.EscapeDataString(fecha)}";

        var json = await _destinos.GetAsync(destino, url, ct);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<EstadoCtaCteBas>(json, JsonOpts);
    }

    // Tolerante: acepta numeros que vengan como texto (BAS lo hace en varios campos).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
