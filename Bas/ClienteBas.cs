namespace PortalClientes.Bas;

// Campos del Cliente de BAS que usamos. Para la busqueda por CUIT y para
// "Mis datos" del portal.
public class ClienteBas
{
    public string Codigo { get; set; } = string.Empty;       // Codigo de cliente
    public string RazonSocial { get; set; } = string.Empty;
    public string? NombreFantasia { get; set; }
    public string? Email { get; set; }
    public string? PaginaWeb { get; set; }
    public string? TratImpositivo { get; set; }               // codigo de tratamiento impositivo
    public string? NumeroImpositivoTipo { get; set; }
    public string? NumeroImpositivo1 { get; set; }            // el CUIT
    public List<DomicilioBas> Domicilios { get; set; } = new();
}

// Domicilio del cliente (solo los campos legibles que mostramos).
public class DomicilioBas
{
    public string? Descripcion { get; set; }
    public string? Domicilio1 { get; set; }
    public string? Domicilio2 { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Localidad { get; set; }
    public string? Provincia { get; set; }
    public string? Telefono { get; set; }
}
