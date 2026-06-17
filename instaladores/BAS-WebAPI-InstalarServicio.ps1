# ============================================================
#  BAS-WebAPI-InstalarServicio.ps1
#  Crea el servicio de Windows de la WebAPI de BAS:
#   - Arranque automatico con el servidor
#   - Reinicio automatico ante caidas
#   - Regla de firewall para el puerto
#  Ejecutar en PowerShell COMO ADMINISTRADOR.
# ============================================================

# ---- Parametros (ajustar si hace falta) ----
$ServiceName = "BAS-WebAPI"
$DisplayName = "BAS CS WebAPI"
$Descripcion = "WebAPI de BAS"
$Exe         = "C:\Agente\BAScsWebApi\BASCSWEBAPI.exe"
$Port        = 5080
$Url         = "http://*:$Port"

# ---- Auto-elevar a Administrador si hace falta (salta el UAC) ----
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# ---- Verificar que el ejecutable exista ----
if (-not (Test-Path $Exe)) {
    Write-Host "No se encuentra el ejecutable: $Exe" -ForegroundColor Red
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
New-Service -Name $ServiceName `
  -BinaryPathName "`"$Exe`" --urls $Url" `
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
if (-not (Get-NetFirewallRule -DisplayName "BAS-WebAPI $Port" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "BAS-WebAPI $Port" -Direction Inbound `
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
Write-Host "Listo. Verifica en: http://localhost:$Port/swagger" -ForegroundColor Cyan
Read-Host "Enter para salir"
