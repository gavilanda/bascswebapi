using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// Estadísticas de venta (función interna del portal). Consolida las ventas de las
// bases que ve el usuario en un período: total, evolución por mes y ranking por
// cliente. El detalle por artículo NO está disponible (BAS no expone esa consulta).
[ApiController]
[Route("api/estadisticas")]
[Authorize]
public class EstadisticasController : ControllerBase
{
    private readonly BasEstadisticasVentaService _ventas;
    private readonly BasDestinosService _destinos;
    private readonly BasCacheMaestros _cache;
    private readonly BasClientesService _clientes;

    public EstadisticasController(
        BasEstadisticasVentaService ventas, BasDestinosService destinos, BasCacheMaestros cache,
        BasClientesService clientes)
    {
        _ventas = ventas;
        _destinos = destinos;
        _cache = cache;
        _clientes = clientes;
    }

    // Sólo personal interno: la venta del negocio no es un dato de un cliente externo.
    private bool EsInterno() => User.FindFirstValue("tipo") == "Interno";

    // CUITs del grupo (Xardo y Bark). En "Modo Grupo" se excluyen de la estadística en
    // CUALQUIER base (son ventas entre empresas del grupo, no venta real al mercado).
    // Se comparan sólo por dígitos, así no importa cómo venga formateado el CUIT en el padrón.
    private static readonly HashSet<string> CuitsGrupo = new(StringComparer.Ordinal)
    {
        "30661372946",   // BARK S.A.
        "30629689865",   // XARDO S.A.
    };

    private static string SoloDigitos(string? s)
        => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    // Bases del portal que ve el usuario (activas + IncluirEnPortal, intersectadas con
    // las que tiene tildadas). Mismo criterio que la cuenta corriente.
    private IReadOnlyList<string> PortalBases()
    {
        var delPortal = _destinos.Nombres
            .Where(n => _destinos.Config(n) is { Activa: true, IncluirEnPortal: true })
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var delUsuario = User.FindAll("basePortal").Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return delPortal.Where(n => delUsuario.Contains(n)).ToList();
    }

    // PortalBases() intersectado con las bases pedidas por el front (CSV en ?bases=). Sin el
    // parámetro → todas. Permite excluir bases (ej. una caída) de la consulta.
    private IReadOnlyList<string> FiltrarBases(string? bases)
    {
        var todas = PortalBases();
        var pedidas = (bases ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pedidas.Length == 0) return todas;
        var set = new HashSet<string>(pedidas, StringComparer.OrdinalIgnoreCase);
        return todas.Where(b => set.Contains(b)).ToList();
    }

    // GET /api/estadisticas/ventas?desde=yyyy-MM-dd&hasta=yyyy-MM-dd&cuit=
    // cuit (opcional): limita las ventas a ese cliente (se resuelven sus códigos por
    // base). Sin cuit = todos los clientes.
    // ct = HttpContext.RequestAborted (lo bindea ASP.NET automáticamente): si el navegador
    // cancela el fetch (botón Cancelar), se corta también el trabajo contra BAS.
    [HttpGet("ventas")]
    public async Task<ActionResult> Ventas([FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? cuit, [FromQuery] string? bases, [FromQuery] bool refrescar = false,
        [FromQuery] bool excluirGrupo = false, CancellationToken ct = default)
    {
        if (!EsInterno())
            return StatusCode(403, new { mensaje = "Sólo el personal interno puede ver estadísticas de venta." });

        if (!DateOnly.TryParse(desde, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
            !DateOnly.TryParse(hasta, CultureInfo.InvariantCulture, DateTimeStyles.None, out var h))
            return BadRequest(new { mensaje = "Fechas inválidas (usá yyyy-MM-dd)." });
        if (h < d) (d, h) = (h, d);

        var cuitFiltro = (cuit ?? "").Trim();
        var basesUsar = FiltrarBases(bases);   // sólo las bases pedidas (tildadas); sin param → todas

        // Cada base en paralelo (son servidores distintos), con error aislado por base.
        // Si viene cuit, resolvemos sus códigos en cada base y filtramos a ese cliente.
        var resultados = await Task.WhenAll(basesUsar.Select(async b =>
        {
            IReadOnlyList<string>? codigos = null;
            if (cuitFiltro.Length > 0)
            {
                try
                {
                    var cuentas = await _clientes.BuscarCuentasPorCuitEnBaseAsync(b, cuitFiltro, ct);
                    codigos = cuentas.Select(x => (x.Codigo ?? "").Trim()).Where(x => x.Length > 0).ToList();
                }
                catch { codigos = new List<string>(); }
                // El cliente no está en esta base: no aporta ventas (no consultamos).
                if (codigos.Count == 0)
                    return new BaseResultado(b, true, null, 0m, 0m, 0,
                        new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
                        new Dictionary<string, ClienteImp>());
            }
            return await PorBaseAsync(b, d, h, codigos, refrescar, excluirGrupo, ct);
        }));

        // Desglose POR BASE; el front consolida, tilda/destilda bases y unifica por CUIT.
        var basesOut = resultados.Select(r => new
        {
            baseNombre = r.Base,
            ok = r.Ok,
            error = r.Error,
            total = r.Total,
            totalKilos = r.TotalKilos,
            cantidad = r.Cantidad,
            // Unión de meses con importe y/o kilos.
            porMes = r.PorMes.Keys.Union(r.PorMesKg.Keys).OrderBy(m => m, StringComparer.Ordinal)
                .Select(m => new
                {
                    mes = m,
                    importe = r.PorMes.TryGetValue(m, out var i) ? i : 0m,
                    kilos = r.PorMesKg.TryGetValue(m, out var k) ? k : 0m
                }),
            // Top 150 por importe unido con top 150 por kilos (para que el ranking por kg no
            // pierda clientes que venden mucho peso pero poco $).
            porCliente = r.PorCliente.Values.OrderByDescending(v => v.Imp).Take(150)
                .Union(r.PorCliente.Values.OrderByDescending(v => v.Kg).Take(150))
                .Select(v => new { codigo = v.Cod, cuit = v.Cuit, razonSocial = v.Razon, importe = v.Imp, kilos = v.Kg })
        });

        return Ok(new
        {
            desde = d.ToString("yyyy-MM-dd"),
            hasta = h.ToString("yyyy-MM-dd"),
            bases = basesOut
        });
    }

    private async Task<BaseResultado> PorBaseAsync(string baseNombre, DateOnly desde, DateOnly hasta, IReadOnlyList<string>? codigos, bool forzar, bool excluirGrupo, CancellationToken ct)
    {
        try
        {
            var padron = _cache.Obtener(baseNombre).Clientes;

            // FUENTE PRIMARIA: BI_VENTAS trae importe + kg + comprobantes en UNA consulta
            // (reconciliado al centavo con ComprobanteCliente). Si la base no tiene la vista,
            // VentasBiAsync lanza y caemos al camino ComprobanteCliente de más abajo.
            try
            {
                var bi = await _ventas.VentasBiAsync(baseNombre, desde, hasta, codigos, ct);
                decimal totalBi = 0m, totalKgBi = 0m;
                int cantBi = 0;
                var pmBi = new Dictionary<string, decimal>();
                var pmKgBi = new Dictionary<string, decimal>();
                var pcBi = new Dictionary<string, ClienteImp>();
                foreach (var f in bi)
                {
                    padron.TryGetValue(f.CodCliente, out var cliB);
                    var cuitB = cliB?.Cuit ?? "";
                    if (excluirGrupo && CuitsGrupo.Contains(SoloDigitos(cuitB))) continue;
                    var razonB = !string.IsNullOrWhiteSpace(cliB?.RazonSocial) ? cliB!.RazonSocial : f.CodCliente;

                    totalBi += f.Importe; totalKgBi += f.Kilos; cantBi += f.Comprobantes;
                    pmBi[f.Mes] = (pmBi.TryGetValue(f.Mes, out var xb) ? xb : 0m) + f.Importe;
                    pmKgBi[f.Mes] = (pmKgBi.TryGetValue(f.Mes, out var xkb) ? xkb : 0m) + f.Kilos;
                    pcBi[f.CodCliente] = pcBi.TryGetValue(f.CodCliente, out var eB)
                        ? eB with { Imp = eB.Imp + f.Importe, Kg = eB.Kg + f.Kilos }
                        : new ClienteImp(f.CodCliente, cuitB, razonB, f.Importe, f.Kilos);
                }
                return new BaseResultado(baseNombre, true, null, totalBi, totalKgBi, cantBi, pmBi, pmKgBi, pcBi);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* sin BI_VENTAS -> fallback */ }

            // FALLBACK (bases sin BI_VENTAS): importe desde ComprobanteCliente + kg aparte.
            var signos = await _ventas.SignosPorTipoAsync(baseNombre, ct);
            var comps = await _ventas.ComprobantesAsync(baseNombre, desde, hasta, codigos, forzar, ct);

            decimal total = 0m;
            int cantidad = 0;
            var porMes = new Dictionary<string, decimal>();
            var porMesKg = new Dictionary<string, decimal>();
            var porCliente = new Dictionary<string, ClienteImp>();

            foreach (var c in comps)
            {
                var signo = signos.TryGetValue(c.Tipo, out var s) ? s : 0;
                if (signo == 0) continue;   // recibos u otros que no cuentan como venta

                padron.TryGetValue(c.CodCliente, out var cli);
                var razon = !string.IsNullOrWhiteSpace(cli?.RazonSocial) ? cli!.RazonSocial : c.CodCliente;
                var cuit = cli?.Cuit ?? "";

                // Modo Grupo: se saltean por completo las cuentas del grupo (Xardo/Bark),
                // así no cuentan en total, cantidad, evolución por mes ni ranking.
                if (excluirGrupo && CuitsGrupo.Contains(SoloDigitos(cuit))) continue;

                var neto = c.Total * signo;
                total += neto;
                cantidad++;

                var mes = c.Fecha.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                porMes[mes] = (porMes.TryGetValue(mes, out var x) ? x : 0m) + neto;

                // Agrupamos por CÓDIGO (con su CUIT adjunto). El front decide si unifica
                // por CUIT según el checkbox "Unificar por CUIT".
                porCliente[c.CodCliente] = porCliente.TryGetValue(c.CodCliente, out var e)
                    ? e with { Imp = e.Imp + neto }
                    : new ClienteImp(c.CodCliente, cuit, razon, neto, 0m);
            }

            // KILOS (desde BI_VENTAS, ya neteados de créditos). Fuente aparte del importe:
            // si la base no tiene la vista, el kg queda en 0 y el importe sigue igual.
            decimal totalKg = 0m;
            try
            {
                var kilos = await _ventas.KilosPorClienteMesAsync(baseNombre, desde, hasta, codigos, forzar, ct);
                foreach (var k in kilos)
                {
                    padron.TryGetValue(k.CodCliente, out var cliK);
                    var cuitK = cliK?.Cuit ?? "";
                    if (excluirGrupo && CuitsGrupo.Contains(SoloDigitos(cuitK))) continue;

                    totalKg += k.Kilos;
                    porMesKg[k.Mes] = (porMesKg.TryGetValue(k.Mes, out var xk) ? xk : 0m) + k.Kilos;

                    if (porCliente.TryGetValue(k.CodCliente, out var eK))
                        porCliente[k.CodCliente] = eK with { Kg = eK.Kg + k.Kilos };
                    else
                    {
                        var razonK = !string.IsNullOrWhiteSpace(cliK?.RazonSocial) ? cliK!.RazonSocial : k.CodCliente;
                        porCliente[k.CodCliente] = new ClienteImp(k.CodCliente, cuitK, razonK, 0m, k.Kilos);
                    }
                }
            }
            catch { /* base sin BI_VENTAS o consulta fallida: kg en 0, no rompe el importe */ }

            return new BaseResultado(baseNombre, true, null, total, totalKg, cantidad, porMes, porMesKg, porCliente);
        }
        catch (Exception ex)
        {
            return new BaseResultado(baseNombre, false, ex.Message, 0m, 0m, 0,
                new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
                new Dictionary<string, ClienteImp>());
        }
    }

    // Acumulador por cliente (por código) dentro de una base; lleva el CUIT adjunto.
    private sealed record ClienteImp(string Cod, string Cuit, string Razon, decimal Imp, decimal Kg);

    private sealed record BaseResultado(
        string Base, bool Ok, string? Error, decimal Total, decimal TotalKilos, int Cantidad,
        Dictionary<string, decimal> PorMes, Dictionary<string, decimal> PorMesKg,
        Dictionary<string, ClienteImp> PorCliente);
}
