#!/usr/bin/env bash
# Vuelca a texto las FSM de todas las salas del juego en decomp/fsm/<sala>.txt (orden fsmdumpall del modo de prueba).
# Las FSM no están en el código descompilado: van dentro de las escenas. Con esto se pueden revisar y buscar sin abrir
# el juego (jefes, personajes, plataformas...).
#
# Uso: bash tools/dump-fsms.sh [--workers N] [--only "Sala1 Sala2"] [--retry]
#   --workers N  copias del juego a la vez (1-4, por defecto 2; ~1,5 GB de RAM cada una)
#   --only       solo esas salas
#   --retry      vuelve a intentar las salas que fallaron (decomp/fsm/_fallidas.txt)
# Sigue donde lo dejó: las salas ya volcadas se saltan (borra decomp/fsm para empezar de cero).
#
# Usa sus propias copias (test-instances/l, m, n, o), nunca las de las rutas ni b/c. Cada copia empieza una partida
# nueva en su ranura 1, se hace invulnerable (god) y viaja a cada sala sin puerta (warp), como "Ir con ...".
set -uo pipefail

source "$(dirname "$0")/routes/lib.sh"

OUT="$HK_TEST_ROOT/decomp/fsm"
WORKERS=2
ONLY=""
RETRY=false
while [ $# -gt 0 ]; do
  case "$1" in
    --workers) WORKERS="$2"; shift ;;
    --only) ONLY="$2"; shift ;;
    --retry) RETRY=true ;;
  esac
  shift
done

LETTERS=(L M N O)
[ "$WORKERS" -ge 1 ] && [ "$WORKERS" -le 4 ] || { echo "--workers entre 1 y 4" >&2; exit 1; }
mkdir -p "$OUT"
OUT_WIN="$(cygpath -w "$OUT")"
FAILED="$OUT/_fallidas.txt"

# Salas que no son de juego: menús y vídeos (no tienen nada que volcar o no se puede entrar a ellas)
SKIP='^(Menu_Title|Pre_Menu_Intro|Quit_To_Menu|BetaEnd|Knight_Pickup|Opening_Sequence|PermaDeath.*|Cinematic_.*|End_.*|Menu_Credits)$'

# Abre la copia, empieza una partida nueva y espera a una sala jugable
start_worker() {
  local inst="$1"
  ensure_instance "$inst" || return 1
  stop_instance "$inst"; sleep 1
  launch_instance "$inst"
  wait_log_since "$inst" 0 "Menú Multijugador añadido" 120 >/dev/null || { echo "$inst: el menú no cargó" >&2; return 1; }
  sleep 3
  send "$inst" "newgame 1"
  sleep 8
  wait_playable "$inst" 180 || { echo "$inst: no llegó a una sala jugable" >&2; return 1; }
  send "$inst" "god"
  # Cada sala se vuelca al activarse: las de sueño sacan al caballero nada más cargar si no se entró soñando
  send "$inst" "fsmdumpauto $OUT_WIN"
  sleep 2
}

# Vuelca una sala; 0 si quedó escrita. El volcado automático (fsmdumpauto) la escribe al activarse; si no salió (ya
# estaba en ella), se pide a mano
dump_scene() {
  local inst="$1" scene="$2" since current deadline
  since=$(log_lines "$inst")
  send "$inst" "warp $scene 0 0"
  deadline=$((SECONDS + 45))
  while [ $SECONDS -lt $deadline ]; do
    [ -s "$OUT/$scene.txt" ] && return 0
    current="$(status_field "$(status_of "$inst")" scene)"
    [ "$current" = "$scene" ] && break
    # Si no se puede viajar ahora (cambio de sala en curso), se vuelve a pedir
    if log_since "$inst" "$since" | grep -aq "ahora no se puede viajar"; then
      sleep 2; since=$(log_lines "$inst"); send "$inst" "warp $scene 0 0"
    fi
    sleep 1
  done
  [ -s "$OUT/$scene.txt" ] && return 0
  [ "$current" = "$scene" ] || return 1

  # Que arranquen sus FSM y se carguen las salas que van con ella (las _boss)
  sleep 3
  [ -s "$OUT/$scene.txt" ] && return 0
  since=$(log_lines "$inst")
  send "$inst" "fsmdumpall $OUT_WIN"
  wait_log_since "$inst" "$since" "fsmdumpall $scene:|'fsmdumpall .*' falló" 90 >/dev/null || return 1
  [ -s "$OUT/$scene.txt" ]
}

# Recorre su parte de la lista; si el juego se cae o se traba, lo vuelve a abrir
run_worker() {
  local inst="$1"; shift
  local scenes=("$@") scene fails=0
  start_worker "$inst" || { printf '%s\n' "${scenes[@]}" >> "$FAILED"; return 1; }
  for scene in "${scenes[@]}"; do
    [ -s "$OUT/$scene.txt" ] && continue
    # Las salas que se cargan con otra (Sala_boss) ya salen en el volcado de esa
    if [[ "$scene" =~ _(boss|boss_defeated|preload)$ ]] && grep -lq "cargadas: .*\b$scene\b" "$OUT"/*.txt 2>/dev/null; then
      continue
    fi

    if dump_scene "$inst" "$scene"; then
      echo "$inst ok $scene"
      fails=0
    else
      echo "$inst FALLO $scene"
      echo "$scene" >> "$FAILED"
      fails=$((fails + 1))
      # Tras un fallo se vuelve a empezar la copia (puede haberse quedado en una cinemática o cerrado)
      start_worker "$inst" || return 1
    fi
  done
  stop_instance "$inst"
}

# Lista de salas: de la propia copia del juego (orden scenelist)
if [ -n "$ONLY" ]; then
  read -r -a ALL <<< "$ONLY"
elif $RETRY; then
  mapfile -t ALL < <(sort -u "$FAILED" 2>/dev/null)
  rm -f "$FAILED"
else
  inst="${LETTERS[0]}"
  ensure_instance "$inst" || exit 1
  bash "$HK_TEST_ROOT/tools/test-cycle.sh" --only "$(echo "${LETTERS[@]:0:$WORKERS}" | tr 'A-Z' 'a-z')" >/dev/null
  stop_instance "$inst"; launch_instance "$inst"
  wait_log_since "$inst" 0 "Menú Multijugador añadido" 120 >/dev/null || { echo "el menú no cargó" >&2; exit 1; }
  mapfile -t ALL < <(query "$inst" "scenelist" 60 | grep -aoE "\[TEST\] scene .*" | sed -E 's/.*\[TEST\] scene //' | tr -d '\r')
  stop_instance "$inst"
fi

SCENES=()
for scene in "${ALL[@]}"; do
  [ -n "$scene" ] && ! [[ "$scene" =~ $SKIP ]] && SCENES+=("$scene")
done
echo "${#SCENES[@]} salas a volcar en $OUT con $WORKERS copias"

# Instalar el mod recién compilado en las copias que se usan (si se pidió una lista, también)
for ((w = 0; w < WORKERS; w++)); do ensure_instance "${LETTERS[$w]}" || exit 1; done
bash "$HK_TEST_ROOT/tools/test-cycle.sh" --only "$(echo "${LETTERS[@]:0:$WORKERS}" | tr 'A-Z' 'a-z')" >/dev/null

# Reparto por turnos: cada copia una de cada N salas
for ((w = 0; w < WORKERS; w++)); do
  part=()
  for ((i = w; i < ${#SCENES[@]}; i += WORKERS)); do part+=("${SCENES[$i]}"); done
  run_worker "${LETTERS[$w]}" "${part[@]}" > "$OUT/_worker-${LETTERS[$w]}.log" 2>&1 &
done
wait

total=$(ls "$OUT"/*.txt 2>/dev/null | grep -vc '/_')
echo "hecho: $total salas volcadas en $OUT"
[ -s "$FAILED" ] && echo "fallaron $(sort -u "$FAILED" | wc -l) (bash tools/dump-fsms.sh --retry): $(sort -u "$FAILED" | tr '\n' ' ')"
exit 0
