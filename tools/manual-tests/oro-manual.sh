#!/usr/bin/env bash
# Coliseo de Oro para jugarlo a mano: F (host) y G (invitado) dentro de la arena, la prueba empezada
source "$(dirname "${BASH_SOURCE[0]}")/fg-lib.sh"
setup_fg M3Jefes2 0 || exit 1
sleep 5
enter Crossroads_01 left1
for p in F G; do
  send $p "pdset zoteRescuedDeepnest true"; send $p "pdset zoteDefeated false"
  send $p "pdset colosseumGoldOpened true"; send $p "pdset nailDamage 100"
  send $p "pdset maxHealth 12"; send $p "pdset health 12"
done
enter Room_Colosseum_Gold left1
send F "heal"; send G "heal"
send F "tp 99 8"; send G "tp 104 8"; sleep 4
send F "tp 99 8"; send G "tp 104 8"; sleep 2
echo "posiciones: F $(status_of F | grep -oE 'pos=\([^)]*\)')  G $(status_of G | grep -oE 'pos=\([^)]*\)')"
send F "fsmevent Colosseum Manager/Manager|HERO ENTER"; sleep 2
send F "fsmevent Colosseum Manager/Manager|HERO CENTRE"; sleep 6
echo "batalla: F $(query F 'findobj Colosseum Manager' | grep -aoE 'Battle Control:[^,]*')  G $(query G 'findobj Colosseum Manager' | grep -aoE 'Battle Control:[^,]*')"
echo "== listo"
