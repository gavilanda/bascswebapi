@echo off
REM Lanzador del protocolo conciliarbas:// -> ejecuta la macro de conciliacion en BAS.
REM Windows lo llama con %1 = la URL (ej. conciliarbas://BARK). La macro extrae la empresa.
REM Corre en la sesion del usuario (lo lanza el navegador), por eso ve BAS y maneja el mouse.
cd /d "C:\Agente\webapi\macro-conciliacion"
python macro_conciliar.py %1
echo.
echo ---------------------------------------------
echo  Termino. Revisa el resultado arriba.
pause
