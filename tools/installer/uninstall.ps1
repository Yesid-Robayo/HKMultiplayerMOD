# Desinstalación de HKMultiplayer (la carga setup.ps1, que la llama desde la ventana o desde la consola).
# Deshace, en orden inverso, lo que apuntó la instalación en HKMultiplayer-backup\manifest.txt: devuelve los archivos
# originales, borra los añadidos, devuelve los mods apartados y el mod anterior, y borra la copia. Los mundos coop y
# las partidas (en AppData\LocalLow) no se tocan; las skins que haya en Mods\HKMultiplayer\Skins se quedan.

# Borra las subcarpetas vacías y luego la carpeta si quedó vacía. Devuelve $true si la borró.
function Remove-EmptyTree([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $true }
    foreach ($child in Get-ChildItem -LiteralPath $path -Directory -Force) { [void](Remove-EmptyTree $child.FullName) }
    if ((Get-ChildItem -LiteralPath $path -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $path -Force
        return $true
    }
    return $false
}

# Carpeta del mod: se borra todo salvo las skins que haya puesto el jugador. Devuelve $true si quedan skins.
function Remove-ModFolder([string]$modDir) {
    if (-not (Test-Path -LiteralPath $modDir)) { return $false }
    foreach ($item in Get-ChildItem -LiteralPath $modDir -Force) {
        if ($item.PSIsContainer -and $item.Name -eq 'Skins') { continue }
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }
    return -not (Remove-EmptyTree $modDir)
}

# Desinstala de la carpeta del juego. Devuelve $true si el juego quedó como estaba; lanza una excepción si algo falla.
function Invoke-Uninstall([string]$game) {
    Write-Note ((L 'Carpeta del juego: ' 'Game folder: ') + $game)
    Assert-GameClosed $game

    $backupDir = Join-Path $game $BackupName
    $manifest = Join-Path $backupDir $ManifestName
    $modDir = Join-Game $game "$ModsRelative/$ModName"

    if (-not (Test-Path -LiteralPath $manifest)) {
        # Instalado a mano (sin el instalador): solo se puede quitar el mod
        if (-not (Test-Path -LiteralPath $modDir)) {
            Write-Warn (L "No encontré HKMultiplayer instalado en esta carpeta (ni la copia $BackupName)." "HKMultiplayer isn't installed in this folder (and there is no $BackupName copy).")
            return $false
        }
        $keptSkins = Remove-ModFolder $modDir
        Write-Warn (L 'Mod quitado. No había copia del instalador, así que la Modding API sigue instalada.' "Mod removed. There was no installer backup, so the Modding API is still installed.")
        Write-Warn (L 'Para dejar el juego original: Steam > clic derecho en Hollow Knight > Propiedades > Archivos instalados > Verificar integridad.' `
                'To get the original game back: Steam > right-click Hollow Knight > Properties > Installed Files > Verify integrity.')
        if ($keptSkins) { Write-Note ((L 'Tus skins siguen en: ' 'Your skins are still in: ') + (Join-Path $modDir 'Skins')) }
        return $false
    }

    Write-Step (L 'Deshaciendo los cambios de la instalación...' 'Undoing the install changes...')
    $lines = @(Get-Content -LiteralPath $manifest | Where-Object { $_ -and -not $_.StartsWith('#') })
    [array]::Reverse($lines)
    $problems = New-Object System.Collections.Generic.List[string]
    $keptSkins = $false
    $restoredOldMod = $false
    $restoredMods = 0
    foreach ($line in $lines) {
        $parts = $line.Split('|')
        $kind = $parts[0]
        $target = Join-Game $game $parts[1]
        switch ($kind) {
            'added' {
                if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
            }
            'replaced' {
                $copy = Join-Path (Join-Path $backupDir 'files') ($parts[1] -replace '/', '\')
                if (Test-Path -LiteralPath $copy) {
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                    Copy-Item -LiteralPath $copy -Destination $target -Force
                } else {
                    $problems.Add((L "falta la copia de $($parts[1])" "the backup of $($parts[1]) is missing"))
                }
            }
            'added-dir' {
                if ($target -ieq $modDir) {
                    $keptSkins = Remove-ModFolder $modDir
                } elseif (-not (Remove-EmptyTree $target) -and -not $keptSkins) {
                    Write-Warn (L "Se deja $($parts[1]): tiene archivos que no puso el instalador." "$($parts[1]) is kept: it has files the installer didn't put there.")
                }
            }
            'moved' {
                $from = Join-Game $game $parts[2]
                if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $target)) {
                    Move-Item -LiteralPath $from -Destination $target
                    $restoredMods++
                } elseif (-not (Test-Path -LiteralPath $target)) {
                    $problems.Add((L "no está $($parts[2]) para devolverlo a su sitio" "$($parts[2]) is not there to move it back"))
                }
            }
            'old-mod' {
                $stored = Join-Path $backupDir ($parts[2] -replace '/', '\')
                if ((Test-Path -LiteralPath $stored) -and -not (Test-Path -LiteralPath $target)) {
                    Move-Item -LiteralPath $stored -Destination $target
                    $restoredOldMod = $true
                } elseif (-not (Test-Path -LiteralPath $target)) {
                    $problems.Add((L "falta la copia del mod anterior ($($parts[2]))" "the backup of the old mod is missing ($($parts[2]))"))
                }
            }
            default { $problems.Add((L "línea desconocida en el manifiesto: $line" "unknown manifest line: $line")) }
        }
    }

    $clean = $problems.Count -eq 0
    if ($clean) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force
    } else {
        Write-Warn (L 'Algo no se pudo devolver:' 'Something could not be restored:')
        foreach ($problem in $problems) { Write-Warn "- $problem" }
        Write-Warn (L "Se conserva la copia en $backupDir. Para dejar el juego original del todo: Steam > Hollow Knight > Propiedades > Archivos instalados > Verificar integridad." `
                "The backup in $backupDir is kept. To get the original game back completely: Steam > Hollow Knight > Properties > Installed Files > Verify integrity.")
    }
    if ($restoredOldMod) { Write-Note (L "El mod anterior ($OldModName) vuelve a estar instalado, como antes." "The old mod ($OldModName) is installed again, as before.") }
    if ($restoredMods -gt 0) { Write-Note (L "Mods apartados a Mods\Disabled que vuelven a su sitio: $restoredMods." "Mods moved back from Mods\Disabled: $restoredMods.") }
    if ($keptSkins) { Write-Note ((L 'Tus skins siguen en: ' 'Your skins are still in: ') + (Join-Path $modDir 'Skins')) }
    Write-Note (L 'Tus mundos coop y tus partidas no se han tocado.' 'Your coop worlds and saves were not touched.')
    return $clean
}
