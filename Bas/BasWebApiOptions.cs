namespace PortalClientes.Bas;

// Valores de la seccion "BasWebApi" de la configuracion: como llegar a
// BAS CS WebAPI, con que credencial autenticarse, y la empresa/sucursal por
// defecto para las consultas que las requieren (ej: cuenta corriente).
public class BasWebApiOptions
{
    public const string Seccion = "BasWebApi";

    public string BaseUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = "api";
    public string ClientSecret { get; set; } = "secret";
    public string Usuario { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    // Empresa y sucursal por defecto para las consultas de cuenta corriente.
    public int Empresa { get; set; } = 1;
    public int Sucursal { get; set; } = 0;
}
