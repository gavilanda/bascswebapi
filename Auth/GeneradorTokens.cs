using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PortalClientes.Models;

namespace PortalClientes.Auth;

// Genera el token JWT del portal. Adentro viajan los datos que el backend
// necesita despues: tipo, si es admin, sus permisos, y (para extranet) sus
// roles y codigos de BAS.
public class GeneradorTokens
{
    private readonly JwtPortalOptions _opciones;

    public GeneradorTokens(IOptions<JwtPortalOptions> opciones) => _opciones = opciones.Value;

    public string GenerarParaUsuario(UsuarioPortal usuario)
    {
        var clave = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opciones.ClaveSecreta));
        var credenciales = new SigningCredentials(clave, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
            new("identificador", usuario.Identificador),
            new("tipo", usuario.Tipo.ToString()),
            new("esAdmin", usuario.EsAdmin ? "true" : "false"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // Un claim "permiso" por cada permiso funcional. Las policies por permiso
        // (ver Program.cs) chequean que exista el claim con ese valor.
        foreach (var permiso in usuario.Permisos)
            claims.Add(new Claim("permiso", permiso));

        // Para extranet sumamos los roles y los codigos de BAS (cliente y/o proveedor).
        if (usuario.Tipo == TipoUsuario.Extranet)
        {
            claims.Add(new Claim("esCliente", usuario.EsCliente ? "true" : "false"));
            claims.Add(new Claim("esProveedor", usuario.EsProveedor ? "true" : "false"));
            claims.Add(new Claim("codigoCliente", usuario.CodigoCliente ?? string.Empty));
            claims.Add(new Claim("codigoProveedor", usuario.CodigoProveedor ?? string.Empty));
        }

        var token = new JwtSecurityToken(
            issuer: _opciones.Issuer,
            audience: _opciones.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_opciones.ExpiraMinutos),
            signingCredentials: credenciales);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
