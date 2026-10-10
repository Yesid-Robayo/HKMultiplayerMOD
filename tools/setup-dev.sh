#!/usr/bin/env bash
# Prepara un PC para compilar el mod (lo que no se sube al repositorio porque es del juego o se genera):
#   1. modding-api/Vanilla: la carpeta Managed del juego SIN modificar (si el juego ya tiene la Modding API, se usan los
#      originales que guardó el instalador en HKMultiplayer-backup; si no hay, hay que "Verificar integridad" en Steam).
#   2. modding-api/override/mscorlib.dll: el del juego.
#   3. Compila la Modding API modificada (modding-api/, para Hollow Knight 1.5.12620) → modding-api/OutputFinal.
#   4. vendor/modding-api/OutputFinal: esa compilación (la usa tools/package.sh para el paquete de jugadores).
#   5. lib/: Managed del juego + la Modding API compilada (contra esto compila el mod).
# Uso: bash tools/setup-dev.sh ["C:/ruta/a/Hollow Knight"]   (por defecto, la carpeta de Steam)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${1:-/c/Program Files (x86)/Steam/steamapps/common/Hollow Knight}"
GAME="$(cd "$GAME" 2>/dev/null && pwd)" || { echo "setup-dev.sh: no existe la carpeta del juego: ${1:-(Steam)}" >&2; exit 1; }
MANAGED="$GAME/hollow_knight_Data/Managed"
BACKUP="$GAME/HKMultiplayer-backup/files/hollow_knight_Data/Managed"
API="$ROOT/modding-api"

fail() { echo "setup-dev.sh: $*" >&2; exit 1; }
[ -f "$MANAGED/Assembly-CSharp.dll" ] || fail "no está $MANAGED/Assembly-CSharp.dll: no parece la carpeta del juego"
command -v dotnet >/dev/null || fail "falta el SDK de .NET (dotnet)"

echo "1. Managed del juego sin modificar → modding-api/Vanilla"
patched() { grep -aq "ModHooks" "$1/Assembly-CSharp.dll"; }
if ! patched "$MANAGED"; then
  rm -rf "$API/Vanilla"
  mkdir -p "$API/Vanilla"
  find "$MANAGED" -maxdepth 1 -type f -exec cp {} "$API/Vanilla/" \;
elif [ -d "$BACKUP" ]; then
  echo "   (el juego tiene el mod instalado: se usan los originales que guardó el instalador en HKMultiplayer-backup)"
  rm -rf "$API/Vanilla"
  mkdir -p "$API/Vanilla"
  find "$MANAGED" -maxdepth 1 -type f -exec cp {} "$API/Vanilla/" \;
  cp -r "$BACKUP/." "$API/Vanilla/"
  # Los que añade la Modding API no son del juego
  rm -f "$API/Vanilla/"{MMHOOK_,MonoMod.,Mono.Cecil,unityscenerepacker,System.ValueTuple}* "$API/Vanilla/Assembly-CSharp.xml"
elif [ -f "$API/Vanilla/Assembly-CSharp.dll" ] && ! patched "$API/Vanilla"; then
  echo "   (el juego ya tiene una Modding API sin copia de los originales: se reutiliza el modding-api/Vanilla que ya había)"
else
  fail "el Assembly-CSharp.dll del juego ya tiene una Modding API y no hay copia del original. En Steam: clic derecho en Hollow Knight > Propiedades > Archivos instalados > Verificar integridad, y vuelve a ejecutar este script"
fi
patched "$API/Vanilla" && fail "modding-api/Vanilla no es el juego original (tiene la Modding API)"

echo "2. mscorlib del juego → modding-api/override"
mkdir -p "$API/override"
cp "$API/Vanilla/mscorlib.dll" "$API/override/mscorlib.dll"

echo "3. Compilando la Modding API..."
build() {
  local log
  log="$(dotnet build "$1" -c Release -nologo -v:q 2>&1)" || { echo "$log" | grep -E " error " | sed -E 's/ \[.*//' | sort -u >&2; fail "no compila $1"; }
}
# Los parcheadores primero: Assembly-CSharp los usa al compilar
build "$API/PrePatcher/PrePatcher.csproj"
build "$API/PostPatcher/PostPatcher.csproj"
build "$API/HollowKnight.Modding.API.sln"
[ -f "$API/OutputFinal/Assembly-CSharp.dll" ] || fail "no salió modding-api/OutputFinal/Assembly-CSharp.dll"
# La librería nativa con la que la Modding API precarga escenas: el .csproj solo la copia compilando con
# --runtime win-x64, que rompe otro proyecto de la solución, así que se copia del paquete NuGet
native="$(ls "$API/Assembly-CSharp/packages/unityscenerepacker/"*/runtimes/win-x64/native/unityscenerepacker.dll 2>/dev/null | head -n1)"
[ -n "$native" ] || fail "no está unityscenerepacker.dll en los paquetes NuGet de la Modding API"
cp "$native" "$API/OutputFinal/"

echo "4. modding-api/OutputFinal → vendor/modding-api/OutputFinal"
rm -rf "$ROOT/vendor/modding-api/OutputFinal"
mkdir -p "$ROOT/vendor/modding-api"
cp -r "$API/OutputFinal" "$ROOT/vendor/modding-api/OutputFinal"

echo "5. lib/: Managed del juego + la Modding API"
rm -rf "$ROOT/lib"
mkdir -p "$ROOT/lib"
cp "$API/Vanilla/"* "$ROOT/lib/"
find "$API/OutputFinal" -maxdepth 1 -type f -name "*.dll" -exec cp {} "$ROOT/lib/" \;

echo
echo "Listo. Para compilar el mod: dotnet build src/HKMultiplayer/HKMultiplayer.csproj -c Release"
echo "Para probarlo en copias del juego: bash tools/test-cycle.sh; para el paquete de jugadores: bash tools/package.sh"
