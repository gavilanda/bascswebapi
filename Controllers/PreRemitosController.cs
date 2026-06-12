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
    private readonly BasRemitoIngresoService _grabador;
    private readonly BasFacturaIngresoService _grabadorFactura;

    public PreRemitosController(
        PortalDbContext db,
        BasResolucionService resolucion,
        BasCacheMaestros cache,
        BasCacheRefresher refresher,
        BasDestinosService destinos,
        BasRemitoIngresoService grabador,
        BasFacturaIngresoService grabadorFactura)
    {
        _db = db;
        _resolucion = resolucion;
        _cache = cache;
        _refresher = refresher;
        _destinos = destinos;
        _grabador = grabador;
        _grabadorFactura = grabadorFactura;
    }

    private string Usuario => User.FindFirstValue("identificador") ?? "";

    // GET /api/pre-remitos?estado=&desde=&hasta=&destino=&tipo=
    //   estado : Borrador|Conformado|Enviado|todos
    //   desde  : fecha de ingreso mínima (yyyy-MM-dd), inclusive
    //   hasta  : fecha de ingreso máxima (yyyy-MM-dd), inclusive
    //   destino: base de grabación (p.ej. BARK / PRUEBAB) | todos
    //   tipo   : Remito|Factura|todos
    [HttpGet]
    public async Task<ActionResult> Listar(
        [FromQuery] string estado = "todos",
        [FromQuery] string? desde = null,
        [FromQuery] string? hasta = null,
        [FromQuery] string destino = "todos",
        [FromQuery] string tipo = "todos")
    {
        IQueryable<PreRemito> q = _db.PreRemitos.Include(p => p.Lineas);

        if (!string.Equals(estado, "todos", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<EstadoPreRemito>(estado, true, out var e))
        {
            q = q.Where(p => p.Estado == e);
        }

        // Filtro por tipo de comprobante (Remito / Factura).
        if (!string.Equals(tipo, "todos", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<TipoComprobante>(tipo, true, out var t))
        {
            q = q.Where(p => p.TipoComprobante == t);
        }

        // Filtro por fecha de ingreso (p.Fecha). "hasta" es inclusive: tomamos
        // hasta el final de ese día comparando contra el día siguiente.
        if (DateTime.TryParse(desde, out var fDesde))
            q = q.Where(p => p.Fecha >= fDesde.Date);
        if (DateTime.TryParse(hasta, out var fHasta))
        {
            var limite = fHasta.Date.AddDays(1);
            q = q.Where(p => p.Fecha < limite);
        }

        // Filtro por destino de grabación. "todos" no filtra. El destino se
        // asigna desde el alta, así que un borrador ya puede tener DestinoBase.
        if (!string.IsNullOrWhiteSpace(destino)
            && !string.Equals(destino, "todos", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(p => p.DestinoBase == destino);
        }

        var lista = await q.OrderByDescending(p => p.Id).ToListAsync();
        return Ok(lista.Select(RemitoMapeo.AItem));
    }

    // GET /api/pre-remitos/destinos  -> nombres de las bases de grabación (config)
    [HttpGet("destinos")]
    public ActionResult Destinos()
        => Ok(new { destinos = _destinos.Nombres.OrderBy(n => n, StringComparer.OrdinalIgnoreCase) });

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
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        return Ok(RemitoMapeo.ADto(p));
    }

    // POST /api/pre-remitos  -> crea en estado Borrador
    [HttpPost]
    [Authorize(Policy = Permisos.EditarRemitos)]
    public async Task<ActionResult> Crear([FromBody] CrearPreRemitoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ProveedorCodigo))
            return BadRequest(new { mensaje = "Falta el proveedor." });

        // El destino (base BAS donde se grabará) se elige en el alta. Es opcional
        // para guardar un borrador, pero si viene tiene que ser un destino conocido.
        var (destinoOk, destino, errDestino) = NormalizarDestino(req.Destino);
        if (!destinoOk) return BadRequest(new { mensaje = errDestino });

        var (tipoOk, tipo, errTipo) = NormalizarTipo(req.TipoComprobante);
        if (!tipoOk) return BadRequest(new { mensaje = errTipo });

        var p = new PreRemito
        {
            TipoComprobante = tipo,
            ProveedorCodigo = req.ProveedorCodigo.Trim(),
            ProveedorRazonSocial = req.ProveedorRazonSocial,
            Fecha = req.Fecha ?? DateTime.Today,
            ComprobantePrefijo = Limpiar(req.ComprobantePrefijo),
            ComprobanteNumero = req.ComprobanteNumero,
            ComprobanteFecha = req.ComprobanteFecha,
            Observaciones = req.Observaciones,
            DestinoBase = destino,
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
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede editar un ingreso en borrador." });

        // El destino y el tipo se pueden cambiar mientras está en borrador.
        var (destinoOk, destino, errDestino) = NormalizarDestino(req.Destino);
        if (!destinoOk) return BadRequest(new { mensaje = errDestino });

        var (tipoOk, tipo, errTipo) = NormalizarTipo(req.TipoComprobante);
        if (!tipoOk) return BadRequest(new { mensaje = errTipo });

        // La fecha de ingreso y el proveedor son la identidad del comprobante:
        // NO se modifican una vez creado. Se conservan siempre los valores
        // originales, ignorando lo que venga en el request (aunque el front ya
        // bloquea esos campos, acá lo blindamos de raíz).
        // -> p.ProveedorCodigo, p.ProveedorRazonSocial y p.Fecha quedan intactos.

        p.TipoComprobante = tipo;
        p.ComprobantePrefijo = Limpiar(req.ComprobantePrefijo);
        p.ComprobanteNumero = req.ComprobanteNumero;
        p.ComprobanteFecha = req.ComprobanteFecha;
        p.Observaciones = req.Observaciones;
        p.DestinoBase = destino;
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
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede eliminar un ingreso en borrador." });

        // Registramos antes de borrar (el log sobrevive a la eliminación).
        AgregarAuditoria(p, EventosAuditoria.Eliminacion);
        _db.PreRemitos.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { mensaje = "Ingreso eliminado." });
    }

    // POST /api/pre-remitos/{id}/conformar  -> Borrador -> Conformado
    [HttpPost("{id:int}/conformar")]
    [Authorize(Policy = Permisos.ConformarRemitos)]
    public async Task<ActionResult> Conformar(int id, [FromBody] AccionRemitoRequest req)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede conformar un ingreso en borrador." });
        if (string.IsNullOrWhiteSpace(p.ProveedorCodigo) || p.Lineas.Count == 0)
            return BadRequest(new { mensaje = "El ingreso necesita un proveedor y al menos un renglón." });
        // Para conformar exigimos que tenga destino: sin base no se va a poder grabar.
        if (string.IsNullOrWhiteSpace(p.DestinoBase))
            return BadRequest(new { mensaje = "Antes de conformar elegí la base de destino del ingreso." });
        // El comprobante del proveedor es obligatorio para BAS (lo exige el ingreso).
        // Lo pedimos ya al conformar para no fallar recién al grabar.
        if (!ComprobanteCompleto(p))
            return BadRequest(new { mensaje = "Cargá el comprobante del proveedor (prefijo, número y fecha) antes de conformar." });

        // Validación bloqueante: el proveedor y TODOS los artículos tienen que
        // existir en la base destino. Si falta algo, no se conforma y se informa
        // con detalle qué falta. (Este control se repite al grabar, porque los
        // códigos pueden cambiar entre que se conforma y se graba.)
        var val = await _resolucion.ValidarPreRemitoEnBaseAsync(
            p.DestinoBase!,
            p.ProveedorCodigo,
            p.Lineas.Select(l => l.ProductoCodigo),
            HttpContext.RequestAborted);

        if (val.Error is not null)
            return BadRequest(new { mensaje = $"No se pudo validar contra {p.DestinoBase}: {val.Error}" });

        if (!val.Ok)
            return BadRequest(new
            {
                mensaje = "No se puede conformar: " + DescribirFaltantes(val, p.DestinoBase!, p.ProveedorCodigo) + ".",
                proveedorExiste = val.ProveedorExiste,
                articulosFaltantes = val.ArticulosFaltantes
            });

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
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Conformado)
            return Conflict(new { mensaje = "Solo se puede reabrir un ingreso conformado." });

        p.Estado = EstadoPreRemito.Borrador;
        p.ConformadoPor = null;
        p.ConformadoEn = null;

        AgregarAuditoria(p, EventosAuditoria.Reabierto);
        return await GuardarConConcurrencia(p, req.RowVersion);
    }

    // POST /api/pre-remitos/{id}/grabar  -> Conformado -> Enviado (graba en BAS)
    // Según el TipoComprobante del ingreso, despacha a la API de Remito o de Factura.
    [HttpPost("{id:int}/grabar")]
    [Authorize(Policy = Permisos.ConformarRemitos)]
    public async Task<ActionResult> Grabar(int id, [FromBody] GrabarRequest req)
    {
        var p = await _db.PreRemitos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Conformado)
            return Conflict(new { mensaje = "Solo se puede grabar un ingreso conformado." });
        if (string.IsNullOrWhiteSpace(p.DestinoBase))
            return BadRequest(new { mensaje = "El ingreso no tiene base de destino." });
        // El comprobante del proveedor es obligatorio para el ingreso en BAS.
        if (!ComprobanteCompleto(p))
            return BadRequest(new { mensaje = "Falta el comprobante del proveedor (prefijo, número y fecha). BAS lo exige para el ingreso." });

        var destino = p.DestinoBase!;
        var ct = HttpContext.RequestAborted;

        // ---- Control duro: revalidar proveedor + artículos contra el destino ----
        // (Pudieron cambiar entre conformar y grabar; nunca grabamos a ciegas.)
        var val = await _resolucion.ValidarPreRemitoEnBaseAsync(
            destino, p.ProveedorCodigo, p.Lineas.Select(l => l.ProductoCodigo), ct);

        if (val.Error is not null)
            return BadRequest(new { mensaje = $"No se pudo validar contra {destino}: {val.Error}" });
        if (!val.Ok)
            return BadRequest(new
            {
                mensaje = "No se puede grabar: " + DescribirFaltantes(val, destino, p.ProveedorCodigo) + ".",
                proveedorExiste = val.ProveedorExiste,
                articulosFaltantes = val.ArticulosFaltantes
            });

        // ---- Resolvemos cada renglón en el destino para traer el BienInfo ----
        // (lo necesitamos para calcular la segunda unidad).
        var lineasOrdenadas = p.Lineas.OrderBy(l => l.Orden).ToList();
        var bienes = new Dictionary<int, BienInfo?>();
        foreach (var l in lineasOrdenadas)
        {
            var res = await _resolucion.ResolverProductoEnBaseAsync(destino, l.ProductoCodigo, ct);
            bienes[l.Id] = res.Articulo;
        }

        // ---- Grabado en BAS según el tipo de comprobante ----
        GrabadoResultado res2;
        if (p.TipoComprobante == TipoComprobante.Factura)
        {
            var renglones = lineasOrdenadas.Select(l => new BasFacturaIngresoService.RenglonFactura(
                l.ProductoCodigo, l.Cantidad, l.Partida, l.Series, bienes[l.Id])).ToList();

            res2 = await _grabadorFactura.GrabarAsync(
                destino, p.Fecha, p.ComprobanteFecha, p.ComprobantePrefijo, p.ComprobanteNumero,
                p.ProveedorCodigo, p.Observaciones, renglones, Usuario, ct);
        }
        else
        {
            var renglones = lineasOrdenadas.Select(l => new BasRemitoIngresoService.RenglonGrabado(
                l.ProductoCodigo, l.Cantidad, l.Partida, l.Series, bienes[l.Id])).ToList();

            res2 = await _grabador.GrabarAsync(
                destino, p.Fecha, p.ComprobanteFecha, p.ComprobantePrefijo, p.ComprobanteNumero,
                p.ProveedorCodigo, p.Observaciones, renglones, Usuario, ct);
        }

        if (!res2.Ok)
        {
            // Falló el grabado: queda Conformado, guardamos el error y lo auditamos.
            p.MensajeError = Recortar(res2.Error ?? "Error desconocido al grabar en BAS.");
            AgregarAuditoria(p, EventosAuditoria.Grabado, $"ERROR ({p.TipoComprobante}): {p.MensajeError}");
            await _db.SaveChangesAsync();
            return StatusCode(502, new { mensaje = "No se pudo grabar en BAS: " + p.MensajeError });
        }

        // ---- Éxito: pasa a Enviado y guardamos la referencia de BAS ----
        var referencia = ArmarReferencia(res2);
        p.Estado = EstadoPreRemito.Enviado;
        p.BasReferencia = referencia;
        p.MensajeError = null;
        p.EnviadoPor = Usuario;
        p.EnviadoEn = DateTime.Now;

        AgregarAuditoria(p, EventosAuditoria.Grabado,
            $"Grabado como {p.TipoComprobante} en {destino}" + (referencia is null ? "" : $" · {referencia}"));

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Raro acá (no usamos rowVersion del cliente en el grabado), pero por las dudas.
            return Conflict(new { mensaje = "Otro usuario modificó el ingreso durante el grabado." });
        }

        return Ok(new
        {
            mensaje = $"Ingreso grabado como {p.TipoComprobante} en {destino}.",
            destino,
            tipo = p.TipoComprobante.ToString(),
            referencia,
            idTransaccion = res2.IdTransaccion,
            prefijo = res2.Prefijo,
            numero = res2.Numero,
            comprobante = res2.Comprobante,
            preRemito = RemitoMapeo.ADto(p)
        });
    }

    // ---- Helpers ----
    private static string? Limpiar(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Recortar(string s) => s.Length > 900 ? s.Substring(0, 900) : s;

    // El comprobante del proveedor (prefijo + número + fecha) es obligatorio para
    // el ingreso de BAS. Lo consideramos completo si están los tres.
    private static bool ComprobanteCompleto(PreRemito p)
        => !string.IsNullOrWhiteSpace(p.ComprobantePrefijo)
        && p.ComprobanteNumero.HasValue && p.ComprobanteNumero.Value > 0
        && p.ComprobanteFecha.HasValue;

    // Texto legible de qué falta en el destino (proveedor y/o artículos).
    private static string DescribirFaltantes(ValidacionDestino val, string destino, string proveedorCodigo)
    {
        var problemas = new List<string>();
        if (!val.ProveedorExiste)
            problemas.Add($"el proveedor {proveedorCodigo} no existe en {destino}");
        if (val.ArticulosFaltantes.Count > 0)
            problemas.Add($"no existen en {destino} los artículos: {string.Join(", ", val.ArticulosFaltantes)}");
        return string.Join("; ", problemas);
    }

    // Arma una referencia legible del comprobante que devolvió BAS.
    private static string? ArmarReferencia(GrabadoResultado r)
    {
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Comprobante)) partes.Add(r.Comprobante!);
        var pn = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Prefijo)) pn.Add(r.Prefijo!);
        if (!string.IsNullOrWhiteSpace(r.Numero)) pn.Add(r.Numero!);
        if (pn.Count > 0) partes.Add(string.Join("-", pn));
        if (!string.IsNullOrWhiteSpace(r.IdTransaccion)) partes.Add($"trans {r.IdTransaccion}");
        return partes.Count > 0 ? string.Join(" · ", partes) : null;
    }

    // Valida y normaliza el destino que viene del request.
    //   - vacío/null  -> (ok, null, null): se guarda sin destino (borrador incompleto).
    //   - conocido    -> (ok, NOMBRE, null): se normaliza al nombre tal cual está en config.
    //   - desconocido -> (false, null, mensaje de error).
    private (bool ok, string? destino, string? error) NormalizarDestino(string? destino)
    {
        if (string.IsNullOrWhiteSpace(destino)) return (true, null, null);
        var pedido = destino.Trim();
        // Buscamos sin distinguir mayúsculas y devolvemos el nombre canónico de la config.
        var canonico = _destinos.Nombres
            .FirstOrDefault(n => string.Equals(n, pedido, StringComparison.OrdinalIgnoreCase));
        if (canonico is null)
            return (false, null, $"La base de destino '{pedido}' no existe.");
        return (true, canonico, null);
    }

    // Valida y normaliza el tipo de comprobante. Vacío/null -> Remito (default).
    private static (bool ok, TipoComprobante tipo, string? error) NormalizarTipo(string? tipo)
    {
        if (string.IsNullOrWhiteSpace(tipo)) return (true, TipoComprobante.Remito, null);
        if (Enum.TryParse<TipoComprobante>(tipo.Trim(), true, out var t))
            return (true, t, null);
        return (false, TipoComprobante.Remito, $"Tipo de comprobante inválido: '{tipo}'. Debe ser Remito o Factura.");
    }

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
            return Conflict(new { mensaje = "Otro usuario modificó este ingreso. Recargá para ver los cambios." });
        }
    }

    private sealed class Acumulado
    {
        public string Descripcion = "";
        public HashSet<string> Bases = new(StringComparer.OrdinalIgnoreCase);
    }
}
