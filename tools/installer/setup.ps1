# Instalador de HKMultiplayer (lo abre "HKMultiplayer Setup.bat").
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

# La consola de PowerShell (abierta minimizada por el .bat) no hace falta con la ventana: se esconde
try {
    Add-Type -Namespace HKMultiplayerSetup -Name ConsoleWindow -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr handle, int command);
'@
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
try { $form.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon((Get-Process -Id $PID).Path) } catch { }

# Cabecera
$form.Controls.Add((New-Label 'HKMultiplayer' 24 16 520 48 $fontTitle $colorText))
$subtitle = (L 'Mod cooperativo para Hollow Knight' 'Co-op mod for Hollow Knight') + $(if ($version) { '   ·   ' + (L 'versión ' 'version ') + $version } else { '' })
$form.Controls.Add((New-Label $subtitle 28 66 660 22 $fontText $colorDim))
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
$form.Controls.Add((New-Label $footer 24 514 672 36 $fontText $colorDim))

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

# Carpeta inicial: la que se arrastró sobre el .bat, o la que se encuentra
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
        if ($found.Count -gt 1 -and -not $given) {
            Add-Log 'note' (L "Encontré el juego en $($found.Count) carpetas: elige la tuya en la lista." "The game was found in $($found.Count) folders: pick yours from the list.")
        } elseif (-not $folder.Text) {
            Add-Log 'warn' (L 'No encontré la carpeta del juego. Pulsa "Buscar…" y elígela.' 'The game folder was not found. Click "Browse…" and choose it.')
        } else {
            Add-Log 'note' (L 'Todo listo. Cierra Hollow Knight y pulsa "Instalar".' 'All set. Close Hollow Knight and click "Install".')
        }
        Update-Status
        $folder.SelectionLength = 0
        if ($install.Enabled) { [void]$install.Focus() }
    })

try {
    [void]$form.ShowDialog()
} catch {
    [void][System.Windows.Forms.MessageBox]::Show((Get-ErrorText $_), 'HKMultiplayer', [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error)
}
