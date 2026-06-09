using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Auth;
using PortalClientes.Bas;
using PortalClientes.Data;
using PortalClientes.Models;
using PortalClientes.Remitos;

namespace PortalClientes.Controllers;

[ApiController]
[Route("api/pre-remitos")]
[Authorize(Policy = "VerRemitos")]   // requiere editar_remitos O conformar_remitos
public class PreRemitosController : ControllerBase
{
    private readonly PortalDbContext _db;
    private readonly BasResolucionService _resolucion;
    private readonly BasCacheMaestros _cache;
    private readonly BasCacheRefresher _refresher;
    private readonly BasDestinosService _destinos;

    public PreRemitosController(
        PortalDbContext db,
        BasResolucionService resolucion,
        BasCacheMaestros cache,
        BasCacheRefresher refresher,
        BasDestinosService destinos)
    {
        _db = db;
        _resolucion = resolucion;
        _cache = cache;
        _refresher = refresher;
        _destinos = destinos;
    }

    private string Usuario => User.FindFirstValue("identificador") ?? "";

    // GET /api/pre-remitos?estado=Borrador|Conformado|Enviado|todos
    [HttpGet]
    public async Task<ActionResult> Listar([FromQuery] string estado = "todos")
    {
        IQueryable<PreRemito> q = _db.PreRemitos.Include(p => p.Lineas);

        if (!string.Equals(estado, "todos", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<EstadoPreRemito>(estado, true, out var e))
        {
            q = q.Where(p => p.Estado == e);
        }

        var lista = await q.OrderByDescending(p => p.Id).ToListAsync();
        return Ok(lista.Select(RemitoMapeo.AItem));
    }

    // GET /api/pre-remitos/resolver-producto?codigo=XXX
    [HttpGet("resolver-producto")]
    public async Task<ActionResult> ResolverProducto([FromQuery] string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return BadRequest(new { mensaje = "Falta el código de producto." });
        var bases = await _resolucion.ResolverProductoAsync(codigo);
        return Ok(new { codigo = codigo.Trim(), bases });
    }

    // GET /api/pre-remitos/resolver-proveedor?codigo=XXX
    [HttpGet("resolver-proveedor")]
    public async Task<ActionResult> ResolverProveedor([FromQuery] string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return BadRequest(new { mensaje = "Falta el código de proveedor." });
        var bases = await _resolucion.ResolverProveedorAsync(codigo);
        return Ok(new { codigo = codigo.Trim(), bases });
    }

    // GET /api/pre-remitos/buscar-productos?q=&offset=&limit=
    [HttpGet("buscar-productos")]
    public ActionResult BuscarProductos([FromQuery] string q = "", [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Ok(BuscarEnCache(
            s => s.Bienes.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value.Descripcion)),
            q, offset, limit));

    // GET /api/pre-remitos/buscar-proveedores?q=&offset=&limit=
    [HttpGet("buscar-proveedores")]
    public ActionResult BuscarProveedores([FromQuery] string q = "", [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Ok(BuscarEnCache(
            s => s.Proveedores.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)),
            q, offset, limit));

    private object BuscarEnCache(
        Func<SnapshotMaestro, IEnumerable<KeyValuePair<string, string?>>> extraer,
        string q, int offset, int limit)
    {
        var texto = (q ?? "").Trim();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);
        var nombres = _destinos.Nombres.ToList();

        var acum = new Dictionary<string, Acumulado>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in nombres)
        {
            var snap = _cache.Obtener(b);
            foreach (var kv in extraer(snap))
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (!acum.TryGetValue(kv.Key, out var a)) { a = new Acumulado(); acum[kv.Key] = a; }
                if (string.IsNullOrEmpty(a.Descripcion) && !string.IsNullOrEmpty(kv.Value)) a.Descripcion = kv.Value!;
                a.Bases.Add(b);
            }
        }

        IEnumerable<KeyValuePair<string, Acumulado>> filtrados = acum;
        if (texto.Length > 0)
            filtrados = filtrados.Where(p =>
                p.Key.Contains(texto, StringComparison.OrdinalIgnoreCase)
                || p.Value.Descripcion.Contains(texto, StringComparison.OrdinalIgnoreCase));

        var ordenados = filtrados.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var total = ordenados.Count;
        var pagina = ordenados.Skip(offset).Take(limit).Select(p => new
        {
            codigo = p.Key,
            descripcion = p.Value.Descripcion,
            bases = nombres.Select(n => new { @base = n, existe = p.Value.Bases.Contains(n) })
        }).ToList();

        return new { total, offset, mostrando = pagina.Count, hayMas = offset + pagina.Count < total, items = pagina };
    }

    // GET /api/pre-remitos/estado-cache  -> estado del padrón en memoria por base
    [HttpGet("estado-cache")]
    public ActionResult EstadoCache()
    {
        var bases = _destinos.Nombres.Select(nombre =>
        {
            var s = _cache.Obtener(nombre);
            return new
            {
                @base = nombre,
                listo = s.BienesListo && s.ProveedoresListo,
                bienesListo = s.BienesListo,
                proveedoresListo = s.ProveedoresListo,
                bienes = s.Bienes.Count,
                proveedores = s.Proveedores.Count,
                actualizado = s.Actualizado,
                error = s.Error
            };
        });
        return Ok(new { bases });
    }

    // POST /api/pre-remitos/refrescar-cache  -> recarga el padrón (en segundo plano)
    [HttpPost("refrescar-cache")]
    public ActionResult RefrescarCache()
    {
        _ = Task.Run(() => _refresher.RefrescarTodoAsync(CancellationToken.None, soloVencidos: false));
        return EstadoCache();
    }

    // GET /api/pre-remitos/diag-padron  -> diagnóstico: qué responde cada base
    [HttpGet("diag-padron")]
    public async Task<ActionResult> DiagPadron()
    {
        var resultado = new List<object>();
        var maestros = new[] { ("/api/Bienes", "bienes"), ("/api/Proveedores", "proveedores") };

        foreach (var nombre in _destinos.Nombres)
        {
            foreach (var (ruta, etiqueta) in maestros)
            {
                try
                {
                    var json = await _destinos.GetAsync(nombre, $"{ruta}?pageSize=5&pageNumber=1", HttpContext.RequestAborted);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        resultado.Add(new { @base = nombre, maestro = etiqueta, ok = true, vacio = true });
                        continue;
                    }

                    using var doc = JsonDocument.Parse(json);
                    var kind = doc.RootElement.ValueKind.ToString();
                    int count = doc.RootElement.ValueKind == JsonValueKind.Array
                        ? doc.RootElement.GetArrayLength() : -1;
                    var muestra = json.Length > 1200 ? json.Substring(0, 1200) : json;

                    resultado.Add(new { @base = nombre, maestro = etiqueta, ok = true, kind, count, muestra });
                }
                catch (Exception ex)
                {
                    resultado.Add(new { @base = nombre, maestro = etiqueta, ok = false, error = ex.Message });
                }
            }
        }

        return Ok(resultado);
    }

    // GET /api/pre-remitos/{id}
    [HttpGet("{id:int}")]
    public async Task<ActionResult> Obtener(int id)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el pre-remito." });
        return Ok(RemitoMapeo.ADto(p));
    }

    // POST /api/pre-remitos  -> crea en estado Borrador
    [HttpPost]
    [Authorize(Policy = Permisos.EditarRemitos)]
    public async Task<ActionResult> Crear([FromBody] CrearPreRemitoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProveedorCodigo))
            return BadRequest(new { mensaje = "Falta el proveedor." });

        var p = new PreRemito
        {
            ProveedorCodigo = req.ProveedorCodigo.Trim(),
            ProveedorRazonSocial = req.ProveedorRazonSocial,
            Fecha = req.Fecha ?? DateTime.Today,
            ComprobantePrefijo = Limpiar(req.ComprobantePrefijo),
            ComprobanteNumero = req.ComprobanteNumero,
            ComprobanteFecha = req.ComprobanteFecha,
            Observaciones = req.Observaciones,
            Estado = EstadoPreRemito.Borrador,
            CreadoPor = Usuario,
            CreadoEn = DateTime.Now,
            Lineas = MapearLineas(req.Lineas)
        };

        _db.PreRemitos.Add(p);
        await _db.SaveChangesAsync();              // genera el Id

        AgregarAuditoria(p, EventosAuditoria.Alta);
        await _db.SaveChangesAsync();

        return Ok(RemitoMapeo.ADto(p));
    }

    // PUT /api/pre-remitos/{id}  -> edita (solo en Borrador)
    [HttpPut("{id:int}")]
    [Authorize(Policy = Permisos.EditarRemitos)]
    public async Task<ActionResult> Modificar(int id, [FromBody] ModificarPreRemitoRequest req)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el pre-remito." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede editar un pre-remito en borrador." });
        if (string.IsNullOrWhiteSpace(req.ProveedorCodigo))
            return BadRequest(new { mensaje = "Falta el proveedor." });

        p.ProveedorCodigo = req.ProveedorCodigo.Trim();
        p.ProveedorRazonSocial = req.ProveedorRazonSocial;
        p.Fecha = req.Fecha ?? p.Fecha;
        p.ComprobantePrefijo = Limpiar(req.ComprobantePrefijo);
        p.ComprobanteNumero = req.ComprobanteNumero;
        p.ComprobanteFecha = req.ComprobanteFecha;
        p.Observaciones = req.Observaciones;
        p.ModificadoPor = Usuario;
        p.ModificadoEn = DateTime.Now;

        // Reemplazamos los renglones.
        _db.PreRemitoLineas.RemoveRange(p.Lineas);
        p.Lineas = MapearLineas(req.Lineas);

        AgregarAuditoria(p, EventosAuditoria.Modificacion);
        return await GuardarConConcurrencia(p, req.RowVersion);
    }

    // DELETE /api/pre-remitos/{id}  -> elimina (solo en Borrador)
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permisos.EditarRemitos)]
    public async Task<ActionResult> Eliminar(int id)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el pre-remito." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede eliminar un pre-remito en borrador." });

        // Registramos antes de borrar (el log sobrevive a la eliminación).
        AgregarAuditoria(p, EventosAuditoria.Eliminacion);
        _db.PreRemitos.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { mensaje = "Pre-remito eliminado." });
    }

    // POST /api/pre-remitos/{id}/conformar  -> Borrador -> Conformado
    [HttpPost("{id:int}/conformar")]
    [Authorize(Policy = Permisos.ConformarRemitos)]
    public async Task<ActionResult> Conformar(int id, [FromBody] AccionRemitoRequest req)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el pre-remito." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede conformar un pre-remito en borrador." });
        if (string.IsNullOrWhiteSpace(p.ProveedorCodigo) || p.Lineas.Count == 0)
            return BadRequest(new { mensaje = "El pre-remito necesita un proveedor y al menos un renglón." });

        p.Estado = EstadoPreRemito.Conformado;
        p.ConformadoPor = Usuario;
        p.ConformadoEn = DateTime.Now;
        p.MensajeError = null;

        AgregarAuditoria(p, EventosAuditoria.Conformado);
        return await GuardarConConcurrencia(p, req.RowVersion);
    }

    // POST /api/pre-remitos/{id}/reabrir  -> Conformado -> Borrador
    [HttpPost("{id:int}/reabrir")]
    [Authorize(Policy = Permisos.ConformarRemitos)]
    public async Task<ActionResult> Reabrir(int id, [FromBody] AccionRemitoRequest req)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el pre-remito." });
        if (p.Estado != EstadoPreRemito.Conformado)
            return Conflict(new { mensaje = "Solo se puede reabrir un pre-remito conformado." });

        p.Estado = EstadoPreRemito.Borrador;
        p.ConformadoPor = null;
        p.ConformadoEn = null;

        AgregarAuditoria(p, EventosAuditoria.Reabierto);
        return await GuardarConConcurrencia(p, req.RowVersion);
    }

    // POST /api/pre-remitos/{id}/grabar  -> Conformado -> Enviado (graba en BAS)
    // PENDIENTE: validar códigos en la base elegida + crear el remito + registrar
    // la auditoría del evento "Grabado".
    [HttpPost("{id:int}/grabar")]
    [Authorize(Policy = Permisos.ConformarRemitos)]
    public async Task<ActionResult> Grabar(int id, [FromBody] GrabarRequest req)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el pre-remito." });
        if (p.Estado != EstadoPreRemito.Conformado)
            return Conflict(new { mensaje = "Solo se puede grabar un pre-remito conformado." });

        return StatusCode(501, new { mensaje = "El grabado en BAS todavía no está implementado." });
    }

    // ---- Helpers ----
    private static string? Limpiar(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Agrega un renglón de auditoría al contexto (se persiste con el SaveChanges
    // de la operación). No guarda por sí solo.
    private void AgregarAuditoria(PreRemito p, string evento, string? detalle = null)
    {
        _db.AuditoriaPreRemitos.Add(new AuditoriaPreRemito
        {
            PreRemitoId = p.Id,
            Evento = evento,
            Usuario = Usuario,
            FechaHora = DateTime.Now,
            ProveedorCodigo = p.ProveedorCodigo,
            ProveedorRazonSocial = p.ProveedorRazonSocial,
            ComprobanteFecha = p.ComprobanteFecha,
            ComprobantePrefijo = p.ComprobantePrefijo,
            ComprobanteNumero = p.ComprobanteNumero,
            Estado = p.Estado.ToString(),
            Detalle = detalle
        });
    }

    private static List<PreRemitoLinea> MapearLineas(List<LineaRequest>? lineas)
    {
        var resultado = new List<PreRemitoLinea>();
        if (lineas is null) return resultado;
        var orden = 0;
        foreach (var l in lineas)
        {
            if (string.IsNullOrWhiteSpace(l.ProductoCodigo)) continue;
            resultado.Add(new PreRemitoLinea
            {
                ProductoCodigo = l.ProductoCodigo.Trim(),
                Descripcion = l.Descripcion,
                Cantidad = l.Cantidad,
                Unidad = Limpiar(l.Unidad),
                Observacion = l.Observacion,
                Partida = Limpiar(l.Partida),
                Series = Limpiar(l.Series),
                Orden = orden++
            });
        }
        return resultado;
    }

    private async Task<ActionResult> GuardarConConcurrencia(PreRemito p, Guid rowVersionCliente)
    {
        _db.Entry(p).Property(x => x.RowVersion).OriginalValue = rowVersionCliente;
        p.RowVersion = Guid.NewGuid();

        try
        {
            await _db.SaveChangesAsync();
            return Ok(RemitoMapeo.ADto(p));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { mensaje = "Otro usuario modificó este pre-remito. Recargá para ver los cambios." });
        }
    }

    private sealed class Acumulado
    {
        public string Descripcion = "";
        public HashSet<string> Bases = new(StringComparer.OrdinalIgnoreCase);
    }
}
