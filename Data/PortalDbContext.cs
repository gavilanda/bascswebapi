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
    public DbSet<PreRemitoPercepcionIngBr> PreRemitoPercepcionesIngBr => Set<PreRemitoPercepcionIngBr>();
    public DbSet<AuditoriaPreRemito> AuditoriaPreRemitos => Set<AuditoriaPreRemito>();
    public DbSet<ConfiguracionBase> ConfiguracionesBase => Set<ConfiguracionBase>();
    public DbSet<FuncionPortal> FuncionesPortal => Set<FuncionPortal>();
    public DbSet<OrdenCompra> OrdenesCompra => Set<OrdenCompra>();
    public DbSet<OrdenCompraLinea> OrdenCompraLineas => Set<OrdenCompraLinea>();
    public DbSet<EmisionEcheq> EmisionesEcheq => Set<EmisionEcheq>();
    public DbSet<PreferenciaPortal> Preferencias => Set<PreferenciaPortal>();

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

        // BasesPortal: misma estrategia que Permisos (lista -> texto separado por comas).
        var basesConverter = new ValueConverter<List<string>, string>(
            v => string.Join(',', v),
            v => string.IsNullOrEmpty(v)
                ? new List<string>()
                : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());

        var basesComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
            v => v.ToList());

        e.Property(u => u.BasesPortal)
            .HasConversion(basesConverter, basesComparer);

        // ---- Ingresos (pre-remitos) ----
        var pr = modelBuilder.Entity<PreRemito>();

        pr.Property(p => p.Estado).HasConversion<string>();
        // El tipo de comprobante se guarda como texto ("Remito"/"Factura"), igual
        // que el estado. La columna se creó como TEXT con default 'Remito'.
        pr.Property(p => p.TipoComprobante).HasConversion<string>();

        // Concurrencia optimista: RowVersion entra en el WHERE al actualizar.
        pr.Property(p => p.RowVersion).IsConcurrencyToken();

        // Renglones: borrado en cascada al eliminar la cabecera.
        pr.HasMany(p => p.Lineas)
          .WithOne(l => l.PreRemito!)
          .HasForeignKey(l => l.PreRemitoId)
          .OnDelete(DeleteBehavior.Cascade);

        // Percepciones de IIBB (factura): también en cascada con la cabecera.
        pr.HasMany(p => p.PercepcionesIngBr)
          .WithOne(x => x.PreRemito!)
          .HasForeignKey(x => x.PreRemitoId)
          .OnDelete(DeleteBehavior.Cascade);

        // ---- Auditoría ----
        // Log independiente: sin relación con PreRemito, así sobrevive a su borrado.
        var au = modelBuilder.Entity<AuditoriaPreRemito>();
        au.HasIndex(a => a.FechaHora);
        au.HasIndex(a => a.Usuario);
        au.HasIndex(a => a.ProveedorCodigo);

        // ---- Configuración de bases ----
        var cb = modelBuilder.Entity<ConfiguracionBase>();
        cb.HasIndex(c => c.Nombre).IsUnique();

        // ---- Funciones del portal (menú data-driven) ----
        // La Clave es el vínculo con el código del front; no se repite.
        var fp = modelBuilder.Entity<FuncionPortal>();
        fp.HasIndex(f => f.Clave).IsUnique();

        // UsuariosAsignados: lista de identificadores como texto separado por comas
        // (misma estrategia que Permisos / BasesPortal).
        var asignadosConverter = new ValueConverter<List<string>, string>(
            v => string.Join(',', v),
            v => string.IsNullOrEmpty(v)
                ? new List<string>()
                : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
        var asignadosComparer = new ValueComparer<List<string>>(
            (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
            v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
            v => v.ToList());
        fp.Property(f => f.UsuariosAsignados).HasConversion(asignadosConverter, asignadosComparer);

        // ---- Órdenes de compra ----
        var oc = modelBuilder.Entity<OrdenCompra>();
        oc.Property(o => o.Estado).HasConversion<string>();     // "Borrador"/"Grabada" legible
        oc.Property(o => o.RowVersion).IsConcurrencyToken();    // concurrencia optimista
        oc.HasMany(o => o.Lineas)
          .WithOne(l => l.OrdenCompra!)
          .HasForeignKey(l => l.OrdenCompraId)
          .OnDelete(DeleteBehavior.Cascade);

        // ---- Echeqs emitidos por API ----
        // Un cheque (por base) se emite una sola vez: índice único anti-doble-emisión.
        modelBuilder.Entity<EmisionEcheq>()
            .HasIndex(e => new { e.BaseNombre, e.NumeroCheque }).IsUnique();
    }
}
