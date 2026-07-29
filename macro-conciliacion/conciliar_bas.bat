@echo off
setlocal
REM Lanzador del protocolo conciliarbas:// -> ejecuta la macro de conciliacion en BAS.
REM Normalmente lo invoca conciliar_oculto.vbs (sin consola visible). %1 = URL (conciliarbas://BARK).
REM
REM BAS corre ELEVADO (admin). Por UIPI, un proceso NO elevado no puede automatizarlo.
REM Chequeo admin con fltmc (confiable, no depende de servicios):
REM  - si YA somos admin (o UAC deshabilitado) -> corremos directo.
REM  - si no -> nos re-lanzamos elevados y ocultos.
fltmc >nul 2>&1
if %errorlevel% equ 0 goto correr

if "%~1"=="" (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -WindowStyle Hidden"
) else (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%~1' -Verb RunAs -WindowStyle Hidden"
)
exit /b

:correr
cd /d "C:\Agente\webapi\macro-conciliacion"
REM Sin consola visible: el log va a un archivo (para diagnosticar si algo falla).
python macro_conciliar.py "%~1" > "%~dp0conciliar.log" 2>&1
