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
  send "$_R_HOST" "goscene $scene $gate"
  send "$_R_GUEST" "goscene $scene $gate"
  deadline=$((SECONDS + 50))
  while [ $SECONDS -lt $deadline ]; do
    sleep 3
    hs="$(status_of "$_R_HOST")"; gs="$(status_of "$_R_GUEST")"
    if [ "$(status_field "$hs" scene)" = "$scene" ] && [ "$(status_field "$gs" scene)" = "$scene" ]; then
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
  send "$_R_HOST" "pdset maxHealth $HK_ROUTE_HEALTH"; send "$_R_HOST" "pdset health $HK_ROUTE_HEALTH"
  send "$_R_GUEST" "pdset maxHealth $HK_ROUTE_HEALTH"; send "$_R_GUEST" "pdset health $HK_ROUTE_HEALTH"

  # Tiempo para que el dueño de la sala reparta su estado y aparezcan los avatares
  sleep 5
  hs="$(status_of "$_R_HOST")"; gs="$(status_of "$_R_GUEST")"
  if [ "$(status_field "$hs" visibles)" -ge 1 ] 2>/dev/null && [ "$(status_field "$gs" visibles)" -ge 1 ] 2>/dev/null; then
    _result OK "room $scene" "los dos en la sala y se ven"
  else
    _result FALLO "room $scene" "en la misma sala pero no se ven (visibles host=$(status_field "$hs" visibles), invitado=$(status_field "$gs" visibles))"
  fi
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
  if got="$(_wait_pd "$dst" "$field" "$value" 8)"; then
    _result OK "shared $from $field $3" "llegó al otro"
  else
    _result FALLO "shared $from $field $3" "el otro sigue con '$got'"
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
  if [ "$got" = "$before" ]; then
    _result OK "private $from $field $3" "no pasó al otro (sigue '$before')"
  else
    _result FALLO "private $from $field $3" "es del personaje pero pasó al otro ('$before' → '$got')"
  fi
}

step_same_pd() {
  local field="$1" h g
  h="$(pd_value "$_R_HOST" "$field")"; g="$(pd_value "$_R_GUEST" "$field")"
  if [ "$h" = "$g" ]; then
    _result OK "same-pd $field" "los dos '$h'"
  else
    _result FALLO "same-pd $field" "host '$h', invitado '$g'"
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
  hb="$(query "$_R_HOST" "breakables" | grep -aoE '[0-9]+ rotos' | grep -oE '^[0-9]+')"
  gb="$(query "$_R_GUEST" "breakables" | grep -aoE '[0-9]+ rotos' | grep -oE '^[0-9]+')"
  if [ -n "$hb" ] && [ "$hb" = "$gb" ]; then
    _result OK "breakables-sync" "el invitado rompió $broke; rotos en los dos: $hb"
  else
    _result FALLO "breakables-sync" "el invitado rompió $broke; rotos host=${hb:-?} invitado=${gb:-?}"
  fi
}

# Ids de enemigos vivos y activos (de la orden "enemies"), ordenados
_alive_enemies() {
  query "$1" "enemies" | grep -a '\[TEST\] enemigo ' | grep -a ' hp=' | grep -av ' MUERTO' | grep -av '(inactivo)' \
    | sed -E 's/.*\[TEST\] enemigo (.*) id=([0-9a-f]+).*/\2 \1/' | sort
}

# Los dos siguen en la sala de la ruta (si uno murió, reapareció en otra)
_both_in_room() {
  [ "$(status_field "$(status_of "$_R_HOST")" scene)" = "$_R_ROOM" ] &&
    [ "$(status_field "$(status_of "$_R_GUEST")" scene)" = "$_R_ROOM" ]
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

  h="$(_alive_enemies "$_R_HOST")"; g="$(_alive_enemies "$_R_GUEST")"
  if [ "$h" = "$g" ]; then
    _result OK "enemies-sync" "$who golpeó $hit; vivos en los dos: $(echo "$h" | grep -c .)"
  else
    _result FALLO "enemies-sync" "$who golpeó $hit; solo vivos en el host: [$(comm -23 <(echo "$h") <(echo "$g") | cut -d' ' -f2- | head -5 | paste -sd,)] solo en el invitado: [$(comm -13 <(echo "$h") <(echo "$g") | cut -d' ' -f2- | head -5 | paste -sd,)]"
  fi
}

step_objects_sync() {
  local who="${1:-guest}" src dst id deadline
  src="$(_who "$who")"; dst="$(_who "$(_other "$who")")"
  id="$(query "$src" "objs" | grep -a '\[TEST\] obj ' | grep -a '=no→' | head -n1 | sed -E 's/.*\[TEST\] obj (.*)=no→.*/\1/')"
  if [ -z "$id" ]; then
    _result NOCONC "objects-sync" "no hay objetos sin activar en la sala"
    return
  fi

  send "$src" "activate $id"
  deadline=$((SECONDS + 10))
  while [ $SECONDS -lt $deadline ]; do
    sleep 2
    if query "$dst" "objs" | grep -aF "obj $id=sí" >/dev/null; then
      _result OK "objects-sync" "$who activó '$id' y el otro lo ve activado"
      return
    fi
  done

  _result FALLO "objects-sync" "$who activó '$id' pero el otro no lo tiene activado"
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
  local scene="${1:-$_R_ROOM}" h g
  send "$_R_HOST" "save"; send "$_R_GUEST" "save"; sleep 4
  h="$(_scene_data "$_R_HOST" "$scene")"; g="$(_scene_data "$_R_GUEST" "$scene")"
  if [ "$h" = "$g" ]; then
    _result OK "scene-data-sync $scene" "$(echo "$h" | grep -c .) objetos guardados iguales"
  else
    _result FALLO "scene-data-sync $scene" "solo en el host: [$(comm -23 <(echo "$h") <(echo "$g") | head -5 | paste -sd,)] solo en el invitado: [$(comm -13 <(echo "$h") <(echo "$g") | head -5 | paste -sd,)]"
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
  if [ "$h" = true ] && [ "$g" = true ]; then
    _result OK "boss $flag" "derrotado en los dos"
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
  local who="${1:-guest}" s
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
      both) send "$_R_HOST" "${line#both }"; send "$_R_GUEST" "${line#both }" ;;
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
  if pair_healthy "$_R_PAIR"; then
    _result OK "sesión" "los dos siguen conectados"
  else
    _result FALLO "sesión" "al terminar la ruta la sesión se cayó o se cerró un juego"
  fi
}

# Resultado global de una ruta a partir de su archivo: FALLO > NOCONC > OK. Una ruta que no llegó al final (el
# intérprete se cortó) es FALLO aunque lo comprobado hasta ahí fuera bien
route_verdict() {
  if grep -q '^FALLO' "$1" || ! grep -qE '\| +fin \| fin \|' "$1"; then echo FALLO
  elif grep -q '^NOCONC' "$1"; then echo NOCONC
  else echo OK
  fi
}
