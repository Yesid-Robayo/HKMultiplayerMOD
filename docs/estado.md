# Estado del trabajo (9 de octubre de 2026, de madrugada)

Dónde se quedó el trabajo y qué sigue. El diseño general está en [arquitectura.md](arquitectura.md); lo que el usuario
aprobó para M5, en [m5-diseno.md](m5-diseno.md).

## Versión y ramas

- Rama principal `main`: **0.18.7**. M1 a M4 hechos; M5 juntado (primera y segunda parte) y sus jefes comprobados en
  vivo (ver abajo); amuletos y acompañantes del compañero en 0.18.0.
- Ramas de agentes (en `.claude/worktrees/`, cada una con su rama `worktree-agent-...`):
  - `worktree-agent-a3fca8e41ebb6684d`: **M6, paquete para jugadores** (sobre 0.18.4, sin subir versión ni juntar).
    Ver "M6: paquete e instalador" abajo.
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
  (`SPAWN`). `EnemySync` los trata como enemigos normales, así que cada PC tiene vivos unos distintos. **Arreglado en
  0.18.5** (ver "Pendientes de la prueba final" abajo).
- **kingdoms-edge, `breakables-sync` en Deepnest_East_01** (intermitente): las gotas `Orange Glob(Clone)` las crea cada
  PC por su cuenta con ids distintos; si el invitado rompe alguna, el host no la tiene. Estético. **Arreglado en
  0.18.5**.
- **kingdoms-edge, `objects-sync` "Break Floor 1"** (pasada 3): el suelo que se rompe bajo los dos; el invitado cae a
  Hive_03_c. **Era de la ruta** (0.18.5): al caminar a la izquierda de la puerta el invitado pisa ese suelo y cae.

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

Visto en las pruebas: al volver del sueño del Campeón Fallido en Crossroads_10 (36,4, 48,4) la zona del cuerpo del
Falso Caballero sale tapada de negro (`Mask 1`, `unmasker` en `Idle Stay`, y los `msk_generic`); era del mundo de
prueba (ver "Pendientes de la prueba final"). Y, estético, en el PC del caído su etiqueta "† Nombre" se pisaba con el
nombre del compañero cuando este estaba encima (arreglado en 0.18.6).

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

## Pendientes de la prueba final (9 de octubre, mediodía; `main`, copias D/E y F/G)

- **Crías de la Colmena (`Hiveling Spawner`, Hive_01)**, 0.18.5. Causa: son enemigos de jaula (esperan en `Hatcher
  Cage (1)`, fuera de la vista; al morir vuelven a ella con la vida repuesta) y los saca el `Zombie Hive` con
  `GetRandomChild` + `SPAWN`. Como enemigos de la sala, `hitall` mataba también a los de la jaula, cada PC los sacaba
  por su cuenta (al moverlo o al repetir su marioneta el `SPAWN`) y quedaban vivos unos distintos. Arreglo
  (`EnemySync`: `TryCage`, `ReleaseTarget`, `OnReleased`, `OnRelease`, `BackInCage`, `ReturnToCage`, y
  `ShouldRunEffect`): ya no son enemigos de la sala; solo el PC que mueve al `Zombie Hive` los saca y los sigue como lo
  creado en combate (`EnemyRelease`, 98), los demás sacan el mismo de su jaula como copia, y al volver a la jaula
  desaparece en todos. Comprobado a mano (D/E): sale en el PC que mueve la colmena, copia en el otro con el mismo id;
  un golpe de aguijón del otro PC lo mata y vuelve a la jaula en los dos; `hitall` deja los mismos vivos (0) en los
  dos. `kingdoms-edge` 3 veces seguidas sin FALLO (solo el NOCONC de Hornet, que no empieza con `hitall`).
- **Rompible de esa ruta y gotas `Orange Glob(Clone)` (Deepnest_East_01)**, 0.18.5. Dos causas: las gotas son del pool
  del juego (están en `GlobalPool` y vuelven solas con `AutoRecycleSelf`), así que su id por ruta no señala la misma
  gota en cada PC; y la ruta hacía caminar al invitado a la izquierda de la puerta, sobre `Break Floor 1` (la entrada a
  la Colmena), que se rompe al pisarlo: caía a Hive_03_c y fallaban `breakables-sync`, `enemies-sync` y
  `objects-sync`. Arreglo: `BreakableSync.IsPoolObject` (lo del pool no se sigue: decoración de cada PC) y la ruta ya
  no mueve al invitado en esa sala (`partner-moves` se prueba en otras).
- Sin explicar: en dos pasadas con dos lanzadores de rutas a la vez sobre el mismo par (una tarea anterior no se
  había parado) salió en el invitado un `NullReferenceException` en `GameObject.get_scene` ("Error en la partida") y un
  `scene-data-sync` distinto en Hive_01. No se repitió en las pasadas limpias.
- **Etiquetas solapadas del caído**, 0.18.6. Causa: el "† Nombre" del cuerpo y el nombre del avatar del compañero van
  los dos a 1,3 sobre su punto; con el compañero encima del cuerpo (reviviéndolo) se pisaban. Arreglo
  (`Revive.UpdateBodies` y `DownedBody.SetLabelLift`): si un avatar está a menos de 3 en horizontal y 1,6 en vertical
  del cuerpo, el "† Nombre" sube por encima del nombre de ese avatar (0,75 más arriba); al irse vuelve a su sitio.
  Comprobado con capturas (F/G): con el host encima, "† Invitado" queda sobre "Host"; con el host a 4 de distancia, a
  la altura de siempre.
- **Zona tapada al volver del sueño del Campeón Fallido (Crossroads_10)**: **del mundo de prueba, no del mod**. La
  puerta de vuelta (`door_dreamReturn`, 36,4, 47,7) está dentro de la zona que tapa `Breakable Wall/Masks` (`Mask 1` y
  sus `msk_generic`). `Mask 1` no tiene caja (su `Trigger2dEvent` de `Idle Stay` nunca salta): lo único que la quita
  es esa pared, al romperla (`Break` → `UNCOVER`) o al cargar la sala ya rota (`Activated` apaga `Masks`). Con la pared
  sin romper, en (36,4, 48,4) `covers` lista los `msk_generic`; con la pared rota (`sdset Crossroads_10 Breakable
  Wall`) y entrando por `door_dreamReturn`, ya no hay nada que tape. La pared es un objeto del mundo (`SceneObjectSync`):
  rota por uno, rota para todos. Ojo al probar: entrar con `goscene Crossroads_10 door_dreamReturn` deja la pantalla en
  negro (el fundido de la cámara se queda en `FadingOut`: esa puerta espera la vuelta de un sueño); es de la orden de
  prueba, no del mod.
- **Ciervo con los dos** (B/C): OK. B toca la campana, se sube (zona `Travel Range`, menú de estaciones) y viaja a
  Cruces Olvidados; en C el ciervo se queda en la estación y C viaja después al mismo sitio. Los dos llegan a
  Crossroads_47 y se ven. Estético: mientras B viaja, el HUD de C dice "HostB en None".
- **Sueño del Campeón Fallido** (B/C; 0.18.7). Arreglos:
  - El jefe se quedaba dormido (`FalseyControl` en `Dormant`, invencible) si su `BATTLE START` se cruzaba con un cambio
    de PC: el reenvío llegaba a un PC que ya no lo movía y lo descartaba. Ahora el aviso lleva a qué PC va; si ese ya
    no lo mueve lo pasa al que lo mueve, y si está a punto de tomarlo lo guarda y lo atiende al tomarlo
    (`EnemySync.OnEnemyEvent`, `SendEventTo`, `ApplyPendingEvents`). Un PC que se lo mandaba a sí mismo (aún sin
    tomarlo) también lo perdía: ahora lo guarda.
  - Y la espera de "todos dentro" no funcionaba con la zona del combate de esa arena (`Battle Scene`, una línea de 1 de
    ancho en x = 45): el primero en cruzarla salía de ella, la espera se olvidaba y el combate no empezaba nunca (solo
    empezaba si cruzaban los dos a la vez). Ahora en una zona fina cuenta estar ya al otro lado
    (`FsmStateSync.InsideZone`, `ArenaSide`).
  - Las puertas de la arena (`Dream Gate`, `Dream Gate (1)`, FSM `Control`) solo se cerraban en el PC que movía al
    jefe: ahora son rejas con controlador (`FsmStateSync.IsGate`), con la puesta al día y el paso de un solo sentido
    de las rejas. Además, el `DREAM GATE CLOSE` que manda el jefe cuando lo despierta la arena copiada de otro PC se
    descartaba (se estaba aplicando esa cadena): ahora lo que manda un enemigo que mueve este PC cuenta.
  - Otros sueños: el Tirano de Almas tiene `Dream Gate` (`Control`) y cuatro `Dream Gate Phase 2` (`Control2`, se
    cierran con `PHASE 2`), también incluidas; Kin Perdido, `Dream Gate` y `Dream Gate (1)`; el Rey de las
    Pesadillas, `Dream Gate` dentro de `Grimm Control`; el Defensor Blanco usa una `Battle Gate` (ya iba); en el de
    Zote no hay puertas. Solo se comprobó en vivo el del Campeón Fallido.
  - Un caído con el cuerpo en el sueño seguía a su compañero fuera del sueño (al ganar o al salir este) y se quedaba
    caído sin que nadie pudiera revivirlo. Ahora el host lo revive junto a ese compañero
    (`Revive.ReviveLeftInDreams`).
  - Comprobado: 4 veces seguidas forzando el cambio de PC justo al empezar (C junto al jefe dormido; C se va y B cruza
    la zona; luego C entra): el combate empieza (barra "Campeón Fallido" en los dos) y las dos puertas quedan
    `Closed` en los dos PCs, con capturas del mismo momento (`fk3-b`, `fk3-c`). Y con B caído en mitad del combate, C
    sigue dentro, gana, sale ("los compañeros del sueño salen también") y B revive a su lado; los dos con control.
  - Sin comprobar: que las puertas se abran a la vez al ganar (el jefe las abre en `Floor Break`, justo antes de la
    salida) y los otros sueños en vivo.
  - Nota: `setup_session` de B/C carga partidas propias (no un mundo coop), así que ahí el progreso no se comparte
    (`falseKnightDreamDefeated` quedó en verdadero solo en C). No es un fallo.

## M6: paquete e instalador (9 de octubre, mañana; copias H/I)

Hecho sobre 0.18.4 (detalle en [arquitectura.md](arquitectura.md#paquete-e-instalador) y en la sección Instalar del
README):

- `tools/package.sh` con el nombre nuevo: compila en Release y deja `out/package/HKMultiplayer-0.18.4.zip` (≈7,7 MB)
  con `instalar.bat`, `desinstalar.bat`, `LEEME.txt`, `scripts/`, `files/` (Modding API de `vendor/` y
  `Mods/HKMultiplayer` con el mod, su `.pdb` y BouncyCastle) y `SHA256SUMS.txt`. Nada más del juego.
- Instalador y desinstalador reescritos (`tools/installer/`: `install.ps1`, `uninstall.ps1`, `common.ps1`, los dos
  .bat y `LEEME.txt`): búsqueda del juego (ruta pasada o arrastrada, Steam, rutas típicas), comprobación de la versión
  del juego (Unity 6), copia de lo que se reemplaza con un manifiesto de cambios, quita `HollowKnightCoop` (guardado
  para devolverlo al desinstalar), aparta los demás mods a `Mods\Disabled` y lo deshace todo al desinstalar.
- Probado con `tools/manual-tests/installer-test.sh` (copias H e I en `test-instances` del repo principal, con
  `Managed` y `steam_appid.txt` como archivos propios para no escribir en los de Steam):
  - H (juego sin mods) e I (además `Mods/HollowKnightCoop` y `Mods/Satchel`): `instalar.bat <ruta>` instala 26
    archivos y guarda 3 originales (`Assembly-CSharp.dll`, `TeamCherry.Localization.dll`, `System.ValueTuple.dll`); en
    I quita el mod anterior y aparta Satchel.
  - Las dos copias arrancan con el mod: ModLog con "Iniciando HKMultiplayer 0.18.4", "HKMultiplayer : 0.18.4",
    "Menú Multijugador añadido al menú principal" y "Multijugador añadido al menú de pausa"; I no carga el mod anterior
    ni Satchel.
  - Segunda instalación encima (actualización) y `desinstalar.bat`: rutas, tamaños y hashes de la raíz y de `Managed`
    iguales a los de antes de instalar (en I vuelven `HollowKnightCoop` y Satchel).
  - El juego de Steam no se tocó (mismos hashes). Las copias H e I se borraron al terminar.
- Pendiente: instalarlo en el PC de otro jugador desde el zip y jugar por internet con el puerto abierto. En este PC no
  hay una `Assembly-CSharp.dll` sin la Modding API: la copia "limpia" de la prueba usa otra versión parcheada
  (`Assembly-CSharp.dll.v`) como original.

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
- **M6**: hecho en la rama `worktree-agent-a3fca8e41ebb6684d` (ver arriba); falta juntarlo y probarlo en otro PC.

## Cómo se trabaja (recordatorio)

- Copias del juego: B/C (agente M5), D/E (rutas automáticas), F/G (pruebas de M3/Coliseo), H/I (prueba del
  instalador; `installer-test.sh` las rehace desde cero). Cada agente instala solo
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
