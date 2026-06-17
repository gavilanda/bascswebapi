# ============================================================
#  BAS-WebAPI-Tray.ps1
#  Icono de bandeja para monitorear y controlar el servicio.
#  Verde = activo / Rojo = detenido / Gris = no instalado.
#  Requiere correr elevado (la tarea programada lo hace solo).
# ============================================================

$ServiceName = "BAS-WebAPI"
$Port        = 5080
$SwaggerUrl  = "http://localhost:$Port/swagger"

# --- Ocultar la ventana de consola de PowerShell ---
Add-Type -Name Win -Namespace Native -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
'@
$h = [Native.Win]::GetConsoleWindow()
if ($h -ne [System.IntPtr]::Zero) { [Native.Win]::ShowWindow($h, 0) | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# --- Iconos de color (se crean una sola vez) ---
function New-DotIcon([System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap 16,16
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillEllipse($brush, 2, 2, 11, 11)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90,0,0,0)), 1
    $g.DrawEllipse($pen, 2, 2, 11, 11)
    $g.Dispose(); $brush.Dispose(); $pen.Dispose()
    return [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}
$icoRunning = New-DotIcon ([System.Drawing.Color]::FromArgb(40,180,70))
$icoStopped = New-DotIcon ([System.Drawing.Color]::FromArgb(210,50,50))
$icoUnknown = New-DotIcon ([System.Drawing.Color]::FromArgb(150,150,150))

# --- Icono de bandeja ---
$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Icon = $icoUnknown
$notify.Text = "BAS-WebAPI"
$notify.Visible = $true

# --- Menu contextual ---
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miEstado   = $menu.Items.Add("Estado: ...");  $miEstado.Enabled = $false
$menu.Items.Add("-") | Out-Null
$miIniciar  = $menu.Items.Add("Iniciar")
$miDetener  = $menu.Items.Add("Detener")
$miReiniciar= $menu.Items.Add("Reiniciar")
$menu.Items.Add("-") | Out-Null
$miSwagger  = $menu.Items.Add("Abrir Swagger")
$menu.Items.Add("-") | Out-Null
$miSalir    = $menu.Items.Add("Salir")
$notify.ContextMenuStrip = $menu

function Get-SvcStatus {
    $s = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $s) { return "NotFound" }
    return $s.Status.ToString()
}

function Update-State {
    $st = Get-SvcStatus
    switch ($st) {
        "Running"  { $notify.Icon = $icoRunning; $notify.Text = "BAS-WebAPI: Activo" }
        "NotFound" { $notify.Icon = $icoUnknown; $notify.Text = "BAS-WebAPI: no instalado" }
        default    { $notify.Icon = $icoStopped; $notify.Text = "BAS-WebAPI: $st" }
    }
    $miEstado.Text = "Estado: $st"
}

function Show-Balloon($title, $msg, $icon) {
    $notify.BalloonTipTitle = $title
    $notify.BalloonTipText  = $msg
    $notify.BalloonTipIcon  = $icon
    $notify.ShowBalloonTip(3000)
}

$miIniciar.add_Click({
    try { Start-Service $ServiceName -ErrorAction Stop; Show-Balloon "BAS-WebAPI" "Servicio iniciado." ([System.Windows.Forms.ToolTipIcon]::Info) }
    catch { Show-Balloon "BAS-WebAPI" ("No se pudo iniciar: " + $_.Exception.Message) ([System.Windows.Forms.ToolTipIcon]::Error) }
    Update-State
})
$miDetener.add_Click({
    try { Stop-Service $ServiceName -ErrorAction Stop; Show-Balloon "BAS-WebAPI" "Servicio detenido." ([System.Windows.Forms.ToolTipIcon]::Info) }
    catch { Show-Balloon "BAS-WebAPI" ("No se pudo detener: " + $_.Exception.Message) ([System.Windows.Forms.ToolTipIcon]::Error) }
    Update-State
})
$miReiniciar.add_Click({
    try { Restart-Service $ServiceName -ErrorAction Stop; Show-Balloon "BAS-WebAPI" "Servicio reiniciado." ([System.Windows.Forms.ToolTipIcon]::Info) }
    catch { Show-Balloon "BAS-WebAPI" ("No se pudo reiniciar: " + $_.Exception.Message) ([System.Windows.Forms.ToolTipIcon]::Error) }
    Update-State
})
$miSwagger.add_Click({ Start-Process $SwaggerUrl })
$miSalir.add_Click({
    $notify.Visible = $false
    $notify.Dispose()
    [System.Windows.Forms.Application]::Exit()
})

# Doble clic en el icono abre Swagger
$notify.add_MouseDoubleClick({ Start-Process $SwaggerUrl })

# Habilitar/deshabilitar opciones segun el estado al abrir el menu
$menu.add_Opening({
    $st = Get-SvcStatus
    $miIniciar.Enabled   = ($st -eq "Stopped")
    $miDetener.Enabled   = ($st -eq "Running")
    $miReiniciar.Enabled = ($st -eq "Running")
})

# Refresco automatico del estado cada 4 segundos
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 4000
$timer.add_Tick({ Update-State })
$timer.Start()

Update-State

$ctx = New-Object System.Windows.Forms.ApplicationContext
[System.Windows.Forms.Application]::Run($ctx)
