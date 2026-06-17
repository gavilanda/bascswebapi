using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// Estado de las WebAPI de BAS (si cada una responde o no), para el monitor de
// bandeja (tray) que corre en el server del portal.
//
// Anónimo PERO sólo accesible desde la misma máquina (loopback): el tray corre
// local. Si el portal se publicara a internet, este endpoint no responde a
// pedidos externos (devuelve 404), así no filtra ni nombres ni URLs internas.
[ApiController]
[Route("api/health")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private readonly BasDestinosService _destinos;
    private readonly IHttpClientFactory _factory;

    public HealthController(BasDestinosService destinos, IHttpClientFactory factory)
    {
        _destinos = destinos;
        _factory = factory;
    }

    // GET /api/health/apis -> por cada base configurada, si su WebAPI de BAS responde.
    [HttpGet("apis")]
    public async Task<ActionResult> Apis()
    {
        // Sólo desde la propia máquina (el tray es local).
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip is null || !IPAddress.IsLoopback(ip)) return NotFound();

        var tareas = _destinos.Nombres.Select(async n =>
        {
            var cfg = _destinos.Config(n);
            var url = cfg?.BaseUrl ?? "";
            var (responde, ms) = await ProbarAsync(url);
            return new
            {
                nombre = n,
                url,
                activa = cfg?.Activa ?? false,
                responde,
                ms
            };
        });

        var apis = (await Task.WhenAll(tareas))
            .OrderBy(a => a.nombre, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new { apis });
    }

    // Ping liviano: CUALQUIER respuesta HTTP = la API está viva (aunque sea 401/404).
    // Sólo un error de conexión / timeout cuenta como "no responde". Timeout corto.
    private async Task<(bool responde, long ms)> ProbarAsync(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return (false, 0);
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var http = _factory.CreateClient("bas-multi");
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            return (true, sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds);
        }
    }
}
