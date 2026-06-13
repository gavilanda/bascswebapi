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

// ---- Configuracion del token del portal ----
builder.Services.Configure<JwtPortalOptions>(
    builder.Configuration.GetSection(JwtPortalOptions.Seccion));

// ---- Base de datos de usuarios del portal ----
builder.Services.AddDbContext<PortalDbContext>(opciones =>
    opciones.UseSqlite(builder.Configuration.GetConnectionString("PortalDb")));

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
builder.Services.AddHttpClient("bas", c => c.BaseAddress = new Uri(basBaseUrl));

builder.Services.AddSingleton<BasAuthService>();      // singleton: cachea el token
builder.Services.AddScoped<BasClientesService>();
builder.Services.AddScoped<BasProveedoresService>();
builder.Services.AddScoped<BasCuentaCorrienteService>();
builder.Services.AddScoped<BasComprobantesService>();

// ---- Destinos BAS (BARK, PRUEBAB) para ingresos ----
// Timeout amplio para la carga del padrón (la carga es secuencial y en segundo
// plano; una base lenta puede tardar). La consulta EN VIVO se topea aparte,
// corto (8s), en BasResolucionService.
var basDestinos = builder.Configuration
    .GetSection(BasDestinosConfig.Seccion)
    .Get<Dictionary<string, DestinoBas>>() ?? new();
foreach (var (nombre, cfg) in basDestinos)
    builder.Services.AddHttpClient($"bas-{nombre}", c =>
    {
        c.BaseAddress = new Uri(cfg.BaseUrl);
        c.Timeout = TimeSpan.FromSeconds(120);
    });
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

    var configBases = scope.ServiceProvider.GetRequiredService<ConfigBasesService>();
    configBases.SembrarFaltantesAsync().GetAwaiter().GetResult();
    configBases.SincronizarMemoriaAsync().GetAwaiter().GetResult();

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
