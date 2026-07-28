using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortalClientes.Auth;
using PortalClientes.Bas;
using PortalClientes.Data;

namespace PortalClientes.Controllers;

// Bco/Conciliación (función interna). Trae los movimientos de una cuenta del banco (API
// Credicoop, scope cuentas) para conciliar, y genera el TXT posicional que importa BAS.
// Usa la misma config del banco POR EMPRESA que E-Cheques (BieCredenciales).
[ApiController]
[Route("api/conciliacion")]
[Authorize]
public class ConciliacionController : ControllerBase
{
    private readonly BancoBieCuentasService _cuentas;
    private readonly BancoBieOptions _bieOpt;
    private readonly PortalDbContext _db;
    private readonly AccesoFuncionesService _acceso;

    public ConciliacionController(BancoBieCuentasService cuentas, IOptions<BancoBieOptions> bieOpt,
        PortalDbContext db, AccesoFuncionesService acceso)
    {
        _cuentas = cuentas;
        _bieOpt = bieOpt.Value;
        _db = db;
        _acceso = acceso;
    }

    private bool EsInterno() => User.FindFirstValue("tipo") == "Interno";

    private async Task<ActionResult?> SinAccesoAsync(CancellationToken ct)
        => (EsInterno() && await _acceso.PuedeUsarAsync("conciliacion", User, ct))
            ? null
            : StatusCode(403, new { mensaje = "No tenés permiso para usar Conciliación." });

    private async Task<BieCredenciales?> CredsAsync(string baseNombre, CancellationToken ct)
    {
        var cb = await _db.ConfiguracionesBase.AsNoTracking().FirstOrDefaultAsync(c => c.Nombre == baseNombre, ct);
        return cb is null ? null : _bieOpt.Credenciales(cb);
    }

    // GET /api/conciliacion/bases -> empresas con la API del banco configurada.
    [HttpGet("bases")]
    public async Task<ActionResult> Bases(CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var cfgs = await _db.ConfiguracionesBase.AsNoTracking().ToListAsync(ct);
        var bases = cfgs.Where(c => _bieOpt.Credenciales(c) is not null)
            .Select(c => c.Nombre).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return Ok(new { bases });
    }

    // GET /api/conciliacion/cuentas?base= -> cuentas del adherente (para el selector).
    [HttpGet("cuentas")]
    public async Task<ActionResult> Cuentas([FromQuery(Name = "base")] string? baseNombre, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var creds = await CredsAsync((baseNombre ?? "").Trim(), ct);
        if (creds is null) return StatusCode(409, new { mensaje = $"La empresa '{baseNombre}' no tiene la API del banco configurada." });
        try
        {
            var cuentas = await _cuentas.ListarCuentasAsync(creds, ct);
            return Ok(new { cuentas });
        }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudieron traer las cuentas: " + ex.Message }); }
    }

    // Valida y normaliza base + cuenta + rango de fechas.
    private ActionResult? Parsear(string? baseNombre, string? cuenta, string? desde, string? hasta,
        out string b, out string cta, out DateOnly d, out DateOnly h)
    {
        b = (baseNombre ?? "").Trim(); cta = (cuenta ?? "").Trim(); d = default; h = default;
        if (b.Length == 0 || cta.Length == 0) return BadRequest(new { mensaje = "Elegí empresa y cuenta." });
        if (!DateOnly.TryParse(desde, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ||
            !DateOnly.TryParse(hasta, CultureInfo.InvariantCulture, DateTimeStyles.None, out h))
            return BadRequest(new { mensaje = "Fechas inválidas (usá yyyy-MM-dd)." });
        if (h < d) return BadRequest(new { mensaje = "La fecha 'Desde' no puede ser mayor que 'Hasta'." });
        return null;
    }

    // GET /api/conciliacion/movimientos?base=&cuenta=&desde=&hasta= -> movimientos (para el modal).
    [HttpGet("movimientos")]
    public async Task<ActionResult> Movimientos(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? cuenta,
        [FromQuery] string? desde, [FromQuery] string? hasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, cuenta, desde, hasta, out var b, out var cta, out var d, out var h);
        if (err is not null) return err;
        var creds = await CredsAsync(b, ct);
        if (creds is null) return StatusCode(409, new { mensaje = $"La empresa '{b}' no tiene la API del banco configurada." });
        try
        {
            var movimientos = await _cuentas.MovimientosAsync(creds, cta, d, h, ct);
            return Ok(new { cantidad = movimientos.Count, movimientos });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudieron traer los movimientos: " + ex.Message }); }
    }

    // GET /api/conciliacion/txt?base=&cuenta=&desde=&hasta= -> TXT posicional para BAS.
    [HttpGet("txt")]
    public async Task<ActionResult> Txt(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? cuenta,
        [FromQuery] string? desde, [FromQuery] string? hasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, cuenta, desde, hasta, out var b, out var cta, out var d, out var h);
        if (err is not null) return err;
        var creds = await CredsAsync(b, ct);
        if (creds is null) return StatusCode(409, new { mensaje = $"La empresa '{b}' no tiene la API del banco configurada." });
        try
        {
            var movimientos = await _cuentas.MovimientosAsync(creds, cta, d, h, ct);
            if (movimientos.Count == 0) return Ok(new { cantidad = 0 });   // sin movimientos: el front avisa
            var bytes = BancoBieCuentasService.ArmarTxt(movimientos);
            Response.Headers["X-Conciliacion-Cantidad"] = movimientos.Count.ToString();
            var nombre = $"conciliacion_{cta}_{d:yyyyMMdd}_{h:yyyyMMdd}.txt";
            return File(bytes, "text/plain", nombre);
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar el TXT: " + ex.Message }); }
    }
}
