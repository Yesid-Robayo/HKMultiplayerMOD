#!/usr/bin/env bash
# Arma dist/hollow-knight-coop-<fecha>.zip para instalar en otro PC: Modding API (Unity 6) + mod Hollow Knight Coop.
# Se descomprime en cualquier carpeta y se ejecuta instalar.bat. Requiere haber compilado antes (tools/test-cycle.sh --no-launch).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAGE="$ROOT/dist/stage"
FILES="$STAGE/archivos"
MANAGED="$FILES/hollow_knight_Data/Managed"
NAME="hollow-knight-coop-$(date +%Y%m%d-%H%M)"

rm -rf "$STAGE"
mkdir -p "$MANAGED/Mods/HollowKnightCoop"

# Modding API: todo OutputFinal salvo el README de la API
find "$ROOT/vendor/modding-api/OutputFinal" -maxdepth 1 -type f ! -name "README.md" -exec cp {} "$MANAGED/" \;
cp "$ROOT/out/HollowKnightCoop/HollowKnightCoop.dll" "$ROOT/out/HollowKnightCoop/BouncyCastle.Cryptography.dll" "$MANAGED/Mods/HollowKnightCoop/"

# Instalador (instalar.bat / desinstalar.bat) y LEEME en la raíz del paquete
cp "$ROOT/tools/installer/"*.bat "$ROOT/tools/installer/"*.ps1 "$STAGE/"
# El LEEME para jugadores se añadirá con el instalador (hito M6)

# Inventario con hashes: lo usa el instalador para comprobar el paquete y lo instalado
(cd "$STAGE" && find ./archivos -type f -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS.txt)

mkdir -p "$ROOT/dist"
powershell.exe -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$STAGE")\*' -DestinationPath '$(cygpath -w "$ROOT/dist/$NAME.zip")' -Force"
rm -rf "$STAGE"
echo "dist/$NAME.zip"
