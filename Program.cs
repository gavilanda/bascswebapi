using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PortalClientes.Auth;
using PortalClientes.Bas;
using PortalClientes.Data;
using PortalClientes.Models;

var builder = WebApplication.CreateBuilder(args);

// Permite que la app funcione como Servicio de Windows: le avisa al Administrador
// de Servicios (SCM) que arrancó (sin esto, SCM cree que nunca inició y reporta
// error aunque el proceso esté vivo) y fija el directorio base en la carpeta del
// .exe (para encontrar appsettings, wwwroot, etc.). Es NO-OP cuando se corre por
// consola (dotnet run / .exe a mano), así que no afecta el desarrollo.
builder.Host.UseWindowsService();

// ---- Configuracion del token del portal ----
builder.Services.Configure<JwtPortalOptions>(
    builder.Configuration.GetSection(JwtPortalOptions.Seccion));

// ---- Base de datos de usuarios del portal ----
// Si la ruta del archivo SQLite es RELATIVA, la anclamos a la carpeta del
// ejecutable (AppContext.BaseDirectory), NO a la carpeta de trabajo del proceso.
// Asi la base queda en el mismo lugar con `dotnet run` (carpeta del proyecto) y
// como Servicio de Windows (carpeta del .exe, ya que el servicio arranca con la
// carpeta de trabajo en System32). Si la ruta ya es absoluta, se respeta tal cual.
var connPortal = builder.Configuration.GetConnectionString("PortalDb")
                 ?? "Data Source=portal-clientes.db";
var csbPortal = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connPortal);
if (!string.IsNullOrWhiteSpace(csbPortal.DataSource)
    && !csbPortal.DataSource.StartsWith(":")            // no tocar :memory: u otros especiales
    && !Path.IsPathRooted(csbPortal.DataSource))
{
    csbPortal.DataSource = Path.Combine(AppContext.BaseDirectory, csbPortal.DataSource);
    connPortal = csbPortal.ConnectionString;
}
builder.Services.AddDbContext<PortalDbContext>(opciones =>
    opciones.UseSqlite(connPortal));

// ---- Servicios de autenticacion del portal ----
builder.Services.AddScoped<IPasswordHasher<UsuarioPortal>, PasswordHasher<UsuarioPortal>>();
builder.Services.AddScoped<GeneradorTokens>();

var jwt = builder.Configuration
    .GetSection(JwtPortalOptions.Seccion)
    .Get<JwtPortalOptions>()!;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opciones =>
    {
        opciones.MapInboundClaims = false;
        opciones.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.ClaveSecreta)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization(opciones =>
{
    // Admin: administra usuarios y asigna permisos.
    opciones.AddPolicy("Admin", p => p.RequireClaim("esAdmin", "true"));

    // Una policy por cada permiso funcional del catalogo. Asi un endpoint
    // se protege con [Authorize(Policy = Permisos.EditarRemitos)], etc.
    foreach (var permiso in Permisos.Codigos)
        opciones.AddPolicy(permiso, p => p.RequireClaim("permiso", permiso));

    // Ver ingresos: alcanza con tener cualquiera de los dos permisos.
    opciones.AddPolicy("VerRemitos", p =>
        p.RequireClaim("permiso", Permisos.EditarRemitos, Permisos.ConformarRemitos));
});

// ---- Cache en memoria (detalle de comprobantes, etc.) ----
builder.Services.AddMemoryCache();

// ---- Cliente de BAS CS WebAPI (base principal, para lecturas del portal) ----
builder.Services.Configure<BasWebApiOptions>(
    builder.Configuration.GetSection(BasWebApiOptions.Seccion));

var basBaseUrl = builder.Configuration[$"{BasWebApiOptions.Seccion}:BaseUrl"]
                 ?? "http://localhost:5081";
builder.Services.AddHttpClient("bas", c => c.BaseAddress = new Uri(basBaseUrl))
    // ConnectTimeout corto: una base caída/inalcanzable falla al conectar en ~8s en vez de
    // colgarse hasta el timeout total (así se muestra el aviso enseguida y las demás siguen).
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) });

builder.Services.AddSingleton<BasAuthService>();      // singleton: cachea el token
builder.Services.AddScoped<BasClientesService>();
builder.Services.AddScoped<BasProveedoresService>();
builder.Services.AddScoped<BasCuentaCorrienteService>();
builder.Services.AddScoped<BasComprobantesService>();
builder.Services.AddScoped<BasEstadisticasVentaService>();   // estadísticas de venta (multi-base)
builder.Services.AddScoped<BasEchequesService>();            // e-cheques (SQL directo a la base)
builder.Services.AddScoped<BasOrdenCompraService>();         // órdenes de compra (grabado a BAS)
builder.Services.AddScoped<BasListasPreciosService>();
builder.Services.AddScoped<PlanillaPreciosService>();       // listas de precios (export a Discovery)
builder.Services.AddScoped<PortalClientes.Auth.AccesoFuncionesService>();  // acceso por función (menú + endpoints)

// ---- Banco Credicoop (BIE): emisión de echeqs por API (multi-empresa) ----
// Auth OAuth2 client_credentials con JWT firmado (private_key_jwt), por empresa. El
// singleton cachea el token por client_id. Las URLs son absolutas (el host depende del
// entorno de cada empresa), así que el cliente NO fija BaseAddress. Lo compartido
// (scopes, URLs por entorno) va en la sección BancoBie; lo propio de cada empresa, por base.
builder.Services.Configure<BancoBieOptions>(
    builder.Configuration.GetSection(BancoBieOptions.Seccion));
builder.Services.AddHttpClient("bancobie", c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) });
builder.Services.AddSingleton<BiePayloadLogger>();           // captura request/response para homologación (config)
builder.Services.AddSingleton<BancoBieAuthService>();        // singleton: cachea token por client_id
builder.Services.AddScoped<BancoBieEcheqService>();
builder.Services.AddScoped<BancoBieCuentasService>();        // cuentas + movimientos (conciliación)
builder.Services.AddScoped<IcbcConciliacionService>();       // conciliación ICBC (import CSV)

// ---- Destinos BAS (BARK, PRUEBAB) para ingresos ----
// Timeout amplio para la carga del padrón (la carga es secuencial y en segundo
// plano; una base lenta puede tardar). La consulta EN VIVO se topea aparte,
// corto (8s), en BasResolucionService.
var basDestinos = new ConcurrentDictionary<string, DestinoBas>(
    builder.Configuration
        .GetSection(BasDestinosConfig.Seccion)
        .Get<Dictionary<string, DestinoBas>>() ?? new());
// Un único cliente multi-base, SIN BaseAddress fija: la URL de cada request se
// arma desde la BaseUrl (editable) de la base. Así se pueden dar de alta bases
// nuevas en runtime sin registrar clientes al arranque. Timeout amplio para la
// carga del padrón (la consulta en vivo se topea aparte, corto, por CancellationToken).
builder.Services.AddHttpClient("bas-multi", c => c.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) });
builder.Services.AddSingleton(basDestinos);
builder.Services.AddSingleton<BasDestinosService>();
builder.Services.AddScoped<BasResolucionService>();
builder.Services.AddScoped<BasRemitoIngresoService>();    // grabado de ingresos como Remito
builder.Services.AddScoped<BasFacturaIngresoService>();   // grabado de ingresos como Factura
builder.Services.AddScoped<BasPartidasService>();         // alta idempotente de partidas en BAS
builder.Services.AddScoped<ConfigBasesService>();         // config por base (editable)

// ---- Caché en memoria del padrón (productos + proveedores) de cada base ----
builder.Services.AddSingleton<BasCacheMaestros>();
builder.Services.AddSingleton<BasCacheRefresher>();
builder.Services.AddHostedService<BasCacheLoader>();   // calienta y refresca por detrás

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- Swagger con soporte para token Bearer ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opciones =>
{
    opciones.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Pega aca el token del portal (el campo 'token' que devuelve /api/auth/login)."
    });
    opciones.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ---- Crear la base y sembrar usuarios de prueba (solo desarrollo) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
    db.Database.EnsureCreated();

    // La tabla de auditoría se agregó después de la creación original de la base.
    // EnsureCreated no agrega tablas a una base que ya existe, así que la creamos
    // acá si falta. Es idempotente y no toca el resto de los datos.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""AuditoriaPreRemitos"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_AuditoriaPreRemitos"" PRIMARY KEY AUTOINCREMENT,
            ""PreRemitoId"" INTEGER NOT NULL,
            ""Evento"" TEXT NOT NULL,
            ""Usuario"" TEXT NOT NULL,
            ""FechaHora"" TEXT NOT NULL,
            ""ProveedorCodigo"" TEXT NULL,
            ""ProveedorRazonSocial"" TEXT NULL,
            ""ComprobanteFecha"" TEXT NULL,
            ""ComprobantePrefijo"" TEXT NULL,
            ""ComprobanteNumero"" INTEGER NULL,
            ""Estado"" TEXT NULL,
            ""Detalle"" TEXT NULL
        );");
    db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_AuditoriaPreRemitos_FechaHora"" ON ""AuditoriaPreRemitos"" (""FechaHora"");");
    db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_AuditoriaPreRemitos_Usuario"" ON ""AuditoriaPreRemitos"" (""Usuario"");");
    db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_AuditoriaPreRemitos_ProveedorCodigo"" ON ""AuditoriaPreRemitos"" (""ProveedorCodigo"");");

    // Columna TipoComprobante: se agregó después de la creación original de la
    // tabla PreRemitos. Default 'Remito' para que los ingresos previos queden
    // como remito. (Ver AgregarColumnaSiFalta: sólo se agrega si no existe.)
    AgregarColumnaSiFalta("PreRemitos", "TipoComprobante", "TEXT NOT NULL DEFAULT 'Remito'");

    // ---- Columnas y tabla propias de FACTURA (sólo se agregan si faltan) ----
    // Cabecera: letra, condición de compra, percepción de IVA y total declarado.
    AgregarColumnaSiFalta("PreRemitos", "Letra", "TEXT NULL");
    AgregarColumnaSiFalta("PreRemitos", "CondicionCompra", "TEXT NULL");
    AgregarColumnaSiFalta("PreRemitos", "PercepcionIva", "TEXT NOT NULL DEFAULT '0'");
    AgregarColumnaSiFalta("PreRemitos", "TotalDeclarado", "TEXT NULL");
    // Renglón: precio unitario, alícuota de IVA y vencimiento de la partida.
    AgregarColumnaSiFalta("PreRemitoLineas", "PrecioUnitario", "TEXT NOT NULL DEFAULT '0'");
    AgregarColumnaSiFalta("PreRemitoLineas", "TasaIva", "TEXT NOT NULL DEFAULT '0'");
    AgregarColumnaSiFalta("PreRemitoLineas", "FechaVencimiento", "TEXT NULL");
    // Referencia a una Orden de Compra pendiente (renglón que consume una OC de BAS).
    AgregarColumnaSiFalta("PreRemitoLineas", "OcNrotrans", "INTEGER NULL");
    AgregarColumnaSiFalta("PreRemitoLineas", "OcSecuencia", "INTEGER NULL");
    AgregarColumnaSiFalta("PreRemitoLineas", "OcFecha", "TEXT NULL");
    AgregarColumnaSiFalta("PreRemitoLineas", "OcPrefijo", "TEXT NULL");
    AgregarColumnaSiFalta("PreRemitoLineas", "OcNumero", "INTEGER NULL");

    // Percepciones de IIBB por provincia (hijas de la factura). Idempotente.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""PreRemitoPercepcionesIngBr"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_PreRemitoPercepcionesIngBr"" PRIMARY KEY AUTOINCREMENT,
            ""PreRemitoId"" INTEGER NOT NULL,
            ""Provincia"" TEXT NULL,
            ""BaseImponible"" TEXT NOT NULL DEFAULT '0',
            ""Importe"" TEXT NOT NULL DEFAULT '0',
            ""Porcentaje"" TEXT NOT NULL DEFAULT '0',
            ""Regimen"" TEXT NULL,
            CONSTRAINT ""FK_PreRemitoPercepcionesIngBr_PreRemitos_PreRemitoId""
                FOREIGN KEY (""PreRemitoId"") REFERENCES ""PreRemitos"" (""Id"") ON DELETE CASCADE
        );");
    db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_PreRemitoPercepcionesIngBr_PreRemitoId"" ON ""PreRemitoPercepcionesIngBr"" (""PreRemitoId"");");

    // Tabla de configuración por base. Idempotente: si la base ya existía, EnsureCreated
    // no la habría agregado, así que la creamos acá. Luego sembramos las bases de
    // appsettings que falten y sincronizamos los valores sobre el diccionario en memoria.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""ConfiguracionesBase"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ConfiguracionesBase"" PRIMARY KEY AUTOINCREMENT,
            ""Nombre"" TEXT NOT NULL,
            ""Descripcion"" TEXT NULL,
            ""Activa"" INTEGER NOT NULL DEFAULT 1,
            ""Empresa"" INTEGER NOT NULL DEFAULT 1,
            ""Sucursal"" INTEGER NOT NULL DEFAULT 1,
            ""RemitoPrefijo"" TEXT NOT NULL DEFAULT '1',
            ""RemitoConcepto"" TEXT NOT NULL DEFAULT 'com',
            ""RemitoDeposito"" INTEGER NOT NULL DEFAULT 1,
            ""FacturaPrefijo"" TEXT NOT NULL DEFAULT '1',
            ""FacturaConcepto"" TEXT NOT NULL DEFAULT 'com',
            ""FacturaDeposito"" INTEGER NOT NULL DEFAULT 1,
            ""FacturaImputacionContable"" INTEGER NOT NULL DEFAULT 21001001
        );");
    db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ConfiguracionesBase_Nombre"" ON ""ConfiguracionesBase"" (""Nombre"");");

    // Columnas de factura agregadas después de la creación original de la tabla
    // ConfiguracionesBase. Sólo se agregan si todavía no existen.
    AgregarColumnaSiFalta("ConfiguracionesBase", "FacturaConcepto", "TEXT NOT NULL DEFAULT 'com'");
    AgregarColumnaSiFalta("ConfiguracionesBase", "FacturaDeposito", "INTEGER NOT NULL DEFAULT 1");
    AgregarColumnaSiFalta("ConfiguracionesBase", "FacturaImputacionContable", "INTEGER NOT NULL DEFAULT 21001001");
    // Conexión editable + flag de portal (la tabla pasó a ser la fuente de verdad de las bases).
    AgregarColumnaSiFalta("ConfiguracionesBase", "BaseUrl", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "RemitoTipo", "TEXT NOT NULL DEFAULT 'N'");
    AgregarColumnaSiFalta("ConfiguracionesBase", "OrdenCompraPrefijo", "TEXT NOT NULL DEFAULT '1'");
    // SQL directo (e-cheques y futuras consultas): server + db + usuario/clave read-only + mail propio.
    AgregarColumnaSiFalta("ConfiguracionesBase", "SqlServidor", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "SqlBase", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "SqlUsuario", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "SqlClave", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "SqlEmailPropio", "TEXT NOT NULL DEFAULT ''");
    // Últimos filtros de e-cheques por base (banco/chequera/prefijo), antes en localStorage.
    AgregarColumnaSiFalta("ConfiguracionesBase", "EchBanco", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "EchChequera", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "EchPrefijo", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "EchUsaPrefijo", "INTEGER NOT NULL DEFAULT 0");
    // Fecha de corte de la emisión por API (red de seguridad para el arranque; por empresa).
    AgregarColumnaSiFalta("ConfiguracionesBase", "EchApiDesde", "TEXT NOT NULL DEFAULT ''");
    // Emisión de echeqs por API del Banco Credicoop, POR EMPRESA (client_id + adherente +
    // CBU + entorno + ruta a la PEM). La clave privada NO va a la base: sólo su ruta.
    AgregarColumnaSiFalta("ConfiguracionesBase", "BieHabilitado", "INTEGER NOT NULL DEFAULT 0");
    AgregarColumnaSiFalta("ConfiguracionesBase", "BieEntorno", "TEXT NOT NULL DEFAULT 'homologacion'");
    AgregarColumnaSiFalta("ConfiguracionesBase", "BieClientId", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "BieNumeroAdherente", "INTEGER NOT NULL DEFAULT 0");
    AgregarColumnaSiFalta("ConfiguracionesBase", "BieCbuDebito", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "BiePemPath", "TEXT NOT NULL DEFAULT ''");
    AgregarColumnaSiFalta("ConfiguracionesBase", "BieFirmantes", "TEXT NOT NULL DEFAULT ''");
    // Mapa Nº cuenta banco -> código de cuenta en BAS (para conciliación), por empresa.
    AgregarColumnaSiFalta("ConfiguracionesBase", "CuentasBas", "TEXT NOT NULL DEFAULT ''");
    // Marca que debe figurar en el título de BAS para confirmar la empresa antes de importar.
    AgregarColumnaSiFalta("ConfiguracionesBase", "TituloBas", "TEXT NOT NULL DEFAULT ''");
    // IncluirEnPortal: al CREAR la columna por primera vez, backfill de las bases que
    // HOY forman la cuenta corriente del portal (BARK + PRUEBAB), para preservar el
    // comportamiento existente. De ahí en más lo controla el admin (checkbox por base);
    // por eso el UPDATE corre sólo en la creación de la columna, no en cada arranque.
    if (!ColumnaExiste("ConfiguracionesBase", "IncluirEnPortal"))
    {
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ConfiguracionesBase"" ADD COLUMN ""IncluirEnPortal"" INTEGER NOT NULL DEFAULT 0;");
        db.Database.ExecuteSqlRaw(@"UPDATE ""ConfiguracionesBase"" SET ""IncluirEnPortal"" = 1 WHERE ""Nombre"" IN ('BARK','PRUEBAB');");
    }

    var configBases = scope.ServiceProvider.GetRequiredService<ConfigBasesService>();
    configBases.SembrarFaltantesAsync().GetAwaiter().GetResult();
    configBases.SincronizarMemoriaAsync().GetAwaiter().GetResult();

    // ---- Tabla de funciones del portal (menú data-driven) ----
    // Gobierna qué consultas aparecen en el menú del portal y a qué público. La
    // LÓGICA de cada función es código (front IMPLEMENTACIONES + endpoint); esta
    // tabla sólo maneja etiqueta, orden, audiencia y activa. EnsureCreated no la
    // agrega a una base que ya existe, así que la creamos acá (idempotente).
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""FuncionesPortal"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_FuncionesPortal"" PRIMARY KEY AUTOINCREMENT,
            ""Clave"" TEXT NOT NULL,
            ""Etiqueta"" TEXT NOT NULL,
            ""Orden"" INTEGER NOT NULL DEFAULT 0,
            ""Audiencia"" TEXT NOT NULL DEFAULT 'ambos',
            ""Activa"" INTEGER NOT NULL DEFAULT 1
        );");
    db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_FuncionesPortal_Clave"" ON ""FuncionesPortal"" (""Clave"");");

    // Acceso por función para internos. TodosLosInternos default 1: las funciones que YA
    // existen quedan abiertas a todos los internos (no rompe nada). Luego el admin restringe
    // las que quiera (ej. e-cheques) desde "Programas para el Portal".
    AgregarColumnaSiFalta("FuncionesPortal", "TodosLosInternos", "INTEGER NOT NULL DEFAULT 1");
    AgregarColumnaSiFalta("FuncionesPortal", "UsuariosAsignados", "TEXT NOT NULL DEFAULT ''");

    // Siembra de las funciones que YA existen en el código del portal (idempotente
    // por Clave). Cuando programemos una consulta nueva, se agrega su Clave acá en
    // el MISMO publish; de ahí en más el admin la configura desde la intranet
    // (etiqueta, orden, audiencia, activa) sin volver a publicar.
    SembrarFuncionSiFalta("cuenta", "Cuenta corriente", 10, "ambos");
    SembrarFuncionSiFalta("ventas", "Estadísticas de venta", 30, "interno");
    SembrarFuncionSiFalta("echeques", "E-Cheques", 40, "interno");
    SembrarFuncionSiFalta("conciliacion", "Bco/Conciliación", 50, "interno");
    SembrarFuncionSiFalta("listasprecios", "Alta de listas de precios", 60, "interno");
    SembrarFuncionSiFalta("discovery", "Precios a Discovery", 61, "interno");
    // "Mis datos" dejó de ser un programa del menú: los datos del cliente ahora se
    // muestran integrados en la card de consulta del portal (junto al buscador),
    // para internos y externos. Quitamos su fila si venía sembrada de antes.
    db.Database.ExecuteSqlRaw(@"DELETE FROM ""FuncionesPortal"" WHERE ""Clave"" = 'datos';");

    // ---- Órdenes de compra (EnsureCreated no agrega tablas a una base ya existente) ----
    // Decimales/fechas/Guid como TEXT (mismo criterio que EF Core usa para PreRemitos).
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""OrdenesCompra"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_OrdenesCompra"" PRIMARY KEY AUTOINCREMENT,
            ""ProveedorCodigo"" TEXT NOT NULL DEFAULT '',
            ""ProveedorRazonSocial"" TEXT NULL,
            ""Comprador"" TEXT NULL,
            ""CondicionCompra"" TEXT NULL,
            ""Fecha"" TEXT NOT NULL,
            ""FechaExpiracion"" TEXT NULL,
            ""CodigoMoneda"" INTEGER NOT NULL DEFAULT 1,
            ""Observaciones"" TEXT NULL,
            ""ObservacionEntrega"" TEXT NULL,
            ""Estado"" TEXT NOT NULL DEFAULT 'Borrador',
            ""DestinoBase"" TEXT NULL,
            ""NrotransBas"" INTEGER NULL,
            ""PrefijoBas"" TEXT NULL,
            ""NumeroBas"" TEXT NULL,
            ""MensajeError"" TEXT NULL,
            ""CreadoPor"" TEXT NOT NULL DEFAULT '',
            ""CreadoEn"" TEXT NOT NULL,
            ""ModificadoPor"" TEXT NULL,
            ""ModificadoEn"" TEXT NULL,
            ""GrabadoPor"" TEXT NULL,
            ""GrabadoEn"" TEXT NULL,
            ""RowVersion"" TEXT NOT NULL
        );");
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""OrdenCompraLineas"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_OrdenCompraLineas"" PRIMARY KEY AUTOINCREMENT,
            ""OrdenCompraId"" INTEGER NOT NULL,
            ""ProductoCodigo"" TEXT NOT NULL DEFAULT '',
            ""Descripcion"" TEXT NULL,
            ""Cantidad"" TEXT NOT NULL DEFAULT '0',
            ""Unidad"" TEXT NULL,
            ""PrecioUnitario"" TEXT NOT NULL DEFAULT '0',
            ""TasaIva"" TEXT NOT NULL DEFAULT '0',
            ""Observacion"" TEXT NULL,
            CONSTRAINT ""FK_OrdenCompraLineas_OrdenesCompra_OrdenCompraId"" FOREIGN KEY (""OrdenCompraId"")
                REFERENCES ""OrdenesCompra"" (""Id"") ON DELETE CASCADE
        );");
    db.Database.ExecuteSqlRaw(@"CREATE INDEX IF NOT EXISTS ""IX_OrdenCompraLineas_OrdenCompraId"" ON ""OrdenCompraLineas"" (""OrdenCompraId"");");

    // ---- Echeqs emitidos por API (idempotencia anti-doble-emisión) ----
    // Sólo guarda los que el banco aceptó. El índice único (BaseNombre, NumeroCheque)
    // evita emitir dos veces el mismo cheque. Idempotente (EnsureCreated no la agrega
    // a una base ya existente).
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""EmisionesEcheq"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_EmisionesEcheq"" PRIMARY KEY AUTOINCREMENT,
            ""BaseNombre"" TEXT NOT NULL DEFAULT '',
            ""NumeroCheque"" INTEGER NOT NULL DEFAULT 0,
            ""Cuit"" TEXT NOT NULL DEFAULT '',
            ""Beneficiario"" TEXT NOT NULL DEFAULT '',
            ""Monto"" TEXT NOT NULL DEFAULT '0',
            ""FechaPago"" TEXT NOT NULL DEFAULT '',
            ""IdOrigen"" TEXT NOT NULL DEFAULT '',
            ""IdOperacion"" INTEGER NULL,
            ""IdCheque"" TEXT NULL,
            ""Estado"" TEXT NOT NULL DEFAULT '',
            ""EmitidoPor"" TEXT NOT NULL DEFAULT '',
            ""EmitidoEn"" TEXT NOT NULL
        );");
    db.Database.ExecuteSqlRaw(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_EmisionesEcheq_Base_Numero"" ON ""EmisionesEcheq"" (""BaseNombre"", ""NumeroCheque"");");

    // ---- Columnas nuevas de Usuarios (agregadas después de la creación original) ----
    // AccedePortalClientes: habilita a un usuario interno a usar el portal de
    // clientes como consulta de staff. BasesPortal: subconjunto de bases del portal
    // que ve el usuario (vacío = todas). Sólo se agregan si faltan.
    AgregarColumnaSiFalta("Usuarios", "AccedePortalClientes", "INTEGER NOT NULL DEFAULT 0");
    AgregarColumnaSiFalta("Usuarios", "BasesPortal", "TEXT NOT NULL DEFAULT ''");
    // Preferencia de la barra "Ver bases" (orden + destildadas), compartida ctacte/estadísticas.
    AgregarColumnaSiFalta("Usuarios", "PrefBases", "TEXT NULL");

    if (!db.Usuarios.Any())
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UsuarioPortal>>();

        var admin = new UsuarioPortal
        {
            Identificador = "admin",
            Tipo = TipoUsuario.Interno,
            EsAdmin = true,
            Activo = true
        };
        admin.PasswordHash = hasher.HashPassword(admin, "Admin1234!");

        var cliente = new UsuarioPortal
        {
            Identificador = "20123456789",
            Tipo = TipoUsuario.Extranet,
            EsCliente = true,
            CodigoCliente = "00123",
            RazonSocial = "Cliente Demo S.A.",
            Activo = true
        };
        cliente.PasswordHash = hasher.HashPassword(cliente, "Demo1234!");

        var proveedor = new UsuarioPortal
        {
            Identificador = "20987654321",
            Tipo = TipoUsuario.Extranet,
            EsProveedor = true,
            CodigoProveedor = "P0050",
            RazonSocial = "Proveedor Demo S.R.L.",
            Activo = true
        };
        proveedor.PasswordHash = hasher.HashPassword(proveedor, "Demo1234!");

        var ambos = new UsuarioPortal
        {
            Identificador = "30711111118",
            Tipo = TipoUsuario.Extranet,
            EsCliente = true,
            CodigoCliente = "00150",
            EsProveedor = true,
            CodigoProveedor = "P0099",
            RazonSocial = "Empresa Dual S.A.",
            Activo = true
        };
        ambos.PasswordHash = hasher.HashPassword(ambos, "Demo1234!");

        db.Usuarios.AddRange(admin, cliente, proveedor, ambos);
        db.SaveChanges();
    }

    // ---- Funciones locales de migración idempotente ----
    // SQLite no soporta "ADD COLUMN IF NOT EXISTS". En vez de intentar el ALTER y
    // atrapar el error (que EF Core logueaba como 'fail' en rojo, aunque estuviera
    // controlado), preguntamos antes si la columna existe y sólo la agregamos si falta.
    void AgregarColumnaSiFalta(string tabla, string columna, string definicion)
    {
        if (!ColumnaExiste(tabla, columna))
            db.Database.ExecuteSqlRaw($@"ALTER TABLE ""{tabla}"" ADD COLUMN ""{columna}"" {definicion};");
    }

    // Siembra una función del portal si su Clave todavía no está en la tabla.
    // Idempotente: al rearrancar no duplica ni pisa lo que el admin haya editado.
    void SembrarFuncionSiFalta(string clave, string etiqueta, int orden, string audiencia)
    {
        if (!db.FuncionesPortal.Any(f => f.Clave == clave))
        {
            db.FuncionesPortal.Add(new FuncionPortal
            {
                Clave = clave,
                Etiqueta = etiqueta,
                Orden = orden,
                Audiencia = audiencia,
                Activa = true
            });
            db.SaveChanges();
        }
    }

    bool ColumnaExiste(string tabla, string columna)
    {
        var conn = db.Database.GetDbConnection();
        var abrir = conn.State != System.Data.ConnectionState.Open;
        if (abrir) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"PRAGMA table_info(""{tabla}"");";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // En el resultado de PRAGMA table_info, el nombre de la columna
                // está en el campo "name" (índice 1).
                if (string.Equals(reader.GetString(1), columna, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally
        {
            if (abrir) conn.Close();
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new
{
    estado = "ok",
    servicio = "PortalClientes",
    hora = DateTimeOffset.Now
}));

app.Run();
