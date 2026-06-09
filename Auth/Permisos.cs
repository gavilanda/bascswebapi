namespace PortalClientes.Auth;

// Catalogo de permisos funcionales para usuarios internos.
//
// Para sumar un permiso nuevo en el futuro:
//   1) agregar la constante aca,
//   2) agregar su entrada en Catalogo (con una etiqueta legible).
// Con eso:
//   - el front lo muestra solo como checkbox (lee /api/admin/permisos),
//   - el endpoint que quieras proteger se marca con [Authorize(Policy = Permisos.Xxx)],
//   - Program.cs ya registra una policy por cada permiso del catalogo.
//
// El "admin" NO esta aca: sigue siendo un flag aparte (administra usuarios y
// asigna estos permisos). Un usuario interno puede tener cualquier combinacion.
public static class Permisos
{
    public const string EditarRemitos = "editar_remitos";
    public const string ConformarRemitos = "conformar_remitos";

    public record ItemPermiso(string Codigo, string Etiqueta);

    public static readonly IReadOnlyList<ItemPermiso> Catalogo = new List<ItemPermiso>
    {
        new(EditarRemitos,    "Editar pre-remitos de compra"),
        new(ConformarRemitos, "Conformar pre-remitos de compra"),
    };

    public static readonly IReadOnlyList<string> Codigos =
        Catalogo.Select(p => p.Codigo).ToList();

    public static bool EsValido(string codigo) => Codigos.Contains(codigo);

    // Filtra una lista cualquiera dejando solo permisos validos, sin repetidos.
    public static List<string> Limpiar(IEnumerable<string>? permisos) =>
        (permisos ?? Enumerable.Empty<string>())
            .Select(p => (p ?? string.Empty).Trim())
            .Where(EsValido)
            .Distinct()
            .ToList();
}
