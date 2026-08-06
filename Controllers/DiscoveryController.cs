using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Auth;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// Precios a Discovery (función interna).
//
// Discovery es el punto de venta; sus listas de precios se actualizan importando
// un TXT. Esta función arma ese archivo con los precios de BAS que pasaron a regir
// desde una fecha:
//
//   Lista 1 de Discovery  <-  lista 004 de BAS (Mostrador CF), precio tal cual
//   Lista 2 de Discovery  <-  lista 029 de BAS (Distrib.Fabr.RI) + IVA, redondeado
//
// Sólo bienes: los servicios (fletes, etc.) no van a Discovery.
//
// El archivo se DESCARGA (no se guarda en el servidor): así cada uno lo deja donde
// quiera —incluida una carpeta de red— con sus propios permisos. El servicio corre
// como LocalSystem y no suele ver los recursos de red, así que guardarlo del lado
// del servidor sería un problema. Del lado del navegador se usa showSaveFilePicker,
// que recuerda la última carpeta usada.
//
// Después hay que importarlo en Discovery (Archivos > Listas de precios > Carga
// manual > Importación de lotes) y aplicarlo desde "Actualización de precios".
[ApiController]
[Route("api/discovery")]
[Authorize]
public class DiscoveryController : ControllerBase
{
    private readonly BasListasPreciosService _precios;
    private readonly AccesoFuncionesService _acceso;

    public DiscoveryController(BasListasPreciosService precios, AccesoFuncionesService acceso)
    {
        _precios = precios;
        _acceso = acceso;
    }

    // Esto aplica sólo a BARK: es la base cuyos precios usa Discovery.
    private const string BaseBas = "BARK";
    private const string ListaBasMostrador = "004";   // ya viene con IVA
    private const string ListaBasDistrib = "029";     // sin IVA -> se le suma
    private const decimal Iva = 1.21m;

    private bool EsInterno() => User.FindFirstValue("tipo") == "Interno";

    private async Task<ActionResult?> SinAccesoAsync(CancellationToken ct)
        => (EsInterno() && await _acceso.PuedeUsarAsync("discovery", User, ct))
            ? null
            : StatusCode(403, new { mensaje = "No tenés permiso para usar Precios a Discovery." });

    private static DateOnly? ParsearFecha(string? txt)
    {
        var s = (txt ?? "").Trim();
        if (s.Length == 0) return null;
        foreach (var f in new[] { "yyyy-MM-dd", "dd/MM/yyyy", "dd/MM/yy", "ddMMyyyy", "ddMMyy" })
            if (DateOnly.TryParseExact(s, f, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d)) return d;
        return null;
    }

    // Precio final para Discovery según de qué lista de BAS venga.
    private static (string listaDiscovery, decimal precioFinal) Convertir(string listaBas, decimal precio)
        => listaBas == ListaBasMostrador
            ? ("1", precio)                                                   // ya es final
            : ("2", DiscoveryTxtBuilder.RedondearComercial(precio * Iva));    // + IVA, redondeado

    private async Task<List<DiscoveryTxtBuilder.Linea>> ArmarLineasAsync(DateOnly desde, CancellationToken ct)
    {
        var listas = new[] { ListaBasMostrador, ListaBasDistrib };
        var precios = await _precios.PreciosDesdeAsync(BaseBas, listas, desde, ct);
        var lineas = new List<DiscoveryTxtBuilder.Linea>(precios.Count);
        foreach (var p in precios)
        {
            var (listaDisc, final) = Convertir(p.Lista, p.Precio);
            lineas.Add(new DiscoveryTxtBuilder.Linea(
                listaDisc, p.Codigo, p.Descripcion, p.Precio, final, p.Vigencia));
        }
        return lineas.OrderBy(l => l.ListaDiscovery, StringComparer.Ordinal)
                     .ThenBy(l => l.Codigo, StringComparer.Ordinal)
                     .ToList();
    }

    /// <summary>
    /// Qué se exportaría con esa fecha, sin escribir nada. Sirve para mostrar
    /// el detalle antes de generar.
    /// </summary>
    [HttpGet("previsualizar")]
    public async Task<ActionResult> Previsualizar([FromQuery] string? desde, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var d = ParsearFecha(desde) ?? DateOnly.FromDateTime(DateTime.Today);
        try
        {
            var lineas = await ArmarLineasAsync(d, ct);
            var largos = lineas.Where(l => l.Codigo.Length > DiscoveryTxtBuilder.AnchoCodigo)
                               .Select(l => l.Codigo).ToList();
            return Ok(new
            {
                desde = d.ToString("dd/MM/yyyy"),
                total = lineas.Count,
                lista1 = lineas.Count(l => l.ListaDiscovery == "1"),
                lista2 = lineas.Count(l => l.ListaDiscovery == "2"),
                codigosLargos = largos,
                filas = lineas.Take(300).Select(l => new
                {
                    lista = l.ListaDiscovery,
                    codigo = l.Codigo,
                    descripcion = l.Descripcion,
                    precioBas = l.PrecioOriginal,
                    precio = l.PrecioFinal,
                    vigencia = l.Vigencia.ToString("dd/MM/yyyy")
                })
            });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Consulta cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudieron leer los precios de BAS: " + ex.Message }); }
    }

    public sealed class GenerarRequest
    {
        public string? Desde { get; set; }
        /// <summary>Qué exportar, como "lista|código" (ej. "1|5568"). Vacío = todo.</summary>
        public List<string>? Seleccion { get; set; }
    }

    /// <summary>
    /// Devuelve el contenido de los TXT, UNO POR LISTA (LIS1_ddmmaaaa.txt y
    /// LIS2_ddmmaaaa.txt). Si una lista no tiene nada marcado, su archivo no viene.
    /// Si viene `Seleccion`, exporta sólo esos ítems (sirve para probar con dos o
    /// tres antes de mandar la lista entera).
    ///
    /// No devuelve el archivo como descarga: manda el texto y el front lo escribe
    /// en la carpeta que el usuario elige, así los dos van al mismo lado de una.
    /// </summary>
    [HttpPost("generar")]
    public async Task<IActionResult> Generar([FromBody] GenerarRequest req, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var d = ParsearFecha(req?.Desde) ?? DateOnly.FromDateTime(DateTime.Today);
        try
        {
            var lineas = await ArmarLineasAsync(d, ct);

            var sel = req?.Seleccion;
            if (sel is { Count: > 0 })
            {
                var claves = new HashSet<string>(sel, StringComparer.OrdinalIgnoreCase);
                lineas = lineas.Where(l => claves.Contains(l.ListaDiscovery + "|" + l.Codigo)).ToList();
            }
            if (lineas.Count == 0)
                return StatusCode(409, new { mensaje = "No hay nada para exportar con esos filtros." });

            var archivos = new List<object>();
            var descartados = new List<string>();
            var total = 0;

            foreach (var g in lineas.GroupBy(l => l.ListaDiscovery)
                                    .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var bytes = DiscoveryTxtBuilder.Armar(g, out var desc);
                descartados.AddRange(desc);
                if (bytes.Length == 0) continue;   // toda la lista quedó afuera

                var cant = g.Count() - desc.Count;
                total += cant;
                archivos.Add(new
                {
                    lista = g.Key,
                    nombre = $"LIS{g.Key}_{d:ddMMyyyy}.txt",
                    cantidad = cant,
                    contenido = System.Text.Encoding.UTF8.GetString(bytes)
                });
            }

            if (archivos.Count == 0)
                return StatusCode(409, new { mensaje = "Ningún ítem pudo escribirse (códigos demasiado largos)." });

            return Ok(new { archivos, cantidad = total, descartados });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Generación cancelada." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar el archivo: " + ex.Message }); }
    }
}
