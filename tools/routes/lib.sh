#!/usr/bin/env bash
# Librería de las rutas de prueba (tools/run-routes.sh). Cada "par" son dos copias del juego en modo de prueba: un host
# (que abre su mundo coop) y un invitado que se une a él. Las órdenes van por el archivo mp-test.txt de cada copia y
# los resultados se leen de su ModLog, como en tools/mp-test-lib.sh.
#
# Los pares usan sus propias copias (test-instances/d y e, f y g...), nunca b ni c: así se pueden lanzar mientras otro
# agente o una persona prueba a mano con b y c, y nunca se cierran juegos que no son suyos.

HK_TEST_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# Carpeta de las copias (se puede cambiar, p. ej. desde un worktree: HK_INSTANCES_DIR=.../test-instances)
HK_INSTANCES_DIR="${HK_INSTANCES_DIR:-$HK_TEST_ROOT/test-instances}"
HK_STEAM_GAME="${HK_STEAM_GAME:-/c/Program Files (x86)/Steam/steamapps/common/Hollow Knight}"
HK_LOCALLOW="/c/Users/$USERNAME/AppData/LocalLow/Team Cherry"
HK_TEMPLATE="B"                      # copia de la que se toman el nombre de producto parcheado y la carpeta Mods
HK_ROUTE_CODE="PRBA-TEST"
HK_ROUTE_WORLD="Ruta"
HK_ROUTE_BASE_PORT="${HK_ROUTE_BASE_PORT:-27100}"
HK_ROUTE_HEALTH="${HK_ROUTE_HEALTH:-50}"   # máscaras de los dos en cada sala de una ruta (que no mueran sin querer)

# Letras de cada par (host, invitado). Una sola letra: el nombre de producto parcheado debe medir lo mismo que el original.
HK_PAIR_HOSTS=(D F H J)
HK_PAIR_GUESTS=(E G I K)

# Errores del ModLog que no cuentan como fallo de la ruta (regex extendida). Se puede ampliar desde fuera.
HK_IGNORE_ERRORS="${HK_IGNORE_ERRORS:-zz_never_matches}"

# =====================================================================================================
# Copias del juego
# =====================================================================================================

_inst_dir() { echo "$HK_INSTANCES_DIR/$(echo "$1" | tr 'A-Z' 'a-z')"; }
_data_dir() { echo "$HK_LOCALLOW/HollowKnight$1"; }
_log_file() { echo "$(_data_dir "$1")/ModLog.txt"; }

# Crea la copia <letra> si no existe: hard links al juego de Steam (no ocupa disco), salvo los dos archivos con el nombre
# de producto (cada copia guarda sus datos en su propia carpeta de LocalLow) y la carpeta Mods (copia real, para que
# instalar el mod en una copia no lo cambie en las demás).
ensure_instance() {
  local letter="$1" dir template
  dir="$(_inst_dir "$letter")"
  template="$(_inst_dir "$HK_TEMPLATE")"
  [ -x "$dir/hollow_knight.exe" ] && return 0

  echo "creando la copia $letter en $dir"
  mkdir -p "$dir"
  local entry
  for entry in D3D12 MonoBleedingEdge UnityCrashHandler64.exe UnityPlayer.dll hollow_knight.exe hollow_knight_Data steam_appid.txt; do
    cp -al "$HK_STEAM_GAME/$entry" "$dir/" || { echo "no se pudo enlazar $entry" >&2; return 1; }
  done

  local data="$dir/hollow_knight_Data"
  rm -rf "$data/Managed/Mods"
  cp -r "$template/hollow_knight_Data/Managed/Mods" "$data/Managed/Mods"
  rm -rf "$data/Managed/Mods/HollowKnightCoop"

  local file
  for file in app.info globalgamemanagers; do
    rm -f "$data/$file"
    cp "$template/hollow_knight_Data/$file" "$data/$file"
    LC_ALL=C perl -0777 -pi -e "s/HollowKnight$HK_TEMPLATE/HollowKnight$letter/g" "$data/$file"
    if ! grep -aq "HollowKnight$letter" "$data/$file" || [ "$(stat -c %s "$data/$file")" != "$(stat -c %s "$template/hollow_knight_Data/$file")" ]; then
      echo "no se pudo cambiar el nombre de producto en $file" >&2
      return 1
    fi
  done

  # Ajustes de pantalla (PlayerPrefs) y de la Modding API como los de la plantilla; la clave de jugador del mod no se
  # copia: cada copia genera la suya y así es otro personaje
  reg copy "HKCU\\Software\\Team Cherry\\HollowKnight$HK_TEMPLATE" "HKCU\\Software\\Team Cherry\\HollowKnight$letter" /s /f >/dev/null 2>&1
  mkdir -p "$(_data_dir "$letter")"
  cp -f "$(_data_dir "$HK_TEMPLATE")/ModdingApi.GlobalSettings.json" "$(_data_dir "$letter")/" 2>/dev/null
  return 0
}

instance_running() {
  local dir; dir="$(cygpath -w "$(_inst_dir "$1")")"
  powershell.exe -NoProfile -Command "if (Get-Process hollow_knight -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$dir\\*' }) { 'si' }" 2>/dev/null | grep -q si
}

# Cierra solo esa copia
stop_instance() {
  local dir; dir="$(cygpath -w "$(_inst_dir "$1")")"
  powershell.exe -NoProfile -Command "Get-Process hollow_knight -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$dir\\*' } | Stop-Process -Force" >/dev/null 2>&1
}

# Lanza la copia en una ventana pequeña. El ModLog anterior se borra: si no, se leerían resultados de la ejecución pasada.
launch_instance() {
  local dir; dir="$(_inst_dir "$1")"
  rm -f "$(_log_file "$1")"
  (cd "$dir" && ./hollow_knight.exe -screen-fullscreen 0 -screen-width 640 -screen-height 360 -mptest >/dev/null 2>&1 &)
}

# =====================================================================================================
# Órdenes y log
# =====================================================================================================

send() { printf '%s\n' "$2" >> "$(_data_dir "$1")/mp-test.txt"; }

log_lines() { local n; n=$(wc -l < "$(_log_file "$1")" 2>/dev/null) || n=0; echo "${n:-0}"; }

# Líneas del log desde la línea <desde> (sin \r)
log_since() { tail -n +"$(( $2 + 1 ))" "$(_log_file "$1")" 2>/dev/null | tr -d '\r'; }

# Espera una línea nueva (después de <desde>) que cumpla la regex. Devuelve la línea.
wait_log_since() {
  local inst="$1" since="$2" regex="$3" timeout="${4:-30}" deadline line
  deadline=$((SECONDS + timeout))
  while [ $SECONDS -lt $deadline ]; do
    line="$(log_since "$inst" "$since" | grep -aE -m1 "$regex")"
    [ -n "$line" ] && { echo "$line"; return 0; }
    sleep 0.5
  done
  return 1
}

# Ejecuta una orden y devuelve lo que escribió en el log. Detrás se envía una orden "marca" (pdget de un campo que no
# existe, que solo escribe una línea): todo lo que hay entre la orden y la marca es su salida.
query() {
  local inst="$1" cmd="$2" timeout="${3:-20}" mark start
  mark="zz_end_${BASHPID}_${RANDOM}${RANDOM}"
  start=$(log_lines "$inst")
  send "$inst" "$cmd"
  send "$inst" "pdget $mark"
  wait_log_since "$inst" "$start" "\[TEST\] pd $mark=" "$timeout" >/dev/null || return 1
  log_since "$inst" "$start" | awk -v c="[TEST] > $cmd" -v e="[TEST] pd $mark=" \
    'index($0, e) { exit } f { print } !f && index($0, c) { f = 1 }'
}

# Valor de un campo de PlayerData en minúsculas ("" si el campo no existe)
pd_value() {
  local out
  out="$(query "$1" "pdget $2")" || return 1
  echo "$out" | grep -aoE "\[TEST\] pd $2=.*" | head -n1 | sed -E "s/.*\[TEST\] pd $2=//" | tr 'A-Z' 'a-z' | sed 's/[[:space:]]*$//'
}

# Campo de la línea de status (scene, sesión, activa, visibles...)
status_field() {
  echo "$1" | grep -aoE "$2=[^ ]+" | head -n1 | cut -d= -f2-
}

status_of() { query "$1" "status" | grep -a "\[TEST\] status" | head -n1; }

# =====================================================================================================
# Pares
# =====================================================================================================

# Prepara el par <n> (0..3): copias creadas, lanzadas, host con su mundo y el invitado dentro.
# Con <limpio>=1 el mundo de la ruta se borra antes (partida nueva desde cero).
setup_pair() {
  local n="$1" fresh="${2:-1}" host="${HK_PAIR_HOSTS[$1]}" guest="${HK_PAIR_GUESTS[$1]}"
  local port=$((HK_ROUTE_BASE_PORT + n * 10))
  ensure_instance "$host" && ensure_instance "$guest" || return 1
  stop_instance "$host"; stop_instance "$guest"; sleep 2

  if [ "$fresh" = 1 ]; then
    rm -rf "$(_data_dir "$host")/HKMultiplayer/Mundos/$HK_ROUTE_WORLD"
  fi

  launch_instance "$host"; sleep 4; launch_instance "$guest"
  wait_log_since "$host" 0 "Menú Multijugador añadido" 120 >/dev/null &&
    wait_log_since "$guest" 0 "Menú Multijugador añadido" 120 >/dev/null || { echo "par $n: el menú no cargó" >&2; return 1; }
  sleep 3

  if [ -n "$HK_LAG" ]; then
    send "$host" "netsim $HK_LAG"; send "$guest" "netsim $HK_LAG"
  fi

  # El host abre el mundo; si es nuevo empieza la partida desde la cinemática, que se salta
  send "$host" "hostworld $HK_ROUTE_WORLD Host $port $HK_ROUTE_CODE"
  local deadline=$((SECONDS + 150))
  until log_since "$host" 0 | grep -aq "Progreso compartido activo"; do
    [ $SECONDS -ge $deadline ] && { echo "par $n: el host no entró en su mundo" >&2; return 1; }
    send "$host" "skip"; sleep 3
  done
  sleep 4

  # El progreso se activa ya durante la cinemática del principio: hay que esperar a una sala jugable
  wait_playable "$host" 120 || { echo "par $n: el host no llegó a una sala jugable" >&2; return 1; }

  local since; since=$(log_lines "$guest")
  send "$guest" "join 127.0.0.1 $port $HK_ROUTE_CODE Invitado"
  wait_log_since "$guest" "$since" "Progreso compartido activo" 120 >/dev/null || { echo "par $n: el invitado no entró en el mundo" >&2; return 1; }
  wait_playable "$guest" 60 || { echo "par $n: el invitado no llegó a una sala jugable" >&2; return 1; }
  sleep 4
  return 0
}

# Salas que no son de juego (menús, cinemáticas)
HK_NOT_PLAYABLE='^(|Pre_Menu_Intro|Menu_Title|Quit_To_Menu|BetaEnd|Knight_Pickup|Opening_Sequence|Cinematic_.*|End_.*)$'

# Espera a que la copia esté jugando en una sala normal; mientras tanto salta cinemáticas
wait_playable() {
  local inst="$1" deadline=$((SECONDS + ${2:-60})) scene
  while [ $SECONDS -lt $deadline ]; do
    scene="$(status_field "$(status_of "$inst")" scene)"
    if [ -n "$scene" ] && ! [[ "$scene" =~ $HK_NOT_PLAYABLE ]]; then
      return 0
    fi

    send "$inst" "skip"; sleep 3
  done

  return 1
}

# Si el par sigue bien: las dos copias abiertas y la sesión activa en las dos
pair_healthy() {
  local host="${HK_PAIR_HOSTS[$1]}" guest="${HK_PAIR_GUESTS[$1]}" hs gs
  instance_running "$host" && instance_running "$guest" || return 1
  hs="$(status_of "$host")" && gs="$(status_of "$guest")" || return 1
  [ "$(status_field "$hs" activa)" = "True" ] && [ "$(status_field "$gs" activa)" = "True" ]
}

stop_pair() { stop_instance "${HK_PAIR_HOSTS[$1]}"; stop_instance "${HK_PAIR_GUESTS[$1]}"; }

# Reserva el par <n> para este proceso (mkdir es atómico). Una reserva de un proceso que ya no existe se recupera.
lock_pair() {
  local lock="$HK_INSTANCES_DIR/.locks/pair$1"
  mkdir -p "$(dirname "$lock")"
  if mkdir "$lock" 2>/dev/null; then
    echo $$ > "$lock/pid"; return 0
  fi

  local owner; owner="$(cat "$lock/pid" 2>/dev/null)"
  if [ -n "$owner" ] && ! kill -0 "$owner" 2>/dev/null; then
    echo $$ > "$lock/pid"; return 0
  fi

  return 1
}

unlock_pair() {
  local lock="$HK_INSTANCES_DIR/.locks/pair$1"
  [ "$(cat "$lock/pid" 2>/dev/null)" = "$$" ] && rm -rf "$lock"
}
