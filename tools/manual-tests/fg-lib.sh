#!/usr/bin/env bash
# Sesión F (host) + G (invitado) en el puerto 27300 con un mundo propio.
source "$(dirname "${BASH_SOURCE[0]}")/../routes/lib.sh"
S="${HK_SHOTS_DIR:-$(dirname "${BASH_SOURCE[0]}")}"
LF="$(_log_file F)"; LG="$(_log_file G)"

# setup_fg <mundo> [fresh=1]
setup_fg() {
  local world="$1" fresh="${2:-1}"
  stop_instance F; stop_instance G; sleep 2
  [ "$fresh" = 1 ] && rm -rf "$(_data_dir F)/HKMultiplayer/Mundos/$world"
  rm -f "$(_data_dir F)/mp-test.txt" "$(_data_dir G)/mp-test.txt"
  launch_instance F; sleep 4; launch_instance G
  wait_log_since F 0 "Menú Multijugador añadido" 120 >/dev/null &&
    wait_log_since G 0 "Menú Multijugador añadido" 120 >/dev/null || { echo "menú no cargó"; return 1; }
  sleep 3
  send F "hostworld $world HostF 27300 PRBA-TEST"
  local deadline=$((SECONDS + 150))
  until log_since F 0 | grep -aq "Progreso compartido activo"; do
    [ $SECONDS -ge $deadline ] && { echo "host no entró"; return 1; }
    send F "skip"; sleep 3
  done
  sleep 4
  wait_playable F 120 || { echo "host no jugable"; return 1; }
  local since; since=$(log_lines G)
  send G "join 127.0.0.1 27300 PRBA-TEST ClientG"
  wait_log_since G "$since" "Progreso compartido activo" 120 >/dev/null || { echo "invitado no entró"; return 1; }
  wait_playable G 60 || { echo "invitado no jugable"; return 1; }
  sleep 4
  echo "sesión lista"
}

both() { send F "$1"; send G "$1"; }
q() { query "$1" "$2" "${3:-20}"; }
# Vida alta en los dos
tanky() { for p in F G; do send $p "pdset maxHealth ${1:-500}"; send $p "pdset health ${1:-500}"; done; }
shots() { send F "shot $1"; send G "shot $1"; }

# Los dos entran en la sala y se espera a que estén dentro (la carga puede tardar mucho con varios juegos abiertos)
enter() {  # sala puerta
  send F "goscene $1 $2"; sleep 2; send G "goscene $1 $2"
  local deadline=$((SECONDS + 120))
  while [ $SECONDS -lt $deadline ]; do
    sleep 3
    [ "$(status_field "$(status_of F)" scene)" = "$1" ] && [ "$(status_field "$(status_of G)" scene)" = "$1" ] && break
  done
  sleep 6
  echo "en $1: F=$(status_field "$(status_of F)" scene) G=$(status_field "$(status_of G)" scene) visibles=$(status_field "$(status_of F)" visibles)"
  tanky 500
}
damage_since() { log_since "$1" "$2" | grep -aoE '\[TEST\] daño [0-9]+ de .*' | sed -E 's/\[TEST\] daño ([0-9]+) de /\1 /' | awk '{n=$1; $1=""; s[$0]+=n} END {for (k in s) printf "%s=%d; ", k, s[k]}'; }
