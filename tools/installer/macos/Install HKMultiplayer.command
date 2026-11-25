#!/bin/bash
# Instalador de HKMultiplayer para macOS (va en el paquete de tools/package-mac.sh): copia la Modding API y el mod en el
# juego de Steam. Doble clic en Finder, o desde la terminal: bash "Install HKMultiplayer.command" ["/ruta/a/Hollow Knight"]
# Para desinstalar: Steam > Verificar integridad de los archivos (deja el juego original; ver README.txt).
set -euo pipefail

HK_VERSION="1.5.12620"
PACKAGE="$(cd "$(dirname "$0")" && pwd)"

# Mensajes en español si el sistema está en español; si no, en inglés
case "$(defaults read -g AppleLocale 2>/dev/null)" in
  es*) es=true ;;
  *) es=false ;;
esac
say() { if $es; then echo "$2"; else echo "$1"; fi; }
finish() {
  echo
  say "Press Return to close this window." "Pulsa Intro para cerrar esta ventana."
  read -r _ || true
  exit "$1"
}
fail() { echo; say "ERROR: $1" "ERROR: $2"; finish 1; }

echo "HKMultiplayer @VERSION@ - macOS"
echo

GAME="${1:-$HOME/Library/Application Support/Steam/steamapps/common/Hollow Knight}"
if [ ! -d "$GAME/hollow_knight.app" ]; then
  say "Hollow Knight was not found in the usual Steam folder." "No se encontró Hollow Knight en la carpeta habitual de Steam."
  say "In Steam: right-click Hollow Knight > Manage > Browse local files. Drag that folder onto this window and press Return:" \
    "En Steam: clic derecho en Hollow Knight > Administrar > Ver archivos locales. Arrastra esa carpeta a esta ventana y pulsa Intro:"
  read -r GAME || true
  # Al arrastrar, la terminal escapa los espacios con "\" y deja un espacio al final
  GAME="${GAME//\\/}"
  GAME="${GAME%"${GAME##*[![:space:]]}"}"
fi
APP="$GAME/hollow_knight.app"
MANAGED="$APP/Contents/Resources/Data/Managed"
[ -f "$MANAGED/Assembly-CSharp.dll" ] ||
  fail "this is not the Hollow Knight folder: $GAME" "esta no es la carpeta de Hollow Knight: $GAME"

version="$(defaults read "$APP/Contents/Info" CFBundleShortVersionString 2>/dev/null || true)"
[ "$version" = "$HK_VERSION" ] ||
  fail "this package is for Hollow Knight $HK_VERSION, but the game is $version. Update the game in Steam." \
    "este paquete es para Hollow Knight $HK_VERSION, pero el juego es la $version. Actualiza el juego en Steam."

if pgrep -x "Hollow Knight" >/dev/null; then
  fail "close Hollow Knight and run the installer again." "cierra Hollow Knight y vuelve a abrir el instalador."
fi

# Lo descargado lleva la marca de cuarentena de macOS: sin quitarla, macOS puede bloquear la librería nativa de la API
xattr -dr com.apple.quarantine "$PACKAGE/files" 2>/dev/null || true
cp -R "$PACKAGE/files/Managed/" "$MANAGED/" ||
  fail "could not copy the files into $MANAGED" "no se pudieron copiar los archivos en $MANAGED"

echo
say "Installed in $GAME" "Instalado en $GAME"
say "Open Hollow Knight from Steam: \"Multiplayer\" appears in the main menu." \
  "Abre Hollow Knight desde Steam: \"Multijugador\" aparece en el menú principal."
finish 0
