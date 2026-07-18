using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Auth;

// Decide si un usuario puede usar una función del portal. Es INDEPENDIENTE de los "Permisos"
// de intranet: acá manda la audiencia (para externos) y la asignación por función (para
// internos). Lo usan el menú (`/api/funciones-portal`) y los endpoints sensibles (candado real).
public class AccesoFuncionesService
{
    private readonly PortalDbContext _db;
    public AccesoFuncionesService(PortalDbContext db) => _db = db;

    // Chequeo contra la base (para proteger un endpoint). true si la función existe, está
    // activa y el usuario puede usarla.
    public async Task<bool> PuedeUsarAsync(string clave, ClaimsPrincipal user, CancellationToken ct = default)
    {
        var f = await _db.FuncionesPortal.AsNoTracking().FirstOrDefaultAsync(x => x.Clave == clave, ct);
        return f is not null && f.Activa && PuedeUsar(f, user);
    }

    // La regla, en memoria (para filtrar el menú sin ir de a una a la base):
    //  - EXTERNO: accede si la audiencia es "externo" o "ambos".
    //  - INTERNO: la audiencia tiene que incluir "interno" ("interno"/"ambos") Y además
    //    (es admin · o TodosLosInternos · o su identificador está asignado).
    public static bool PuedeUsar(FuncionPortal f, ClaimsPrincipal user)
    {
        var esInterno = user.FindFirstValue("tipo") == "Interno";
        if (!esInterno)
            return f.Audiencia is "externo" or "ambos";

        if (f.Audiencia is not ("interno" or "ambos")) return false;
        if (user.FindFirstValue("esAdmin") == "true") return true;
        if (f.TodosLosInternos) return true;
        var ident = user.FindFirstValue("identificador") ?? "";
        return f.UsuariosAsignados.Contains(ident, StringComparer.OrdinalIgnoreCase);
    }
}
