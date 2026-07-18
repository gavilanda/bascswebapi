namespace PortalClientes.Models;

// Como se loguea cada usuario del portal.
public enum TipoUsuario
{
    Interno,   // gente de la empresa: se loguea con nombre de usuario
    Extranet   // se loguea con CUIT; puede ser cliente, proveedor o ambos
}

// Un usuario del portal. Para extranet, un mismo CUIT puede ser cliente Y
// proveedor a la vez (le vendemos y nos vende), por eso son dos flags con
// dos codigos distintos de BAS.
public class UsuarioPortal
{
    public int Id { get; set; }

    // Identificador de login: nombre de usuario (interno) o CUIT (extranet). Unico.
    public string Identificador { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public TipoUsuario Tipo { get; set; } = TipoUsuario.Extranet;

    // Solo internos. El admin administra usuarios y asigna los permisos.
    public bool EsAdmin { get; set; } = false;

    // Permisos funcionales del usuario interno (combinables). Ver Auth/Permisos.cs.
    // En extranet va vacia.
    public List<string> Permisos { get; set; } = new();

    // Acceso al PORTAL DE CLIENTES. Para un usuario INTERNO, habilita usar el
    // portal como "consulta de staff" (buscar un cliente y ver su cuenta). Para
    // extranet el acceso lo define su rol de cliente; este flag no lo restringe.
    public bool AccedePortalClientes { get; set; } = false;

    // Bases que este usuario puede ver en el portal de clientes. Es un SUBCONJUNTO
    // de las bases del portal (las activas marcadas IncluirEnPortal). Si va vacia,
    // el usuario ve TODAS las bases del portal (comportamiento por defecto).
    public List<string> BasesPortal { get; set; } = new();

    // Extranet: roles (uno, otro, o los dos) y su codigo en BAS para cada rol.
    public bool EsCliente { get; set; } = false;
    public bool EsProveedor { get; set; } = false;
    public string? CodigoCliente { get; set; }
    public string? CodigoProveedor { get; set; }

    // Razon social que trajo BAS al dar de alta (para verificar el CUIT a ojo).
    // Si es cliente y proveedor, se guarda la de cliente.
    public string? RazonSocial { get; set; }

    public string? Email { get; set; }
    public bool Activo { get; set; } = true;
    public DateTimeOffset FechaAlta { get; set; } = DateTimeOffset.Now;

    // Preferencia del usuario para la barra "Ver bases" (compartida entre cuenta corriente
    // y estadísticas): JSON { "orden": ["XARDO","BARK",...], "ocultas": ["PRUEBAB"] }.
    // orden = orden preferido; ocultas = bases destildadas (default: todas tildadas).
    // Se maneja como texto opaco; el front arma/parsea el JSON. null = sin preferencia.
    public string? PrefBases { get; set; }
}
