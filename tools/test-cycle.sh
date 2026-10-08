#!/usr/bin/env bash
# Compila el mod y lo instala en las copias de prueba (test-instances/*). Con --game también en el juego de Steam.
# Uso: bash tools/test-cycle.sh [--game]
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="/c/Program Files (x86)/Steam/steamapps/common/Hollow Knight"
MOD_NAME="HKMultiplayer"
INSTALL_GAME=false
[ "${1:-}" = "--game" ] && INSTALL_GAME=true

dotnet build "$ROOT/src/$MOD_NAME/$MOD_NAME.csproj" -c Release -nologo -v:q 2>&1 \
  | grep -E " error |Errores" | sed -E 's/ \[.*//'

targets=("$ROOT"/test-instances/*/hollow_knight_Data/Managed)
$INSTALL_GAME && targets+=("$GAME/hollow_knight_Data/Managed")

for managed in "${targets[@]}"; do
  [ -d "$managed" ] || continue
  # El mod anterior (HollowKnightCoop) también abre la red: no pueden estar los dos a la vez
  rm -rf "$managed/Mods/HollowKnightCoop"
  mkdir -p "$managed/Mods/$MOD_NAME"
  cp "$ROOT/out/$MOD_NAME/"* "$managed/Mods/$MOD_NAME/"
done

if $INSTALL_GAME; then
  echo "instalado en las copias de prueba y en el juego"
else
  echo "instalado en las copias de prueba"
fi
