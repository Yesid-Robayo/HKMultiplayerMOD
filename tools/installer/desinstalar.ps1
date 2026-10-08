# Desinstalador de Hollow Knight Coop: devuelve los archivos originales del respaldo y quita los añadidos.
# Se ejecuta desde desinstalar.bat. Las partidas guardadas no se tocan.
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
$backupName = 'hollow-knight-coop-backup'

function Find-GameDir {
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($root in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        $candidates.Add((Join-Path $root 'steamapps\common\Hollow Knight'))
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $candidates.Add((Join-Path ($Matches[1] -replace '\\\\', '\') 'steamapps\common\Hollow Knight'))
                }
            }
        }
    }
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir 'hollow_knight.exe')) { return $dir }
    }
    return $null
}

Write-Host '=== Hollow Knight Coop: desinstalación ===' -ForegroundColor Cyan

if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'hollow_knight.exe'))) {
    $GameDir = (Read-Host 'Pega la ruta de la carpeta de Hollow Knight').Trim('"')
}
if (-not (Test-Path (Join-Path $GameDir 'hollow_knight.exe'))) { throw "En '$GameDir' no está hollow_knight.exe" }

if (Get-Process hollow_knight -ErrorAction SilentlyContinue) {
    throw 'Hollow Knight está abierto. Ciérralo y vuelve a ejecutar el desinstalador.'
}

$backupDir = Join-Path $GameDir $backupName
$manifest = Join-Path $backupDir 'installed.txt'
if (-not (Test-Path $manifest)) {
    throw "No hay respaldo de la instalación ($manifest). Usa Steam > Propiedades > Archivos instalados > Verificar integridad."
}

$needsSteamVerify = $false
foreach ($line in Get-Content $manifest) {
    $parts = $line.Split('|', 2)
    $target = Join-Path $GameDir $parts[1]
    if ($parts[0] -eq 'no-backup') {
        $needsSteamVerify = $true
    } elseif ($parts[0] -eq 'backed-up') {
        Copy-Item (Join-Path $backupDir $parts[1]) $target -Force
    } elseif (Test-Path $target) {
        Remove-Item $target -Force
    }
}

$modDir = Join-Path $GameDir 'hollow_knight_Data\Managed\Mods\HollowKnightCoop'
$keptSkins = $false
if (Test-Path $modDir) {
    # Las skins son del jugador: se dejan en su carpeta (el resto del mod se borra)
    foreach ($item in Get-ChildItem $modDir -Force) {
        if ($item.Name -eq 'Skins' -and (Get-ChildItem $item.FullName -Force | Measure-Object).Count -gt 0) {
            $keptSkins = $true
        } else {
            Remove-Item $item.FullName -Recurse -Force
        }
    }
    if (-not $keptSkins) { Remove-Item $modDir -Recurse -Force }
}
Remove-Item $backupDir -Recurse -Force

Write-Host ''
if ($needsSteamVerify) {
    Write-Host 'Mod quitado. Para dejar el juego original: Steam > Hollow Knight > Propiedades > Archivos instalados > Verificar integridad.' -ForegroundColor Yellow
} else {
    Write-Host 'Desinstalado: el juego vuelve a estar como antes del mod.' -ForegroundColor Green
}
Write-Host 'Los mods que se movieron a Mods\Disabled siguen ahí.'
if ($keptSkins) { Write-Host "Tus skins siguen en: $(Join-Path $modDir 'Skins')" }
