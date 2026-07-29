using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// Administración de las bases BAS: conexión (nombre + BaseUrl + tipo de talonario)
// y parámetros de negocio (Empresa, Sucursal, prefijos, concepto, depósito, etc.).
// Alta, baja y edición. Sólo administradores.
[ApiController]
[Route("api/admin/config-bases")]
[Authorize(Policy = "Admin")]
public class ConfigBasesController : ControllerBase
{
    private readonly ConfigBasesService _svc;
    private readonly BasCacheRefresher _refresher;

    public ConfigBasesController(ConfigBasesService svc, BasCacheRefresher refresher)
    {
        _svc = svc;
        _refresher = refresher;
    }

    // GET /api/admin/config-bases  -> todas las bases con su config + conexión.
    [HttpGet]
    public async Task<ActionResult> Listar()
    {
        var filas = await _svc.ListarAsync(HttpContext.RequestAborted);
        var conteos = await _svc.ContarIngresosPorBaseAsync(HttpContext.RequestAborted);
        var bases = filas.Select(f => new
        {
            f.Nombre,
            f.Descripcion,
            f.Activa,
            f.Empresa,
            f.Sucursal,
            f.RemitoPrefijo,
            f.RemitoConcepto,
            f.RemitoDeposito,
            f.FacturaPrefijo,
            f.FacturaConcepto,
            f.FacturaDeposito,
            f.FacturaImputacionContable,
            f.OrdenCompraPrefijo,
            // Conexión (ahora editable):
            baseUrl = f.BaseUrl,
            remitoTipo = f.RemitoTipo,
            incluirEnPortal = f.IncluirEnPortal,
            // SQL directo (e-cheques). La clave NO se devuelve: sólo si hay una cargada.
            sqlServidor = f.SqlServidor,
            sqlBase = f.SqlBase,
            sqlUsuario = f.SqlUsuario,
            sqlEmailPropio = f.SqlEmailPropio,
            sqlTieneClave = !string.IsNullOrWhiteSpace(f.SqlClave),
            // Banco Credicoop (echeqs por API), por empresa. Campos no-secretos (la PEM es un archivo).
            bieHabilitado = f.BieHabilitado,
            bieEntorno = f.BieEntorno,
            bieClientId = f.BieClientId,
            bieNumeroAdherente = f.BieNumeroAdherente,
            bieCbuDebito = f.BieCbuDebito,
            biePemPath = f.BiePemPath,
            bieFirmantes = f.BieFirmantes,
            cuentasBas = f.CuentasBas,
            tituloBas = f.TituloBas,
            echApiDesde = f.EchApiDesde,
            ingresos = conteos.TryGetValue(f.Nombre, out var n) ? n : 0,
            conectada = _svc.Memoria(f.Nombre) != null    // está viva en memoria
        });
        return Ok(new { bases });
    }

    // POST /api/admin/config-bases  -> crea una base nueva.
    [HttpPost]
    public async Task<ActionResult> Crear([FromBody] CrearConfigBaseRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { mensaje = "El nombre de la base es obligatorio." });
        if (!UrlValida(req.BaseUrl))
            return BadRequest(new { mensaje = "La URL de conexión no es válida (ej. http://localhost:5081)." });
        var errNeg = ValidarNegocio(req.Empresa, req.Sucursal, req.RemitoPrefijo, req.RemitoConcepto,
            req.RemitoDeposito, req.FacturaPrefijo, req.FacturaConcepto, req.FacturaDeposito, req.FacturaImputacionContable);
        if (errNeg is not null) return BadRequest(new { mensaje = errNeg });

        var (fila, error) = await _svc.CrearAsync(req, HttpContext.RequestAborted);
        if (fila is null) return Conflict(new { mensaje = error });

        // Carga del padrón (productos + proveedores) de la base nueva en segundo
        // plano: no bloquea la respuesta. Los errores quedan en el log del refresher.
        var nombre = fila.Nombre;
        _ = Task.Run(async () =>
        {
            try { await _refresher.RefrescarBaseAsync(nombre); }
            catch { /* best-effort */ }
        });

        return Ok(new { mensaje = "Base creada. El padrón se está cargando en segundo plano.", nombre });
    }

    // PUT /api/admin/config-bases/{nombre}  -> edita la config de una base.
    [HttpPut("{nombre}")]
    public async Task<ActionResult> Actualizar(string nombre, [FromBody] ActualizarConfigBaseRequest req)
    {
        if (!UrlValida(req.BaseUrl))
            return BadRequest(new { mensaje = "La URL de conexión no es válida (ej. http://localhost:5081)." });
        var errNeg = ValidarNegocio(req.Empresa, req.Sucursal, req.RemitoPrefijo, req.RemitoConcepto,
            req.RemitoDeposito, req.FacturaPrefijo, req.FacturaConcepto, req.FacturaDeposito, req.FacturaImputacionContable);
        if (errNeg is not null) return BadRequest(new { mensaje = errNeg });

        var f = await _svc.ActualizarAsync(nombre, req, HttpContext.RequestAborted);
        if (f is null)
            return NotFound(new { mensaje = $"No existe configuración para la base '{nombre}'." });

        return Ok(new { mensaje = "Configuración guardada.", nombre = f.Nombre });
    }

    // DELETE /api/admin/config-bases/{nombre}?borrarRegistros=&borrarAuditoria=
    //   borrarRegistros=false -> elimina sólo la base; los ingresos quedan (en rojo).
    //   borrarRegistros=true  -> elimina la base y borra también sus ingresos.
    //   borrarAuditoria=true  -> (con borrarRegistros) borra además su auditoría.
    [HttpDelete("{nombre}")]
    public async Task<ActionResult> Eliminar(
        string nombre,
        [FromQuery] bool borrarRegistros = false,
        [FromQuery] bool borrarAuditoria = false)
    {
        var (ok, error, ingresos) = await _svc.EliminarAsync(
            nombre, borrarRegistros, borrarAuditoria, HttpContext.RequestAborted);
        if (!ok) return BadRequest(new { mensaje = error });
        var msg = ingresos == 0
            ? "Base eliminada."
            : borrarRegistros
                ? (borrarAuditoria
                    ? $"Base eliminada junto con {ingresos} ingreso(s) y su auditoría."
                    : $"Base eliminada junto con {ingresos} ingreso(s) (auditoría conservada).")
                : $"Base eliminada. Quedaron {ingresos} ingreso(s) con el destino '{nombre}' (se muestran en rojo).";
        return Ok(new { mensaje = msg, nombre, ingresos });
    }

    private static bool UrlValida(string? url)
        => Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string? ValidarNegocio(
        int empresa, int sucursal, string? remPrefijo, string? remConcepto, int remDeposito,
        string? facPrefijo, string? facConcepto, int facDeposito, long facImputacion)
    {
        if (string.IsNullOrWhiteSpace(remPrefijo)) return "El prefijo de remito es obligatorio.";
        if (string.IsNullOrWhiteSpace(remConcepto)) return "El concepto de remito es obligatorio.";
        if (string.IsNullOrWhiteSpace(facPrefijo)) return "El prefijo de factura es obligatorio.";
        if (string.IsNullOrWhiteSpace(facConcepto)) return "El concepto de factura es obligatorio.";
        if (empresa <= 0 || sucursal <= 0) return "Empresa y Sucursal deben ser mayores a cero.";
        if (remDeposito <= 0) return "El depósito de remito debe ser mayor a cero.";
        if (facDeposito <= 0) return "El depósito de factura debe ser mayor a cero.";
        if (facImputacion <= 0) return "La cuenta contable de la factura debe ser mayor a cero.";
        return null;
    }
}
