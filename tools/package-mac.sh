#!/usr/bin/env bash
# Arma el paquete para jugadores de macOS: out/package/HKMultiplayer-<versión>-macOS.zip, con una carpeta dentro:
#   Install HKMultiplayer.command              el instalador (tools/installer/macos): doble clic en Finder
#   README.txt, LEEME.txt                      instrucciones en inglés y en español
#   LICENSE.txt, THIRD_PARTY_NOTICES.txt       licencia del mod y avisos de los componentes de terceros
#   files/Managed/                             la Modding API compilada contra el juego de macOS
#                                              (vendor/modding-api/OutputFinal) y Mods/HKMultiplayer
# La versión del mod es la misma que en Windows: los jugadores de los dos sistemas juegan juntos.
# Uso (en un Mac, tras tools/setup-dev.sh): bash tools/package-mac.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MOD_NAME="HKMultiplayer"
API_DIR="$ROOT/vendor/modding-api/OutputFinal"
BUILD_DIR="$ROOT/out/$MOD_NAME"
MAC_DIR="$ROOT/tools/installer/macos"
MOD_FILES=("$MOD_NAME.dll" "$MOD_NAME.pdb" "BouncyCastle.Cryptography.dll")

fail() { echo "package-mac.sh: $*" >&2; exit 1; }
[ "$(uname -s)" = Darwin ] || fail "hay que armarlo en un Mac: la Modding API se compila contra el juego de macOS"

VERSION="$(grep -oE 'const string Version = "[^"]+"' "$ROOT/src/$MOD_NAME/${MOD_NAME}Mod.cs" | cut -d'"' -f2)"
[ -n "$VERSION" ] || fail "no se encontró la versión en src/$MOD_NAME/${MOD_NAME}Mod.cs"
NAME="$MOD_NAME-$VERSION-macOS"
PACKAGE_DIR="$ROOT/out/package"
STAGE="$PACKAGE_DIR/$NAME"
ZIP="$PACKAGE_DIR/$NAME.zip"
MANAGED="$STAGE/files/Managed"

# La librería nativa de macOS solo está si setup-dev.sh compiló la API en un Mac (contra el juego de macOS)
[ -f "$API_DIR/libunityscenerepacker.dylib" ] ||
  fail "falta la Modding API compilada en macOS en vendor/modding-api/OutputFinal (bash tools/setup-dev.sh)"

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

sed "s/@VERSION@/$VERSION/g" "$MAC_DIR/Install HKMultiplayer.command" > "$STAGE/Install HKMultiplayer.command"
chmod +x "$STAGE/Install HKMultiplayer.command"
for readme in LEEME.txt README.txt; do
  sed "s/@VERSION@/$VERSION/g" "$MAC_DIR/$readme" > "$STAGE/$readme"
done
cp "$ROOT/LICENSE" "$STAGE/LICENSE.txt"
cp "$ROOT/THIRD_PARTY_NOTICES.txt" "$STAGE/THIRD_PARTY_NOTICES.txt"

# Sin rutas de este Mac dentro del paquete (el .pdb del mod usa PathMap; esto comprueba que nada más la lleve)
leaks="$(grep -rlF "$HOME" "$STAGE" || true)"
[ -z "$leaks" ] || fail "llevan la ruta de este Mac ($HOME): $leaks"

# ditto conserva el permiso de ejecución del instalador; sin atributos extendidos ni archivos ._
(cd "$PACKAGE_DIR" && ditto -c -k --norsrc --noextattr --keepParent "$NAME" "$NAME.zip")
rm -rf "$STAGE"
[ -f "$ZIP" ] || fail "no se creó el zip"
echo "out/package/$NAME.zip ($(( $(stat -f %z "$ZIP") / 1024 )) KB)"
