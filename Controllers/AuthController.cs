using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortalClientes.Auth;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly PortalDbContext _db;
    private readonly GeneradorTokens _tokens;
    private readonly IPasswordHasher<UsuarioPortal> _hasher;
    private readonly JwtPortalOptions _jwt;

    public AuthController(
        PortalDbContext db,
        GeneradorTokens tokens,
        IPasswordHasher<UsuarioPortal> hasher,
        IOptions<JwtPortalOptions> jwt)
    {
        _db = db;
        _tokens = tokens;
        _hasher = hasher;
        _jwt = jwt.Value;
    }

    // POST /api/auth/login
    // Busca por identificador (usuario interno o CUIT extranet) y, si la
    // contrasena coincide, emite el token del portal.
    [HttpPost("login")]
    public async Task<ActionResult<RespuestaLogin>> Login(SolicitudLogin solicitud)
    {
        var ident = (solicitud.Identificador ?? string.Empty).Trim();

        var usuario = await _db.Usuarios
            .FirstOrDefaultAsync(u => u.Identificador == ident && u.Activo);

        if (usuario is null)
            return Unauthorized(new { mensaje = "Usuario o contrasena incorrectos." });

        var resultado = _hasher.VerifyHashedPassword(usuario, usuario.PasswordHash, solicitud.Password);
        if (resultado == PasswordVerificationResult.Failed)
            return Unauthorized(new { mensaje = "Usuario o contrasena incorrectos." });

        var token = _tokens.GenerarParaUsuario(usuario);

        return Ok(new RespuestaLogin(
            token,
            usuario.Identificador,
            usuario.Tipo.ToString(),
            usuario.EsAdmin,
            usuario.EsCliente,
            usuario.EsProveedor,
            usuario.CodigoCliente,
            usuario.CodigoProveedor,
            usuario.RazonSocial,
            usuario.Permisos,
            DateTimeOffset.UtcNow.AddMinutes(_jwt.ExpiraMinutos)));
    }
}
