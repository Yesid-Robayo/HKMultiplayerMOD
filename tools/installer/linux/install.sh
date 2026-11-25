#!/usr/bin/env bash
# Instalador de HKMultiplayer para Linux (experimental, aún sin probar; va en el paquete de tools/package-linux.sh): copia
# la Modding API y el mod en la versión de Linux del juego de Steam, guardando antes una copia de lo que cambia.
# Uso: bash install.sh ["/ruta/a/Hollow Knight"]        Para deshacerlo: bash install.sh --uninstall ["/ruta"]
# (o Steam > Verificar integridad de los archivos del juego).
set -euo pipefail

PACKAGE="$(cd "$(dirname "$0")" && pwd)"

# Mensajes en español si el sistema está en español; si no, en inglés
case "${LANG:-}${LC_ALL:-}" in
  *es_*) es=true ;;
  *) es=false ;;
esac
say() { if $es; then echo "$2"; else echo "$1"; fi; }
fail() { echo; say "ERROR: $1" "ERROR: $2"; exit 1; }

UNINSTALL=false
if [ "${1:-}" = "--uninstall" ]; then
  UNINSTALL=true
  shift
fi

echo "HKMultiplayer @VERSION@ - Linux (experimental)"
echo

GAME="${1:-$HOME/.local/share/Steam/steamapps/common/Hollow Knight}"
if [ ! -d "$GAME/hollow_knight_Data" ]; then
  say "Hollow Knight was not found in the usual Steam folder." "No se encontró Hollow Knight en la carpeta habitual de Steam."
  say "In Steam: right-click Hollow Knight > Manage > Browse local files, and run: bash install.sh \"/path/to/Hollow Knight\"" \
    "En Steam: clic derecho en Hollow Knight > Administrar > Ver archivos locales, y ejecuta: bash install.sh \"/ruta/a/Hollow Knight\""
  exit 1
fi
MANAGED="$GAME/hollow_knight_Data/Managed"
BACKUP="$GAME/HKMultiplayer-backup/Managed"
[ -f "$MANAGED/Assembly-CSharp.dll" ] ||
  fail "this is not the Hollow Knight folder: $GAME" "esta no es la carpeta de Hollow Knight: $GAME"
# Con Proton el juego es la versión de Windows: para esa va el paquete de Windows
[ -e "$GAME/hollow_knight.x86_64" ] ||
  fail "this is not the Linux version of the game (with Proton, use the Windows package)." \
    "esta no es la versión de Linux del juego (con Proton, usa el paquete de Windows)."

if pgrep -f "hollow_knight.x86_64" >/dev/null; then
  fail "close Hollow Knight and run the installer again." "cierra Hollow Knight y vuelve a ejecutar el instalador."
fi

if $UNINSTALL; then
  [ -d "$BACKUP" ] || fail "there is no backup in $BACKUP: use Steam's Verify integrity of game files." \
    "no hay copia en $BACKUP: usa Verificar integridad de los archivos del juego de Steam."
  # Lo que añadió la instalación y no estaba en el juego (la Modding API y el mod) se quita; lo que cambió vuelve
  while IFS= read -r added; do
    [ -n "$added" ] && rm -rf "${MANAGED:?}/$added"
  done < "$BACKUP/../added.txt"
  # La carpeta del mod, aunque Mods ya existiera (con otros mods)
  rm -rf "${MANAGED:?}/Mods/HKMultiplayer"
  cp -R "$BACKUP/." "$MANAGED/"
  rm -rf "$GAME/HKMultiplayer-backup"
  say "Uninstalled: the game is as it was." "Desinstalado: el juego queda como estaba."
  exit 0
fi

# Copia de lo que se va a cambiar (solo la primera vez: al actualizar ya está la del juego original), y lista de lo que
# se añade, para poder deshacerlo
if [ ! -d "$BACKUP" ]; then
  mkdir -p "$BACKUP"
  : > "$BACKUP/../added.txt"
  (cd "$PACKAGE/files/Managed" && find . -mindepth 1 -maxdepth 1) | while IFS= read -r item; do
    item="${item#./}"
    if [ -e "$MANAGED/$item" ]; then
      cp -R "$MANAGED/$item" "$BACKUP/"
    else
      echo "$item" >> "$BACKUP/../added.txt"
    fi
  done
fi

cp -R "$PACKAGE/files/Managed/." "$MANAGED/" ||
  fail "could not copy the files into $MANAGED" "no se pudieron copiar los archivos en $MANAGED"

echo
say "Installed in $GAME" "Instalado en $GAME"
say "Open Hollow Knight from Steam: \"Multiplayer\" appears in the main menu." \
  "Abre Hollow Knight desde Steam: \"Multijugador\" aparece en el menú principal."
say "This Linux version is experimental: if something fails, please report it with the ModLog (see README.txt)." \
  "Esta versión de Linux es experimental: si algo falla, avísanos con el ModLog (mira LEEME.txt)."
