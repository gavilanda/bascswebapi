using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// Administración de la configuración por base BAS (Empresa, Sucursal, prefijos,
// concepto, depósito, etc.). Sólo administradores.
[ApiController]
[Route("api/admin/config-bases")]
[Authorize(Policy = "Admin")]
public class ConfigBasesController : ControllerBase
{
    private readonly ConfigBasesService _svc;

    public ConfigBasesController(ConfigBasesService svc)
    {
        _svc = svc;
    }

    // GET /api/admin/config-bases  -> todas las bases con su config + datos de
    // conexión (sólo lectura: BaseUrl, RemitoTipo).
    [HttpGet]
    public async Task<ActionResult> Listar()
    {
        var filas = await _svc.ListarAsync(HttpContext.RequestAborted);
        var bases = filas.Select(f =>
        {
            var mem = _svc.Memoria(f.Nombre);
            return new
            {
                f.Nombre,
                f.Descripcion,
                f.Activa,
                f.Empresa,
                f.Sucursal,
                f.RemitoPrefijo,
                f.RemitoConcepto,
                f.RemitoDeposito,
                f.FacturaPrefijo,
                f.FacturaConcepto,
                f.FacturaDeposito,
                f.FacturaImputacionContable,
                // Sólo lectura / informativo:
                baseUrl = mem?.BaseUrl,
                remitoTipo = mem?.RemitoTipo,
                conectada = mem != null    // false si la fila quedó sin base en appsettings
            };
        });
        return Ok(new { bases });
    }

    // PUT /api/admin/config-bases/{nombre}  -> edita la config de una base.
    [HttpPut("{nombre}")]
    public async Task<ActionResult> Actualizar(string nombre, [FromBody] ActualizarConfigBaseRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RemitoPrefijo))
            return BadRequest(new { mensaje = "El prefijo de remito es obligatorio." });
        if (string.IsNullOrWhiteSpace(req.RemitoConcepto))
            return BadRequest(new { mensaje = "El concepto de remito es obligatorio." });
        if (string.IsNullOrWhiteSpace(req.FacturaPrefijo))
            return BadRequest(new { mensaje = "El prefijo de factura es obligatorio." });
        if (string.IsNullOrWhiteSpace(req.FacturaConcepto))
            return BadRequest(new { mensaje = "El concepto de factura es obligatorio." });
        if (req.Empresa <= 0 || req.Sucursal <= 0)
            return BadRequest(new { mensaje = "Empresa y Sucursal deben ser mayores a cero." });
        if (req.RemitoDeposito <= 0)
            return BadRequest(new { mensaje = "El depósito de remito debe ser mayor a cero." });
        if (req.FacturaDeposito <= 0)
            return BadRequest(new { mensaje = "El depósito de factura debe ser mayor a cero." });
        if (req.FacturaImputacionContable <= 0)
            return BadRequest(new { mensaje = "La cuenta contable de la factura debe ser mayor a cero." });

        var f = await _svc.ActualizarAsync(nombre, req, HttpContext.RequestAborted);
        if (f is null)
            return NotFound(new { mensaje = $"No existe configuración para la base '{nombre}'." });

        return Ok(new { mensaje = "Configuración guardada.", nombre = f.Nombre });
    }
}
