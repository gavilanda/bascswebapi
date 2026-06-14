using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

[ApiController]
[Route("api/mi-cuenta")]
[Authorize] // Requiere un token valido del portal.
public class MiCuentaController : ControllerBase
{
    private readonly BasCuentaCorrienteService _ctaCte;
    private readonly BasClientesService _clientes;
    private readonly BasComprobantesService _comprobantes;
    private readonly BasDestinosService _destinos;

    public MiCuentaController(
        BasCuentaCorrienteService ctaCte,
        BasClientesService clientes,
        BasComprobantesService comprobantes,
        BasDestinosService destinos)
    {
        _ctaCte = ctaCte;
        _clientes = clientes;
        _comprobantes = comprobantes;
        _destinos = destinos;
    }

    // Bases que se consolidan en el portal del cliente: las ACTIVAS marcadas para
    // incluir en el portal (configurable por base desde la intranet, sin hardcodear).
    private IReadOnlyList<string> PortalBases()
        => _destinos.Nombres
            .Where(n => _destinos.Config(n) is { Activa: true, IncluirEnPortal: true })
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // GET /api/mi-cuenta/perfil  -> datos del token.
    [HttpGet("perfil")]
    public ActionResult Perfil()
    {
        return Ok(new
        {
            identificador = User.FindFirstValue("identificador"),
            tipo = User.FindFirstValue("tipo"),
            esCliente = User.FindFirstValue("esCliente"),
            esProveedor = User.FindFirstValue("esProveedor"),
            codigoCliente = User.FindFirstValue("codigoCliente"),
            codigoProveedor = User.FindFirstValue("codigoProveedor")
        });
    }

    // GET /api/mi-cuenta/datos  -> datos del cliente (desde BAS, por su CUIT).
    [HttpGet("datos")]
    public async Task<ActionResult> Datos()
    {
        var esCliente = User.FindFirstValue("esCliente") == "true";
        var cuit = User.FindFirstValue("identificador");
        var codigo = User.FindFirstValue("codigoCliente");

        if (!esCliente || string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { mensaje = "Tu usuario no tiene datos de cliente asociados." });

        try
        {
            var c = await _clientes.BuscarPorCuitAsync(cuit);
            if (c is null)
                return NotFound(new { mensaje = "No se encontraron tus datos en BAS." });

            var dom = c.Domicilios.FirstOrDefault();
            object? domicilio = dom is null ? null : new
            {
                calle = string.Join(" ", new[] { dom.Domicilio1, dom.Domicilio2 }
                            .Where(x => !string.IsNullOrWhiteSpace(x))),
                localidad = dom.Localidad,
                codigoPostal = dom.CodigoPostal,
                provincia = dom.Provincia,
                telefono = dom.Telefono
            };

            return Ok(new
            {
                razonSocial = c.RazonSocial,
                nombreFantasia = c.NombreFantasia,
                cuit = string.IsNullOrWhiteSpace(c.NumeroImpositivo1) ? cuit : c.NumeroImpositivo1,
                codigoCliente = string.IsNullOrWhiteSpace(codigo) ? c.Codigo : codigo,
                email = c.Email,
                paginaWeb = c.PaginaWeb,
                condicionImpositiva = c.TratImpositivo,
                domicilio
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { mensaje = "No se pudieron consultar tus datos en BAS: " + ex.Message });
        }
    }

    // GET /api/mi-cuenta/cuenta-corriente?fecha=yyyy-MM-dd (fecha opcional)
    // Estado de cuenta CONSOLIDADO del cliente logueado: trae la cuenta corriente
    // de cada base (BARK + PRUEBAB), resolviendo el codigo del cliente por CUIT en
    // cada una, y devuelve los movimientos mergeados y ordenados por fecha, cada
    // uno etiquetado con su base. El saldo de cada comprobante es sumable, asi que
    // el total y la columna acumulada combinan ambas bases naturalmente.
    [HttpGet("cuenta-corriente")]
    public async Task<ActionResult> CuentaCorriente([FromQuery] string? fecha)
    {
        var esCliente = User.FindFirstValue("esCliente") == "true";
        var cuit = User.FindFirstValue("identificador");

        if (!esCliente || string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { mensaje = "Tu usuario no tiene una cuenta corriente de cliente asociada." });

        var f = string.IsNullOrWhiteSpace(fecha)
            ? DateTime.Today.ToString("yyyy-MM-dd")
            : fecha.Trim();

        // Traemos cada base en paralelo, con error aislado por base.
        var resultados = await Task.WhenAll(
            PortalBases().Select(b => TraerEstadoBaseAsync(b, cuit!, f)));

        var comprobantes = resultados
            .SelectMany(r => r.Comprobantes)
            .OrderBy(c => c.OrdenFecha)
            .ThenBy(c => c.Base, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Numero)
            .Select(c => new
            {
                baseNombre = c.Base,
                fecha = c.Fecha,
                tipo = c.Tipo,
                prefijo = c.Prefijo,
                numero = c.Numero,
                comprobante = $"{c.Prefijo}-{c.Numero}",
                vencimiento = c.Vencimiento,
                cuotas = c.Cuotas,
                total = c.Total,
                saldo = c.Saldo
            })
            .ToList();

        return Ok(new
        {
            totalSaldo = resultados.Sum(r => r.TotalSaldo),
            fechaActualizacion = resultados
                .Where(r => !string.IsNullOrWhiteSpace(r.FechaActualizacion))
                .Select(r => r.FechaActualizacion)
                .LastOrDefault(),
            bases = resultados.Select(r => new { baseNombre = r.Base, ok = r.Ok, error = r.Error }),
            comprobantes
        });
    }

    // Trae el estado de cuenta de UNA base, resolviendo el codigo del cliente por
    // CUIT en esa base. Nunca lanza: ante error devuelve el resultado marcado con
    // Ok=false y el mensaje, para no tumbar toda la consulta si una base falla.
    private async Task<EstadoBaseResultado> TraerEstadoBaseAsync(string baseNombre, string cuit, string fecha)
    {
        try
        {
            var cliente = await _clientes.BuscarPorCuitEnBaseAsync(baseNombre, cuit);
            if (cliente is null || string.IsNullOrWhiteSpace(cliente.Codigo))
                // El cliente no existe en esta base: no es error, simplemente no aporta movimientos.
                return new EstadoBaseResultado(baseNombre, true, null, 0m, null, new());

            var estado = await _ctaCte.EstadoClienteEnBaseAsync(baseNombre, cliente.Codigo, fecha);
            var comps = estado?.Comprobantes ?? new();

            var lista = comps.Select(c => new CompCtaCte(
                baseNombre,
                ParseFecha(c.Fecha),
                c.Fecha,
                c.TipoComprobante,
                c.Prefijo,
                c.Numero,
                c.Vencimientos.FirstOrDefault()?.FechaVencimiento,
                c.Vencimientos.Count,
                c.TotalCtaCte,
                c.Saldo)).ToList();

            return new EstadoBaseResultado(
                baseNombre, true, null, comps.Sum(c => c.Saldo), estado?.FechaActualizacion, lista);
        }
        catch (Exception ex)
        {
            return new EstadoBaseResultado(baseNombre, false, ex.Message, 0m, null, new());
        }
    }

    private static DateTime ParseFecha(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : DateTime.MaxValue;

    private sealed record CompCtaCte(
        string Base, DateTime OrdenFecha, string? Fecha, string? Tipo, string? Prefijo,
        int Numero, string? Vencimiento, int Cuotas, decimal Total, decimal Saldo);

    private sealed record EstadoBaseResultado(
        string Base, bool Ok, string? Error, decimal TotalSaldo, string? FechaActualizacion,
        List<CompCtaCte> Comprobantes);

    // GET /api/mi-cuenta/comprobante?tipo=&prefijo=&numero=&base=
    // Detalle (items) de UN comprobante en la base indicada. Verifica que sea del
    // cliente logueado (segun su codigo EN ESA base).
    [HttpGet("comprobante")]
    public async Task<ActionResult> Comprobante(
        [FromQuery] string tipo, [FromQuery] string prefijo, [FromQuery] string numero,
        [FromQuery(Name = "base")] string? baseNombre)
    {
        var esCliente = User.FindFirstValue("esCliente") == "true";
        var cuit = User.FindFirstValue("identificador");

        if (!esCliente || string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { mensaje = "Tu usuario no tiene comprobantes de cliente asociados." });

        if (string.IsNullOrWhiteSpace(tipo) || string.IsNullOrWhiteSpace(prefijo) || string.IsNullOrWhiteSpace(numero))
            return BadRequest(new { mensaje = "Faltan datos del comprobante." });

        var bn = PortalBases().FirstOrDefault(b =>
            string.Equals(b, (baseNombre ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (bn is null)
            return BadRequest(new { mensaje = "Base inválida." });

        try
        {
            // Codigo del cliente EN esa base, para el control de acceso.
            var cliente = await _clientes.BuscarPorCuitEnBaseAsync(bn, cuit!);
            var cod = (cliente?.Codigo ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cod))
                return StatusCode(403, new { mensaje = "No tenés cuenta en esta base." });

            var c = await _comprobantes.ConsultaVentaEnBaseAsync(bn, tipo, prefijo, numero);
            if (c is null)
                return NotFound(new { mensaje = "No se encontró el comprobante." });

            // Control de acceso: el comprobante tiene que ser de este cliente.
            var dueno = (c.CodClienteCtaCte ?? "").Trim();
            if (!string.Equals(dueno, cod, StringComparison.OrdinalIgnoreCase))
                return StatusCode(403, new { mensaje = "Este comprobante no corresponde a tu cuenta." });

            var items = c.LineasItem.Select(l => new
            {
                descripcion = l.Item,
                cantidad = l.Cantidad1,
                unidad = l.NroUnimed,
                precio = l.Precio,
                bonificacion = l.Bonificacion,
                iva = l.ImpIva,
                importe = l.Importe
            });

            // Resumen de impuestos: BAS los guarda por renglón, así que sumamos.
            var impuestos = new
            {
                netoGravado = c.LineasItem.Sum(l => l.ImpGravado),
                iva = c.LineasItem.Sum(l => l.ImpIva),
                ivaAdicional = c.LineasItem.Sum(l => l.ImpIvaNoi),
                percepcionIva = c.LineasItem.Sum(l => l.ImpPerIva),
                percepcionIibb = c.LineasItem.Sum(l => l.ImpPerIbr),
                internos = c.LineasItem.Sum(l => l.ImpInterno)
            };

            return Ok(new
            {
                baseNombre = bn,
                tipo = c.CodCmp,
                prefijo = c.Prefijo,
                numero = c.Numero,
                comprobante = $"{c.CodCmp} {c.Prefijo}-{c.Numero}".Trim(),
                fecha = c.Fecha,
                moneda = c.MonedaCtaCte,
                observacion = c.Observacion,
                impuestos,
                total = c.Total,
                saldo = c.Saldo,
                items
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { mensaje = "No se pudo consultar el comprobante en BAS: " + ex.Message });
        }
    }
}
