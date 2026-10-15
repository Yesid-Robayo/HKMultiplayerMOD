#!/usr/bin/env bash
# Prueba del paquete para jugadores (tools/package.sh) sobre copias del juego dentro de test-instances, sin tocar el
# juego de Steam: crea las copias, las deja como el juego sin mods, instala con "HKMultiplayer Setup.exe" /install y la ruta, arranca
# la copia, mira en su ModLog que carga el mod y el menú Multijugador, desinstala y comprueba que queda como estaba.
#
# Uso: HK_INSTANCES_DIR=<carpeta con la copia b> bash tools/manual-tests/installer-test.sh <zip> [letras]
#   letras: por defecto "H I". H es el juego limpio; I además tiene el mod anterior (HollowKnightCoop) y otro mod
#   (Satchel) en Mods, para ver que el instalador los quita y el desinstalador los devuelve.
# Los resultados y las listas de archivos quedan en out/installer-test/. Las copias se quedan al terminar (enlaces duros,
# casi no ocupan): se pueden borrar con rm -rf, que no toca los archivos de Steam.
#
# Las copias de ensure_instance son enlaces duros al juego de Steam. Antes de instalar, Managed y steam_appid.txt se
# pasan a copias reales: si no, el instalador escribiría dentro de los archivos de Steam.
# Sin la Assembly-CSharp.dll original del juego en este PC, el "juego limpio" usa Assembly-CSharp.dll.v (otra versión,
# distinta de la del paquete) en su lugar: sirve para ver que el original se guarda y se devuelve.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
source "$ROOT/tools/routes/lib.sh"
ZIP="${1:?falta la ruta del zip del paquete}"
LETTERS="${2:-H I}"
WORK="$ROOT/out/installer-test"
LAUNCH_TIMEOUT=180

fail() { echo "FALLO: $*"; FAILED=1; }
FAILED=0

# Archivos de la Modding API y del mod: se quitan para dejar la copia como el juego sin mods
API_PATTERNS=(Assembly-CSharp.xml 'MMHOOK_*' 'Mono.Cecil*' 'MonoMod.*' unityscenerepacker.dll README.md TeamCherry.Localization.xml)

prepare_copy() {
  local letter="$1" dir managed
  dir="$(_inst_dir "$letter")"
  managed="$dir/hollow_knight_Data/Managed"
  stop_instance "$letter"
  if [ -d "$dir" ]; then
    echo "[$letter] se borra la copia anterior"
    rm -rf "$dir"
  fi
  ensure_instance "$letter" || { fail "[$letter] no se pudo crear la copia"; return 1; }

  # Managed y steam_appid.txt como archivos propios de la copia (sin enlaces al juego de Steam)
  mv "$managed" "$managed.links"
  cp -r "$managed.links" "$managed"
  rm -rf "$managed.links"
  cp "$dir/steam_appid.txt" "$dir/steam_appid.txt.real"
  rm -f "$dir/steam_appid.txt"
  mv "$dir/steam_appid.txt.real" "$dir/steam_appid.txt"
  if [ -n "$(find "$managed" "$dir/steam_appid.txt" -type f -links +1 | head -n1)" ]; then
    fail "[$letter] quedan enlaces duros en Managed"; return 1
  fi

  # Como el juego sin mods. En este PC no hay un Assembly-CSharp.dll original de esta versión (el .v es de una versión
  # anterior del juego, que el instalador rechaza): se usa el actual sin los textos ModHooks y MonoMod, que es lo que distingue un
  # original de uno parcheado
  LC_ALL=C perl -0777 -pi -e 's/ModHooks/XodHooks/g; s/MonoMod/XonoMod/g' "$managed/Assembly-CSharp.dll"
  rm -f "$managed/Assembly-CSharp.dll.v"
  local pattern
  for pattern in "${API_PATTERNS[@]}"; do
    rm -f "$managed"/$pattern
  done
  rm -rf "$managed/Mods"

  if [ "$letter" = I ]; then
    # Mod anterior (sin sus claves) y otro mod, como los dejaba el instalador viejo o un jugador
    mkdir -p "$managed/Mods/HollowKnightCoop" "$managed/Mods/Satchel"
    cp "$HK_STEAM_GAME/hollow_knight_Data/Managed/Mods/HollowKnightCoop/HollowKnightCoop.dll" \
      "$HK_STEAM_GAME/hollow_knight_Data/Managed/Mods/HollowKnightCoop/BouncyCastle.Cryptography.dll" \
      "$managed/Mods/HollowKnightCoop/"
    cp "$HK_STEAM_GAME/hollow_knight_Data/Managed/Mods/Disabled/Satchel/Satchel.dll" "$managed/Mods/Satchel/"
  fi
  echo "[$letter] copia limpia lista en $dir"
}

# Lista de la copia: todas las rutas con su tamaño y el hash de lo que está fuera de los datos del juego
# (Managed y la raíz), que es lo que tocan el instalador y el desinstalador
snapshot() {
  local letter="$1" out="$2" dir
  dir="$(_inst_dir "$letter")"
  (cd "$dir" && find . -printf '%y %s %p\n' | grep -v '^d' | sort -k3 > "$out.files"
   find . -type d | sort > "$out.dirs"
   { find . -maxdepth 1 -type f -print0; find ./hollow_knight_Data/Managed -type f -print0; } | sort -z | xargs -0 sha256sum > "$out.sha")
}

same_snapshot() {
  local a="$1" b="$2" kind ok=0
  for kind in files dirs sha; do
    if ! diff -q "$a.$kind" "$b.$kind" >/dev/null; then
      echo "  diferencias ($kind):"; diff "$a.$kind" "$b.$kind" | head -n 20 | sed 's/^/    /'
      ok=1
    fi
  done
  return $ok
}

# Ejecuta el instalador del paquete en la consola (/install o /uninstall) con la ruta de la copia (la pausa del final
# recibe una entrada vacía)
run_bat() {
  local mode="$1" letter="$2" log="$3" wrapper
  wrapper="$WORK/run-$letter.cmd"
  printf '@echo off\r\ncall "%s" %s "%s" < nul\r\nexit /b %%errorlevel%%\r\n' \
    "$(cygpath -w "$PKG/HKMultiplayer Setup.exe")" "$mode" "$(cygpath -w "$(_inst_dir "$letter")")" > "$wrapper"
  cmd.exe //c "$(cygpath -w "$wrapper")" > "$log" 2>&1
  local code=$?
  sed 's/^/    /' "$log"
  return $code
}

launch_and_check() {
  local letter="$1" dir log deadline
  dir="$(_inst_dir "$letter")"
  log="$(_log_file "$letter")"
  rm -f "$log"
  (cd "$dir" && ./hollow_knight.exe -screen-fullscreen 0 -screen-width 640 -screen-height 360 >/dev/null 2>&1 &)
  deadline=$((SECONDS + LAUNCH_TIMEOUT))
  until grep -aq "Menú Multijugador añadido" "$log" 2>/dev/null; do
    if [ $SECONDS -ge $deadline ]; then
      fail "[$letter] el menú Multijugador no apareció en $LAUNCH_TIMEOUT s"
      break
    fi
    sleep 2
  done
  sleep 3
  stop_instance "$letter"
  sleep 2
  cp "$log" "$WORK/$letter-ModLog.txt" 2>/dev/null
  echo "  ModLog de $letter:"
  grep -aE "Finished loading mods|HKMultiplayer|HollowKnightCoop|Satchel|Menú Multijugador|ERROR|Exception" "$log" | head -n 20 | sed 's/^/    /'
  grep -aq "Iniciando HKMultiplayer $VERSION" "$log" || fail "[$letter] el ModLog no dice 'Iniciando HKMultiplayer $VERSION'"
  grep -aq "HKMultiplayer : $VERSION" "$log" || fail "[$letter] la Modding API no lista 'HKMultiplayer : $VERSION'"
  if grep -aqE "HollowKnightCoop :|HollowKnightCoop\.dll|Satchel :" "$log"; then fail "[$letter] carga el mod anterior u otro apartado"; fi
}

mkdir -p "$WORK"
rm -rf "$WORK/package"
mkdir -p "$WORK/package"
/c/Windows/System32/tar.exe -xf "$(cygpath -w "$ZIP")" -C "$(cygpath -w "$WORK/package")" || { echo "no se pudo descomprimir $ZIP"; exit 1; }
PKG="$(find "$WORK/package" -mindepth 1 -maxdepth 1 -type d | head -n1)"
# La carpeta del paquete se llama HKMultiplayer-<versión>
VERSION="${PKG##*/HKMultiplayer-}"
echo "paquete: $PKG (versión $VERSION)"

for letter in $LETTERS; do
  echo "=== copia $letter ==="
  prepare_copy "$letter" || continue
  snapshot "$letter" "$WORK/$letter-before"
  cp "$(_inst_dir "$letter")/hollow_knight_Data/Managed/Assembly-CSharp.dll" "$WORK/$letter-clean-Assembly-CSharp.dll"

  echo "[$letter] instalar"
  run_bat /install "$letter" "$WORK/$letter-install.txt" || fail "[$letter] la instalación terminó con error"
  snapshot "$letter" "$WORK/$letter-installed"
  if same_snapshot "$WORK/$letter-before" "$WORK/$letter-installed" >/dev/null; then fail "[$letter] la instalación no cambió nada"; fi
  [ -f "$(_inst_dir "$letter")/hollow_knight_Data/Managed/Mods/HKMultiplayer/HKMultiplayer.dll" ] || fail "[$letter] no está el mod"
  [ -d "$(_inst_dir "$letter")/hollow_knight_Data/Managed/Mods/HollowKnightCoop" ] && fail "[$letter] sigue el mod anterior"

  echo "[$letter] arrancando la copia"
  launch_and_check "$letter"

  echo "[$letter] reinstalar (actualización) y desinstalar"
  run_bat /install "$letter" "$WORK/$letter-reinstall.txt" || fail "[$letter] la segunda instalación terminó con error"
  run_bat /uninstall "$letter" "$WORK/$letter-uninstall.txt" || fail "[$letter] la desinstalación terminó con error"
  snapshot "$letter" "$WORK/$letter-after"
  if same_snapshot "$WORK/$letter-before" "$WORK/$letter-after"; then
    echo "[$letter] tras desinstalar queda igual que antes de instalar"
  else
    fail "[$letter] tras desinstalar no queda como estaba"
  fi
done

# Steam cambia Assembly-CSharp.dll entre instalar y desinstalar (el juego se actualizó o se verificó). Antes la
# desinstalación volvía a poner la copia de la primera instalación, de una versión anterior, y el juego no arrancaba.
# La "versión nueva" se hace con el Assembly-CSharp.dll de la copia limpia sin los textos ModHooks y MonoMod (lo que distingue un
# original de uno parcheado: en este PC no hay un original de verdad, el .v es una Modding API anterior) y un byte más.
# El juego no se abre en estos pasos.
letter="${LETTERS%% *}"
managed="$(_inst_dir "$letter")/hollow_knight_Data/Managed"
dll="$managed/Assembly-CSharp.dll"
sha() { sha256sum "$1" | cut -d' ' -f1; }
newer="$WORK/Assembly-CSharp.newer.dll"
LC_ALL=C perl -0777 -pe 's/ModHooks/XodHooks/g; s/MonoMod/XonoMod/g' "$WORK/$letter-clean-Assembly-CSharp.dll" > "$newer" && printf 'x' >> "$newer"
grep -aq ModHooks "$newer" && fail "[$letter] la versión nueva de prueba sigue nombrando ModHooks"

echo "[$letter] el juego se actualiza, luego se actualiza el mod y se desinstala"
run_bat /install "$letter" "$WORK/$letter-steam1-install.txt" || fail "[$letter] instalar (actualización de Steam 1)"
cp "$newer" "$dll"
run_bat /install "$letter" "$WORK/$letter-steam1-update.txt" || fail "[$letter] actualizar el mod tras la de Steam"
grep -aq "desde la última instalación\|since the last install" "$WORK/$letter-steam1-update.txt" ||
  fail "[$letter] la actualización del mod no vio que el juego cambió Assembly-CSharp.dll"
run_bat /uninstall "$letter" "$WORK/$letter-steam1-uninstall.txt" || fail "[$letter] desinstalar (actualización de Steam 1)"
[ "$(sha "$dll")" = "$(sha "$newer")" ] && echo "[$letter] queda el Assembly-CSharp.dll nuevo del juego" ||
  fail "[$letter] tras actualizar el juego y el mod, desinstalar no deja el Assembly-CSharp.dll nuevo"

echo "[$letter] el juego se actualiza y se desinstala sin actualizar el mod"
run_bat /install "$letter" "$WORK/$letter-steam2-install.txt" || fail "[$letter] instalar (actualización de Steam 2)"
cp "$newer" "$dll" && printf 'y' >> "$dll"
cp "$dll" "$newer.2"
run_bat /uninstall "$letter" "$WORK/$letter-steam2-uninstall.txt" || fail "[$letter] desinstalar (actualización de Steam 2)"
[ "$(sha "$dll")" = "$(sha "$newer.2")" ] && echo "[$letter] se deja el Assembly-CSharp.dll que puso Steam" ||
  fail "[$letter] desinstalar volvió a poner un Assembly-CSharp.dll viejo"

[ $FAILED = 0 ] && echo "RESULTADO: OK" || echo "RESULTADO: FALLO"
exit $FAILED
