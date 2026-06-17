# ============================================================
#  Portal-Tray.ps1
#  Icono de bandeja para monitorear y controlar el Portal de Clientes.
#
#  Icono principal (estado del servicio del portal + APIs de BAS):
#    Verde    = portal activo y todas las APIs de BAS responden
#    Amarillo = portal activo pero alguna API de BAS no responde
#    Rojo     = portal detenido
#    Gris     = portal no instalado
#
#  Submenu "APIs de BAS": una linea por base, con punto de color
#    Verde = responde / Rojo = no responde / Gris = base inactiva.
#    (El estado lo informa el propio portal en /api/health/apis.)
#
#  Requiere correr elevado (la tarea programada lo hace solo).
# ============================================================

$ServiceName = "PortalClientes"
$Port        = 5080
$BaseLocal   = "http://localhost:$Port"
$PortalUrl   = "$BaseLocal/portal.html"
$IntranetUrl = "$BaseLocal/intranet.html"
$HealthUrl   = "$BaseLocal/api/health/apis"

# --- Log de errores: si el tray crashea (corre oculto), el motivo queda aca ---
$LogErr = "C:\Agente\Portal-Tray-error.log"
trap {
    try { ("[" + (Get-Date) + "] " + ($_ | Out-String)) | Out-File -FilePath $LogErr -Append } catch {}
    continue
}

# --- Ocultar la ventana de consola de PowerShell ---
Add-Type -Name Win -Namespace Native -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
'@
$h = [Native.Win]::GetConsoleWindow()
if ($h -ne [System.IntPtr]::Zero) { [Native.Win]::ShowWindow($h, 0) | Out-Null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# --- Estado global de las APIs (lo actualiza el timer lento) ---
$script:apisOverall = "unknown"   # ok | warn | down | unknown

# --- Icono de bandeja: cuadrado de color con un rayo blanco ---
function New-DotIcon([System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap 16,16
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Cuadrado de fondo lo mas grande posible (16x16) con el color de estado
    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillRectangle($brush, 0, 0, 16, 16)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110,0,0,0)), 1
    $g.DrawRectangle($pen, 0, 0, 15, 15)

    # Rayo blanco encima
    $pts = [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(10,1),
        [System.Drawing.Point]::new(3,9),
        [System.Drawing.Point]::new(7,9),
        [System.Drawing.Point]::new(6,15),
        [System.Drawing.Point]::new(13,7),
        [System.Drawing.Point]::new(9,7),
        [System.Drawing.Point]::new(11,1)
    )
    $bolt = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($bolt, $pts)

    $g.Dispose(); $brush.Dispose(); $pen.Dispose(); $bolt.Dispose()
    return [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}
# --- Punto de color chico (para las lineas del submenu de APIs) ---
function New-DotImage([System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap 16,16
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillEllipse($brush, 4, 4, 8, 8)
    $g.Dispose(); $brush.Dispose()
    return $bmp
}
$cVerde = [System.Drawing.Color]::FromArgb(40,180,70)
$cRojo  = [System.Drawing.Color]::FromArgb(210,50,50)
$cAmar  = [System.Drawing.Color]::FromArgb(230,170,20)
$cGris  = [System.Drawing.Color]::FromArgb(150,150,150)

$icoRunning = New-DotIcon $cVerde
$icoWarn    = New-DotIcon $cAmar
$icoStopped = New-DotIcon $cRojo
$icoUnknown = New-DotIcon $cGris

$dotVerde = New-DotImage $cVerde
$dotRojo  = New-DotImage $cRojo
$dotGris  = New-DotImage $cGris

# --- Icono de bandeja ---
$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Icon = $icoUnknown
$notify.Text = "Portal Clientes"
$notify.Visible = $true

# --- Menu contextual ---
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miEstado    = $menu.Items.Add("Estado: ...");  $miEstado.Enabled = $false
$menu.Items.Add("-") | Out-Null
$miIniciar   = $menu.Items.Add("Iniciar")
$miDetener   = $menu.Items.Add("Detener")
$miReiniciar = $menu.Items.Add("Reiniciar")
$menu.Items.Add("-") | Out-Null
$miApis      = New-Object System.Windows.Forms.ToolStripMenuItem
$miApis.Text = "APIs de BAS: ..."
$menu.Items.Add($miApis) | Out-Null
$menu.Items.Add("-") | Out-Null
$miPortal    = $menu.Items.Add("Abrir portal de clientes")
$miIntranet  = $menu.Items.Add("Abrir intranet")
$miActualizar= $menu.Items.Add("Actualizar estado de APIs")
$menu.Items.Add("-") | Out-Null
$miSalir     = $menu.Items.Add("Salir")
$notify.ContextMenuStrip = $menu

function Get-SvcStatus {
    $s = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $s) { return "NotFound" }
    return $s.Status.ToString()
}

function Show-Balloon($title, $msg, $icon) {
    $notify.BalloonTipTitle = $title
    $notify.BalloonTipText  = $msg
    $notify.BalloonTipIcon  = $icon
    $notify.ShowBalloonTip(3000)
}

# --- Refresca el icono y el texto segun el servicio + el ultimo estado de APIs ---
function Update-State {
    $st = Get-SvcStatus
    switch ($st) {
        "NotFound" {
            $notify.Icon = $icoUnknown; $notify.Text = "Portal: no instalado"
        }
        "Running" {
            switch ($script:apisOverall) {
                "ok"   { $notify.Icon = $icoRunning; $notify.Text = "Portal: activo - APIs OK" }
                "warn" { $notify.Icon = $icoWarn;    $notify.Text = "Portal: activo - APIs con fallas" }
                "down" { $notify.Icon = $icoWarn;    $notify.Text = "Portal: activo - APIs sin respuesta" }
                default { $notify.Icon = $icoRunning; $notify.Text = "Portal: activo" }
            }
        }
        default {
            $notify.Icon = $icoStopped; $notify.Text = "Portal: $st"
        }
    }
    # Limitar el texto del tooltip a 63 caracteres (limite de NotifyIcon)
    if ($notify.Text.Length -gt 63) { $notify.Text = $notify.Text.Substring(0,63) }
    $miEstado.Text = "Estado del portal: $st"
}

# --- Consulta /api/health/apis y reconstruye el submenu de APIs ---
function Update-Apis {
    $miApis.DropDownItems.Clear()

    if ((Get-SvcStatus) -ne "Running") {
        $script:apisOverall = "unknown"
        $x = $miApis.DropDownItems.Add("(portal detenido)"); $x.Enabled = $false
        $miApis.Text = "APIs de BAS: --"
        Update-State
        return
    }

    try {
        $resp = Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 6 -ErrorAction Stop
        $apis = @($resp.apis)

        if ($apis.Count -eq 0) {
            $d = $miApis.DropDownItems.Add("(no hay bases configuradas)"); $d.Enabled = $false
            $script:apisOverall = "ok"
            $miApis.Text = "APIs de BAS: 0"
            Update-State
            return
        }

        $total = 0; $ok = 0
        foreach ($a in $apis) {
            $estadoTxt = if ($a.responde) { "responde ($($a.ms) ms)" } else { "SIN RESPUESTA" }
            $sufijo    = if (-not $a.activa) { "  -  inactiva" } else { "" }

            $it = New-Object System.Windows.Forms.ToolStripMenuItem
            $it.Text = "{0}   {1}{2}" -f $a.nombre, $estadoTxt, $sufijo
            if (-not $a.activa)      { $it.Image = $dotGris }
            elseif ($a.responde)     { $it.Image = $dotVerde }
            else                     { $it.Image = $dotRojo }

            # Click abre la URL de esa WebAPI (util para inspeccionar / swagger)
            $u = [string]$a.url
            $it.add_Click({ if ($u) { Start-Process $u } }.GetNewClosure())
            $miApis.DropDownItems.Add($it) | Out-Null

            # Para el resumen contamos solo las bases ACTIVAS (las que el portal usa)
            if ($a.activa) { $total++; if ($a.responde) { $ok++ } }
        }

        if ($total -eq 0) {
            $script:apisOverall = "ok"          # solo hay bases inactivas
            $miApis.Text = "APIs de BAS: (todas inactivas)"
        } else {
            $miApis.Text = "APIs de BAS: $ok/$total OK"
            if     ($ok -eq $total) { $script:apisOverall = "ok" }
            elseif ($ok -eq 0)      { $script:apisOverall = "down" }
            else                    { $script:apisOverall = "warn" }
        }
    }
    catch {
        $script:apisOverall = "warn"
        $e = $miApis.DropDownItems.Add("(no se pudo consultar el estado)"); $e.Enabled = $false
        $miApis.Text = "APIs de BAS: ?"
    }
    Update-State
}

# --- Acciones del servicio ---
$miIniciar.add_Click({
    try { Start-Service $ServiceName -ErrorAction Stop; Show-Balloon "Portal Clientes" "Servicio iniciado." ([System.Windows.Forms.ToolTipIcon]::Info) }
    catch { Show-Balloon "Portal Clientes" ("No se pudo iniciar: " + $_.Exception.Message) ([System.Windows.Forms.ToolTipIcon]::Error) }
    Update-State; Update-Apis
})
$miDetener.add_Click({
    try { Stop-Service $ServiceName -ErrorAction Stop; Show-Balloon "Portal Clientes" "Servicio detenido." ([System.Windows.Forms.ToolTipIcon]::Info) }
    catch { Show-Balloon "Portal Clientes" ("No se pudo detener: " + $_.Exception.Message) ([System.Windows.Forms.ToolTipIcon]::Error) }
    Update-State; Update-Apis
})
$miReiniciar.add_Click({
    try { Restart-Service $ServiceName -ErrorAction Stop; Show-Balloon "Portal Clientes" "Servicio reiniciado." ([System.Windows.Forms.ToolTipIcon]::Info) }
    catch { Show-Balloon "Portal Clientes" ("No se pudo reiniciar: " + $_.Exception.Message) ([System.Windows.Forms.ToolTipIcon]::Error) }
    Update-State; Start-Sleep -Milliseconds 800; Update-Apis
})
$miPortal.add_Click({ Start-Process $PortalUrl })
$miIntranet.add_Click({ Start-Process $IntranetUrl })
$miActualizar.add_Click({ Update-Apis })
$miSalir.add_Click({
    $notify.Visible = $false
    $notify.Dispose()
    [System.Windows.Forms.Application]::Exit()
})

# Doble clic en el icono abre la intranet
$notify.add_MouseDoubleClick({ Start-Process $IntranetUrl })

# Habilitar/deshabilitar opciones segun el estado al abrir el menu
$menu.add_Opening({
    $st = Get-SvcStatus
    $miIniciar.Enabled   = ($st -eq "Stopped")
    $miDetener.Enabled   = ($st -eq "Running")
    $miReiniciar.Enabled = ($st -eq "Running")
})

# Refresco del estado del servicio cada 4 segundos (liviano)
$timerSvc = New-Object System.Windows.Forms.Timer
$timerSvc.Interval = 4000
$timerSvc.add_Tick({ Update-State })
$timerSvc.Start()

# Refresco del estado de las APIs cada 15 segundos (hace HTTP al portal)
$timerApis = New-Object System.Windows.Forms.Timer
$timerApis.Interval = 15000
$timerApis.add_Tick({ Update-Apis })
$timerApis.Start()

# Estado inicial (el Update-Apis va protegido: si falla el HTTP, no tumba el tray)
Update-State
try { Update-Apis } catch { }

$ctx = New-Object System.Windows.Forms.ApplicationContext
[System.Windows.Forms.Application]::Run($ctx)
