@echo off
setlocal
REM Lanzador del protocolo conciliarbas:// -> ejecuta la macro de conciliacion en BAS.
REM Windows lo llama con %1 = la URL (ej. conciliarbas://BARK). La macro extrae la empresa.
REM
REM BAS corre ELEVADO (admin). Por UIPI, un proceso NO elevado no puede automatizarlo.
REM Chequeo admin con fltmc (confiable, no depende de servicios como 'net session'):
REM  - si YA somos admin (o UAC deshabilitado) -> corremos directo.
REM  - si no -> nos re-lanzamos elevados (con UAC "Nunca notificar" no muestra prompt).
fltmc >nul 2>&1
if %errorlevel% equ 0 goto correr

echo Elevando permisos...
if "%~1"=="" (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
) else (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%~1' -Verb RunAs"
)
exit /b

:correr
cd /d "C:\Agente\webapi\macro-conciliacion"
python macro_conciliar.py "%~1"
echo.
echo ---------------------------------------------
echo  Termino. Revisa el resultado arriba.
pause
