# Estado del trabajo (9 de octubre de 2026, tarde)

Dónde está el trabajo y qué queda. El diseño general está en [arquitectura.md](arquitectura.md); lo que el usuario
aprobó para M5, en [m5-diseno.md](m5-diseno.md).

## Versión

- `main`: **0.20.2**, solo commits locales (no se sube nada sin que lo pida el usuario). M1 a M6 hechos y juntados;
  no quedan ramas de agentes. Paquete para jugadores: `bash tools/package.sh` → `out/package/HKMultiplayer-<ver>.zip`.
- Última pasada de las 16 rutas (0.20.1): 0 FALLO.

## Detalles menores (0.20.2; copias B/C, pruebas solo de lo cambiado)

| Qué | Arreglo | Comprobado |
|---|---|---|
| "Recargar sala" y el caído viajaban sin puerta de entrada (el juego los ponía en la primera puerta y en algunas salas salían por ella) | Los viajes a un punto entran por `dreamGate` (como `SceneTravel`); el caído que sigue a su compañero entra por la misma puerta que él | En vivo: recargar en la sala de Madre Gruz deja a C en el mismo punto; C caído sigue a B a `Crossroads_50` por `left1` y se queda; al caer B, C vuelve a su cuerpo en `Crossroads_04`, muere de verdad y los dos aparecen en su banco |
| `damages_enemy/Send Event` de los barriles (unos 257 mensajes por minuto sin efecto) | Las FSM de daño (`damages_enemy`, `damages_hero`) no se siguen como efectos de la marioneta | Por código (solo deja de enviar algo que el otro PC no usaba) |
| Myla infectada solo aparecía al volver a entrar | Lo que un `DeactivateIfPlayerdata*` apagó al cargar la sala se vuelve a encender si el dato cambia en otro PC (los enemigos solo se encienden, nunca se apagan) | En vivo (ver Misiones: Myla) |
| Fuente de Hornet: el invitado se quedó una vez sin control | Durante la conversación, los sí/no cambiados en otro PC se leen como al empezarla (antes solo los números) | Por análisis: no se pudo repetir el fallo original |
| Combate del Rey Vengamosca (Zote) | — | En vivo: el mismo jefe en los dos, pasa a moverlo el PC cercano, muere en los dos y `killedBigBuzzer` en los dos |

Se deja como está: la marioneta se dibuja unas centésimas en el pasado (para moverse suave aunque la red varíe); en
lo que cae rápido (barriles) se nota un poco. Adelantarla haría que atravesara el suelo y diera saltos.

## Misiones de NPC (9 de octubre, tarde; copias B/C, rama `worktree-agent-a8f25163372f2f05e`)

Sobre 0.19.0, sin subir versión ni juntar. Pregunta del usuario: "¿qué pasa con las misiones, por ejemplo la de la
seta?". El avance de una misión es del mundo y se comparte, pero el personaje en la sala es de cada PC y el juego solo
mira el dato al cargar la sala.

Reproducido con Mr. Mushroom (Fungus2_18, `mrMushroomState` 1, los dos con Spore Shroom): B le habla (sube a 2 al
empezar la conversación, en `Conversation Control/Convo`), en B se va y en C sigue ahí; C lo despierta, le habla y la
misión sube a 3 (se salta el sitio 2) y C lee el texto del sitio siguiente.

Arreglo general (detalle en [arquitectura.md](arquitectura.md), "Personajes de misión"):
- `QuestNpcSync` (nuevo): cuando llega de otro PC un cambio de un dato del mundo, las FSM de la sala que lo miran al
  empezar (de personajes, o que solo enseñan o esconden algo) vuelven a empezar con el valor nuevo, y los objetos con
  `DeactivateIfPlayerdataTrue`/`False`/`ActivateIfPlayerdataTrue` se encienden o apagan. Se espera a que el caballero
  local tenga control. Si los dos hablan a la vez con su copia del personaje, la segunda conversación no vuelve a
  avanzar el número que el otro avanzó mientras tanto y lo lee como estaba al empezarla.
- `FsmStateSync`: los objetos de misión que se golpean con el aguijón y cambian un dato del mundo copian su cadena como
  una palanca (Zote en Nido Profundo, el farol de la Pesadilla); un golpe local desde el reposo cuenta aunque acabe de
  llegar la cadena de otro PC.
- `CharacterData`: lo que pasa con el Forjaguijones tras la última mejora es de cada personaje.
- Órdenes de prueba: `questnpcs`, `fsmuses <texto>` y `pressat <hora ms UTC> <botón>`.

| Misión | Resultado | Cómo se comprobó |
|---|---|---|
| Mr. Mushroom (7 sitios, `mrMushroomState`) | **Arreglado** | En vivo (capturas del mismo momento): B habla y en C desaparece en ese momento; C ya no puede hablarle; queda en 2. Los dos a la vez (C y B medio segundo después, `pressat`): los dos hablan, cada uno lee el texto del sitio 1, en B sale "ya lo avanzó otro jugador" y queda en 2. |
| Quirrel (Templo del Huevo Negro y demás sitios) | **Arreglado** | En vivo: B en el templo con Quirrel; C entra en Crossroads_06 (`quirrelLeftEggTemple`) y en B Quirrel se va. Sus conversaciones ponen el número de su diálogo (no hay doble avance). Los demás sitios usan el mismo patrón (`FSM`/`Check` con un sí/no); no probados uno a uno. |
| Zote: rescate en Nido Profundo | **Arreglado** | En vivo: B corta la telaraña con el aguijón; Zote cae en los dos, `zoteRescuedDeepnest` en los dos y C habla con su Zote. |
| Zote: rescate del Rey Vengamosca y Coliseo | Combate OK | En vivo (0.20.2): el jefe es el mismo en los dos, pasa a moverlo el PC cercano y muere en los dos (`killedBigBuzzer` en los dos). La conversación con Zote después y el Coliseo no se probaron. |
| Bretta (rescate y Bocasucia) | **Arreglado** (rescate) | En vivo: B habla con Bretta en Fungus2_23 y en C desaparece (`brettaRescued`). En Bocasucia la casa (`bretta_house/Check Opened`) se abre igual al llegar el dato; no probado en vivo. |
| Cloth | OK por análisis, sin probar | Mismo patrón que Bretta (`Destroy`/`Check` con `clothEncounteredQueensGarden`, `savedCloth`, `clothKilled`). Su ayuda en el combate del Señor Traidor no se probó. |
| Tiso | OK por análisis, sin probar | Mismo patrón (`Tiso Bench NPC/FSM` con `tisoEncounteredBench`/`Town`). En el PC de quien habla sigue hasta volver a entrar; en el otro se va enseguida. |
| Grimm y la Compañía: llamas | OK (ya iba) | En vivo: Grimmkin muerto por C, `flamesCollected` sube 1 en los dos (la `Spawn Control` ya copiaba su cadena sin repetir el `IncrementPlayerDataInt`). |
| Grimm y la Compañía: farol | **Arreglado** | En vivo: B lo hace aparecer (Sycophant) y en C aparece; golpes de los dos seguidos lo encienden en los dos a la vez (`nightmareLanternLit`, `troupeInTown`). Antes cada PC contaba solo sus golpes. El destierro (segunda tanda de golpes) va por la misma cadena; no probado. |
| Myla | **Arreglado** | Va con `DeactivateIfPlayerdataTrue`/`False` (`hasSuperDash`, `mageLordDefeated`). En vivo (0.20.2): C consigue el dato y en B, que está en su sala, la Myla que canta se apaga y aparece la infectada (antes solo al volver a entrar: su `DeactivateIfPlayerdataFalse` solo apaga); C la ve igual, en el mismo sitio, movida por B. |
| Hornet en la Ciudad (fuente) | OK | En vivo: B ve su escena (cinemática y diálogo) y al acabar (`hornetFountainEncounter`) en C se apaga el encuentro. Una vez, con los dos haciendo la escena a la vez, C se quedó sin control al acabar (con `regain` siguió); no se repitió con C solo ni con C mirando. Causa probable, arreglada en 0.20.2: a mitad de su escena C leía `hornetFountainEncounter` ya puesto por B y tomaba la rama de después; ahora durante la conversación los sí/no se leen como al empezarla (igual que los números). |
| "El Leñador" | Sin identificar | No hay ningún personaje con ese nombre en los textos del juego (`langget`). Se revisó el Forjaguijones (`Titles/NAILSMITH_MAIN`): **arreglado**, lo que pasa con él tras la última mejora es de cada personaje (antes, si uno lo perdonaba, el otro no podía elegir). |
| Menderbug | OK por análisis, sin probar | El cartel de Cruces Olvidados es un rompible que ya copia su cadena (`menderSignBroken`, `menderState` se ponen, no se suman); el Menderbug es un enemigo compartido. Sale al azar al cargar la sala. |
| Vidente (esencia) | OK por diseño | `dreamOrbs` y `dreamRewardN` son del personaje: cada uno recibe sus recompensas. Lo que abre (puerta del Claro, Aguijón onírico despertado, Portal de sueños) es del mundo y se comparte. |
| Padre de las Larvas | OK por diseño | `grubsCollected` es del mundo, `grubRewards` del personaje: cada uno cobra sus recompensas. Las larvas de su sala van con `DeactivateIfPlayerdataTrue` (`fatGrubKing`). |

Notas:
- En el PC de quien habla, el personaje hace lo del juego (Bretta o Tiso siguen hasta volver a entrar); en el otro se
  va al llegar el dato, como si se cargara la sala.
- Si el dato hace aparecer a alguien que se destruyó al cargar la sala (otro sitio de Mr. Mushroom en la sala de este
  jugador), no aparece hasta volver a entrar.
- Ojo al probar: `goscene Fungus2_18 left1` a veces deja al caballero entrando sin terminar (en el aire, sin control); con
  `right1` no pasa.

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
| Final (2 pares, D/E y F/G; `main`) | 0.18.9 | 387 / 0 / 39 | ninguna |

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
  - Aviso "Esperando a ... para empezar el combate" (0.18.9): seguía saliendo en el invitado con el combate ya
    empezado (su espera retenida no se soltaba: la arena había empezado por la cadena copiada del host). Ahora la
    espera se olvida y el aviso se quita en cuanto la arena sale de su estado de espera o el combate empieza
    (`FsmStateSync.UpdatePendingStart`, `Notice.Hide`). Comprobado (B/C): C cruza primero y ve el aviso; B entra y
    2,5 s después C ya no lo tiene y ve la barra del jefe.
  - Nota: `setup_session` de B/C carga partidas propias (no un mundo coop), así que ahí el progreso no se comparte
    (`falseKnightDreamDefeated` quedó en verdadero solo en C). No es un fallo.

- **Pasada final de las rutas** (0.18.9, `bash tools/run-routes.sh --pairs 2` con D/E y F/G, `out/routes/20261009-131301`): 16 rutas, **387 OK, 0 FALLO, 39 NOCONC**. Los NOCONC son de la ruta: los `boss` (la
  pelea no empieza con `hitall`), salas sin enemigos vivos u objetos sin activar en un mundo ya recorrido, piedras de
  geo ya rotas y, nuevo, `waterways` no llega a `Waterways_01` por `top1` (los dos se quedan en `Ruins1_05b`; mirar la
  puerta de la ruta).
- Sin comprobar (punto 5): matar a un jefe con red mala (`netsim 120 30 2`): no quedó sitio (las copias D/E se
  usaban para otra prueba y como mucho caben 4 juegos). Tampoco que las puertas de los sueños se abran a la vez al
  ganar ni los demás sueños de jefe en vivo.
- Ojo con `run-routes.sh --pairs 2` si un par está reservado: coge el siguiente (H/I) y crea esas copias. Pasó una
  vez (se cerraron en seguida y se borraron `test-instances/h` e `i`); al crearlas, `setup_pair` borra el mundo
  `Ruta` de `HollowKnightH` y copia los ajustes del registro de B a H e I.

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

## Pendiente

Sin probar (no se tocó el código; nada indica que falle):
- La conversación con Zote tras el combate del Rey Vengamosca (mismo patrón que el rescate de Nido Profundo, que sí
  se probó) y Zote en el Coliseo.
- Los jefes de sueño aparte del Campeón Fallido y del Falso Caballero; que las puertas del sueño se abran a la vez al
  ganar.
- Volver de una cinemática que acaba en una sala de juego (va por `SceneTravel`, ya probado con "Ir con ...").
- La entrada a la Radiancia por el camino del juego (una vez se trabó la escena de Hornet atando al Hollow Knight).
- Cloth, Tiso y Menderbug (por análisis).

Solo en una sesión real de dos PCs: matar a un jefe con red mala, revivir con 3 o más jugadores e instalar desde el
zip en un juego sin mods y jugar por internet.

Limitación conocida: si un dato cambiado en otro PC hace aparecer a alguien que el juego destruyó al cargar la sala
(otro sitio de Mr. Mushroom), no aparece hasta volver a entrar (lo destruido no se puede recuperar).

## Cómo se trabaja (recordatorio)

- Copias del juego: B/C (pruebas sueltas), D/E (rutas automáticas), F/G (pruebas de M3/Coliseo), H/I (prueba del
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
