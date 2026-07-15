# PortalClientes — documentación del proyecto

> Documento de contexto para trabajar el proyecto (pensado para que un agente o un
> desarrollador nuevo entienda todo sin tener que reconstruirlo leyendo el código).
> Última actualización: julio 2026.

---

## 1. Qué es esto

Backend .NET 8 / C# (ASP.NET Core) + dos front-ends en HTML plano que forman un
**portal de clientes/proveedores** integrado con el ERP externo **BAS CS WebAPI**.

Regla de oro de la arquitectura: **este backend es lo ÚNICO que habla con BAS**.
Los front-ends nunca pegan contra BAS directamente; siempre pasan por `/api/...` de
esta aplicación.

Dos caras, un solo backend y un solo login:

| Front | Archivo | Para quién | Qué hace |
|---|---|---|---|
| **Portal** | `wwwroot/portal.html` (~30 KB) | Clientes (extranet) + staff interno habilitado | Cuenta corriente consolidada, detalle de comprobantes, "Mis datos" |
| **Intranet** | `wwwroot/intranet.html` (~125 KB) | Personal interno | Pre-remitos de compra, auditoría, configuración de bases, administración de usuarios |

---

## 2. Stack

- .NET 8, ASP.NET Core, EF Core (SQLite hoy; SQL Server previsto para producción)
- ASP.NET Core Identity **solo para el hash de contraseñas** (`IPasswordHasher`)
- JWT propio (`Auth/GeneradorTokens.cs`)
- Swagger en `/swagger`
- Front: **JavaScript vanilla**, sin build step, sin framework. Los `.html` son
  archivos estáticos servidos desde `wwwroot/`.
- Se ejecuta como **servicio de Windows** (`UseWindowsService()`), escuchando en
  `http://*:5080`.

---

## 3. Mapa de carpetas (¡importante!)

Hay tres carpetas que se confunden fácil:

| Carpeta | Qué es |
|---|---|
| `C:\Agente\webapi` | **El código fuente** (este repo git). Acá se edita. |
| `C:\Agente\PortalPublish` | **El compilado que corre el servicio de Windows.** Se genera con `Portal-PUBLICAR.bat`. |
| `C:\Agente\PortalData\portal-clientes.db` | La base SQLite real de pruebas. |
| `C:\Agente\webapi\cache-padron` | Caché del padrón BAS en disco (gitignored, se regenera). |

### 3.1 Ciclo de trabajo (CRÍTICO)

El servicio Windows `PortalClientes` corre como **LocalSystem** y ejecuta el
**compilado** de `C:\Agente\PortalPublish`, **no** el fuente. Por lo tanto:

> **Todo cambio — de código `.cs` O de `.html` en `wwwroot/` — requiere ejecutar
> `C:\Agente\webapi\Portal-PUBLICAR.bat` COMO ADMINISTRADOR.**

(El .bat frena el servicio → `dotnet publish -c Release -o C:\Agente\PortalPublish`
→ lo vuelve a arrancar. Necesita admin para poder frenar/arrancar el servicio.)

Además, para cambios en `.html`:
- Hacé **Ctrl + F5** en el navegador (si no, la caché te sirve el HTML viejo).
- Portal e intranet son **SPA**: navegar por el menú **no** recarga el HTML. Si la
  pestaña quedó abierta desde antes del publish, hay que recargarla sí o sí.

Para **desarrollar** sin el servicio, alcanza con `dotnet run` (queda en
`http://localhost:5080`).

### 3.2 Los dos .bat (no confundir)

| Archivo | Qué hace |
|---|---|
| `C:\Agente\webapi\Portal-PUBLICAR.bat` | **Compila y publica** a `PortalPublish` (lo que consume el servicio). Correr como admin. |
| `C:\Agente\webapi\PUBLICAR.bat` | **Sube el repo a GitHub** (git). No compila nada. |

Los `.ps1` de `instaladores/` instalan/desinstalan el servicio y el ícono de bandeja
(tray). El servicio se instala con `Portal-InstalarServicio.ps1`
(`$Exe = C:\Agente\PortalPublish\PortalClientes.exe`, `--urls http://*:5080`).

---

## 4. Estructura del repo

```
C:\Agente\webapi\
├── Program.cs               Arranque, DI, migración de columnas, siembra de usuarios
├── PortalClientes.csproj    AssemblyName=PortalClientes, UserSecretsId=portal-clientes-dev
├── appsettings.json         Template (se versiona, sin secretos)
├── appsettings.Development.json   Config real local — GITIGNORED
├── Portal-PUBLICAR.bat      Compila → PortalPublish (admin)
├── PUBLICAR.bat             git push
├── README.md                Resumen corto del proyecto
├── CLAUDE.md                Este archivo
│
├── Auth/
│   ├── AuthDtos.cs          SolicitudLogin, RespuestaLogin, Crear/ModificarUsuarioRequest
│   ├── GeneradorTokens.cs   Arma el JWT y sus claims
│   ├── JwtPortalOptions.cs  Clave, emisor, ExpiraMinutos
│   └── Permisos.cs          Catálogo de permisos funcionales
│
├── Bas/                     TODO lo que habla con BAS CS WebAPI
│   ├── BasAuthService.cs           Singleton; cachea el token de BAS
│   ├── BasDestinosService.cs       Multi-base: GetAsync/PostAsync por base
│   ├── BasDestinos.cs / BasWebApiOptions.cs   Config
│   ├── BasCacheMaestros.cs         Caché en memoria (SnapshotMaestro por base)
│   ├── BasCacheRefresher.cs        Carga el padrón desde BAS vía CONSULTAGRAL
│   ├── BasCacheLoader.cs           BackgroundService: disco al arrancar + refresco 6 h
│   ├── BasClientesService.cs       BuscarPorCuitAsync / BuscarPorCuitEnBaseAsync
│   ├── BasProveedoresService.cs
│   ├── BasCuentaCorrienteService.cs
│   ├── BasComprobantesService.cs
│   ├── BasResolucionService.cs
│   ├── BasPartidasService.cs       AsegurarAsync (GET/POST /api/Partidas, idempotente)
│   ├── BasRemitoIngresoService.cs  Graba el remito de compra en BAS
│   ├── BasFacturaIngresoService.cs Graba la factura de compra en BAS
│   ├── ConfigBasesService.cs
│   └── (modelos) BienInfo.cs, ClienteBas.cs, ProveedorBas.cs,
│                 ComprobanteVentaBas.cs, CuentaCorrienteBas.cs
│
├── Controllers/
│   ├── AuthController.cs          POST /api/auth/login  (login unificado)
│   ├── MiCuentaController.cs      Portal: perfil, datos, cuenta-corriente, comprobante,
│   │                              buscar-clientes (staff)
│   ├── PreRemitosController.cs    (~36 KB) Alta/edición/conformado/grabado de pre-remitos
│   ├── UsuariosAdminController.cs ABM de usuarios + GET /api/admin/bases-portal
│   ├── ConfigBasesController.cs   ABM de bases BAS
│   ├── AuditoriaController.cs
│   ├── BasAdminController.cs      Refresco manual del caché
│   └── HealthController.cs        /api/health/apis (solo loopback)
│
├── Data/PortalDbContext.cs   DbSets: Usuarios, PreRemitos, PreRemitoLineas,
│                             PreRemitoPercepcionesIngBr, AuditoriaPreRemitos,
│                             ConfiguracionesBase
├── Models/
│   ├── UsuarioPortal.cs      Modelo central de usuario (ver §6)
│   ├── ConfiguracionBase.cs  Activa, IncluirEnPortal, BaseUrl, Empresa, Sucursal…
│   ├── PreRemito.cs
│   └── AuditoriaPreRemito.cs
├── Remitos/RemitoDtos.cs
│
├── wwwroot/
│   ├── portal.html      Portal de clientes (+ vista de staff)
│   └── intranet.html    Intranet completa
│
├── instaladores/        .ps1 de servicio y tray (Portal-* y BAS-WebAPI-*)
├── BASCS/               Manuales del WebAPI propio (Esquema/Respuestas .xlsx + swagger.json).
│                        2,35 MB. NO es basura, no borrar.
└── cache-padron/        Caché en disco (gitignored)
```

---

## 5. Integración con BAS CS WebAPI

### 5.1 Multi-base

El sistema trabaja contra **varias bases BAS** (empresas) a la vez. Configuradas en
`appsettings.json` bajo `BasDestinos` (ej.: `BARK` → puerto 5081, `PRUEBAB` → 5082;
en Development se agrega `BARKTEST` → 5083). Cada base tiene `Activa`, `BaseUrl`,
`Empresa`, `Sucursal`, e **`IncluirEnPortal`** (interruptor maestro por base).

**Concepto clave:** un mismo cliente puede existir en varias bases **con distinto
código**, pero con **el mismo CUIT**. Por eso **todo se pivotea por CUIT**, nunca por
código de cliente. `BasClientesService.BuscarPorCuitEnBaseAsync(base, cuit)` resuelve
el código local en cada base. La cuenta corriente del portal consolida todas las bases
y etiqueta cada movimiento con la suya.

`ElegirCasaCentral` descarta sucursales: en BAS, un cliente con `AdministradaPor` con
valor **es una sucursal**; casa central tiene ese campo vacío. La cuenta corriente vive
en la casa central.

### 5.2 Caché del padrón (CONSULTAGRAL)

Traer el padrón entero en cada consulta es inviable (la licencia gratis de BAS topea
en **~4 transacciones por minuto**). Por eso se cachea.

- Se carga vía el motor de consultas `POST /api/CONSULTAGRAL/{Entidad}` pidiendo solo
  los campos necesarios (`SelectDatosPrimarios`) → respuestas livianas.
- **La carga es SECUENCIAL**: el WebAPI de BAS no tolera varios paginados en paralelo.
- Paginado: BAS no informa el total, así que se pagina hasta que una página venga
  vacía, más corta que la anterior, o repetida.
- Tres maestros por base, cada uno con su flag "listo" independiente (si uno falla, los
  otros siguen sirviendo):
  - **bienes** (`/api/CONSULTAGRAL/Bien`) → `BienInfo` (incluye `AdministraPartidas`,
    `AdministraSeries`, `Impuesto`; la tasa de IVA se resuelve contra `/api/Impuestos/{empresa}`)
  - **proveedores** (`/api/CONSULTAGRAL/Proveedor`) → código → razón social
  - **clientes** (`/api/CONSULTAGRAL/Cliente`) → `ClienteCache { Codigo, RazonSocial, Cuit }`
    (el CUIT sale de `NumeroImpositivo1`; **se saltean las sucursales** —
    `AdministradaPor` con valor — para deduplicar por CUIT).
    ✅ **Verificado en funcionamiento:** la entidad de CONSULTAGRAL se llama
    efectivamente `Cliente` y el caché carga bien (la búsqueda de staff trae
    resultados).
- Se **persiste en disco** (`cache-padron/{base}-{maestro}.json`). Al arrancar se lee
  del disco al instante; el refresco contra BAS ocurre en segundo plano
  (`BasCacheLoader`) y solo si está vencido (> 6 h). Se repite cada 6 h.
- Los diccionarios se reconstruyen siempre con `StringComparer.OrdinalIgnoreCase`: al
  venir de JSON traen el comparador sensible a mayúsculas y no matchearía
  `02023p` vs `02023P`.

> ⚠️ **Trampa conocida:** `GuardarBienes` / `GuardarProveedores` / `GuardarClientes` /
> `MarcarError` hacen un swap del `SnapshotMaestro` entero. **Cada uno tiene que
> preservar explícitamente los campos de los otros maestros.** Si agregás un maestro
> nuevo y te olvidás de propagarlo en los otros métodos, cargar un maestro **borra**
> el otro.

### 5.3 Credenciales de BAS (bug histórico, ya resuelto)

Las credenciales BAS vivían **solo en user-secrets** del perfil del desarrollador. El
servicio corre como **LocalSystem** y **no lee user-secrets** → el portal fallaba con
`Invalid client or user (400)` / "No se pudo consultar BARK/PRUEBAB". (La intranet
parecía andar porque se servía del caché en disco; solo el portal hace login BAS en vivo.)

**Solución:** variables de entorno a nivel **Machine** (el `__` equivale a `:`), en una
PowerShell **realmente elevada**:

```powershell
[Environment]::SetEnvironmentVariable("BasWebApi__Usuario","sa","Machine")
[Environment]::SetEnvironmentVariable("BasWebApi__Password","<clave>","Machine")
Restart-Service PortalClientes
```

Si te da "Acceso denegado al Registro / SecurityException", la ventana **no** está
elevada.

En producción: mismas variables Machine o un `appsettings.Production.json` al lado del
`.exe`. **Usar un usuario BAS acotado, no `sa`.**

---

## 6. Usuarios, login y permisos

### 6.1 Modelo (`Models/UsuarioPortal.cs`)

- `Tipo`: `Interno` | `Extranet`
- `Identificador`: **único**. Para internos es el nombre de usuario; para extranet es
  el **CUIT**.
- `EsAdmin`, `Permisos` (lista; catálogo en `Auth/Permisos.cs`:
  `editar_remitos`, `conformar_remitos`, `auditar`)
- `EsCliente` / `EsProveedor`, `CodigoCliente` / `CodigoProveedor`, `RazonSocial`
- `AccedePortalClientes` (bool): habilita a un **interno** a entrar al portal como
  staff. No restringe a los de extranet.
- `BasesPortal` (lista de nombres de base): las bases que ve ese usuario. El usuario ve
  **solo las tildadas**; **vacío = NINGUNA** (ya no hay fallback a "todas" — cambiado
  jul 2026). Al dar de alta/editar un usuario hay que tildarle las bases o no verá nada.

### 6.2 Login unificado

`POST /api/auth/login` busca por `Identificador`, sea usuario interno o CUIT de
extranet. Un solo endpoint para ambos front-ends.

`RespuestaLogin` devuelve (en camelCase por la serialización de ASP.NET):
`token, identificador, tipo, esAdmin, esCliente, esProveedor, codigoCliente,
codigoProveedor, razonSocial, permisos, accedePortal, expira`.

### 6.3 Claims del JWT

`GeneradorTokens` emite, entre otros: `identificador`, `tipo`, `esCliente`,
`esProveedor`, `codigoCliente`, `codigoProveedor`, `accedePortal`, y **un claim
`basePortal` por cada base permitida**.

> ⚠️ **Consecuencia clave:** como estos flags viajan **en el token**, cambiar el acceso
> o las bases de un usuario **no tiene efecto hasta que ese usuario vuelva a
> loguearse**. Es la causa #1 de "no me aparece el cambio".

### 6.4 Bases visibles en el portal

`MiCuentaController.PortalBases()` calcula la **intersección** entre:
1. las bases **Activas + `IncluirEnPortal`** (config por base, desde la intranet), y
2. los claims `basePortal` del usuario. **Sin fallback**: si no tiene ninguno tildado, no
   ve ninguna base (antes veía todas; cambiado jul 2026). Mismo criterio en
   `EstadisticasController.PortalBases()` (ver §14).

### 6.5 Administración de usuarios

Vive **dentro de `intranet.html`, sección "Usuarios"**. (Existió un `admin.html`
duplicado y en desuso; fue movido a `C:\Agente\_descartados`. No revivirlo.)

Endpoints en `UsuariosAdminController`, incluido
`GET /api/admin/bases-portal` (devuelve las bases activas+portal para pintar los
checkboxes).

---

## 7. Vista de staff en el portal (feature reciente)

**Problema:** un usuario interno que entraba a `portal.html` recibía *"Tu usuario no
tiene una cuenta corriente de cliente asociada"*, porque el portal exigía
`esCliente = true` y un interno no lo es.

**Solución:** el interno habilitado (`Tipo=Interno` + `AccedePortalClientes=true`) entra
al portal como **staff**, **busca un cliente** y ve la cuenta **de ese cliente**.

Cómo funciona:

1. El front detecta staff con `data.tipo === "Interno" && data.accedePortal === true`.
2. Aparece la barra **"Consulta de clientes"** (`#staffBar`) con un buscador
   (debounce 250 ms, mínimo 2 caracteres) contra
   `GET /api/mi-cuenta/buscar-clientes?q=&limit=`.
3. Ese endpoint (solo staff) busca en el **caché de clientes** de las bases del usuario
   — offline, instantáneo, **no consume transacciones de BAS** — y **deduplica por
   CUIT** entre bases (`ClienteAgrupado { RazonSocial, Bases }`).
4. Al elegir un cliente se guarda su **CUIT**, y a partir de ahí el front agrega
   `?cuit=` (helper `cuitQ()`) a las tres consultas: `datos`, `cuenta-corriente` y
   `comprobante`.
5. El backend resuelve con `ResolverCuitObjetivo(cuitParam)`:
   - **staff** → usa el `?cuit=` (obligatorio; sin él responde "Elegí un cliente")
   - **cliente extranet** → usa **su propio** identificador, **ignorando** cualquier
     `?cuit=` que le manden (esto es el control de acceso: un cliente no puede espiar a otro)
   - otro → sin acceso
6. Toda la maquinaria multi-base ya pivoteaba por CUIT, así que el problema del
   "código distinto por base" **ya estaba resuelto**: no hubo que tocarlo.

Estado en el front: `esStaff`, `clienteCuit`, `clienteRazon`, persistidos en
localStorage (`pc_staff`, `pc_cli_cuit`, `pc_cli_razon`). Al elegir otro cliente se
**resetean todas las cachés** del front (`ccCache`, `datosCache`, `cacheDetalle`).

**Para activarlo en un usuario:** intranet → Usuarios → tildar "acceso al portal de
clientes" (+ bases opcionales) → guardar → **el usuario debe re-loguearse** (§6.3).

---

## 8. Pre-remitos de compra (intranet)

Flujo: se carga un pre-remito en la base local (SQLite) → se conforma → se **graba en
BAS** (`PreRemitosController.Grabar`), que da de alta el remito y/o la factura de compra
vía `BasRemitoIngresoService` / `BasFacturaIngresoService`. Todo queda auditado
(`AuditoriaPreRemitos`: alta, modificación, eliminación, conformado, grabado).

### 8.1 Partidas condicionales (bug resuelto)

BAS rechazaba con **409** (`"El ítem 02023 debe manejar partidas"`) al grabar. La partida
es una **precondición (FK)**: hay que crearla **antes** del comprobante, no después.

Pero **no todos los artículos administran partidas**, y eso **varía por base**. Regla
implementada: *si un producto trae partida cargada pero en la base destino no administra
partidas, se graba igual, SIN partida.*

Se corrigió en **tres lugares** (los tres deben mantenerse coherentes):
1. `PreRemitosController.Grabar` — solo llama a `_partidas.AsegurarAsync` si
   `bienes[l.Id]?.AdministraPartidas == true`; si no, `partidaPorLinea[l.Id] = null`.
2. `BasRemitoIngresoService` — solo agrega `item["Partida"]` si hay partida **y**
   `r.Articulo?.AdministraPartidas == true`.
3. `BasFacturaIngresoService` — ídem.

> **Series:** el negocio **no usa series**. `ExplosionSeries` quedó sin tocar (candidato
> a limpieza futura).

---

## 9. Base de datos

SQLite (`C:\Agente\PortalData\portal-clientes.db`), ruta anclada a
`AppContext.BaseDirectory`. Se usa **`EnsureCreated()`, no migraciones de EF**.

> ⚠️ `EnsureCreated()` **no** actualiza una base que ya existe. Por eso `Program.cs`
> tiene un helper artesanal **`AgregarColumnaSiFalta(tabla, columna, tipoSql)`**
> (que chequea con `PRAGMA table_info`) y se invoca **antes** de
> `if (!db.Usuarios.Any())`. **Si agregás una propiedad a una entidad, tenés que sumar
> ahí su `AgregarColumnaSiFalta` o la base existente no la va a tener.**

Ejemplos ya presentes:
```csharp
AgregarColumnaSiFalta("Usuarios", "AccedePortalClientes", "INTEGER NOT NULL DEFAULT 0");
AgregarColumnaSiFalta("Usuarios", "BasesPortal", "TEXT NOT NULL DEFAULT ''");
```

Las listas (`Permisos`, `BasesPortal`) se guardan como **texto separado por comas**
mediante `ValueConverter` + `ValueComparer` en `PortalDbContext`.

Siembra: si no hay usuarios, crea `admin` / `Admin1234!`.

---

## 10. Configuración y secretos

- `appsettings.json` → **template versionado**, sin secretos (`BasWebApi` vacío,
  `ClientId=api`, `ClientSecret=secret`, `BasDestinos` con las bases).
- `appsettings.Development.json` → **gitignored**. Config real local (ruta absoluta de
  la DB, base `BARKTEST`).
- Credenciales BAS bajo servicio → **variables de entorno Machine** (§5.3).
- `.gitignore` ignora: `bin/`, `obj/`, `publish/`, `cache-padron/`, `*.db`,
  `appsettings.Development.json`. **`BASCS/` está comentado a propósito: sí se sube.**

---

## 11. Convenciones del proyecto

- **Idioma del código y los comentarios: español.** Nombres de clases, métodos y
  variables en español (`BuscarPorCuitEnBaseAsync`, `GuardarClientes`,
  `ResolverCuitObjetivo`). Mantener el estilo.
- Comentarios explicativos **arriba** de las clases/métodos importantes, explicando el
  *por qué*, no el *qué*.
- Front sin framework ni build: HTML + JS vanilla, todo en un archivo por página.
- Los mensajes al usuario van en español rioplatense ("Elegí un cliente…").
- Fallo gracioso: si un maestro del caché o una base fallan, el resto sigue andando.

---

## 12. Deuda técnica y pendientes

| # | Tema | Detalle |
|---|---|---|
| 1 | **Factura letra B** | Precio final sin IVA discriminado: no implementado. |
| 2 | **Claves por defecto** | Cambiar `admin/admin` y `admin/Admin1234!` antes de producción. |
| 3 | **Usuario BAS** | Reemplazar `sa` por un usuario acotado. |
| 4 | **Exposición externa** | El portal hoy es interno. Para publicarlo: reverse proxy + HTTPS, o *split deployment*. Conceptual, sin implementar. |
| 5 | **Token de GitHub filtrado** | Hay un `ghp_...` en texto plano en `C:\Agente\Horario\.git\config` (**otro** repo). **Revocarlo en GitHub.** No mover el archivo: rompe ese repo. |
| 6 | **`ExplosionSeries`** | Código muerto (no se usan series). Candidato a borrar. |
| 7 | **Label del login** | El portal dice "CUIT" y la intranet "Usuario"; como el login es unificado, podría ser "Usuario o CUIT". |
| 8 | **Estadísticas por artículo** | Requiere habilitar `vstaestvtas` (o una consulta agregada) en BAS. Sin eso, solo nivel comprobante (§15). |
| 9 | **Cachear meses de venta** | Un mes cerrado no cambia: cachear los tramos mensuales aceleraría muchísimo los rangos largos (hoy tardan minutos, §15). |

---

## 13. Errores frecuentes / checklist de diagnóstico

1. **"Cambié el código y no pasa nada"** → no publicaste. `Portal-PUBLICAR.bat` **como
   administrador** (§3.1).
2. **"Cambié el HTML y no pasa nada"** → publicaste, pero falta **Ctrl + F5**; o la
   pestaña SPA quedó abierta de antes.
3. **"Le di permiso al usuario y no lo ve"** → los flags viajan en el JWT: **tiene que
   re-loguearse** (§6.3).
4. **"No se pudo consultar BARK/PRUEBAB" / `Invalid client or user (400)`** →
   credenciales BAS no visibles para LocalSystem (§5.3).
5. **"La búsqueda de clientes no trae nada"** → el caché se llena en segundo plano tras
   el arranque; puede tardar un rato la primera vez. Si sigue vacío, mirar los logs
   (`Caché BAS '{Base}': no se pudo cargar clientes`) y verificar que la base esté
   Activa + `IncluirEnPortal` y dentro de las `BasesPortal` del usuario.
6. **"Cargar un maestro me borró el otro"** → falta propagar el campo en los
   `Guardar*`/`MarcarError` (§5.2).
7. **"Agregué una propiedad y la base tira error de columna"** → falta el
   `AgregarColumnaSiFalta` en `Program.cs` (§9).

---

## 14. Menú del portal "data-driven" (Programas para el Portal)

El menú lateral de `portal.html` **NO es fijo**: se arma desde una tabla, filtrando por
el público del usuario. Frontera **código / configuración**:

- **La lógica de cada función es código** (su sección HTML + `init` en el front, y su
  endpoint en el back). Se programa y se **publica**.
- **La metadata es configuración**, editable desde la intranet **sin publicar**:
  etiqueta, orden, público (`externo`/`interno`/`ambos`) y activa.

Piezas:
- Tabla **`FuncionesPortal`** (`Models/FuncionPortal.cs`): `Clave` (única, vínculo con el
  código), `Etiqueta`, `Orden`, `Audiencia`, `Activa`. Se crea con `CREATE TABLE IF NOT
  EXISTS` en `Program.cs` (EnsureCreated no crea tablas nuevas en base existente, §9) y se
  **siembra por código** con `SembrarFuncionSiFalta(...)`.
- **`FuncionesPortalController`**: `GET /api/funciones-portal` (arma el menú del usuario,
  filtrado por el claim `tipo`) + ABM admin `GET/PUT /api/admin/funciones`.
- **Front**: `IMPLEMENTACIONES = { clave: {sec, init, requiereCliente} }` (catálogo de
  código). `construirMenu()` pide `/api/funciones-portal` y lo cruza con `IMPLEMENTACIONES`
  (ignora claves que no implemente). La **audiencia** la resuelve el backend por el claim
  `tipo` (Interno→interno, Extranet→externo); el front ya no filtra por audiencia.
- **Intranet**: sección **"Programas para el Portal"** (solo admin) edita etiqueta/orden/
  público/activa. La `Clave` es de solo lectura; **no se inventan claves a mano** (una fila
  sin código detrás no haría nada).

**Para agregar una función nueva** (3 pasos, en el mismo publish): 1) programar su
`<div class="seccion">` + `init` en `portal.html` y su endpoint; 2) registrarla en
`IMPLEMENTACIONES` con su clave; 3) `SembrarFuncionSiFalta("clave", ...)` en `Program.cs`.
De ahí en más, etiqueta/orden/público/activa se manejan desde la intranet (aplica al
recargar el portal, **sin re-login** — no viaja en el JWT).

Detalle del portal: la **card de cliente** (buscador de staff + datos del cliente) aparece
solo si la función pide cliente (`requiereCliente:true`). "Mis datos" dejó de ser función:
los datos del cliente viven en esa card. La cuenta corriente ordena por fecha (desc por
defecto, click en "Fecha" invierte) y colorea filas por base.

---

## 15. Estadísticas de venta (función interna) y consultas de venta a BAS

Función `ventas` (audiencia **interno**): total vendido + evolución por mes + ranking de
clientes, en un período, para las bases tildadas del usuario. **Tabla + gráfico** (barras
HTML propias, sin librería). `EstadisticasController` + `BasEstadisticasVentaService`.

**Cómo se saca de BAS** (esto costó bastante de descubrir — leer antes de tocar):

- **No hay endpoint para ejecutar stored procedures**, ni CONSULTAGRAL agrega
  (`GROUP BY`). Agregamos **nosotros** de este lado. La vista `vstaestvtas` (estadística
  de ventas por artículo) existe en SQL pero **NO está habilitada** como consulta del
  WebAPI ("La consulta no está habilitada" = falta habilitarla en BAS). Por eso **no hay
  nivel artículo**; sí a nivel comprobante.
- Fuente: **`POST /api/CONSULTAGRAL/ComprobanteCliente`** (comprobantes de venta de
  clientes). La entidad es el cliente; los campos del comprobante (`Comprobante`, `Fecha`,
  `Total`) van en **`SelectGrupoInformacion`** (no en `SelectDatosPrimarios`). Devuelve
  `Cuerpo.CLIENTES[]` con `Comprobantes[]` anidados; **pagina POR CLIENTE**.
- **Filtro de fecha**: `FiltrosAdicionales` con `TagEntidad:"ComprobanteCliente"`,
  `NombreCampo:"Fecha"`, dos condiciones — `Comparacion:"4"` (≥) y `"5"` (≤). **Códigos de
  `Comparacion`**: 0 `=`, 1 `<>`, 2 entre(≥ y ≤), 3 entre(> y <), 4 `≥`, 5 `≤`, 6 `>`,
  7 `<`, 8 comienza-con, 9 contiene. **El valor de fecha va `dd/mm/yyyy`** (el modo "entre"
  con coma rompe la conversión; ISO también falla — usar dd/mm/yyyy con dos filtros).
- **Signo**: cada tipo de comprobante tiene `EstadisticaVta` en `GET /api/TiposComprobantes`
  (`+1` factura/nota débito, `-1` nota crédito, `0`/`null` recibos y demás). El neto es
  `Total * EstadisticaVta` (netea créditos, ignora lo que no es venta). **No hardcodear**
  qué tipos son venta: usar ese campo (cacheado por base ~1h).
- **Filtrar por un cliente** (buscador): `FiltroCodigos:[{Codigo}]` sí funciona en
  `ComprobanteCliente`. Se resuelven los códigos del CUIT por base con
  `BasClientesService.BuscarCuentasPorCuitEnBaseAsync` (pivoteo por CUIT, igual que la
  cuenta corriente) y se pasan como filtro.
- ⚠️ **Trampa de performance (rangos amplios)**: el SQL de `ComprobanteCliente` procesa
  **todo el rango** y llega a **timeout** con rangos largos en bases de alto volumen (XARDO),
  **sin importar el `pageSize`**. Solución implementada: **partir el rango en tramos
  MENSUALES** y combinar (cada comprobante cae en un único mes → sin doble conteo). Aun así
  un período largo tarda **varios minutos** (tramos secuenciales + límite ~4 tx/min de BAS).
- **Unificación por CUIT**: el ranking agrupa por CUIT (un cliente real puede tener varios
  códigos, incluso en la misma base). El backend manda por código con su CUIT; el front
  agrupa según el checkbox "Unificar por CUIT".
