using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PortalClientes.Models;

namespace PortalClientes.Data;

// Acceso a la base de datos de usuarios del portal.
public class PortalDbContext : DbContext
{
    public PortalDbContext(DbContextOptions<PortalDbContext> options) : base(options) { }

    public DbSet<UsuarioPortal> Usuarios => Set<UsuarioPortal>();
    public DbSet<PreRemito> PreRemitos => Set<PreRemito>();
    public DbSet<PreRemitoLinea> PreRemitoLineas => Set<PreRemitoLinea>();
    public DbSet<AuditoriaPreRemito> AuditoriaPreRemitos => Set<AuditoriaPreRemito>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ---- Usuarios ----
        var e = modelBuilder.Entity<UsuarioPortal>();

        // El identificador (usuario o CUIT) no se puede repetir.
        // Un mismo CUIT es una sola cuenta, aunque sea cliente y proveedor.
        e.HasIndex(u => u.Identificador).IsUnique();

        // Guardamos el tipo como texto legible en la base.
        e.Property(u => u.Tipo).HasConversion<string>();

        // Permisos: la lista se guarda como texto separado por comas en una columna.
        var permisosConverter = new ValueConverter<List<string>, string>(
            v => string.Join(',', v),
            v => string.IsNullOrEmpty(v)
                ? new List<string>()
                : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());

        var permisosComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
            v => v.ToList());

        e.Property(u => u.Permisos)
            .HasConversion(permisosConverter, permisosComparer);

        // ---- Pre-remitos ----
        var pr = modelBuilder.Entity<PreRemito>();

        pr.Property(p => p.Estado).HasConversion<string>();

        // Concurrencia optimista: RowVersion entra en el WHERE al actualizar.
        pr.Property(p => p.RowVersion).IsConcurrencyToken();

        // Renglones: borrado en cascada al eliminar la cabecera.
        pr.HasMany(p => p.Lineas)
          .WithOne(l => l.PreRemito!)
          .HasForeignKey(l => l.PreRemitoId)
          .OnDelete(DeleteBehavior.Cascade);

        // ---- Auditoría ----
        // Log independiente: sin relación con PreRemito, así sobrevive a su borrado.
        var au = modelBuilder.Entity<AuditoriaPreRemito>();
        au.HasIndex(a => a.FechaHora);
        au.HasIndex(a => a.Usuario);
        au.HasIndex(a => a.ProveedorCodigo);
    }
}
