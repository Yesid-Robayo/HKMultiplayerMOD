#!/usr/bin/env bash
# Utilidades para probar el multijugador con dos instancias de prueba (B = host, C = cliente) en este mismo PC.
# Uso: source tools/mp-test-lib.sh; setup_session; send C "press right 3"; tlog B
# Las instancias son copias con hard links (test-instances/b, c) con otro nombre de producto, así que cada una tiene
# su propia carpeta de datos en LocalLow y no tocan las partidas del juego original.

HK_TEST_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HK_LOCALLOW="/c/Users/$USERNAME/AppData/LocalLow/Team Cherry"
HK_TEST_CODE="PRBA-TEST"

_inst_dir() { echo "$HK_TEST_ROOT/test-instances/$(echo "$1" | tr 'A-Z' 'a-z')"; }
_data_dir() { echo "$HK_LOCALLOW/HollowKnight$1"; }

# Lanza la instancia en ventana, en modo de prueba. El ModLog anterior se borra antes: si no, wait_log podría
# encontrar líneas de la ejecución pasada y enviar órdenes antes de que el juego vacíe su archivo de órdenes.
launch() {
  local dir; dir="$(_inst_dir "$1")"
  rm -f "$(_data_dir "$1")/ModLog.txt"
  (cd "$dir" && ./hollow_knight.exe -screen-fullscreen 0 -screen-width 960 -screen-height 540 \
     -mptest >/dev/null 2>&1 &)
  echo "lanzada $1"
}

# Añade una orden al archivo que lee el TestDriver del mod
send() { printf '%s\n' "$2" >> "$(_data_dir "$1")/mp-test.txt"; }

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

# Líneas relevantes del ModLog: pruebas, avisos y errores
tlog() {
  grep -E "\[TEST\]|\[HKMultiplayer\]|ERROR|WARN|Exception" "$(_data_dir "$1")/ModLog.txt" | tail -n "${2:-30}"
}

# Sesión lista para probar: B hostea y carga su partida 1, C carga la suya y se une; ambos quedan en Dirtmouth
setup_session() {
  stop_all; sleep 2
  launch B; sleep 8; launch C
  wait_log B "modo de prueba activo" 60 && wait_log C "modo de prueba activo" 60 || return 1
  sleep 6
  send B "host HostB 26950 $HK_TEST_CODE"; send B "loadslot 1"; send C "loadslot 1"
  wait_log B "Partida abierta" 30 || return 1
  sleep 15
  send C "join 127.0.0.1 26950 $HK_TEST_CODE ClientC"
  wait_log C "Conectado como jugador" 30 || return 1
  send B "goscene Town left1"; send C "goscene Town left1"; sleep 12
  send B "status"; send C "status"; sleep 2
}

# Cierra todas las instancias de prueba (nunca toca el juego original)
stop_all() {
  powershell.exe -NoProfile -Command "Get-Process hollow_knight -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*HKMultiplayerMOD\test-instances\*' } | Stop-Process -Force" 2>/dev/null
}
