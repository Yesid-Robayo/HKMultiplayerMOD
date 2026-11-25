#!/usr/bin/env bash
# Arma el paquete para jugadores de Linux (experimental, aún sin probar): out/package/HKMultiplayer-<versión>-Linux.zip,
# con una carpeta dentro:
#   install.sh                                 el instalador (tools/installer/linux): bash install.sh
#   README.txt, LEEME.txt                      instrucciones en inglés y en español
#   LICENSE.txt, THIRD_PARTY_NOTICES.txt       licencia del mod y avisos de los componentes de terceros
#   files/Managed/                             la Modding API compilada contra el juego de Linux
#                                              (vendor/modding-api/OutputFinal) y Mods/HKMultiplayer
# La versión del mod es la misma que en Windows y macOS: los jugadores de los tres sistemas juegan juntos.
# Uso (en Linux, tras tools/setup-dev.sh con la versión de Linux del juego): bash tools/package-linux.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MOD_NAME="HKMultiplayer"
API_DIR="$ROOT/vendor/modding-api/OutputFinal"
BUILD_DIR="$ROOT/out/$MOD_NAME"
LINUX_DIR="$ROOT/tools/installer/linux"
MOD_FILES=("$MOD_NAME.dll" "$MOD_NAME.pdb" "BouncyCastle.Cryptography.dll")

fail() { echo "package-linux.sh: $*" >&2; exit 1; }
[ "$(uname -s)" = Linux ] || fail "hay que armarlo en Linux: la Modding API se compila contra el juego de Linux"
command -v zip >/dev/null || fail "falta zip"

VERSION="$(grep -oE 'const string Version = "[^"]+"' "$ROOT/src/$MOD_NAME/${MOD_NAME}Mod.cs" | cut -d'"' -f2)"
[ -n "$VERSION" ] || fail "no se encontró la versión en src/$MOD_NAME/${MOD_NAME}Mod.cs"
NAME="$MOD_NAME-$VERSION-Linux"
PACKAGE_DIR="$ROOT/out/package"
STAGE="$PACKAGE_DIR/$NAME"
ZIP="$PACKAGE_DIR/$NAME.zip"
MANAGED="$STAGE/files/Managed"

# La librería nativa de Linux solo está si setup-dev.sh compiló la API en Linux (contra el juego de Linux)
[ -f "$API_DIR/libunityscenerepacker.so" ] ||
  fail "falta la Modding API compilada en Linux en vendor/modding-api/OutputFinal (bash tools/setup-dev.sh)"

echo "compilando $MOD_NAME $VERSION..."
build_log="$(dotnet build "$ROOT/src/$MOD_NAME/$MOD_NAME.csproj" -c Release -nologo -v:q 2>&1)" || {
  echo "$build_log" | grep -E " error " | sed -E 's/ \[.*//' >&2
  fail "no compila"
}

rm -rf "$STAGE" "$ZIP"
mkdir -p "$MANAGED/Mods/$MOD_NAME"
find "$API_DIR" -maxdepth 1 -type f ! -name "README.md" -exec cp {} "$MANAGED/" \;
for file in "${MOD_FILES[@]}"; do
  cp "$BUILD_DIR/$file" "$MANAGED/Mods/$MOD_NAME/" || fail "falta out/$MOD_NAME/$file tras compilar"
done

sed "s/@VERSION@/$VERSION/g" "$LINUX_DIR/install.sh" > "$STAGE/install.sh"
chmod +x "$STAGE/install.sh"
for readme in LEEME.txt README.txt; do
  sed "s/@VERSION@/$VERSION/g" "$LINUX_DIR/$readme" > "$STAGE/$readme"
done
cp "$ROOT/LICENSE" "$STAGE/LICENSE.txt"
cp "$ROOT/THIRD_PARTY_NOTICES.txt" "$STAGE/THIRD_PARTY_NOTICES.txt"

# Sin rutas de este PC dentro del paquete (el .pdb del mod usa PathMap; esto comprueba que nada más la lleve)
leaks="$(grep -rlF "$HOME" "$STAGE" || true)"
[ -z "$leaks" ] || fail "llevan la ruta de este PC ($HOME): $leaks"

# zip conserva el permiso de ejecución del instalador
(cd "$PACKAGE_DIR" && zip -qrX "$NAME.zip" "$NAME")
rm -rf "$STAGE"
[ -f "$ZIP" ] || fail "no se creó el zip"
echo "out/package/$NAME.zip ($(( $(stat -c %s "$ZIP") / 1024 )) KB)"
