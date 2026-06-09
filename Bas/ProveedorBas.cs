namespace PortalClientes.Bas;

// Campos del Proveedor de BAS que usamos. Mismo criterio que ClienteBas.
public class ProveedorBas
{
    public string Codigo { get; set; } = string.Empty;       // Codigo de proveedor
    public string RazonSocial { get; set; } = string.Empty;
    public string? NumeroImpositivoTipo { get; set; }
    public string? NumeroImpositivo1 { get; set; }            // el CUIT
}
