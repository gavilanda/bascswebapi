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

// E-Cheques (función interna). Dos caminos sobre las MISMAS filas que se sacan del SQL de
// BAS (BAS no expone esto por WebAPI → SQL directo):
//   1) EXPORTAR el .xls que se sube a mano al banco (camino histórico, se mantiene).
//   2) EMITIR por API contra el Banco Credicoop (nuevo). La firma queda "Enviada a la
//      firma" (se completa en Banca Internet Empresa).
[ApiController]
[Route("api/echeques")]
[Authorize]
public class EchequesController : ControllerBase
{
    private readonly BasEchequesService _echeques;
    private readonly BancoBieEcheqService _bie;
    private readonly BancoBieOptions _bieOpt;
    private readonly PortalDbContext _db;
    private readonly AccesoFuncionesService _acceso;

    public EchequesController(
        BasEchequesService echeques, BancoBieEcheqService bie, IOptions<BancoBieOptions> bieOpt,
        PortalDbContext db, AccesoFuncionesService acceso)
    {
        _echeques = echeques;
        _bie = bie;
        _bieOpt = bieOpt.Value;
        _db = db;
        _acceso = acceso;
    }

    private bool EsInterno() => User.FindFirstValue("tipo") == "Interno";

    // Candado real: interno + asignado a la función "echeques". Devuelve el 403 o null.
    private async Task<ActionResult?> SinAccesoAsync(CancellationToken ct)
        => (EsInterno() && await _acceso.PuedeUsarAsync("echeques", User, ct))
            ? null
            : StatusCode(403, new { mensaje = "No tenés permiso para usar E-Cheques." });

    // Credenciales de la empresa (por base) para operar con el banco, o null si esa base
    // no tiene la emisión por API configurada/habilitada.
    private async Task<BieCredenciales?> CredsAsync(string baseNombre, CancellationToken ct)
    {
        var cb = await _db.ConfiguracionesBase.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Nombre == baseNombre, ct);
        return cb is null ? null : _bieOpt.Credenciales(cb);
    }

    // GET /api/echeques/bases -> bases con la config SQL completa + cuáles tienen ADEMÁS la
    // emisión por API configurada (por empresa).
    [HttpGet("bases")]
    public async Task<ActionResult> Bases(CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var bases = await _echeques.BasesConfiguradasAsync(ct);
        var cfgs = await _db.ConfiguracionesBase.AsNoTracking()
            .Where(c => bases.Contains(c.Nombre)).ToListAsync(ct);
        var basesApi = cfgs.Where(c => _bieOpt.Credenciales(c) is not null)
            .Select(c => c.Nombre).ToList();
        // Filtros guardados por base (banco/chequera/prefijo) para precargar el formulario.
        var defaults = cfgs.ToDictionary(c => c.Nombre, c => (object)new
        {
            banco = c.EchBanco, chequera = c.EchChequera, prefijo = c.EchPrefijo, usaPrefijo = c.EchUsaPrefijo
        });
        // Aviso de vencimiento de credenciales del banco: para cada empresa con API, si su
        // BieVencimiento está a <=30 días o ya venció, se manda un aviso (el front lo muestra
        // al entrar a E-Cheques). Así se renueva a tiempo y no se corta la emisión.
        var hoy = DateOnly.FromDateTime(DateTime.Today);
        var avisos = new List<string>();
        foreach (var c in cfgs.Where(c => _bieOpt.Credenciales(c) is not null))
        {
            if (!DateOnly.TryParse(c.BieVencimiento, CultureInfo.InvariantCulture, DateTimeStyles.None, out var venc))
                continue;
            var dias = venc.DayNumber - hoy.DayNumber;
            if (dias < 0)
                avisos.Add($"{c.Nombre}: las credenciales del banco VENCIERON el {venc:dd/MM/yyyy}. La emisión por API va a fallar hasta que las renueves.");
            else if (dias <= 30)
                avisos.Add($"{c.Nombre}: las credenciales del banco vencen el {venc:dd/MM/yyyy} (faltan {dias} día{(dias == 1 ? "" : "s")}). Renovalas para no cortar la emisión.");
        }
        return Ok(new { bases, basesApi, defaults, avisos });
    }

    private sealed record Params(string Base, DateOnly D, DateOnly H, string Banco, string Chequera, string ChqD, string ChqH);

    // Valida y normaliza los parámetros comunes a todos los endpoints. Devuelve el error (o null).
    private ActionResult? Parsear(string? baseNombre, string? desde, string? hasta, string? banco,
        string? chequera, string? chqDesde, string? chqHasta, out Params? p)
    {
        p = null;
        if (!DateOnly.TryParse(desde, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
            !DateOnly.TryParse(hasta, CultureInfo.InvariantCulture, DateTimeStyles.None, out var h))
            return BadRequest(new { mensaje = "Fechas inválidas (usá yyyy-MM-dd)." });
        if (h < d)
            return BadRequest(new { mensaje = "La fecha 'Desde' no puede ser mayor que 'Hasta'." });

        baseNombre = (baseNombre ?? "").Trim();
        banco = (banco ?? "").Trim();
        chequera = (chequera ?? "").Trim();
        if (baseNombre.Length == 0 || banco.Length == 0 || chequera.Length == 0)
            return BadRequest(new { mensaje = "Faltan datos: base, banco y chequera son obligatorios." });

        var cd = (chqDesde ?? "").Trim();
        var chh = (chqHasta ?? "").Trim();
        if ((cd.Length > 0) != (chh.Length > 0))
            return BadRequest(new { mensaje = "Completá ambos números de cheque (Desde y Hasta) o dejá los dos vacíos." });

        p = new Params(baseNombre, d, h, banco, chequera, cd, chh);
        return null;
    }

    // GET /api/echeques/lista?base=&desde=&hasta=&banco=&chequera= -> e-cheques EMITIDOS de la
    // cuenta (del BANCO, con su estado) en el rango. banco/chequera son OPCIONALES: si vienen,
    // se completa beneficiario/CUIT best-effort desde BAS (por numeroCheque).
    [HttpGet("lista")]
    public async Task<ActionResult> Lista(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        if (!DateOnly.TryParse(desde, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
            !DateOnly.TryParse(hasta, CultureInfo.InvariantCulture, DateTimeStyles.None, out var h))
            return BadRequest(new { mensaje = "Fechas inválidas (usá yyyy-MM-dd)." });
        if (h < d) return BadRequest(new { mensaje = "La fecha 'Desde' no puede ser mayor que 'Hasta'." });
        var b = (baseNombre ?? "").Trim();
        if (b.Length == 0) return BadRequest(new { mensaje = "Elegí una empresa." });
        var creds = await CredsAsync(b, ct);
        if (creds is null) return StatusCode(409, new { mensaje = $"La empresa '{b}' no tiene la API del banco configurada." });
        try
        {
            var cheques = await _bie.ListarGeneradosAsync(creds, d, h, ct);
            // Beneficiario/CUIT desde BAS (best-effort): sólo si vinieron banco+chequera.
            var bco = (banco ?? "").Trim(); var chq = (chequera ?? "").Trim();
            if (cheques.Count > 0 && bco.Length > 0 && chq.Length > 0)
            {
                try
                {
                    var filas = await _echeques.ConsultarAsync(b, d, h, bco, chq, null, null, ct);
                    var mapa = filas.GroupBy(f => f.NumEcheq).ToDictionary(g => g.Key, g => g.First());
                    foreach (var e in cheques)
                        if (mapa.TryGetValue(e.NumeroCheque, out var f)) { e.Beneficiario = f.Beneficiario; e.Cuit = f.NroCuiCdi; }
                }
                catch { /* si BAS no responde, el listado igual sale (sin beneficiario) */ }
            }

            // Fecha de emisión REAL (la nuestra) para los cheques del banco: sale de la tabla local
            // por numeroCheque (independiente del rango — el banco los fecha por la FIRMA, que puede
            // ser otro día). La FechaEmision del banco queda como fecha de FIRMA en el front.
            if (cheques.Count > 0)
            {
                var nums = cheques.Select(c => c.NumeroCheque).ToList();
                var emis = await _db.EmisionesEcheq.AsNoTracking()
                    .Where(e => e.BaseNombre == b && nums.Contains(e.NumeroCheque))
                    .Select(e => new { e.NumeroCheque, e.EmitidoEn }).ToListAsync(ct);
                var mapEmi = emis.GroupBy(e => e.NumeroCheque).ToDictionary(g => g.Key, g => g.Max(x => x.EmitidoEn));
                foreach (var c in cheques)
                    if (mapEmi.TryGetValue(c.NumeroCheque, out var fe))
                        c.FechaEmisionReal = fe.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }

            // Sumar lo EMITIDO por el portal (tabla local) que todavía NO figura en el banco
            // (operaciones "Enviada a la firma": lista-cheques no las trae hasta firmarlas). Se
            // marcan con estado "(local)" para distinguirlas de las generadas en el banco.
            var enBanco = cheques.Select(c => c.NumeroCheque).ToHashSet();
            var dStart = d.ToDateTime(TimeOnly.MinValue);
            var hEnd = h.AddDays(1).ToDateTime(TimeOnly.MinValue);
            var locales = await _db.EmisionesEcheq.AsNoTracking()
                .Where(e => e.BaseNombre == b && e.EmitidoEn >= dStart && e.EmitidoEn < hEnd)
                .ToListAsync(ct);
            foreach (var e in locales)
            {
                if (enBanco.Contains(e.NumeroCheque)) continue;
                decimal.TryParse(e.Monto, NumberStyles.Any, CultureInfo.InvariantCulture, out var m);
                var estado = "Pendiente de firma";   // ya enviado al banco; falta firmarlo en BIE
                cheques.Add(new BancoBieEcheqService.EcheqGenerado(
                    e.NumeroCheque, e.IdCheque ?? "", estado, "",
                    "", e.FechaPago ?? "",   // FechaEmision (=Firma) vacía: aún no firmado en el banco
                    m, "ARS", "", "", "")
                { Beneficiario = e.Beneficiario ?? "", Cuit = e.Cuit ?? "",
                  FechaEmisionReal = e.EmitidoEn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) });
            }
            var orden = cheques.OrderBy(c => c.NumeroCheque).ToList();
            return Ok(new { cantidad = orden.Count, cheques = orden });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo traer el listado del banco: " + ex.Message }); }
    }

    // GET /api/echeques?... -> JSON (recuento/prueba)
    [HttpGet]
    public async Task<ActionResult> Consultar(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;

        try
        {
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);
            return Ok(new { cantidad = filas.Count });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo consultar e-cheques: " + ex.Message }); }
    }

    // GET /api/echeques/exportar?... -> devuelve el .xls (o JSON { cantidad:0 } si no hay registros).
    [HttpGet("exportar")]
    public async Task<ActionResult> Exportar(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;

        try
        {
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);
            if (filas.Count == 0) return Ok(new { cantidad = 0 });   // sin registros: el front avisa
            var bytes = BasEchequesService.ArmarXls(filas);
            Response.Headers["X-Echeques-Cantidad"] = filas.Count.ToString();
            return File(bytes, "application/vnd.ms-excel", "e-cheques_EXPORTACION.xls");
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar e-cheques: " + ex.Message }); }
    }

    // ---- Emisión por API (Banco Credicoop) ----

    // Una fila para el modal de preparación (cheque candidato, todavía NO emitido por API).
    private sealed record ChequeItem(long numeroCheque, string codProveedor, string beneficiario,
        string cuit, decimal importe, string fechaPago, string mail, bool emitibleApi, string? problemaApi);

    // Selección enviada desde el modal (números de cheque tildados) para emitir/exportar.
    public sealed record SeleccionRequest(long[]? Numeros);

    // Motivo por el que un cheque NO se puede emitir POR API (dato faltante o anterior al corte).
    // null = emitible. (No aplica al .xls: ahí se exporta igual.)
    private static string? ProblemaEmision(BasEchequesService.ChequeRow r, DateOnly? corteApi)
    {
        if (corteApi.HasValue && r.FechaCarga < corteApi.Value)
            return $"Anterior a la fecha de corte de API ({corteApi.Value:dd/MM/yyyy})";
        if (string.IsNullOrWhiteSpace(r.NroCuiCdi)) return "Sin CUIT del beneficiario";
        if (string.IsNullOrWhiteSpace(r.Mail)) return "Sin e-mail del beneficiario";
        if (r.Importe <= 0) return "Importe en cero";
        if (r.NumEcheq.ToString(CultureInfo.InvariantCulture).Length > 8) return "Nº de cheque > 8 dígitos";
        // Fecha de pago (vencimiento): el banco exige HOY o futura y dentro de ~360 días
        // (si no, rechaza al emitir con APIE-1022). La validamos acá para no mandarla.
        if (!DateOnly.TryParseExact(r.FechaPago, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fp))
            return "Fecha de pago inválida";
        var hoy = DateOnly.FromDateTime(DateTime.Today);
        // E-cheque DIFERIDO (ECHD): la fecha de pago debe ser POSTERIOR a hoy. Hoy o anterior
        // el banco lo rechaza (APIE-1022 "fecha de pago no válida").
        if (fp < hoy) return $"Fecha de pago vencida ({r.FechaPago})";
        if (fp == hoy) return $"Vence hoy ({r.FechaPago}): el e-cheque diferido requiere fecha futura";
        if (fp > hoy.AddDays(360)) return $"Fecha de pago a más de 360 días ({r.FechaPago})";
        return null;
    }

    // Fecha de corte de la emisión por API para una base (EchApiDesde). null = sin corte.
    private async Task<DateOnly?> CorteApiAsync(string baseNombre, CancellationToken ct)
    {
        var s = await _db.ConfiguracionesBase.AsNoTracking()
            .Where(c => c.Nombre == baseNombre).Select(c => c.EchApiDesde).FirstOrDefaultAsync(ct);
        return DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    // Números de cheque ya emitidos por API para una base (para excluirlos de todo).
    private async Task<HashSet<long>> YaEmitidosAsync(string baseNombre, CancellationToken ct)
        => (await _db.EmisionesEcheq.AsNoTracking()
                .Where(e => e.BaseNombre == baseNombre)
                .Select(e => e.NumeroCheque).ToListAsync(ct)).ToHashSet();

    // Guarda los últimos filtros de e-cheques de la base (banco/chequera/prefijo) si cambiaron.
    // Es lo que precarga el formulario al elegir la empresa. Sólo escribe si hubo cambios.
    private async Task RecordarFiltrosAsync(
        string baseNombre, string banco, string chequera, string? prefijo, bool usaPrefijo, CancellationToken ct)
    {
        var cb = await _db.ConfiguracionesBase.FirstOrDefaultAsync(c => c.Nombre == baseNombre, ct);
        if (cb is null) return;
        var pref = (prefijo ?? "").Trim();
        if (cb.EchBanco == banco && cb.EchChequera == chequera
            && cb.EchPrefijo == pref && cb.EchUsaPrefijo == usaPrefijo) return;
        cb.EchBanco = banco; cb.EchChequera = chequera;
        cb.EchPrefijo = pref; cb.EchUsaPrefijo = usaPrefijo;
        await _db.SaveChangesAsync(ct);
    }

    // GET /api/echeques/preparar?... -> cheques del rango que TODAVÍA NO se emitieron por API,
    // con el código de proveedor de BAS y si son emitibles por API. Sirve para el modal donde
    // el usuario elige cuáles y por qué canal (API o .xls). No requiere config de API (el .xls
    // anda igual); apiHabilitada indica si esta empresa además puede emitir por API.
    [HttpGet("preparar")]
    public async Task<ActionResult> Preparar(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta,
        [FromQuery] string? prefijo, [FromQuery] bool usaPrefijo = false, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;

        try
        {
            // Recordar los filtros usados para esta base (banco/chequera/prefijo): se guardan
            // solos y precargan el formulario la próxima vez (viajan entre PCs, no localStorage).
            await RecordarFiltrosAsync(p!.Base, p.Banco, p.Chequera, prefijo, usaPrefijo, ct);

            var creds = await CredsAsync(p!.Base, ct);
            var apiHab = creds is not null;
            var corte = await CorteApiAsync(p.Base, ct);
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);
            var yaSet = await YaEmitidosAsync(p.Base, ct);
            // Capa extra: números ya generados en el banco (Excel firmado o API), en el mismo
            // rango de la emisión + un margen. Se suman a los ya emitidos localmente (best-effort).
            if (creds is not null)
                yaSet.UnionWith(await _bie.NumerosGeneradosAsync(
                    creds, p.D.AddDays(-_bieOpt.MargenChequeoBancoDias), p.H.AddDays(_bieOpt.MargenChequeoBancoDias), ct));

            var cheques = filas.Where(f => !yaSet.Contains(f.NumEcheq)).Select(f =>
            {
                var prob = ProblemaEmision(f, corte);
                return new ChequeItem(f.NumEcheq, f.CodProveedor, f.Beneficiario, f.NroCuiCdi,
                    f.Importe, f.FechaPago, f.Mail, prob is null, prob);
            }).ToList();
            var yaEmitidosApi = filas.Count(f => yaSet.Contains(f.NumEcheq));

            return Ok(new { baseNombre = p.Base, apiHabilitada = apiHab, yaEmitidosApi, cheques });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo preparar: " + ex.Message }); }
    }

    // POST /api/echeques/exportar-sel?... (body { numeros }) -> .xls de los cheques SELECCIONADOS,
    // excluyendo los ya emitidos por API. (El .xls no valida CUIT/e-mail: exporta lo tildado.)
    [HttpPost("exportar-sel")]
    public async Task<ActionResult> ExportarSeleccion(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta,
        [FromBody] SeleccionRequest? sel, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;

        try
        {
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);
            var yaSet = await YaEmitidosAsync(p.Base, ct);
            var seleccion = sel?.Numeros?.ToHashSet();
            var elegidos = filas.Where(f => !yaSet.Contains(f.NumEcheq)
                && (seleccion is null || seleccion.Contains(f.NumEcheq))).ToList();

            if (elegidos.Count == 0) return Ok(new { cantidad = 0 });
            var bytes = BasEchequesService.ArmarXls(elegidos);
            Response.Headers["X-Echeques-Cantidad"] = elegidos.Count.ToString();
            return File(bytes, "application/vnd.ms-excel", "e-cheques_EXPORTACION.xls");
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Exportación cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar el .xls: " + ex.Message }); }
    }

    private sealed record ResultadoFront(long numeroCheque, string codProveedor, string beneficiario,
        decimal importe, bool ok, string? estado, long? idOperacion, string? error);

    // POST /api/echeques/emitir?... -> emite los cheques NUEVOS y emitibles del rango. Idempotente:
    // omite los ya emitidos. Devuelve el resultado por cheque.
    [HttpPost("emitir")]
    public async Task<ActionResult> Emitir(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta,
        [FromBody] SeleccionRequest? sel, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;
        var creds = await CredsAsync(p!.Base, ct);
        if (creds is null)
            return StatusCode(409, new { mensaje = $"La empresa '{p!.Base}' no tiene la emisión por API configurada." });

        var quien = User.FindFirstValue("identificador") ?? "?";
        try
        {
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);

            // Excluir los ya emitidos y los no emitibles (dato faltante); y quedarnos SÓLO con
            // los seleccionados en el modal (si vino selección; sin selección = todos los nuevos).
            var yaSet = await YaEmitidosAsync(p.Base, ct);
            // Capa extra contra el banco (mismo rango + margen): no re-emitir lo ya generado.
            yaSet.UnionWith(await _bie.NumerosGeneradosAsync(
                creds, p.D.AddDays(-_bieOpt.MargenChequeoBancoDias), p.H.AddDays(_bieOpt.MargenChequeoBancoDias), ct));
            var corte = await CorteApiAsync(p.Base, ct);
            var seleccion = sel?.Numeros?.ToHashSet();
            var aEmitir = filas.Where(f => !yaSet.Contains(f.NumEcheq) && ProblemaEmision(f, corte) is null
                && (seleccion is null || seleccion.Contains(f.NumEcheq))).ToList();

            if (aEmitir.Count == 0)
                return Ok(new { emitidos = 0, resultados = Array.Empty<ResultadoFront>(),
                    mensaje = "No hay cheques nuevos para emitir (ya emitidos, no seleccionados o con datos faltantes)." });

            // 1) Asegurar los beneficiarios en la agenda (idempotente).
            var fallasBenef = await _bie.AsegurarBeneficiariosAsync(creds, aEmitir.Select(f => f.NroCuiCdi), ct);

            // 2) Emitir uno por uno; persistir sólo los aceptados por el banco.
            var resultados = new List<ResultadoFront>();
            int okCount = 0;
            foreach (var f in aEmitir)
            {
                // Si el beneficiario no se pudo dar de alta, no intentamos emitir (fallaría con APIE-1020).
                if (fallasBenef.TryGetValue(f.NroCuiCdi, out var motivo))
                {
                    resultados.Add(new ResultadoFront(f.NumEcheq, f.CodProveedor, f.Beneficiario, f.Importe,
                        false, null, null, "Beneficiario: " + motivo));
                    continue;
                }

                var res = await _bie.EmitirAsync(creds, f, ct);
                if (res.Ok)
                {
                    try
                    {
                        _db.EmisionesEcheq.Add(new EmisionEcheq
                        {
                            BaseNombre = p.Base,
                            NumeroCheque = f.NumEcheq,
                            Cuit = f.NroCuiCdi,
                            Beneficiario = f.Beneficiario,
                            Monto = f.Importe.ToString("0.00", CultureInfo.InvariantCulture),
                            FechaPago = f.FechaPago,   // dd/MM/yyyy (informativo para el listado)
                            IdOrigen = res.IdOrigen,
                            IdOperacion = res.IdOperacion,
                            IdCheque = res.IdCheque,
                            Estado = res.Estado ?? "Aceptada",
                            EmitidoPor = quien,
                            EmitidoEn = DateTime.Now,
                        });
                        await _db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException)
                    {
                        // Choque con el índice único (alguien lo emitió en paralelo): lo damos por hecho.
                        _db.ChangeTracker.Clear();
                    }
                    okCount++;
                    resultados.Add(new ResultadoFront(f.NumEcheq, f.CodProveedor, f.Beneficiario, f.Importe,
                        true, res.Estado, res.IdOperacion, null));
                }
                else
                {
                    resultados.Add(new ResultadoFront(f.NumEcheq, f.CodProveedor, f.Beneficiario, f.Importe,
                        false, null, null, res.ErrorTexto));
                }
            }

            return Ok(new { emitidos = okCount, resultados });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Emisión cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo emitir: " + ex.Message }); }
    }

    // GET /api/echeques/emision-estado?base=&idOperacion= -> consulta al banco el estado de una emisión.
    [HttpGet("emision-estado")]
    public async Task<ActionResult> EmisionEstado(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] long idOperacion, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        if (idOperacion <= 0) return BadRequest(new { mensaje = "idOperacion inválido." });
        var creds = await CredsAsync((baseNombre ?? "").Trim(), ct);
        if (creds is null)
            return StatusCode(409, new { mensaje = $"La empresa '{baseNombre}' no tiene la emisión por API configurada." });

        try
        {
            var json = await _bie.ConsultarEmisionAsync(creds, idOperacion, ct);
            return Content(json ?? "{}", "application/json");
        }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo consultar la emisión: " + ex.Message }); }
    }
}
