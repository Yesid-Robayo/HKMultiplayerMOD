# Instalador de Hollow Knight Coop.
# Busca la carpeta del juego, comprueba los archivos del paquete, respalda los originales que se van a reemplazar
# y copia el mod. Se ejecuta desde instalar.bat.
param([string]$GameDir)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'archivos'
$backupName = 'hollow-knight-coop-backup'

function Find-GameDir {
    $candidates = New-Object System.Collections.Generic.List[string]
    $steamRoots = @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")
    foreach ($root in $steamRoots) {
        $candidates.Add((Join-Path $root 'steamapps\common\Hollow Knight'))
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') {
                    $lib = $Matches[1] -replace '\\\\', '\'
                    $candidates.Add((Join-Path $lib 'steamapps\common\Hollow Knight'))
                }
            }
        }
    }

    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir 'hollow_knight.exe')) { return $dir }
    }
    return $null
}

Write-Host '=== Hollow Knight Coop: instalación ===' -ForegroundColor Cyan

if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'hollow_knight.exe'))) {
    Write-Host 'No encontré la carpeta del juego automáticamente.'
    $GameDir = Read-Host 'Pega la ruta de la carpeta de Hollow Knight (la que tiene hollow_knight.exe)'
    $GameDir = $GameDir.Trim('"')
}
if (-not (Test-Path (Join-Path $GameDir 'hollow_knight.exe'))) {
    throw "En '$GameDir' no está hollow_knight.exe"
}
Write-Host "Juego: $GameDir"

if (Get-Process hollow_knight -ErrorAction SilentlyContinue) {
    throw 'Hollow Knight está abierto. Ciérralo y vuelve a ejecutar el instalador.'
}

# 1. Comprobar que el paquete está completo y sin cambios
Write-Host 'Comprobando los archivos del paquete...'
$sums = Join-Path $here 'SHA256SUMS.txt'
$entries = @()
foreach ($line in Get-Content $sums) {
    if ($line -match '^([0-9a-f]{64})\s+\*?\./archivos/(.+)$') {
        $entries += [pscustomobject]@{ Hash = $Matches[1]; Path = $Matches[2] }
    }
}
if ($entries.Count -eq 0) { throw 'SHA256SUMS.txt no tiene archivos del mod' }
foreach ($entry in $entries) {
    $file = Join-Path $source $entry.Path
    if (-not (Test-Path $file)) { throw "Falta en el paquete: $($entry.Path)" }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $entry.Hash) { throw "Archivo dañado en el paquete: $($entry.Path). Vuelve a descargarlo." }
}
Write-Host "  $($entries.Count) archivos correctos"

# 2. Respaldo de los originales (solo la primera vez)
$backupDir = Join-Path $GameDir $backupName
$manifest = Join-Path $backupDir 'installed.txt'
$firstInstall = -not (Test-Path $manifest)
# Si ya había una Modding API instalada, los archivos del juego ya no son los originales: no se respaldan
$apiInstalled = Test-Path (Join-Path $GameDir 'hollow_knight_Data\Managed\MMHOOK_Assembly-CSharp.dll')
if ($firstInstall) {
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($entry in $entries) {
        $target = Join-Path $GameDir $entry.Path
        if ((Test-Path $target) -and $apiInstalled) {
            $lines.Add("no-backup|$($entry.Path)")
        } elseif (Test-Path $target) {
            $copy = Join-Path $backupDir $entry.Path
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $copy) | Out-Null
            Copy-Item $target $copy -Force
            $lines.Add("backed-up|$($entry.Path)")
        } else {
            $lines.Add("new|$($entry.Path)")
        }
    }
    if (-not (Test-Path (Join-Path $GameDir 'steam_appid.txt'))) { $lines.Add('new|steam_appid.txt') }
    Set-Content -Path $manifest -Value $lines -Encoding UTF8
    if ($apiInstalled) {
        Write-Host 'Ya había otra Modding API instalada: para desinstalar del todo habrá que verificar la integridad en Steam.' -ForegroundColor Yellow
    } else {
        Write-Host "Respaldo de los archivos originales en: $backupDir"
    }
} else {
    Write-Host 'Ya había una instalación: se conserva el respaldo original.'
}

# 3. Otros mods: no funcionan con esta versión de la Modding API
$mods = Join-Path $GameDir 'hollow_knight_Data\Managed\Mods'
if (Test-Path $mods) {
    $disabled = Join-Path $mods 'Disabled'
    foreach ($dir in Get-ChildItem $mods -Directory) {
        if ($dir.Name -ne 'HollowKnightCoop' -and $dir.Name -ne 'Disabled') {
            New-Item -ItemType Directory -Force -Path $disabled | Out-Null
            Move-Item $dir.FullName (Join-Path $disabled $dir.Name) -Force
            Write-Host "Mod movido a Mods\Disabled: $($dir.Name)"
        }
    }
}

# 4. Copiar
Write-Host 'Copiando el mod...'
foreach ($entry in $entries) {
    $target = Join-Path $GameDir $entry.Path
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item (Join-Path $source $entry.Path) $target -Force
}
$appId = Join-Path $GameDir 'steam_appid.txt'
if (-not (Test-Path $appId)) { Set-Content -Path $appId -Value '367520' -Encoding ASCII }

# 5. Verificar lo instalado
foreach ($entry in $entries) {
    $hash = (Get-FileHash (Join-Path $GameDir $entry.Path) -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $entry.Hash) { throw "No se copió bien: $($entry.Path)" }
}

Write-Host ''
Write-Host 'Instalación completa.' -ForegroundColor Green
Write-Host 'Abre Hollow Knight: en el menú principal aparece "Multijugador".'
Write-Host 'Los ajustes están en el menú de pausa (panel Coop) o en Opciones > Mods > Hollow Knight Coop.'
