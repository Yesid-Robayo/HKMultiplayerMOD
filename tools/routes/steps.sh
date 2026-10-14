#!/usr/bin/env bash
# Intérprete de las rutas (tests/routes/*.route). Cada línea es un paso; el formato está en tests/routes/README.md.
# Cada comprobación escribe una línea en el resultado de la ruta:
#   OK          funciona
#   FALLO       el mod no sincronizó algo (o hubo una excepción): hay que arreglarlo
#   NOCONC      no se pudo comprobar (no hay rompibles en la sala, el jefe no murió, no se llegó a la sala...)

# shellcheck source=lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Prefijos de objetos de sala que cada jugador recoge por su cuenta (CharacterData.IndividualItemPrefixes): no se comparan
HK_INDIVIDUAL_PREFIXES='^(Shiny|Heart Piece|Vessel Fragment|Dream Plant|Egg Sac|Reward|Reminder|Interact Reminder|Death Respawn Trigger|Initial Fall Impact|Force Hard Landing|Secret Sound Region|Poo )'

# Estado de la ruta en curso
_R_HOST=""; _R_GUEST=""; _R_OUT=""; _R_LINE=0; _R_ROOM=""; _R_ROOM_OK=1
_R_START_HOST=0; _R_START_GUEST=0

# Jugadores de más (3 o 4 jugadores, run-routes.sh --players): copias que se unen a la partida del par, entran en cada
# sala con los dos y cuentan en cada comprobación (el mismo progreso, los mismos enemigos vivos, sin errores...)
read -r -a HK_EXTRAS <<< "${HK_EXTRAS_LIST:-}"
declare -A _R_START_EXTRA=()

# Todas las copias de la ruta: host, invitado y los de más
_all() { echo "$_R_HOST" "$_R_GUEST" "${HK_EXTRAS[@]}"; }

# Todas menos esa
_all_but() { local inst; for inst in $(_all); do [ "$inst" != "$1" ] && echo "$inst"; done; return 0; }

_result() {
  # _result <OK|FALLO|NOCONC> <paso> <detalle>
  printf '%-6s | %4s | %s | %s\n' "$1" "$_R_LINE" "$2" "$3" >> "$_R_OUT"
  [ -n "$HK_VERBOSE" ] && printf '  %-6s %s — %s\n' "$1" "$2" "$3"
  return 0
}

_who() {
  case "$1" in
    host) echo "$_R_HOST" ;;
    guest) echo "$_R_GUEST" ;;
    *) return 1 ;;
  esac
}

_other() { [ "$1" = host ] && echo guest || echo host; }

# Espera hasta <segundos> a que el campo valga <valor> en esa copia. Imprime el último valor leído.
_wait_pd() {
  local inst="$1" field="$2" want="$3" timeout="$4" deadline value
  deadline=$((SECONDS + timeout))
  while :; do
    value="$(pd_value "$inst" "$field")"
    [ "$value" = "$want" ] && { echo "$value"; return 0; }
    [ $SECONDS -ge $deadline ] && { echo "$value"; return 1; }
    sleep 1
  done
}

# =====================================================================================================
# Pasos
# =====================================================================================================

step_room() {
  local scene="$1" gate="$2" deadline hs gs
  _R_ROOM="$scene"; _R_ROOM_OK=0
  local inst
  for inst in $(_all); do send "$inst" "goscene $scene $gate"; done
  deadline=$((SECONDS + 50))
  while [ $SECONDS -lt $deadline ]; do
    sleep 3
    hs="$(status_of "$_R_HOST")"; gs="$(status_of "$_R_GUEST")"
    if [ "$(status_field "$hs" scene)" = "$scene" ] && [ "$(status_field "$gs" scene)" = "$scene" ] && _extras_in "$scene"; then
      _R_ROOM_OK=1
      break
    fi
  done

  if [ "$_R_ROOM_OK" != 1 ]; then
    _result NOCONC "room $scene $gate" "no llegaron los dos (host en '$(status_field "$hs" scene)', invitado en '$(status_field "$gs" scene)'); ¿puerta incorrecta? Se saltan los pasos de esta sala"
    return
  fi

  # Mucha vida: la ruta deja a los caballeros quietos entre enemigos y uno nuevo tiene 5 máscaras. Si muere, reaparece
  # en su banco y lo que se comprueba después ya no es de esta sala (sus enemigos parecen no haber muerto en el otro)
  for inst in $(_all); do send "$inst" "pdset maxHealth $HK_ROUTE_HEALTH"; send "$inst" "pdset health $HK_ROUTE_HEALTH"; done

  # Tiempo para que el dueño de la sala reparta su estado y aparezcan los avatares. Cada uno ve a todos los demás
  sleep 5
  local others=$(( ${#HK_EXTRAS[@]} + 1 )) seen="" bad=0 v
  for inst in $(_all); do
    v="$(status_field "$(status_of "$inst")" visibles)"
    seen="$seen $inst=$v"
    [ "$v" -ge "$others" ] 2>/dev/null || bad=1
  done
  if [ $bad = 0 ]; then
    _result OK "room $scene" "todos en la sala y se ven ($others cada uno)"
  else
    _result FALLO "room $scene" "en la misma sala pero no se ven todos (visibles:$seen; deberían ser $others)"
  fi
}

# Los de más están en esa sala (sin extras, siempre)
_extras_in() {
  local inst
  for inst in "${HK_EXTRAS[@]}"; do
    [ "$(status_field "$(status_of "$inst")" scene)" = "$1" ] || return 1
  done
  return 0
}

step_shared() {
  local from="$1" field="$2" value; value="$(echo "$3" | tr 'A-Z' 'a-z')"
  local src dst got
  src="$(_who "$from")"; dst="$(_who "$(_other "$from")")"
  send "$src" "pdset $field $3"
  got="$(_wait_pd "$src" "$field" "$value" 5)" || {
    _result NOCONC "shared $from $field $3" "no se pudo poner en el que lo cambia (vale '$got'; ¿existe el campo?)"
    return
  }
  local bad=""
  for dst in $(_all_but "$src"); do
    got="$(_wait_pd "$dst" "$field" "$value" 8)" || bad="$bad $dst='$got'"
  done
  if [ -z "$bad" ]; then
    _result OK "shared $from $field $3" "llegó a los demás"
  else
    _result FALLO "shared $from $field $3" "siguen con otro valor:$bad"
  fi
}

step_private() {
  local from="$1" field="$2" value; value="$(echo "$3" | tr 'A-Z' 'a-z')"
  local src dst before got
  src="$(_who "$from")"; dst="$(_who "$(_other "$from")")"
  before="$(pd_value "$dst" "$field")"
  if [ "$before" = "$value" ]; then
    _result NOCONC "private $from $field $3" "el otro ya tenía ese valor: usa otro"
    return
  fi

  send "$src" "pdset $field $3"
  got="$(_wait_pd "$src" "$field" "$value" 5)" || {
    _result NOCONC "private $from $field $3" "no se pudo poner en el que lo cambia (vale '$got')"
    return
  }
  sleep 4
  got="$(pd_value "$dst" "$field")"
  local leaked="" inst
  for inst in "${HK_EXTRAS[@]}"; do
    [ "$(pd_value "$inst" "$field")" = "$value" ] && leaked="$leaked $inst"
  done
  if [ "$got" = "$before" ] && [ -z "$leaked" ]; then
    _result OK "private $from $field $3" "no pasó a los demás (sigue '$before')"
  else
    _result FALLO "private $from $field $3" "es del personaje pero pasó a otro ('$before' → '$got'${leaked:+; también a$leaked})"
  fi
}

step_same_pd() {
  local field="$1" h v inst all="" bad=0
  h="$(pd_value "$_R_HOST" "$field")"
  for inst in $(_all); do
    v="$(pd_value "$inst" "$field")"
    all="$all $inst='$v'"
    [ "$v" = "$h" ] || bad=1
  done
  if [ $bad = 0 ]; then
    _result OK "same-pd $field" "todos '$h'"
  else
    _result FALLO "same-pd $field" "distinto:$all"
  fi
}

step_expect_pd() {
  local who="$1" field="$2" want; want="$(echo "$3" | tr 'A-Z' 'a-z')"
  local got
  if got="$(_wait_pd "$(_who "$who")" "$field" "$want" "${4:-8}")"; then
    _result OK "expect-pd $who $field $3" "vale '$got'"
  else
    _result FALLO "expect-pd $who $field $3" "vale '$got'"
  fi
}

step_partner_moves() {
  # Hacia dónde camina (right por defecto). Si se entró por una puerta de la derecha, a la derecha saldría de la sala
  local dir="${1:-right}" back before after
  [ "$dir" = left ] && back=right || back=left
  before="$(status_of "$_R_HOST" | grep -aoE 'compañero=\([-0-9.]+' | grep -oE '[-0-9.]+$')"
  if [ -z "$before" ]; then
    _result FALLO "partner-moves" "el host no ve al invitado en la sala"
    return
  fi

  send "$_R_GUEST" "press $dir 1.2"; sleep 2.5
  after="$(status_of "$_R_HOST" | grep -aoE 'compañero=\([-0-9.]+' | grep -oE '[-0-9.]+$')"
  # Vuelve a donde estaba (para no acabar dentro de la siguiente puerta)
  send "$_R_GUEST" "press $back 1.2"; sleep 1.5
  if [ -n "$after" ] && awk -v a="$before" -v b="$after" 'BEGIN { d = a - b; exit !(d > 0.5 || d < -0.5) }'; then
    _result OK "partner-moves" "el host lo ve moverse (x $before → $after)"
  else
    _result FALLO "partner-moves" "el invitado caminó pero el host lo ve quieto (x $before → ${after:-?})"
  fi
}

step_breakables_sync() {
  local amount="${1:-3}" broke hb gb
  broke="$(query "$_R_GUEST" "breaknear $amount" | grep -aoE '\[TEST\] rotos [0-9]+' | grep -oE '[0-9]+$')"
  if [ "${broke:-0}" -eq 0 ]; then
    _result NOCONC "breakables-sync" "no hay rompibles enteros en la sala"
    return
  fi

  sleep 3
  local inst v all="" bad=0
  hb="$(query "$_R_HOST" "breakables" | grep -aoE '[0-9]+ rotos' | grep -oE '^[0-9]+')"
  for inst in $(_all); do
    v="$(query "$inst" "breakables" | grep -aoE '[0-9]+ rotos' | grep -oE '^[0-9]+')"
    all="$all $inst=${v:-?}"
    { [ -n "$v" ] && [ "$v" = "$hb" ]; } || bad=1
  done
  if [ $bad = 0 ]; then
    _result OK "breakables-sync" "el invitado rompió $broke; rotos en todos: $hb"
  else
    _result FALLO "breakables-sync" "el invitado rompió $broke; rotos:$all"
  fi
}

# Ids de enemigos vivos y activos (de la orden "enemies"), ordenados
_alive_enemies() {
  query "$1" "enemies" | grep -a '\[TEST\] enemigo ' | grep -a ' hp=' | grep -av ' MUERTO' | grep -av '(inactivo)' \
    | sed -E 's/.*\[TEST\] enemigo (.*) id=([0-9a-f]+).*/\2 \1/' | sort
}

# Todos siguen en la sala de la ruta (si uno murió, reapareció en otra)
_both_in_room() {
  [ "$(status_field "$(status_of "$_R_HOST")" scene)" = "$_R_ROOM" ] &&
    [ "$(status_field "$(status_of "$_R_GUEST")" scene)" = "$_R_ROOM" ] && _extras_in "$_R_ROOM"
}

step_enemies_sync() {
  local who="${1:-guest}" hit h g
  if ! _both_in_room; then
    _result NOCONC "enemies-sync" "uno de los dos ya no está en $_R_ROOM (¿murió?)"
    return
  fi

  hit="$(query "$(_who "$who")" "hitall 9999" | grep -aoE 'golpeados [0-9]+' | grep -oE '[0-9]+$')"
  if [ "${hit:-0}" -eq 0 ]; then
    _result NOCONC "enemies-sync" "no hay enemigos vivos en la sala"
    return
  fi

  sleep 2; send "$(_who "$who")" "hitall 9999"; sleep 5
  if ! _both_in_room; then
    _result NOCONC "enemies-sync" "$who golpeó $hit, pero uno de los dos ya no está en $_R_ROOM (¿murió?)"
    return
  fi

  h="$(_alive_enemies "$_R_HOST")"
  local inst bad=""
  for inst in $(_all_but "$_R_HOST"); do
    g="$(_alive_enemies "$inst")"
    [ "$h" = "$g" ] || bad="$bad $inst: solo vivos en el host [$(comm -23 <(echo "$h") <(echo "$g") | cut -d' ' -f2- | head -5 | paste -sd,)] solo en $inst [$(comm -13 <(echo "$h") <(echo "$g") | cut -d' ' -f2- | head -5 | paste -sd,)];"
  done
  if [ -z "$bad" ]; then
    _result OK "enemies-sync" "$who golpeó $hit; vivos en todos: $(echo "$h" | grep -c .)"
  else
    _result FALLO "enemies-sync" "$who golpeó $hit;$bad"
  fi
}

step_objects_sync() {
  local who="${1:-guest}" src dst id deadline
  src="$(_who "$who")"; dst="$(_who "$(_other "$who")")"
  # Uno que siga en la sala: lo ya destruido (una batalla terminada, un suelo roto) no se puede activar
  id="$(query "$src" "objs" | grep -a '\[TEST\] obj ' | grep -a '=no→' | grep -av '(destruido)' | head -n1 | sed -E 's/.*\[TEST\] obj (.*)=no→.*/\1/')"
  if [ -z "$id" ]; then
    _result NOCONC "objects-sync" "no hay objetos sin activar en la sala"
    return
  fi

  send "$src" "activate $id"
  deadline=$((SECONDS + 10))
  while [ $SECONDS -lt $deadline ]; do
    sleep 2
    local seen
    seen="$(query "$dst" "objs" | grep -aF "obj $id=")"
    if echo "$seen" | grep -aqF "obj $id=sí" && _extras_see "$id"; then
      _result OK "objects-sync" "$who activó '$id' y los demás lo ven activado"
      return
    fi

    # Lo que se rompe y se destruye a sí mismo (suelos que se rompen con la caída del hechizo): en el otro ya no existe
    if echo "$seen" | grep -aqF "(destruido)"; then
      _result OK "objects-sync" "$who activó '$id' y en el otro se rompió (destruido)"
      return
    fi
  done

  _result FALLO "objects-sync" "$who activó '$id' pero el otro no lo tiene activado"
}

# Los de más ven ese objeto activado (o ya destruido)
_extras_see() {
  local inst seen
  for inst in "${HK_EXTRAS[@]}"; do
    seen="$(query "$inst" "objs" | grep -aF "obj $1=")"
    echo "$seen" | grep -aqE "=sí|\(destruido\)" || return 1
  done
  return 0
}

step_rock_sync() {
  local line id hits deadline
  line="$(query "$_R_GUEST" "extras" | grep -aE '\[TEST\] extra piedra .* golpes=[1-9]' | head -n1)"
  if [ -z "$line" ]; then
    _result NOCONC "rock-sync" "no hay piedras de geo sin romper"
    return
  fi

  id="$(echo "$line" | sed -E 's/.*\[TEST\] extra piedra (.*) golpes=.*/\1/')"
  hits="$(echo "$line" | grep -oE 'golpes=[0-9]+' | cut -d= -f2)"
  send "$_R_GUEST" "hitrock $id"
  deadline=$((SECONDS + 10))
  while [ $SECONDS -lt $deadline ]; do
    sleep 2
    if query "$_R_HOST" "extras" | grep -aF "piedra $id golpes=$((hits - 1)) " >/dev/null; then
      _result OK "rock-sync" "golpe a '$id' visto en el host ($hits → $((hits - 1)))"
      return
    fi
  done

  _result FALLO "rock-sync" "el invitado golpeó '$id' ($hits → $((hits - 1))) y el host no lo ve"
}

# Lo guardado de la sala (objetos activados y contadores), sin lo que es de cada jugador
_scene_data() {
  query "$1" "sdlist $2" | grep -aE '\[TEST\] sd (bool .*=True|int )' | sed -E 's/.*\[TEST\] sd (bool|int) //' \
    | grep -avE "$HK_INDIVIDUAL_PREFIXES" | sort
}

step_scene_data_sync() {
  local scene="${1:-$_R_ROOM}" h g inst bad=""
  for inst in $(_all); do send "$inst" "save"; done
  sleep 4
  h="$(_scene_data "$_R_HOST" "$scene")"
  for inst in $(_all_but "$_R_HOST"); do
    g="$(_scene_data "$inst" "$scene")"
    [ "$h" = "$g" ] || bad="$bad $inst: solo en el host [$(comm -23 <(echo "$h") <(echo "$g") | head -5 | paste -sd,)] solo en $inst [$(comm -13 <(echo "$h") <(echo "$g") | head -5 | paste -sd,)];"
  done
  if [ -z "$bad" ]; then
    _result OK "scene-data-sync $scene" "$(echo "$h" | grep -c .) objetos guardados iguales"
  else
    _result FALLO "scene-data-sync $scene" "$bad"
  fi
}

step_boss() {
  local flag="$1" who="${2:-guest}" tries="${3:-4}" i h g
  query "$(_who "$who")" "bosses" >/dev/null
  for ((i = 0; i < tries; i++)); do
    send "$(_who "$who")" "hitall 9999"; sleep 5
    [ "$(pd_value "$(_who "$who")" "$flag")" = true ] || [ "$(pd_value "$(_who "$(_other "$who")")" "$flag")" = true ] && break
  done

  sleep 4
  h="$(_wait_pd "$_R_HOST" "$flag" true 8)"; g="$(_wait_pd "$_R_GUEST" "$flag" true 8)"
  local inst missing=""
  for inst in "${HK_EXTRAS[@]}"; do
    [ "$(_wait_pd "$inst" "$flag" true 8)" = true ] || missing="$missing $inst"
  done
  if [ "$h" = true ] && [ "$g" = true ] && [ -z "$missing" ]; then
    _result OK "boss $flag" "derrotado en todos"
  elif [ "$h" = true ] && [ "$g" = true ]; then
    _result FALLO "boss $flag" "derrotado en el host y el invitado, pero no en:$missing"
  elif [ "$h" != true ] && [ "$g" != true ]; then
    _result NOCONC "boss $flag" "el jefe no murió con hitall (puede que la pelea no haya empezado)"
  else
    _result FALLO "boss $flag" "derrotado solo en uno (host '$h', invitado '$g')"
  fi
}

step_expect_log() {
  local who="$1" regex="$2" timeout="${3:-15}" inst start
  inst="$(_who "$who")"
  [ "$who" = host ] && start=$_R_START_HOST || start=$_R_START_GUEST
  if wait_log_since "$inst" "$start" "$regex" "$timeout" >/dev/null; then
    _result OK "expect-log $who $regex" "apareció"
  else
    _result FALLO "expect-log $who $regex" "no apareció en ${timeout}s"
  fi
}

step_rejoin() {
  local since port
  port=$((HK_ROUTE_BASE_PORT + _R_PAIR * 10))
  since=$(log_lines "$_R_HOST")
  send "$_R_GUEST" "leavegame"
  if ! wait_log_since "$_R_HOST" "$since" "Invitado salió de la partida|Personaje de Invitado guardado" 40 >/dev/null; then
    _result FALLO "rejoin" "el invitado salió pero el host no se enteró"
  fi

  sleep 12
  since=$(log_lines "$_R_GUEST")
  send "$_R_GUEST" "join 127.0.0.1 $port $HK_ROUTE_CODE Invitado"
  if wait_log_since "$_R_GUEST" "$since" "Progreso compartido activo" 90 >/dev/null; then
    sleep 4
    _result OK "rejoin" "el invitado volvió a entrar en el mundo"
  else
    _result FALLO "rejoin" "el invitado no pudo volver a entrar en el mundo"
  fi
}

step_die() {
  local who="${1:-guest}" s inst
  # "extras": mueren los jugadores de más (con 2 jugadores no hace nada). Para que caigan todos, antes del último
  if [ "$who" = extras ]; then
    for inst in "${HK_EXTRAS[@]}"; do send "$inst" "die"; done
    [ ${#HK_EXTRAS[@]} -gt 0 ] && { sleep 15; _result OK "die extras" "murieron ${HK_EXTRAS[*]}"; }
    return
  fi
  send "$(_who "$who")" "die"; sleep 15
  s="$(status_of "$(_who "$who")")"
  if [ "$(status_field "$s" activa)" = True ]; then
    _result OK "die $who" "murió y sigue en la partida (en $(status_field "$s" scene))"
  else
    _result FALLO "die $who" "después de morir la sesión no está activa: $s"
  fi
}

# =====================================================================================================
# Ruta
# =====================================================================================================

# Errores del log de una copia desde el principio de la ruta
_new_errors() {
  log_since "$1" "$2" | grep -aE '\[ERROR\]|Exception' | grep -avE "$HK_IGNORE_ERRORS" | head -n 3
}

# run_route <par> <archivo .route> <archivo de resultado>
run_route() {
  _R_PAIR="$1"; _R_HOST="${HK_PAIR_HOSTS[$1]}"; _R_GUEST="${HK_PAIR_GUESTS[$1]}"; _R_OUT="$3"
  _R_LINE=0; _R_ROOM=""; _R_ROOM_OK=1
  _R_START_HOST=$(log_lines "$_R_HOST"); _R_START_GUEST=$(log_lines "$_R_GUEST")
  local _x
  for _x in "${HK_EXTRAS[@]}"; do _R_START_EXTRA[$_x]=$(log_lines "$_x"); done
  : > "$_R_OUT"

  local raw line keyword
  local -a args
  while IFS= read -r raw || [ -n "$raw" ]; do
    _R_LINE=$((_R_LINE + 1))
    line="$(echo "${raw%%#*}" | tr -d '\r' | sed -E 's/^[[:space:]]+//; s/[[:space:]]+$//')"
    [ -z "$line" ] && continue
    read -r -a args <<< "$line"
    keyword="${args[0]}"

    # Si no se llegó a la sala, se salta todo hasta la siguiente
    if [ "$_R_ROOM_OK" != 1 ] && [ "$keyword" != room ] && [ "$keyword" != title ]; then
      continue
    fi

    case "$keyword" in
      title) ;;
      room) step_room "${args[1]:-}" "${args[2]:-}" ;;
      host) send "$_R_HOST" "${line#host }" ;;
      guest) send "$_R_GUEST" "${line#guest }" ;;
      both) for _inst in $(_all); do send "$_inst" "${line#both }"; done ;;
      wait) sleep "${args[1]:-}" ;;
      shared) step_shared "${args[1]:-}" "${args[2]:-}" "${args[3]:-}" ;;
      private) step_private "${args[1]:-}" "${args[2]:-}" "${args[3]:-}" ;;
      same-pd) step_same_pd "${args[1]:-}" ;;
      expect-pd) step_expect_pd "${args[1]:-}" "${args[2]:-}" "${args[3]:-}" "${args[4]:-}" ;;
      partner-moves) step_partner_moves "${args[1]:-}" ;;
      breakables-sync) step_breakables_sync "${args[1]:-}" ;;
      enemies-sync) step_enemies_sync "${args[1]:-}" ;;
      objects-sync) step_objects_sync "${args[1]:-}" ;;
      rock-sync) step_rock_sync ;;
      scene-data-sync) step_scene_data_sync "${args[1]:-}" ;;
      boss) step_boss "${args[1]:-}" "${args[2]:-}" "${args[3]:-}" ;;
      expect-log) step_expect_log "${args[1]:-}" "$(echo "$line" | cut -d' ' -f3- | sed -E 's/ [0-9]+$//')" "$(echo "$line" | grep -oE ' [0-9]+$' | tr -d ' ')" ;;
      rejoin) step_rejoin ;;
      die) step_die "${args[1]:-}" ;;
      *) _result NOCONC "$keyword" "paso desconocido (mira tests/routes/README.md)" ;;
    esac
  done < "$2"

  # Al final de cada ruta: sin excepciones y la sesión sigue viva
  _R_LINE="fin"
  _result OK "fin" "la ruta llegó al final"
  local errors
  errors="$(_new_errors "$_R_HOST" "$_R_START_HOST")"
  [ -n "$errors" ] && _result FALLO "errores (host)" "$(echo "$errors" | cut -c1-200 | paste -sd'|')"
  errors="$(_new_errors "$_R_GUEST" "$_R_START_GUEST")"
  [ -n "$errors" ] && _result FALLO "errores (invitado)" "$(echo "$errors" | cut -c1-200 | paste -sd'|')"
  for _x in "${HK_EXTRAS[@]}"; do
    errors="$(_new_errors "$_x" "${_R_START_EXTRA[$_x]}")"
    [ -n "$errors" ] && _result FALLO "errores ($_x)" "$(echo "$errors" | cut -c1-200 | paste -sd'|')"
  done
  if group_healthy "$_R_PAIR"; then
    _result OK "sesión" "todos siguen conectados"
  else
    _result FALLO "sesión" "al terminar la ruta la sesión se cayó o se cerró un juego"
  fi
}

# El par y los de más siguen abiertos y con la sesión activa
group_healthy() {
  pair_healthy "$1" || return 1
  local inst
  for inst in "${HK_EXTRAS[@]}"; do
    { instance_running "$inst" && [ "$(status_field "$(status_of "$inst")" activa)" = "True" ]; } || return 1
  done
  return 0
}

# Resultado global de una ruta a partir de su archivo: FALLO > NOCONC > OK. Una ruta que no llegó al final (el
# intérprete se cortó) es FALLO aunque lo comprobado hasta ahí fuera bien
route_verdict() {
  if grep -q '^FALLO' "$1" || ! grep -qE '\| +fin \| fin \|' "$1"; then echo FALLO
  elif grep -q '^NOCONC' "$1"; then echo NOCONC
  else echo OK
  fi
}
