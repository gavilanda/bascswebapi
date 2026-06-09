namespace PortalClientes.Auth;

// Valores de la seccion "JwtPortal" de appsettings. Configuran el token
// que emite el propio portal (distinto del token de BAS CS).
public class JwtPortalOptions
{
    public const string Seccion = "JwtPortal";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ClaveSecreta { get; set; } = string.Empty;
    public int ExpiraMinutos { get; set; } = 60;
}
