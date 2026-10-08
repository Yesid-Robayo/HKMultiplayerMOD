# Rutas de prueba del coop

Pruebas automáticas que recorren el juego con dos jugadores reales: un **host**, que abre un mundo coop nuevo, y un
**invitado**, que se une a él. Cada archivo `.route` es una zona del juego o un tema (sesión, progreso). En cada sala
se hace algo en un PC y se comprueba que el otro lo vea igual.

```bash
bash tools/run-routes.sh                        # todas las rutas, 1 par (2 juegos)
bash tools/run-routes.sh --pairs 3              # todas, repartidas entre 3 pares (6 juegos): ~3 veces más rápido
bash tools/run-routes.sh crossroads city        # solo esas rutas
bash tools/run-routes.sh --reuse -v crossroads  # repetir una ruta sin empezar el mundo de cero, viendo cada paso
bash tools/run-routes.sh --lag "120 30 2"       # con red mala (retraso ms, variación ms, pérdida %)
```

- Con **un par** basta para comprobarlo todo. Con más pares solo se reparten las rutas para terminar antes. Cada juego
  ocupa ~1,5 GB de RAM.
- Los pares usan sus propias copias (`test-instances/d`/`e`, `f`/`g`, `h`/`i`, `j`/`k`), que se crean solas con hard
  links. **Nunca usan `b` ni `c`** ni cierran juegos que no son suyos, así que puedes probar a mano mientras corren.
- Si dos procesos lanzan rutas a la vez, cada uno reserva pares distintos (`test-instances/.locks`).
- Para probar una versión nueva del mod: cierra los juegos de las rutas y ejecuta `bash tools/test-cycle.sh`, que lo
  instala en todas las copias.
- Al terminar se muestra un resumen y se guarda en `out/routes/<fecha>/resumen.txt` (un `.txt` con el detalle de cada
  ruta). El código de salida es **0 si no hubo ningún FALLO**.

## Resultados

| Resultado | Significado |
|-----------|-------------|
| `OK`      | Funciona. |
| `FALLO`   | El mod no sincronizó algo, salió una excepción en el log o se cayó la sesión: **hay que arreglarlo**. |
| `NOCONC`  | No se pudo comprobar: no hay rompibles en la sala, el jefe no murió, no se llegó a la sala... Revisa la ruta, no el mod. |

Cada línea del detalle indica el número de línea del `.route`, el paso y qué pasó.

## Formato de un `.route`

Un paso por línea. `#` empieza un comentario. Los pasos que tocan un PC aceptan `host` o `guest` (por defecto `guest`).

### Moverse y dar órdenes

| Paso | Qué hace |
|------|----------|
| `title <texto>` | Nombre de la ruta (solo informativo). |
| `room <Sala> <puerta>` | Los dos entran en la sala por esa puerta y se comprueba que lleguen y se vean. Si no llegan (puerta mal escrita), da `NOCONC` y se saltan los pasos hasta el siguiente `room`. |
| `host <orden>` / `guest <orden>` / `both <orden>` | Envía una orden cruda del modo de prueba (`src/HKMultiplayer/Testing/TestDriver.cs`): `tp`, `press`, `fsmevent`, `hitenemy`... |
| `wait <segundos>` | Espera. |

### Comprobaciones

| Paso | Qué comprueba |
|------|---------------|
| `partner-moves` | El invitado camina y el host ve moverse su avatar. |
| `shared <quién> <campo> <valor>` | Un campo de `PlayerData` que es **del mundo** (habilidades, puertas, jefes...): se cambia en un PC y debe llegar al otro. |
| `private <quién> <campo> <valor>` | Un campo que es **del personaje** (geo, amuletos, máscaras...): se cambia en un PC y el otro **no** debe cambiar. |
| `same-pd <campo>` | El campo vale lo mismo en los dos. |
| `expect-pd <quién> <campo> <valor> [s]` | El campo vale eso en ese PC (espera hasta 8 s o `[s]`). |
| `breakables-sync [n]` | El invitado rompe los `n` rompibles más cercanos (3 por defecto) y los dos tienen los mismos rotos. |
| `rock-sync` | El invitado golpea una piedra de geo y el host ve el golpe. |
| `objects-sync [quién]` | Activa el primer objeto de sala sin activar (palanca, pared, puerta...) y el otro lo ve activado. |
| `enemies-sync [quién]` | Golpea a todos los enemigos de la sala y comprueba que quedan vivos los mismos en los dos. |
| `scene-data-sync [Sala]` | Los dos guardan y lo guardado de la sala (objetos activados, contadores) es igual, sin contar lo que recoge cada uno. |
| `boss <campo> [quién] [intentos]` | Golpea a todo hasta que muera el jefe y comprueba que su campo de derrota (`falseKnightDefeated`...) es `true` en los dos. Si el jefe no muere (la pelea no empezó), da `NOCONC`. |
| `expect-log <host\|guest> <regex> [s]` | Aparece esa línea en el ModLog de ese PC desde que empezó la ruta. |
| `die [quién]` | Ese jugador muere y sigue en la partida. |
| `rejoin` | El invitado sale al menú y vuelve a unirse. Conserva su personaje. |

Al final de cada ruta, siempre se comprueba que no haya excepciones ni `[ERROR]` nuevos en el log de ninguno de los dos
y que la sesión siga activa. Para ignorar un error conocido: `HK_IGNORE_ERRORS='texto|otro' bash tools/run-routes.sh`.

## Escribir rutas

- Las rutas se ejecutan en paralelo y en cualquier orden **sobre el mismo mundo** de cada par. No pongas en una ruta
  campos que cambien otras zonas (`crossroadsInfected`, campos de derrota de jefes con `shared`...). Para los jefes usa
  `boss` en su sala.
- Los nombres de campos deben existir en `PlayerData`. Si no existe, `shared` da `NOCONC` ("no se pudo poner").
- Las puertas son las del juego (`left1`, `right1`, `top1`, `bot1`, `door1`...). Si una sala da `NOCONC` al entrar,
  prueba otra puerta: entra a mano con `guest goscene <Sala> <puerta>` y la orden `gates` lista las de la sala.
- Una ruta corta para probar un arreglo concreto es perfectamente válida: crea `tests/routes/mi-prueba.route` y lánzala
  con `bash tools/run-routes.sh --reuse -v mi-prueba`.
