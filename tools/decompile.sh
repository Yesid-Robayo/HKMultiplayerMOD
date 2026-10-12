#!/usr/bin/env bash
# Descompila con ILSpy el código del juego contra el que compila el mod (lib/, con la Modding API) en decomp/, un
# archivo .cs por clase, para consultar cómo hace algo el juego. decomp/ es código de Team Cherry: solo en local, nunca
# se sube (.gitignore). Las FSM de PlayMaker (jefes, NPC, ascensores...) no salen aquí: van en las escenas, no en las DLL.
# Uso: bash tools/decompile.sh   (después de tools/setup-dev.sh; necesita ilspycmd: dotnet tool install -g ilspycmd)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LIB="$ROOT/lib"
OUT="$ROOT/decomp"

fail() { echo "decompile.sh: $*" >&2; exit 1; }
[ -f "$LIB/Assembly-CSharp.dll" ] || fail "no está lib/Assembly-CSharp.dll: ejecuta antes bash tools/setup-dev.sh"
command -v ilspycmd >/dev/null || fail "falta ilspycmd: dotnet tool install -g ilspycmd"

for name in Assembly-CSharp PlayMaker Assembly-CSharp-firstpass; do
  rm -rf "${OUT:?}/$name"
  mkdir -p "$OUT/$name"
  ilspycmd -p -o "$OUT/$name" -r "$LIB" "$LIB/$name.dll" 2>&1 | grep -vi "latest version\|not using the latest" || true
  echo "$name: $(find "$OUT/$name" -name '*.cs' | wc -l) archivos .cs"
done
echo "Listo en decomp/"
