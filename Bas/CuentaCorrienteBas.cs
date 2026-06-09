namespace PortalClientes.Bas;

// Estado de cuenta corriente de un cliente (lo que devuelve
// GET /api/EstadoCtaCteCliente). Solo los campos que usamos.
public class EstadoCtaCteBas
{
    public string? Cliente { get; set; }
    public string? FechaDesde { get; set; }
    public string? FechaActualizacion { get; set; }
    public List<ComprobanteCtaCteBas> Comprobantes { get; set; } = new();
}

// Cada comprobante del estado de cuenta.
public class ComprobanteCtaCteBas
{
    public string? Fecha { get; set; }
    public string? TipoComprobante { get; set; }
    public string? Prefijo { get; set; }
    public int Numero { get; set; }
    public decimal TotalCtaCte { get; set; }
    public decimal Saldo { get; set; }

    // BAS lo manda como texto (no como numero), por eso string.
    public string? Moneda { get; set; }

    // Vencimientos (cuotas) del comprobante.
    public List<VencimientoComprobanteBas> Vencimientos { get; set; } = new();
}

// Cada vencimiento/cuota dentro de un comprobante.
public class VencimientoComprobanteBas
{
    public string? FechaVencimiento { get; set; }
    public decimal Importe { get; set; }
    public decimal Saldo { get; set; }
}
