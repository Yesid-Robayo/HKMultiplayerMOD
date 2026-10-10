# Instalación de HKMultiplayer (la carga setup.ps1, que la llama desde la ventana o desde la consola).
# Comprueba el paquete, guarda una copia de lo que va a cambiar y lo apunta en HKMultiplayer-backup\manifest.txt (así la
# desinstalación deja el juego como estaba), quita el mod anterior HollowKnightCoop, aparta los demás mods a
# Mods\Disabled y copia la Modding API y el mod.
#
# Líneas del manifiesto (una por cambio, en el orden en que se hicieron):
#   added-dir|<carpeta>         carpeta que no existía
#   added|<archivo>             archivo que no existía
#   replaced|<archivo>          archivo que existía: el original está en HKMultiplayer-backup\files\<archivo>
#   old-mod|<ruta>|<copia>      mod anterior quitado: está en HKMultiplayer-backup\<copia>
#   moved|<desde>|<hasta>       otro mod movido a Mods\Disabled

# Archivos del paquete con su hash (SHA256SUMS.txt, generado por tools/package.sh)
function Read-PackageEntries([string]$packageRoot) {
    $sums = Join-Path $packageRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $sums)) {
        throw (L 'Falta SHA256SUMS.txt: el paquete está incompleto. Vuelve a descomprimirlo.' 'SHA256SUMS.txt is missing: the package is incomplete. Extract it again.')
    }
    $entries = @()
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ($line -match '^([0-9a-f]{64})\s+\*?\./files/(.+)$') {
            $entries += [pscustomobject]@{ Hash = $Matches[1]; Path = $Matches[2] }
        }
    }
    if ($entries.Count -eq 0) { throw (L 'SHA256SUMS.txt no tiene archivos: el paquete está incompleto.' 'SHA256SUMS.txt lists no files: the package is incomplete.') }
    return $entries
}

function Get-Sha256([string]$file) { return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLower() }

# La Modding API del paquete es para Hollow Knight 1.5.12620 (Unity 6, con los ensamblados TeamCherry.*). En otra
# versión del juego dejaría el juego sin arrancar.
function Assert-CompatibleGame([string]$game) {
    $managed = Join-Game $game 'hollow_knight_Data/Managed'
    if (-not (Test-Path -LiteralPath $managed)) {
        throw (L "En '$game' no está hollow_knight_Data\Managed: no parece la carpeta del juego." "'$game' has no hollow_knight_Data\Managed: it doesn't look like the game folder.")
    }
    $unity = ''
    $player = Join-Path $game 'UnityPlayer.dll'
    if (Test-Path -LiteralPath $player) { $unity = (Get-Item -LiteralPath $player).VersionInfo.FileVersion }
    if (-not $unity -or -not $unity.StartsWith('6000.') -or -not (Test-Path -LiteralPath (Join-Path $managed 'TeamCherry.TK2D.dll'))) {
        throw (L ("Este mod es para Hollow Knight 1.5.12620 (la versión actual de Steam). Tu juego es de otra versión " +
                "(motor Unity '$unity'). Actualízalo en Steam (Propiedades > Betas: ninguna) y vuelve a instalar.") `
                ("This mod is for Hollow Knight 1.5.12620 (the current Steam version). Your game is a different version " +
                "(Unity engine '$unity'). Update it in Steam (Properties > Betas: none) and install again."))
    }
}

# Instala en la carpeta del juego (ya comprobada). Lanza una excepción si algo falla; $script:Changing dice si ya se
# había empezado a cambiar el juego.
function Invoke-Install([string]$game) {
    $packageRoot = Split-Path -Parent $PSScriptRoot
    $source = Join-Path $packageRoot 'files'
    $script:Changing = $false

    Write-Note ((L 'Carpeta del juego: ' 'Game folder: ') + $game)
    Assert-CompatibleGame $game
    Assert-GameClosed $game

    # 1. El paquete está completo y sin cambios
    Write-Step (L 'Comprobando los archivos del paquete...' 'Checking the package files...')
    $entries = Read-PackageEntries $packageRoot
    foreach ($entry in $entries) {
        $file = Join-Path $source $entry.Path
        if (-not (Test-Path -LiteralPath $file)) {
            throw (L "Falta en el paquete: $($entry.Path). Vuelve a descomprimirlo entero." "Missing from the package: $($entry.Path). Extract the whole package again.")
        }
        if ((Get-Sha256 $file) -ne $entry.Hash) {
            throw (L "Archivo dañado en el paquete: $($entry.Path). Vuelve a descargarlo." "Damaged file in the package: $($entry.Path). Download it again.")
        }
    }
    Write-Note (L "$($entries.Count) archivos correctos" "$($entries.Count) files OK")

    # 2. Copia de lo que se va a cambiar. Si ya se instaló antes (actualización), se conserva la copia de entonces
    #    (la del juego como estaba) y solo se añade lo nuevo de este paquete.
    $script:Changing = $true
    $backupDir = Join-Path $game $BackupName
    $manifest = Join-Path $backupDir $ManifestName
    $update = Test-Path -LiteralPath $manifest
    $known = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    if ($update) {
        foreach ($line in Get-Content -LiteralPath $manifest) {
            if ($line -and -not $line.StartsWith('#')) { [void]$known.Add($line.Split('|')[1]) }
        }
    } else {
        New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
        Set-Content -LiteralPath $manifest -Encoding UTF8 -Value @(
            '# HKMultiplayer: cambios del instalador en la carpeta del juego. La desinstalación los deshace.',
            '# HKMultiplayer: installer changes in the game folder. Uninstalling undoes them.',
            '# No borres esta carpeta mientras el mod esté instalado. / Do not delete this folder while the mod is installed.')
    }
    $record = { param([string]$line) Add-Content -LiteralPath $manifest -Value $line -Encoding UTF8 }

    Write-Step (L 'Guardando una copia de lo que se va a cambiar...' 'Backing up what will change...')
    $replaced = 0
    $targets = @($entries | ForEach-Object { $_.Path }) + @('steam_appid.txt')
    foreach ($relative in $targets) {
        # Carpetas que hay que crear, de fuera hacia dentro
        $parents = @()
        $dir = Split-Path -Parent $relative
        while ($dir) { $parents = @($dir) + $parents; $dir = Split-Path -Parent $dir }
        foreach ($parent in $parents) {
            $parentRelative = $parent -replace '\\', '/'
            $parentPath = Join-Game $game $parentRelative
            if (-not (Test-Path -LiteralPath $parentPath)) {
                & $record "added-dir|$parentRelative"
                [void]$known.Add($parentRelative)
                New-Item -ItemType Directory -Force -Path $parentPath | Out-Null
            }
        }

        if ($known.Contains($relative)) { continue }
        $target = Join-Game $game $relative
        if (Test-Path -LiteralPath $target) {
            if ($relative -eq 'steam_appid.txt') { continue }   # no se toca: se deja el del juego
            $copy = Join-Path (Join-Path $backupDir 'files') ($relative -replace '/', '\')
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $copy) | Out-Null
            Copy-Item -LiteralPath $target -Destination $copy -Force
            & $record "replaced|$relative"
            $replaced++
        } else {
            & $record "added|$relative"
        }
        [void]$known.Add($relative)
    }
    if ($update) {
        Write-Note (L 'Ya estaba instalado: se conserva la copia del juego de la primera instalación.' 'Already installed: the game backup from the first install is kept.')
    } else {
        Write-Note (L "$replaced archivos del juego guardados en $backupDir" "$replaced game files backed up to $backupDir")
    }

    # 3. Mod anterior (HollowKnightCoop): también abre la red, no pueden estar los dos. Se guarda en la copia y la
    #    desinstalación lo devuelve.
    $mods = Join-Game $game $ModsRelative
    foreach ($name in @($OldModName, "$OldModName.dll")) {
        $path = Join-Path $mods $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $stored = "old-mod/$name"
        $suffix = 2
        while (Test-Path -LiteralPath (Join-Path $backupDir ($stored -replace '/', '\'))) { $stored = "old-mod/$name.$suffix"; $suffix++ }
        New-Item -ItemType Directory -Force -Path (Join-Path $backupDir 'old-mod') | Out-Null
        Move-Item -LiteralPath $path -Destination (Join-Path $backupDir ($stored -replace '/', '\'))
        & $record "old-mod|$ModsRelative/$name|$stored"
        Write-Warn (L "Quitado el mod anterior $OldModName (los dos usan la red a la vez). Su copia queda en $BackupName." `
                "Removed the old mod $OldModName (both use the network at the same time). Its copy stays in $BackupName.")
    }

    # 4. Otros mods: los hechos para otra versión de la Modding API no cargan con esta y pueden romper el juego. Se
    #    apartan a Mods\Disabled (la Modding API no carga esa carpeta) y la desinstalación los devuelve.
    if (Test-Path -LiteralPath $mods) {
        $others = @(Get-ChildItem -LiteralPath $mods -Force | Where-Object {
                ($_.PSIsContainer -and $_.Name -ne $ModName -and $_.Name -ne 'Disabled') -or
                (-not $_.PSIsContainer -and $_.Extension -ieq '.dll') })
        if ($others.Count -gt 0) {
            $disabled = Join-Path $mods 'Disabled'
            if (-not (Test-Path -LiteralPath $disabled)) {
                & $record "added-dir|$ModsRelative/Disabled"
                New-Item -ItemType Directory -Force -Path $disabled | Out-Null
            }
            foreach ($item in $others) {
                $destination = Join-Path $disabled $item.Name
                if (Test-Path -LiteralPath $destination) {
                    Write-Warn (L "No se movió $($item.Name): ya hay uno con ese nombre en Mods\Disabled. Si el juego falla al cargarlo, quítalo a mano." `
                            "$($item.Name) was not moved: Mods\Disabled already has one with that name. If the game fails to load it, remove it by hand.")
                    continue
                }
                Move-Item -LiteralPath $item.FullName -Destination $destination
                & $record "moved|$ModsRelative/$($item.Name)|$ModsRelative/Disabled/$($item.Name)"
                Write-Warn ((L 'Otro mod apartado a Mods\Disabled: ' 'Another mod moved to Mods\Disabled: ') + $item.Name)
            }
        }
    }

    # 5. Copiar la Modding API y el mod
    Write-Step (L 'Copiando la Modding API y el mod...' 'Copying the Modding API and the mod...')
    foreach ($entry in $entries) {
        Copy-Item -LiteralPath (Join-Path $source $entry.Path) -Destination (Join-Game $game $entry.Path) -Force
    }
    # Sin este archivo, abrir hollow_knight.exe directamente (sin Steam) no arranca el juego
    $appId = Join-Path $game 'steam_appid.txt'
    if (-not (Test-Path -LiteralPath $appId)) { Set-Content -LiteralPath $appId -Value '367520' -Encoding ASCII }

    # 6. Comprobar lo copiado
    foreach ($entry in $entries) {
        if ((Get-Sha256 (Join-Game $game $entry.Path)) -ne $entry.Hash) {
            throw (L "No se copió bien: $($entry.Path)" "Not copied correctly: $($entry.Path)")
        }
    }
    Write-Note (L 'Copiado y comprobado' 'Copied and verified')
    $script:Changing = $false
}
