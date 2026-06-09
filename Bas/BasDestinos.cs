namespace PortalClientes.Bas;

// Configuración de los destinos BAS (BARK, PRUEBAB, ...). Las credenciales
// son compartidas (viven en BasWebApi); cada destino sólo aporta su URL y,
// para el grabado futuro, su Empresa/Sucursal.
public class DestinoBas
{
    public string BaseUrl { get; set; } = "";
    public int Empresa { get; set; } = 1;
    public int Sucursal { get; set; } = 1;
}

public static class BasDestinosConfig
{
    public const string Seccion = "BasDestinos";
}
