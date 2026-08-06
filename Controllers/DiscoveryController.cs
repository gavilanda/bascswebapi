using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PortalClientes.Auth;
using PortalClientes.Bas;
using PortalClientes.Data;
using PortalClientes.Models;

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
// El archivo se guarda en una carpeta que elige el usuario; la última usada se
// recuerda y se ofrece la próxima vez. Después hay que importarlo en Discovery
// (Archivos > Listas de precios > Carga manual > Importación de lotes) y aplicarlo
// desde "Actualización de precios".
[ApiController]
[Route("api/discovery")]
[Authorize]
public class DiscoveryController : ControllerBase
{
    private readonly BasListasPreciosService _precios;
    private readonly PortalDbContext _db;
    private readonly AccesoFuncionesService _acceso;

    public DiscoveryController(BasListasPreciosService precios, PortalDbContext db,
                               AccesoFuncionesService acceso)
    {
        _precios = precios;
        _db = db;
        _acceso = acceso;
    }

    // Esto aplica sólo a BARK: es la base cuyos precios usa Discovery.
    private const string BaseBas = "BARK";
    private const string ListaBasMostrador = "004";   // ya viene con IVA
    private const string ListaBasDistrib = "029";     // sin IVA -> se le suma
    private const decimal Iva = 1.21m;
    private const string ClavePrefCarpeta = "discovery.carpeta";

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

    /// <summary>Última carpeta usada, para proponerla.</summary>
    [HttpGet("carpeta")]
    public async Task<ActionResult> Carpeta(CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var pref = await _db.Preferencias.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Clave == ClavePrefCarpeta, ct);
        return Ok(new { carpeta = pref?.Valor ?? "" });
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
        public string? Carpeta { get; set; }
        public string? Archivo { get; set; }
    }

    /// <summary>Genera el TXT y lo guarda en la carpeta indicada.</summary>
    [HttpPost("generar")]
    public async Task<ActionResult> Generar([FromBody] GenerarRequest req, CancellationToken ct = default)
    {
        var noAcc = await SinAccesoAsync(ct); if (noAcc is not null) return noAcc;
        var d = ParsearFecha(req?.Desde) ?? DateOnly.FromDateTime(DateTime.Today);

        var carpeta = (req?.Carpeta ?? "").Trim();
        if (carpeta.Length == 0)
            return BadRequest(new { mensaje = "Indicá la carpeta donde guardar el archivo." });

        var nombre = (req?.Archivo ?? "").Trim();
        if (nombre.Length == 0) nombre = $"PRECIOS_DISCOVERY_{d:yyyyMMdd}.txt";
        if (nombre.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return BadRequest(new { mensaje = "El nombre del archivo tiene caracteres no válidos." });

        try
        {
            var lineas = await ArmarLineasAsync(d, ct);
            if (lineas.Count == 0)
                return Ok(new { cantidad = 0, mensaje = "No hay precios con vigencia desde esa fecha." });

            var bytes = DiscoveryTxtBuilder.Armar(lineas, out var descartados);

            Directory.CreateDirectory(carpeta);
            var ruta = Path.Combine(carpeta, nombre);
            await System.IO.File.WriteAllBytesAsync(ruta, bytes, ct);

            // Se recuerda la carpeta para proponerla la próxima vez.
            var pref = await _db.Preferencias.FirstOrDefaultAsync(p => p.Clave == ClavePrefCarpeta, ct);
            if (pref is null)
                _db.Preferencias.Add(new PreferenciaPortal { Clave = ClavePrefCarpeta, Valor = carpeta });
            else { pref.Valor = carpeta; pref.Actualizado = DateTime.Now; }
            await _db.SaveChangesAsync(ct);

            return Ok(new
            {
                cantidad = lineas.Count - descartados.Count,
                lista1 = lineas.Count(l => l.ListaDiscovery == "1" && !descartados.Contains(l.Codigo)),
                lista2 = lineas.Count(l => l.ListaDiscovery == "2" && !descartados.Contains(l.Codigo)),
                descartados,
                ruta,
                desde = d.ToString("dd/MM/yyyy")
            });
        }
        catch (OperationCanceledException) { return StatusCode(499, new { mensaje = "Generación cancelada." }); }
        catch (UnauthorizedAccessException) { return StatusCode(409, new { mensaje = "No hay permiso para escribir en esa carpeta." }); }
        catch (DirectoryNotFoundException) { return StatusCode(409, new { mensaje = "La carpeta indicada no existe y no se pudo crear." }); }
        catch (Exception ex) { return StatusCode(502, new { mensaje = "No se pudo generar el archivo: " + ex.Message }); }
    }
}
