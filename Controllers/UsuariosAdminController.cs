using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Auth;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Controllers;

// Administracion de usuarios del portal. Solo para internos administradores
// (la politica "Admin" exige el claim esAdmin=true).
[ApiController]
[Route("api/admin/usuarios")]
[Authorize(Policy = "Admin")]
public class UsuariosAdminController : ControllerBase
{
    private readonly PortalDbContext _db;
    private readonly IPasswordHasher<UsuarioPortal> _hasher;

    public UsuariosAdminController(PortalDbContext db, IPasswordHasher<UsuarioPortal> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    // GET /api/admin/permisos  -> catalogo de permisos funcionales (codigo + etiqueta).
    // El front lo usa para armar los checkboxes solo.
    [HttpGet("/api/admin/permisos")]
    public ActionResult CatalogoPermisos() => Ok(PortalClientes.Auth.Permisos.Catalogo);

    // GET /api/admin/usuarios?estado=activos|inactivos|todos&tipo=internos|externos|todos
    [HttpGet]
    public async Task<ActionResult> Listar(
        [FromQuery] string estado = "activos",
        [FromQuery] string tipo = "internos")
    {
        var query = _db.Usuarios.AsQueryable();

        estado = (estado ?? "activos").ToLowerInvariant();
        if (estado == "activos")
            query = query.Where(u => u.Activo);
        else if (estado == "inactivos")
            query = query.Where(u => !u.Activo);

        tipo = (tipo ?? "internos").ToLowerInvariant();
        if (tipo == "internos")
            query = query.Where(u => u.Tipo == TipoUsuario.Interno);
        else if (tipo == "externos" || tipo == "extranet")
            query = query.Where(u => u.Tipo == TipoUsuario.Extranet);

        // Traemos las entidades y armamos la salida en memoria (asi incluimos
        // Permisos, que es una propiedad convertida, sin sorpresas en el SQL).
        var entidades = await query.OrderBy(u => u.Identificador).ToListAsync();

        var usuarios = entidades.Select(u => new
        {
            u.Id,
            u.Identificador,
            Tipo = u.Tipo,
            u.EsAdmin,
            u.Permisos,
            u.EsCliente,
            u.EsProveedor,
            u.CodigoCliente,
            u.CodigoProveedor,
            u.RazonSocial,
            u.Email,
            u.Activo,
            u.FechaAlta
        });

        return Ok(usuarios);
    }

    // POST /api/admin/usuarios  -> crea un usuario (interno o extranet).
    [HttpPost]
    public async Task<ActionResult> Crear(CrearUsuarioRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Identificador) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { mensaje = "Identificador y contrasena son obligatorios." });

        var ident = req.Identificador.Trim();
        if (await _db.Usuarios.AnyAsync(u => u.Identificador == ident))
            return Conflict(new { mensaje = "Ya existe un usuario con ese identificador." });

        var usuario = new UsuarioPortal
        {
            Identificador = ident,
            Tipo = req.Tipo,
            Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
            Activo = true
        };

        if (req.Tipo == TipoUsuario.Interno)
        {
            usuario.EsAdmin = req.EsAdmin;
            usuario.Permisos = PortalClientes.Auth.Permisos.Limpiar(req.Permisos);
            usuario.EsCliente = false;
            usuario.EsProveedor = false;
            usuario.CodigoCliente = null;
            usuario.CodigoProveedor = null;
            usuario.RazonSocial = null;
        }
        else // Extranet: el rol se infiere de los codigos. Sin permisos internos.
        {
            var codCli = string.IsNullOrWhiteSpace(req.CodigoCliente) ? null : req.CodigoCliente.Trim();
            var codProv = string.IsNullOrWhiteSpace(req.CodigoProveedor) ? null : req.CodigoProveedor.Trim();
            if (codCli is null && codProv is null)
                return BadRequest(new { mensaje = "Carga al menos un codigo (cliente o proveedor)." });

            usuario.EsAdmin = false;
            usuario.Permisos = new();
            usuario.CodigoCliente = codCli;
            usuario.CodigoProveedor = codProv;
            usuario.EsCliente = codCli is not null;
            usuario.EsProveedor = codProv is not null;
            usuario.RazonSocial = string.IsNullOrWhiteSpace(req.RazonSocial) ? null : req.RazonSocial.Trim();
        }

        usuario.PasswordHash = _hasher.HashPassword(usuario, req.Password);

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync();

        return Ok(new { usuario.Id, usuario.Identificador, tipo = usuario.Tipo.ToString() });
    }

    // PUT /api/admin/usuarios/{id}  -> modifica un usuario existente.
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Modificar(int id, ModificarUsuarioRequest req)
    {
        var usuario = await _db.Usuarios.FindAsync(id);
        if (usuario is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(req.Identificador))
            return BadRequest(new { mensaje = "El identificador es obligatorio." });

        var ident = req.Identificador.Trim();
        if (ident != usuario.Identificador &&
            await _db.Usuarios.AnyAsync(u => u.Identificador == ident && u.Id != id))
            return Conflict(new { mensaje = "Ya existe un usuario con ese identificador." });

        if (usuario.Tipo == TipoUsuario.Interno)
        {
            // No dejar el sistema sin ningun admin activo.
            if (usuario.EsAdmin && usuario.Activo && !req.EsAdmin && await EsUltimoAdminActivo(id))
                return BadRequest(new { mensaje = "No se puede quitar admin al unico administrador activo." });

            usuario.Identificador = ident;
            usuario.EsAdmin = req.EsAdmin;
            usuario.Permisos = PortalClientes.Auth.Permisos.Limpiar(req.Permisos);
            usuario.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        }
        else // Extranet: el rol se infiere de los codigos.
        {
            var codCli = string.IsNullOrWhiteSpace(req.CodigoCliente) ? null : req.CodigoCliente.Trim();
            var codProv = string.IsNullOrWhiteSpace(req.CodigoProveedor) ? null : req.CodigoProveedor.Trim();
            if (codCli is null && codProv is null)
                return BadRequest(new { mensaje = "Carga al menos un codigo (cliente o proveedor)." });

            usuario.Identificador = ident;
            usuario.CodigoCliente = codCli;
            usuario.CodigoProveedor = codProv;
            usuario.EsCliente = codCli is not null;
            usuario.EsProveedor = codProv is not null;
            usuario.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
            usuario.RazonSocial = string.IsNullOrWhiteSpace(req.RazonSocial) ? null : req.RazonSocial.Trim();
        }

        // Password opcional: solo si vino algo.
        if (!string.IsNullOrWhiteSpace(req.Password))
        {
            if (req.Password.Length < 6)
                return BadRequest(new { mensaje = "La contrasena debe tener al menos 6 caracteres." });
            usuario.PasswordHash = _hasher.HashPassword(usuario, req.Password);
        }

        await _db.SaveChangesAsync();
        return Ok(new { usuario.Id });
    }

    // PATCH /api/admin/usuarios/{id}/estado  -> baja logica (reversible).
    [HttpPatch("{id:int}/estado")]
    public async Task<ActionResult> CambiarEstado(int id, CambiarEstadoRequest req)
    {
        var usuario = await _db.Usuarios.FindAsync(id);
        if (usuario is null)
            return NotFound();

        if (usuario.EsAdmin && !req.Activo && await EsUltimoAdminActivo(id))
            return BadRequest(new { mensaje = "No se puede desactivar al unico administrador activo. Design\u00e1 otro admin primero." });

        usuario.Activo = req.Activo;
        await _db.SaveChangesAsync();

        return Ok(new { usuario.Id, usuario.Activo });
    }

    // PATCH /api/admin/usuarios/{id}/password  -> resetea la contrasena.
    [HttpPatch("{id:int}/password")]
    public async Task<ActionResult> CambiarPassword(int id, CambiarPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NuevaPassword) || req.NuevaPassword.Length < 6)
            return BadRequest(new { mensaje = "La contrasena debe tener al menos 6 caracteres." });

        var usuario = await _db.Usuarios.FindAsync(id);
        if (usuario is null)
            return NotFound();

        usuario.PasswordHash = _hasher.HashPassword(usuario, req.NuevaPassword);
        await _db.SaveChangesAsync();

        return Ok(new { usuario.Id });
    }

    // DELETE /api/admin/usuarios/{id}  -> baja fisica (irreversible).
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Eliminar(int id)
    {
        var usuario = await _db.Usuarios.FindAsync(id);
        if (usuario is null)
            return NotFound();

        if (usuario.EsAdmin && usuario.Activo && await EsUltimoAdminActivo(id))
            return BadRequest(new { mensaje = "No se puede eliminar al unico administrador activo. Design\u00e1 otro admin primero." });

        _db.Usuarios.Remove(usuario);
        await _db.SaveChangesAsync();

        return Ok(new { eliminado = id });
    }

    // True si, fuera de este usuario, no queda ningun otro admin activo.
    private async Task<bool> EsUltimoAdminActivo(int id)
    {
        var otros = await _db.Usuarios
            .CountAsync(u => u.EsAdmin && u.Activo && u.Id != id);
        return otros == 0;
    }
}
