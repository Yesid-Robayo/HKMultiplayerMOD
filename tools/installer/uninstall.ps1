# Desinstalador de HKMultiplayer (se ejecuta desde desinstalar.bat).
# Deshace, en orden inverso, lo que apuntó el instalador en HKMultiplayer-backup\manifest.txt: devuelve los archivos
# originales, borra los añadidos, devuelve los mods apartados y el mod anterior, y borra la copia. Los mundos coop y
# las partidas (en AppData\LocalLow) no se tocan; las skins que haya en Mods\HKMultiplayer\Skins se quedan.
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

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

function Main {
    Write-Host '=== HKMultiplayer: desinstalación ===' -ForegroundColor Cyan
    Write-Host ''

    $game = Resolve-GameDir $GameDir
    Write-Host "Carpeta del juego: $game"
    Assert-GameClosed $game

    $backupDir = Join-Path $game $BackupName
    $manifest = Join-Path $backupDir $ManifestName
    $modDir = Join-Game $game "$ModsRelative/$ModName"

    if (-not (Test-Path -LiteralPath $manifest)) {
        # Instalado a mano (sin el instalador): solo se puede quitar el mod
        if (-not (Test-Path -LiteralPath $modDir)) {
            Write-Host "No encontré HKMultiplayer instalado en esta carpeta (ni la copia $BackupName)." -ForegroundColor Yellow
            return
        }
        $keptSkins = Remove-ModFolder $modDir
        Write-Host ''
        Write-Host 'Mod quitado. No había copia del instalador, así que la Modding API sigue instalada.' -ForegroundColor Yellow
        Write-Host 'Para dejar el juego original: Steam > clic derecho en Hollow Knight > Propiedades > Archivos instalados > Verificar integridad.' -ForegroundColor Yellow
        if ($keptSkins) { Write-Host "Tus skins siguen en: $(Join-Path $modDir 'Skins')" }
        return
    }

    Write-Step 'Deshaciendo los cambios del instalador...'
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
                    $problems.Add("falta la copia de $($parts[1])")
                }
            }
            'added-dir' {
                if ($target -ieq $modDir) {
                    $keptSkins = Remove-ModFolder $modDir
                } elseif (-not (Remove-EmptyTree $target) -and -not $keptSkins) {
                    Write-Warn "Se deja $($parts[1]): tiene archivos que no puso el instalador."
                }
            }
            'moved' {
                $from = Join-Game $game $parts[2]
                if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $target)) {
                    Move-Item -LiteralPath $from -Destination $target
                    $restoredMods++
                } elseif (-not (Test-Path -LiteralPath $target)) {
                    $problems.Add("no está $($parts[2]) para devolverlo a su sitio")
                }
            }
            'old-mod' {
                $stored = Join-Path $backupDir ($parts[2] -replace '/', '\')
                if ((Test-Path -LiteralPath $stored) -and -not (Test-Path -LiteralPath $target)) {
                    Move-Item -LiteralPath $stored -Destination $target
                    $restoredOldMod = $true
                } elseif (-not (Test-Path -LiteralPath $target)) {
                    $problems.Add("falta la copia del mod anterior ($($parts[2]))")
                }
            }
            default { $problems.Add("línea desconocida en el manifiesto: $line") }
        }
    }

    Write-Host ''
    if ($problems.Count -gt 0) {
        Write-Host 'Desinstalado, pero algo no se pudo devolver:' -ForegroundColor Yellow
        foreach ($problem in $problems) { Write-Warn $problem }
        Write-Host "Se conserva la copia en $backupDir. Para dejar el juego original del todo: Steam > Hollow Knight > Propiedades > Archivos instalados > Verificar integridad." -ForegroundColor Yellow
    } else {
        Remove-Item -LiteralPath $backupDir -Recurse -Force
        Write-Host 'Desinstalado: el juego vuelve a estar como antes de instalar HKMultiplayer.' -ForegroundColor Green
    }
    if ($restoredOldMod) { Write-Host "El mod anterior ($OldModName) vuelve a estar instalado, como antes." }
    if ($restoredMods -gt 0) { Write-Host "Mods apartados a Mods\Disabled que vuelven a su sitio: $restoredMods." }
    if ($keptSkins) { Write-Host "Tus skins siguen en: $(Join-Path $modDir 'Skins')" }
    Write-Host 'Tus mundos coop y tus partidas no se han tocado.'
}

try {
    Main
} catch {
    Write-Host ''
    Write-Host "No se pudo desinstalar: $(Get-ErrorText $_)" -ForegroundColor Red
    exit 1
}
