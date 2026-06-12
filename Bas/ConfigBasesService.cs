using Microsoft.EntityFrameworkCore;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Bas;

// Lee y escribe la configuración por base (tabla ConfiguracionesBase) y la
// mantiene sincronizada con el diccionario de DestinoBas en memoria, que es lo
// que el grabado de ingresos consume. Scoped: usa el DbContext del request.
//
// Reglas de sincronización:
//   - La tabla manda sobre los parámetros de negocio (empresa, sucursal, prefijos,
//     concepto, depósito, descripción, activa).
//   - La CONEXIÓN (BaseUrl) y el RemitoTipo NO se tocan: quedan como vinieron de
//     appsettings.
//   - Sólo se sincronizan bases que existen en memoria (las conectadas vía
//     appsettings); una fila de una base no conectada se ignora al sincronizar.
public class ConfigBasesService
{
    private readonly PortalDbContext _db;
    private readonly Dictionary<string, DestinoBas> _destinos;

    public ConfigBasesService(PortalDbContext db, Dictionary<string, DestinoBas> destinos)
    {
        _db = db;
        _destinos = destinos;
    }

    // Crea filas para las bases de appsettings que todavía no estén en la tabla,
    // tomando como punto de partida los valores actuales del DestinoBas.
    public async Task SembrarFaltantesAsync(CancellationToken ct = default)
    {
        var existentes = await _db.ConfiguracionesBase
            .Select(c => c.Nombre)
            .ToListAsync(ct);
        var set = new HashSet<string>(existentes, StringComparer.OrdinalIgnoreCase);

        var nuevas = new List<ConfiguracionBase>();
        foreach (var (nombre, d) in _destinos)
        {
            if (set.Contains(nombre)) continue;
            nuevas.Add(new ConfiguracionBase
            {
                Nombre = nombre,
                Descripcion = string.IsNullOrWhiteSpace(d.Descripcion) ? nombre : d.Descripcion,
                Activa = d.Activa,
                Empresa = d.Empresa,
                Sucursal = d.Sucursal,
                RemitoPrefijo = d.RemitoPrefijo,
                RemitoConcepto = d.RemitoConcepto,
                RemitoDeposito = d.RemitoDeposito,
                FacturaPrefijo = d.FacturaPrefijo,
                FacturaConcepto = d.FacturaConcepto,
                FacturaDeposito = d.FacturaDeposito
            });
        }

        if (nuevas.Count > 0)
        {
            _db.ConfiguracionesBase.AddRange(nuevas);
            await _db.SaveChangesAsync(ct);
        }
    }

    // Aplica la tabla sobre el diccionario en memoria. Se llama al arranque (y
    // tras cada edición, vía ActualizarAsync).
    public async Task SincronizarMemoriaAsync(CancellationToken ct = default)
    {
        var filas = await _db.ConfiguracionesBase.ToListAsync(ct);
        foreach (var f in filas)
        {
            if (_destinos.TryGetValue(f.Nombre, out var d))
                AplicarEnMemoria(d, f);
        }
    }

    public async Task<List<ConfiguracionBase>> ListarAsync(CancellationToken ct = default)
        => await _db.ConfiguracionesBase.OrderBy(c => c.Nombre).ToListAsync(ct);

    // Datos de conexión (sólo lectura) para mostrar junto a la config editable.
    public DestinoBas? Memoria(string nombre)
        => _destinos.TryGetValue(nombre, out var d) ? d : null;

    // Actualiza una base existente y refleja el cambio en memoria. Devuelve null
    // si no hay fila para ese nombre.
    public async Task<ConfiguracionBase?> ActualizarAsync(
        string nombre, ActualizarConfigBaseRequest req, CancellationToken ct = default)
    {
        var f = await _db.ConfiguracionesBase.FirstOrDefaultAsync(c => c.Nombre == nombre, ct);
        if (f is null) return null;

        f.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? nombre : req.Descripcion!.Trim();
        f.Activa = req.Activa;
        f.Empresa = req.Empresa;
        f.Sucursal = req.Sucursal;
        f.RemitoPrefijo = (req.RemitoPrefijo ?? "").Trim();
        f.RemitoConcepto = (req.RemitoConcepto ?? "").Trim();
        f.RemitoDeposito = req.RemitoDeposito;
        f.FacturaPrefijo = (req.FacturaPrefijo ?? "").Trim();
        f.FacturaConcepto = (req.FacturaConcepto ?? "").Trim();
        f.FacturaDeposito = req.FacturaDeposito;

        await _db.SaveChangesAsync(ct);

        if (_destinos.TryGetValue(nombre, out var d))
            AplicarEnMemoria(d, f);

        return f;
    }

    private static void AplicarEnMemoria(DestinoBas d, ConfiguracionBase f)
    {
        // OJO: NO se tocan d.BaseUrl ni d.RemitoTipo (quedan de appsettings).
        d.Descripcion = f.Descripcion;
        d.Activa = f.Activa;
        d.Empresa = f.Empresa;
        d.Sucursal = f.Sucursal;
        d.RemitoPrefijo = f.RemitoPrefijo;
        d.RemitoConcepto = f.RemitoConcepto;
        d.RemitoDeposito = f.RemitoDeposito;
        d.FacturaPrefijo = f.FacturaPrefijo;
        d.FacturaConcepto = f.FacturaConcepto;
        d.FacturaDeposito = f.FacturaDeposito;
    }
}

// Request de edición de la config de una base (todos los campos editables).
public record ActualizarConfigBaseRequest(
    string? Descripcion,
    bool Activa,
    int Empresa,
    int Sucursal,
    string? RemitoPrefijo,
    string? RemitoConcepto,
    int RemitoDeposito,
    string? FacturaPrefijo,
    string? FacturaConcepto,
    int FacturaDeposito);
