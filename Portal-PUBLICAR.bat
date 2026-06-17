@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
cd /d "%~dp0"

REM ============================================================
REM   Portal-PUBLICAR.bat
REM   Compila y publica el Portal de Clientes a la carpeta de
REM   publicacion (lo que consume el SERVICIO de Windows).
REM
REM   NO es lo mismo que el PUBLICAR.bat de la carpeta webapi
REM   (ese sube a GitHub). Esto genera el .exe para el servicio.
REM
REM   Para DESARROLLAR seguis usando "dotnet run"; este .bat solo
REM   hace falta cuando corres el portal como SERVICIO y queres
REM   que tome los ultimos cambios.
REM
REM   Si el servicio esta instalado, conviene correr este .bat
REM   como ADMINISTRADOR (para poder frenar/arrancar el servicio).
REM ============================================================

set "PROY=C:\Agente\webapi\PortalClientes.csproj"
set "DEST=C:\Agente\PortalPublish"
set "SERVICIO=PortalClientes"
set "ESTABA_CORRIENDO="

echo ============================================
echo   Publicar Portal de Clientes
echo ============================================
echo   Proyecto: %PROY%
echo   Destino : %DEST%
echo.

REM --- Verifica que el proyecto exista ---
if not exist "%PROY%" (
    echo [ERROR] No se encuentra el proyecto: %PROY%
    goto fin
)

REM --- Si el servicio existe y esta corriendo, frenarlo para liberar el .exe ---
sc query "%SERVICIO%" 2>nul | find "RUNNING" >nul
if %errorlevel%==0 (
    set "ESTABA_CORRIENDO=1"
    echo Deteniendo el servicio %SERVICIO% para liberar archivos...
    net stop "%SERVICIO%" >nul 2>&1
    echo.
)

REM --- Publicar ---
echo Publicando ^(dotnet publish -c Release^)...
echo --------------------------------------------
dotnet publish "%PROY%" -c Release -o "%DEST%"
set "PUB_ERR=%errorlevel%"
echo --------------------------------------------

REM --- Si lo habiamos frenado, arrancarlo de nuevo (haya salido bien o mal) ---
if defined ESTABA_CORRIENDO (
    echo.
    echo Reiniciando el servicio %SERVICIO%...
    net start "%SERVICIO%" >nul 2>&1
)

if not "%PUB_ERR%"=="0" (
    echo.
    echo [ERROR] Fallo la publicacion. Revisa los mensajes de arriba.
    goto fin
)

echo.
echo ============================================
echo   LISTO. Portal publicado en:
echo   %DEST%
echo ============================================
echo.
echo Recordatorio: para PRODUCCION necesitas un appsettings.Production.json
echo al lado del .exe. Para tu PC ^(entorno Development^) no hace falta.

:fin
echo.
echo (Presiona una tecla para cerrar esta ventana)
pause >nul
endlocal
