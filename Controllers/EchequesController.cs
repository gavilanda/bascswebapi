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
        return Ok(new { bases, basesApi });
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

    // Info de una fila para el preview (qué se emitiría / qué ya se emitió).
    private sealed record PreviewFila(long NumeroCheque, string Beneficiario, string Cuit,
        decimal Importe, string FechaPago, string Mail, bool Emitible, string? Problema,
        string? Estado, long? IdOperacion, DateTime? EmitidoEn);

    // Motivo por el que un cheque NO se puede emitir (dato faltante). null = emitible.
    private static string? ProblemaEmision(BasEchequesService.ChequeRow r)
    {
        if (string.IsNullOrWhiteSpace(r.NroCuiCdi)) return "Sin CUIT del beneficiario";
        if (string.IsNullOrWhiteSpace(r.Mail)) return "Sin e-mail del beneficiario";
        if (r.Importe <= 0) return "Importe en cero";
        if (r.NumEcheq.ToString(CultureInfo.InvariantCulture).Length > 8) return "Nº de cheque > 8 dígitos";
        return null;
    }

    // GET /api/echeques/emitir-preview?... -> qué cheques son nuevos (emitibles o no) y cuáles ya se emitieron.
    [HttpGet("emitir-preview")]
    public async Task<ActionResult> EmitirPreview(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var err = Parsear(baseNombre, desde, hasta, banco, chequera, chqDesde, chqHasta, out var p);
        if (err is not null) return err;
        if (await CredsAsync(p!.Base, ct) is null)
            return StatusCode(409, new { mensaje = $"La empresa '{p!.Base}' no tiene la emisión por API configurada." });

        try
        {
            var filas = await _echeques.ConsultarAsync(p!.Base, p.D, p.H, p.Banco, p.Chequera, p.ChqD, p.ChqH, ct);

            // Ya emitidos: los que están en la tabla para esta base.
            var numeros = filas.Select(f => f.NumEcheq).ToList();
            var yaMap = await _db.EmisionesEcheq.AsNoTracking()
                .Where(e => e.BaseNombre == p.Base && numeros.Contains(e.NumeroCheque))
                .ToDictionaryAsync(e => e.NumeroCheque, ct);

            var nuevos = new List<PreviewFila>();
            var yaEmitidos = new List<PreviewFila>();
            foreach (var f in filas)
            {
                if (yaMap.TryGetValue(f.NumEcheq, out var em))
                    yaEmitidos.Add(new PreviewFila(f.NumEcheq, f.Beneficiario, f.NroCuiCdi, f.Importe,
                        f.FechaPago, f.Mail, false, null, em.Estado, em.IdOperacion, em.EmitidoEn));
                else
                {
                    var prob = ProblemaEmision(f);
                    nuevos.Add(new PreviewFila(f.NumEcheq, f.Beneficiario, f.NroCuiCdi, f.Importe,
                        f.FechaPago, f.Mail, prob is null, prob, null, null, null));
                }
            }
            return Ok(new { nuevos, yaEmitidos });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo preparar la emisión: " + ex.Message }); }
    }

    private sealed record ResultadoFront(long numeroCheque, bool ok, string? estado, long? idOperacion, string? error);

    // POST /api/echeques/emitir?... -> emite los cheques NUEVOS y emitibles del rango. Idempotente:
    // omite los ya emitidos. Devuelve el resultado por cheque.
    [HttpPost("emitir")]
    public async Task<ActionResult> Emitir(
        [FromQuery(Name = "base")] string? baseNombre, [FromQuery] string? desde, [FromQuery] string? hasta,
        [FromQuery] string? banco, [FromQuery] string? chequera,
        [FromQuery] string? chqDesde, [FromQuery] string? chqHasta, CancellationToken ct = default)
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

            // Excluir los ya emitidos y los no emitibles (dato faltante).
            var yaEmitidos = await _db.EmisionesEcheq.AsNoTracking()
                .Where(e => e.BaseNombre == p.Base)
                .Select(e => e.NumeroCheque).ToListAsync(ct);
            var yaSet = yaEmitidos.ToHashSet();
            var aEmitir = filas.Where(f => !yaSet.Contains(f.NumEcheq) && ProblemaEmision(f) is null).ToList();

            if (aEmitir.Count == 0)
                return Ok(new { emitidos = 0, resultados = Array.Empty<ResultadoFront>(),
                    mensaje = "No hay cheques nuevos para emitir (ya emitidos o con datos faltantes)." });

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
                    resultados.Add(new ResultadoFront(f.NumEcheq, false, null, null, "Beneficiario: " + motivo));
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
                    resultados.Add(new ResultadoFront(f.NumEcheq, true, res.Estado, res.IdOperacion, null));
                }
                else
                {
                    resultados.Add(new ResultadoFront(f.NumEcheq, false, null, null, res.ErrorTexto));
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
