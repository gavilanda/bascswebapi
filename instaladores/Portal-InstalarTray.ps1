# ============================================================
#  Portal-InstalarTray.ps1
#  Registra una tarea programada que lanza el icono de bandeja
#  del Portal al iniciar sesion, con privilegios elevados (sin UAC).
#  Ejecutar en PowerShell COMO ADMINISTRADOR.
# ============================================================

# ---- Parametros (ajustar si hace falta) ----
$TaskName   = "PortalClientes-Tray"
$ScriptTray = "C:\Agente\webapi\instaladores\Portal-Tray.ps1"
$Usuario    = "$env:USERDOMAIN\$env:USERNAME"

# ---- Auto-elevar a Administrador si hace falta (salta el UAC) ----
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# ---- Verificar que el script del tray exista ----
if (-not (Test-Path $ScriptTray)) {
    Write-Host "No se encuentra el script del tray: $ScriptTray" -ForegroundColor Red
    Read-Host "Enter para salir"; exit 1
}

# ---- Crear / reemplazar la tarea programada ----
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
  -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$ScriptTray`""

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $Usuario

$principal = New-ScheduledTaskPrincipal -UserId $Usuario `
  -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $TaskName `
  -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Tarea '$TaskName' registrada para el usuario $Usuario." -ForegroundColor Green

# ---- Lanzarla ya mismo para no esperar al proximo inicio de sesion ----
Start-ScheduledTask -TaskName $TaskName
Write-Host "Tray iniciado. Deberia aparecer el icono en la bandeja." -ForegroundColor Cyan
Read-Host "Enter para salir"
