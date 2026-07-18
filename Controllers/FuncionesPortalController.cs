using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Auth;
using PortalClientes.Data;

namespace PortalClientes.Controllers;

// Menú del portal, manejado por datos (tabla FuncionesPortal). La tabla decide
// QUÉ consultas aparecen y a QUÉ público; el código (front IMPLEMENTACIONES +
// endpoints) hace la consulta en sí. Dos caras:
//
//  - GET /api/funciones-portal  -> lo consume el PORTAL para armar el menú del
//    usuario logueado, según su tipo (Interno -> interno, Extranet -> externo).
//    Cualquier usuario autenticado.
//  - /api/admin/funciones       -> ABM de metadata (etiqueta, orden, audiencia,
//    activa) para la pantalla de la INTRANET. Sólo admin. La Clave no se crea ni
//    se edita desde acá: es el vínculo con el código y se siembra al publicar.
[ApiController]
[Authorize] // Requiere un token válido del portal.
public class FuncionesPortalController : ControllerBase
{
    private readonly PortalDbContext _db;
    public FuncionesPortalController(PortalDbContext db) => _db = db;

    private static readonly string[] AudienciasValidas = { "externo", "interno", "ambos" };

    // GET /api/funciones-portal -> funciones ACTIVAS visibles para este usuario,
    // ordenadas. Es lo que el portal usa para dibujar el menú (una entrada por
    // función, con su clave y etiqueta). El front cruza la clave con su catálogo
    // de implementaciones e ignora las que no implemente.
    [HttpGet("api/funciones-portal")]
    public async Task<ActionResult> Menu()
    {
        // Traemos las activas y filtramos EN MEMORIA con la regla de acceso (necesita
        // UsuariosAsignados, que es una propiedad convertida). Es una tabla chica.
        var activas = await _db.FuncionesPortal.AsNoTracking()
            .Where(f => f.Activa)
            .OrderBy(f => f.Orden).ThenBy(f => f.Etiqueta)
            .ToListAsync(HttpContext.RequestAborted);
        var funciones = activas
            .Where(f => AccesoFuncionesService.PuedeUsar(f, User))
            .Select(f => new { f.Clave, f.Etiqueta });
        return Ok(new { funciones });
    }

    // GET /api/admin/funciones -> todas las funciones con su metadata, para la
    // pantalla de administración de la intranet. Sólo admin.
    [HttpGet("api/admin/funciones")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult> Listar()
    {
        var funciones = (await _db.FuncionesPortal.AsNoTracking()
            .OrderBy(f => f.Orden).ThenBy(f => f.Etiqueta)
            .ToListAsync(HttpContext.RequestAborted))
            .Select(f => new
            {
                f.Id, f.Clave, f.Etiqueta, f.Orden, f.Audiencia, f.Activa,
                f.TodosLosInternos, usuariosAsignados = f.UsuariosAsignados
            });
        return Ok(new { funciones });
    }

    // PUT /api/admin/funciones/{id} -> edita la metadata configurable. La Clave
    // NO se toca (es el vínculo con el código). Sólo admin.
    [HttpPut("api/admin/funciones/{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult> Actualizar(int id, [FromBody] ActualizarFuncionRequest req)
    {
        var etiqueta = (req.Etiqueta ?? "").Trim();
        if (etiqueta.Length == 0)
            return BadRequest(new { mensaje = "La etiqueta es obligatoria." });

        var audiencia = (req.Audiencia ?? "").Trim().ToLowerInvariant();
        if (!AudienciasValidas.Contains(audiencia))
            return BadRequest(new { mensaje = "Audiencia inválida (externo, interno o ambos)." });

        var f = await _db.FuncionesPortal.FindAsync(new object[] { id }, HttpContext.RequestAborted);
        if (f is null)
            return NotFound(new { mensaje = "No existe la función." });

        f.Etiqueta = etiqueta;
        f.Orden = req.Orden;
        f.Audiencia = audiencia;
        f.Activa = req.Activa;
        f.TodosLosInternos = req.TodosLosInternos;
        f.UsuariosAsignados = (req.UsuariosAsignados ?? new())
            .Select(s => (s ?? "").Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok(new { mensaje = "Función actualizada.", f.Id });
    }
}

// Edición de una función desde la intranet. La Clave no viaja: no se edita.
public record ActualizarFuncionRequest(
    string Etiqueta, int Orden, string Audiencia, bool Activa,
    bool TodosLosInternos = true, List<string>? UsuariosAsignados = null);
