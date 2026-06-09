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

REM --- Muestra que cambio hay ---
echo Cambios detectados:
echo --------------------------------------------
git status --short
echo --------------------------------------------
echo.

REM --- Si no hay nada para subir, avisa y sale ---
git diff --quiet
set HAY_MOD=%errorlevel%
git diff --cached --quiet
set HAY_STAGED=%errorlevel%
git ls-files --others --exclude-standard >"%temp%\_gitnew.txt"
for %%A in ("%temp%\_gitnew.txt") do set NEW_SIZE=%%~zA
del "%temp%\_gitnew.txt" >nul 2>&1

if "%HAY_MOD%"=="0" if "%HAY_STAGED%"=="0" if "%NEW_SIZE%"=="0" (
    echo No hay cambios para publicar. Todo esta al dia.
    goto fin
)

REM --- Pide el mensaje del commit ---
set "MENSAJE="
set /p MENSAJE=Mensaje del commit (Enter = "Actualizacion"): 
if "%MENSAJE%"=="" set "MENSAJE=Actualizacion"

echo.
echo Agregando cambios...
git add -A
if errorlevel 1 (
    echo [ERROR] Fallo "git add".
    goto fin
)

echo Creando commit...
git commit -m "%MENSAJE%"
if errorlevel 1 (
    echo [ERROR] Fallo "git commit" (puede que no hubiera nada para commitear).
    goto fin
)

echo Subiendo a GitHub...
git push
if errorlevel 1 (
    echo.
    echo [ERROR] Fallo "git push". Revisa la conexion o las credenciales/token.
    goto fin
)

echo.
echo ============================================
echo   LISTO. Cambios publicados en GitHub.
echo ============================================

:fin
echo.
pause
endlocal
