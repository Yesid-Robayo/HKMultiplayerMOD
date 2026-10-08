#!/usr/bin/env bash
# Utilidades para probar el coop con dos instancias de prueba (B = host, C = cliente) en este mismo PC.
# Uso: source tools/coop-test-lib.sh; launch B; send B "status"; wait_log B "patrón" 60; tlog B
# Las instancias son copias con hard links (test-instances/b, c, d) con otro nombre de producto, así que cada una
# tiene su propia carpeta de datos en LocalLow y no tocan las partidas del juego original.

HK_TEST_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HK_LOCALLOW="/c/Users/$USERNAME/AppData/LocalLow/Team Cherry"

_inst_dir() { echo "$HK_TEST_ROOT/test-instances/$(echo "$1" | tr 'A-Z' 'a-z')"; }
_data_dir() { echo "$HK_LOCALLOW/HollowKnight$1"; }

# Lanza la instancia en ventana, en modo de prueba
launch() {
  local dir; dir="$(_inst_dir "$1")"
  local x=${2:-0}
  (cd "$dir" && ./hollow_knight.exe -screen-fullscreen 0 -screen-width 960 -screen-height 540 \
     -cooptest >/dev/null 2>&1 &)
  echo "lanzada $1"
}

# Añade una orden al archivo que lee CoopTestDriver
send() { printf '%s\n' "$2" >> "$(_data_dir "$1")/coop-test.txt"; }

# Espera hasta que aparezca un patrón (regex extendida) en el ModLog de la instancia
wait_log() {
  local log; log="$(_data_dir "$1")/ModLog.txt"
  local deadline=$((SECONDS + ${3:-60}))
  while [ $SECONDS -lt $deadline ]; do
    if grep -qE "$2" "$log" 2>/dev/null; then return 0; fi
    sleep 1
  done
  echo "TIMEOUT esperando '$2' en $1" >&2
  return 1
}

# Líneas relevantes del ModLog: pruebas, CoopRevive, errores y avisos
tlog() {
  grep -E "\[TEST\]|CoopRevive|ERROR|WARN|Exception" "$(_data_dir "$1")/ModLog.txt" | grep -v "SkinLoader" | tail -n "${2:-30}"
}

# Sesión lista para probar: B hostea con su partida 1, C se conecta y ambos quedan en Dirtmouth (zona normal)
setup_session() {
  stop_all; sleep 2
  launch B; sleep 8; launch C
  wait_log B "modo de prueba activo" 60 && wait_log C "modo de prueba activo" 60 || return 1
  sleep 8
  send B "host HostB 26950"; sleep 4; send B "loadslot 1"
  wait_log B "Received EnterScene data from \(0" 90 || return 1
  sleep 3
  send C "connect 127.0.0.1 26950 ClientC"
  wait_log C "Received server info" 60 || return 1
  sleep 20
  send B "goscene Town left1"; sleep 12
  send C "goscene Town - 6.0 44.41"; sleep 12
  send B "status"; send C "status"; sleep 2
}

# Sesión en King's Pass (enemigos, piedras de geo, rompibles): B entra primero y queda como host de la sala
setup_kings_pass() {
  setup_session || return 1
  send B "goscene Tutorial_01 right1"; sleep 14
  send C "goscene Tutorial_01 right1"; sleep 14
}

# Sesión con partida NUEVA (mundo intacto: piedras, paredes, jefes): B hostea en su espacio 2 recién creado,
# espera a que termine la cinemática inicial y C se conecta; ambos quedan en King's Pass
setup_new_game() {
  stop_all; sleep 2
  rm -f "$(_data_dir B)"/user2*
  launch B; sleep 8; launch C
  wait_log B "modo de prueba activo" 60 && wait_log C "modo de prueba activo" 60 || return 1
  sleep 8
  send B "host HostB 26950"; sleep 4; send B "newgame 2"
  local s=""
  for _ in $(seq 1 40); do
    send B "status"; sleep 3
    s=$(grep -h "\[TEST\] status" "$(_data_dir B)/ModLog.txt" | tail -1 | grep -oE "scene=\S+")
    [ "$s" = "scene=Tutorial_01" ] && break
  done
  sleep 6
  send C "connect 127.0.0.1 26950 ClientC"
  wait_log C "Configuración del host recibida" 90 || return 1
  sleep 6
}

# Partida nueva y los dos en la sala del False Knight (Crossroads_10), B primero (host de la sala)
setup_false_knight() {
  setup_new_game || return 1
  send B "goscene Crossroads_10 left1"; sleep 14
  send C "goscene Crossroads_10 left1"; sleep 14
}

# Cierra todas las instancias de prueba (nunca toca el juego original)
stop_all() {
  powershell.exe -NoProfile -Command "Get-Process hollow_knight -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*HKMultiplayerMOD\\test-instances\\*' } | Stop-Process -Force" 2>/dev/null
}
