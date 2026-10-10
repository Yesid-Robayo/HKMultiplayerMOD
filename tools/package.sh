#!/usr/bin/env bash
# Arma el paquete para jugadores: out/package/HKMultiplayer-<versión>.zip, con una carpeta HKMultiplayer-<versión>:
#   HKMultiplayer Setup.bat                    el instalador (ventana para instalar, actualizar o desinstalar)
#   LEEME.txt, README.txt                      instrucciones en español y en inglés (con la instalación a mano)
#   scripts/                                   el instalador en PowerShell y la versión del paquete
#   files/hollow_knight_Data/Managed/          la Modding API compilada (vendor/modding-api/OutputFinal) y
#                                              Mods/HKMultiplayer (el mod y su cifrado, BouncyCastle)
#   SHA256SUMS.txt                             hashes de files/: el instalador comprueba el paquete y lo copiado
# Nada más del juego de Team Cherry: solo los dos ensamblados que parchea la Modding API (Assembly-CSharp.dll y
# TeamCherry.Localization.dll), que forman parte de ella.
# Uso: bash tools/package.sh   (compila el mod en Release antes de empaquetar)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MOD_NAME="HKMultiplayer"
API_DIR="$ROOT/vendor/modding-api/OutputFinal"
BUILD_DIR="$ROOT/out/$MOD_NAME"
MOD_FILES=("$MOD_NAME.dll" "$MOD_NAME.pdb" "BouncyCastle.Cryptography.dll")

fail() { echo "package.sh: $*" >&2; exit 1; }

VERSION="$(grep -oE 'const string Version = "[^"]+"' "$ROOT/src/$MOD_NAME/${MOD_NAME}Mod.cs" | cut -d'"' -f2)"
[ -n "$VERSION" ] || fail "no se encontró la versión en src/$MOD_NAME/${MOD_NAME}Mod.cs"
NAME="$MOD_NAME-$VERSION"
PACKAGE_DIR="$ROOT/out/package"
STAGE="$PACKAGE_DIR/$NAME"
ZIP="$PACKAGE_DIR/$NAME.zip"
MANAGED="$STAGE/files/hollow_knight_Data/Managed"

[ -f "$API_DIR/Assembly-CSharp.dll" ] || fail "falta la Modding API compilada en vendor/modding-api/OutputFinal (solo en local)"

echo "compilando $MOD_NAME $VERSION..."
build_log="$(dotnet build "$ROOT/src/$MOD_NAME/$MOD_NAME.csproj" -c Release -nologo -v:q 2>&1)" || {
  echo "$build_log" | grep -E " error " | sed -E 's/ \[.*//' >&2
  fail "no compila"
}
for file in "${MOD_FILES[@]}"; do
  [ -f "$BUILD_DIR/$file" ] || fail "falta out/$MOD_NAME/$file tras compilar"
done

rm -rf "$STAGE" "$ZIP"
mkdir -p "$MANAGED/Mods/$MOD_NAME" "$STAGE/scripts"

# Modding API: todo OutputFinal salvo su README (en inglés, con otras instrucciones de instalación)
find "$API_DIR" -maxdepth 1 -type f ! -name "README.md" -exec cp {} "$MANAGED/" \;
for file in "${MOD_FILES[@]}"; do
  cp "$BUILD_DIR/$file" "$MANAGED/Mods/$MOD_NAME/"
done

# Instalador e instrucciones (en español y en inglés)
cp "$ROOT/tools/installer/setup.bat" "$STAGE/HKMultiplayer Setup.bat"
cp "$ROOT/tools/installer/"*.ps1 "$STAGE/scripts/"
printf '%s\r\n' "$VERSION" > "$STAGE/scripts/version.txt"
for readme in LEEME.txt README.txt; do
  sed "s/@VERSION@/$VERSION/g" "$ROOT/tools/installer/$readme" > "$STAGE/$readme"
done

# Inventario con hashes
(cd "$STAGE" && find ./files -type f -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS.txt)

# Zip con la carpeta del paquete dentro (tar.exe de Windows: rutas con "/", las lee cualquier descompresor)
WIN_TAR="/c/Windows/System32/tar.exe"
if [ -x "$WIN_TAR" ]; then
  (cd "$PACKAGE_DIR" && "$WIN_TAR" -a -cf "$NAME.zip" "$NAME")
else
  powershell.exe -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$STAGE")' -DestinationPath '$(cygpath -w "$ZIP")' -Force"
fi
rm -rf "$STAGE"
[ -f "$ZIP" ] || fail "no se creó el zip"
echo "out/package/$NAME.zip ($(( $(stat -c %s "$ZIP") / 1024 )) KB)"
