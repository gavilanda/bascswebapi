# BAS CS WebAPI — Portal de Clientes

Backend .NET 8 + intranet web para un portal de clientes/proveedores integrado con
**BAS CS WebAPI** (ERP).

## Componentes

- **Backend .NET 8 / C#**: API propia que es lo único que habla con BAS CS WebAPI.
  Gestión de usuarios del portal (login por usuario interno o por CUIT), JWT,
  permisos funcionales.
- **Intranet** (`wwwroot/intranet.html`): administración de usuarios y carga de
  pre-remitos de compra.
- **Padrón de productos y proveedores** cacheado por base BAS, cargado vía el motor
  de consultas `CONSULTAGRAL` (con select de campos para respuestas livianas) y
  persistido en disco.
- **Auditoría** de pre-remitos (alta, modificación, eliminación, conformado, grabado).

## Stack

.NET 8, ASP.NET Core, EF Core (SQLite en desarrollo → SQL Server en producción),
ASP.NET Core Identity (hash de contraseñas), JWT, Swagger.

## Configuración

`appsettings.json` es un template. La configuración real (cadena de conexión, clave
JWT, credenciales BAS) va en `appsettings.Development.json`, que **no** se versiona.

## Ejecución

```bash
dotnet run
```

La API queda en `http://localhost:5080` (Swagger en `/swagger`, intranet en
`/intranet.html`).
