# Estado del trabajo (9 de octubre de 2026, de madrugada)

Dónde se quedó el trabajo y qué sigue. El diseño general está en [arquitectura.md](arquitectura.md); lo que el usuario
aprobó para M5, en [m5-diseno.md](m5-diseno.md).

## Versión y ramas

- Rama principal `main`: **0.16.1** (`aa0e259`). M1, M2, M3 y M4 hechos; M5 a medias (primera parte juntada).
- Ramas de agentes (en `.claude/worktrees/`, cada una con su rama `worktree-agent-...`):
  - `worktree-agent-a8e869211b55a23d1`: **M5, segunda parte, en curso** (cinemáticas, sueños y jefes, skins). Lleva
    un commit (`78cd576`, cinemáticas) y cambios sin commit (`DreamSync.cs`, `BossPresentation.cs`,
    `GrimmchildGift.cs`). Hay que revisarla, terminarla y juntarla.
  - `worktree-agent-a18beb099b6ea4290` (M4) y `worktree-agent-a23c959e3a8f6d80f` (M5, primera parte): ya juntadas en
    `main`; se pueden borrar (`git worktree remove`).
- En `main` puede haber cambios sin commit en `EnemySync.cs` del agente del Coliseo de Oro (ver abajo): revisar con
  `git diff` antes de tocar nada.

## Lo que estaba en marcha al cortar

1. **Coliseo de Oro: un enemigo se apaga vivo** (agente en las copias F/G). La batalla se quedó en "Wave 45" con
   `Battle Enemies=1`: las 113 muertes coinciden en los dos PCs, pero un `Colosseum_Flying_Sentry(Clone)` (id
   `7febe6d1`) quedó desactivado con la vida entera en el suelo de la arena, movido por el invitado. Algo lo apaga en
   los dos sin matarlo. Encontrar quién (registro de "enemigo vivo desactivado" con la pila), arreglarlo y pasar Oro
   entero hasta la Domadora de Dioses (`Lancer` + `Lobster`) y End igual en los dos.
2. **M5, segunda parte** (agente en las copias B/C). Fallos que vio el usuario y están pendientes de arreglar:
   - Cinemática del final: el compañero llevado ve la pantalla **en negro** (el vídeo no arranca); el host la ve.
   - Sueños:
     - Al caer al vacío en un sueño, el "Dream Fall Catcher" se toma como presentación de jefe y **deja quieto al
       compañero**. Solo las presentaciones de jefes de verdad deben inmovilizar a los demás.
     - Una caída (o muerte) de uno **saca a los dos** del sueño. Según el diseño, salen los dos solo al ganar.
     - **Vuelta del sueño mal**: el compañero llevado vuelve a `Dream_Nailcollection` (no se le pone
       `dreamReturnScene` ni la puerta/posición antes de entrar); el que muere no vuelve a donde entró; el host
       apareció dentro de la pared en Crossroads_10 (36.4, 48.4); y a veces se queda la **pantalla en blanco** al salir.
       Comprobar las tres salidas (muerte, caída, victoria) para quien entra y para el llevado.
   - Barra de vida del jefe: el nombre debe salir **en el idioma del juego** (español), no el interno en inglés.
   - Después: el resto de jefes de sueño, Grimm, Rey de las Pesadillas, Hollow Knight, Radiancia y las skins.

## Pendiente después

- **M3**:
  - Comprobar en vivo que `RefreshZoneFlags` desatasca el Mage Blob de la oleada 23 de Oro.
  - Que Hornet 1 y Mawlek siguen esperando a los dos tras el cambio `InBattleScene` (los jefes de arena ya no esperan).
  - Coliseo de Bronce: espera en "WaveCheck 5" en la prueba automática (minero escondido; los caballeros no se
    mueven). Jugando no debería pasar.
- **M4**: está juntado pero sin repasar en el juego tras el merge: pasar `tests/routes/effects.route`.
- **M5**: terminar la segunda parte, juntarla, subir a 0.17.0 y repasar todo M5 en vivo (revivir, sombras, HUD,
  ajustes, sueños, cinemáticas, skins).
- **Amuletos del compañero (pedido del usuario)**: que se vea igual lo que hace el otro caballero con amuletos que
  cambian sus ataques. M4 dejó sin reproducir: moscas de Flukenest, rayos de Grubberfly, nubes de Spore Shroom y
  Defender's Crest, Thorns of Agony y el golpe de Sharp Shadow. Comprobar también Quick Slash (el tajo del compañero al
  ritmo rápido), Mark of Pride y Longnail (alcance), Shaman Stone y Spell Twister (hechizos), Dashmaster/Sprintmaster,
  y los acompañantes que atacan solos (crías de Glowing Womb, tejedoras de Weaversong, Grimmchild). Todo visual en el
  PC que mira; el daño lo hace el PC del dueño.
- **Cinemáticas**: saltarlas solo cuando le dan los dos (voto 1/2, 2/2), sin bloquearse si uno se va.
- **Pruebas extra para sincronizar al 100 %** (pedido del usuario): las dos a la vez entrando y saliendo de salas por
  puertas distintas; bancos (sentarse uno con el otro en combate); viaje en ciervo y ascensores con los dos; tiendas y
  NPC (cada uno lo suyo); recoger objetos individuales y geo; muerte, sombra y reaparición tras pinchos; entrar tarde
  a una sala con combate, jefe o palanca a medias; salir y volver a unirse en mitad de un combate; red mala
  (`netsim` con retraso y pérdida) en un jefe y en el Coliseo; el host en pausa; tres pasadas de las rutas seguidas
  para ver fallos intermitentes.
- **Pruebas finales**: agentes que pasen todas las rutas (`bash tools/run-routes.sh --pairs 2`) sobre la versión
  completa, con el PC sin otros juegos abiertos (la memoria va justa: cada copia ~1,1 GB; Edge y Discord ocupan mucho).
- **M6**: `tools/package.sh` aún usa los nombres del mod anterior (HollowKnightCoop).

## Cómo se trabaja (recordatorio)

- Copias del juego: B/C (agente M5), D/E (rutas automáticas), F/G (pruebas de M3/Coliseo). Cada agente instala solo
  en las suyas (`bash tools/test-cycle.sh --only "f g"`, `HK_INSTANCES_DIR` desde un worktree) y cierra solo las suyas
  (`stop_all F G`). Puertos: 26950 (B/C), 27100+ (rutas), 27300 (F/G).
- Órdenes de prueba útiles: `hero` (por qué un caballero no responde) y `regain`; `enemies` marca "oculto",
  "sin-caja" e "(inactivo)"; `tp x y` no fija el punto de reaparición (`tp x y respawn` sí); `fsmgo` salta a un estado
  de un objeto con controlador.
- No usar `hitall` para comprobar combates: quita vida directamente, mata enemigos dormidos o invulnerables y falsea el
  contador de las oleadas. Mejor golpes de aguijón reales (`tools/manual-tests/colo-nail.sh`).
- Pruebas a mano del Coliseo en `tools/manual-tests/` (`fg-lib.sh`, `colo-natural.sh`, `colo-nail.sh`,
  `oro-manual.sh`: Oro con los dos dentro de la arena para jugarlo a mano).
- Los agentes deben poner tiempo máximo a todas las esperas: varias veces se quedaron colgados esperando a juegos
  cerrados.
