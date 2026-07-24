namespace PortalClientes.Bas;

// Configuración de los destinos BAS (BARK, PRUEBAB, ...). Las credenciales
// son compartidas (viven en BasWebApi); cada destino aporta su URL, su
// Empresa/Sucursal y los parámetros del comprobante de ingreso.
//
// Esta clase es la "vista en memoria" de la config de cada base. La tabla
// ConfiguracionesBase es la FUENTE DE VERDAD: al arranque se arma desde la tabla
// (appsettings sólo siembra las filas que falten la primera vez) e incluye TODO,
// conexión incluida (BaseUrl). El grabado de ingresos y las consultas leen de acá,
// así que editar la tabla se refleja sin reiniciar.
public class DestinoBas
{
    // ---- Conexión (editable desde la tabla / intranet) ----
    public string BaseUrl { get; set; } = "";

    // ---- Presentación / habilitación (editables desde la tabla) ----
    // Nombre amigable para mostrar en la UI.
    public string? Descripcion { get; set; }
    // Si está inactiva, no se ofrece para cargar ingresos ni se puede grabar.
    public bool Activa { get; set; } = true;

    // Si la base se incluye en la cuenta corriente CONSOLIDADA del portal de clientes.
    public bool IncluirEnPortal { get; set; } = false;

    // ---- Parámetros de negocio (editables desde la tabla) ----
    public int Empresa { get; set; } = 1;
    public int Sucursal { get; set; } = 1;

    // ---- Parámetros del remito de ingreso ----
    // Valores por defecto que coinciden con el talonario que usamos hoy; se
    // pueden pisar por destino en appsettings o desde la tabla de configuración.

    // Prefijo del comprobante. En BAS, el prefijo "1" corresponde al talonario 15,
    // cuya numeración es automática (no enviamos Numero, lo asigna BAS).
    public string RemitoPrefijo { get; set; } = "1";
    // Tipo de comprobante del talonario de ingreso. "N" = ingreso de compra directo
    // (confirmado contra BAS: el "S" pedía un remito de egreso de contrapartida).
    // Editable desde la tabla (forma parte de la conexión del destino).
    public string RemitoTipo { get; set; } = "N";
    // Concepto del comprobante: "com" = compra.
    public string RemitoConcepto { get; set; } = "com";
    // Depósito donde ingresa la mercadería.
    public int RemitoDeposito { get; set; } = 1;

    // ---- Parámetros de la factura de compra ----
    // Mismos parámetros de negocio que el remito (prefijo, concepto y depósito).
    // El resto del schema se sumará cuando definamos la API de factura de BAS.
    public string FacturaPrefijo { get; set; } = "1";
    public string FacturaConcepto { get; set; } = "com";
    public int FacturaDeposito { get; set; } = 1;
    // Cuenta contable de compras (se manda como ImputacionContable al grabar factura).
    public long FacturaImputacionContable { get; set; } = 21001001;

    // Prefijo del talonario de Orden de Compra (numeración automática de BAS).
    public string OrdenCompraPrefijo { get; set; } = "1";
}

public static class BasDestinosConfig
{
    public const string Seccion = "BasDestinos";
}
