#!/usr/bin/env bash
# Compila el mod en Release (queda en out/HKMultiplayer). Con --game lo instala también en el juego de Steam.
# Uso: bash tools/test-cycle.sh [--game]
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="/c/Program Files (x86)/Steam/steamapps/common/Hollow Knight"
MOD_NAME="HKMultiplayer"
INSTALL_GAME=false
while [ $# -gt 0 ]; do
  case "$1" in
    --game) INSTALL_GAME=true ;;
  esac
  shift
done

dotnet build "$ROOT/src/$MOD_NAME/$MOD_NAME.csproj" -c Release -nologo -v:q 2>&1 \
  | grep -E " error |Errores" | sed -E 's/ \[.*//'

if ! $INSTALL_GAME; then
  echo "compilado en out/$MOD_NAME"
  exit 0
fi

managed="$GAME/hollow_knight_Data/Managed"
if [ ! -d "$managed" ]; then
  echo "no está el juego en $GAME" >&2
  exit 1
fi

# El mod anterior (HollowKnightCoop) también abre la red: no pueden estar los dos a la vez
rm -rf "$managed/Mods/HollowKnightCoop"
mkdir -p "$managed/Mods/$MOD_NAME"
cp "$ROOT/out/$MOD_NAME/"* "$managed/Mods/$MOD_NAME/" || { echo "no se pudo instalar en $managed (¿juego abierto?)" >&2; exit 1; }
echo "instalado en el juego"
