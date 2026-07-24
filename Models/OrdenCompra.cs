namespace PortalClientes.Models;

// Estado de una orden de compra. Sin "Conformado": Borrador editable -> Grabada en BAS.
public enum EstadoOrdenCompra
{
    Borrador,   // se edita libremente
    Grabada     // ya se grabó en BAS (tiene Nrotrans/número asignado); no se edita más
}

// Orden de compra al proveedor. Espejo (más simple) del pre-remito de compra: cabecera +
// renglones, editable como borrador y luego grabada en BAS vía POST /api/OrdenesCompra.
// Al grabar, BAS asigna un NÚMERO INTERNO (Nrotrans / IdTransaccion) que guardamos: es la
// REFERENCIA que después usan los remitos/facturas de ingreso que nazcan de esta OC.
public class OrdenCompra
{
    public int Id { get; set; }

    // Proveedor (elegido del padrón de BAS). Comprador y CondicionCompra vienen del proveedor.
    public string ProveedorCodigo { get; set; } = "";
    public string? ProveedorRazonSocial { get; set; }
    public string? Comprador { get; set; }
    public string? CondicionCompra { get; set; }

    public DateTime Fecha { get; set; } = DateTime.Today;
    public DateTime? FechaExpiracion { get; set; }   // validez de la OC (campo propio de la OC)
    public int CodigoMoneda { get; set; } = 1;       // moneda (1 = peso, por defecto)

    public string? Observaciones { get; set; }
    public string? ObservacionEntrega { get; set; }

    public EstadoOrdenCompra Estado { get; set; } = EstadoOrdenCompra.Borrador;

    // Base BAS a la que se grabó (o se va a grabar).
    public string? DestinoBase { get; set; }

    // ---- Asignado por BAS al GRABAR (referencia para remitos/facturas) ----
    public long? NrotransBas { get; set; }           // IdTransaccion: número interno de BAS
    public string? PrefijoBas { get; set; }
    public string? NumeroBas { get; set; }
    public string? MensajeError { get; set; }         // si el último grabado falló

    // ---- Auditoría ----
    public string CreadoPor { get; set; } = "";
    public DateTime CreadoEn { get; set; } = DateTime.Now;
    public string? ModificadoPor { get; set; }
    public DateTime? ModificadoEn { get; set; }
    public string? GrabadoPor { get; set; }
    public DateTime? GrabadoEn { get; set; }

    // Concurrencia optimista (igual que PreRemito).
    public Guid RowVersion { get; set; } = Guid.NewGuid();

    public List<OrdenCompraLinea> Lineas { get; set; } = new();
}

// Renglón de la OC. Mismos datos que el renglón de ingreso (producto + cantidad + precio),
// que mapean 1:1 a los Items de la OrdenCompra de BAS.
public class OrdenCompraLinea
{
    public int Id { get; set; }
    public int OrdenCompraId { get; set; }

    public string ProductoCodigo { get; set; } = "";
    public string? Descripcion { get; set; }   // guardada al elegir, para mostrar sin re-consultar
    public decimal Cantidad { get; set; }
    public string? Unidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal TasaIva { get; set; }
    public string? Observacion { get; set; }

    public OrdenCompra? OrdenCompra { get; set; }
}
