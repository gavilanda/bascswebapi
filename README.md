# Portal de Clientes - Backend

Backend en .NET 8 que sirve al portal. Por encima de BAS CS WebAPI: expone a
cada cliente/proveedor su cuenta y (mas adelante) la carga de pedidos, con su
propio sistema de usuarios y filtrando todo por el codigo de BAS.

Este backend es el unico que habla con BAS CS WebAPI. Los usuarios nunca tocan
BAS directo.

## Tipos de usuario

- Interno (red local): se loguea con nombre de usuario. Puede ser admin.
- Extranet (clientes / proveedores): se loguea con CUIT. Tiene un rol
  (Cliente o Proveedor) y un codigo de BAS. Por ahora el codigo se carga a
  mano; en la pieza 2 se resolvera automaticamente buscando el CUIT en BAS
  (cliente y proveedor estan en el mismo maestro, con un campo que los
  diferencia).

## Estado actual

Cimiento + usuarios del portal (interno/extranet) + pagina de administracion.

- Login (`POST /api/auth/login`) por identificador (usuario o CUIT).
- Token del portal (JWT) con tipo, rol y codigo BAS adentro.
- Endpoint protegido de ejemplo (`GET /api/mi-cuenta/perfil`).
- Administracion (solo admin): listar (con filtro activos/inactivos/todos),
  crear, activar/desactivar (baja logica), eliminar (baja fisica).
- Pagina web en `/admin.html`.

Todavia NO incluye: cliente HTTP hacia BAS ni la resolucion del CUIT (pieza 2),
ni la cuenta corriente real (pieza 3). Fuera de alcance: pedidos y mock.

## IMPORTANTE al actualizar

Cambio la estructura de los usuarios. Como en desarrollo la base se crea con
EnsureCreated (no migra), hay que borrar la base vieja una vez:

1. Frenar el backend (Ctrl+C).
2. Borrar el archivo `portal-clientes.db` de la carpeta del proyecto.
3. Volver a correr `dotnet run`. Se recrea con el esquema nuevo y se siembran
   los usuarios de prueba.

## Como correrlo

Requisito: SDK de .NET 8.

    cd C:\Agente\webapi
    dotnet run

Usuarios de prueba que se siembran solos:

- Interno admin:      usuario "admin"            / Admin1234!
- Extranet cliente:   CUIT "20123456789"         / Demo1234!  (codigo 00123)
- Extranet proveedor: CUIT "20987654321"         / Demo1234!  (codigo P0050)

## Pagina de administracion

    http://localhost:5080/admin.html

Ingresar con "admin" / Admin1234!. Desde ahi se crean usuarios (eligiendo
Interno o Extranet), se listan con filtro, y se desactivan o eliminan.

Swagger (`/swagger`) sigue siendo solo una herramienta de prueba.

## Base de datos

- Desarrollo: SQLite, archivo `portal-clientes.db` (no se sube al repo).
- Produccion: apuntar `ConnectionStrings:PortalDb` a SQL Server.

## Estructura

    webapi/
      Program.cs                       arranque + auth + base + Swagger + estaticos
      appsettings*.json
      Models/UsuarioPortal.cs          usuario + enums (TipoUsuario, RolExtranet)
      Data/PortalDbContext.cs
      Auth/
        JwtPortalOptions.cs
        GeneradorTokens.cs
        AuthDtos.cs
      Controllers/
        AuthController.cs              POST /api/auth/login
        MiCuentaController.cs          GET  /api/mi-cuenta/perfil
        UsuariosAdminController.cs     administracion (solo admin)
      wwwroot/admin.html               pagina de administracion
