# ============================================================
#  Tray-AgregarRetardo.ps1
#  Agrega 1 minuto de retardo al trigger "al iniciar sesion" de
#  las tareas del tray, para que el icono se registre despues de
#  que explorer.exe tenga lista la barra (evita que no aparezca
#  tras un reinicio). Se autoeleva (pide UAC).
# ============================================================

# --- Autoelevacion ---
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""
    )
    return
}

# --- Cuerpo elevado ---
$tareas = @('BAS-WebAPI-Tray','PortalClientes-Tray')
$retardo = 'PT1M'   # 1 minuto (ISO-8601). Subir a PT2M si hiciera falta.

foreach ($t in $tareas) {
    try {
        $task = Get-ScheduledTask -TaskName $t -ErrorAction Stop
        $task.Triggers[0].Delay = $retardo
        Set-ScheduledTask -TaskName $t -Trigger $task.Triggers -ErrorAction Stop | Out-Null
        $chk = (Get-ScheduledTask -TaskName $t).Triggers[0].Delay
        Write-Host ("OK   {0}  -> Delay = {1}" -f $t, $chk) -ForegroundColor Green
    } catch {
        Write-Host ("FALLO {0}: {1}" -f $t, $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Listo. El cambio se aplica en el proximo reinicio / inicio de sesion." -ForegroundColor Cyan
Read-Host "Presiona ENTER para cerrar"
