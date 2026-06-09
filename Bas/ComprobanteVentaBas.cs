namespace PortalClientes.Bas;

// Detalle de un comprobante de venta de BAS (lo que devuelve ConsultaComprobanteVenta).
public class ComprobanteVentaBas
{
    public int Nrotrans { get; set; }
    public string? CodCmp { get; set; }              // tipo de comprobante
    public string? Prefijo { get; set; }
    public int Numero { get; set; }
    public string? Fecha { get; set; }
    public decimal Total { get; set; }
    public string? CodClienteCtaCte { get; set; }    // dueño del comprobante (control de acceso)
    public string? MonedaCtaCte { get; set; }
    public string? Observacion { get; set; }
    public decimal Saldo { get; set; }
    public List<LineaItemBas> LineasItem { get; set; } = new();
}

public class LineaItemBas
{
    public int Secuencia { get; set; }
    public string? Item { get; set; }                // descripción del ítem
    public decimal Cantidad1 { get; set; }
    public string? NroUnimed { get; set; }           // unidad de medida
    public decimal Precio { get; set; }
    public decimal Bonificacion { get; set; }
    public decimal Importe { get; set; }

    // Impuestos por renglón (BAS los guarda a nivel línea, no en una cabecera).
    public decimal ImpGravado { get; set; }          // neto gravado
    public decimal ImpIva { get; set; }              // IVA
    public decimal ImpIvaNoi { get; set; }           // IVA no inscripto / adicional
    public decimal ImpPerIva { get; set; }           // percepción de IVA
    public decimal ImpPerIbr { get; set; }           // percepción de IIBB
    public decimal ImpInterno { get; set; }          // impuestos internos
}
