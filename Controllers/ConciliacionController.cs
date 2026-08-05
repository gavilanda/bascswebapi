using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortalClientes.Auth;
using PortalClientes.Bas;
using PortalClientes.Data;
using PortalClientes.Models;

namespace PortalClientes.Controllers;

// Bco/Conciliación (función interna). Trae los movimientos de una cuenta del banco (API
// Credicoop, scope cuentas) para conciliar, y genera el TXT posicional que importa BAS.
// Usa la misma config del banco POR EMPRESA que E-Cheques (BieCredenciales).
[ApiController]
[Route("api/conciliacion")]
[Authorize]
public class ConciliacionController : ControllerBase
{
    private readonly BancoBieCuentasService _cuentas;
    private readonly IcbcConciliacionService _icbc;
    private readonly BancoBieOptions _bieOpt;
    private readonly PortalDbContext _db;
    private readonly AccesoFuncionesService _acceso;

    public ConciliacionController(BancoBieCuentasService cuentas, IcbcConciliacionService icbc,
        IOptions<BancoBieOptions> bieOpt, PortalDbContext db, AccesoFuncionesService acceso)
    {
        _cuentas = cuentas;
        _icbc = icbc;
        _bieOpt = bieOpt.Value;
        _db = db;
        _acceso = acceso;
    }

    private bool EsInterno() => User.FindFirstValue("tipo") == "Interno";

    private async Task<ActionResult?> SinAccesoAsync(CancellationToken ct)
        => (EsInterno() && await _acceso.PuedeUsarAsync("conciliacion", User, ct))
            ? null
            : StatusCode(403, new { mensaje = "No tenés permiso para usar Conciliación." });

    private async Task<BieCredenciales?> CredsAsync(string baseNombre, CancellationToken ct)
    {
        var cb = await _db.ConfiguracionesBase.AsNoTracking().FirstOrDefaultAsync(c => c.Nombre == baseNombre, ct);
        return cb is null ? null : _bieOpt.Credenciales(cb);
    }

    // GET /api/conciliacion/bases -> empresas con la API del banco configurada.
    [HttpGet("bases")]
    public async Task<ActionResult> Bases(CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var cfgs = await _db.ConfiguracionesBase.AsNoTracking().ToListAsync(ct);
        var bases = cfgs.Where(c => _bieOpt.Credenciales(c) is not null)
            .Select(c => c.Nombre).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return Ok(new { bases });
    }

    // GET /api/conciliacion/cuentas?base= -> cuentas del adherente (para el selector).
    [HttpGet("cuentas")]
    public async Task<ActionResult> Cuentas([FromQuery(Name = "base")] string? baseNombre, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var creds = await CredsAsync((baseNombre ?? "").Trim(), ct);
        if (creds is null) return StatusCode(409, new { mensaje = $"La empresa '{baseNombre}' no tiene la API del banco configurada." });
        try
        {
            var cuentas = await _cuentas.ListarCuentasAsync(creds, ct);
            return Ok(new { cuentas });
        }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudieron traer las cuentas: " + ex.Message }); }
    }

    // Valida y normaliza base + cuenta + rango de fechas.
    private ActionResult? Parsear(string? baseNombre, string? cuenta, string? desde, string? hasta,
        out string b, out string cta, out DateOnly d, out DateOnly h)
    {
        b = (baseNombre ?? "").Trim(); cta = (cuenta ?? "").Trim(); d = default; h = default;
        if (b.Length == 0 || cta.Length == 0) return BadRequest(new { mensaje = "Elegí empresa y cuenta." });
        if (!DateOnly.TryParse(desde, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ||
            !DateOnly.TryParse(hasta, CultureInfo.InvariantCulture, DateTimeStyles.None, out h))
            return BadRequest(new { mensaje = "Fechas inválidas (usá yyyy-MM-dd)." });
        if (h < d) return BadRequest(new { mensaje = "La fecha 'Desde' no puede ser mayor que 'Hasta'." });
        return null;
    }

    // GET /api/conciliacion/movimientos?base=&cuenta=&desde=&hasta= -> movimientos (para el modal).
    [HttpGet("movimientos")]
    public async Task<ActionResult> Movimientos(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? cuenta,
        [FromQuery] string? desde, [FromQuery] string? hasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, cuenta, desde, hasta, out var b, out var cta, out var d, out var h);
        if (err is not null) return err;
        var creds = await CredsAsync(b, ct);
        if (creds is null) return StatusCode(409, new { mensaje = $"La empresa '{b}' no tiene la API del banco configurada." });
        try
        {
            var movimientos = await _cuentas.MovimientosAsync(creds, cta, d, h, ct);
            return Ok(new { cantidad = movimientos.Count, movimientos });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudieron traer los movimientos: " + ex.Message }); }
    }

    // GET /api/conciliacion/txt?base=&cuenta=&desde=&hasta= -> TXT posicional para BAS.
    [HttpGet("txt")]
    public async Task<ActionResult> Txt(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? cuenta,
        [FromQuery] string? desde, [FromQuery] string? hasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, cuenta, desde, hasta, out var b, out var cta, out var d, out var h);
        if (err is not null) return err;
        var cb = await _db.ConfiguracionesBase.AsNoTracking().FirstOrDefaultAsync(c => c.Nombre == b, ct);
        var creds = cb is null ? null : _bieOpt.Credenciales(cb);
        if (creds is null) return StatusCode(409, new { mensaje = $"La empresa '{b}' no tiene la API del banco configurada." });
        try
        {
            var movimientos = await _cuentas.MovimientosAsync(creds, cta, d, h, ct);
            if (movimientos.Count == 0) return Ok(new { cantidad = 0 });   // sin movimientos: el front avisa
            var bytes = BancoBieCuentasService.ArmarTxt(movimientos);
            // Nombre FIJO por empresa (CONC_<EMPRESA>.txt): el macro toma siempre el mismo archivo.
            var empresa = new string(b.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
            var nombre = $"CONC_{empresa}.txt";

            // Se GUARDA directo en la carpeta del servidor (pisando el anterior); NO se descarga
            // (así el navegador no pregunta dónde). La carpeta se crea si no existe.
            var carpeta = (_bieOpt.CarpetaConciliacion ?? "").Trim();
            if (carpeta.Length == 0)
                return StatusCode(409, new { mensaje = "No hay carpeta de conciliación configurada (BancoBie:CarpetaConciliacion)." });
            Directory.CreateDirectory(carpeta);
            var rutaArchivo = Path.Combine(carpeta, nombre);
            await System.IO.File.WriteAllBytesAsync(rutaArchivo, bytes, ct);

            // Companion CONC_<EMPRESA>.info: metadatos (key=value) para que el macro de BAS
            // arme el "período" de la descripción. Sin BOM (el macro lee UTF-8 simple).
            var rutaInfo = Path.Combine(carpeta, $"CONC_{empresa}.info");
            var cuentaBas = MapearCuentaBas(cb!.CuentasBas, cta);   // código interno de BAS (config por empresa)
            var info = string.Join("\r\n", new[]
            {
                $"empresa={b}",
                $"banco=CREDICOOP",
                $"cuenta={cta}",
                $"cuentaBas={cuentaBas}",
                $"tituloBas={cb.TituloBas?.Trim() ?? ""}",   // marca que la macro exige en el título de BAS
                $"desde={d:dd/MM/yyyy}",
                $"hasta={h:dd/MM/yyyy}",
                $"cantidad={movimientos.Count}",
            }) + "\r\n";
            await System.IO.File.WriteAllTextAsync(rutaInfo, info, new System.Text.UTF8Encoding(false), ct);
            return Ok(new { cantidad = movimientos.Count, ruta = rutaArchivo, info = rutaInfo });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar/guardar el TXT: " + ex.Message }); }
    }

    // Busca en el mapa por-empresa (líneas "nroCuentaBanco=codigoBAS") el código interno de
    // BAS para la cuenta bancaria dada. Compara sólo por DÍGITOS (así "0513/02104031/13" del
    // ICBC matchea con o sin barras). Devuelve "" si no está mapeada.
    private static string MapearCuentaBas(string? mapa, string cuentaBanco)
    {
        static string Dig(string s) => new string((s ?? "").Where(char.IsDigit).ToArray());
        var objetivo = Dig(cuentaBanco);
        foreach (var linea in (mapa ?? "").Split('\n'))
        {
            var l = linea.Trim();
            var i = l.IndexOf('=');
            if (i <= 0) continue;
            if (objetivo.Length > 0 && Dig(l[..i]) == objetivo) return l[(i + 1)..].Trim();
        }
        return "";
    }

    // Escribe CONC_<EMPRESA>.txt (posicional) + CONC_<EMPRESA>.info en la carpeta de conciliación.
    // Compartido por Credicoop (API) e ICBC (CSV): misma estructura de archivos para el macro de BAS.
    private async Task<string> GuardarTxtInfoAsync(ConfiguracionBase cb, string banco, string cuentaBanco,
        IReadOnlyList<BancoBieCuentasService.Movimiento> movs, string desde, string hasta, CancellationToken ct)
    {
        var carpeta = (_bieOpt.CarpetaConciliacion ?? "").Trim();
        if (carpeta.Length == 0)
            throw new InvalidOperationException("No hay carpeta de conciliación configurada (BancoBie:CarpetaConciliacion).");
        var empresa = new string(cb.Nombre.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
        Directory.CreateDirectory(carpeta);
        var rutaArchivo = Path.Combine(carpeta, $"CONC_{empresa}.txt");
        await System.IO.File.WriteAllBytesAsync(rutaArchivo, BancoBieCuentasService.ArmarTxt(movs), ct);
        var info = string.Join("\r\n", new[]
        {
            $"empresa={cb.Nombre}",
            $"banco={banco}",
            $"cuenta={cuentaBanco}",
            $"cuentaBas={MapearCuentaBas(cb.CuentasBas, cuentaBanco)}",
            $"tituloBas={cb.TituloBas?.Trim() ?? ""}",
            $"desde={desde}",
            $"hasta={hasta}",
            $"cantidad={movs.Count}",
        }) + "\r\n";
        await System.IO.File.WriteAllTextAsync(Path.Combine(carpeta, $"CONC_{empresa}.info"), info, new System.Text.UTF8Encoding(false), ct);
        return rutaArchivo;
    }

    // ---- ICBC: conciliación por importación de CSV (el banco no tiene API) ----

    // POST /api/conciliacion/icbc/movimientos  (multipart: archivo=CSV) -> movimientos parseados
    // (misma estructura que Credicoop) para el modal.
    [HttpPost("icbc/movimientos")]
    public async Task<ActionResult> IcbcMovimientos([FromForm] IFormFile? archivo, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        if (archivo is null || archivo.Length == 0) return BadRequest(new { mensaje = "Subí el archivo CSV del ICBC." });
        try
        {
            IcbcConciliacionService.Resultado res;
            await using (var s = archivo.OpenReadStream()) res = _icbc.Parsear(s);
            return Ok(new { cantidad = res.Movimientos.Count, cuenta = res.Cuenta, movimientos = res.Movimientos });
        }
        catch (Exception ex) { return StatusCode(422, new { mensaje = "No se pudo leer el CSV del ICBC: " + ex.Message }); }
    }

    // POST /api/conciliacion/icbc/txt?base=  (multipart: archivo=CSV) -> genera CONC_<empresa>.txt + .info.
    [HttpPost("icbc/txt")]
    public async Task<ActionResult> IcbcTxt(
        [FromQuery(Name = "base")] string? baseNombre, [FromForm] IFormFile? archivo, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var b = (baseNombre ?? "").Trim();
        if (b.Length == 0) return BadRequest(new { mensaje = "Elegí una empresa." });
        if (archivo is null || archivo.Length == 0) return BadRequest(new { mensaje = "Subí el archivo CSV del ICBC." });
        var cb = await _db.ConfiguracionesBase.AsNoTracking().FirstOrDefaultAsync(c => c.Nombre == b, ct);
        if (cb is null) return StatusCode(409, new { mensaje = $"Empresa '{b}' desconocida." });
        try
        {
            IcbcConciliacionService.Resultado res;
            await using (var s = archivo.OpenReadStream()) res = _icbc.Parsear(s);
            if (res.Movimientos.Count == 0) return Ok(new { cantidad = 0 });
            static string Fmt(string ymd) => ymd.Length == 8 ? $"{ymd[6..8]}/{ymd[4..6]}/{ymd[0..4]}" : "";
            var fechas = res.Movimientos.Select(m => m.Fecha).Where(f => f.Length == 8).OrderBy(f => f).ToList();
            var ruta = await GuardarTxtInfoAsync(cb, "ICBC", res.Cuenta, res.Movimientos,
                fechas.Count > 0 ? Fmt(fechas[0]) : "", fechas.Count > 0 ? Fmt(fechas[^1]) : "", ct);
            return Ok(new { cantidad = res.Movimientos.Count, ruta });
        }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar el TXT: " + ex.Message }); }
    }
}
