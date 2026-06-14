using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    // BAS limita el campo Partida a 13 caracteres. Lo validamos antes de mandar.
    private const int LargoMaxPartida = 13;

    private readonly PortalDbContext _db;
    private readonly BasResolucionService _resolucion;
    private readonly BasCacheMaestros _cache;
    private readonly BasCacheRefresher _refresher;
    private readonly BasDestinosService _destinos;
    private readonly BasRemitoIngresoService _grabador;
    private readonly BasFacturaIngresoService _grabadorFactura;
    private readonly BasPartidasService _partidas;

    public PreRemitosController(
        PortalDbContext db,
        BasResolucionService resolucion,
        BasCacheMaestros cache,
        BasCacheRefresher refresher,
        BasDestinosService destinos,
        BasRemitoIngresoService grabador,
        BasFacturaIngresoService grabadorFactura,
        BasPartidasService partidas)
    {
        _db = db;
        _resolucion = resolucion;
        _cache = cache;
        _refresher = refresher;
        _destinos = destinos;
        _grabador = grabador;
        _grabadorFactura = grabadorFactura;
        _partidas = partidas;
    }

    private string Usuario => User.FindFirstValue("identificador") ?? "";

    // ¿La base de destino existe y está activa? (la activación/desactivación se
    // maneja desde la sección Configuración; el objeto vive en memoria).
    private bool DestinoActivo(string? destino)
        => !string.IsNullOrWhiteSpace(destino) && (_destinos.Config(destino!)?.Activa ?? false);

    // Carga un ingreso con sus renglones y percepciones (para leer/editar/grabar).
    private IQueryable<PreRemito> PreRemitosCompletos()
        => _db.PreRemitos.Include(p => p.Lineas).Include(p => p.PercepcionesIngBr);

    // GET /api/pre-remitos?estado=&desde=&hasta=&destino=&tipo=
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

        // Filtro por destino de grabación. "todos" no filtra.
        if (!string.IsNullOrWhiteSpace(destino)
            && !string.Equals(destino, "todos", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(p => p.DestinoBase == destino);
        }

        var lista = await q.OrderByDescending(p => p.Id).ToListAsync();
        return Ok(lista.Select(RemitoMapeo.AItem));
    }

    // GET /api/pre-remitos/destinos  -> nombres de las bases de grabación ACTIVAS.
    [HttpGet("destinos")]
    public ActionResult Destinos()
        => Ok(new
        {
            destinos = _destinos.Nombres
                .Where(n => _destinos.Config(n)?.Activa ?? true)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            // Bases que existen pero están inactivas: sirve para distinguir, en la
            // lista de ingresos, un destino INACTIVO (negro) de uno ELIMINADO (rojo).
            inactivas = _destinos.Nombres
                .Where(n => !(_destinos.Config(n)?.Activa ?? true))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        });

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
        var p = await PreRemitosCompletos().FirstOrDefaultAsync(x => x.Id == id);
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
            Lineas = MapearLineas(req.Lineas),
            // ---- Factura ----
            Letra = NormalizarLetra(req.Letra),
            CondicionCompra = Limpiar(req.CondicionCompra),
            PercepcionIva = req.PercepcionIva,
            TotalDeclarado = req.TotalDeclarado,
            PercepcionesIngBr = MapearPercepciones(req.PercepcionesIngBr)
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
        var p = await PreRemitosCompletos().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede editar un ingreso en borrador." });

        var (destinoOk, destino, errDestino) = NormalizarDestino(req.Destino);
        if (!destinoOk) return BadRequest(new { mensaje = errDestino });

        var (tipoOk, tipo, errTipo) = NormalizarTipo(req.TipoComprobante);
        if (!tipoOk) return BadRequest(new { mensaje = errTipo });

        // La fecha de ingreso y el proveedor son la identidad del comprobante:
        // NO se modifican una vez creado (el front ya los bloquea; acá lo blindamos).

        p.TipoComprobante = tipo;
        p.ComprobantePrefijo = Limpiar(req.ComprobantePrefijo);
        p.ComprobanteNumero = req.ComprobanteNumero;
        p.ComprobanteFecha = req.ComprobanteFecha;
        p.Observaciones = req.Observaciones;
        p.DestinoBase = destino;
        p.ModificadoPor = Usuario;
        p.ModificadoEn = DateTime.Now;

        // ---- Factura ----
        p.Letra = NormalizarLetra(req.Letra);
        p.CondicionCompra = Limpiar(req.CondicionCompra);
        p.PercepcionIva = req.PercepcionIva;
        p.TotalDeclarado = req.TotalDeclarado;

        // Reemplazamos renglones y percepciones.
        _db.PreRemitoLineas.RemoveRange(p.Lineas);
        p.Lineas = MapearLineas(req.Lineas);
        _db.PreRemitoPercepcionesIngBr.RemoveRange(p.PercepcionesIngBr);
        p.PercepcionesIngBr = MapearPercepciones(req.PercepcionesIngBr);

        AgregarAuditoria(p, EventosAuditoria.Modificacion);
        return await GuardarConConcurrencia(p, req.RowVersion);
    }

    // DELETE /api/pre-remitos/{id}  -> elimina (solo en Borrador)
    [HttpDelete("{id:int}")]
    [Authorize(Policy = Permisos.EditarRemitos)]
    public async Task<ActionResult> Eliminar(int id)
    {
        var p = await PreRemitosCompletos().FirstOrDefaultAsync(x => x.Id == id);
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
        var p = await PreRemitosCompletos().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Borrador)
            return Conflict(new { mensaje = "Solo se puede conformar un ingreso en borrador." });
        if (string.IsNullOrWhiteSpace(p.ProveedorCodigo) || p.Lineas.Count == 0)
            return BadRequest(new { mensaje = "El ingreso necesita un proveedor y al menos un renglón." });
        if (string.IsNullOrWhiteSpace(p.DestinoBase))
            return BadRequest(new { mensaje = "Antes de conformar elegí la base de destino del ingreso." });
        if (!DestinoActivo(p.DestinoBase))
            return BadRequest(new { mensaje = $"La base de destino '{p.DestinoBase}' está inactiva. Activala en Configuración o elegí otra base." });
        // En una factura, la letra es obligatoria (define el comprobante en BAS).
        if (p.TipoComprobante == TipoComprobante.Factura && string.IsNullOrWhiteSpace(p.Letra))
            return BadRequest(new { mensaje = "Elegí la letra de la factura (A, B o C) antes de conformar." });
        if (!ComprobanteCompleto(p))
            return BadRequest(new { mensaje = "Cargá el comprobante del proveedor (prefijo, número y fecha) antes de conformar." });
        // La partida no puede superar lo que admite BAS (13 caracteres).
        var errPartida = ValidarPartidas(p);
        if (errPartida is not null) return BadRequest(new { mensaje = errPartida });

        // Validación bloqueante: proveedor + artículos tienen que existir en la base.
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
        var p = await PreRemitosCompletos().FirstOrDefaultAsync(x => x.Id == id);
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
    [HttpPost("{id:int}/grabar")]
    [Authorize(Policy = Permisos.ConformarRemitos)]
    public async Task<ActionResult> Grabar(int id, [FromBody] GrabarRequest req)
    {
        var p = await PreRemitosCompletos().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { mensaje = "No se encontró el ingreso." });
        if (p.Estado != EstadoPreRemito.Conformado)
            return Conflict(new { mensaje = "Solo se puede grabar un ingreso conformado." });
        if (string.IsNullOrWhiteSpace(p.DestinoBase))
            return BadRequest(new { mensaje = "El ingreso no tiene base de destino." });
        if (!DestinoActivo(p.DestinoBase))
            return BadRequest(new { mensaje = $"La base de destino '{p.DestinoBase}' está inactiva. Activala en Configuración o elegí otra base." });
        if (!ComprobanteCompleto(p))
            return BadRequest(new { mensaje = "Falta el comprobante del proveedor (prefijo, número y fecha). BAS lo exige para el ingreso." });
        // La partida no puede superar lo que admite BAS (13 caracteres).
        var errPartida = ValidarPartidas(p);
        if (errPartida is not null) return BadRequest(new { mensaje = errPartida });

        var destino = p.DestinoBase!;
        var ct = HttpContext.RequestAborted;

        // ---- Control duro: revalidar proveedor + artículos contra el destino ----
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
        var lineasOrdenadas = p.Lineas.OrderBy(l => l.Orden).ToList();
        var bienes = new Dictionary<int, BienInfo?>();
        foreach (var l in lineasOrdenadas)
        {
            var res = await _resolucion.ResolverProductoEnBaseAsync(destino, l.ProductoCodigo, ct);
            bienes[l.Id] = res.Articulo;
        }

        // ---- Alta idempotente de PARTIDAS (las decide el usuario por renglón) ----
        // BAS exige que la partida exista antes de usarla en el ítem. La partida se
        // forma {proveedor}-{ddMMyy de la fecha del ingreso}. El renglón lleva partida
        // si el usuario le cargó un vencimiento: ahí la generamos y la aseguramos en
        // BAS (la crea si falta, idempotente). Sin vencimiento, el renglón va sin partida.
        var partidaPorLinea = new Dictionary<int, string?>();
        var numeroPartida = BasPartidasService.NumeroPartida(p.ProveedorCodigo, p.Fecha);
        // La descripción de la partida es la razón social del proveedor (si no la
        // tenemos guardada, caemos al número de partida).
        var descripcionPartida = string.IsNullOrWhiteSpace(p.ProveedorRazonSocial)
            ? numeroPartida
            : p.ProveedorRazonSocial.Trim();
        for (int i = 0; i < lineasOrdenadas.Count; i++)
        {
            var l = lineasOrdenadas[i];
            if (l.FechaVencimiento.HasValue)
            {
                if (numeroPartida.Length > LargoMaxPartida)
                    return BadRequest(new { mensaje = $"La partida calculada '{numeroPartida}' supera los {LargoMaxPartida} caracteres. Revisá el código de proveedor." });

                var ase = await _partidas.AsegurarAsync(destino, l.ProductoCodigo, numeroPartida, descripcionPartida, l.FechaVencimiento.Value, ct);
                if (!ase.Ok)
                    return StatusCode(502, new { mensaje = $"No se pudo crear/verificar la partida '{numeroPartida}' del artículo {l.ProductoCodigo} en {destino}: {ase.Error}" });

                l.Partida = numeroPartida;          // dejamos registrada la partida usada
                partidaPorLinea[l.Id] = numeroPartida;
            }
            else
            {
                partidaPorLinea[l.Id] = null;
            }
        }

        // ---- Grabado en BAS según el tipo de comprobante ----
        GrabadoResultado res2;
        if (p.TipoComprobante == TipoComprobante.Factura)
        {
            var renglones = lineasOrdenadas.Select(l => new BasFacturaIngresoService.RenglonFactura(
                l.ProductoCodigo, l.Cantidad, l.PrecioUnitario, l.TasaIva, partidaPorLinea[l.Id], l.Series, bienes[l.Id])).ToList();

            var percepciones = p.PercepcionesIngBr.Select(x => new BasFacturaIngresoService.PercepcionIngBrItem(
                x.Provincia, x.BaseImponible, x.Importe, x.Porcentaje, x.Regimen)).ToList();

            res2 = await _grabadorFactura.GrabarAsync(
                destino, p.Fecha, p.ComprobanteFecha, p.ComprobantePrefijo, p.ComprobanteNumero,
                p.ProveedorCodigo, p.Observaciones, p.Letra, p.PercepcionIva, percepciones,
                p.TotalDeclarado, renglones, Usuario, ct);
        }
        else
        {
            var renglones = lineasOrdenadas.Select(l => new BasRemitoIngresoService.RenglonGrabado(
                l.ProductoCodigo, l.Cantidad, partidaPorLinea[l.Id], l.Series, bienes[l.Id])).ToList();

            res2 = await _grabador.GrabarAsync(
                destino, p.Fecha, p.ComprobanteFecha, p.ComprobantePrefijo, p.ComprobanteNumero,
                p.ProveedorCodigo, p.Observaciones, renglones, Usuario, ct);
        }

        if (!res2.Ok)
        {
            // Mensaje legible: referencia al ingreso + errores de validación de BAS.
            var msgUsuario = FormatearErrorBas(res2.Error, p);
            p.MensajeError = Recortar(msgUsuario);
            AgregarAuditoria(p, EventosAuditoria.Grabado, $"ERROR ({p.TipoComprobante}): {p.MensajeError}");
            await _db.SaveChangesAsync();
            return StatusCode(502, new { mensaje = "No se pudo grabar en BAS: " + msgUsuario });
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

    // Normaliza la letra a "A" / "B" / "C" en mayúscula, o null si no es válida.
    private static string? NormalizarLetra(string? letra)
    {
        var l = (letra ?? "").Trim().ToUpperInvariant();
        return l is "A" or "B" or "C" ? l : null;
    }

    private static string Recortar(string s) => s.Length > 1500 ? s.Substring(0, 1500) : s;

    private static bool ComprobanteCompleto(PreRemito p)
        => !string.IsNullOrWhiteSpace(p.ComprobantePrefijo)
        && p.ComprobanteNumero.HasValue && p.ComprobanteNumero.Value > 0
        && p.ComprobanteFecha.HasValue;

    // Verifica que ninguna partida supere el largo que admite BAS. Devuelve el
    // mensaje de error (nombrando los renglones) o null si está todo bien.
    private static string? ValidarPartidas(PreRemito p)
    {
        var largas = p.Lineas
            .OrderBy(l => l.Orden)
            .Select((l, i) => (nro: i + 1, l))
            .Where(x => (x.l.Partida?.Trim().Length ?? 0) > LargoMaxPartida)
            .ToList();
        if (largas.Count == 0) return null;

        var detalle = string.Join("; ", largas.Select(x =>
            $"renglón {x.nro} ({x.l.ProductoCodigo}): \"{x.l.Partida}\""));
        return $"La partida supera los {LargoMaxPartida} caracteres que admite BAS en {detalle}. " +
               $"Acortala a {LargoMaxPartida} caracteres o menos.";
    }

    private static string DescribirFaltantes(ValidacionDestino val, string destino, string proveedorCodigo)
    {
        var problemas = new List<string>();
        if (!val.ProveedorExiste)
            problemas.Add($"el proveedor {proveedorCodigo} no existe en {destino}");
        if (val.ArticulosFaltantes.Count > 0)
            problemas.Add($"no existen en {destino} los artículos: {string.Join(", ", val.ArticulosFaltantes)}");
        return string.Join("; ", problemas);
    }

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

    // Arma un mensaje de error legible: referencia al ingreso + errores de
    // validación de BAS (cuando vienen como ProblemDetails con "errors").
    private static string FormatearErrorBas(string? error, PreRemito p)
    {
        var letra = string.IsNullOrWhiteSpace(p.Letra) ? "" : $" {p.Letra}";
        var comp = string.IsNullOrWhiteSpace(p.ComprobantePrefijo)
            ? ""
            : $", comp. {p.ComprobantePrefijo}-{p.ComprobanteNumero}";
        var refIngreso = $"Ingreso #{p.Id} ({p.TipoComprobante}{letra}, proveedor {p.ProveedorCodigo}{comp})";

        var detalle = ExtraerErroresValidacion(error)
                      ?? (string.IsNullOrWhiteSpace(error) ? "Error desconocido al grabar en BAS." : error.Trim());
        return $"{refIngreso}: {detalle}";
    }

    // Si el error contiene un ProblemDetails de validación ({"errors":{...}}),
    // devuelve los campos y mensajes en texto legible. Si no, null.
    private static string? ExtraerErroresValidacion(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var i = error.IndexOf('{');
        if (i < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(error[i..]);
            if (!doc.RootElement.TryGetProperty("errors", out var errs) || errs.ValueKind != JsonValueKind.Object)
                return null;

            var partes = new List<string>();
            foreach (var campo in errs.EnumerateObject())
            {
                var msgs = campo.Value.ValueKind == JsonValueKind.Array
                    ? string.Join(" ", campo.Value.EnumerateArray().Select(x => x.GetString()))
                    : campo.Value.ToString();
                partes.Add($"{AmigarCampo(campo.Name)}: {msgs}");
            }
            return partes.Count > 0 ? string.Join(" | ", partes) : null;
        }
        catch
        {
            return null;
        }
    }

    // "Items[0].Partida" -> "Renglón 1 · Partida".
    private static string AmigarCampo(string campo)
    {
        var m = Regex.Match(campo, @"^Items\[(\d+)\]\.(.+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var idx))
            return $"Renglón {idx + 1} · {m.Groups[2].Value}";
        return campo;
    }

    // Valida y normaliza el destino que viene del request.
    // Nota: NO se rechaza un destino inactivo al guardar (para no romper borradores
    // viejos). El bloqueo por inactiva está en Conformar y Grabar.
    private (bool ok, string? destino, string? error) NormalizarDestino(string? destino)
    {
        if (string.IsNullOrWhiteSpace(destino)) return (true, null, null);
        var pedido = destino.Trim();
        var canonico = _destinos.Nombres
            .FirstOrDefault(n => string.Equals(n, pedido, StringComparison.OrdinalIgnoreCase));
        if (canonico is null)
            return (false, null, $"La base de destino '{pedido}' no existe.");
        return (true, canonico, null);
    }

    private static (bool ok, TipoComprobante tipo, string? error) NormalizarTipo(string? tipo)
    {
        if (string.IsNullOrWhiteSpace(tipo)) return (true, TipoComprobante.Remito, null);
        if (Enum.TryParse<TipoComprobante>(tipo.Trim(), true, out var t))
            return (true, t, null);
        return (false, TipoComprobante.Remito, $"Tipo de comprobante inválido: '{tipo}'. Debe ser Remito o Factura.");
    }

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
                PrecioUnitario = l.PrecioUnitario,
                TasaIva = l.TasaIva,
                FechaVencimiento = l.FechaVencimiento,
                Orden = orden++
            });
        }
        return resultado;
    }

    // Mapea las percepciones de IIBB del request a entidades, calculando el % a
    // partir del importe y la base. Ignora filas vacías (sin importe ni provincia).
    private static List<PreRemitoPercepcionIngBr> MapearPercepciones(List<PercepcionIngBrRequest>? percepciones)
    {
        var resultado = new List<PreRemitoPercepcionIngBr>();
        if (percepciones is null) return resultado;
        foreach (var x in percepciones)
        {
            if (x.Importe == 0 && string.IsNullOrWhiteSpace(x.Provincia)) continue;
            var baseImp = x.BaseImponible;
            var pct = baseImp > 0
                ? Math.Round(x.Importe / baseImp * 100m, 4, MidpointRounding.AwayFromZero)
                : 0m;
            resultado.Add(new PreRemitoPercepcionIngBr
            {
                Provincia = Limpiar(x.Provincia),
                BaseImponible = baseImp,
                Importe = x.Importe,
                Porcentaje = pct,
                Regimen = Limpiar(x.Regimen)
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
