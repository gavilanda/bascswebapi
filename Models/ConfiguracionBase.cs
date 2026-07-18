namespace PortalClientes.Models;

// Configuración por base BAS, editable desde la intranet (sección "Configuración
// de bases"). Persiste TODA la definición de la base: conexión (nombre + BaseUrl +
// tipo de talonario) y parámetros de negocio. La tabla es la FUENTE DE VERDAD: al
// arrancar, el diccionario de DestinoBas en memoria se arma desde acá (appsettings
// sólo siembra las filas que falten la primera vez). El Nombre es la clave del
// destino.
public class ConfiguracionBase
{
    public int Id { get; set; }

    // Nombre de la base BAS (= clave del destino). Se define al crear; no se renombra.
    public string Nombre { get; set; } = "";

    // Nombre amigable para mostrar en la UI (ej. "BARK · Producción").
    public string? Descripcion { get; set; }

    // Si está inactiva, no aparece para cargar nuevos ingresos ni se puede grabar
    // contra ella. Pensado para deshabilitar bases de prueba (BARKTEST) en producción.
    public bool Activa { get; set; } = true;

    // ---- Conexión (ahora editable desde la intranet) ----
    // URL base del WebAPI de esa base BAS (ej. http://localhost:5081).
    public string BaseUrl { get; set; } = "";
    // Tipo de comprobante del talonario de ingreso ("N" = ingreso de compra directo).
    public string RemitoTipo { get; set; } = "N";
    // Incluir esta base en la cuenta corriente CONSOLIDADA del portal de clientes.
    public bool IncluirEnPortal { get; set; } = false;

    // ---- Parámetros de negocio ----
    public int Empresa { get; set; } = 1;
    public int Sucursal { get; set; } = 1;

    // Remito de ingreso. (El "Tipo" del talonario se edita en la conexión, arriba.)
    public string RemitoPrefijo { get; set; } = "1";
    public string RemitoConcepto { get; set; } = "com";
    public int RemitoDeposito { get; set; } = 1;

    // Factura de compra. Mismos parámetros de negocio que el remito (prefijo,
    // concepto y depósito); el resto del schema se completa cuando esté la API.
    public string FacturaPrefijo { get; set; } = "1";
    public string FacturaConcepto { get; set; } = "com";
    public int FacturaDeposito { get; set; } = 1;
    // Cuenta contable a la que se imputa la compra (se manda como ImputacionContable
    // a BAS al grabar la factura). FK a dbo.CUENTAS.
    public long FacturaImputacionContable { get; set; } = 21001001;

    // ---- E-Cheques (consulta directa al SQL Server de la base) ----
    // La función "echeques" NO va por el WebAPI (BAS no expone esa consulta): se conecta
    // directo al SQL Server de la base. Servidor + base de datos van acá (editables); el
    // usuario/clave de SOLO LECTURA van por variable de entorno (Echeques__SqlUser /
    // Echeques__SqlPassword), nunca en la base ni en el front. Vacío = función deshabilitada
    // para esta base.
    public string SqlServidor { get; set; } = "";
    public string SqlBase { get; set; } = "";
    // Usuario/clave de SOLO LECTURA (ej. portal_consultas) para el SQL directo. Si quedan
    // vacíos, se cae a la variable de entorno SqlConsultas__User / SqlConsultas__Password.
    // ⚠️ La clave queda en texto plano en la base del portal; sólo la ve el editor de bases
    // (admin). Es read-only, pero igual: no exponerla en endpoints de usuarios comunes.
    public string SqlUsuario { get; set; } = "";
    public string SqlClave { get; set; } = "";
    // Mail "propio" de la empresa a EXCLUIR de la consulta de cheques (en BARK:
    // pagos@bark-sa.com.ar). Por base porque difiere entre empresas.
    public string SqlEmailPropio { get; set; } = "";
}
