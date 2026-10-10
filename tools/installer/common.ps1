# Funciones comunes del instalador de HKMultiplayer (las carga setup.ps1, junto con install.ps1 y uninstall.ps1).

$ModName = 'HKMultiplayer'
# Mod anterior: también abre la red, así que no puede estar a la vez que este
$OldModName = 'HollowKnightCoop'
# Carpeta (dentro de la del juego) con el respaldo de lo que cambió el instalador y la lista de cambios
$BackupName = 'HKMultiplayer-backup'
$ManifestName = 'manifest.txt'
$ModsRelative = 'hollow_knight_Data/Managed/Mods'

# Idioma de los textos: el de Windows (español si es español; si no, inglés)
$Spanish = [System.Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'es'

function L([string]$spanish, [string]$english) { if ($Spanish) { return $spanish } return $english }

function Get-PackageVersion {
    $file = Join-Path $PSScriptRoot 'version.txt'
    if (Test-Path $file) { return (Get-Content $file -TotalCount 1).Trim() }
    return ''
}

# Salida: en la ventana del instalador (si la hay, con $script:OutputSink) o en la consola, con colores
$script:OutputSink = $null

function Write-Out([string]$kind, [string]$text) {
    if ($script:OutputSink) { & $script:OutputSink $kind $text; return }
    switch ($kind) {
        'step' { Write-Host $text -ForegroundColor Cyan }
        'warn' { Write-Host "  $text" -ForegroundColor Yellow }
        'ok' { Write-Host $text -ForegroundColor Green }
        'error' { Write-Host $text -ForegroundColor Red }
        default { Write-Host "  $text" }
    }
}

function Write-Step([string]$text) { Write-Out 'step' $text }
function Write-Note([string]$text) { Write-Out 'note' $text }
function Write-Warn([string]$text) { Write-Out 'warn' $text }
function Write-Ok([string]$text) { Write-Out 'ok' $text }

# Ruta que da el jugador: admite la del .exe, la de hollow_knight_Data o la de Managed, con o sin comillas
function ConvertTo-GameDir([string]$path) {
    if (-not $path) { return $null }
    $path = $path.Trim().Trim('"').Trim()
    if (-not $path) { return $null }
    $path = $path.TrimEnd('\', '/')
    if ($path -match '\.exe$') { $path = Split-Path -Parent $path }
    for ($i = 0; $i -lt 3 -and $path; $i++) {
        if (Test-Path -LiteralPath (Join-Path $path 'hollow_knight.exe')) {
            return (Resolve-Path -LiteralPath $path).ProviderPath
        }
        $path = Split-Path -Parent $path
    }
    return $null
}

# Carpetas de Steam: la del registro, las bibliotecas de libraryfolders.vdf y las de siempre
function Get-SteamLibraries {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @(
            @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
            @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
            @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' })) {
        try {
            $value = (Get-ItemProperty -Path $key.Path -Name $key.Name -ErrorAction Stop).($key.Name)
            if ($value) { $roots.Add(($value -replace '/', '\')) }
        } catch { }
    }
    $roots.Add("${env:ProgramFiles(x86)}\Steam")
    $roots.Add("$env:ProgramFiles\Steam")

    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        $libraries.Add($root)
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($line in Get-Content -LiteralPath $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') { $libraries.Add(($Matches[1] -replace '\\\\', '\')) }
            }
        }
    }
    return $libraries
}

# Todas las carpetas del juego que se encuentran (sin repetir): Steam, la desinstalación de Steam, GOG y rutas típicas
function Find-GameDirs {
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($library in Get-SteamLibraries) { $candidates.Add((Join-Path $library 'steamapps\common\Hollow Knight')) }
    foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 367520',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 367520')) {
        try {
            $value = (Get-ItemProperty -Path $key -Name 'InstallLocation' -ErrorAction Stop).InstallLocation
            if ($value) { $candidates.Add($value) }
        } catch { }
    }
    # Solo discos locales: una unidad de red desconectada haría esperar al instalador
    $drives = [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady }
    foreach ($drive in $drives) {
        foreach ($relative in @('SteamLibrary\steamapps\common\Hollow Knight', 'Steam\steamapps\common\Hollow Knight',
                'Games\Steam\steamapps\common\Hollow Knight', 'Juegos\Steam\steamapps\common\Hollow Knight',
                'Program Files (x86)\Steam\steamapps\common\Hollow Knight', 'GOG Games\Hollow Knight',
                'Program Files (x86)\GOG Galaxy\Games\Hollow Knight', 'Games\Hollow Knight', 'Juegos\Hollow Knight')) {
            $candidates.Add((Join-Path $drive.RootDirectory.FullName $relative))
        }
    }

    $found = New-Object System.Collections.Generic.List[string]
    foreach ($dir in $candidates) {
        $game = ConvertTo-GameDir $dir
        if ($game -and -not ($found | Where-Object { $_ -ieq $game })) { $found.Add($game) }
    }
    return $found
}

# Carpeta del juego en la consola: la que se pasa (o se arrastra sobre el .bat); si no, la que se encuentra; si hay
# varias o ninguna, se pregunta
function Resolve-GameDir([string]$given) {
    if ($given) {
        $game = ConvertTo-GameDir $given
        if (-not $game) {
            throw (L "En '$given' no está hollow_knight.exe. Indica la carpeta del juego (la que tiene hollow_knight.exe)." `
                    "hollow_knight.exe is not in '$given'. Give the game folder (the one with hollow_knight.exe).")
        }
        return $game
    }

    $found = @(Find-GameDirs)
    if ($found.Count -eq 1) { return $found[0] }
    if ($found.Count -gt 1) {
        Write-Host (L 'Encontré Hollow Knight en varias carpetas:' 'Hollow Knight was found in several folders:')
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host "  $($i + 1). $($found[$i])" }
        $answer = Read-Host (L "Escribe el número de la carpeta que quieres usar (1-$($found.Count))" "Type the number of the folder to use (1-$($found.Count))")
        $index = 0
        if ([int]::TryParse($answer.Trim(), [ref]$index) -and $index -ge 1 -and $index -le $found.Count) {
            return $found[$index - 1]
        }
        throw (L 'No elegiste ninguna carpeta.' 'No folder was chosen.')
    }

    Write-Host (L 'No encontré la carpeta del juego.' "The game folder wasn't found.")
    Write-Host (L 'En Steam: clic derecho en Hollow Knight > Administrar > Ver archivos locales, y copia la ruta de esa carpeta.' `
            'In Steam: right-click Hollow Knight > Manage > Browse local files, and copy the path of that folder.')
    $answer = Read-Host (L 'Pega aquí la ruta de la carpeta de Hollow Knight (la que tiene hollow_knight.exe)' 'Paste the Hollow Knight folder path here (the one with hollow_knight.exe)')
    $game = ConvertTo-GameDir $answer
    if (-not $game) { throw (L "En '$answer' no está hollow_knight.exe." "hollow_knight.exe is not in '$answer'.") }
    return $game
}

# Copias del juego abiertas desde esa carpeta (otras copias en otras carpetas no molestan)
function Get-GameProcesses([string]$gameDir) {
    $prefix = $gameDir.TrimEnd('\') + '\'
    return @(Get-Process hollow_knight -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) })
}

function Assert-GameClosed([string]$gameDir) {
    if ((Get-GameProcesses $gameDir).Count -gt 0) {
        throw (L 'Hollow Knight está abierto. Ciérralo y vuelve a intentarlo.' 'Hollow Knight is running. Close it and try again.')
    }
}

function Join-Game([string]$gameDir, [string]$relative) { return Join-Path $gameDir ($relative -replace '/', '\') }

# Versión del mod instalada en esa carpeta (vacío si no está)
function Get-InstalledVersion([string]$gameDir) {
    $dll = Join-Game $gameDir "$ModsRelative/$ModName/$ModName.dll"
    if (-not (Test-Path -LiteralPath $dll)) { return '' }
    $version = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion
    if (-not $version) { return '?' }
    return ($version -replace '\.0$', '')
}

# Mensaje de un error de permisos, con lo que hay que hacer
function Get-ErrorText($errorRecord) {
    $exception = $errorRecord.Exception
    while ($exception.InnerException) { $exception = $exception.InnerException }
    if ($exception -is [System.UnauthorizedAccessException]) {
        return (L "Windows no deja escribir en la carpeta del juego. Cierra el instalador, haz clic derecho en el .bat y elige 'Ejecutar como administrador'. ($($exception.Message))" `
                "Windows won't let the installer write to the game folder. Close the installer, right-click the .bat and choose 'Run as administrator'. ($($exception.Message))")
    }
    if ($exception -is [System.IO.IOException] -and $exception.Message -match 'proceso|process') {
        return (L "Un archivo del juego está en uso. Cierra Hollow Knight (y Steam si sigue bloqueado) y vuelve a probar. ($($exception.Message))" `
                "A game file is in use. Close Hollow Knight (and Steam if it is still locked) and try again. ($($exception.Message))")
    }
    return $errorRecord.Exception.Message
}
