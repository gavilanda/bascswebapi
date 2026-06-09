using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortalClientes.Bas;

namespace PortalClientes.Controllers;

// Consultas a BAS pensadas para la administracion (ej: resolver un CUIT).
// Solo para internos administradores.
[ApiController]
[Route("api/admin/bas")]
[Authorize(Policy = "Admin")]
public class BasAdminController : ControllerBase
{
    private readonly BasClientesService _clientes;
    private readonly BasProveedoresService _proveedores;

    public BasAdminController(BasClientesService clientes, BasProveedoresService proveedores)
    {
        _clientes = clientes;
        _proveedores = proveedores;
    }

    // GET /api/admin/bas/cliente-por-cuit?cuit=...
    // Busqueda directa por documento. Rapida.
    [HttpGet("cliente-por-cuit")]
    public async Task<ActionResult> ClientePorCuit([FromQuery] string cuit)
    {
        if (string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { mensaje = "Indica un CUIT." });

        try
        {
            var c = await _clientes.BuscarPorCuitAsync(cuit.Trim());
            if (c is null)
                return NotFound(new { mensaje = "No se encontro un cliente con ese CUIT en BAS." });

            return Ok(new { codigo = c.Codigo, razonSocial = c.RazonSocial });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { mensaje = "No se pudo consultar BAS: " + ex.Message });
        }
    }

    // GET /api/admin/bas/proveedor-por-cuit?cuit=...
    // Lista + filtra (no hay busqueda directa). Puede tardar.
    [HttpGet("proveedor-por-cuit")]
    public async Task<ActionResult> ProveedorPorCuit([FromQuery] string cuit)
    {
        if (string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { mensaje = "Indica un CUIT." });

        try
        {
            var p = await _proveedores.BuscarPorCuitAsync(cuit.Trim());
            if (p is null)
                return NotFound(new { mensaje = "No se encontro un proveedor con ese CUIT en BAS." });

            return Ok(new { codigo = p.Codigo, razonSocial = p.RazonSocial });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { mensaje = "No se pudo consultar BAS: " + ex.Message });
        }
    }
}
