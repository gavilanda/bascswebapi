using System.Collections.Concurrent;

namespace PortalClientes.Bas;

// Foto inmutable del padrón de una base.
//   Bienes:      código -> datos del artículo (BienInfo).
//   Proveedores: código -> razón social.
// Se reemplaza entera en cada actualización (swap atómico de referencia), así
// los lectores siempre ven una foto completa y consistente. Cada maestro tiene
// su propio "listo": si uno falla (p.ej. proveedores de una base lenta), el
// otro igual queda disponible.
public sealed class SnapshotMaestro
{
    public IReadOnlyDictionary<string, BienInfo> Bienes { get; init; } = VacioBienes;
    public IReadOnlyDictionary<string, string> Proveedores { get; init; } = VacioProv;
    public bool BienesListo { get; init; }
    public bool ProveedoresListo { get; init; }
    public DateTimeOffset? Actualizado { get; init; }
    public string? Error { get; init; }

    private static readonly IReadOnlyDictionary<string, BienInfo> VacioBienes = new Dictionary<string, BienInfo>();
    private static readonly IReadOnlyDictionary<string, string> VacioProv = new Dictionary<string, string>();
}

// Caché en memoria del padrón de productos y proveedores por base.
// Singleton: vive mientras corre el servidor.
public class BasCacheMaestros
{
    private readonly ConcurrentDictionary<string, SnapshotMaestro> _porBase = new();

    public SnapshotMaestro Obtener(string baseNombre)
        => _porBase.TryGetValue(baseNombre, out var s) ? s : new SnapshotMaestro();

    public void Guardar(string baseNombre, SnapshotMaestro snap)
        => _porBase[baseNombre] = snap;

    // Lectura-modificación segura: se usa para ir guardando de a un maestro
    // a medida que carga.
    public void Actualizar(string baseNombre, Func<SnapshotMaestro, SnapshotMaestro> f)
        => _porBase.AddOrUpdate(baseNombre, _ => f(new SnapshotMaestro()), (_, actual) => f(actual));

    public IReadOnlyDictionary<string, SnapshotMaestro> Todos => _porBase;
}
