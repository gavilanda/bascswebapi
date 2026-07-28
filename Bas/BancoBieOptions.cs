using PortalClientes.Models;

namespace PortalClientes.Bas;

// Configuración COMPARTIDA de la API de echeqs del Banco Credicoop (BIE). Lo que es
// propio de cada empresa (client_id, adherente, CBU, PEM, entorno) vive por base en
// ConfiguracionBase; acá sólo lo común a todas: scopes, tipo/concepto por defecto, y
// las URLs de cada entorno (homologación / producción). Sección "BancoBie" de appsettings.
public class BancoBieOptions
{
    public const string Seccion = "BancoBie";

    public string Scopes { get; set; } = "cuentas beneficiarioEcheq echeqConFirma consultaCbuCvuAlias";
    public string TipoCheque { get; set; } = "ECHD";   // ECHD diferido | ECHC común
    public string Concepto { get; set; } = "VAR";       // código de la tabla de conceptos del banco

    public EntornoUrls Homologacion { get; set; } = new();
    public EntornoUrls Produccion { get; set; } = new();

    public class EntornoUrls
    {
        public string BaseUrl { get; set; } = "";
        public string TokenUrl { get; set; } = "";
    }

    private EntornoUrls UrlsDe(string? entorno)
        => string.Equals(entorno, "produccion", StringComparison.OrdinalIgnoreCase)
            ? Produccion : Homologacion;

    // Arma las credenciales efectivas de una empresa combinando lo compartido (URLs del
    // entorno + scopes/tipo/concepto) con lo propio de la base. Devuelve null si la base
    // no tiene la emisión por API habilitada o le falta algún dato (incluida la PEM en disco).
    public BieCredenciales? Credenciales(ConfiguracionBase cb)
    {
        if (cb is null || !cb.BieHabilitado) return null;
        var urls = UrlsDe(cb.BieEntorno);
        if (string.IsNullOrWhiteSpace(urls.BaseUrl) || string.IsNullOrWhiteSpace(urls.TokenUrl))
            return null;
        if (string.IsNullOrWhiteSpace(cb.BieClientId) || cb.BieNumeroAdherente <= 0
            || string.IsNullOrWhiteSpace(cb.BieCbuDebito)
            || string.IsNullOrWhiteSpace(cb.BiePemPath) || !File.Exists(cb.BiePemPath))
            return null;

        return new BieCredenciales(
            cb.Nombre, urls.BaseUrl, urls.TokenUrl, Scopes,
            cb.BieClientId.Trim(), cb.BieNumeroAdherente, cb.BieCbuDebito.Trim(),
            cb.BiePemPath.Trim(), TipoCheque, Concepto);
    }
}

// Credenciales efectivas de UNA empresa para operar contra el banco. Todo lo que los
// servicios necesitan en una sola pieza (no dependen del DbContext).
public sealed record BieCredenciales(
    string BaseNombre, string BaseUrl, string TokenUrl, string Scopes,
    string ClientId, long NumeroAdherente, string CbuDebito, string PemPath,
    string TipoCheque, string Concepto);
