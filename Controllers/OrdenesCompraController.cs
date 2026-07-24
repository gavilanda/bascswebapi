using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Auth;
using PortalClientes.Bas;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Controllers;

// Órdenes de compra (intranet). Borrador editable → Grabar en BAS (POST /api/OrdenesCompra).
// Al grabar se captura el Nrotrans/número que asigna BAS (referencia futura de remitos/facturas).
// Gateada por el permiso "ordenes_compra".
[ApiController]
[Route("api/ordenes-compra")]
[Authorize(Policy = Permisos.OrdenesCompra)]
public class OrdenesCompraController : ControllerBase
{
    private readonly PortalDbContext _db;
    private readonly BasOrdenCompraService _oc;
    private readonly BasDestinosService _destinos;
    private readonly BasCacheMaestros _cache;

    public OrdenesCompraController(
        PortalDbContext db, BasOrdenCompraService oc, BasDestinosService destinos, BasCacheMaestros cache)
    {
        _db = db;
        _oc = oc;
        _destinos = destinos;
        _cache = cache;
    }

    private string Usuario => User.FindFirstValue("identificador") ?? "";
    private bool DestinoActivo(string? d) => !string.IsNullOrWhiteSpace(d) && (_destinos.Config(d!)?.Activa ?? false);

    // GET /api/ordenes-compra?estado=  -> lista (cabeceras).
    [HttpGet]
    public async Task<ActionResult> Listar([FromQuery] string? estado)
    {
        var q = _db.OrdenesCompra.AsNoTracking().AsQueryable();
        if (estado == "borrador") q = q.Where(o => o.Estado == EstadoOrdenCompra.Borrador);
        else if (estado == "grabada") q = q.Where(o => o.Estado == EstadoOrdenCompra.Grabada);
        var ordenes = (await q.OrderByDescending(o => o.Id).ToListAsync(HttpContext.RequestAborted))
            .Select(o => new
            {
                o.Id, o.ProveedorCodigo, o.ProveedorRazonSocial, fecha = o.Fecha.ToString("yyyy-MM-dd"),
                estado = o.Estado.ToString(), o.DestinoBase, o.NrotransBas, o.PrefijoBas, o.NumeroBas
            });
        return Ok(new { ordenes });
    }

    // GET /api/ordenes-compra/{id}  -> cabecera + renglones.
    [HttpGet("{id:int}")]
    public async Task<ActionResult> Ver(int id)
    {
        var o = await _db.OrdenesCompra.AsNoTracking().Include(x => x.Lineas)
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (o is null) return NotFound(new { mensaje = "No existe la orden de compra." });
        return Ok(Proyectar(o));
    }

    // POST /api/ordenes-compra  -> crea un borrador.
    [HttpPost]
    public async Task<ActionResult> Crear([FromBody] GuardarOrdenCompraDto req)
    {
        var err = Validar(req);
        if (err is not null) return BadRequest(new { mensaje = err });

        var o = new OrdenCompra { Estado = EstadoOrdenCompra.Borrador, CreadoPor = Usuario, CreadoEn = DateTime.Now };
        Aplicar(o, req);
        _db.OrdenesCompra.Add(o);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { mensaje = "Orden de compra creada.", o.Id });
    }

    // PUT /api/ordenes-compra/{id}  -> edita un borrador.
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Actualizar(int id, [FromBody] GuardarOrdenCompraDto req)
    {
        var err = Validar(req);
        if (err is not null) return BadRequest(new { mensaje = err });

        var o = await _db.OrdenesCompra.Include(x => x.Lineas)
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (o is null) return NotFound(new { mensaje = "No existe la orden de compra." });
        if (o.Estado != EstadoOrdenCompra.Borrador)
            return BadRequest(new { mensaje = "La orden ya fue grabada; no se puede editar." });
        if (req.RowVersion is not null && req.RowVersion != o.RowVersion)
            return Conflict(new { mensaje = "La orden fue modificada por otro usuario. Recargá antes de guardar." });

        _db.OrdenCompraLineas.RemoveRange(o.Lineas);
        o.Lineas.Clear();
        Aplicar(o, req);
        o.ModificadoPor = Usuario; o.ModificadoEn = DateTime.Now; o.RowVersion = Guid.NewGuid();
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { mensaje = "Orden de compra guardada.", o.Id });
    }

    // DELETE /api/ordenes-compra/{id}  -> borra un borrador.
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Eliminar(int id)
    {
        var o = await _db.OrdenesCompra.FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (o is null) return NotFound(new { mensaje = "No existe la orden de compra." });
        if (o.Estado != EstadoOrdenCompra.Borrador)
            return BadRequest(new { mensaje = "La orden ya fue grabada; no se puede borrar." });
        _db.OrdenesCompra.Remove(o);
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        return Ok(new { mensaje = "Orden de compra eliminada." });
    }

    // POST /api/ordenes-compra/{id}/grabar  -> graba en BAS y captura Nrotrans/número.
    [HttpPost("{id:int}/grabar")]
    public async Task<ActionResult> Grabar(int id)
    {
        var o = await _db.OrdenesCompra.Include(x => x.Lineas)
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (o is null) return NotFound(new { mensaje = "No existe la orden de compra." });
        if (o.Estado == EstadoOrdenCompra.Grabada)
            return BadRequest(new { mensaje = "La orden ya está grabada." });
        if (!DestinoActivo(o.DestinoBase))
            return BadRequest(new { mensaje = "La base de destino no está activa." });
        if (o.Lineas.Count == 0)
            return BadRequest(new { mensaje = "La orden no tiene renglones." });

        var renglones = o.Lineas
            .Select(l => new BasOrdenCompraService.RenglonOC(l.ProductoCodigo, l.Cantidad, l.PrecioUnitario, l.TasaIva))
            .ToList();

        var res = await _oc.GrabarAsync(
            o.DestinoBase!, o.Fecha, o.FechaExpiracion, o.CodigoMoneda, o.ProveedorCodigo, o.Comprador,
            o.CondicionCompra, o.Observaciones, o.ObservacionEntrega, renglones, Usuario, HttpContext.RequestAborted);

        if (!res.Ok)
        {
            o.MensajeError = res.Error;
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            return StatusCode(502, new { mensaje = "No se pudo grabar en BAS: " + (res.Error ?? "error desconocido") });
        }

        // Capturamos el número interno de BAS (referencia futura).
        o.NrotransBas = long.TryParse(res.IdTransaccion, out var n) ? n : null;
        o.PrefijoBas = res.Prefijo;
        o.NumeroBas = res.Numero;
        o.MensajeError = null;
        o.Estado = EstadoOrdenCompra.Grabada;
        o.GrabadoPor = Usuario; o.GrabadoEn = DateTime.Now; o.RowVersion = Guid.NewGuid();
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        return Ok(new { mensaje = "Orden de compra grabada en BAS.", nrotrans = o.NrotransBas, prefijo = o.PrefijoBas, numero = o.NumeroBas });
    }

    // GET /api/ordenes-compra/proveedor?base=&codigo=  -> razón social + comprador + condición.
    [HttpGet("proveedor")]
    public async Task<ActionResult> Proveedor([FromQuery] string? @base, [FromQuery] string? codigo)
    {
        var b = (@base ?? "").Trim(); var c = (codigo ?? "").Trim();
        if (b.Length == 0 || c.Length == 0) return BadRequest(new { mensaje = "Faltan base y código de proveedor." });
        if (!DestinoActivo(b)) return BadRequest(new { mensaje = "Base inválida o inactiva." });
        try
        {
            var p = await _oc.TraerProveedorAsync(b, c, HttpContext.RequestAborted);
            if (p is null) return NotFound(new { mensaje = "No se encontró el proveedor en esa base." });
            return Ok(new { codigo = c, razonSocial = p.Value.razonSocial, comprador = p.Value.comprador, condicionCompra = p.Value.condicionCompra });
        }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo consultar el proveedor: " + ex.Message }); }
    }

    // GET /api/ordenes-compra/buscar-proveedores?base=&q=&limit=  (padrón en memoria)
    [HttpGet("buscar-proveedores")]
    public ActionResult BuscarProveedores([FromQuery] string? @base, [FromQuery] string q = "", [FromQuery] int limit = 40)
        => Ok(new { items = BuscarPadron(@base, true, q, limit) });

    // GET /api/ordenes-compra/buscar-productos?base=&q=&limit=
    [HttpGet("buscar-productos")]
    public ActionResult BuscarProductos([FromQuery] string? @base, [FromQuery] string q = "", [FromQuery] int limit = 40)
        => Ok(new { items = BuscarPadron(@base, false, q, limit) });

    // GET /api/ordenes-compra/destinos  -> bases activas del portal (para el selector).
    [HttpGet("destinos")]
    public ActionResult Destinos()
        => Ok(new { destinos = _destinos.Nombres.Where(n => _destinos.Config(n)?.Activa ?? false).OrderBy(n => n).ToList() });

    // ---- helpers ----
    private object BuscarPadron(string? @base, bool proveedores, string q, int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        var b = (@base ?? "").Trim();
        if (b.Length == 0 || _destinos.Config(b) is null) return Array.Empty<object>();
        var snap = _cache.Obtener(b);
        var texto = (q ?? "").Trim();
        IEnumerable<KeyValuePair<string, string?>> fuente = proveedores
            ? snap.Proveedores.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value))
            : snap.Bienes.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value.Descripcion));
        if (texto.Length > 0)
            fuente = fuente.Where(kv => kv.Key.Contains(texto, StringComparison.OrdinalIgnoreCase)
                || (kv.Value ?? "").Contains(texto, StringComparison.OrdinalIgnoreCase));
        return fuente.OrderBy(kv => kv.Key).Take(limit)
            .Select(kv => new { codigo = kv.Key, descripcion = kv.Value }).ToList();
    }

    private static string? Validar(GuardarOrdenCompraDto req)
    {
        if (string.IsNullOrWhiteSpace(req.DestinoBase)) return "Elegí la base.";
        if (string.IsNullOrWhiteSpace(req.ProveedorCodigo)) return "Elegí un proveedor.";
        if (req.Renglones is null || req.Renglones.Count == 0) return "Cargá al menos un renglón.";
        foreach (var r in req.Renglones)
        {
            if (string.IsNullOrWhiteSpace(r.ProductoCodigo)) return "Hay un renglón sin producto.";
            if (r.Cantidad <= 0) return "Las cantidades deben ser mayores a cero.";
            if (r.PrecioUnitario < 0) return "El precio no puede ser negativo.";
        }
        return null;
    }

    private static void Aplicar(OrdenCompra o, GuardarOrdenCompraDto req)
    {
        o.DestinoBase = req.DestinoBase.Trim();
        o.ProveedorCodigo = req.ProveedorCodigo.Trim();
        o.ProveedorRazonSocial = req.ProveedorRazonSocial?.Trim();
        o.Comprador = req.Comprador?.Trim();
        o.CondicionCompra = req.CondicionCompra?.Trim();
        o.Fecha = req.Fecha == default ? DateTime.Today : req.Fecha.Date;
        o.FechaExpiracion = req.FechaExpiracion?.Date;
        o.CodigoMoneda = req.CodigoMoneda <= 0 ? 1 : req.CodigoMoneda;
        o.Observaciones = req.Observaciones?.Trim();
        o.ObservacionEntrega = req.ObservacionEntrega?.Trim();
        o.Lineas = req.Renglones.Select(r => new OrdenCompraLinea
        {
            ProductoCodigo = r.ProductoCodigo.Trim(),
            Descripcion = r.Descripcion?.Trim(),
            Cantidad = r.Cantidad,
            Unidad = r.Unidad?.Trim(),
            PrecioUnitario = r.PrecioUnitario,
            TasaIva = r.TasaIva,
            Observacion = r.Observacion?.Trim()
        }).ToList();
    }

    private static object Proyectar(OrdenCompra o) => new
    {
        o.Id, o.DestinoBase, o.ProveedorCodigo, o.ProveedorRazonSocial, o.Comprador, o.CondicionCompra,
        fecha = o.Fecha.ToString("yyyy-MM-dd"),
        fechaExpiracion = o.FechaExpiracion?.ToString("yyyy-MM-dd"),
        o.CodigoMoneda, o.Observaciones, o.ObservacionEntrega,
        estado = o.Estado.ToString(), o.NrotransBas, o.PrefijoBas, o.NumeroBas, o.MensajeError, o.RowVersion,
        renglones = o.Lineas.Select(l => new
        {
            l.ProductoCodigo, l.Descripcion, l.Cantidad, l.Unidad, l.PrecioUnitario, l.TasaIva, l.Observacion
        })
    };
}

public record RenglonOCDto(
    string ProductoCodigo, string? Descripcion, decimal Cantidad, string? Unidad,
    decimal PrecioUnitario, decimal TasaIva, string? Observacion);

public record GuardarOrdenCompraDto(
    string DestinoBase, string ProveedorCodigo, string? ProveedorRazonSocial, string? Comprador,
    string? CondicionCompra, DateTime Fecha, DateTime? FechaExpiracion, int CodigoMoneda,
    string? Observaciones, string? ObservacionEntrega, List<RenglonOCDto> Renglones, Guid? RowVersion);
