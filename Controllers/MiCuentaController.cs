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

    public MiCuentaController(
        BasCuentaCorrienteService ctaCte,
        BasClientesService clientes,
        BasComprobantesService comprobantes)
    {
        _ctaCte = ctaCte;
        _clientes = clientes;
        _comprobantes = comprobantes;
    }

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
    // Estado de cuenta del cliente logueado. El codigo sale del TOKEN.
    [HttpGet("cuenta-corriente")]
    public async Task<ActionResult> CuentaCorriente([FromQuery] string? fecha)
    {
        var esCliente = User.FindFirstValue("esCliente") == "true";
        var cod = User.FindFirstValue("codigoCliente");

        if (!esCliente || string.IsNullOrWhiteSpace(cod))
            return BadRequest(new { mensaje = "Tu usuario no tiene una cuenta corriente de cliente asociada." });

        var f = string.IsNullOrWhiteSpace(fecha)
            ? DateTime.Today.ToString("yyyy-MM-dd")
            : fecha.Trim();

        try
        {
            var estado = await _ctaCte.EstadoClienteAsync(cod, f);
            var comps = estado?.Comprobantes ?? new();

            var comprobantes = comps.Select(c => new
            {
                fecha = c.Fecha,
                tipo = c.TipoComprobante,
                prefijo = c.Prefijo,
                numero = c.Numero,
                comprobante = $"{c.Prefijo}-{c.Numero}",
                vencimiento = c.Vencimientos.FirstOrDefault()?.FechaVencimiento,
                cuotas = c.Vencimientos.Count,
                total = c.TotalCtaCte,
                saldo = c.Saldo
            });

            return Ok(new
            {
                cliente = cod,
                fechaActualizacion = estado?.FechaActualizacion,
                totalSaldo = comps.Sum(c => c.Saldo),
                comprobantes
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { mensaje = "No se pudo consultar la cuenta corriente en BAS: " + ex.Message });
        }
    }

    // GET /api/mi-cuenta/comprobante?tipo=&prefijo=&numero=
    // Detalle (items) de UN comprobante. Verifica que sea del cliente logueado.
    [HttpGet("comprobante")]
    public async Task<ActionResult> Comprobante(
        [FromQuery] string tipo, [FromQuery] string prefijo, [FromQuery] string numero)
    {
        var esCliente = User.FindFirstValue("esCliente") == "true";
        var cod = (User.FindFirstValue("codigoCliente") ?? "").Trim();

        if (!esCliente || string.IsNullOrWhiteSpace(cod))
            return BadRequest(new { mensaje = "Tu usuario no tiene comprobantes de cliente asociados." });

        if (string.IsNullOrWhiteSpace(tipo) || string.IsNullOrWhiteSpace(prefijo) || string.IsNullOrWhiteSpace(numero))
            return BadRequest(new { mensaje = "Faltan datos del comprobante." });

        try
        {
            var c = await _comprobantes.ConsultaVentaAsync(tipo, prefijo, numero);
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
