using PortalClientes.Models;

namespace PortalClientes.Auth;

// Lo que el cliente envia para loguearse (identificador = usuario o CUIT).
public record SolicitudLogin(string Identificador, string Password);

// Lo que devolvemos cuando el login es correcto.
public record RespuestaLogin(
    string Token,
    string Identificador,
    string Tipo,
    bool EsAdmin,
    bool EsCliente,
    bool EsProveedor,
    string? CodigoCliente,
    string? CodigoProveedor,
    string? RazonSocial,
    List<string> Permisos,
    DateTimeOffset Expira);

// Alta de un usuario desde la administracion.
//  - Interno:  Identificador = nombre de usuario, EsAdmin (opcional), Permisos (opcional).
//  - Extranet: Identificador = CUIT. El rol se infiere de los codigos:
//    si hay codigo de cliente -> es cliente; si hay de proveedor -> es proveedor.
public record CrearUsuarioRequest(
    TipoUsuario Tipo,
    string Identificador,
    string Password,
    bool EsAdmin,
    string? CodigoCliente,
    string? CodigoProveedor,
    string? Email,
    string? RazonSocial,
    List<string>? Permisos);

// Modificacion de un usuario existente. Password opcional: si viene, se cambia;
// si va vacio, se mantiene la actual. El Tipo no se cambia (lo toma del existente).
public record ModificarUsuarioRequest(
    string Identificador,
    string? Password,
    bool EsAdmin,
    string? CodigoCliente,
    string? CodigoProveedor,
    string? Email,
    string? RazonSocial,
    List<string>? Permisos);

// Cambio de estado (activo/inactivo).
public record CambiarEstadoRequest(bool Activo);

// Cambio de contrasena de un usuario (reset por parte del admin).
public record CambiarPasswordRequest(string NuevaPassword);
