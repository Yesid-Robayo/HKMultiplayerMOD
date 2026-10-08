#!/usr/bin/env bash
# Demostración visible de las sincronizaciones con las dos instancias de prueba (B a la izquierda, C a la derecha).
# Cada paso muestra un cartel en las dos pantallas con lo que está pasando y lo que debería verse en la otra.
# Uso: bash tools/demo-sync.sh            (prepara la partida y recorre todos los pasos)
#      bash tools/demo-sync.sh --sin-preparar   (las instancias ya están conectadas en la misma partida)

source "$(dirname "${BASH_SOURCE[0]}")/mp-test-lib.sh"

PAUSA=${PAUSA:-7}

# Coloca las ventanas de las instancias lado a lado (B izquierda, C derecha) arriba de la pantalla
tile_windows() {
  powershell.exe -NoProfile -Command '
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r); }
"@
    foreach ($p in Get-Process hollow_knight -ErrorAction SilentlyContinue) {
      if ($p.Path -like "*test-instances\b\*") { [Win]::MoveWindow($p.MainWindowHandle, 0, 0, 976, 579, $true) | Out-Null }
      if ($p.Path -like "*test-instances\c\*") { [Win]::MoveWindow($p.MainWindowHandle, 976, 0, 976, 579, $true) | Out-Null }
    }' 2>/dev/null
}

# Cartel en las dos pantallas
say_both() { send B "say $1 $2"; send C "say $1 $2"; }

paso() {
  echo "== $1"
  say_both "$PAUSA" "$1|$2"
  sleep 2
}

preparar() {
  stop_all; sleep 2
  launch B; sleep 6; launch C
  wait_log B "Menú Multijugador añadido" 90 && wait_log C "Menú Multijugador añadido" 90 || return 1
  sleep 4; tile_windows
  send B "hostworld Prueba HostB 26950 $HK_TEST_CODE"
  wait_log B "Partida abierta" 30 || return 1
  sleep 16
  send C "join 127.0.0.1 26950 $HK_TEST_CODE ClientC"
  wait_log C "Conectado como jugador" 30 || return 1
  sleep 18
  send B "goscene Tutorial_01 right1"; send C "goscene Tutorial_01 right1"; sleep 14
}

[ "$1" = "--sin-preparar" ] || preparar || { echo "no se pudo preparar la partida" >&2; exit 1; }
tile_windows

paso "1/7 Jugadores" "C camina a la derecha y vuelve: en la pantalla de B se mueve suave, con su nombre"
send C "press right 1.5"; sleep 1.7; send C "press left 1.5"; sleep 3

paso "2/7 Enemigos" "C golpea un enemigo: la vida baja en los dos y muere en las dos pantallas"
send B "tpenemy Buzzer 1"; send C "tpenemy Buzzer 1"; sleep 2
for i in 1 2 3; do send C "hitenemy Buzzer 1 3"; sleep 1.2; done; sleep 3

paso "3/7 Piedra de geo" "C golpea la piedra hasta romperla: B la ve romperse (cada uno se lleva su geo)"
send B "tpobj Geo Rock 4|1"; send C "tpobj Geo Rock 4|1"; sleep 2
for i in 1 2 3 4 5; do send C "hitrock Geo Rock 4"; sleep 0.8; done; sleep 3

paso "4/7 Estalactita" "B pasa debajo: la estalactita cae también en la pantalla de C"
send B "dropstalactite"; sleep 4

send B "goscene Ruins1_23 left1"; send C "goscene Ruins1_23 left1"; sleep 15

paso "5/7 Rompibles y telas" "C rompe los objetos y telas que tiene cerca: se rompen también en la pantalla de B"
send B "tpobj break_rag|1"; send C "tpobj break_rag|1"; sleep 2
send C "breaknear 6"; sleep 4

paso "6/7 Ascensor (subidos los dos)" "B golpea la palanca de dentro: el ascensor sube con los dos encima en ambas pantallas"
send B "tpobj Ruins Lift 2|1.6"; send C "tpobj Ruins Lift 2|1.6"; sleep 2
send B "fsmstate Control Lever|Flip Direction"; sleep 8

paso "7/7 Ascensor (llamado desde fuera)" "B se baja y lo llama desde otro piso: baja con C encima, sin saltos"
send B "tp 11.7 29.9"; sleep 2
send B "fsmstate Lift Call Lever (2)|Get Direction"; sleep 8

say_both 6 "Fin de la demostración|Las dos ventanas siguen conectadas: puedes probar tú mismo"
echo "== fin"
