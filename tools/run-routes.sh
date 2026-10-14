#!/usr/bin/env bash
# Ejecuta las rutas de prueba del coop (tests/routes/*.route) con uno o varios pares de copias del juego en paralelo.
# Cada par (host + invitado) va cogiendo la siguiente ruta libre, así que con más pares la pasada completa tarda menos.
#
# Uso:
#   bash tools/run-routes.sh                       todas las rutas con 1 par
#   bash tools/run-routes.sh --pairs 3             todas, repartidas entre 3 pares (6 juegos abiertos)
#   bash tools/run-routes.sh crossroads progress   solo esas rutas (nombre del archivo sin .route, o su ruta)
#   bash tools/run-routes.sh --lag "120 30 2"      con red mala en todos los pares (retraso ms, variación ms, pérdida %)
#   bash tools/run-routes.sh --keep                deja los juegos abiertos al terminar
#   bash tools/run-routes.sh --reuse               no empieza el mundo de cero (más rápido para repetir una ruta)
#   bash tools/run-routes.sh -v                    muestra cada comprobación según pasa
#   bash tools/run-routes.sh --players 4           con 4 jugadores: el par J/K y L y M, que entran en cada sala y
#                                                  cuentan en cada comprobación (un solo grupo; 3 = solo L)
#
# Usa las copias test-instances/d..k (las crea si faltan, con hard links: no ocupan disco). No toca b ni c.
# Si otro proceso ya está usando un par, se usa el siguiente libre.
# Resultado: out/routes/<fecha>/ (un .txt por ruta y resumen.txt). Sale con 0 si no hubo ningún FALLO.
# Para instalar una versión nueva del mod en todas las copias: bash tools/test-cycle.sh (con los juegos cerrados).

set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Jugadores (antes de cargar steps.sh, que lee los de más de HK_EXTRAS_LIST)
PLAYERS=2
for ((a = 1; a <= $#; a++)); do
  if [ "${!a}" = --players ]; then
    b=$((a + 1)); PLAYERS="${!b:-2}"
  fi
done
if ! [[ "$PLAYERS" =~ ^[2-4]$ ]]; then
  echo "--players va de 2 a 4" >&2
  exit 2
fi
EXTRA_LETTERS=(L M)
export HK_EXTRAS_LIST="${EXTRA_LETTERS[*]:0:$((PLAYERS - 2))}"

# shellcheck source=routes/steps.sh
source "$ROOT/tools/routes/steps.sh"

PAIRS=1
KEEP=0
REUSE=0
HK_LAG=""
HK_VERBOSE=""
ROUTES=()
while [ $# -gt 0 ]; do
  case "$1" in
    --pairs) PAIRS="$2"; shift ;;
    --players) shift ;;
    --lag) HK_LAG="$2"; shift ;;
    --keep) KEEP=1 ;;
    --reuse) REUSE=1 ;;
    -v) HK_VERBOSE=1 ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) ROUTES+=("$1") ;;
  esac
  shift
done
export HK_LAG HK_VERBOSE

if ! [[ "$PAIRS" =~ ^[1-4]$ ]]; then
  echo "--pairs va de 1 a 4" >&2
  exit 2
fi

# Rutas a ejecutar: las pedidas o todas; las más largas primero para repartir mejor
resolve_route() {
  if [ -f "$1" ]; then echo "$1"
  elif [ -f "$ROOT/tests/routes/$1.route" ]; then echo "$ROOT/tests/routes/$1.route"
  else echo "no existe la ruta '$1'" >&2; return 1
  fi
}

FILES=()
if [ ${#ROUTES[@]} -eq 0 ]; then
  FILES=("$ROOT"/tests/routes/*.route)
else
  for r in "${ROUTES[@]}"; do FILES+=("$(resolve_route "$r")") || exit 2; done
fi
mapfile -t FILES < <(for f in "${FILES[@]}"; do echo "$(grep -cvE '^[[:space:]]*(#|$)' "$f") $f"; done | sort -rn | cut -d' ' -f2-)

# Antes de abrir nada: salas, puertas, campos y pasos de las rutas
bash "$ROOT/tools/routes/check-routes.sh" "${FILES[@]}" || { echo "corrige las rutas antes de lanzarlas" >&2; exit 2; }

RUN="$ROOT/out/routes/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RUN/claims"
echo "rutas: ${#FILES[@]}  pares: $PAIRS  resultados: $RUN"

# Reserva de pares libres. Con más de 2 jugadores, un solo grupo: el par J/K (el 4) y los de más
TAKEN=()
if [ "$PLAYERS" -gt 2 ]; then
  lock_pair 3 && TAKEN+=(3)
else
  for ((i = 0; i < 4 && ${#TAKEN[@]} < PAIRS; i++)); do
    lock_pair "$i" && TAKEN+=("$i")
  done
fi
if [ ${#TAKEN[@]} -eq 0 ]; then
  echo "no hay pares libres (otro proceso los está usando: test-instances/.locks)" >&2
  exit 2
fi
[ ${#TAKEN[@]} -lt "$PAIRS" ] && echo "solo hay ${#TAKEN[@]} par(es) libre(s)"

cleanup() {
  for i in "${TAKEN[@]}"; do
    [ "$KEEP" = 1 ] || { stop_pair "$i"; stop_group "${HK_EXTRAS[@]}"; }
    unlock_pair "$i"
  done
}

# Prepara el par (o el grupo, con jugadores de más). <limpio> 1 empieza el mundo de cero
setup_route_group() {
  if [ ${#HK_EXTRAS[@]} -eq 0 ]; then
    setup_pair "$1" "$2"
  else
    HK_GROUP_FRESH="$2" setup_group $((HK_ROUTE_BASE_PORT + $1 * 10)) "${HK_PAIR_HOSTS[$1]}" "${HK_PAIR_GUESTS[$1]}" "${HK_EXTRAS[@]}"
  fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM

worker() {
  local pair="$1" label="par $(( $1 + 1 )) (${HK_PAIR_HOSTS[$1]}/${HK_PAIR_GUESTS[$1]})" file name verdict

  local ready=0
  if [ "$REUSE" = 1 ] && group_healthy "$pair"; then
    ready=1
  else
    echo "[$label] preparando..."
    for attempt in 1 2; do
      setup_route_group "$pair" "$([ "$REUSE" = 1 ] && echo 0 || echo 1)" && { ready=1; break; }
      echo "[$label] no se pudo preparar (intento $attempt)"
    done
  fi
  [ "$ready" = 1 ] || { echo "[$label] se rinde: no se pudo preparar el par"; return; }
  echo "[$label] listo"

  for file in "${FILES[@]}"; do
    name="$(basename "$file" .route)"
    mkdir "$RUN/claims/$name" 2>/dev/null || continue

    # Si el par se cayó en la ruta anterior, se vuelve a preparar sin borrar el mundo
    if ! group_healthy "$pair"; then
      echo "[$label] el par no responde: se reinicia"
      setup_route_group "$pair" 0 || { echo "[$label] no se pudo reiniciar"; rmdir "$RUN/claims/$name"; return; }
    fi

    echo "[$label] $name..."
    run_route "$pair" "$file" "$RUN/$name.txt"
    verdict="$(route_verdict "$RUN/$name.txt")"
    echo "[$label] $name: $verdict ($(grep -c '^OK' "$RUN/$name.txt") OK, $(grep -c '^FALLO' "$RUN/$name.txt") FALLO, $(grep -c '^NOCONC' "$RUN/$name.txt") NOCONC)"
  done
}

for i in "${TAKEN[@]}"; do
  worker "$i" &
done
wait

# Resumen
{
  echo "Rutas de prueba — $(date '+%Y-%m-%d %H:%M')  pares: ${#TAKEN[@]}  jugadores: $PLAYERS${HK_LAG:+  red: $HK_LAG}"
  echo
  printf '%-8s %-24s %5s %6s %7s\n' RESULTADO RUTA OK FALLO NOCONC
  for file in "${FILES[@]}"; do
    name="$(basename "$file" .route)"
    if [ -f "$RUN/$name.txt" ]; then
      printf '%-8s %-24s %5s %6s %7s\n' "$(route_verdict "$RUN/$name.txt")" "$name" \
        "$(grep -c '^OK' "$RUN/$name.txt")" "$(grep -c '^FALLO' "$RUN/$name.txt")" "$(grep -c '^NOCONC' "$RUN/$name.txt")"
    else
      printf '%-8s %-24s\n' "SIN-EJEC" "$name"
    fi
  done
  echo
  echo "== FALLOS"
  for file in "${FILES[@]}"; do
    name="$(basename "$file" .route)"
    [ -f "$RUN/$name.txt" ] && grep '^FALLO' "$RUN/$name.txt" | sed "s|^|$name: |"
  done
  echo
  echo "== NO CONCLUYENTES"
  for file in "${FILES[@]}"; do
    name="$(basename "$file" .route)"
    [ -f "$RUN/$name.txt" ] && grep '^NOCONC' "$RUN/$name.txt" | sed "s|^|$name: |"
  done
} > "$RUN/resumen.txt"

echo
cat "$RUN/resumen.txt"

failed=0
for file in "${FILES[@]}"; do
  name="$(basename "$file" .route)"
  if [ ! -f "$RUN/$name.txt" ] || [ "$(route_verdict "$RUN/$name.txt")" = FALLO ]; then
    failed=1
  fi
done
exit $failed
