using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Auth;
using PortalClientes.Data;

namespace PortalClientes.Controllers;

// Consulta del log de auditoría de pre-remitos. Solo lectura.
// Protegido con el permiso "auditar".
[ApiController]
[Route("api/auditoria")]
[Authorize(Policy = Permisos.Auditar)]
public class AuditoriaController : ControllerBase
{
    private readonly PortalDbContext _db;

    public AuditoriaController(PortalDbContext db) => _db = db;

    // GET /api/auditoria?ingresoDesde=&ingresoHasta=&usuario=&compDesde=&compHasta=&proveedor=&evento=&limit=
    // Todos los filtros son opcionales. Las fechas en formato yyyy-MM-dd.
    [HttpGet]
    public async Task<ActionResult> Consultar(
        [FromQuery] DateTime? ingresoDesde,
        [FromQuery] DateTime? ingresoHasta,
        [FromQuery] string? usuario,
        [FromQuery] DateTime? compDesde,
        [FromQuery] DateTime? compHasta,
        [FromQuery] string? proveedor,
        [FromQuery] string? evento,
        [FromQuery] int limit = 500)
    {
        limit = Math.Clamp(limit, 1, 2000);

        IQueryable<Models.AuditoriaPreRemito> q = _db.AuditoriaPreRemitos;

        // Fecha de ingreso (fecha/hora del suceso). "hasta" incluye todo el día.
        if (ingresoDesde.HasValue)
            q = q.Where(a => a.FechaHora >= ingresoDesde.Value.Date);
        if (ingresoHasta.HasValue)
            q = q.Where(a => a.FechaHora < ingresoHasta.Value.Date.AddDays(1));

        // Fecha del comprobante del proveedor.
        if (compDesde.HasValue)
            q = q.Where(a => a.ComprobanteFecha != null && a.ComprobanteFecha >= compDesde.Value.Date);
        if (compHasta.HasValue)
            q = q.Where(a => a.ComprobanteFecha != null && a.ComprobanteFecha < compHasta.Value.Date.AddDays(1));

        // Usuario (coincidencia parcial, sin distinguir mayúsculas).
        if (!string.IsNullOrWhiteSpace(usuario))
        {
            var u = usuario.Trim();
            q = q.Where(a => EF.Functions.Like(a.Usuario, "%" + u + "%"));
        }

        // Proveedor: por código o por razón social (coincidencia parcial).
        if (!string.IsNullOrWhiteSpace(proveedor))
        {
            var p = proveedor.Trim();
            q = q.Where(a =>
                (a.ProveedorCodigo != null && EF.Functions.Like(a.ProveedorCodigo, "%" + p + "%"))
                || (a.ProveedorRazonSocial != null && EF.Functions.Like(a.ProveedorRazonSocial, "%" + p + "%")));
        }

        // Tipo de evento exacto (Alta, Modificacion, ...).
        if (!string.IsNullOrWhiteSpace(evento) && !string.Equals(evento, "todos", StringComparison.OrdinalIgnoreCase))
        {
            var e = evento.Trim();
            q = q.Where(a => a.Evento == e);
        }

        var registros = await q
            .OrderByDescending(a => a.FechaHora)
            .Take(limit)
            .Select(a => new
            {
                a.Id,
                a.PreRemitoId,
                a.Evento,
                a.Usuario,
                a.FechaHora,
                a.ProveedorCodigo,
                a.ProveedorRazonSocial,
                a.ComprobanteFecha,
                a.ComprobantePrefijo,
                a.ComprobanteNumero,
                a.Estado,
                a.Detalle
            })
            .ToListAsync();

        return Ok(new { total = registros.Count, limite = limit, registros });
    }
}
