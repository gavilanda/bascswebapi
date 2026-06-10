@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo   Publicar BAS CS WebAPI en GitHub
echo ============================================
echo.

REM --- Verifica que sea un repo git ---
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Esta carpeta no es un repositorio git.
    echo Ubicacion: %cd%
    goto fin
)

REM --- Detecta la rama actual (main, master, etc.) ---
set "RAMA="
for /f "delims=" %%B in ('git rev-parse --abbrev-ref HEAD') do set "RAMA=%%B"
if "%RAMA%"=="" (
    echo [ERROR] No se pudo determinar la rama actual.
    goto fin
)
echo Rama actual: %RAMA%
echo.

REM --- Muestra que cambios hay ---
echo Cambios detectados:
echo --------------------------------------------
git status --short
echo --------------------------------------------
echo.

REM --- Pide el mensaje del commit ---
set "MENSAJE="
set /p MENSAJE=Mensaje del commit (Enter = "Actualizacion"): 
if "%MENSAJE%"=="" set "MENSAJE=Actualizacion"

echo.
echo Agregando cambios...
git add -A

echo Creando commit...
git commit -m "%MENSAJE%"
REM Nota: si no hay nada para commitear, git devuelve error; seguimos igual
REM porque puede haber un commit anterior sin subir.

echo.
echo Subiendo a GitHub...
git push -u origin %RAMA%
if errorlevel 1 (
    echo.
    echo [ERROR] Fallo el push. Revisa la conexion o las credenciales/token.
    echo Si el problema persiste, corre a mano:  git push -u origin %RAMA%
    goto fin
)

echo.
echo ============================================
echo   LISTO. Cambios publicados en GitHub.
echo ============================================

:fin
echo.
echo (Presiona una tecla para cerrar esta ventana)
pause >nul
endlocal
