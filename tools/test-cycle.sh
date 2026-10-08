#!/usr/bin/env bash
# Compila el mod y lo instala en las copias de prueba (test-instances/*). Con --game también en el juego de Steam.
# Uso: bash tools/test-cycle.sh [--game] [--only "b c"]
#   --only "letras"   instala solo en esas copias (para no tocar las de otro agente que tenga sus juegos abiertos)
#   HK_INSTANCES_DIR  carpeta de las copias (por defecto test-instances de este repo; útil desde un worktree)
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="/c/Program Files (x86)/Steam/steamapps/common/Hollow Knight"
MOD_NAME="HKMultiplayer"
INSTALL_GAME=false
ONLY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --game) INSTALL_GAME=true ;;
    --only) ONLY="$2"; shift ;;
  esac
  shift
done
INSTANCES_DIR="${HK_INSTANCES_DIR:-$ROOT/test-instances}"

dotnet build "$ROOT/src/$MOD_NAME/$MOD_NAME.csproj" -c Release -nologo -v:q 2>&1 \
  | grep -E " error |Errores" | sed -E 's/ \[.*//'

if [ -n "$ONLY" ]; then
  targets=()
  for letter in $ONLY; do
    targets+=("$INSTANCES_DIR/$(echo "$letter" | tr 'A-Z' 'a-z')/hollow_knight_Data/Managed")
  done
else
  targets=("$INSTANCES_DIR"/*/hollow_knight_Data/Managed)
fi
$INSTALL_GAME && targets+=("$GAME/hollow_knight_Data/Managed")

for managed in "${targets[@]}"; do
  [ -d "$managed" ] || continue
  # El mod anterior (HollowKnightCoop) también abre la red: no pueden estar los dos a la vez
  rm -rf "$managed/Mods/HollowKnightCoop"
  mkdir -p "$managed/Mods/$MOD_NAME"
  cp "$ROOT/out/$MOD_NAME/"* "$managed/Mods/$MOD_NAME/" || echo "no se pudo instalar en $managed (¿juego abierto?)" >&2
done

if $INSTALL_GAME; then
  echo "instalado en las copias de prueba y en el juego"
else
  echo "instalado en las copias de prueba"
fi
