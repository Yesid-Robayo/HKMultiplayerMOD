# Estado del trabajo (9 de octubre de 2026, de madrugada)

Dónde se quedó el trabajo y qué sigue. El diseño general está en [arquitectura.md](arquitectura.md); lo que el usuario
aprobó para M5, en [m5-diseno.md](m5-diseno.md).

## Versión y ramas

- Rama principal `main`: **0.18.3**. M1 a M4 hechos; M5 juntado (primera y segunda parte) y sus jefes comprobados en
  vivo (ver abajo); amuletos y acompañantes del compañero en 0.18.0.
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

## Jefes de M5 (9 de octubre, mañana; copias F/G)

Comprobado en vivo con capturas de los dos PCs (detalle en [arquitectura.md](arquitectura.md), Pendientes → M5):

- **Grimm**: primer encuentro y regalo de Grimmchild, combate (espera, diálogos, "Call Child") y el Grimm vencido en
  los dos. Arreglos: la copia del encuentro no toma el control por el `ABSORB END` de su Grimmchild; la copia pone los
  datos propios (`metGrimm`); el aviso `GRIMM DEFEATED` del jefe muerto llega a la sala del otro; la espera del
  encuentro solo retiene el aviso de su zona (el `LAND` se quedaba retenido).
- **Tirano de Almas**: primera fase, suelo roto, segunda fase y salida de los dos junto al cuerpo. Arreglos: avisos a
  todos (`PHASE 2`) a la lógica parada de una marioneta se reenvían al PC que la mueve; la segunda fase tenía dos
  `HealthManager` y salía como dos enemigos.
- **Hollow Knight**: desde el Atrio, cadenas (copiadas como palancas), combate, cadáver en los dos y final A
  compartido hasta el banco. Arreglos: cadenas; el cadáver se suelta también en la marioneta; avisos de muerte solo
  si murió de verdad.
- **Radiancia**: presentación compartida (su `Boss Control`), combate, golpe final desde cualquiera y final C
  compartido hasta el banco. Arreglos: controlador compartido; no se retiene a un jefe lejos del caballero local;
  aguijonazos que espera la lógica parada se reenvían.
- **Hornet 1 y Mawlek** siguen esperando a los dos (Mawlek no esperaba: su arena no tiene zona propia).

Sin probar o pendiente: la entrada a la Radiancia por el camino del juego (Hornet ata al Hollow Knight; se quedó en
"H Thread" en la prueba) y que depende del Corazón del Vacío de quien mueve al Hollow Knight; quien mueve a la
Radiancia en sus últimos golpes es quien debe pulsar ataque (el jefe cambia al más cercano también ahí); la revisita
del Núcleo con el Hollow Knight ya vencido.

## Pruebas de sincronización al 100 % (9 de octubre, mañana; rama `worktree-agent-a3d09e6d8f2af91cb`)

Rama de pruebas sobre 0.18.0 con `main` (0.18.3) ya juntado (merge `9dfbd12`). Sin subir versión ni juntar en
`main`. Copias B/C (pruebas extra) y D/E + F/G (rutas).

### Rutas (`tools/run-routes.sh`, 16 rutas)

| Pasada | Versión | Comprobaciones OK / FALLO / NOCONC | Rutas con FALLO |
|---|---|---|---|
| Base (antes de arreglar) | 0.18.0 | 359 / 31 / 37 | 14 de 16 |
| 1 (1 par: F/G no arrancó) | 0.18.3 + arreglos | 389 / 5 / 38 | effects, crystal-peak, kingdoms-edge (2), resting-grounds |
| 2 (2 pares) | ídem | 391 / 2 / 39 | kingdoms-edge (2) |
| 3 (2 pares) | ídem | 383 / 4 / 40 | effects, kingdoms-edge (3) |

Los NOCONC que quedan son de la ruta, no del mod: los `boss` (la pelea no empieza con `hitall`), salas sin
enemigos vivos u objetos sin activar en un mundo ya recorrido. Los FALLO que quedan:
- **effects, doble salto** (intermitente): a mano el host ve el doble salto del invitado 3 de 3 veces; en la ruta a
  veces no sale (tiempo de las pulsaciones con varias copias abiertas). Ya lleva un segundo intento.
- **kingdoms-edge, `enemies-sync` en Hive_01**: los `Hiveling Spawner` son enemigos de un pool: al morir vuelven a su
  jaula (`Death` → `Inert`, `SetHP 5`, `SetIsDead false`) y la colmena de cada PC los vuelve a sacar por su cuenta
  (`SPAWN`). `EnemySync` los trata como enemigos normales, así que cada PC tiene vivos unos distintos. **Pendiente**:
  tratarlos como lo creado en combate (que los saque solo quien mueve la colmena).
- **kingdoms-edge, `breakables-sync` en Deepnest_East_01** (intermitente): las gotas `Orange Glob(Clone)` las crea cada
  PC por su cuenta con ids distintos; si el invitado rompe alguna, el host no la tiene. Estético. **Pendiente**.
- **kingdoms-edge, `objects-sync` "Break Floor 1"** (pasada 3): el suelo que se rompe bajo los dos; el invitado cae a
  Hive_03_c. La ruta ya lo prueba al final de la sala; queda revisar si en el host se rompe (no se pudo repetir).

### Pruebas extra (B/C, 0.18.0 con los arreglos; las marcadas con * repetidas en 0.18.3)

| Prueba | Resultado |
|---|---|
| Los dos salen y entran a la vez por puertas distintas (3 veces) | OK |
| Banco: uno se sienta mientras el otro pelea; el otro entra luego | OK (mismos enemigos vivos) |
| Revivir en el banco (el caído revive junto al que se sienta, con toda la vida) | OK |
| Ascensor de la ciudad con los dos encima, y el otro lo usa después | OK (mismo sitio en los dos; el control pasa) |
| Ciervo: tocar la campana | **FALLO arreglado**: el ciervo solo llegaba en el PC de quien la tocaba |
| Ciervo: subirse y viajar | No comprobado (diálogo y menú del ciervo con órdenes de prueba) |
| Tienda de Iselda: compra de uno | OK (al otro no le cambia nada y el objeto sigue a la venta para él) |
| Geo de un enemigo | OK (cae en los dos PCs y cada uno recoge el suyo) |
| Objeto individual (sello de Crossroads_01) | OK (lo coge uno; el otro lo sigue viendo y puede cogerlo) |
| Muerte con revivir (Focus junto al cuerpo) | OK ("te está reviviendo", barra, vuelve con 1 máscara) |
| Muerte de los dos (nadie en pie) | OK (muerte normal, sombra y banco) |
| Sombras (copia de la del otro; matarla devuelve el geo al dueño) | OK |
| Pinchos (reaparición tras pinchos) | OK |
| Entrar tarde: combate a medias* | **FALLO arreglado**: los heridos se veían con la vida entera |
| Entrar tarde: palanca ya activada | OK (con el arreglo de objetos de sala) |
| Entrar tarde: jefe (Falso Caballero) | OK (misma vida y los golpes del que llega cuentan) |
| Salir y volver a unirse en mitad del combate del jefe | OK |
| Red mala `netsim 120 30 2`: sala con 16 enemigos | OK (mismos vivos en los dos) |
| Red mala: jefe (Falso Caballero) | OK en lo comprobado (misma vida, estado y sitio); no se llegó a matarlo con `hitenemy` |
| El host (o el invitado) en pausa* | **FALLO arreglado**: los enemigos que movía se quedaban congelados para el otro |
| HUD del compañero (máscaras y alma en capturas del mismo momento) | OK (5/7 y alma en los dos) |
| Ajustes del host (cambia el host, el invitado los ve y no puede cambiarlos) | OK |
| Cinemática compartida (créditos) con voto de los dos | OK ("Esperando a ti para saltar 1/2", 2/2 y cada uno a su banco) |
| Sueño (Campeón Fallido): entrar juntos, caída de uno, muerte del otro | OK (sale solo el que cae; el otro sigue con control) |
| Sueño del Tirano de Almas: entrar los dos* | OK |
| Esperas de Hornet 1 y de Mawlek* | OK (Hornet: "espera a que entren todos" y diálogo para los dos; Mawlek despierta al llegar el otro) |
| Skins (POPKnight y Pale King; cambiar; "ver las de los demás") | OK (la textura llega tras la transferencia y el siguiente momento seguro, como está diseñado) |
| Sueño: salida al ganar | No comprobado |

Visto en las pruebas y **pendiente**: al volver del sueño del Campeón Fallido en Crossroads_10 (36,4, 48,4) la zona del
cuerpo del Falso Caballero sale tapada de negro (`Mask 1`, `unmasker` en `Idle Stay`, y los `msk_generic`). En este
mundo de prueba nunca se rompió la pared de esa zona (`Breakable Wall`); hay que mirarlo en una partida normal. Y,
estético, en el PC del caído su etiqueta "† Nombre" se pisa con el nombre del compañero cuando este está encima.

### Arreglos (commits de la rama)

- `5bed957` Objetos de sala (`SceneObjectSync.HasStarted`, gancho de `PersistentBoolItem.Start`): el campo privado
  `started` del juego nunca se pone a verdadero, así que `Activate` dejaba la activación para un `Start` ya pasado. En
  quien no activaba, `Activated` se quedaba en falso (palancas, paredes, máscaras, Battle Scene...) y a quien entraba
  tarde no se le ponían los objetos activados. Era el FALLO de `objects-sync` (24 comprobaciones de 12 rutas en la pasada base).
- `0bbc759` Ciervo (`FsmStateSync.IsStagBell`): la campana de la estación se copia como una palanca.
- `3138f8f` Enemigos (`EnemySync.OnHealth`): la vida de la foto de quien entra ya no se descarta si el enemigo aún no
  es marioneta.
- `dc2dcd0` Pausa (`PartyState`, `MultiplayerRunner`, `EnemySync.AssignControllers`): cada jugador dice si está en
  pausa (`ActivityReport`), el host lo reparte en `PartyStatus` y quien está en pausa no se queda con enemigos.
- Herramientas de prueba: `1e5d7d2` (`HK_INSTANCES_DIR` en las rutas), `2e7fea7` (la regex de salas no jugables
  dejaba empezar la ruta con el host en la cinemática del principio), `a74ba58` (aviso si el host tiene ya 4 mundos).
- Rutas: `fb7bd55` (`canDash`, revivir en la sesión, `partner-moves left`), `0db27bd`, `40550fe`, `02d9dfe`.
- Merge de `main` 0.18.3: `9dfbd12` (conflicto en `FsmStateSync.IsSwitchLike`: cadenas del Hollow Knight y campana).

Nota: la copia F tenía ya 4 mundos y la ruta no podía crear el suyo; `M3Jefes2` se apartó (sin borrar) a
`LocalLow/Team Cherry/HollowKnightF/HKMultiplayer/Mundos-apartados/`.

## Pendiente después

- **M3**:
  - Comprobar en vivo que `RefreshZoneFlags` desatasca el Mage Blob de la oleada 23 de Oro.
  - Coliseo de Bronce: espera en "WaveCheck 5" en la prueba automática (minero escondido; los caballeros no se
    mueven). Jugando no debería pasar.
- **M4**: `tests/routes/effects.route` pasada sobre 0.18.3 (el doble salto falla a veces en la ruta, no a mano).
- **M5**: terminar la segunda parte, juntarla, subir a 0.17.0 y repasar todo M5 en vivo (revivir, sombras, HUD,
  ajustes, sueños, cinemáticas, skins).
- **Amuletos del compañero (pedido del usuario)**: hecho en la rama del agente de amuletos (sobre `merge-m5`, sin
  juntar): Grubberfly, Flukenest (y con Defender's Crest), nubes al curarse, Thorns of Agony, efecto del golpe recibido,
  onda de los gritos, ráfagas del dash sombra y acompañantes (crías, tejedoras, Grimmchild), comprobados con capturas
  en los dos PCs, también Sharp Shadow y los acompañantes atacando a un enemigo.
- **Cinemáticas**: saltarlas solo cuando le dan los dos (voto 1/2, 2/2), sin bloquearse si uno se va.
- **Pruebas extra para sincronizar al 100 %**: hechas (ver "Pruebas de sincronización al 100 %" arriba, con lo que
  queda pendiente). Lo que se pidió: las dos a la vez entrando y saliendo de salas por
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
