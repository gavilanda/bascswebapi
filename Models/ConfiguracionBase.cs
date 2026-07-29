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
    // Prefijo del talonario de Orden de Compra (numeración automática de BAS, como Remito).
    public string OrdenCompraPrefijo { get; set; } = "1";

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

    // Últimos filtros de e-cheques usados, POR BASE (antes vivían sólo en el navegador).
    // Se guardan al "Preparar" desde el portal y precargan el formulario al elegir la
    // empresa, así viajan entre PCs/navegadores. Las fechas NO se guardan (cambian siempre).
    public string EchBanco { get; set; } = "";
    public string EchChequera { get; set; } = "";
    public string EchPrefijo { get; set; } = "";
    public bool EchUsaPrefijo { get; set; } = false;

    // Fecha de corte de la emisión por API (yyyy-MM-dd), POR EMPRESA. Red de seguridad para el
    // arranque: la API NO deja emitir cheques con fecha de carga ANTERIOR a esta fecha (se
    // asumen ya subidos por Excel al banco). Vacío = sin corte. Lo setea el admin una vez en el
    // editor de bases (no se autoajusta). No afecta al .xls, sólo a la emisión por API.
    public string EchApiDesde { get; set; } = "";

    // ---- Emisión de echeqs por API del Banco Credicoop (BIE) — POR EMPRESA ----
    // Cada empresa (BARK, XARDO) tiene su propio adherente, credenciales y clave privada.
    // Lo compartido (host, realm) vive en appsettings sección BancoBie; acá va lo propio
    // de la empresa. El host/realm se elige con BieEntorno (homologacion|produccion), así
    // se puede homologar una empresa y dejar la otra en producción, independientes.
    // Vacío / BieHabilitado=false = emisión por API deshabilitada para esta base.
    public bool BieHabilitado { get; set; } = false;
    public string BieEntorno { get; set; } = "homologacion";   // "homologacion" | "produccion"
    public string BieClientId { get; set; } = "";
    public long BieNumeroAdherente { get; set; }
    public string BieCbuDebito { get; set; } = "";
    // Ruta al archivo .pem con la clave privada de ESTA empresa. Archivo protegido en el
    // servidor del portal (ej. C:\Agente\PortalData\pem\bark.pem), fuera de git. En la base
    // sólo se guarda la ruta (la clave privada NO vive en la DB).
    public string BiePemPath { get; set; } = "";

    // Mapa Nº de cuenta bancaria (el que el portal usa contra Credicoop) -> código INTERNO
    // de esa cuenta en BAS (el que espera la pantalla de conciliación, ej. "011"). BAS no
    // conoce el Nº del banco, así que la traducción se configura acá, POR EMPRESA. Texto
    // multilínea, una línea por cuenta:  <nroCuentaBanco>=<codigoEnBas>  (ej. 00440199559=011).
    // El export de conciliación lo vuelca al .info (clave cuentaBas) para el macro de BAS.
    public string CuentasBas { get; set; } = "";

    // RED DE SEGURIDAD para la conciliación: texto que DEBE aparecer en el título de la ventana
    // de BAS para confirmar que está en ESTA empresa/entorno antes de importar (BAS permite
    // cambiar de empresa; si el operador quedó en otra, importaríamos en la equivocada). El
    // título de BAS es del tipo "BASCS XE ... (bark-Bark/SA - 1:Bark S.A. ...)"; acá va la marca
    // inequívoca, ej. "bark-Bark" (prod) o "bark-Test" (testeo). Se vuelca al .info (tituloBas);
    // si no coincide con el título real, la macro ABORTA sin importar nada.
    public string TituloBas { get; set; } = "";
}
