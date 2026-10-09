source "$(dirname "${BASH_SOURCE[0]}")/fg-lib.sh"
# Coliseo entero jugando con el aguijón: en cada vuelta un caballero (F y G por turnos) va junto a un enemigo vivo,
# activo y visible en su PC, lo mira y le pega con el aguijón de verdad. Cada vuelta se compara cómo ve cada PC a cada
# enemigo (activo, visible, con caja): un enemigo que un PC ve y el otro no se avisa.
# Uso: colo-nail.sh <Bronze|Silver|Gold> [minutos] [reuse]
room="Room_Colosseum_${1:-Gold}"; minutes="${2:-20}"
if [ "$3" = reuse ] && instance_running F && instance_running G && [ "$(status_field "$(status_of F)" activa)" = True ]; then
  echo "sesión reutilizada"; enter Crossroads_01 left1
else
  setup_fg M3Jefes2 0 || exit 1; sleep 5; enter Crossroads_01 left1
fi
for p in F G; do
  send $p "pdset zoteRescuedDeepnest true"; send $p "pdset zoteDefeated false"; send $p "pdset nailDamage 500"
  send $p "pdset maxHealth 900"; send $p "pdset health 900"
done
enter "$room" left1
for p in F G; do send $p "pdset maxHealth 900"; send $p "heal"; done
send F "tp 99 8"; send G "tp 104 8"; sleep 4; send F "tp 99 8"; send G "tp 104 8"; sleep 2
send F "fsmevent Colosseum Manager/Manager|HERO ENTER"; sleep 2
send F "fsmevent Colosseum Manager/Manager|HERO CENTRE"; sleep 10

bc() { q $1 "findobj Colosseum Manager" | grep -aoE 'Battle Control:[^,]*' | head -1 | cut -d: -f2; }
# Enemigos vivos: "id nombre x y flags" (flags: i=inactivo o=oculto c=sin caja)
live() {
  q $1 enemies | grep -a ' hp=' | grep -av 'MUERTO' | awk '{
    line = $0; sub(/.*enemigo /, "", line)
    name = line; sub(/ id=.*/, "", name); gsub(/ /, "~", name)
    id = line; sub(/.* id=/, "", id); sub(/ .*/, "", id)
    f = ""; if (line ~ /\(inactivo\)/) f = f "i"; if (line ~ / oculto/) f = f "o"; if (line ~ / sin-caja/) f = f "c"
    if (f == "") f = "-"
    pos = line; sub(/.*pos=\(/, "", pos); sub(/\).*/, "", pos); split(pos, v, ",")
    printf "%s %s|%s.%s %s.%s %s\n", id, name, v[1], v[2], v[3], v[4], f
  }'
}
deadline=$((SECONDS + minutes * 60)); last=""; since=$SECONDS; n=0; turn=F
while [ $SECONDS -lt $deadline ]; do
  n=$((n + 1))
  sf="$(bc F)"; sg="$(bc G)"
  lf="$(live F)"; lg="$(live G)"
  act=$(echo "$lf" | awk 'NF && $NF == "-"' | wc -l)
  echo "[$((SECONDS))s] F=$sf  G=$sg  activos=$act"
  [ "$sf" != "$last" ] && { last="$sf"; since=$SECONDS; }
  [ "$sf" = End ] && [ "$sg" = End ] && { echo "== terminado"; break; }
  # Mismo enemigo visto distinto en los dos PCs (solo los que alguno ve activo)
  while read -r id rest; do
    [ -z "$id" ] && continue
    ff=$(echo "$lf" | awk -v i="$id" '$1==i {print $NF}'); gf=$(echo "$lg" | awk -v i="$id" '$1==i {print $NF}')
    [ "$ff" != "$gf" ] && echo "   distinto: $(echo "$rest" | cut -d'|' -f1) F=[$ff] G=[$gf]"
  done <<< "$(echo "$lf" | awk 'NF && $NF !~ /i/')"
  # El caballero del turno pega al primer enemigo que ve activo, visible y con caja
  list="$lf"; [ $turn = G ] && list="$lg"
  target=$(echo "$list" | awk 'NF && $NF == "-"' | head -1)
  if [ -z "$target" ] && [ $((SECONDS - since)) -gt 8 ]; then
    # Nadie a la vista: como jugando, el caballero se acerca a uno escondido (los que salen al ver al caballero),
    # sin ponerse encima (algunos no salen con el caballero donde aparecerían)
    hidden=$(echo "$list" | awk 'NF && $NF ~ /o/ && $NF !~ /i/' | sed -n "$(( (n / 2) % 6 + 1 ))p")
    [ -z "$hidden" ] && hidden=$(echo "$list" | awk 'NF && $NF ~ /o/ && $NF !~ /i/' | head -1)
    if [ -n "$hidden" ]; then
      hx=$(echo "$hidden" | cut -d"|" -f2 | cut -d" " -f1)
      echo "   $turn se acerca a $(echo "$hidden" | cut -d'|' -f1 | cut -d' ' -f2) escondido (x=$hx)"
      send $turn "tp $(awk -v x="$hx" 'BEGIN { printf "%.1f", x - 4 }') 7"
    fi
  fi
  if [ -n "$target" ]; then
    x=$(echo "$target" | sed -E 's/.*\|([-0-9.]+) ([-0-9.]+).*/\1/'); y=$(echo "$target" | sed -E 's/.*\|([-0-9.]+) ([-0-9.]+).*/\2/')
    tx=$(awk -v x="$x" 'BEGIN { printf "%.1f", x - 1.3 }'); ty=$(awk -v y="$y" 'BEGIN { printf "%.1f", (y < 7 ? 7 : y) }')
    tid=${target%% *}
    if [ "$tid" = "$lasttid" ]; then tries=$((tries + 1)); else tries=1; lasttid=$tid; fi
    if [ $tries -gt 3 ]; then
      # Voladores que se apartan antes del tajo: tras tres intentos, golpe directo de este jugador (a la vista y vivo)
      tname=$(echo "$target" | cut -d'|' -f1 | cut -d' ' -f2- | tr '~' ' ')
      echo "   $turn: golpe directo a $tname tras $((tries - 1)) intentos"
      send $turn "hitenemy $tname 500"
    else
      send $turn "tp $tx $ty"; sleep 0.3; send $turn "press right 0.03"; sleep 0.2; send $turn "press attack"; sleep 0.45; send $turn "press attack"
    fi
  fi
  [ $turn = F ] && turn=G || turn=F
  if [ $((SECONDS - since)) -gt 60 ]; then
    echo "== ATASCO: $sf desde hace $((SECONDS - since)) s"; echo "   F: $(echo "$lf" | paste -sd';')"; echo "   G: $(echo "$lg" | paste -sd';')"
    shots "atasco-$n"; since=$SECONDS
  fi
  sleep 1.5
  [ $((n % 10)) = 0 ] && shots "nail-$n"
done
echo "===== fin"
