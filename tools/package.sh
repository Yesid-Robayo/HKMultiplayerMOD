#!/usr/bin/env bash
# Arma el paquete para jugadores: out/package/HKMultiplayer-<versión>.zip, con una carpeta HKMultiplayer-<versión>:
#   HKMultiplayer Setup.exe                    el instalador (ventana para instalar, actualizar o desinstalar);
#                                              solo abre scripts/setup.ps1 sin consola (tools/installer/launcher)
#   LEEME.txt, README.txt                      instrucciones en español y en inglés (con la instalación a mano)
#   LICENSE.txt, THIRD_PARTY_NOTICES.txt       licencia del mod y avisos de los componentes de terceros
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
# El instalador lee la versión instalada del archivo (AssemblyVersion): si no coincide, diría otra versión
ASSEMBLY_VERSION="$(grep -oE '<AssemblyVersion>[^<]+' "$ROOT/src/$MOD_NAME/$MOD_NAME.csproj" | cut -d'>' -f2)"
[ "$ASSEMBLY_VERSION" = "$VERSION.0" ] || [ "$ASSEMBLY_VERSION" = "$VERSION" ] ||
  fail "la versión del código ($VERSION) y la del .csproj ($ASSEMBLY_VERSION) no coinciden"
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

echo "compilando el instalador..."
SETUP_OUT="$ROOT/out/setup"
setup_log="$(dotnet build "$ROOT/tools/installer/launcher/SetupLauncher.csproj" -c Release -nologo -v:q \
  -p:Version="$VERSION" -o "$SETUP_OUT" 2>&1)" || {
  echo "$setup_log" | grep -E " error " | sed -E 's/ \[.*//' >&2
  fail "el instalador no compila"
}
[ -f "$SETUP_OUT/HKMultiplayer Setup.exe" ] || fail "falta out/setup/HKMultiplayer Setup.exe tras compilar"

rm -rf "$STAGE" "$ZIP"
mkdir -p "$MANAGED/Mods/$MOD_NAME" "$STAGE/scripts"

# Modding API: todo OutputFinal salvo su README (en inglés, con otras instrucciones de instalación)
find "$API_DIR" -maxdepth 1 -type f ! -name "README.md" -exec cp {} "$MANAGED/" \;
for file in "${MOD_FILES[@]}"; do
  cp "$BUILD_DIR/$file" "$MANAGED/Mods/$MOD_NAME/"
done

# Instalador e instrucciones (en español y en inglés)
cp "$SETUP_OUT/HKMultiplayer Setup.exe" "$STAGE/HKMultiplayer Setup.exe"
cp "$ROOT/tools/installer/"*.ps1 "$STAGE/scripts/"
# Su icono con todos los tamaños (la ventana lo pone en el título y en la barra de tareas)
cp "$ROOT/tools/installer/launcher/setup.ico" "$STAGE/scripts/setup.ico"
printf '%s\r\n' "$VERSION" > "$STAGE/scripts/version.txt"
for readme in LEEME.txt README.txt; do
  sed "s/@VERSION@/$VERSION/g" "$ROOT/tools/installer/$readme" > "$STAGE/$readme"
done
# Licencia del mod y avisos de los componentes de terceros que van en el paquete (sus licencias MIT lo piden)
sed 's/$/\r/' "$ROOT/LICENSE" > "$STAGE/LICENSE.txt"
sed 's/$/\r/' "$ROOT/THIRD_PARTY_NOTICES.txt" > "$STAGE/THIRD_PARTY_NOTICES.txt"

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
