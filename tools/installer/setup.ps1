# Instalador de HKMultiplayer (lo abre "HKMultiplayer Setup.exe", tools/installer/launcher).
# Sin modo: ventana para instalar, actualizar y desinstalar, en español o en inglés según el idioma de Windows.
# Con -Mode install|uninstall: en la consola, sin ventana (para pruebas o para quien lo prefiera).
param([string]$Mode = '', [string]$GameDir = '')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'install.ps1')
. (Join-Path $PSScriptRoot 'uninstall.ps1')

$version = Get-PackageVersion
$packageRoot = Split-Path -Parent $PSScriptRoot
$script:Changing = $false

# =====================================================================================================
# Consola
# =====================================================================================================

function Invoke-Console([string]$mode, [string]$given) {
    $action = if ($mode -eq 'install') { L 'instalación' 'install' } else { L 'desinstalación' 'uninstall' }
    Write-Host "=== HKMultiplayer $($version): $action ===" -ForegroundColor Cyan
    Write-Host ''
    try {
        $game = Resolve-GameDir $given
        if ($mode -eq 'install') {
            Invoke-Install $game
            Write-Host ''
            Write-Ok (L 'Instalación completa.' 'Installation complete.')
            Write-Host (L 'Abre Hollow Knight desde Steam: en el menú principal aparece "Multijugador".' 'Open Hollow Knight from Steam: "Multiplayer" appears in the main menu.')
            Write-Host (L 'Cómo hostear, unirse y abrir el puerto: LEEME.txt.' 'How to host, join and open the port: README.txt.')
        } else {
            $clean = Invoke-Uninstall $game
            Write-Host ''
            if ($clean) {
                Write-Ok (L 'Desinstalado: el juego vuelve a estar como antes de instalar HKMultiplayer.' 'Uninstalled: the game is back to how it was before installing HKMultiplayer.')
            } else {
                return 2
            }
        }
        return 0
    } catch {
        Write-Host ''
        $what = if ($mode -eq 'install') { L 'No se pudo instalar: ' 'Could not install: ' } else { L 'No se pudo desinstalar: ' 'Could not uninstall: ' }
        Write-Out 'error' ($what + (Get-ErrorText $_))
        if ($script:Changing) {
            Write-Out 'error' (L 'Si se quedó a medias, la desinstalación deja el juego como estaba.' 'If it stopped halfway, uninstalling puts the game back as it was.')
        }
        return 1
    }
}

if ($Mode -eq 'install' -or $Mode -eq 'uninstall') {
    exit (Invoke-Console $Mode $GameDir)
}

# =====================================================================================================
# Ventana
# =====================================================================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# La consola de PowerShell no hace falta con la ventana: si la hay (abierto a mano, sin el .exe), se esconde
try {
    Add-Type -Namespace HKMultiplayerSetup -Name ConsoleWindow -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr handle, int command);
[DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string id);
'@
    # Programa propio para la barra de tareas: si no, Windows agrupa la ventana con PowerShell y enseña su icono
    [void][HKMultiplayerSetup.ConsoleWindow]::SetCurrentProcessExplicitAppUserModelID('HKMultiplayer.Setup')
    $console = [HKMultiplayerSetup.ConsoleWindow]::GetConsoleWindow()
    if ($console -ne [System.IntPtr]::Zero) { [void][HKMultiplayerSetup.ConsoleWindow]::ShowWindow($console, 0) }
} catch { }

function New-Color([string]$hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }

# Colores de los menús del juego: fondo casi negro azulado, texto blanco, el azul del alma para lo importante
$colorBack = New-Color '#0f121a'
$colorPanel = New-Color '#181d29'
$colorLine = New-Color '#2a3142'
$colorText = New-Color '#e8ecf3'
$colorDim = New-Color '#8c96a8'
$colorAccent = New-Color '#7fb2ff'
$colorAccentDark = New-Color '#2f5fb3'
$colorOk = New-Color '#7ddc8f'
$colorWarn = New-Color '#f0c86a'
$colorError = New-Color '#ff7b7b'

$fontTitle = New-Object System.Drawing.Font('Georgia', 26, [System.Drawing.FontStyle]::Bold)
$fontText = New-Object System.Drawing.Font('Segoe UI', 10)
$fontBold = New-Object System.Drawing.Font('Segoe UI Semibold', 10)
$fontButton = New-Object System.Drawing.Font('Segoe UI Semibold', 11)
$fontLog = New-Object System.Drawing.Font('Segoe UI', 9.5)

function New-Label([string]$text, [int]$x, [int]$y, [int]$width, [int]$height, $font, $color) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $text
    $label.Location = New-Object System.Drawing.Point($x, $y)
    $label.Size = New-Object System.Drawing.Size($width, $height)
    $label.Font = $font
    $label.ForeColor = $color
    $label.BackColor = [System.Drawing.Color]::Transparent
    return $label
}

function New-Button([string]$text, [int]$x, [int]$y, [int]$width, [bool]$primary) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $text
    $button.Location = New-Object System.Drawing.Point($x, $y)
    $button.Size = New-Object System.Drawing.Size($width, 42)
    $button.Font = $fontButton
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.Cursor = [System.Windows.Forms.Cursors]::Hand
    if ($primary) {
        $button.BackColor = $colorAccentDark
        $button.ForeColor = [System.Drawing.Color]::White
        $button.FlatAppearance.BorderColor = $colorAccent
    } else {
        $button.BackColor = $colorPanel
        $button.ForeColor = $colorText
        $button.FlatAppearance.BorderColor = $colorLine
    }
    $button.FlatAppearance.MouseOverBackColor = $colorAccent
    $button.FlatAppearance.MouseDownBackColor = $colorAccentDark
    # Desactivado, Windows pinta el texto en un gris oscuro que casi no se lee sobre el fondo: se vuelve a dibujar el
    # botón con el texto en blanco (ForeColor no cuenta en un botón desactivado)
    $button.Add_Paint({
        param($sender, $e)
        if ($sender.Enabled) { return }
        $bounds = $sender.ClientRectangle
        $back = New-Object System.Drawing.SolidBrush($sender.BackColor)
        $e.Graphics.FillRectangle($back, $bounds)
        $back.Dispose()
        $pen = New-Object System.Drawing.Pen($sender.FlatAppearance.BorderColor)
        $e.Graphics.DrawRectangle($pen, 0, 0, $bounds.Width - 1, $bounds.Height - 1)
        $pen.Dispose()
        $flags = [System.Windows.Forms.TextFormatFlags]::HorizontalCenter -bor [System.Windows.Forms.TextFormatFlags]::VerticalCenter -bor [System.Windows.Forms.TextFormatFlags]::SingleLine
        [System.Windows.Forms.TextRenderer]::DrawText($e.Graphics, $sender.Text, $sender.Font, $bounds, [System.Drawing.Color]::White, $flags)
    })
    return $button
}

$form = New-Object System.Windows.Forms.Form
$form.Text = (L 'HKMultiplayer: instalador' 'HKMultiplayer setup')
$form.ClientSize = New-Object System.Drawing.Size(720, 560)
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.BackColor = $colorBack
$form.ForeColor = $colorText
$form.Font = $fontText
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi
# El icono del instalador, con todos sus tamaños (el de la barra de tareas es más grande que el del título); si falta,
# el de "HKMultiplayer Setup.exe" y, si tampoco está, el de PowerShell
$iconFile = Join-Path $PSScriptRoot 'setup.ico'
$setupExe = Join-Path $packageRoot 'HKMultiplayer Setup.exe'
try {
    $form.Icon = if (Test-Path -LiteralPath $iconFile) { New-Object System.Drawing.Icon($iconFile) }
    elseif (Test-Path -LiteralPath $setupExe) { [System.Drawing.Icon]::ExtractAssociatedIcon($setupExe) }
    else { [System.Drawing.Icon]::ExtractAssociatedIcon((Get-Process -Id $PID).Path) }
} catch { }

# Cabecera
$form.Controls.Add((New-Label 'HKMultiplayer' 24 16 330 48 $fontTitle $colorText))
$subtitle = (L 'Mod cooperativo para Hollow Knight' 'Co-op mod for Hollow Knight') + $(if ($version) { '   ·   ' + (L 'versión ' 'version ') + $version } else { '' })
$form.Controls.Add((New-Label $subtitle 28 66 350 22 $fontText $colorDim))

# Cartel de versión nueva (arriba a la derecha): sale solo si GitHub tiene una más nueva que este paquete
$banner = New-Object System.Windows.Forms.Panel
$banner.Location = New-Object System.Drawing.Point(392, 14)
$banner.Size = New-Object System.Drawing.Size(304, 76)
$banner.BackColor = New-Color '#1c2b4a'
$banner.Visible = $false
$banner.Add_Paint({
        param($sender, $e)
        $pen = New-Object System.Drawing.Pen($colorAccent)
        $e.Graphics.DrawRectangle($pen, 0, 0, $sender.Width - 1, $sender.Height - 1)
        $pen.Dispose()
    })
$bannerTitle = New-Label (L '¡Hay una versión nueva!' 'A new version is out!') 12 7 284 22 $fontBold $colorAccent
$bannerVersion = New-Label '' 12 29 284 20 $fontText $colorText
$bannerText = New-Object System.Windows.Forms.LinkLabel
$bannerText.Location = New-Object System.Drawing.Point(12, 50)
$bannerText.Size = New-Object System.Drawing.Size(284, 20)
$bannerText.Font = $fontText
$bannerText.ForeColor = $colorText
$bannerText.LinkColor = $colorAccent
$bannerText.ActiveLinkColor = [System.Drawing.Color]::White
$bannerText.BackColor = [System.Drawing.Color]::Transparent
$banner.Controls.AddRange(@($bannerTitle, $bannerVersion, $bannerText))
$form.Controls.Add($banner)
$line = New-Object System.Windows.Forms.Panel
$line.Location = New-Object System.Drawing.Point(24, 98)
$line.Size = New-Object System.Drawing.Size(672, 1)
$line.BackColor = $colorLine
$form.Controls.Add($line)

# Carpeta del juego
$form.Controls.Add((New-Label (L 'Carpeta de Hollow Knight' 'Hollow Knight folder') 24 114 400 22 $fontBold $colorText))
$folder = New-Object System.Windows.Forms.ComboBox
$folder.Location = New-Object System.Drawing.Point(24, 140)
$folder.Size = New-Object System.Drawing.Size(560, 28)
$folder.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDown
$folder.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$folder.BackColor = $colorPanel
$folder.ForeColor = $colorText
$form.Controls.Add($folder)

$browse = New-Button (L 'Buscar…' 'Browse…') 594 136 102 $false
$browse.Height = 32
$browse.Font = $fontText
$form.Controls.Add($browse)

$status = New-Label '' 24 176 672 22 $fontText $colorDim
$form.Controls.Add($status)

# Botones
$install = New-Button (L 'Instalar' 'Install') 24 212 220 $true
$uninstall = New-Button (L 'Desinstalar' 'Uninstall') 256 212 200 $false
$readme = New-Button (L 'Instrucciones' 'Instructions') 468 212 228 $false
$form.Controls.AddRange(@($install, $uninstall, $readme))

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Location = New-Object System.Drawing.Point(24, 266)
$progress.Size = New-Object System.Drawing.Size(672, 4)
$progress.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
$progress.MarqueeAnimationSpeed = 25
$progress.Visible = $false
$form.Controls.Add($progress)

# Registro de lo que se hace
$log = New-Object System.Windows.Forms.RichTextBox
$log.Location = New-Object System.Drawing.Point(24, 278)
$log.Size = New-Object System.Drawing.Size(672, 222)
$log.BackColor = (New-Color '#0a0c12')
$log.ForeColor = $colorText
$log.Font = $fontLog
$log.ReadOnly = $true
$log.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$log.ScrollBars = [System.Windows.Forms.RichTextBoxScrollBars]::Vertical
$form.Controls.Add($log)

$footer = L 'Tus partidas normales no se tocan. Al desinstalar, el juego queda como estaba.' 'Your normal saves are not touched. Uninstalling puts the game back as it was.'
$form.Controls.Add((New-Label $footer 24 514 520 36 $fontText $colorDim))
$notes = New-Button (L 'Novedades' "What's new") 556 510 140 $false
$notes.Height = 34
$notes.Font = $fontBold
$form.Controls.Add($notes)

function Add-Log([string]$kind, [string]$text) {
    $color = switch ($kind) { 'step' { $colorAccent } 'warn' { $colorWarn } 'ok' { $colorOk } 'error' { $colorError } default { $colorText } }
    $prefix = switch ($kind) { 'step' { '›  ' } 'warn' { '!  ' } 'ok' { '✔  ' } 'error' { '✖  ' } default { '    ' } }
    $log.SelectionStart = $log.TextLength
    $log.SelectionLength = 0
    $log.SelectionColor = $color
    $log.AppendText($prefix + $text + "`r`n")
    $log.SelectionColor = $log.ForeColor
    $log.ScrollToCaret()
    [System.Windows.Forms.Application]::DoEvents()
}
$script:OutputSink = { param($kind, $text) Add-Log $kind $text }

# Estado de la carpeta elegida: si es la del juego y si el mod ya está
function Update-Status {
    $game = ConvertTo-GameDir $folder.Text
    if (-not $game) {
        $status.Text = '●  ' + (L 'Elige la carpeta del juego (la que tiene hollow_knight.exe).' 'Choose the game folder (the one with hollow_knight.exe).')
        $status.ForeColor = $colorWarn
        $install.Text = L 'Instalar' 'Install'
        $install.Enabled = $false
        $uninstall.Enabled = $false
        return
    }
    $installed = Get-InstalledVersion $game
    if ($installed) {
        $status.Text = '●  ' + (L "HKMultiplayer $installed está instalado en esta carpeta." "HKMultiplayer $installed is installed in this folder.")
        $status.ForeColor = $colorOk
        $install.Text = L 'Actualizar' 'Update'
    } else {
        $status.Text = '●  ' + (L 'HKMultiplayer no está instalado en esta carpeta.' "HKMultiplayer isn't installed in this folder.")
        $status.ForeColor = $colorDim
        $install.Text = L 'Instalar' 'Install'
    }
    $install.Enabled = $true
    $uninstall.Enabled = [bool]$installed -or (Test-Path -LiteralPath (Join-Path $game $BackupName))
}

function Set-Busy([bool]$busy) {
    $progress.Visible = $busy
    foreach ($control in @($install, $uninstall, $readme, $browse, $folder)) { $control.Enabled = -not $busy }
    $form.Cursor = if ($busy) { [System.Windows.Forms.Cursors]::WaitCursor } else { [System.Windows.Forms.Cursors]::Default }
    [System.Windows.Forms.Application]::DoEvents()
}

function Show-Message([string]$text, [string]$icon) {
    [void][System.Windows.Forms.MessageBox]::Show($form, $text, 'HKMultiplayer', [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::$icon)
}

$browse.Add_Click({
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = L 'Elige la carpeta de Hollow Knight (la que tiene hollow_knight.exe). En Steam: clic derecho en el juego > Administrar > Ver archivos locales.' `
            'Choose the Hollow Knight folder (the one with hollow_knight.exe). In Steam: right-click the game > Manage > Browse local files.'
        $dialog.ShowNewFolderButton = $false
        if ($folder.Text -and (Test-Path -LiteralPath $folder.Text)) { $dialog.SelectedPath = $folder.Text }
        if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
            $game = ConvertTo-GameDir $dialog.SelectedPath
            $folder.Text = if ($game) { $game } else { $dialog.SelectedPath }
            Update-Status
        }
    })
$folder.Add_TextChanged({ Update-Status })
$folder.Add_SelectedIndexChanged({ Update-Status })

$install.Add_Click({
        $game = ConvertTo-GameDir $folder.Text
        if (-not $game) { return }
        # Se dice antes qué otros mods se apartan: también uno que el jugador volvió a activar después de otra instalación
        $others = @(Get-OtherMods $game | ForEach-Object { $_.Name })
        if ($others.Count -gt 0) {
            $answer = [System.Windows.Forms.MessageBox]::Show($form,
                (L ("Estos mods se apartarán a Mods\Disabled (no funcionan con la Modding API de HKMultiplayer; al desinstalar vuelven):`n`n" +
                        ($others -join "`n") + "`n`n¿Seguir?") `
                        ("These mods will be moved to Mods\Disabled (they don't work with HKMultiplayer's Modding API; uninstalling brings them back):`n`n" +
                        ($others -join "`n") + "`n`nContinue?")),
                'HKMultiplayer', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
            if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        }
        $log.Clear()
        Set-Busy $true
        try {
            Invoke-Install $game
            Add-Log 'ok' (L 'Instalación completa.' 'Installation complete.')
            Add-Log 'note' (L 'Abre Hollow Knight desde Steam: en el menú principal aparece "Multijugador".' 'Open Hollow Knight from Steam: "Multiplayer" appears in the main menu.')
            Set-Busy $false
            Update-Status
            Show-Message (L "¡Listo! HKMultiplayer $version está instalado.`n`nAbre Hollow Knight desde Steam: en el menú principal aparece ""Multijugador"". Cómo hostear y unirse: botón ""Instrucciones""." `
                    "Done! HKMultiplayer $version is installed.`n`nOpen Hollow Knight from Steam: ""Multiplayer"" appears in the main menu. How to host and join: ""Instructions"" button.") 'Information'
        } catch {
            $text = Get-ErrorText $_
            Add-Log 'error' ((L 'No se pudo instalar: ' 'Could not install: ') + $text)
            if ($script:Changing) { Add-Log 'error' (L 'Si se quedó a medias, "Desinstalar" deja el juego como estaba.' 'If it stopped halfway, "Uninstall" puts the game back as it was.') }
            Set-Busy $false
            Update-Status
            Show-Message ((L 'No se pudo instalar:' 'Could not install:') + "`n`n" + $text) 'Error'
        }
    })

$uninstall.Add_Click({
        $game = ConvertTo-GameDir $folder.Text
        if (-not $game) { return }
        $answer = [System.Windows.Forms.MessageBox]::Show($form,
            (L "¿Quitar HKMultiplayer y dejar el juego como estaba?`n`nTus mundos coop, tus partidas y tus skins no se borran." `
                    "Remove HKMultiplayer and put the game back as it was?`n`nYour coop worlds, saves and skins are not deleted."),
            'HKMultiplayer', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        $log.Clear()
        Set-Busy $true
        try {
            $clean = Invoke-Uninstall $game
            if ($clean) { Add-Log 'ok' (L 'Desinstalado: el juego vuelve a estar como antes de instalar HKMultiplayer.' 'Uninstalled: the game is back to how it was before installing HKMultiplayer.') }
            Set-Busy $false
            Update-Status
            if ($clean) { Show-Message (L 'HKMultiplayer se desinstaló. El juego vuelve a estar como antes.' 'HKMultiplayer was uninstalled. The game is back to how it was.') 'Information' }
        } catch {
            $text = Get-ErrorText $_
            Add-Log 'error' ((L 'No se pudo desinstalar: ' 'Could not uninstall: ') + $text)
            Set-Busy $false
            Update-Status
            Show-Message ((L 'No se pudo desinstalar:' 'Could not uninstall:') + "`n`n" + $text) 'Error'
        }
    })

$readme.Add_Click({
        $file = Join-Path $packageRoot (L 'LEEME.txt' 'README.txt')
        if (-not (Test-Path -LiteralPath $file)) { $file = Join-Path $packageRoot 'README.txt' }
        if (Test-Path -LiteralPath $file) { Start-Process -FilePath $file }
    })

# Versiones publicadas. Al abrir, sin parar la ventana, se pide a GitHub la lista (todas se publican como pre-release,
# así que no sirve "latest"): si hay una más nueva que este paquete sale el cartel de arriba. "Novedades" enseña lo que
# trae esa versión nueva o, si no la hay, la de este paquete (el apartado "Novedades de la ..." de sus notas)
$releasesApi = 'https://api.github.com/repos/Yesid-Robayo/HKMultiplayerMOD/releases?per_page=20'
$releasesPage = 'https://github.com/Yesid-Robayo/HKMultiplayerMOD/releases'
$script:Releases = $null
$script:Newest = $null

function Get-ReleaseVersion($release) {
    $parsed = $null
    # Los números del principio: una etiqueta como "v0.26.0-hotfix" cuenta como 0.26.0 (antes se ignoraba sin avisar)
    if (([string]$release.tag_name) -match '^[vV]?(\d+(\.\d+){1,3})' -and [Version]::TryParse($Matches[1], [ref]$parsed)) { return $parsed }
    return $null
}

function Set-Releases([string]$json) {
    # En PowerShell 5.1 ConvertFrom-Json da la lista como un solo objeto: guardada en una variable, la tubería sí la
    # recorre versión a versión
    $list = ConvertFrom-Json $json
    $script:Releases = @($list | Where-Object { -not $_.draft -and (Get-ReleaseVersion $_) })
    $script:Newest = $script:Releases | Sort-Object { Get-ReleaseVersion $_ } -Descending | Select-Object -First 1
    $current = $null
    [void][Version]::TryParse([string]$version, [ref]$current)
    if ($script:Newest -and $current -and (Get-ReleaseVersion $script:Newest) -gt $current) {
        $newestText = ([string]$script:Newest.tag_name).TrimStart('v', 'V')
        $bannerVersion.Text = L "La $newestText (este paquete es la $version)" "$newestText (this package is $version)"
        $bannerText.Text = L 'Descargarla' 'Download it'
        $banner.Visible = $true
    }
}

function New-WebClient {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $client = New-Object System.Net.WebClient
    $client.Encoding = [System.Text.Encoding]::UTF8
    $client.Headers.Add('User-Agent', "HKMultiplayer-Setup/$version")
    $client.Headers.Add('Accept', 'application/vnd.github+json')
    return $client
}

function Start-ReleaseCheck {
    try {
        $client = New-WebClient
        # La respuesta llega en el hilo de la ventana (WebClient usa su contexto): se puede tocar el cartel ahí
        $client.Add_DownloadStringCompleted({
                param($sender, $e)
                if (-not $e.Cancelled -and -not $e.Error) {
                    try { Set-Releases $e.Result } catch { }
                }
                $sender.Dispose()
            })
        $client.DownloadStringAsync([Uri]$releasesApi)
    } catch { }
}

# El apartado "Novedades de la X" / "What's new in X" de las notas, en el idioma de Windows, de Markdown a texto
function Get-ReleaseNotes($release) {
    $body = ([string]$release.body) -replace "`r", ''
    $marker = if ($Spanish) { '### Novedades' } else { "### What's new" }
    $start = $body.IndexOf($marker)
    if ($start -ge 0) {
        $rest = $body.Substring($start)
        $next = $rest.IndexOf("`n### ", 1)
        $body = if ($next -gt 0) { $rest.Substring(0, $next) } else { $rest }
    }
    # Sin el título del apartado (la ventana ya lo tiene)
    $body = $body -replace '^###[^\n]*\n', ''
    $body = $body -replace '(?m)^#+\s*', '' -replace '\[([^\]]+)\]\([^)]+\)', '$1' -replace '\*\*', '' -replace '`', ''
    $body = $body -replace '(?m)^\s*[-*]\s+', ([string][char]0x2022 + '  ') -replace '(?<![\w*])\*([^*\n]+)\*(?![\w*])', '$1'
    return $body.Trim()
}

function Show-Notes {
    if (-not $script:Releases) {
        # Aún no llegó (o falló al abrir): se pide ya, sin parar la ventana y esperando como mucho 10 s (DownloadString
        # en este hilo la dejaba congelada hasta ~100 s sin internet)
        $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
        $script:NotesDone = $false
        try {
            $client = New-WebClient
            $client.Add_DownloadStringCompleted({
                    param($sender, $e)
                    if (-not $e.Cancelled -and -not $e.Error) {
                        try { Set-Releases $e.Result } catch { }
                    }
                    $script:NotesDone = $true
                    $sender.Dispose()
                })
            $client.DownloadStringAsync([Uri]$releasesApi)
            $deadline = (Get-Date).AddSeconds(10)
            while (-not $script:NotesDone -and (Get-Date) -lt $deadline) {
                [System.Windows.Forms.Application]::DoEvents()
                Start-Sleep -Milliseconds 50
            }
            if (-not $script:NotesDone) { $client.CancelAsync() }
        } catch { }
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
    }
    $current = $null
    [void][Version]::TryParse([string]$version, [ref]$current)
    $release = if ($script:Newest -and $current -and (Get-ReleaseVersion $script:Newest) -gt $current) { $script:Newest }
    else { $script:Releases | Where-Object { (Get-ReleaseVersion $_) -eq $current } | Select-Object -First 1 }
    if (-not $release) {
        $text = if ($script:Releases) { L "Las novedades de la $version aún no están publicadas. Todas las versiones: $releasesPage" "What's new in $version isn't published yet. All versions: $releasesPage" }
        else { L "No se pudieron cargar las novedades (hace falta internet). Están en $releasesPage" "Couldn't load what's new (an internet connection is needed). It's at $releasesPage" }
        Show-Message $text 'Information'
        return
    }

    $tag = ([string]$release.tag_name).TrimStart('v', 'V')
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = (L "Novedades de la $tag" "What's new in $tag")
    $dialog.ClientSize = New-Object System.Drawing.Size(640, 460)
    $dialog.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.BackColor = $colorBack
    $dialog.ForeColor = $colorText
    $dialog.Font = $fontText
    $dialog.Icon = $form.Icon
    $dialog.Controls.Add((New-Label ((L 'Novedades de la ' "What's new in ") + $tag) 24 16 592 30 $fontButton $colorText))
    $box = New-Object System.Windows.Forms.RichTextBox
    $box.Location = New-Object System.Drawing.Point(24, 54)
    $box.Size = New-Object System.Drawing.Size(592, 336)
    $box.BackColor = (New-Color '#0a0c12')
    $box.ForeColor = $colorText
    $box.Font = $fontLog
    $box.ReadOnly = $true
    $box.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $box.ScrollBars = [System.Windows.Forms.RichTextBoxScrollBars]::Vertical
    $box.Text = Get-ReleaseNotes $release
    $dialog.Controls.Add($box)
    $url = [string]$release.html_url
    $web = New-Button (L 'Ver en GitHub' 'Open on GitHub') 24 404 200 $false
    $web.Add_Click({ Start-Process -FilePath $url }.GetNewClosure())
    $close = New-Button (L 'Cerrar' 'Close') 476 404 140 $true
    $close.Add_Click({ $dialog.Close() }.GetNewClosure())
    $dialog.Controls.AddRange(@($web, $close))
    $dialog.AcceptButton = $close
    $dialog.CancelButton = $close
    [void]$dialog.ShowDialog($form)
    $dialog.Dispose()
}

$notes.Add_Click({ Show-Notes })
$bannerText.Add_LinkClicked({ if ($script:Newest) { Start-Process -FilePath ([string]$script:Newest.html_url) } })

# Carpeta inicial: la que se arrastró sobre el .exe, o la que se encuentra
$form.Add_Shown({
        $form.Activate()
        Add-Log 'step' (L 'Buscando Hollow Knight...' 'Looking for Hollow Knight...')
        $found = @()
        try { $found = @(Find-GameDirs) } catch { }
        foreach ($dir in $found) { [void]$folder.Items.Add($dir) }
        $given = ConvertTo-GameDir $GameDir
        if ($given) {
            $folder.Text = $given
        } elseif ($found.Count -gt 0) {
            $folder.SelectedIndex = 0
        }
        $log.Clear()
        # Primero el estado: el botón dice "Instalar" o "Actualizar" y el aviso nombra el mismo
        Update-Status
        if ($found.Count -gt 1 -and -not $given) {
            Add-Log 'note' (L "Encontré el juego en $($found.Count) carpetas: elige la tuya en la lista." "The game was found in $($found.Count) folders: pick yours from the list.")
        } elseif (-not $folder.Text) {
            Add-Log 'warn' (L 'No encontré la carpeta del juego. Pulsa "Buscar…" y elígela.' 'The game folder was not found. Click "Browse…" and choose it.')
        } else {
            Add-Log 'note' (L "Todo listo. Cierra Hollow Knight y pulsa ""$($install.Text)""." "All set. Close Hollow Knight and click ""$($install.Text)"".")
        }
        $folder.SelectionLength = 0
        if ($install.Enabled) { [void]$install.Focus() }
        Start-ReleaseCheck
    })

try {
    [void]$form.ShowDialog()
} catch {
    [void][System.Windows.Forms.MessageBox]::Show((Get-ErrorText $_), 'HKMultiplayer', [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error)
}
