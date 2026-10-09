source "$(dirname "${BASH_SOURCE[0]}")/fg-lib.sh"
# Prueba del Coliseo entera, sin atajos: F acepta, las oleadas se matan con golpes de poca vida en los dos y el Coliseo
# lanza la siguiente solo. Cada vuelta: estado de Battle Control y enemigos vivos y activos en cada PC.
# Uso: colo-natural.sh <Bronze|Silver|Gold> [minutos] [reuse]
room="Room_Colosseum_${1:-Bronze}"; minutes="${2:-15}"
if [ "$3" = reuse ] && instance_running F && instance_running G && [ "$(status_field "$(status_of F)" activa)" = True ]; then
  echo "sesión reutilizada"
else
  setup_fg M3Jefes2 0 || exit 1; sleep 5; enter Crossroads_01 left1
fi
for p in F G; do
  send $p "pdset zoteRescuedDeepnest true"; send $p "pdset zoteDefeated false"; send $p "pdset nailDamage 500"
  send $p "pdset maxHealth 900"; send $p "pdset health 900"
done
enter "$room" left1
send F "pdset maxHealth 900"; send F "pdset health 900"; send G "pdset maxHealth 900"; send G "pdset health 900"
send F "tp 99 8"; send G "tp 104 8"; sleep 4
# Por si el invitado aún cargaba: otra vez, y se comprueba que esté dentro
send F "tp 99 8"; send G "tp 104 8"; sleep 2
echo "posiciones: F $(status_of F | grep -oE 'pos=\([^)]*\)')  G $(status_of G | grep -oE 'pos=\([^)]*\)')"
send F "fsmevent Colosseum Manager/Manager|HERO ENTER"; sleep 2
send F "fsmevent Colosseum Manager/Manager|HERO CENTRE"; sleep 10

bc() { q $1 "findobj Colosseum Manager" | grep -aoE 'Battle Control:[^,]*' | head -1 | cut -d: -f2; }
alive() { q $1 enemies | grep -a ' hp=' | grep -av 'MUERTO\|(inactivo)' | wc -l; }
names() { q $1 enemies | grep -a ' hp=' | grep -av 'MUERTO\|(inactivo)' | sed -E 's/.*enemigo (.*) id=([0-9a-f]+) (real|marioneta).*mueve=([0-9]+) hp=([0-9]+).*/\1@\2:\3:\4:\5/' | sort | paste -sd' '; }

deadline=$((SECONDS + minutes * 60)); last=""; since=$SECONDS; n=0
while [ $SECONDS -lt $deadline ]; do
  n=$((n + 1))
  sf="$(bc F)"; sg="$(bc G)"; af=$(alive F); ag=$(alive G)
  echo "[$((SECONDS))s] F=$sf ($af vivos)  G=$sg ($ag vivos)"
  [ "$sf" != "$last" ] && { last="$sf"; since=$SECONDS; }
  [ "$sf" = End ] && [ "$sg" = End ] && { echo "== terminado"; break; }
  if [ $((SECONDS - since)) -gt 45 ]; then
    echo "== ATASCO: $sf desde hace $((SECONDS - since)) s"
    echo "   F: $(names F)"; echo "   G: $(names G)"
    shots "atasco-$n"; since=$SECONDS
  fi
  # Cada uno pega a lo que tiene cerca como en el juego (daño del aguijón alto) y un golpe moderado a todos
  send F "hitall 80"; send G "hitall 80"; sleep 3
  [ $((n % 4)) = 0 ] && shots "nat-$n"
done
echo "   F final: $(names F)"; echo "   G final: $(names G)"
echo "===== fin"
