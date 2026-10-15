# Quita las rutas de este PC de las DLL y EXE de una carpeta (lo usa tools/package.sh antes de hacer el zip).
# El compilador guarda la ruta completa del .pdb, con el usuario de Windows, en el directorio de depuración de cada
# ensamblado (bloque RSDS: firma, GUID de 16 bytes, edad de 4 bytes y la ruta terminada en cero). Se deja solo el nombre
# del archivo y se rellena con ceros: el juego no lo usa y el ensamblado no cambia de tamaño.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools/strip-pdb-paths.ps1 <carpeta>
param([Parameter(Mandatory = $true)][string]$Folder)

$ErrorActionPreference = 'Stop'
$files = Get-ChildItem -LiteralPath $Folder -Recurse -File | Where-Object { $_.Extension -in '.dll', '.exe' }
foreach ($file in $files) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $changed = $false
    for ($i = 0; $i -le $bytes.Length - 28; $i++) {
        # "RSDS"
        if ($bytes[$i] -ne 0x52 -or $bytes[$i + 1] -ne 0x53 -or $bytes[$i + 2] -ne 0x44 -or $bytes[$i + 3] -ne 0x53) { continue }
        $start = $i + 24
        $end = $start
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
        $path = [Text.Encoding]::UTF8.GetString($bytes, $start, $end - $start)
        # Solo una ruta dentro de la carpeta de un usuario (C:\Users\<nombre>\...), que es la que lleva un nombre: las de las
        # máquinas que compilaron las librerías de terceros (firmadas) no se tocan
        if ($path -notmatch '\.pdb$' -or $path -notmatch '^[A-Za-z]:[\\/]Users[\\/]') { continue }
        $name = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFileName($path))
        for ($j = $start; $j -lt $end; $j++) { $bytes[$j] = 0 }
        [Array]::Copy($name, 0, $bytes, $start, $name.Length)
        $changed = $true
    }

    if ($changed) {
        [IO.File]::WriteAllBytes($file.FullName, $bytes)
        "  sin la ruta del .pdb: $($file.Name)"
    }
}
