using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Auth;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// E-Cheques (función interna). Consulta los cheques emitidos de una chequera para exportar
// al formato .xls del banco. Va DIRECTO al SQL Server de la base (BAS no lo expone por
// WebAPI). Dos endpoints: uno JSON (recuento/prueba) y uno que devuelve el .xls para bajar.
[ApiController]
[Route("api/echeques")]
[Authorize]
public class EchequesController : ControllerBase
{
    private readonly BasEchequesService _echeques;
    private readonly AccesoFuncionesService _acceso;

    public EchequesController(BasEchequesService echeques, AccesoFuncionesService acceso)
    {
        _echeques = echeques;
        _acceso = acceso;
    }

    private bool EsInterno() => User.FindFirstValue("tipo") == "Interno";

    // Candado real: interno + asignado a la función "echeques". Devuelve el 403 o null.
    private async Task<ActionResult?> SinAccesoAsync(CancellationToken ct)
        => (EsInterno() && await _acceso.PuedeUsarAsync("echeques", User, ct))
            ? null
            : StatusCode(403, new { mensaje = "No tenés permiso para usar E-Cheques." });

    // GET /api/echeques/bases -> bases con la config SQL de e-cheques completa (para el dropdown).
    [HttpGet("bases")]
    public async Task<ActionResult> Bases(CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var bases = await _echeques.BasesConfiguradasAsync(ct);
        return Ok(new { bases });
    }

    private sealed record Params(string Base, DateOnly D, DateOnly H, string Banco, string Chequera, string ChqD, string ChqH);

    // Valida y normaliza los parámetros comunes a los dos endpoints. Devuelve el error (o null).
    private ActionResult? Parsear(string? baseNombre, string? desde, string? hasta, string? banco,
        string? chequera, string? chqDesde, string? chqHasta, out Params? p)
    {
        p = null;
        if (!DateOnly.TryParse(desde, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
            !DateOnly.TryParse(hasta, CultureInfo.InvariantCulture, DateTimeStyles.None, out var h))
            return BadRequest(new { mensaje = "Fechas inválidas (usá yyyy-MM-dd)." });
        if (h < d)
            return BadRequest(new { mensaje = "La fecha 'Desde' no puede ser mayor que 'Hasta'." });

        baseNombre = (baseNombre ?? "").Trim();
        banco = (banco ?? "").Trim();
        chequera = (chequera ?? "").Trim();
        if (baseNombre.Length == 0 || banco.Length == 0 || chequera.Length == 0)
            return BadRequest(new { mensaje = "Faltan datos: base, banco y chequera son obligatorios." });

        var cd = (chqDesde ?? "").Trim();
        var chh = (chqHasta ?? "").Trim();
        if ((cd.Length > 0) != (chh.Length > 0))
            return BadRequest(new { mensaje = "Completá ambos números de cheque (Desde y Hasta) o dejá los dos vacíos." });

        p = new Params(baseNombre, d, h, banco, chequera, cd, chh);
        return null;
    }

    // GET /api/echeques?base=&desde=&hasta=&banco=&chequera=&chqDesde=&chqHasta=  -> JSON (recuento/prueba)
    [HttpGet]
    public async Task<ActionResult> Consultar(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;

        try
        {
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);
            return Ok(new { cantidad = filas.Count });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo consultar e-cheques: " + ex.Message }); }
    }

    // GET /api/echeques/exportar?... -> devuelve el .xls (o JSON { cantidad:0 } si no hay registros).
    [HttpGet("exportar")]
    public async Task<ActionResult> Exportar(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;

        try
        {
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);
            if (filas.Count == 0) return Ok(new { cantidad = 0 });   // sin registros: el front avisa
            var bytes = BasEchequesService.ArmarXls(filas);
            Response.Headers["X-Echeques-Cantidad"] = filas.Count.ToString();
            return File(bytes, "application/vnd.ms-excel", "e-cheques_EXPORTACION.xls");
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar e-cheques: " + ex.Message }); }
    }
}
