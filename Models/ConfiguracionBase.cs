namespace PortalClientes.Models;

// Configuración por base BAS, editable desde la intranet (sección "Configuración
// de bases"). Persiste los parámetros de NEGOCIO con los que se graban los
// comprobantes en esa base.
//
// La CONEXIÓN (nombre + BaseUrl) NO vive acá: sigue en appsettings, porque define
// los HttpClients que se arman al arranque. El Nombre de esta fila coincide con la
// clave del destino en appsettings (ej. "BARK"); es la llave para cruzar la fila
// con el DestinoBas que vive en memoria y que consume el grabado.
public class ConfiguracionBase
{
    public int Id { get; set; }

    // Nombre de la base BAS (= clave del destino en appsettings). No editable.
    public string Nombre { get; set; } = "";

    // Nombre amigable para mostrar en la UI (ej. "BARK · Producción").
    public string? Descripcion { get; set; }

    // Si está inactiva, no aparece para cargar nuevos ingresos ni se puede grabar
    // contra ella. Pensado para deshabilitar bases de prueba (BARKTEST) en producción.
    public bool Activa { get; set; } = true;

    // ---- Parámetros de negocio ----
    public int Empresa { get; set; } = 1;
    public int Sucursal { get; set; } = 1;

    // Remito de ingreso. (El "Tipo" del talonario —hoy "N"— NO es editable: queda
    // fijo en la config del destino.)
    public string RemitoPrefijo { get; set; } = "1";
    public string RemitoConcepto { get; set; } = "com";
    public int RemitoDeposito { get; set; } = 1;

    // Factura de compra. Mismos parámetros de negocio que el remito (prefijo,
    // concepto y depósito); el resto del schema se completa cuando esté la API.
    public string FacturaPrefijo { get; set; } = "1";
    public string FacturaConcepto { get; set; } = "com";
    public int FacturaDeposito { get; set; } = 1;
}
