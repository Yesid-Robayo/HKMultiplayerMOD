#!/usr/bin/env bash
# Revisa las rutas sin abrir el juego: que existan las salas, las puertas y los campos de PlayerData que usan, y que
# todos los pasos sean conocidos. Tarda unos segundos; tools/run-routes.sh lo ejecuta antes de empezar.
# Uso: bash tools/routes/check-routes.sh [archivos .route]   (sin argumentos, todas)
#
# Las salas salen de la lista de escenas del juego (globalgamemanagers, en orden de compilación: la sala N es el archivo
# levelN) y la puerta se busca como texto dentro de ese archivo. Los campos se buscan en los nombres de Assembly-CSharp
# (como final de un nombre: .NET guarda un nombre dentro de otro más largo que acaba igual).

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
GAME_DATA="${HK_STEAM_GAME:-/c/Program Files (x86)/Steam/steamapps/common/Hollow Knight}/hollow_knight_Data"
STEPS='title|room|host|guest|both|wait|shared|private|same-pd|expect-pd|partner-moves|breakables-sync|enemies-sync|objects-sync|rock-sync|scene-data-sync|boss|expect-log|rejoin|die'

files=("$@")
[ ${#files[@]} -eq 0 ] && files=("$ROOT"/tests/routes/*.route)

mapfile -t scenes < <(LC_ALL=C grep -aoE "Assets/[A-Za-z0-9_ ./-]+\.unity" "$GAME_DATA/globalgamemanagers" | sed -E 's|.*/||; s|\.unity$||')
declare -A scene_index
for i in "${!scenes[@]}"; do scene_index["${scenes[$i]}"]=$i; done

problems=0
report() { echo "$1"; problems=$((problems + 1)); }

fields="$(for f in "${files[@]}"; do sed 's/#.*//' "$f" | tr -d '\r' | awk '$1 ~ /^(shared|private|expect-pd)$/ { print $3 } $1 ~ /^(same-pd|boss)$/ { print $2 }'; done | sort -u)"
if [ -n "$fields" ]; then
  while read -r missing; do
    [ -n "$missing" ] && report "campo que no existe en PlayerData: $missing"
  done < <(perl -e 'local $/; open my $fh, "<", shift; my $d = <$fh>; for (@ARGV) { print "$_\n" unless $d =~ /\Q$_\E\x00/ }' \
    "$GAME_DATA/Managed/Assembly-CSharp.dll" $fields)
fi

for file in "${files[@]}"; do
  name="$(basename "$file")"
  n=0
  while IFS= read -r raw || [ -n "$raw" ]; do
    n=$((n + 1))
    line="$(echo "${raw%%#*}" | tr -d '\r' | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//')"
    [ -z "$line" ] && continue
    read -r -a args <<< "$line"
    if ! [[ "${args[0]}" =~ ^($STEPS)$ ]]; then
      report "$name:$n: paso desconocido '${args[0]}'"
      continue
    fi

    case "${args[0]}" in
      room)
        scene="${args[1]}"; gate="${args[2]:-}"
        if [ -z "${scene_index[$scene]+x}" ]; then
          report "$name:$n: la sala '$scene' no existe"
        elif [ -z "$gate" ]; then
          report "$name:$n: falta la puerta de '$scene'"
        elif ! LC_ALL=C grep -aq "$gate" "$GAME_DATA/level${scene_index[$scene]}"; then
          report "$name:$n: '$scene' no tiene la puerta '$gate' (tiene: $(LC_ALL=C grep -aoE '(left|right|top|bot|door)[0-9]{1,2}' "$GAME_DATA/level${scene_index[$scene]}" | sort -u | paste -sd' '))"
        fi
        ;;
      shared|private)
        [[ "${args[1]:-}" =~ ^(host|guest)$ ]] && [ -n "${args[3]:-}" ] || report "$name:$n: uso: ${args[0]} <host|guest> <campo> <valor>"
        ;;
      expect-pd)
        [[ "${args[1]:-}" =~ ^(host|guest)$ ]] && [ -n "${args[3]:-}" ] || report "$name:$n: uso: expect-pd <host|guest> <campo> <valor> [s]"
        ;;
      objects-sync|enemies-sync|die)
        [ -z "${args[1]:-}" ] || [[ "${args[1]}" =~ ^(host|guest)$ ]] || report "$name:$n: '${args[1]}' debe ser host o guest"
        ;;
      boss)
        [ -n "${args[1]:-}" ] || report "$name:$n: uso: boss <campo> [host|guest] [intentos]"
        ;;
    esac
  done < "$file"
done

if [ $problems -eq 0 ]; then
  echo "rutas correctas (${#files[@]} archivos)"
else
  echo "$problems problema(s)"
  exit 1
fi
