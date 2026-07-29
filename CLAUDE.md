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
- Recargá con **Ctrl + Shift + R** (recarga DURA). ⚠️ **Ctrl + F5 a veces NO alcanza** y te
  deja el HTML/CSS viejo cacheado: eso hizo perseguir un "bug" de scroll fantasma un buen rato
  (el archivo publicado estaba OK, pero el navegador servía el viejo). Ante cualquier "cambié
  el `.html` y no se ve", **primero** sospechar de esto y hacer hard-reload antes de tocar código.
- Para confirmar qué CSS/HTML tiene el navegador de verdad: F12 → Console →
  `getComputedStyle(document.getElementById('id')).propiedad`.
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
│   ├── BasAuthService.cs           Singleton single-base; cachea el token de BAS. Igual que
│   │                              BasDestinosService: expone EnviarConReintentoAuthAsync
│   │                              (token + reintento ante 401) — sus consumidores (clientes/
│   │                              comprobantes/ctacte/proveedores single-base) lo usan.
│   ├── BasDestinosService.cs       Multi-base: GetAsync/PostAsync por base. Cachea 1 token
│   │                              por destino. ⚠️ Si BAS se REINICIA invalida los tokens
│   │                              viejos → ante 401 se invalida el token cacheado y se
│   │                              REINTENTA 1 vez con uno fresco (auto-recupera al volver una
│   │                              base, sin esperar a que venza ~1 h). No quitar ese reintento.
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
│   ├── ConciliacionController.cs  Bco/Conciliación: movimientos + TXT + .info (ver §17)
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
├── macro-conciliacion/  Macro de importación a BAS (pywinauto) + protocolo conciliarbas:// (ver §17)
│                        macro_conciliar.py, conciliar_oculto.vbs, conciliar_bas.bat,
│                        registrar_protocolo.reg (conciliar.log = salida, gitignored)
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
| 9 | ~~**Cachear meses de venta**~~ | ✅ **Hecho.** Caché por mes cerrado en memoria (~12 h) **y en disco** (`cache-ventas/`, sobrevive al reinicio); buscar un cliente sobre un rango cacheado filtra en memoria (§15). |

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

### 14.1 Acceso por función (asignación por usuario interno)
Independiente de los "Permisos" del editor de usuarios (esos son para funciones de la
**intranet**: remitos, auditoría). Acá se controla el acceso a las funciones del **portal**:
- **`FuncionPortal.TodosLosInternos`** (bool, default true) + **`UsuariosAsignados`** (lista de
  identificadores, CSV como `Permisos`/`BasesPortal`).
- Regla (`Auth/AccesoFuncionesService.PuedeUsar`, verificada con tabla de decisión):
  - **Externo**: accede si `Audiencia` es `externo`/`ambos` (sin asignar nada).
  - **Interno**: la audiencia debe incluir `interno` Y (es admin · o `TodosLosInternos` · o su
    identificador está en `UsuariosAsignados`).
- **Doble candado**: `GET /api/funciones-portal` filtra el menú con esa regla **y** los endpoints
  sensibles la re-chequean contra la base (ej. `EchequesController` → `PuedeUsarAsync("echeques")`).
  No alcanza con esconder del menú.
- **Migración**: `TodosLosInternos` default 1 → las funciones que ya existían quedan abiertas a
  todos los internos (no rompe). El admin restringe las que quiera desde "Programas para el
  Portal" (check "Todos los internos" + botón "Asignar usuarios" → modal con los internos activos).
- No viaja en el JWT (se lee de la base) → cambiar la asignación aplica **sin re-login**.

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
  Por eso hay **botón Cancelar** (dentro del `#vtaCargando`): el front usa un `AbortController`
  y el back recibe `CancellationToken ct` (= `HttpContext.RequestAborted`, enhebrado hasta las
  llamadas a BAS), así cancelar corta el trabajo del servidor y no sigue pegándole a BAS.
  El front además valida `desde ≤ hasta` antes de consultar (comparación de strings `yyyy-MM-dd`);
  el back igual hace swap defensivo (`if (h < d) …`).
- **Unificación por CUIT**: el ranking agrupa por CUIT (un cliente real puede tener varios
  códigos, incluso en la misma base). El backend manda por código con su CUIT; el front
  agrupa según el checkbox "Unificar por CUIT".
- **Caché por mes** (`ComprobantesAsync`): cada **mes CERRADO** (ya terminado) se cachea con
  **TODOS los clientes** y el **mes entero**: en memoria (`IMemoryCache`, key
  `estVtaMes|{base}|{yyyy-MM}`, ~12 h) **y en disco** (`cache-ventas/{base}-{yyyy-MM}.json`,
  permanente, gitignored, sobrevive al reinicio; se lee al primer acceso y se re-siembra en
  memoria). **`cache-ventas/` vive DENTRO de `PortalPublish` (= ContentRootPath), pero
  `dotnet publish -o` NO limpia archivos extra, así que republicar NO lo borra.** El **mes en
  curso NUNCA se cachea** (cambia): un rango que lo incluye SIEMPRE pega a BAS por ese mes —
  no es bug. Claves de la lógica (todas las combinaciones cubiertas):
  - **Lectura desacoplada de `mesCompleto`**: cualquier tramo de un mes cerrado se sirve del
    caché del mes completo **recortando por fecha** (`c.Fecha >= tramoDesde && <= tramoHasta`).
    Así un rango con **bordes parciales** (ej. `hasta` el 15, o `desde` el 3) igual sale del
    caché, no de BAS.
  - **Auto-siembra**: si un mes cerrado SIN filtro no está cacheado, se trae el **mes ENTERO**
    (a BAS le cuesta lo mismo que un pedazo), se cachea, y se recorta al tramo pedido.
  - Buscar **UN cliente** sobre un mes cacheado filtra en memoria (`codigosSet`) sin pegar a
    BAS. Filtrado por cliente sobre un mes **no** cacheado va liviano (`FiltroCodigos`) y **no**
    se guarda (parcial); si es `forzar`, invalida el caché del mes completo.
- **Scroll del área de resultados**: alto **FIJO por CSS** (`#vtaScroll { max-height }`); TODO
  —gráfico "Evolución por mes", títulos y filas de clientes— scrollea dentro de esa ventana, y
  la cantidad de meses **NO** cambia el alto de la card. `ajustarScrollVentas()` sólo hace
  `scrollTop = 0` al re-renderizar/cambiar de vista. ⚠️ **No volver** al esquema anterior de
  medir alturas (`offsetTop`/`getBoundingClientRect` + `requestAnimationFrame` para calcular
  `max-height` = encabezado + N filas): daba intermitencias (el alto salía mal al cambiar de
  rango/solapa) y hacía crecer la card con la cantidad de meses. Alto fijo = simple y robusto.
- **Botones Gráfico/Listado + Refrescar** (naranja) en la **misma línea**, a la **derecha** de la
  línea de bases (`.vta-btn-stack` = flex **row**, `margin-left:auto`). Refrescar reconsulta el
  **mismo período** cargado con
  `&refrescar=true` → `ComprobantesAsync(forzar:true)`, que **saltea la lectura de caché**,
  repega a BAS y **reescribe** el caché (memoria + disco) de los meses cerrados. Es la salida
  para el único caso donde "mes cerrado = inmutable" no se cumple: un comprobante cargado o
  corregido **retroactivamente** en BAS con fecha de un mes ya cacheado. (Si se refresca con un
  cliente filtrado, el traído es parcial y no se guarda, pero se **invalida** el mes cacheado de
  todos los clientes para que la próxima consulta sin filtro lo traiga fresco.) ⚠️ Ojo al cablear
  el listener: `addEventListener("click", cargarVentas)` pasaría el `MouseEvent` como `forzar`
  (siempre truthy) → usar `() => cargarVentas(false)` para Consultar y `(true)` para Refrescar.
- **Barra "Ver bases" (orden + tildado), POR USUARIO y COMPARTIDA** entre cuenta corriente y
  estadísticas: se persiste en `UsuarioPortal.PrefBases` (columna JSON `{orden:[],ocultas:[]}`)
  vía `GET/PUT /api/mi-cuenta/pref-bases`. El front tiene un único `prefBases` en memoria
  (cargado en `actualizarVista`), y las dos pantallas usan `construirLeyendaBases(cont, bases,
  onCambio)` → mismo estado para ambas (reordenás/destildás en una y se ve en la otra + queda
  guardado). Reordenar = **drag & drop nativo** (`hacerReordenable`, sin librería). `ocultas` =
  bases destildadas (default: todas tildadas, así una base nueva aparece sola). Al reordenar un
  subconjunto (una pantalla muestra menos bases), `reordenarPref` preserva la posición de las
  no visibles. Guardado con **debounce** (500 ms). Reemplazó los viejos `basesVisibles` /
  `basesVtaVisibles` (que se reseteaban por consulta/cliente). La pref NO se resetea al cambiar
  de cliente (es del usuario); sí se limpia en `salir()`.
- **Pre-elegir bases antes de consultar**: la barra "Ver bases" se muestra **desde el arranque**
  (cards vacías incluidas), poblada con `disponibles` (que ahora devuelve `GET /pref-bases` =
  `PortalBases()`). Las consultas mandan `?bases=CSV` con las **tildadas** y el backend
  (`FiltrarBases` en `MiCuentaController` y `EstadisticasController`) consulta **sólo esas**
  (sin el param → todas). Así se excluye una base (ej. caída) de entrada. El front siempre pinta
  la barra con TODAS las `basesDisponibles` (no sólo las con datos), así se puede re-incluir una
  excluida (requiere volver a Consultar).
- **Base caída sin colgar** (estadísticas): (1) los **signos** de tipos (`SignosPorTipoAsync`) se
  piden a BAS ANTES de leer el caché de comprobantes; ahora se **persisten a disco**
  (`cache-ventas/{base}-tipos.json`) y si la base no responde se cae al último catálogo conocido
  → un mes YA cacheado se sirve completo aunque la base esté caída (distingue timeout de
  cancelación real con `when (!ct.IsCancellationRequested)`). (2) **`ConnectTimeout` 8s** en los
  HttpClient `bas`/`bas-multi` → una base inalcanzable falla al conectar en ~8s, no a los 120s.

---

## 16. E-Cheques (función interna) — SQL Server DIRECTO (no WebAPI)

Exporta los cheques emitidos de una chequera al `.xls` que importa el banco (e-cheques).
Portado del `Echeques.py` (Tkinter + pyodbc) que se usaba como `.exe` por PC.

**Por qué NO va por el WebAPI**: se verificó (swagger + "Esquema de CONSULTAS") que BAS **no
expone** una consulta de cheques por chequera/banco/fecha — los cheques sólo aparecen anidados
dentro de un `ComprobanteCompra`. Así que esta función se conecta **DIRECTO al SQL Server** de la
base (`Microsoft.Data.SqlClient`), corriendo la misma query del `.py`. Es el primer camino
SQL-directo del portal (pensado para reusar en futuras "consultas").

- **`BasEchequesService`** (`Bas/`): `ConsultarAsync` conecta al SQL de la base y trae los cheques
  (parametrizada, dedup por número, orden, `ConnectTimeout` 8s). `ArmarXls` genera el `.xls` binario
  (BIFF, con **NPOI**) idéntico al de `xlwt` (importe `0.00`; numEcheq/nroCuiCdi/carácter/modo enteros;
  resto texto; anchos). Verificado: sale con la firma `.xls` `D0-CF-11-E0-…`.
- **`EchequesController`** (solo-interno): `GET /api/echeques` (recuento) y `GET /api/echeques/exportar`
  (baja el `.xls`, header `X-Echeques-Cantidad`; o JSON `{cantidad:0}` si no hay registros).
- **Config por base** (`ConfiguracionesBase`, editable en la intranet): `SqlServidor`, `SqlBase`,
  `SqlUsuario`, `SqlClave`, `SqlEmailPropio` (mail propio a excluir, difiere por empresa). La **clave**
  no se devuelve en el GET (sólo `sqlTieneClave`); en edición, clave vacía = se conserva. Vacío
  `SqlServidor`/`SqlBase` = función deshabilitada para esa base.
- **Credencial read-only**: usuario **`portal_consultas`** (login SQL con `db_datareader`), creado en
  CADA SQL Server. Se lee de la config de la base; si está vacía, cae a la var. de entorno
  `SqlConsultas__User` / `SqlConsultas__Password`. ⚠️ Es la MISMA credencial pensada para reusar en
  futuras funciones de SQL directo. Clave en texto plano en la DB del portal (sólo la ve el admin del
  editor de bases); es read-only, pero no exponerla en endpoints de usuarios comunes.
- **Front** (`secEcheques` en portal.html): formulario (base, fechas, banco, chequera, prefijo/usar,
  cheque desde/hasta) con las validaciones del `.py`; el prefijo se antepone al número si "usar prefijo".
  Genera y **descarga** el `.xls` (fetch con auth → blob → anchor). Recuerda el último banco/chequera/
  prefijo/base por navegador en `localStorage` (como el `.ini` por PC).
- **Red**: el server del portal tiene que alcanzar el SQL Server de cada base (puerto 1433) — dependencia
  nueva además de los puertos del WebAPI.

## 16.1 E-Cheques — EMISIÓN por API del Banco Credicoop (BIE)

Además de exportar el `.xls` (que se sube a mano), el portal ahora puede **emitir los echeqs
por la API del Banco Credicoop** (Banca Internet Empresa). Toma las **mismas filas** que
saca `BasEchequesService` del SQL de BAS y las emite. La firma queda **"Enviada a la firma"**:
el banco acepta la operación y una persona la completa en Banca Internet Empresa (no por API).

- **Auth = OAuth2 `client_credentials` con `private_key_jwt`** (NO client_secret): se firma un
  JWT RS256 con la clave privada RSA (`client_assertion`). `BancoBieAuthService` (singleton)
  arma el JWT (iss=sub=client_id, aud=TokenUrl, jti, iat, nbf, exp+5min), pide el token y lo
  cachea (~30min, margen 60s). La clave se lee de un `.pem` en disco (`RSA.ImportFromPem`,
  acepta PKCS#1 y PKCS#8). **Verificado en homologación** con un probe Python (`C:\Agente\echeques\probe_bie.py`).
- **`BancoBieEcheqService`**: `AsegurarBeneficiariosAsync` (alta idempotente en la agenda,
  ignora `APIE-8010` = ya existe) + `EmitirAsync` (uno por echeq) + `ConsultarEmisionAsync`.
  > **Nombre del beneficiario (APIE-1013):** el banco es muy restrictivo (verificado en
  > homologación con probe_nombre*.py): rechaza `&`, la **Ñ**, los **acentos** (Á/É/Í/Ó/Ú) y la
  > **ü**; sólo acepta letras/números/espacio y `.`/`,` (largo 42 OK). `LimpiarNombre` usa **lista
  > blanca**: `&`→"Y", quita diacríticos (Ñ→N, á→a, ü→u vía Normalize FormD) y reemplaza cualquier
  > carácter fuera de `[A-Za-z0-9 .,]` por espacio (cubre `#`, `°`, `/`, `(`, `)`, etc. sin tener
  > que probarlos uno por uno). Sólo aplica a la emisión por API (el `.xls` va tal cual). Nota:
  > `motivoPago` también rechaza no-ASCII, pero ahí va el texto fijo `"prov"`, no hace falta sanearlo.
  > **Verificado en homologación (probe_alta.py):** el alta valida el CUIT contra la **Coelsa
  > REAL** (no una de prueba) → los proveedores reales de BARK se dan de alta OK en homologación;
  > los que dan **`APIE-8011` "no bancarizado"** es porque ese CUIT realmente no está apto para
  > echeqs (pasaría igual en producción → revisar el CUIT en BAS). Ese cheque se omite y se
  > reporta por fila; el resto se emite. Sirve además como control de calidad de CUIT.
- **Body real de emisión** (`POST /api/echeq/v1/ConFirma/emision`) — OJO, el ejemplo del
  Postman del banco está copiado del de transferencias y es INCORRECTO. El real:
  cabecera `{numeroAdherente, idOrigen, cbuCuentaDebito, operadoresFirmantes?, echeqs[]}`; cada
  echeq con campos **planos**: `monto` (string "0.00"), `fechaPago` (**`yyyyMMdd`**),
  `motivoPago`, `caracter`/`modo` (string "1"), `beneficiarioNombre`/`beneficiarioDocumentoTipo`/
  `beneficiarioDocumento`, `concepto` (código: VAR/FAC/…), `tipoCheque` (ECHD diferido→fecha
  futura, ECHC común→hoy), `mails[]`, y `numeroCheque` (**opcional, ≤8 díg = el NUMEROEXT de
  BAS**; no se pueden mezclar echeqs con y sin número). `operadoresFirmantes` es opcional: sin
  él, igual queda "Enviada a la firma".
- **`idOrigen`** = clave de idempotencia (guid único por echeq). El banco rechaza uno repetido
  el mismo día (`APIE-1003`). Errores del banco vienen como `{error:{codigo:"APIE-xxxx",descripcion}}`.
- **Anti-doble-emisión**: tabla **`EmisionesEcheq`** (base del portal) con índice único
  `(BaseNombre, NumeroCheque)`. Sólo se persisten los **aceptados**; los rechazados NO, así se
  reintentan tras corregir. `/preparar` cruza los cheques de BAS contra esta tabla y **sólo lista
  los NO emitidos** (informa cuántos ya emitidos se omitieron). Un cheque no es emitible POR API si
  le falta CUIT, e-mail, importe>0 o el nº supera 8 díg (igual se puede exportar al .xls).
  > ⚠️ **Verificado en homologación (probe_duplicado.py):** el banco **NO deduplica por
  > `numeroCheque`** — emitir el mismo número 3 veces (con `idOrigen` distinto) devolvió 3
  > operaciones OK distintas. La idempotencia del banco es sólo por `idOrigen` y sólo el mismo
  > día (APIE-1003). Por eso esta tabla es IMPRESCINDIBLE: sin ella, re-emitir un rango duplica
  > echeqs (plata real en producción). No quitarla "porque el banco lo rechazaría" — no lo hace.
- **Endpoints** (en `EchequesController`, mismo candado interno+función `echeques`):
  `GET /preparar` (cheques NO emitidos por API, con `codProveedor` de BAS + `emitibleApi` +
  `apiHabilitada`), `POST /emitir` (body `{numeros}` = selección), `POST /exportar-sel` (body
  `{numeros}` → .xls de los seleccionados), `GET /emision-estado?base=&idOperacion=`. `GET /bases`
  devuelve `{ bases, basesApi }`. **Tanto la API como el .xls excluyen los ya emitidos por API**
  (por eso el .xls también sale por `/exportar-sel`, no por el viejo `/exportar`).
- **Config MULTI-EMPRESA** (cada empresa —BARK, XARDO— tiene su propio adherente, credenciales y
  PEM): lo COMPARTIDO (mismo banco) va en `appsettings` sección `BancoBie`: `Scopes`, `TipoCheque`,
  `Concepto`, y las URLs por entorno (`Homologacion`/`Produccion` → `BaseUrl`+`TokenUrl`). Lo PROPIO
  de cada empresa va POR BASE en `ConfiguracionBase`, **editable desde el editor de bases de la
  intranet**: `BieHabilitado`, `BieEntorno` (homologacion|produccion, **independiente por empresa**
  → se puede homologar una y dejar la otra en prod), `BieClientId`, `BieNumeroAdherente`,
  `BieCbuDebito`, y **`BiePemPath`** (ruta a la `.pem` de esa empresa). La clave privada vive como
  **ARCHIVO protegido en el server** (ej. `C:\Agente\PortalData\pem\bark.pem`), **NUNCA en git ni en
  la DB** — en la base sólo se guarda la ruta. `BancoBieOptions.Credenciales(cb)` arma las
  credenciales efectivas por base (`BieCredenciales`; null si falta algo o el archivo PEM no existe).
  `BancoBieAuthService` (singleton) cachea el token por `client_id` y la clave RSA por archivo.
- **Front** (`secEcheques`): un solo botón **"Preparar"** abre un **modal** con los cheques del rango
  que TODAVÍA no se emitieron por API — un **checkbox por cheque (todos tildados)**, el **código de
  proveedor de BAS**, y una marca de si es emitible por API — con **Marcar todos / ninguno**. En el
  pie del modal se elige el canal sobre lo tildado: **"Generar .xls (N)"** (siempre) o **"Emitir por
  API (M)"** (sólo si la empresa tiene API; M = tildados emitibles). El resultado por cheque
  (nº, código de proveedor, beneficiario, importe y estado / motivo `descripción (APIE-xxxx)`) se
  muestra en el mismo modal. Es la confirmación previa al envío. Tablas de **columnas de ancho fijo**
  y con **scroll interno** pasadas ~20 filas.
- **Filtros por empresa (no en el navegador)**: `banco`, `chequera`, `prefijo` y `usaPrefijo` se
  guardan **por base** en `ConfiguracionBase` (`EchBanco`/`EchChequera`/`EchPrefijo`/`EchUsaPrefijo`),
  **NO** en localStorage. `GET /bases` los devuelve en `defaults` para precargar el formulario al
  elegir la empresa; `GET /preparar` los **persiste solos** cuando cambian (no se editan a mano).
  En el navegador sólo queda la última empresa elegida (`ech_base`).
- **Fecha de corte de API (transición al arranque)**: `EchApiDesde` por base (editable en el
  editor de bases; **no** se autoajusta). La API **no emite** cheques con **fecha de carga
  (`CHEQUES.FECHA`) anterior** al corte — se asumen ya subidos por Excel al banco, para no
  duplicar. Se implementa como un motivo más de `ProblemaEmision` (aparecen en el preview como
  "no emitible por API: anterior a la fecha de corte"), así el `.xls` los sigue exportando pero la
  API los bloquea. `ChequeRow.FechaCarga` trae `CHEQUES.FECHA` para esto.
  > ⚠️ **Hueco pendiente (dos canales):** el portal sólo registra lo que emite por API. Un cheque
  > subido por Excel NO queda registrado → la API podría re-emitirlo (por eso el corte). Al revés,
  > registrar los Excel como emitidos tampoco sirve (pueden no haberse subido). La **fecha de corte
  > cubre el arranque**, pero el hueco permanente sólo se cierra: (a) haciendo la API el ÚNICO canal
  > tras el corte, o (b) con una API del banco que LISTE echeqs por cuenta/chequera para chequear
  > existencia antes de emitir. La "Consulta de Echeq Emitido" (`GET /emision?idOperacion=`) NO
  > sirve para eso: necesita el idOperacion, que sólo tenemos de lo emitido por API.
- **Chequeo anti-duplicado contra el banco (capa extra)**: `BancoBieEcheqService.NumerosGeneradosAsync`
  consulta **`POST /api/echeq/v1/lista-cheques`** (`gestion="GENERADOS"`, `estado="TODOS"`,
  `cbuEmisor`=CBU débito) y trae los `numeroCheque` YA GENERADOS en el banco (subidos por Excel y
  firmados, o por API). `/preparar` y `/emitir` los **suman a los ya-emitidos locales** y los
  excluyen. Ventana = el **mismo rango de la emisión ± `MargenChequeoBancoDias`** (default 5; se
  registra/emite/firma en el día). Chunkea en tramos ≤30 días (el banco limita el rango, `APIE-3006`)
  y pagina. **Best-effort**: si la consulta falla, no frena (quedan tabla local + corte).
  > ⚠️ **No ve los "Enviada a la firma"** (operaciones pendientes de firma todavía NO son echeqs
  > generados → no aparecen en lista-cheques). Por eso el chequeo local sigue siendo necesario para
  > el doble-envío API↔API. Valores de `gestion` válidos verificados: `GENERADOS` y `RECIBIDOS`
  > (los demás dan `APIE-3024`). El scope `echeq` NO es pedible en el token (da 400); lista-cheques
  > anda con los scopes ya concedidos (`echeqConFirma`…).
- **Homologación → producción**: el banco homologa SÓLO los scopes desarrollados
  (`echeqConFirma` + `beneficiarioEcheq`; `cuentas`/`consultaCbuCvuAlias` de apoyo). Datos de
  homologación: adherente `1399230`, client_id `20100794889`, CBU débito `1910044555004401995596`.
- **Red**: el server del portal debe alcanzar `homoapibccl.bancocredicoop.coop` (y el host de
  producción) por HTTPS 443.

### 16.2 Ver E-Cheques (listado + PDF)
Botón **"Ver E-Cheques"** (junto a "Preparar", `secEcheques`): lista los e-cheques **EMITIDOS**
de la cuenta en el rango, **traídos del banco** con su **estado** (ej. `EMITIDO-PENDIENTE`).
- **Backend**: `GET /api/echeques/lista?base=&desde=&hasta=&banco=&chequera=` → `BancoBieEcheqService.ListarGeneradosAsync`
  (mismo `lista-cheques` GENERADOS, chunk ≤30 días + paginado, mapea TODOS los campos al record
  `EcheqGenerado`: numeroCheque, estado, fechaEmision/Pago, monto, moneda, caracter, motivoPago,
  cmc7completo, chequeId, cuenta). El banco **NO trae beneficiario/CUIT del beneficiario** (solo
  emisor): se completan **best-effort desde BAS** (`BasEchequesService.ConsultarAsync`, por
  `numeroCheque`) si vinieron `banco`+`chequera`. `banco`/`chequera` opcionales; base+fechas obligatorios.
- **Front**: modal `echVerOv` con tabla de **columnas fijas** + botón **PDF**. El PDF es **plano**
  (jsPDF 2.5.1 + autotable 3.8.2, **locales** en `wwwroot/lib/`, no CDN — PCs internos), **sin** las
  convenciones del proyecto Horario. Descarga directa `doc.save("ECheques-<cuenta>-<desde>-<hasta>-<empresa>.pdf")`;
  el usuario decide si lo guarda. (CMC7 y chequeId se omiten en pantalla/PDF por ancho; están en el DTO si se quisieran.)

## 17. Bco/Conciliación (función interna)

Trae los **movimientos bancarios** de una cuenta (API Credicoop, scope `cuentas`), genera el **TXT
posicional** que importa BAS, y **dispara una macro** que hace la importación en BAS automáticamente.
Usa la **misma config del banco por empresa** que E-Cheques (`BieCredenciales`); las empresas que
pueden conciliar son las que tienen la API configurada. Función `conciliacion`, audiencia **interno**,
se registra en "Programas para el Portal".

### 17.1 Movimientos + TXT + `.info`
- **`BancoBieCuentasService`**: `ListarCuentasAsync` (`GET /api/cuentas/v1/listaCuentas`) +
  `MovimientosAsync` (`GET /api/cuentas/v1/{nroCuenta}/movimientos?fechaDesde=&fechaHasta=&topeMovimientos=1000`,
  **chunkea ≤31 días** porque el banco limita el rango; descarta el `ENCABEZADO` = `indDBCR` vacío) +
  `ArmarTxt`. Campos del movimiento: `fecha` (yyyymmdd), `descripcion`, `indDBCR` (DB/CR), `monto`,
  `nroComprobante`, `codOperativo`, `saldo`, `idTransaccion`.
- **TXT posicional (128 chars/línea), Latin1 (1 byte/char para no descolocar columnas), CRLF**:
  - **col 1** fecha `dd/mm/aa` (8, año 2 dígitos). Cols 9-10 en blanco (la descripción va en col 11).
  - **col 11** descripción (90, izq, se trunca).
  - **col 103** nº operación = `nroComprobante` (8, derecha con ceros a la izquierda; vacío = en blanco).
  - **col 114** importe (15, **coma decimal**, 2 decimales, ceros a la izquierda, **signo `-` en
    débitos**; créditos sin signo). Se arma poniendo cada campo en su **columna de inicio** exacta.
- **`ConciliacionController`** (interno + función `conciliacion`): `GET /bases` (empresas con API),
  `GET /cuentas?base=`, `GET /movimientos?base=&cuenta=&desde=&hasta=` (para el modal),
  `GET /txt?base=&cuenta=&desde=&hasta=` → **GUARDA** `CONC_<EMPRESA>.txt` (nombre FIJO por empresa,
  se sobrescribe) en `BancoBie:CarpetaConciliacion` (default `C:\conciliacion`, en la máquina del
  servicio; se **crea si no existe**) y devuelve `{cantidad, ruta, info}` (`{cantidad:0}` si no hay
  movimientos). **NO descarga** por el navegador (así no pregunta dónde). Ante error de escritura → 502.
- **Companion `CONC_<EMPRESA>.info`** (UTF-8 **sin BOM**, junto al TXT, se sobrescribe): líneas
  `key=value` — `empresa`, `cuenta` (Nº bancario consultado), `cuentaBas` (código interno de BAS, ver
  17.2), `desde`/`hasta` (dd/MM/yyyy), `cantidad`. Lo lee la macro.

### 17.2 Código de cuenta de BAS por empresa (`ConfiguracionBase.CuentasBas`)
El `.info` trae el **Nº de cuenta bancaria** (el que el portal usa contra Credicoop, ej. `00440199559`).
La pantalla de conciliación de BAS usa un **código interno propio** (ej. `011`), distinto, que BAS no
expone por API. La traducción se configura **por empresa** en `ConfiguracionBase.CuentasBas` (columna
TEXT, migración idempotente `AgregarColumnaSiFalta` en `Program.cs`): texto multilínea, una línea por
cuenta `NºCuentaBanco=códigoBAS`. Editable en la **intranet** (editor de bases → card BIE → campo
"Cuentas para conciliación"). `ConciliacionController.MapearCuentaBas` resuelve el código de la cuenta
consultada y lo escribe en el `.info` como `cuentaBas`.

**Red de seguridad `ConfiguracionBase.TituloBas`** (crítica): BAS permite cambiar de empresa
(BARK/XARDO) en la misma sesión; si el operador quedó en otra, la macro importaría en la empresa
equivocada. Por eso, por empresa se configura una **marca que debe figurar en el título de la
ventana de BAS** (el título es tipo `BASCS XE ... (bark-Bark/SA - 1:Bark S.A. ...)`; la marca es
p.ej. `bark-Bark` en prod o `bark-Test` en testeo). Editable en la intranet (card BIE, "Marca en
título de BAS"), se vuelca al `.info` como `tituloBas`. La macro, **antes de tocar nada**, exige que
esa marca esté en el título; si **falta la config** o **no coincide**, **ABORTA** con un cartel de
aviso (sin importar nada) y vuelve al portal. La comparación es **case-insensitive** (el `-nombre`
se configura por puesto y puede venir `bark` o `Bark`); solo importa la secuencia de letras.

### 17.3 Importación en BAS = macro de UI (BAS NO tiene API de conciliación)
Verificado en el swagger de BAS: no hay endpoint de conciliación, la pantalla es manual. La importación
la hace un **macro de automatización de UI** con **pywinauto** (BAS es una app **Gupta Team Developer**).
Vive en `macro-conciliacion/` (versionado con el portal por git):
- **`macro_conciliar.py`**: empresa por argumento (o del `.info` más reciente de la carpeta); lee del
  `.info` la cuenta y `cuentaBas`; abre la pantalla desde el menú **Procesos → Conciliación Bancaria →
  Ingreso** si no está abierta (si ya está, la usa); teclea el código de cuenta; espera que carguen los
  movimientos; y recorre la cadena **Archivo → &Archivos → &Agregar → (path del TXT + descripción) →
  &Importar → &Ok**. Descripción = `CREDICOOP - <fecha de hoy>`.
- **Gotchas Gupta (críticos)**: (a) los campos hay que **teclearlos de verdad** (`type_keys`), NO
  `set_edit_text` — Gupta no dispara su lógica interna y el Importar queda "vacío" / "como si no
  clickearas"; (b) los botones se clickean con **clic real** (`click_input`) sobre la ventana
  **enfocada** (los clics por mensaje se ignoran), con **fallback por acelerador** (Alt+I / Alt+O) si no
  toma; (c) **BAS debe estar al frente todo el tiempo**: la macro maneja el mouse/teclado reales, si otra
  ventana roba el foco se frena (re-enfoca antes de cada paso clave, pero **no hay que tocar nada mientras
  corre**); (d) el botón "Archivo" **no responde hasta que terminan de cargar** los movimientos.
- Diagnóstico incorporado: si no encuentra un diálogo, lista las ventanas abiertas (`_listar_ventanas`);
  si un botón falla, vuelca los controles de esa ventana (`print_control_identifiers`).

### 17.4 Disparo desde el portal (protocolo `conciliarbas://`)
El servicio del portal corre como **LocalSystem en la sesión 0 (no interactiva)** → **no puede** ver ni
manejar el escritorio donde está BAS, así que **no puede lanzar la macro directamente**. Solución: un
**protocolo de URL propio** `conciliarbas://<EMPRESA>` que dispara **el navegador** (que corre en la
sesión del usuario, la que ve BAS):
- **`conciliar_oculto.vbs`** → **`conciliar_bas.bat`**: el protocolo invoca el `.vbs` con `wscript`,
  que corre el `.bat` **sin consola visible** (window style 0). El `.bat` corre `python macro_conciliar.py %1`
  y manda el log a **`conciliar.log`** (junto al `.bat`) para diagnosticar sin consola.
  ⚠️ **Elevación**: BAS corre **elevado** (admin) y por **UIPI** un proceso no-elevado no puede
  automatizarlo (UIA da timeout: "No encontré la ventana de BAS (uia)"). El `.bat` chequea admin con
  **`fltmc`**: si ya es admin (o UAC deshabilitado) corre directo; si no, se **re-lanza elevado y oculto**
  (`Start-Process -Verb RunAs -WindowStyle Hidden`) → con UAC "Nunca notificar" no muestra prompt.
  (Alternativa: **tarea programada** con "máximos privilegios" disparada con `schtasks /run`.)
- **`registrar_protocolo.reg`**: registra `HKCU\Software\Classes\conciliarbas` → el `.bat` (HKCU = **no
  necesita admin**). **Se corre una vez por PC** donde se concilie. Si se mueve la carpeta, re-correrlo.
- **Front**: el botón **"Generar e importar a BAS"** genera TXT+`.info` y, tras el aviso *"Tené BAScs
  abierto"* (modal `basAvisoOv`), abre `conciliarbas://<empresa>` con un `<a>` temporal (no navega la
  página). **Heurístico** blur/visibilitychange: si al disparar la pestaña **no pierde el foco en ~1,5s**,
  asume que el protocolo **no está registrado** en esa PC y muestra *"Falta registrar protocolo — Avise al
  Administrador"* + la ruta del `.reg` (no hay API de navegador para chequear registro; es best-effort).

### 17.5 Front (`secConciliacion`)
Encabezado estilo E-Cheques (empresa + cuenta + fechas + **Preparar**); el modal (estilos `ech-*`) lista
los movimientos (scroll pasadas ~20 filas, ancho fijo, débitos en rojo) con el botón **"Generar e
importar a BAS"**. Recuerda empresa/cuenta por navegador (`conc_base`, `conc_cuenta_<base>`).
