# ============================================================
#  Portal-InstalarServicio.ps1
#  Crea el servicio de Windows del Portal de Clientes (.NET):
#   - Arranque automatico con el servidor
#   - Reinicio automatico ante caidas
#   - Regla de firewall para el puerto
#  Ejecutar en PowerShell COMO ADMINISTRADOR.
#
#  REQUISITOS PREVIOS:
#   1) Tener instalado el "ASP.NET Core Runtime 8" en el server.
#   2) Haber publicado la app:
#        dotnet publish C:\Agente\webapi\PortalClientes.csproj -c Release -o C:\Agente\PortalPublish
#   3) Segun el entorno (parametro $Entorno mas abajo):
#       - "Development" (por defecto, para tu PC): usa appsettings.Development.json,
#         que ya tiene la base SQLite local y la clave de dev. No hace falta nada mas.
#       - "Production" (para el server real): necesita un appsettings.Production.json
#         (al lado del .exe) con los valores reales: ConnectionStrings:PortalDb
#         (ruta ABSOLUTA de la base SQLite, ej. C:\Agente\PortalData\portal-clientes.db),
#         JwtPortal:ClaveSecreta y los BasDestinos. Ese archivo NO se commitea.
# ============================================================

# ---- Parametros (ajustar si hace falta) ----
$ServiceName = "PortalClientes"
$DisplayName = "Portal Clientes"
$Descripcion = "Portal de clientes/proveedores e intranet (integra BAS CS WebAPI)"
$Exe         = "C:\Agente\PortalPublish\PortalClientes.exe"
$Port        = 5080
$Entorno     = "Development"         # en tu PC; en el server de produccion poner "Production"
$Url         = "http://*:$Port"

# ---- Auto-elevar a Administrador si hace falta (salta el UAC) ----
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# ---- Verificar que el ejecutable publicado exista ----
if (-not (Test-Path $Exe)) {
    Write-Host "No se encuentra el ejecutable publicado: $Exe" -ForegroundColor Red
    Write-Host "Publica primero con:" -ForegroundColor Yellow
    Write-Host "  dotnet publish C:\Agente\webapi\PortalClientes.csproj -c Release -o C:\Agente\webapi\publish" -ForegroundColor Yellow
    Read-Host "Enter para salir"; exit 1
}

# ---- Si el servicio ya existe, detenerlo y borrarlo para recrearlo limpio ----
$existente = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existente) {
    Write-Host "El servicio '$ServiceName' ya existe. Se recrea..." -ForegroundColor Yellow
    if ($existente.Status -ne 'Stopped') { Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# ---- 1) Crear el servicio (arranque automatico) ----
#   --urls         : en que puerto/interfaces escucha (todas, para la red interna)
#   --environment  : fuerza el entorno => carga appsettings.Production.json
New-Service -Name $ServiceName `
  -BinaryPathName "`"$Exe`" --urls $Url --environment $Entorno" `
  -DisplayName $DisplayName `
  -Description $Descripcion `
  -StartupType Automatic | Out-Null
Write-Host "Servicio creado." -ForegroundColor Green

# ---- 2) Reinicio automatico ante caidas (5s, 10s, 30s; contador se resetea cada 24hs) ----
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

# ---- 3) Aplicar la recuperacion tambien ante salida con codigo de error (no solo crash) ----
sc.exe failureflag $ServiceName 1 | Out-Null
Write-Host "Reinicio automatico configurado." -ForegroundColor Green

# ---- 4) Regla de firewall para el puerto ----
if (-not (Get-NetFirewallRule -DisplayName "PortalClientes $Port" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "PortalClientes $Port" -Direction Inbound `
      -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
    Write-Host "Regla de firewall creada (puerto $Port)." -ForegroundColor Green
} else {
    Write-Host "La regla de firewall ya existia." -ForegroundColor Yellow
}

# ---- 5) Arrancar y mostrar estado ----
Start-Service $ServiceName
Start-Sleep -Seconds 2
Get-Service $ServiceName | Format-Table -AutoSize

Write-Host ""
Write-Host "Listo. Verifica en: http://localhost:$Port/portal.html" -ForegroundColor Cyan
Write-Host "Intranet en:        http://localhost:$Port/intranet.html" -ForegroundColor Cyan
Write-Host ""
Write-Host "Si el servicio no levanta, revisa el Visor de eventos de Windows" -ForegroundColor DarkGray
Write-Host "(Registro de aplicaciones) y que exista appsettings.Production.json." -ForegroundColor DarkGray
Read-Host "Enter para salir"
