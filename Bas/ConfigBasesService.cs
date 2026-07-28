using Microsoft.EntityFrameworkCore;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Bas;

// Lee y escribe la configuración por base (tabla ConfiguracionesBase) y la
// mantiene sincronizada con el diccionario de DestinoBas en memoria (vía
// BasDestinosService), que es lo que consumen el grabado y las consultas.
//
// La TABLA es la FUENTE DE VERDAD: define la lista de bases, su conexión (BaseUrl,
// RemitoTipo) y sus parámetros de negocio. appsettings sólo SIEMBRA las filas que
// falten la primera vez. El alta y la baja de bases se hacen acá. Scoped: usa el
// DbContext del request.
public class ConfigBasesService
{
    private readonly PortalDbContext _db;
    private readonly BasDestinosService _destinos;

    public ConfigBasesService(PortalDbContext db, BasDestinosService destinos)
    {
        _db = db;
        _destinos = destinos;
    }

    // Crea filas para las bases que estén en memoria (sembradas de appsettings) y
    // todavía no figuren en la tabla, copiando también la conexión.
    public async Task SembrarFaltantesAsync(CancellationToken ct = default)
    {
        var existentes = await _db.ConfiguracionesBase.Select(c => c.Nombre).ToListAsync(ct);
        var set = new HashSet<string>(existentes, StringComparer.OrdinalIgnoreCase);

        var nuevas = new List<ConfiguracionBase>();
        foreach (var nombre in _destinos.Nombres)
        {
            if (set.Contains(nombre)) continue;
            var d = _destinos.Config(nombre);
            if (d is null) continue;
            nuevas.Add(FilaDesde(nombre, d));
        }

        if (nuevas.Count > 0)
        {
            _db.ConfiguracionesBase.AddRange(nuevas);
            await _db.SaveChangesAsync(ct);
        }
    }

    // Aplica la tabla sobre el diccionario en memoria. Se llama al arranque (y tras
    // cada alta/baja/edición). La tabla manda: CREA en memoria las bases que sólo
    // existen en la tabla (las dadas de alta desde la intranet) y actualiza el resto.
    // Backfill: para filas viejas sin BaseUrl (creadas antes de que la conexión fuera
    // editable), trae la conexión desde memoria (appsettings) a la tabla y la persiste.
    public async Task SincronizarMemoriaAsync(CancellationToken ct = default)
    {
        var filas = await _db.ConfiguracionesBase.ToListAsync(ct);
        var hayCambios = false;

        foreach (var f in filas)
        {
            var mem = _destinos.Config(f.Nombre);

            if (string.IsNullOrWhiteSpace(f.BaseUrl) && mem is not null && !string.IsNullOrWhiteSpace(mem.BaseUrl))
            {
                f.BaseUrl = mem.BaseUrl;
                if (string.IsNullOrWhiteSpace(f.RemitoTipo)) f.RemitoTipo = mem.RemitoTipo;
                hayCambios = true;
            }

            // Fila sin conexión y sin respaldo en memoria: se ignora (inválida).
            if (string.IsNullOrWhiteSpace(f.BaseUrl)) continue;

            var destino = mem ?? new DestinoBas();
            AplicarEnMemoria(destino, f);
            _destinos.Definir(f.Nombre, destino);
        }

        if (hayCambios) await _db.SaveChangesAsync(ct);
    }

    public async Task<List<ConfiguracionBase>> ListarAsync(CancellationToken ct = default)
        => await _db.ConfiguracionesBase.OrderBy(c => c.Nombre).ToListAsync(ct);

    // Datos en memoria de una base (para mostrar / cruzar).
    public DestinoBas? Memoria(string nombre) => _destinos.Config(nombre);

    // Alta de una base nueva: valida, inserta la fila y la pone en memoria.
    // Devuelve la fila creada, o (null, motivo) si no se pudo.
    public async Task<(ConfiguracionBase? fila, string? error)> CrearAsync(
        CrearConfigBaseRequest req, CancellationToken ct = default)
    {
        var nombre = (req.Nombre ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nombre))
            return (null, "El nombre de la base es obligatorio.");
        if (await _db.ConfiguracionesBase.AnyAsync(c => c.Nombre == nombre, ct))
            return (null, $"Ya existe una base con el nombre '{nombre}'.");

        var f = new ConfiguracionBase
        {
            Nombre = nombre,
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? nombre : req.Descripcion!.Trim(),
            Activa = req.Activa,
            BaseUrl = (req.BaseUrl ?? "").Trim(),
            RemitoTipo = string.IsNullOrWhiteSpace(req.RemitoTipo) ? "N" : req.RemitoTipo!.Trim(),
            IncluirEnPortal = req.IncluirEnPortal,
            Empresa = req.Empresa,
            Sucursal = req.Sucursal,
            RemitoPrefijo = (req.RemitoPrefijo ?? "").Trim(),
            RemitoConcepto = (req.RemitoConcepto ?? "").Trim(),
            RemitoDeposito = req.RemitoDeposito,
            FacturaPrefijo = (req.FacturaPrefijo ?? "").Trim(),
            FacturaConcepto = (req.FacturaConcepto ?? "").Trim(),
            FacturaDeposito = req.FacturaDeposito,
            FacturaImputacionContable = req.FacturaImputacionContable,
            OrdenCompraPrefijo = string.IsNullOrWhiteSpace(req.OrdenCompraPrefijo) ? "1" : req.OrdenCompraPrefijo!.Trim(),
            SqlServidor = (req.SqlServidor ?? "").Trim(),
            SqlBase = (req.SqlBase ?? "").Trim(),
            SqlUsuario = (req.SqlUsuario ?? "").Trim(),
            SqlClave = (req.SqlClave ?? "").Trim(),
            SqlEmailPropio = (req.SqlEmailPropio ?? "").Trim(),
            BieHabilitado = req.BieHabilitado,
            BieEntorno = string.IsNullOrWhiteSpace(req.BieEntorno) ? "homologacion" : req.BieEntorno!.Trim(),
            BieClientId = (req.BieClientId ?? "").Trim(),
            BieNumeroAdherente = req.BieNumeroAdherente,
            BieCbuDebito = (req.BieCbuDebito ?? "").Trim(),
            BiePemPath = (req.BiePemPath ?? "").Trim()
        };

        _db.ConfiguracionesBase.Add(f);
        await _db.SaveChangesAsync(ct);

        var destino = new DestinoBas();
        AplicarEnMemoria(destino, f);
        _destinos.Definir(nombre, destino);

        return (f, null);
    }

    // Edición de una base existente (incluida la conexión). Si cambió la BaseUrl,
    // invalida el token cacheado de esa base. Devuelve null si no existe.
    public async Task<ConfiguracionBase?> ActualizarAsync(
        string nombre, ActualizarConfigBaseRequest req, CancellationToken ct = default)
    {
        var f = await _db.ConfiguracionesBase.FirstOrDefaultAsync(c => c.Nombre == nombre, ct);
        if (f is null) return null;

        var urlAntes = f.BaseUrl;

        f.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? nombre : req.Descripcion!.Trim();
        f.Activa = req.Activa;
        f.BaseUrl = (req.BaseUrl ?? "").Trim();
        f.RemitoTipo = string.IsNullOrWhiteSpace(req.RemitoTipo) ? "N" : req.RemitoTipo!.Trim();
        f.IncluirEnPortal = req.IncluirEnPortal;
        f.Empresa = req.Empresa;
        f.Sucursal = req.Sucursal;
        f.RemitoPrefijo = (req.RemitoPrefijo ?? "").Trim();
        f.RemitoConcepto = (req.RemitoConcepto ?? "").Trim();
        f.RemitoDeposito = req.RemitoDeposito;
        f.FacturaPrefijo = (req.FacturaPrefijo ?? "").Trim();
        f.FacturaConcepto = (req.FacturaConcepto ?? "").Trim();
        f.FacturaDeposito = req.FacturaDeposito;
        f.FacturaImputacionContable = req.FacturaImputacionContable;
        f.OrdenCompraPrefijo = string.IsNullOrWhiteSpace(req.OrdenCompraPrefijo) ? "1" : req.OrdenCompraPrefijo!.Trim();
        f.SqlServidor = (req.SqlServidor ?? "").Trim();
        f.SqlBase = (req.SqlBase ?? "").Trim();
        f.SqlUsuario = (req.SqlUsuario ?? "").Trim();
        f.SqlEmailPropio = (req.SqlEmailPropio ?? "").Trim();
        // Clave: sólo se actualiza si viene una nueva; vacío = se mantiene la actual.
        if (!string.IsNullOrWhiteSpace(req.SqlClave)) f.SqlClave = req.SqlClave.Trim();
        // Banco Credicoop (echeqs por API), por empresa. Todos los campos son no-secretos
        // (la clave privada vive como archivo en disco; acá sólo su ruta).
        f.BieHabilitado = req.BieHabilitado;
        f.BieEntorno = string.IsNullOrWhiteSpace(req.BieEntorno) ? "homologacion" : req.BieEntorno!.Trim();
        f.BieClientId = (req.BieClientId ?? "").Trim();
        f.BieNumeroAdherente = req.BieNumeroAdherente;
        f.BieCbuDebito = (req.BieCbuDebito ?? "").Trim();
        f.BiePemPath = (req.BiePemPath ?? "").Trim();

        await _db.SaveChangesAsync(ct);

        var destino = _destinos.Config(nombre) ?? new DestinoBas();
        AplicarEnMemoria(destino, f);
        _destinos.Definir(nombre, destino);

        if (!string.Equals(urlAntes, f.BaseUrl, StringComparison.OrdinalIgnoreCase))
            _destinos.InvalidarToken(nombre);

        return f;
    }

    // Cantidad de ingresos del portal por base (para avisar antes de eliminar).
    public async Task<Dictionary<string, int>> ContarIngresosPorBaseAsync(CancellationToken ct = default)
    {
        var filas = await _db.PreRemitos
            .Where(p => p.DestinoBase != null)
            .GroupBy(p => p.DestinoBase!)
            .Select(g => new { Base = g.Key, N = g.Count() })
            .ToListAsync(ct);
        return filas.ToDictionary(x => x.Base, x => x.N, StringComparer.OrdinalIgnoreCase);
    }

    // Baja de una base: saca la fila y la quita de memoria. Si la base tiene
    // ingresos asociados:
    //   - borrarRegistros = false -> se elimina SÓLO la base; los ingresos quedan
    //     (con el destino apuntando a una base que ya no existe, se ven en rojo).
    //   - borrarRegistros = true  -> se borran además todos esos ingresos (y por
    //     cascada sus renglones y percepciones).
    //   - borrarAuditoria = true (sólo si borrarRegistros) -> borra también los
    //     registros de auditoría de esos ingresos. Si es false, la auditoría queda
    //     como log histórico.
    // Devuelve cuántos ingresos tenía la base.
    public async Task<(bool ok, string? error, int ingresos)> EliminarAsync(
        string nombre, bool borrarRegistros, bool borrarAuditoria, CancellationToken ct = default)
    {
        var f = await _db.ConfiguracionesBase.FirstOrDefaultAsync(c => c.Nombre == nombre, ct);
        if (f is null) return (false, $"No existe la base '{nombre}'.", 0);

        var ingresos = await _db.PreRemitos.CountAsync(p => p.DestinoBase == nombre, ct);

        if (ingresos > 0 && borrarRegistros)
        {
            // IDs de los ingresos de la base (para borrar su auditoría si se pidió).
            var ids = await _db.PreRemitos
                .Where(p => p.DestinoBase == nombre)
                .Select(p => p.Id)
                .ToListAsync(ct);

            if (borrarAuditoria && ids.Count > 0)
            {
                var audit = await _db.AuditoriaPreRemitos
                    .Where(a => ids.Contains(a.PreRemitoId))
                    .ToListAsync(ct);
                _db.AuditoriaPreRemitos.RemoveRange(audit);
            }

            var aBorrar = await _db.PreRemitos
                .Include(p => p.Lineas)
                .Include(p => p.PercepcionesIngBr)
                .Where(p => p.DestinoBase == nombre)
                .ToListAsync(ct);
            _db.PreRemitos.RemoveRange(aBorrar);   // cascada: renglones + percepciones
            await _db.SaveChangesAsync(ct);
        }

        _db.ConfiguracionesBase.Remove(f);
        await _db.SaveChangesAsync(ct);

        _destinos.Quitar(nombre);   // saca de memoria + invalida el token

        return (true, null, ingresos);
    }

    private static ConfiguracionBase FilaDesde(string nombre, DestinoBas d) => new()
    {
        Nombre = nombre,
        Descripcion = string.IsNullOrWhiteSpace(d.Descripcion) ? nombre : d.Descripcion,
        Activa = d.Activa,
        BaseUrl = d.BaseUrl,
        RemitoTipo = d.RemitoTipo,
        IncluirEnPortal = d.IncluirEnPortal,
        Empresa = d.Empresa,
        Sucursal = d.Sucursal,
        RemitoPrefijo = d.RemitoPrefijo,
        RemitoConcepto = d.RemitoConcepto,
        RemitoDeposito = d.RemitoDeposito,
        FacturaPrefijo = d.FacturaPrefijo,
        FacturaConcepto = d.FacturaConcepto,
        FacturaDeposito = d.FacturaDeposito,
        FacturaImputacionContable = d.FacturaImputacionContable,
        OrdenCompraPrefijo = string.IsNullOrWhiteSpace(d.OrdenCompraPrefijo) ? "1" : d.OrdenCompraPrefijo
    };

    private static void AplicarEnMemoria(DestinoBas d, ConfiguracionBase f)
    {
        // La tabla manda sobre TODO, incluida la conexión (BaseUrl, RemitoTipo).
        d.Descripcion = f.Descripcion;
        d.Activa = f.Activa;
        d.BaseUrl = f.BaseUrl;
        d.RemitoTipo = string.IsNullOrWhiteSpace(f.RemitoTipo) ? "N" : f.RemitoTipo;
        d.IncluirEnPortal = f.IncluirEnPortal;
        d.Empresa = f.Empresa;
        d.Sucursal = f.Sucursal;
        d.RemitoPrefijo = f.RemitoPrefijo;
        d.RemitoConcepto = f.RemitoConcepto;
        d.RemitoDeposito = f.RemitoDeposito;
        d.FacturaPrefijo = f.FacturaPrefijo;
        d.FacturaConcepto = f.FacturaConcepto;
        d.FacturaDeposito = f.FacturaDeposito;
        d.FacturaImputacionContable = f.FacturaImputacionContable;
        d.OrdenCompraPrefijo = string.IsNullOrWhiteSpace(f.OrdenCompraPrefijo) ? "1" : f.OrdenCompraPrefijo;
    }
}

// Request de alta de una base nueva (conexión + negocio).
public record CrearConfigBaseRequest(
    string Nombre,
    string? Descripcion,
    bool Activa,
    string BaseUrl,
    string? RemitoTipo,
    bool IncluirEnPortal,
    int Empresa,
    int Sucursal,
    string? RemitoPrefijo,
    string? RemitoConcepto,
    int RemitoDeposito,
    string? FacturaPrefijo,
    string? FacturaConcepto,
    int FacturaDeposito,
    long FacturaImputacionContable,
    string? OrdenCompraPrefijo = null,
    // SQL directo (e-cheques): conexión + credencial read-only + mail propio a excluir.
    string? SqlServidor = null,
    string? SqlBase = null,
    string? SqlUsuario = null,
    string? SqlClave = null,
    string? SqlEmailPropio = null,
    // Emisión de echeqs por API del Banco Credicoop, por empresa.
    bool BieHabilitado = false,
    string? BieEntorno = null,
    string? BieClientId = null,
    long BieNumeroAdherente = 0,
    string? BieCbuDebito = null,
    string? BiePemPath = null);

// Request de edición de la config de una base (todos los campos editables, incl. conexión).
public record ActualizarConfigBaseRequest(
    string? Descripcion,
    bool Activa,
    string? BaseUrl,
    string? RemitoTipo,
    bool IncluirEnPortal,
    int Empresa,
    int Sucursal,
    string? RemitoPrefijo,
    string? RemitoConcepto,
    int RemitoDeposito,
    string? FacturaPrefijo,
    string? FacturaConcepto,
    int FacturaDeposito,
    long FacturaImputacionContable,
    string? OrdenCompraPrefijo = null,
    // SQL directo (e-cheques). SqlClave vacío en edición = NO cambiar la existente.
    string? SqlServidor = null,
    string? SqlBase = null,
    string? SqlUsuario = null,
    string? SqlClave = null,
    string? SqlEmailPropio = null,
    // Emisión de echeqs por API del Banco Credicoop, por empresa.
    bool BieHabilitado = false,
    string? BieEntorno = null,
    string? BieClientId = null,
    long BieNumeroAdherente = 0,
    string? BieCbuDebito = null,
    string? BiePemPath = null);
