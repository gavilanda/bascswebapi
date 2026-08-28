using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Auth;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// Alta de listas de precios en BAS desde la planilla madre (función interna).
//
// El operador arma los precios en "Precios Mostrador BARK.xlsx" y hoy los carga
// a mano en BAS, uno por uno. Esta función lee la planilla, muestra QUÉ CAMBIA
// contra los precios vigentes, y da de alta sólo eso.
//
// Dos cosas a tener en cuenta:
//   * La planilla se SUBE desde el navegador, no se lee de una ruta del
//     servidor. Así cada uno la toma de donde la tenga —incluida una carpeta de
//     red— con sus propios permisos de Windows; el servicio no vería esos
//     recursos.
//   * No se manda la lista entera: BAS resuelve el precio vigente por ítem, así
//     que alcanza con los que cambian. Verificado contra BARKTEST.
//
// El alta es una acción deliberada: primero se previsualiza, se tilda lo que se
// quiere mandar y recién ahí se confirma, eligiendo el destino (BARKTEST para
// probar, BARK para producción).
[ApiController]
[Route("api/listas-precios")]
[Authorize]
public class ListasPreciosController : ControllerBase
{
    private readonly BasListasPreciosService _precios;
    private readonly PlanillaPreciosService _planilla;
    private readonly AccesoFuncionesService _acceso;

    public ListasPreciosController(BasListasPreciosService precios,
                                   PlanillaPreciosService planilla,
                                   AccesoFuncionesService acceso)
    {
        _precios = precios;
        _planilla = planilla;
        _acceso = acceso;
    }

    // De la planilla salen: mostrador (004) y mayorista (029). Además, la 003 se genera
    // IGUAL que la 004 (mismo precio ítem por ítem).
    private const string ListaMostrador = "004";
    private const string Lista003 = "003";        // espejo de la 004 (mismo precio)
    private const string ListaMayorista = "029";

    private bool EsInterno() => User.FindFirstValue("tipo") == "Interno";

    private async Task<ActionResult?> SinAccesoAsync(CancellationToken ct)
        => (EsInterno() && await _acceso.PuedeUsarAsync("listasprecios", User, ct))
            ? null
            : StatusCode(403, new { mensaje = "No tenés permiso para dar de alta listas de precios." });

    private static DateOnly? ParsearFecha(string? txt)
    {
        var s = (txt ?? "").Trim();
        if (s.Length == 0) return null;
        foreach (var f in new[] { "yyyy-MM-dd", "dd/MM/yyyy", "dd/MM/yy", "ddMMyyyy", "ddMMyy" })
            if (DateOnly.TryParseExact(s, f, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d)) return d;
        return null;
    }

    /// <summary>
    /// Lee la planilla subida y devuelve, por lista, sólo los precios que
    /// difieren de lo vigente en BAS. No escribe nada.
    /// </summary>
    [HttpPost("previsualizar")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<ActionResult> Previsualizar([FromForm] IFormFile archivo,
                                                  [FromForm] string? destino,
                                                  [FromForm] string? vigencia,
                                                  CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        if (archivo is null || archivo.Length == 0)
            return BadRequest(new { mensaje = "No llegó ninguna planilla." });

        var baseBas = string.IsNullOrWhiteSpace(destino) ? "BARKTEST" : destino.Trim();
        try
        {
            PlanillaPreciosService.Lectura lectura;
            using (var st = archivo.OpenReadStream())
                lectura = _planilla.Leer(st);

            if (lectura.Renglones.Count == 0)
                return StatusCode(409, new { mensaje = "La planilla no tiene renglones con código y precio." });

            var codigos = lectura.Renglones.Select(r => r.Codigo).ToList();
            // Se compara contra lo vigente A LA FECHA DE VIGENCIA elegida (no hoy): si la lista
            // ya se cargó para esa fecha (ej. desde otra PC), no la vuelve a marcar como cambio.
            var fechaComp = ParsearFecha(vigencia);   // null = hoy
            var vigentes = await _precios.VigentesAsync(
                baseBas, new[] { ListaMostrador, Lista003, ListaMayorista }, codigos, fechaComp, ct);

            // Lista COMPLETA a generar (una entrada por ítem y lista), con el precio resuelto:
            //   celda con valor (incl. 0) -> ese precio            (origen "nuevo" / "cero")
            //   celda vacía + hay anterior -> el precio anterior   (origen "anterior")
            //   celda vacía + sin anterior -> no se genera
            var items = new List<object>();
            int cambian = 0;

            decimal? Vigente(string lista, string codigo)
                => vigentes.TryGetValue(lista, out var d) && d.TryGetValue(codigo, out var p) ? p : null;

            // Resuelve el precio a generar de una celda para una lista. gen=false = no se genera.
            (bool gen, decimal precio, string origen) Resolver(decimal? celda, decimal? actual)
            {
                if (celda is not null) return (true, celda.Value, celda.Value == 0m ? "cero" : "nuevo");
                if (actual is not null) return (true, actual.Value, "anterior");
                return (false, 0m, "");
            }

            void Emitir(string lista, PlanillaPreciosService.Renglon r, decimal? actual, decimal precio, string origen)
            {
                bool cambia = actual is null || Math.Abs(actual.Value - precio) >= 0.005m;
                if (cambia) cambian++;
                items.Add(new
                {
                    lista,
                    codigo = r.Codigo,
                    descripcion = r.Descripcion,
                    fila = r.Fila,
                    actual,
                    precio,
                    origen,
                    cambia,
                    esAlta = actual is null,
                });
            }

            foreach (var r in lectura.Renglones)
            {
                // MOSTRADOR -> 004, y 003 = IGUAL que la 004 (mismo precio; comparada contra su
                // propio vigente para saber si cambia).
                var m = Resolver(r.Mostrador, Vigente(ListaMostrador, r.Codigo));
                if (m.gen)
                {
                    Emitir(ListaMostrador, r, Vigente(ListaMostrador, r.Codigo), m.precio, m.origen);
                    Emitir(Lista003, r, Vigente(Lista003, r.Codigo), m.precio, m.origen);
                }

                // MAYORISTA -> 029
                var y = Resolver(r.Mayorista, Vigente(ListaMayorista, r.Codigo));
                if (y.gen)
                    Emitir(ListaMayorista, r, Vigente(ListaMayorista, r.Codigo), y.precio, y.origen);
            }

            return Ok(new
            {
                hoja = lectura.Hoja,
                destino = baseBas,
                vigenciaSugerida = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
                renglones = lectura.Renglones.Count,
                avisos = lectura.Avisos,
                cambian,
                items,
            });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Cancelado." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo procesar: " + ex.Message }); }
    }

    public sealed class AltaRequest
    {
        public string? Destino { get; set; }
        public string? Vigencia { get; set; }
        public string? Observaciones { get; set; }
        /// <summary>Qué mandar, como "lista|código|precio" (ej. "004|4230|17330").</summary>
        public List<string>? Seleccion { get; set; }
    }

    private sealed record ResultadoLista(string Lista, int Items, bool Ok, string Mensaje);

    /// <summary>Crea la vigencia nueva en BAS con los ítems seleccionados.</summary>
    [HttpPost("alta")]
    public async Task<ActionResult> Alta([FromBody] AltaRequest req, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;

        var baseBas = string.IsNullOrWhiteSpace(req?.Destino) ? "BARKTEST" : req!.Destino!.Trim();
        var vig = ParsearFecha(req?.Vigencia);
        if (vig is null)
            return BadRequest(new { mensaje = "Falta la fecha de vigencia o no se entiende." });
        if (req?.Seleccion is null || req.Seleccion.Count == 0)
            return BadRequest(new { mensaje = "No hay ningún precio seleccionado." });

        // Agrupado por lista: BAS pide un alta por lista.
        var porLista = new Dictionary<string, List<BasListasPreciosService.ItemAlta>>();
        foreach (var s in req.Seleccion)
        {
            var p = (s ?? "").Split('|');
            if (p.Length != 3) continue;
            if (!decimal.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var precio))
                continue;
            if (precio < 0) continue;   // se permite 0 (celda en 0 = precio 0); no negativos
            if (!porLista.TryGetValue(p[0], out var l))
                porLista[p[0]] = l = new List<BasListasPreciosService.ItemAlta>();
            l.Add(new BasListasPreciosService.ItemAlta(p[1], precio));
        }
        if (porLista.Count == 0)
            return BadRequest(new { mensaje = "La selección no tiene ningún precio válido." });

        var obs = string.IsNullOrWhiteSpace(req.Observaciones)
            ? $"Alta desde planilla ({User.Identity?.Name})"
            : req.Observaciones!.Trim();

        var resultados = new List<ResultadoLista>();
        foreach (var par in porLista.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            try
            {
                await _precios.CrearListaAsync(baseBas, par.Key, vig.Value, par.Value, obs, ct);
                resultados.Add(new ResultadoLista(par.Key, par.Value.Count, true, "creada"));
            }
            catch (Exception ex)
            {
                resultados.Add(new ResultadoLista(par.Key, par.Value.Count, false, ex.Message));
            }
        }

        return Ok(new
        {
            ok = resultados.All(r => r.Ok),
            destino = baseBas,
            vigencia = vig.Value.ToString("dd/MM/yyyy"),
            resultados = resultados.Select(r => new
            {
                lista = r.Lista,
                items = r.Items,
                ok = r.Ok,
                mensaje = r.Mensaje,
            }),
        });
    }
}
