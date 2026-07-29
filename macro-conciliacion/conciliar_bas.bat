@echo off
REM Lanzador del protocolo conciliarbas:// -> ejecuta la macro de conciliacion en BAS.
REM Windows lo llama con %1 = la URL (ej. conciliarbas://BARK). La macro extrae la empresa.
REM
REM IMPORTANTE: BAS corre ELEVADO (admin). Por UIPI, un proceso NO elevado no puede
REM automatizar (ni leer el arbol UIA de) una app elevada. El navegador lanza este .bat
REM SIN elevar, asi que primero nos AUTO-ELEVAMOS (UAC) y recien ahi corremos la macro.
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Elevando permisos (UAC)...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%~1' -Verb RunAs"
  exit /b
)
cd /d "C:\Agente\webapi\macro-conciliacion"
python macro_conciliar.py "%~1"
echo.
echo ---------------------------------------------
echo  Termino. Revisa el resultado arriba.
pause
