# ============================================================
#  BAS-WebAPI-Desinstalar.ps1
#  Elimina TODO lo instalado:
#   - Cierra el tray si esta corriendo
#   - Quita la tarea programada
#   - Detiene y borra el servicio
#   - Borra la regla de firewall
#  Ejecutar en PowerShell COMO ADMINISTRADOR.
# ============================================================

# ---- Parametros (deben coincidir con los de instalacion) ----
$ServiceName = "BAS-WebAPI"
$TaskName    = "BAS-WebAPI-Tray"
$ScriptTray  = "C:\Agente\BAS-WebAPI-Tray.ps1"
$Port        = 5080

# ---- Auto-elevar a Administrador si hace falta (salta el UAC) ----
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# ---- 1) Cerrar el proceso del tray (PowerShell que ejecuta el script del tray) ----
$proc = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*BAS-WebAPI-Tray.ps1*" }
if ($proc) {
    $proc | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Write-Host "Tray cerrado." -ForegroundColor Green
} else {
    Write-Host "El tray no estaba en ejecucion." -ForegroundColor Yellow
}

# ---- 2) Quitar la tarea programada ----
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Tarea programada eliminada." -ForegroundColor Green
} else {
    Write-Host "La tarea programada no existia." -ForegroundColor Yellow
}

# ---- 3) Detener y borrar el servicio ----
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Servicio eliminado." -ForegroundColor Green
} else {
    Write-Host "El servicio no existia." -ForegroundColor Yellow
}

# ---- 4) Borrar la regla de firewall ----
if (Get-NetFirewallRule -DisplayName "BAS-WebAPI $Port" -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -DisplayName "BAS-WebAPI $Port"
    Write-Host "Regla de firewall eliminada." -ForegroundColor Green
} else {
    Write-Host "La regla de firewall no existia." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Desinstalacion completa." -ForegroundColor Cyan
Read-Host "Enter para salir"
