# Revisión contra el juego descompilado (12/10/2026)

Lista de fallos y mejoras que salieron al revisar todo el mod (con la 0.25.0, commit `84fff73`) contra el código del
juego en `decomp/`, en tres pasadas:

- Primera: datos del mundo y del personaje, ganchos y reflexión, enemigos (lo que falta por sincronizar), objetos de
  sala, jugador y funciones del coop, y rendimiento y red.
- Segunda: la lógica interna de `EnemySync`, FSM compartidas y misiones, sesión (unirse, salir, reconectar), skins,
  interfaz y ajustes, e instalador.
- Tercera: las FSM de las 481 salas del juego (`decomp/fsm`, volcadas con `tools/dump-fsms.sh` y cruzadas con
  `tools/analyze-fsms.py`): jefes y enemigos, y objetos, plataformas, misiones y datos de la partida que escriben. Sus
  hallazgos van en "Del volcado de FSM" de cada sección, y lo que confirmó o descartó de las anteriores, en una línea
  **Volcado de FSM** bajo cada entrada.

Lo que **no** cubre: cómo se siente cada combate jugando entre dos personas, y lo que solo pasa con dos PCs reales.

**Estado en la 0.26.0:** 171 de 172 cerradas (arregladas o revisadas con su motivo). Queda abierta en parte J8 (efectos
del caballero que no se ven en el compañero).

**Pruebas (12/10/2026):** 82 entradas probadas en el juego con dos copias (línea **Probado en el juego**) y 16 más
comprobadas con las rutas, NetTests o la prueba del instalador (línea **Comprobado**). Al probar salieron 12 fallos, ya
arreglados: el tranvía de las estaciones, las crías que no pasaban de PC, el despertar pendiente en el traspaso, la
espera de una arena con zona fina, el cuerpo del caído mirando al revés, la barra de vida del Falso Caballero, la marca
"ya muerto" pegada en la marioneta, los tótems (no se comparten: semipersistentes), el mod puesto a mano que el
desinstalador no devolvía, la espera de los jefes, que ahora no tiene límite, el guardado del propio juego que se
enviaba dentro de un Panteón (S5) y el encuentro que se quedaba esperando el diálogo de quien lo controla si caía a
mitad (F11). Seis entradas tienen una línea **No se puede reproducir aquí** con el motivo. Lo que queda sin probar en el
juego es lo que no se puede preparar con las copias (tres jugadores, el juego como administrador, Lumafly/Scarab,
mandos) o es interno (rendimiento, carreras de red); está en las entradas sin esas líneas.

Cómo leerla:

- **Comprobado**: se miró en el código del mod y del juego que pasa así. **Sin comprobar**: lo encontró la revisión
  leyendo el código, pero falta mirarlo a mano o en el juego antes de arreglarlo.
- Prioridad: **alta** (rompe una partida o da ventaja/pierde progreso), **media** (se nota jugando), **baja** (raro,
  cosmético o de mantenimiento).
- Las líneas son las de ese commit; pueden moverse al ir arreglando. Las del juego son de `decomp/Assembly-CSharp/`.
- Al arreglar uno: marcar `[x]`, poner la versión y pasar lo que cambie a `arquitectura.md`.

## Orden sugerido

1. Alta y comprobados: J1, E1, E15, S1, W1, W2, I1.
2. Media y comprobados: W3, E3, E6, E8, E13, E22 con E32, E28, E30, O1, O2, H1, N1, P1, F1, F4.
3. Los sin comprobar de prioridad alta o media, mirándolos primero: E2, E16, E17, E18, H2, E4, E5, E11, E29, J2, J3,
   J4, J5, W4, W5, W6 con W13, O3, O19, O20, N2, N3, N4, N5, P2, P3, F2, F3, S2, S3, K1, K2, U1, U6, I2.
4. El resto (baja y notas).

Son 172 entradas (3 ya descartadas con el volcado: H6, O15 y W12): enemigos 32, jugador 16, mundo 16, ganchos 9,
objetos de sala 23, rendimiento 6, red 9, FSM y misiones 12, sesión 10, skins 14, interfaz 9, instalador 16 (más lo
revisado que está bien, al final). Las descartadas quedan marcadas `[x]` con el motivo.

---

## Enemigos (E)

- [x] **E1 · alta · comprobado.** El daño que no pasa por `HealthManager.Hit` no llega al PC que mueve al enemigo.
  - Flukenest: `SpellFluke.DoDamage` resta la vida directamente y llama a `Die` (juego `SpellFluke.cs:80-97`).
  - Spore Shroom, Defender's Crest y nubes de estiércol: `DamageEffectTicker` → `ExtraDamageable` →
    `HealthManager.ApplyExtraDamage` → `SubtractHealth` y `Die` (juego `DamageEffectTicker.cs:22-41`,
    `HealthManager.cs:543-551`).
  - `KillOnContact` llama a `Die` directamente (juego `KillOnContact.cs:10`).
  - El mod solo engancha `Hit` ([EnemySync.cs:5411](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5411)) y
    `ExtraDamageable` se deja encendido en las marionetas (`KeepOnPuppet`, [EnemySync.cs:189-191](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L189)).
  - Consecuencia: contra un enemigo que mueve el otro PC, ese daño se pierde. La vida de la marioneta baja solo en
    este PC y puede morir solo aquí (geo y diario aquí, el enemigo sigue vivo en el otro). En el PC que lo mueve, ese
    daño tampoco pasa por la vida extra de los jefes (`Difficulty` solo mira `TakeDamage`).
  - Arreglo: enganchar `ApplyExtraDamage` y `SpellFluke.DoDamage` (o `SubtractHealth`) al cargar el mod; en una
    marioneta, enviar la cantidad al PC que la mueve como un golpe más y no restar aquí. Aplicar ahí el multiplicador
    de `Difficulty`.
  - **Arreglado en 0.26.0:** Mensaje 108 (`EnemyDamage`): `ApplyExtraDamage` y `SpellFluke.DoDamage` se miden con la vida subida; en una marioneta van a quien la mueve y en un enemigo real se aplican con la vida extra (`Difficulty.ScaleDamage`).
  - **Probado en el juego (12/10/2026):** Defender's Crest del invitado contra un Husk que mueve el host: la vida baja en el PC del host (15 → 14 → 8 → 1).
- [x] **E2 · alta · sin comprobar.** Una marioneta que muere en este PC no se corrige nunca.
  - `UpdatePuppets` y `Update` la saltan ([EnemySync.cs:1176](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1176),
    [:816](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L816)), `AssignControllers` también
    ([:2319](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2319)) y la foto del dueño de la sala la manda como
    `AlreadyDead` a quien entra ([:886-891](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L886)) aunque en el PC que la
    mueve siga viva.
  - Arreglo: además de E1, que una marioneta no pueda llegar a `Die` en local (prefijo en `Die` que lo impida salvo
    cuando lo pide `ApplyHealth(Died)`).
  - **Arreglado en 0.26.0:** Gancho a `HealthManager.Die`: una marioneta no muere en local, se avisa a quien la mueve (`DamageKind.Kill`).
  - **Probado en el juego (12/10/2026):** El mismo Husk muere en los dos.
- [x] **E3 · media · comprobado.** Los Tiktik (`Climber`) vuelven a andar solos tras cada golpe a la marioneta.
  - `HitWithoutDamage` ([EnemySync.cs:5292](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5292)) hace el
    `TakeDamage` completo, que llama a `recoil.RecoilByDirection` (juego `HealthManager.cs:448-451`). Con
    `freezeInPlace`, `Recoil.Freeze` dispara `OnHandleFreeze` (juego `Recoil.cs:166-176`), que `Climber.Start` enganchó a
    `Stun` (juego `Climber.cs:78-83`), y `DoStun` acaba en `StartCoroutine(Walk())` (juego `Climber.cs:198-212`): las
    corrutinas corren aunque el componente esté apagado. Es el fallo de "van a saltos" de la 0.23.0.
  - Lo mismo con el aguijón onírico: `EnemyDreamnailReaction.RecieveDreamImpact` llama a `Recoil` (juego
    `EnemyDreamnailReaction.cs:70-75`).
  - En los que no se congelan, `Recoil` se queda en `Recoiling` con el componente apagado y retrocede al retomarlo.
  - Arreglo: en las marionetas, quitar `HealthManager.recoil` mientras dura `HitWithoutDamage` y el golpe onírico (o
    volver a parar sus corrutinas justo después).
  - **Arreglado en 0.26.0:** Gancho a `Recoil.RecoilByDirection`: no se hace en marionetas (su posición ya llega, retroceso incluido).
  - **Probado en el juego (12/10/2026):** `Crossroads_01`: el Tiktik golpeado por el invitado sigue al real (1-2 unidades de retraso de dibujo) y la vida coincide.
- [x] **E4 · media · sin comprobar.** Un aguijonazo del compañero a un enemigo con escudo hace retroceder al caballero
  del PC que lo mueve.
  - `OnHit` ([EnemySync.cs:5209-5238](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5209)) llama a `Health.Hit`, y
    `HealthManager.Invincible` hace `HeroController.instance.RecoilLeft/Right`, `FreezeMoment(1)` y temblor de cámara
    (juego `HealthManager.cs:359-372`).
  - Afecta a los Baldur, caballeros de musgo con escudo, centinelas pesados y jefes invulnerables.
  - Arreglo: mientras se aplica un golpe de otro (`_applyingRemoteHit`), no hacer el retroceso, la congelación ni el
    temblor.
  - **Arreglado en 0.26.0:** Al aplicar un golpe de otro PC, `SetPreventInvincibleEffect(true)` mientras dura.
  - **Probado en el juego (12/10/2026):** Baldur de `Fungus1_28`: con los golpes del invitado el host no se mueve; con el suyo propio, sí rebota.
- [x] **E5 · media · sin comprobar.** Los golpes que llegan mientras se traspasa un enemigo se pierden.
  - `OnHit` descarta el golpe si es marioneta ([EnemySync.cs:5222](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5222));
    el que lo suelta ya es marioneta ([:2560-2563](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2560)) y el nuevo lo es
    hasta que llega el traspaso ([:822-827](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L822)). Quien golpeó ya
    cobró el alma y vio el impacto.
  - Arreglo: guardarlo como `KeepPendingEvent` y aplicarlo en `TakeOver`, o pasarlo al que lo mueve ahora.
  - **Arreglado en 0.26.0:** El PC que va a tomarlo guarda los golpes (`PendingHits`, 3 s) y los aplica en `TakeOver`.
  - **No se puede reproducir aquí (12/10/2026):** el momento es demasiado justo para provocarlo a mano.
- [x] **E6 · media · comprobado.** El estado del componente `Walker` no se traspasa.
  - Guarda `state` y `currentFacing` privados y pone la velocidad según `currentFacing` (juego `Walker.cs:151-169`,
    `338-367`). Al retomarlo sigue con lo que tenía antes de ser marioneta: anda de espaldas, gira de golpe o anda
    cuando su FSM lo paró.
  - Afecta a los enemigos que usan las acciones `StartWalker`, `StopWalker` y `SetWalkerFacing`.
  - Arreglo: al retomarlo, recalcular `currentFacing` por la escala (`ChangeFacing` o reflexión) y llamar a `Stop` o
    `StartMoving` según el traspaso.
  - **Volcado de FSM:** `Walker` está en 107 salas; p. ej. `Ruins Sentry` de `Abyss_02` hace `StopWalker` en `Init Chase`, `StartWalker` en `Alert End` y `SetWalkerFacing` en `Check Facing`. El mod solo para y rearranca `Climber` y `Crawler` ([EnemySync.cs:4856-4889](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L4856)).
  - **Arreglado en 0.26.0:** El traspaso lleva el estado del `Walker` (estado, motivo de parada, hacia dónde mira, giro y tiempos) y se aplica al tomarlo.
  - **Probado en el juego (12/10/2026):** un centinela de `Ruins1_01` patrullando pasa al invitado y sigue andando a la misma velocidad y en el mismo sentido, con posiciones seguidas.
- [x] **E7 · baja · sin comprobar.** Si la marioneta ya está en el estado traspasado no se aplica `KeepWaited`
  ([EnemySync.cs:2841-2862](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2841)): la espera se queda en la de cuando
  se pausó (lo que se arregló para el Rey Vengamosca no vale si los dos están en el mismo estado). Aplicarlo siempre.
  - **Arreglado en 0.26.0:** `KeepWaited` también si la marioneta ya estaba en ese estado.
- [x] **E8 · baja · comprobado en parte.** `MuteSpawns` ([EnemySync.cs:3404-3411](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3404))
  solo calla `SpawnObjectFromGlobalPool`, `CreateObject` y `FlingObjectsFromGlobalPool`. Faltan
  `FlingObjectsFromGlobalPoolVel` (el vómito de los Ogros Hongo), `FlingObjectsFromGlobalPoolTime`,
  `SpawnObjectFromGlobalPoolOverTime*`, `SpawnRandomObjects*`, `FlingObject(s)` y `CreateObjectPreInst`: retomar a
  mitad de ese estado puede repetir el ataque.
  - **Volcado de FSM:** Lo que se repite de verdad es `FlingObjectsFromGlobalPoolVel` (lanza en `OnEnter`): `Spitting Zombie [Spit]` (`Crossroads_04/08/15/40`), `Lesser Mawlek` (`Abyss_17`), `Mushroom Brawler` (`Fungus2_05/29/30`), Nosk (`Deepnest_32`) y el Pale Lurker; al retomarlo se lanza otra vez y `_flinging` envía la segunda ráfaga como copias. Se quitan de la entrada: `FlingObjectsFromGlobalPoolTime` y `SpawnObjectFromGlobalPoolOverTime*` (crean en `OnUpdate`, van en E28), `FlingObject(s)` (solo dan velocidad), `CreateObjectPreInst` (no sale en el juego) y `SpawnRandomObjects` en jefes (solo visual).
  - **Arreglado en 0.26.0:** `MuteSpawns` calla también `FlingObjectsFromGlobalPoolVel`.
- [x] **E9 · baja · sin comprobar.** El aviso de muerte no lleva tipo de ataque ni dirección: `ApplyHealth` usa
  `Die(null, AttackTypes.Nail, true)` ([EnemySync.cs:5131-5138](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5131)).
  Las muertes por `Splatter` (no sueltan geo) o `RuinsWater` (sin geo), y las de ácido, agua o hechizo (otro cadáver),
  salen distintas en el PC de la marioneta (juego `HealthManager.cs:605-614`, `676-682`).
  - **Arreglado en 0.26.0:** La muerte lleva el tipo de ataque (en el número que iba a 0) y el otro PC muere con él.
  - **Probado en el juego (12/10/2026):** matado con `Spell` desde el PC que no lo mueve, el otro registra "murió en el PC que lo movía (Spell)".
- [x] **E10 · baja · sin comprobar.** El PC que mueve al enemigo aplica su ventana de 0,2 s sin daño tras un golpe
  (juego `HealthManager.cs:334`, `539`): los golpes del compañero que rechaza ya le dieron alma y efectos en su PC.
  - **Arreglado en 0.26.0:** Antes de aplicar el golpe de otro PC se pone a 0 la ventana sin daño (`evasionByHitRemaining`).
  - **Probado en el juego (12/10/2026):** Dos golpes seguidos del invitado (3 + 3) bajan la vida de 15 a 9 en el host.
- [x] **E11 · media · comprobado en código y volcado.** Doble daño de cosas del mundo que pasan en los dos PCs: el gancho de `Hit`
  reenvía cualquier golpe a una marioneta, no solo los del caballero local. Candidatos: la explosión del cadáver del
  Gruzzer volátil (si tiene `damages_enemy`), estalactitas, ácido. Arreglo: reenviar solo los golpes cuyo `Source` es
  del caballero local, sus hechizos o sus acompañantes.
  - **Volcado de FSM:** `damages_enemy` (→ `HitTaker.Hit` → `HealthManager.Hit`) está en `Acid Box` (83 salas), pinchos (`Cave Spikes`, `Deep Spikes`, `Thorn Collider`), `Gas Explosion M2` del `Ceiling Dropper`, `Bursting Zombie`, `Enemy Crusher` de los ascensores y `Colosseum Spike`, con `Trigger2dEvent` `OnTriggerStay2D` en la capa de enemigos. Falta verlo en el juego.
  - **Arreglado en 0.26.0:** No se reenvían a quien mueve una marioneta los golpes del mundo (`IsWorldHazard`: ácido, salpicadura, agua de la Ciudad y genéricos de 50 o más).
  - **Probado en el juego (12/10/2026):** un golpe de ácido desde el PC que no lo mueve no baja su vida (15 → 15); uno de aguijón sí (15 → 11).
- [x] **E12 · baja · sin comprobar.** El aguijón onírico a una marioneta hace `Recoil` sin `_replaying`, que manda
  `RECOIL HORIZONTAL`, `HIT LEFT/RIGHT/UP/DOWN` y `FREEZE IN PLACE`; el gancho de `ProcessEvent` los pasa al PC que lo
  mueve ([EnemySync.cs:5464-5491](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5464)) y su FSM puede girarse o
  encogerse.
  - **Arreglado en 0.26.0:** Cubierto por E3: sin `RecoilByDirection` en la marioneta no salen esos avisos.
  - **Comprobado (12/10/2026):** Con la prueba de E3: golpes del invitado al Tiktik sin que la marioneta se gire ni ande sola.
- [x] **E13 · baja · comprobado.** `EnemySpawner` tira su `spawnChance` en cada PC por separado (juego
  `EnemySpawner.cs:46-67`): el enemigo puede existir solo en uno y su control caer en el PC donde no está.
  - **Volcado de FSM:** `EnemySpawner` es el `Buzzer Summon` del Rey Vengamosca (`Summon`: `CreateObject`, en `Fungus1_20_v02` y la oleada 15 de `Room_Colosseum_Bronze`). No es de juego, así que `OnSpawned` no lo sigue y la marioneta lo crea como efecto (`ShouldRunEffect`, [EnemySync.cs:1849](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1849)): cada PC tira su 75 % y tiene sus propias mosquitas (`NoteEnabled` solo atiende tarros, [:1393](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1393)). En `GG_Vengefly` salen de `PreSpawnGameObjects` + `GetNextPreSpawnedGameObject`, solo en el PC que mueve al jefe y repetidas si se retoma en `GG Summon`. Igual las crías del cadáver de `Zom Hive` (`SpawnRandomObjects Bee Hatchling`).
  - **Arreglado en 0.26.0:** `EnemySpawner` cuenta como de juego: solo lo crea el PC que mueve al jefe y lo que suelta va a los demás como creado por la misma acción (con el prefab del enemigo). Falta la variante de `GG_Vengefly` (`PreSpawnGameObjects`).
  - **Probado en el juego (12/10/2026):** con el Rey Vengamosca en `Summon`, las mismas 5 mosquitas (mismos ids) en los dos: el azar se tira una vez.
- [x] **E14 · baja · decisión pendiente.** Lifeseeds (`ScuttlerControl`): huyen del caballero local, mueren solo en el PC
  de quien los golpea y cada jugador puede cobrar su savia (juego `ScuttlerControl.cs:189-232`). Decidir si es de cada
  uno (como el geo) o se comparte.
  - **Volcado de FSM:** Lifeseeds `Orange Scuttler` en `Abyss_19/20` y `Crossroads_14/43`; `Health Scuttler` de capullo en 8 salas. El capullo ya se rompe en todos y cada PC suelta los suyos: lo coherente es que sean de cada uno, como el geo.
  - **Revisado en 0.26.0:** Decisión: los Lifeseeds son de cada jugador, como el geo (ya es así). No se cambia nada.

Segunda pasada (lógica interna de `EnemySync`):

- [x] **E15 · alta · comprobado.** Un jefe que espera a despertar puede quedarse invulnerable para siempre.
  `HoldWake` pone `Health.IsInvincible = true` mientras espera ([EnemySync.cs:3097-3102](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3097)),
  `SendHandover` lo envía como parte de su aspecto (`Capture(...).Extra`, [:2666-2668](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2666))
  y `MatchPrevious` lo aplica en el PC que lo toma ([:2927-2930](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2927)).
  Allí `WaitingWake` es falso, así que `WakeUp` no lo devuelve ([:3108-3114](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3108));
  y si vuelve a esperar, apunta `WasInvincible = true`. Caso: A llega a Mawlek, sale "Esperando a B", y A sale de la
  sala o pone la pausa: B toma al jefe invulnerable y no se le puede vencer hasta recargar la sala. Arreglo: con
  `WaitingWake`, enviar `WasInvincible` en lugar del valor de ahora (o quitar la bandera en `MatchPrevious` si el jefe
  no está despierto).
  - **Arreglado en 0.26.0:** En el traspaso, con `WaitingWake`, se envía `WasInvincible` en lugar de la invulnerabilidad de la espera.
  - **Probado en el juego (12/10/2026):** Mawlek pasa al invitado dormido y, al despertarlo, se le puede dañar (vida igual en los dos). Salió otro caso: si el jefe cambia de PC mientras espera a que lleguen todos, el nuevo no seguía la espera; ahora el traspaso la lleva (probado: "sigue esperando ... (lo esperaba el otro PC)" y despierta al acercarse).
- [x] **E16 · media · sin comprobar.** El PC que toma un jefe a mitad del combate cree que su estado de ahora es el de
  dormido. `NoteDormant` ([EnemySync.cs:3054-3075](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3054)) solo pide
  `!Puppet && !Awake && DormantState == null`, y `Awake`/`DormantState` no van en el traspaso. Tras `TakeOver`, el
  siguiente cambio de estado pasa por `HoldWake`: si alguien está a más de 25 (y no es una arena con zona), el jefe se
  para invulnerable a mitad del combate con "Esperando a X". Arreglo: `Awake` en el traspaso (o `Awake = true` al
  tomarlo si la vida no está al máximo) y `NoteDormant` solo para enemigos que este PC lleva desde que cargó la sala.
  - **Arreglado en 0.26.0:** El traspaso lleva si el jefe ya despertó; al tomarlo, `Awake = true`.
  - **Comprobado (12/10/2026):** Con la prueba de E15: el jefe que se traspasa ya despierto sigue peleando y el que se traspasa dormido espera.
- [x] **E17 · media · sin comprobar.** El estado de marioneta se queda en el pool. En `UpdateReal`
  ([EnemySync.cs:870-878](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L870)) una copia creada que se apaga o destruye
  solo se olvida (`Forget`), sin `SetPuppet(false)` (comparar con `RemoveSpawned`, [:3853](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3853)).
  `EnemyBullet` se recicla solo aunque esté apagado (juego `EnemyBullet.cs:74-131`): la bala vuelve al pool apagada,
  con sus FSM paradas, `RestartOnEnable = false` y cuerpo cinemático ([:4783-4813](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L4783)),
  y la próxima vez que este PC la saca (de un enemigo suyo) sale rota: sin gravedad y sin reciclarse. Arreglo:
  `SetPuppet(enemy, false)` antes de `Forget` en ese caso.
  - **Arreglado en 0.26.0:** Una copia que vuelve sola al pool vuelve antes a no ser marioneta (`SetPuppet(false)`).
- [x] **E18 · media · sin comprobar.** El traspaso a un PC que acaba de entrar se pierde. `OnHandover` lo descarta si
  el enemigo no es marioneta ([EnemySync.cs:2724](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2724)) y `Update` solo
  retoma marionetas ([:822](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L822)); los enemigos de quien acaba de entrar
  no son marioneta hasta 0,5 s después ([:828](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L828)). Si el dueño se lo
  da en ese rato, ese PC sigue con lo recién cargado (sitio de salida, estado inicial): la marioneta del otro salta al
  inicio y un jefe a mitad del combate empieza de nuevo. En esos 0,5 s además envía el estado de todos sus enemigos y
  nadie mira quién lo envía ([:959-999](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L959), [:5085](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5085)).
  Arreglo: guardar el traspaso aunque no sea marioneta y aplicarlo al tomarlo; o que el dueño no dé enemigos a quien
  entró hace menos de ~1 s; y aceptar estados solo de `ControllerOf(enemy)`.
  - **Arreglado en 0.26.0:** Se guarda el traspaso aunque aún no sea marioneta (recién llegado) y se toma; los estados solo se aceptan del PC que mueve al enemigo.
  - **Probado en el juego (12/10/2026):** En parte: el Tiktik pasa al invitado nada más entrar y los dos coinciden; su sitio inicial estaba cerca, así que no distingue si siguió desde el traspaso.
- [x] **E19 · baja · sin comprobar.** Al cerrar la sesión quedan vivas las copias creadas por otros: `Detach` quita
  la marioneta a todo ([EnemySync.cs:695-697](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L695)) antes de `Clear()`,
  y `RemoveSpawnedCopies` solo quita `Spawned && Puppet` ([:3894](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3894)),
  que ya no hay. Proyectiles, crías y bichos del Coleccionista pasan a ser objetos de verdad; las crías de la Colmena
  se quedan fuera de su jaula. Arreglo: `RemoveSpawnedCopies()` antes de ese bucle.
  - **Arreglado en 0.26.0:** `Detach` quita las copias creadas por otros antes de devolver la lógica a las marionetas.
  - **Probado en el juego (12/10/2026):** Al salir de la sesión, el invitado quita las copias de las crías de la Aspid (4 fuera de la jaula → 0).
- [x] **E20 · baja · sin comprobar (3+ jugadores).** Un traspaso viejo se puede usar: B guarda el de A
  (`HandoverReady`), el dueño toma el enemigo y `ApplyControl` sale antes si no cambia ([:2548](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2548)),
  así que no se borra; más tarde, un cambio P→B que llega antes que el traspaso de P hace que B lo tome con el estado
  de hace minutos. Arreglo: borrar `Handover`/`HandoverReady` en cada cambio y apuntar de quién y cuándo es.
  - **Arreglado en 0.26.0:** `ApplyControl` tira el traspaso guardado si el nuevo controlador no es este PC, también cuando el control no cambia según este PC.
  - **No se puede reproducir aquí (12/10/2026):** hacen falta 3 jugadores o más.
- [x] **E21 · baja · sin comprobar.** `PendingEvents` solo se vacía en `TakeOver`: el reenvío en ping-pong
  ([:4032](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L4032)) puede llenar sus 8 huecos en un PC que nunca lo toma, y
  luego se descartan los nuevos ([:3937](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3937)). Quitar los viejos antes de
  mirar el límite.
  - **Arreglado en 0.26.0:** `KeepPendingEvent` quita los viejos antes de mirar el límite.
- [x] **E22 · media · comprobado.** `ZeroHp` no se pone nunca a falso ([EnemySync.cs:5467-5470](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5467)).
  Un jefe cuya FSM atiende `ZERO HP` sin morir (cambio de fase con `hasSpecialDeath`, juego `HealthManager.cs:563-576`)
  quedaría como `Dying`: su control queda fijo en ese PC aunque el jugador caiga o pause ([:2340](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2340)),
  y cada estado se envía como "ya muerto allí", así que las marionetas repiten sus avisos a todos como si hubiera
  muerto ([:2163](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2163)).
  - **Volcado de FSM:** Atienden `ZERO HP` y siguen vivos: el Falso Caballero (`[Check Health]` en `Crossroads_10` y `GG_False_Knight`: `Check` → `Stun` con `SetHP {Recover HP}` en cada armadura rota; desde la primera queda `Dying` todo el combate, no cambia de PC y la marioneta repite sus avisos a todos, `FK RAGE COMPLETE` en `Rage End`); Sly (`GG_Sly`: `Death Reset` → `Death Stun` → `SetHP {Rage HP}`); Oro y Mato (`GG_Nailmasters`: `Death Start` → ... → `Reactivate` con `SetHP {P2 HP}`); `Zote Balloon`. El arreglo tiene que respetar la muerte real del Falso Caballero (E32).
  - **Arreglado en 0.26.0:** `ZeroHp` se quita si la vida vuelve a subir de 0 (0,5 s después): era un cambio de fase.
  - **Comprobado (12/10/2026):** Con la pelea del Falso Caballero (E32): cada armadura deja "ZERO HP era un cambio de fase" y el control sigue repartiéndose.
- [x] **E23 · media · comprobado.** Los enemigos creados en combate nunca cambian de PC ([:2319](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2319)):
  si quien los creó cae o pone la pausa, sus crías, las de la Colmena y los bichos de los tarros se quedan quietos o
  persiguiendo al caído, y en los demás PCs son copias que no hacen nada. El doc dice que "pasan enseguida a otro".
  - **Volcado de FSM:** Ejemplos: `Baby Centipede` del `Centipede Hatcher` (`Deepnest_26`/`26b`) y los bichos del Coleccionista (`Ruins2_11_boss`).
  - **Arreglado en 0.26.0:** Los creados en combate con vida pasan a otro si quien los mueve está caído o en pausa (los proyectiles siguen con quien los creó).
  - **Probado en el juego (12/10/2026):** Aspid Mother en `Crossroads_19`: al pausar el host, sus crías pasan al invitado. Al probarlo salió que el invitado ignoraba el cambio (los creados en combate no se aceptaban al recibir): arreglado, ahora las 7 pasan y los dos PCs coinciden.
- [x] **E24 · baja · sin comprobar.** Si el pool recicla un objeto y lo vuelve a sacar en el mismo frame antes de que
  `UpdateReal` lo note, `OnSpawned` lo apunta otra vez ([:3483-3486](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3483)):
  dos entradas con la misma raíz y dos copias en el otro PC.
  - **Arreglado en 0.26.0:** Si el objeto ya estaba apuntado, la entrada vieja se acaba (con su aviso) antes de apuntarlo otra vez.
- [x] **E25 · baja · sin comprobar.** La espera para despertar no tiene tiempo máximo ([:3136-3142](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3136)):
  si falta el avatar de un compañero (`TryGetAvatarObject` falso, [:3183](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3183))
  o está lejos en pausa, el jefe espera invulnerable sin fin.
  - **Arreglado en 0.26.0:** La espera para despertar dura como mucho 60 s.
  - **Probado en el juego (12/10/2026):** Cambiado a petición: sin límite de un minuto. Con el invitado lejos y en pausa, Mawlek espera más de un minuto (76 s) y despierta cuando llega; no se espera a quien está caído, en otra sala o sin avatar.
- [x] **E26 · baja · sin comprobar.** `ApplyHealth(Died)` quita la marioneta al enemigo que muere ([:5133](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5133)):
  su FSM de muerte avisa de lo que saca del pool (sin la guarda de "preparación") y el PC que lo mató lo vuelve a
  crear (el id 0 vale para un creador que no es marioneta, [:3713](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3713)):
  efectos de muerte dobles.
  - **Arreglado en 0.26.0:** Un enemigo que murió en otro PC (`DiedElsewhere`) no envía lo que saca del pool su muerte.
  - **Probado en el juego (12/10/2026):** muerte de la Madre Gruz en el PC que la mueve: sus crías son las mismas (mismos ids) en los dos, sin copias de más.
- [x] **E27 · baja · sin comprobar.** Las copias de las crías de la Colmena vuelven a la jaula sin reponer su vida
  (`ReturnToCage`, [:3635-3654](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3635)): si después este PC suelta esa cría
  como original, envía a todos su vida vieja (quizá baja).
  - **Arreglado en 0.26.0:** La jaula guarda su vida al cargar la sala y la cría vuelve con ella.
  - **Probado en el juego (12/10/2026):** una cría de la Aspid con 2 de vida muere y vuelve a la jaula con 5 en los dos PCs.

Del volcado de FSM (`decomp/fsm`):

- [x] **E28 · media · comprobado.** Los disparos que se crean "con el tiempo" no llegan al otro PC.
  `FlingObjectsFromGlobalPoolTime` y `SpawnObjectFromGlobalPoolOverTime(V2)` crean en `OnUpdate` con `ObjectPool.Spawn`,
  y el gancho del pool solo avisa con `_flinging` puesto, que solo ponen los `OnEnter` de
  `FlingObjectsFromGlobalPool(Vel)` ([EnemySync.cs:5387-5408](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5387)); la
  marioneta tampoco las hace (no están en `EffectActions`, [:1803](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1803)).
  Afecta al Hollow Knight (`SmallShot HighLow/LowHigh`, `Shot HK Glob/Shadow`; `Room_Final_Boss_Core`,
  `GG_Hollow_Knight`), Nosk (`Roof Drop`; `Deepnest_32`, `GG_Nosk`), `Fungus Flyer [Spit]` (`Gas Projectile`;
  `Fungus2_03/06/08/11/29`), `Mushroom Roller` (`Roll`/`In Air`; `Fungus2_07/23/28/29`) y el `Flame Trail` de Grimm de la
  Pesadilla: solo existen en el PC que mueve al enemigo y el compañero ni los ve ni le dañan. Arreglo: enganchar el
  `OnUpdate` de esas tres clases poniendo `_flinging` y añadirlas al `switch` de `OnSpawn`
  ([:3726-3748](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3726)).
  - **Arreglado en 0.26.0:** Ganchos a `OnUpdate` de `FlingObjectsFromGlobalPoolTime` y `SpawnObjectFromGlobalPoolOverTime(V2)` con `_flinging`, y sus casos en `OnSpawn`.
  - **Probado en el juego (12/10/2026):** Fungus Flyer en `Fungus2_03`: las 48 bolas de gas del host salen igual en el invitado.
- [x] **E29 · media · sin comprobar.** Un traspaso a mitad de un rugido deja al caballero sin control. Hay 53
  `SendEventByName {Hero} ROAR ENTER/EXIT` en jefes (Infected Knight `Roar` → `Roar End` en `Abyss_19`; Rey Vengamosca
  `Roar Left` → `Summon` → `Roar End`). `Roar Lock` del caballero solo sale de `Lock Grounded/Air` con `ROAR EXIT` o al
  salir de un colisionador "Roar". Si el jefe cambia de PC entre los dos estados (caído, pausa, reparto), el `ROAR EXIT`
  lo manda el nuevo a su caballero, la marioneta no avisa al caballero ([EnemySync.cs:1865-1895](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1865))
  y el del PC que lo soltó se queda congelado. Arreglo: al soltarlo (`SendHandover`, [:2584](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2584))
  o al llegar a la marioneta un estado con `ROAR EXIT`, mandar `ROAR EXIT` al caballero local si `Roar Lock` está en
  `Lock*`.
  - **Arreglado en 0.26.0:** Al soltar un jefe que manda `ROAR ENTER`, si `Roar Lock` está en `Lock*`, se manda `ROAR EXIT` al caballero local.
  - **Probado en el juego (12/10/2026):** Mawlek: con el caballero del host bloqueado por el rugido, al pausar se suelta el jefe y el host recupera el control.
- [x] **E30 · media · comprobado.** Las púas de Hornet en los Bordes del Reino solo salen en un PC.
  `Hornet Boss 2 [Control] Barb Throw` avisa a todos con `SPAWN3`; `Barb Region [Spawn Barbs]`
  (`Deepnest_East_Hornet`, `GG_Hornet_2`), que no es enemigo ni tiene `DamageHero`, crea `Hornet Barb` (con
  `DamageHero`) con `SpawnObjectFromGlobalPool`. `AddSatellites` ([EnemySync.cs:4206](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L4206))
  solo adopta lo que escucha un aviso a todos si tiene `DamageHero` en sus hijos, y `OnSpawned` exige el creador en
  `_enemyFsms` ([:3461](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3461)): en el PC que la mueve las púas no se siguen
  y en los demás no hay. Arreglo: en `AddSatellites`, aceptar también lo que escucha el aviso si `SpawnsGameplay(fsm)`.
  - **Arreglado en 0.26.0:** `AddSatellites` acepta lo que escucha un aviso a todos si crea cosas de juego (`SpawnsGameplay`).
  - **Probado en el juego (12/10/2026):** Hornet en `Deepnest_East_Hornet` (con `Barb Throw` forzado): 6 púas en el host y 6 copias en el invitado, en los mismos sitios.
- [x] **E31 · baja · comprobado.** Radiancia: la reaparición tras pinchos del compañero no sube con la pelea.
  `Radiance [Control] Climb Plats1` (`Dream_Final_Boss`, `GG_Radiance`) hace `CallMethodProper {Hero} SetHazardRespawn` en
  `P2A Hazard` y la marioneta no hace `CallMethodProper`. `GG_Radiance` no tiene `HazardRespawnTrigger` de subida
  (`Dream_Final_Boss` sí, `Ascend Respawns`): si el compañero cae antes de tocar uno, reaparece en el punto viejo.
  Arreglo: que la marioneta haga los `SetHazardRespawn` al caballero local, como `FsmStateSync` ([FsmStateSync.cs:1182](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1182)).
  - **Arreglado en 0.26.0:** La marioneta hace también `CallMethodProper SetHazardRespawn` sobre el caballero local.
  - **No se puede reproducir aquí (12/10/2026):** la Radiance está apagada hasta la pelea, que empieza desde el Huevo Negro.
- [x] **E32 · media · comprobado.** El arreglo de E22 tiene que respetar la muerte del Falso Caballero. Hoy `ZeroHp` se
  pone en la primera armadura rota ([EnemySync.cs:5465-5470](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5465), `Handles`
  [:3986](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3986)) y de rebote cubre su `Cough` final, que va por la cabeza.
  Arreglo: quitar `ZeroHp` cuando la vida vuelve a subir de 0 (`SetHP` de la fase siguiente: `Stun` del Falso Caballero,
  `Death Stun` de Sly, `Reactivate` de Oro y Mato) y marcar la muerte real del Falso Caballero por el `ZERO HP` de
  `Head [Health Check]` en su última fase o por `FalseyControl` al entrar en sus estados de muerte.
- Descartados del volcado (no crean nada nuevo): `FireAtTarget` (solo da velocidad hacia el objetivo), `FireGrimmBall`
  (dispara una bola ya creada, que ya se sigue), `FlingObject(s)`, `CreatePoolObjects` (pool en `Init`),
  `SpawnRandomObjectsV2` (salpicaduras y fantasmas de golpe, visual) y `SetSpawnJarContents` (ya va con los tarros).
  `iTweenMoveBy` y `Tk2dWatchAnimationEvents` en enemigos se recalculan al volver a entrar en el estado.
  - **Arreglado en 0.26.0:** La muerte por la lógica se marca al entrar en un estado `Death*` o `Cough` (`DeathState`, no se quita).
  - **Probado en el juego (12/10/2026):** Pelea entera del Falso Caballero: cada armadura es un cambio de fase, el suelo se rompe en los dos, la muerte se marca en `Death Open` (desde ahí no hay vuelta atrás), sigue en el PC del host hasta `Cough` y `falseKnightDefeated` queda en los dos. Salió que la barra de vida del jefe seguía al morir y que la marioneta guardaba para siempre el ZERO HP de una fase: arreglados.

## Jugador y funciones del coop (J)

- [x] **J1 · alta · comprobado.** Un caído recupera los controles tras seguir al compañero a otra sala: puede lanzar
  hechizos y hacer dash invisible.
  - `FinishedEnteringScene` siempre llama a `AcceptInput()` (juego `HeroController.cs:4193-4200`) y
    `SetStartingMotionState` lo pone en `grounded`/`airborne`. Lo llama el mod al llegar
    ([Revive.cs:1261](../src/HKMultiplayer/Game/Features/Revive.cs#L1261)) y el propio juego al entrar por una puerta.
  - `ApplyHiddenState` solo llama a `RelinquishControl` si `!controlReqlinquished`
    ([Revive.cs:1351](../src/HKMultiplayer/Game/Features/Revive.cs#L1351)), y ese dato sigue en verdadero.
  - `CanCast`, `CanJump` y `CanDash` miran `acceptingInput` y `hero_state`, no `controlReqlinquished` (juego
    `HeroController.cs:3416`, `5276`, `5321`). Los hechizos dañan donde está el compañero y gastan alma.
  - Arreglo: en `ApplyHiddenState`, cada frame, `IgnoreInput()` si `acceptingInput` y forzar `no_input`.
  - **Arreglado en 0.26.0:** `ApplyHiddenState` llama cada frame a `IgnoreInput()` y pone `no_input`; `RegainControl` lo devuelve al revivir.
  - **Probado en el juego (12/10/2026):** El caído sigue al host a otra sala y sigue con `acceptingInput=False` y `no_input`; al revivir vuelve a `True`/`idle`.
- [x] **J2 · media · sin comprobar.** Un caído que entra siguiendo al compañero por una puerta de abajo se queda en
  `transitionState = DROPPING_DOWN` (juego `HeroController.cs:2899-2904`; solo lo acaba `OnCollisionEnter2D`, y su
  colisión está apagada). Si lo reviven en esa sala no recibe daño hasta cambiar de sala (`CanTakeDamage`,
  `HeroController.cs:5361`). Arreglo: en `RestoreVisibleState` ([Revive.cs:1381-1411](../src/HKMultiplayer/Game/Features/Revive.cs#L1381))
  y tras cada llegada, `transitionState = WAITING_TO_TRANSITION` y `cState.transitioning = false`.
  - **Arreglado en 0.26.0:** `RestoreVisibleState` pone `WAITING_TO_TRANSITION` y `transitioning = false` (si no se está cambiando de sala).
  - **Probado en el juego (12/10/2026):** Caído que entra por arriba siguiendo al host: al revivir queda en `WAITING_TO_TRANSITION` y recibe daño.
- [x] **J3 · media · sin comprobar.** El viaje por `dreamGate` mueve la marca real de la Puerta de los sueños.
  `SceneTravel` y `Revive` cambian `dreamGateX/Y` ([SceneTravel.cs:75-77](../src/HKMultiplayer/Game/Features/SceneTravel.cs#L75),
  [Revive.cs:1212-1214](../src/HKMultiplayer/Game/Features/Revive.cs#L1212)) y los reponen al llegar, pero si el destino
  es la sala de la marca (`dreamGateScene`), el juego la coloca en `dreamGateX/Y` al empezar la sala (juego
  `SceneManager.cs:524-528`). Un guardado a mitad del viaje también se los lleva. Arreglo: guardar y vaciar también
  `dreamGateScene` durante el viaje.
  - **Arreglado en 0.26.0:** `DreamGatePoint` (en `SceneTravel.cs`, lo usan los dos viajes) vacía también `dreamGateScene` durante el viaje y, al reponerlo, pone la marca como el juego si es de esa sala.
  - **Probado en el juego (12/10/2026):** "Ir con" no cambia la marca de la Puerta de los sueños del invitado.
- [x] **J4 · media · sin comprobar.** `DreamSync` se lleva al sueño (y saca de él) a un compañero en pausa, sentado en
  un banco o en un diálogo o tienda: solo mira cambio de sala, muerte, caído y si ya está en un sueño
  ([DreamSync.cs:307-310](../src/HKMultiplayer/Game/Features/DreamSync.cs#L307), [:428-430](../src/HKMultiplayer/Game/Features/DreamSync.cs#L428)).
  Un cambio de sala con el menú de pausa abierto lo deja colgado. Arreglo: las mismas negativas que `StagTogether`
  ([StagTogether.cs:132-139](../src/HKMultiplayer/Game/Features/StagTogether.cs#L132)).
  - **Arreglado en 0.26.0:** Entrar: no se lleva a quien está en pausa, sentado o sin control (diálogo, tienda). Salir con la pausa abierta: se sale al quitarla.
  - **Probado en el juego (12/10/2026):** Con el invitado en pausa, el host entra solo en el sueño ("no va (en pausa)"). Con los dos dentro y el invitado en pausa al ganar, este sale al quitarla.
- [x] **J5 · media · sin comprobar.** Un `atBench` que se queda puesto: los viajes de `SharedCutscenes`
  ([:362-391](../src/HKMultiplayer/Game/Features/SharedCutscenes.cs#L362)) y `DreamSync` sacan de la sala a quien está
  sentado sin levantarlo, y solo la FSM del banco quita `atBench`. Después "Ir con" lo rechaza siempre
  (`PartnerTravel.BusyError`, [:221](../src/HKMultiplayer/Game/Features/PartnerTravel.cs#L221)), revivir en el banco
  no salta (`Revive.UpdateBench` espera el paso de falso a verdadero, [:938-947](../src/HKMultiplayer/Game/Features/Revive.cs#L938))
  y el inventario se abre siempre. Arreglo: no llevar a quien está sentado, o quitar `atBench` al llegar.
  - **Arreglado en 0.26.0:** Los sueños ya no se llevan a quien está sentado (J4) y las cinemáticas compartidas quitan `atBench` al llevarlo.
  - **Probado en el juego (12/10/2026):** Con `atBench`, el invitado no va al sueño ("sentado en un banco").
- [x] **J6 · baja · sin comprobar.** `RestoreVisibleState` vuelve a encender todos los renderers que apagó
  ([Revive.cs:1382-1386](../src/HKMultiplayer/Game/Features/Revive.cs#L1382)), aunque el juego los hubiera apagado
  después: si cayó a mitad de Focus, un hechizo o un efecto de amuleto, pueden quedarse pegados. Además se apagan
  `heroLight` y `vignette` (juego `HeroController.cs:708-710`): el caído ve las salas oscuras sin oscuridad. Arreglo:
  ocultar y reponer solo el `MeshRenderer` del caballero y dejar la luz y la viñeta.
  - **En parte en 0.26.0:** la luz y la viñeta ya no se esconden (en la 0.26.0, con J1). Lo de los efectos que vuelven a encenderse sigue abierto.
  - **Arreglado en 0.26.0:** Se esconde con `Renderer.forceRenderingOff`, una marca que el juego no toca: lo que encienda o apague mientras tanto queda como lo dejó. `PlayerSync` mira también esa marca para decir si se ve.
  - **Probado en el juego (12/10/2026):** Al revivir, el host vuelve a verlo (`visibles=1`).
- [x] **J7 · baja · sin comprobar.** Se puede perder la recuperación de la sombra: `KillOwnShade`
  ([ShadeSync.cs:575-577](../src/HKMultiplayer/Game/Features/ShadeSync.cs#L575)) sale antes si no hay prefab o
  caballero (el dueño está en una cinemática o en el ciervo) y no pone `_ownSent = false` ([:593-594](../src/HKMultiplayer/Game/Features/ShadeSync.cs#L593)).
  El host ya la dio por muerta y el dueño no vuelve a avisar: la sombra desaparece para los demás y no devuelve el geo.
  Arreglo: poner `_ownSent = false` en un `finally` y reintentar más tarde.
  - **Arreglado en 0.26.0:** `KillOwnShade` espera (hasta un minuto) a tener caballero y sala y siempre vuelve a informar (`finally`).
  - **Probado en el juego (12/10/2026), en parte:** el invitado mata la sombra del host en otra sala y al host le vuelve el geo (777). El caso del dueño sin caballero no se puede preparar a mano.
- [ ] **J8 · baja · sin comprobar.** Efectos del caballero que no se ven en el compañero (todo lo que es su animación
  sí llega). Del juego (`HeroController.cs`):
  - escudo de Carefree Melody (`carefreeShield`, 2321);
  - golpe bloqueado por Baldur Shell (`HeroCtrl-TookBlockerHit`, 2330-2336; la FSM `Blocker Shield` no está en
    `ActionFsmNames`, [HeroEffectTemplates.cs:25](../src/HKMultiplayer/Game/Players/HeroEffectTemplates.cs#L25));
  - polvo al deslizarse por la pared (`wallslideDustPrefab`, 3364, 3740, 3753);
  - muerte en pinchos o ácido (`spikeDeathPrefab`, `acidDeathPrefab`, 4288-4298; no están en
    [CharmTemplates.cs:184-186](../src/HKMultiplayer/Game/Players/CharmTemplates.cs#L184)): el compañero desaparece sin más;
  - destello de la recarga del dash sombra (`shadowRechargePrefab`, 4091; falta en `PlayDash`,
    [RemoteEffects.cs:740-765](../src/HKMultiplayer/Game/Players/RemoteEffects.cs#L740));
  - golpe de 2 o más (`takeHitDoublePrefab`, 4342);
  - chispas del aguijón contra la pared (`nailTerrainImpactEffectPrefab`, 4959);
  - muerte de verdad (`heroDeathPrefab`, 4261);
  - su luz (`heroLight`, 708): en salas oscuras el compañero se pierde en la oscuridad si no está cerca.
  - **En parte en 0.26.0:** ya salen las chispas del aguijón contra la pared y el efecto del golpe de 2 o más (`Puff`; el doble va como bit 2 del aviso de golpe) y el polvo de la pared (sin red: mientras el avatar enseña un sprite de "Wall Slide"; sin probar, solo sale si el objeto está en las carpetas copiadas). Siguen sin salir: el escudo de Carefree Melody, el golpe parado por Baldur Shell, la recarga del dash sombra, las muertes y la luz (necesitan ver en el juego cómo están montados en el caballero).
  - **Probado en el juego (12/10/2026):** El polvo de la pared: el host detectó el deslizamiento del invitado y lo encendió (sin captura).
- [x] **J9 · baja · sin comprobar.** Caer a mitad de Crystal Heart, Desolate Dive o Cyclone Slash: `OnHeroDie` no corta
  nada ([Revive.cs:430-455](../src/HKMultiplayer/Game/Features/Revive.cs#L430)) y las FSM `Superdash` y
  `Spell Control` pueden seguir (sonido en bucle, velocidad) y retomarse al revivir. Probar; si pasa, mandar sus avisos
  de cancelar o `hero.ResetState()` al caer.
  - **Arreglado en 0.26.0:** Al caer se manda `FSM CANCEL` (global en las tres) a `Superdash`, `Spell Control` y `Nail Arts`.
  - **Probado en el juego (12/10/2026):** Caer cargando Crystal Heart: al revivir está quieto y con control.
- [x] **J10 · baja · sin comprobar.** `BossPresentation._localWantsLock` ([:106](../src/HKMultiplayer/Game/Features/BossPresentation.cs#L106),
  [:212](../src/HKMultiplayer/Game/Features/BossPresentation.cs#L212)) se queda en verdadero si a un `RelinquishControl`
  no le sigue un `RegainControl` (el juego a veces devuelve el control directamente, `HeroController.cs:3222`,
  `3225`): el siguiente bloqueo por el compañero no devolvería el control. Apuntar aparte "este bloqueo nos lo quitó"
  y limpiarlo al entrar en una sala.
  - **Arreglado en 0.26.0:** `BossPresentation.Update` quita las marcas si el caballero ya tiene el control (el juego lo devolvió sin `RegainControl`).
  - **Probado en el juego (12/10/2026):** encuentro con Hornet en los Bordes: cada uno queda quieto mientras el otro presenta y al acabar no queda ninguna marca en los dos.
- [x] **J11 · baja · sin comprobar.** `StagTogether` pone `Fade` aunque el ciervo de quien recibe no esté en esa
  estación, o esté en un diálogo o reapareciendo tras pinchos ([StagTogether.cs:146-158](../src/HKMultiplayer/Game/Features/StagTogether.cs#L146));
  la cinemática usa su `stagPosition`.
  - **Arreglado en 0.26.0:** Tampoco se lleva a quien está sin control (diálogo, mapa, tienda) ni reapareciendo.
  - **Probado en el juego (12/10/2026):** Con el mapa rápido abierto, el ciervo no se lleva al invitado ("ocupado").
- [x] **J12 · baja · sin comprobar.** La copia de la sombra del compañero solo lee del dueño `GetPlayerDataInt` y
  `shadeMapZone`: un `GetPlayerDataBool` (Void Heart, amuletos) o lo que lean FSM hijas usaría los datos del jugador
  local.
  - **Revisado en 0.26.0:** Descartado con el volcado: la sombra solo lee el sí/no `collectorDefeated`, que es del mundo; los números ya se leen del dueño.
- [x] **J13 · baja · no se puede comprobar con el volcado.** Salas de sueño que no estén marcadas `DREAM_WORLD` (quizá la Radiancia o la
  Pesadilla de Grimm): `CanBeDowned` ([Revive.cs:495](../src/HKMultiplayer/Game/Features/Revive.cs#L495)) las trataría
  como salas normales.
  - **Volcado de FSM:** El volcado no guarda el `mapZone` de cada sala; solo 5 FSM usan `CheckCurrentMapZone DREAM_WORLD`. Mirar `Grimm_Nightmare`, `Dream_Final_Boss` y `GG_*` (o que el volcador escriba `SceneManager.mapZone`).
  - **Arreglado en 0.26.0:** La comprobación de sueño mira también el nombre de la sala (`Dream_*`, `Grimm_Nightmare`). Además, "alguien en pie" ya no cuenta a quien está entrando (S2).
  - **Probado en el juego (12/10/2026):** Solo en `Dream_01_False_Knight` el invitado muere normal y despierta; con el host dentro, cae.
- [x] **J14 · baja · sin comprobar.** Salir de la partida estando caído en Alma de Acero evita la muerte (truco).
  - **Revisado en 0.26.0:** No aplica: los mundos coop no pueden ser de Alma de Acero (ver W10).
- [x] **J15 · baja · sin comprobar.** El avatar no tiene colisión: las salpicaduras al entrar en agua o ácido no salen
  en el compañero.
  - **Revisado en 0.26.0:** Se deja: dar colisión al avatar haría que el juego lo tratara como el caballero (peligros, puertas, zonas que se activan al entrar).
- [x] **J16 · baja · sin comprobar.** `DownedBody` repite la animación "Death" cada vez que se vuelve a entrar en la
  sala y mira siempre a la izquierda.
  - **Arreglado en 0.26.0:** El cuerpo hace la muerte solo si la caída es de hace menos de 3 s, y mira hacia donde miraba al caer.
  - **Probado en el juego (12/10/2026):** El cuerpo mira a la derecha o a la izquierda según el caído (escala -1 / 1) y al volver a la sala ya está tumbado. Salió que cada PC tomaba la orientación de su avatar al llegar el estado, y podía salir al revés: ahora va en el aviso de caída.

## Datos del mundo y del personaje (W)

- [x] **W1 · alta · comprobado.** `maxMP` se comparte y es de cada jugador. No está en `CharacterData` ni en
  `Excluded`. `AddMPCharge` lo reescribe en cada ganancia de alma: 66 con la sombra fuera (`soulLimited`), 99 sin ella,
  33 con alma atada (juego `PlayerData.cs:4804-4815`); también `StartSoulLimiter`/`EndSoulLimiter` (4877-4886). Los dos
  PCs se lo pisan sin parar y `ReviveCost` ([Revive.cs:1543-1547](../src/HKMultiplayer/Game/Features/Revive.cs#L1543))
  y `Vitals` ([:153](../src/HKMultiplayer/Game/Features/Vitals.cs#L153)) leen el del otro: puede volver el "nadie puede
  revivir" con la sombra fuera. Arreglo: a `CharacterData` (estado).
  - **Arreglado en 0.26.0:** `maxMP` es de cada personaje (`SharedUntil026` en `CharacterData`; los guardados antes toman el del mundo).
  - **Probado en el juego (12/10/2026):** `maxMP` cambiado en el host no llega al invitado.
- [x] **W2 · alta · comprobado.** El progreso de Dios Hogar no se guarda para el invitado: los 45
  `statueState*` (`BossStatue.Completion`), `bossDoorStateTier1-5` (`BossSequenceDoor.Completion`) y
  `currentBossStatueCompletionKey` son structs o texto, y `SharedProgress` solo mira `bool`, `int` y `List<string>`
  ([SharedProgress.cs:99-101](../src/HKMultiplayer/Game/World/SharedProgress.cs#L99)). Lo escriben
  `BossChallengeUI.cs:268-283`, `BossSequenceDoor.cs:213`, `BossSequenceController.cs:454` y `BossStatue.cs:166-201`.
  Decidir: de cada uno (a `CharacterData`; el JSON guarda los structs) o del mundo (un tipo nuevo en `SharedProgress`
  que junte con "o" cada bool).
  - **Arreglado en 0.26.0:** Estatuas, puertas de los Panteones y estatua elegida son de cada personaje (`SharedUntil026`).
  - **Probado en el juego (12/10/2026):** con `pdjson`, una estatua y una puerta de Panteón completadas en el invitado siguen al volver a entrar y el host no las tiene.
- [x] **W3 · media · comprobado.** `blockerHits` (lo que aguanta Baldur Shell) se comparte: los bloqueos de uno
  rompen el escudo del otro y el banco de uno lo repone a los dos (juego `PlayerData.cs:4648`, `HeroController.cs:2330`).
  Arreglo: a `CharacterData`.
  - **Arreglado en 0.26.0:** `blockerHits` es de cada personaje.
  - **Probado en el juego (12/10/2026):** `blockerHits` es de cada uno.
- [x] **W4 · media · sin comprobar.** Carrera en las listas: al aplicar un `ListAdd` de otro PC,
  `_listCounts[i] = list.Count` ([SharedProgress.cs:476-480](../src/HKMultiplayer/Game/World/SharedProgress.cs#L476)).
  Si este PC añadió algo hace menos de 0,2 s y aún no se comparó, nunca se envía (`scenesVisited`,
  `scenesGrubRescued`, `scenesFlameCollected`, `unlockedBossScenes`...). Arreglo: avanzar la cuenta solo si no había
  nada local pendiente; el `Contains` de quien recibe ya quita los repetidos.
  - **Arreglado en 0.26.0:** Al aplicar un `ListAdd`, la cuenta solo avanza si no había nada local sin enviar.
  - **Probado en el juego (12/10/2026):** host e invitado entran a la vez en dos salas nuevas y las dos quedan en `scenesVisited` de los dos.
- [x] **W5 · media · sin comprobar.** Los números compartidos van como valor absoluto
  ([SharedProgress.cs:239-245](../src/HKMultiplayer/Game/World/SharedProgress.cs#L239), [:463-468](../src/HKMultiplayer/Game/World/SharedProgress.cs#L463)):
  si los dos suman al mismo contador a la vez (RTT + 0,2 s), se pierde una suma. `grubsCollected` (recompensas del
  Padre de las Larvas), `kills*` del diario (`EnemyDeathEffects.cs:270-274`), `journalEntriesCompleted`,
  `journalNotesCompleted` (`:288`) y `flamesCollected`. Una muerte solo local de una marioneta (E2) y la real después
  cuentan dos veces. Arreglo: enviar la diferencia en los contadores o que el host los recalcule (`grubsCollected`
  desde `scenesGrubRescued.Count`).
  - **Arreglado en 0.26.0:** `grubsCollected` va como suma (`Kind.IntAdd`). Los del diario siguen como valor a propósito: los cambian los dos PCs con la misma muerte.
  - **Probado en el juego (12/10/2026):** Con `pdadd` (suma como el juego), una suma a la vez en cada PC da 2 y tres más en cada uno dan 6.
- [x] **W6 · media · comprobado, pero el arreglo propuesto rompería ascensores.** `PersistentIntData` puede ir hacia atrás: los ganchos de `PersistentBoolData` y
  de las rocas de geo pasan por `KeepActivated`/`KeepRockHits`, el de enteros no
  ([SharedProgress.cs:276-279](../src/HKMultiplayer/Game/World/SharedProgress.cs#L276)). Una sala cargada antes de que
  llegue una subida guarda su valor viejo al salir (juego `PersistentIntItem.cs:62-67`), el host lo guarda y nunca se
  envían bajadas. Arreglo: un `KeepInt` que se quede con el mayor conocido (comprobar antes que ningún
  `PersistentIntItem` baje a propósito, p. ej. una palanca de varias posiciones).
  - **Volcado de FSM:** Los `PersistentIntItem` (63 salas) son tótems de alma (`soul_totem`, su `Value` baja de 3 a 0) y ascensores de la Ciudad (`Ruins Lift*`, su `Value` es el piso: sube y baja). "Quedarse con el mayor" rompería los ascensores, y eso ya pasa hoy en la otra dirección: ver W13.
  - **Arreglado en 0.26.0:** Ver W13.
- [x] **W7 · baja · sin comprobar.** `ReturnToMainMenu` llama a `TimePasses` justo antes de salir (juego
  `GameManager.cs:3358`, `1021-1083`) y la comparación de cada 0,2 s puede no llegar a enviarlo. Tres dependen de los
  datos de quien sale: `fatGrubKing` (`finalGrubRewardCollected`), `extraFlowerAppear` (`xunFlowerGiven`) y
  `nailsmithCorpseAppeared` (`nailsmithKilled`). `brettaPosition` se tira al azar en cada PC y gana el último.
  Arreglo: forzar la comparación en `CoopSaves.OnSaveGame` antes de `GuestSaving`.
  - **Arreglado en 0.26.0:** `SharedProgress.FlushNow()` al empezar el guardado del mundo coop.
  - **Probado en el juego (12/10/2026):** El invitado cambia un dato del mundo y sale al menú en el mismo instante: llega al host.
- [x] **W8 · baja · sin comprobar.** El guardado y la carga del mundo coop se saltan los ganchos de la Modding API:
  `OnLoadLocalSettings`, `OnAfterSaveGameLoad`, `OnSavegameLoad`, `OnBeforeSaveGameSave`, `OnSaveLocalSettings`,
  `OnSavegameSave`, además del volcado de logros y `profileID` (juego `GameManager.cs:2708-2826`, `2921-2964`;
  [CoopSaves.cs:202-257](../src/HKMultiplayer/Game/World/CoopSaves.cs#L202)). Otros mods no guardan ni cargan sus datos
  en un mundo coop y `gm.moddedData` queda el de la última ranura normal.
  - **Revisado en 0.26.0:** No aplica a quien instala el paquete: el instalador aparta los demás mods. Si alguien los activa a mano, sus datos por partida no se guardan en un mundo coop (los ganchos son `internal` en la Modding API).
- [x] **W9 · baja · sin comprobar.** `WorldStore.SaveCharacter` borra el archivo y luego mueve el nuevo
  ([WorldStore.cs:202-206](../src/HKMultiplayer/Game/World/WorldStore.cs#L202)): si el juego se cierra en medio, el
  invitado se queda sin personaje (`LoadCharacter` no mira el `.tmp`) y empieza uno nuevo. Arreglo: `File.Replace` o
  un `.bak` como en `Save`.
  - **Arreglado en 0.26.0:** El anterior queda como `.bak` y `LoadCharacter` lo usa si el último falta o está dañado.
  - **Probado en el juego (12/10/2026):** Con el personaje del invitado estropeado en el host, se carga el `.bak` ("se cargó la copia anterior") y conserva lo suyo.
- [x] **W10 · baja · nota.** `permadeathMode` se comparte. Hoy los mundos coop empiezan con `StartNewGame()` y no
  hay Alma de Acero; si algún día se permite, la muerte de un invitado (juego `HeroController.cs:4248-4250`) marcaría
  el mundo del host como perdido.
  - **Revisado en 0.26.0:** Sigue sin aplicar: los mundos coop no pueden ser de Alma de Acero. Si algún día se permite, `permadeathMode` tiene que ser de cada personaje.
- [x] **W11 · baja · sin comprobar.** Grimmchild para quien entra después: `GrimmchildGift` solo actúa si el jugador
  recibe el amuleto con el otro conectado ([GrimmchildGift.cs:67-73](../src/HKMultiplayer/Game/Features/GrimmchildGift.cs#L67)).
  Un invitado que entra luego no lo recibe, pero `grimmChildLevel` y la Compañía son del mundo y `metGrimm` es suyo:
  la FSM de la tienda puede comportarse raro. `OnGift` tampoco mira si se juega un mundo coop.
  - **Arreglado en 0.26.0:** Al entrar en el mundo, si `grimmChildLevel` ya es 1 o más y el personaje no tiene el amuleto, se le da; `OnGift` solo en un mundo coop.
  - **Probado en el juego (12/10/2026):** Un invitado sin el amuleto entra en un mundo con Grimmchild y lo recibe ("el mundo ya lo tenía").
- [x] **W12 · baja · descartado.** Campos compartidos que parecen de inventario: `bossStatueTargetLevel` (lo que
  se elige en el menú del Salón, `BossChallengeUI.cs:284`), `hasGodfinder` y `hasJournal`. Probablemente da igual.
  - **Volcado de FSM:** `hasJournal` y `hasGodfinder` los da un `Shiny Item`, que es de cada uno, y compartirlos no estorba; ninguna FSM escribe `bossStatueTargetLevel`.

Del volcado de FSM:

- [x] **W13 · baja · comprobado.** Los ascensores de la Ciudad no guardan una bajada: `OnSceneInt` y el aplicar solo
  aceptan un número mayor ([SharedProgress.cs:325](../src/HKMultiplayer/Game/World/SharedProgress.cs#L325),
  [:496](../src/HKMultiplayer/Game/World/SharedProgress.cs#L496)), y el `Value` de los `Ruins Lift*` es el piso
  (`Ruins1_02/03/05/05b/05c/23/25/31`, `Ruins2_01_b/03/03b/05`, `Ruins2_Watcher_Room`, `Ruins_Elevator`). El otro PC y el
  mundo del host se quedan con el piso más alto: quien entra después lo encuentra en otro piso. Arreglo: que gane el
  último cambio; para los tótems de alma (`soul_totem`, de 3 a 0), el menor (si no son semipersistentes). Ver W6.
  - **Arreglado en 0.26.0:** `Replaces`: en los tótems gana el menor y en los ascensores el último cambio.
  - **Probado en el juego (12/10/2026):** Ascensor de `Ruins1_02`: lo baja el host y lo sube el invitado; los dos guardan el último piso. Los tótems resultaron semipersistentes (se rellenan al descansar) y no se comparten: esa parte no aplica.
- [x] **W14 · baja · comprobado.** Millibelle mezcla un dato del mundo con uno de cada personaje: `Fungus3_35:Banker`,
  en `Check Theft`, si `bankerTheftCheck` (del mundo) está puesto y el `bankerBalance` local ([CharacterData.cs:82](../src/HKMultiplayer/Game/World/CharacterData.cs#L82))
  pasa del límite, multiplica el saldo por 1,5 y pone `bankerTheft = 1` (del mundo). El robo depende de quién entra
  primero; después desaparece para los dos y el otro no puede sacar su saldo hasta golpearla en el Spa (sin el ×1,5).
  Arreglo: `bankerTheft` y `bankerTheftCheck` a `CharacterData`.
  - **Arreglado en 0.26.0:** `bankerTheft` y `bankerTheftCheck` son de cada personaje.
  - **Probado en el juego (12/10/2026):** `bankerTheft` y `bankerTheftCheck` son de cada uno.
- [x] **W15 · baja · sin comprobar.** `liftArrival` se comparte ([SharedProgress.cs:50-55](../src/HKMultiplayer/Game/World/SharedProgress.cs#L50)):
  `Lift Move` lo pone a verdadero justo antes de cambiar de sala; si el otro PC carga en ese momento uno de los 4
  `elev_main` (p. ej. con "Ir con"), su `Check Arrival` sujeta a su caballero a la cabina y hace la llegada. Arreglo: a
  `Excluded`, como `travelling`.
  - **Arreglado en 0.26.0:** `liftArrival` en `Excluded`.
  - **Probado en el juego (12/10/2026):** `liftArrival` no se comparte.
- [x] **W16 · baja · nota.** Preferencias de interfaz y avisos de tutorial compartidos: `mapKeyPref` (leyenda del mapa),
  `promptFocus`, `seenJournalMsg`, `seenHunterMsg`, `bossDoorEntranceTextSeen` y `firstGeo`. Lo que elige o ve uno cambia
  al otro. Arreglo: a `Excluded`.
- Todos los textos y decimales que escriben las FSM son del personaje (`respawn*`, `shade*`, `dreamGate*`,
  `gMap_door*`, `nextScene`, `bossReturnEntryGate`, `dreamReturnScene`): `SharedProgress` no pierde ninguno.
  - **Arreglado en 0.26.0:** En `Excluded`.
  - **Probado en el juego (12/10/2026):** `promptFocus`, `seenJournalMsg` y `seenHunterMsg` no se comparten.

## Ganchos (H)

- [x] **H1 · media · comprobado.** Los enemigos vuelven al cruzar un punto de reaparición. Los ganchos a
  `SetBenchRespawn` ([EnemySync.cs:5529-5540](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5529)) llaman a
  `OnLocalRest`, pero el banco no los usa: los llama `RespawnTrigger.OnTriggerEnter2D` (juego `RespawnTrigger.cs:55`,
  `62`; los que no son de un solo uso, cada vez que se cruzan) y `HeroController.SetBenchRespawn` (`:2141`). El host
  devuelve los enemigos antes de tiempo. El banco y la muerte ya van por `ResetSemiPersistentItems`. Arreglo: quitar
  los tres ganchos (y su comentario, que dice lo contrario).
  - **Arreglado en 0.26.0:** Quitados los tres ganchos a `SetBenchRespawn`.
  - **Probado en el juego (12/10/2026):** `SetBenchRespawn` (como al cruzar un punto de reaparición) no devuelve los enemigos muertos; sentarse en el banco sí ("los enemigos vuelven a aparecer").
- [x] **H2 · alta · sin comprobar.** `QuestNpcSync` engancha `PlayerData.GetBool`, `GetInt` y `SetInt` en `Attach`
  ([QuestNpcSync.cs:83](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L83), [:562-576](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L562)).
  Son métodos de una línea (8-9 bytes, juego `PlayerData.cs:4534`, `4576`, `4581`), como el `Die()` que el doc dice
  que Mono copia dentro de quien lo llama. Si quien los llama (`GameManager.GetPlayerDataBool`,
  `SetPlayerDataInt`, `GameManager.cs:1137`, `1167`) ya se compiló antes de la primera sesión (tras una partida
  normal en el mismo proceso), el gancho no se ve y vuelven los fallos de misión que avanzan dos veces, solo en ese
  caso. Arreglo: usar `ModHooks.GetPlayerBoolHook`, `GetPlayerIntHook` y `SetPlayerIntHook` (`ModHooks.cs:72-78`;
  también cubren `IncrementInt`, `IntAdd` y `DecrementInt`, que llaman a `ModHooks.SetPlayerInt`), o al menos
  engancharlos al cargar el mod. En general, mover todos los `InstallHooks()` de `Attach` a `MultiplayerRunner.Awake`
  (ya miran `_active`).
  - **Arreglado en 0.26.0:** `QuestNpcSync.InstallHooks` se llama al cargar el mod.
- [x] **H3 · baja · sin comprobar.** `ObjectPool.Spawn` puede avisar dos veces del mismo objeto: si el pool no tiene
  copia del prefab, `Spawn` llama a `CreatePool` y otra vez a `Spawn` (juego `ObjectPool.cs:631`), que pasa por el
  gancho. `EnemySync` ([:5403](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5403)) apunta dos creaciones con dos ids
  (el otro PC tendría una copia huérfana) y `PlayerEffects` ([:761](../src/HKMultiplayer/Game/Players/PlayerEffects.cs#L761))
  envía el efecto dos veces. Arreglo: contar la profundidad y avisar solo en la llamada de fuera.
  - **Arreglado en 0.26.0:** Contador de profundidad en los dos ganchos a `ObjectPool.Spawn` (`EnemySync` y `PlayerEffects`): solo cuenta la llamada de fuera.
  - **Probado en el juego (12/10/2026):** Con el gas del Fungus Flyer, cada bola creada tiene exactamente una copia (48 y 48): no hay avisos dobles.
- [x] **H4 · baja · sin comprobar.** Los avisos tras `orig` en `Fsm.SwitchState`
  ([EnemySync.cs:5442](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5442), [PlayerEffects.cs:753](../src/HKMultiplayer/Game/Players/PlayerEffects.cs#L753))
  pueden llegar al revés: entrar en un estado puede provocar otro cambio dentro (juego PlayMaker `Fsm.cs:2339-2343`) y
  el último enviado sería el viejo. Arreglo: avisar antes de `orig` (como `FsmStateSync`) o comprobar que
  `ActiveState` sigue siendo ese.
  - **Arreglado en 0.26.0:** En `EnemySync`, si entrar en el estado provocó otro cambio dentro, se vuelve a avisar del estado en que quedó.
- [x] **H5 · baja · sin comprobar.** El gancho de `ProcessEvent` de `EnemySync` (hacia [:5464-5490](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5464))
  lee `Fsm.EventData.SentByFsm` (estático) después de `orig`, y los avisos de dentro lo cambian (juego PlayMaker
  `Fsm.cs:2084`). Guardarlo antes de llamar a `orig`.
  - **Arreglado en 0.26.0:** El remitente se lee antes de `orig`.
- [x] **H6 · baja · descartado.** `BossPresentation` solo engancha `RelinquishControl`; falta
  `RelinquishControlNotVelocity` (juego `HeroController.cs:3312`), si alguna FSM lo usa.
  - **Volcado de FSM:** `RelinquishControlNotVelocity` solo la usa `Inventory Control [Open]` del caballero (`_persistent`); ninguna FSM de jefe ni código de C# fuera de `HeroController`.
- [x] **H7 · baja · nota.** `QuestNpcSync` no trata `DeactivateIfPlayerdataFalseDelayed`.
  - **Volcado de FSM:** Solo existe en `GG_Atrium` (`top2`, el camino oculto).
  - **Arreglado en 0.26.0:** `DeactivateIfPlayerdataFalseDelayed` va como `DeactivateIfPlayerdataFalse`; se apunta al apagar el objeto (`DoCheck`, tras su espera).
- [x] **H8 · baja · sin comprobar.** Vueltas al menú que no pasan por los ganchos de `ReturnToMainMenu`
  (`CollectGuestsThen`, `CancelDowned`): `QuitToMenu` (juego `GameManager.cs:3419`), `EmergencyReturnToMenu` (`3513`)
  y la muerte en Alma de Acero (va a `PermaDeath`).
  - **Revisado en 0.26.0:** No aplica en PC: `EmergencyReturnToMenu` solo salta si la plataforma se desconecta (consolas); la muerte de Alma de Acero no existe en los mundos coop.
- [x] **H9 · nota.** El gancho de `Die` tiene que seguir antes que el de `TakeDamage` ([Revive.cs:323](../src/HKMultiplayer/Game/Features/Revive.cs#L323)
  y [:333](../src/HKMultiplayer/Game/Features/Revive.cs#L333)): MonoMod compila la copia de `TakeDamage` que usa como
  `orig` al añadir el gancho. Bien hoy, pero frágil: dejar un comentario.
  - **Arreglado en 0.26.0:** Comentario en el gancho de `Die` de `Revive.InstallHooks`.
  - **Comprobado (12/10/2026):** Es un comentario.

## Objetos de sala (O)

- [x] **O1 · media · comprobado.** Las estalactitas pueden no caer en el otro PC. Al cargar la sala se apuntan por
  `ScenePaths.Hash` ([SceneObjectSync.cs:403-406](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L403)) y al caer
  se vuelve a calcular ([:509](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L509)). La huella usa el puesto entre
  hermanos ([ScenePaths.cs:15-17](../src/HKMultiplayer/Game/Scene/ScenePaths.cs#L15)), que cambia si muere un enemigo
  de la raíz (`EnemyDeathEffects.cs:235`, `HealthManager.cs:613` lo destruyen) o si `EnemySync` suelta hijos de su
  padre ([EnemySync.cs:1612](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1612), 1935, 2941, 4585). Nido Profundo y
  Cumbre de Cristal. Arreglo: buscar el id apuntado por referencia (diccionario `StalactiteControl → uint`).
  - **Arreglado en 0.26.0:** `ScenePaths.StableHash`: la huella se apunta al cargar cada sala (con partida) y se usa siempre esa.
  - **Comprobado (12/10/2026):** Por las 16 rutas (379 OK, 0 FALLO) con las huellas apuntadas al cargar.
- [x] **O2 · media · comprobado.** El puente de la Ciudad de las Lágrimas no se abre en el otro PC. `BridgeLever`
  (`cityBridge1`, `cityBridge2`) es C# sin FSM ni `PersistentBoolItem` (juego `BridgeLever.cs:34-90`): solo mira el
  dato en `Start`, así que el compañero en la sala no puede cruzar hasta volver a entrar. Arreglo: aviso "palanca
  golpeada"; al recibirlo, `activated = true`, `bridgeCollider.enabled = true` y `Open` de cada sección (sin
  `ROAR ENTER` y sin volver a escribir el dato).
  - **Volcado de FSM:** Solo en `Fungus2_21` (2, `Bridge Lever 1`): el puente de la entrada a la Ciudad de las Lágrimas.
  - **Arreglado en 0.26.0:** Mensaje 109 (`BridgeLeverHit`): el otro PC abre el puente sin el rugido ni la cámara.
  - **Probado en el juego (12/10/2026):** palanca del puente en `Fungus2_21`: el invitado abre el puente y `cityBridge1` queda en los dos.
- [x] **O3 · media · sin comprobar del todo.** `HazardSync.OnPhases` trata toda animación como un bucle
  ([HazardSync.cs:563-572](../src/HKMultiplayer/Game/Scene/HazardSync.cs#L563)): en una que no se repite y ya acabó,
  `normalizedTime` pasa de 1 y la parte decimal casi nunca coincide, así que `Play(state, 0, frac)` la vuelve a
  empezar cada segundo (repite sus eventos y lo que encienda o apague). Si el otro está en otro estado, lo fuerza al
  del dueño, y hay animadores que son de cada PC: `DreamPlatform` aparece o se esconde según el caballero local (juego
  `DreamPlatform.cs:26-74`), `BossDoorCage` pone "Unlock". Si `DreamPlatform` apaga su colisión con la animación, la
  plataforma puede desaparecer bajo el compañero. Arreglo: alinear solo si el estado es un bucle en los dos y no forzar
  otro estado en una animación que no se repite.
  - **Arreglado en 0.26.0:** Solo se envían y alinean animaciones en bucle, y nunca se fuerza otro estado.
- [x] **O4 · baja · sin comprobar.** Al entrar en una sala, los `Breakable` ya rotos se rompen en directo (`Break`,
  no hay `SetBroken` para ese tipo, [BreakableSync.cs:415-418](../src/HKMultiplayer/Game/Scene/BreakableSync.cs#L415)):
  cada uno suena, tiembla la cámara (`EnemyKillShake`, juego `Breakable.cs:420-437`) y saltan los trozos. Lo roto en
  directo por el compañero también mueve la cámara de este PC esté donde esté. Arreglo: `SetBroken` para `Breakable`
  (`isBroken = true`, `SetStaticPartsActivation(true)`, apagar `bodyCollider`).
  - **Arreglado en 0.26.0:** `SetBroken` para `Breakable` (como su `OnSetSaveState`: `isBroken` y `SetStaticPartsActivation(true)`).
  - **Probado en el juego (12/10/2026):** roto en directo por el host, sus trozos salen volando; el invitado que entra después lo ve roto con los trozos apagados en su sitio (sin volver a romperse).
- [x] **O5 · baja · comprobado.** Plataformas que solo activa el caballero local: `FlipPlatform` (Cumbre de
  Cristal, se gira con pinchos 4 s, juego `FlipPlatform.cs:65-78`) y `DropPlatform` (capa 9, `DropPlatform.cs:42-48`).
  El daño es justo en cada PC, pero el compañero parece estar sobre pinchos o en el aire. Arreglo: aviso "activada" con
  su id y arrancar la misma corrutina.
  - **Volcado de FSM:** `FlipPlatform` en `Mines_17`, `Mines_29`, `Mines_31` y `Mines_35` (44); `DropPlatform` en `Fungus1_24` y `Fungus3_04/05/08/10/11/13/21/22/23/34` (~110). Sin FSM y el mod no las nombra.
  - **Arreglado en 0.26.0:** Mensaje 110 (`LocalTrigger`): la plataforma empieza su corrutina `Flip` también en el otro PC.
  - **Probado en el juego (12/10/2026):** `Mines_17` (las plataformas acaban con los mismos pinchos encendidos en los dos) y `Fungus3_04` (el host cae por dos plataformas y el invitado las deja caer).
- [x] **O6 · baja · comprobado.** Los huevos de Cañón de Niebla (`JellyEgg`, también el que explota) se rompen solo
  en un PC (juego `JellyEgg.cs:58-75`): el compañero sigue viendo el huevo y le explota después.
  - **Volcado de FSM:** `JellyEgg` en `Fungus1_11` y `Fungus3_01/02/03/24/25/25b/26/27/28/47`; los que explotan, en `Fungus3_25b`, `Fungus3_26` y `Fungus3_28` (46 en esta).
  - **Arreglado en 0.26.0:** Mensaje 110: el huevo hace su `Burst` también en el otro PC.
  - **Probado en el juego (12/10/2026):** `Fungus3_02`: el huevo que rompe el host queda roto en el invitado (colisión apagada).
- [x] **O7 · baja · sin comprobar.** La primera pasada de `FsmStateSync.Scan` ([:620-628](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L620))
  no descarta los objetos de `DontDestroyOnLoad` ni los del caballero (`AddBattleListeners` sí, [:691-693](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L691)):
  una FSM persistente con "Button" o "Switch" en el nombre se seguiría. Añadir `buildIndex < 0` e `IsChildOf(hero)`.
  - **Arreglado en 0.26.0:** La primera pasada descarta lo persistente y lo del caballero.
- [x] **O8 · baja · sin comprobar.** Ids distintos para objetos clonados en la raíz: los `fsm:` de `SceneObjectSync`
  ([:362](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L362)) usan `ScenePaths.Hash` y `FsmStateSync.FsmId`
  ([:894-897](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L894)) usa `PlacedId`. Un rompible de FSM clonado en
  partida tendría un id que depende del orden.
  - **Arreglado en 0.26.0:** `SceneObjectSync` usa `FsmStateSync.FsmId`.
- [x] **O9 · baja · sin comprobar.** Dos golpes a la vez a la misma roca de geo cuentan uno: se envía el número de
  golpes absoluto ([SceneObjectSync.cs:309](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L309), [:435](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L435)).
  Enviar la resta.
  - **Arreglado en 0.26.0:** En directo va también cuántos golpes dio este jugador, y quien lo recibe los resta de los suyos.
  - **Probado en el juego (12/10/2026):** Un golpe a la misma roca en cada PC a la vez: 3 → 1 en los dos.
- [x] **O10 · baja · sin comprobar.** `BreakableSync._sendSnapshot` no se pone a cero al cambiar de sala
  ([:255-271](../src/HKMultiplayer/Game/Scene/BreakableSync.cs#L255)), al contrario que `SceneObjectSync.Clear`
  ([:179](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L179)): una petición de foto puede pasar a la sala
  siguiente.
  - **Arreglado en 0.26.0:** `_sendSnapshot` se pone a falso al cambiar de sala.
- [x] **O11 · baja · sin comprobar.** `HazardSync.Scan` para en `MaxHazards` y `FindObjectsByType` no ordena
  ([:216-218](../src/HKMultiplayer/Game/Scene/HazardSync.cs#L216)): en una sala con más de 160 animadores cada PC puede
  seguir otros.
  - **Arreglado en 0.26.0:** Con más de `MaxHazards`, se ordenan por huella antes de recortar.
  - **Comprobado (12/10/2026):** Por las rutas (peligros de cada sala iguales en los dos).
- [x] **O12 · baja · sin comprobar.** El botín puede salir distinto: `DropSeeds` calcula la huella en el momento del
  lanzamiento ([DropSeeds.cs:88](../src/HKMultiplayer/Game/Scene/DropSeeds.cs#L88), [:109](../src/HKMultiplayer/Game/Scene/DropSeeds.cs#L109)),
  con el mismo problema que O1: tras morir un enemigo de la raíz, cada PC usa otra semilla. Solo cosmético. Arreglo:
  guardar la clave por `Transform` al cargar la sala (arregla también P1).
  - **Arreglado en 0.26.0:** `DropSeeds` usa `ScenePaths.StableHash` (ver O1).
  - **Comprobado (12/10/2026):** Por las rutas (botín con la misma semilla).
- [x] **O13 · baja · sin comprobar.** Ids frágiles al cargar la sala: si `Shiny Control` destruye (en vez de apagar) un
  objeto brillante que uno ya recogió, el puesto de los objetos de la raíz cambia entre jugadores y fallarían los ids
  de todo lo que va detrás (rompibles, peligros, FSM, la foto para quien entra). Lo encontrado en la segunda pasada
  (1,5 s) se apunta después de posibles cambios. Arreglo posible: ids por nombre y posición (como `PlacedId`) o sin el
  puesto de la raíz cuando el nombre es único.
  - **Revisado en 0.26.0:** Cubierto por O1: `ScenePaths` apunta las huellas en `sceneLoaded`, que llega antes del `Start` de los objetos de la sala, así que antes de que `Shiny Control` empiece y haga `DestroySelf`. Lo creado después va por su sitio (`PlacedId`).
- [x] **O14 · baja · sin comprobar.** Una `VinePlatform` que cae por un corte llegado de otro PC (o de la foto) cae en
  directo en quien lo recibe, y su `enemyDetector` mata con `Die()` a los enemigos de ese PC que tiene debajo (juego
  `VinePlatform.cs:101-108`): muertes dobles o solo locales.
  - **Volcado de FSM:** `VinePlatform` solo en `Fungus1_06/07/09/13/14/21/22` y `GG_Atrium_Roof`; el volcado no dice si hay enemigos debajo.
  - **Arreglado en 0.26.0:** Cubierto por E2: una marioneta ya no muere en local, se avisa a quien la mueve (que también tiene la liana cayendo).
- [x] **O15 · baja · descartado.** Plataformas que se desmoronan y vuelven, llevadas por una FSM sin `Activated`
  ni nombre de palanca: probablemente no se copian y cada PC tiene la suya (como O5).
  - **Volcado de FSM:** Todos los suelos que se desmoronan por FSM llevan `PersistentBoolItem` + `Activated` y ya los siguen `FsmStateSync`/`SceneObjectSync`; ninguno vuelve a aparecer (`collapse small`, `break_floor`, `quake_floor_shake`, `Garden Slide Floor`, `Resting Grounds Slide Floor`, `Chain Platform` de `Ruins1_28`, `Detect Quake` de `Mines_01`/`Mines_35`).
- [x] **O16 · baja · comprobado.** Plataformas sólidas movidas por FSM (`iTweenMoveTo/By/Add/Update`,
  `MoveTowards`): el doc dice que no hay en las zonas revisadas, y en el C# no hay ninguna aparte de las ya tratadas,
  pero en las FSM no se puede ver desde `decomp/`. Una orden del modo de prueba que registre colisiones no trigger que
  se mueven sin ser enemigo, caballero ni ascensor lo resolvería.
  - **Volcado de FSM:** Hay sólidos movidos por FSM que no sigue nadie: O19 (tranvía), O21 (ascensor del Palacio Blanco), O22 (plataformas con espera al azar). Los grandes ascensores `elev_main` (`Crossroads_49/49b`, `Ruins2_10/10b`, `Lift Move`) van bien: la palanca `Lift Call Lever` se copia y solo manda `MOVE` si el caballero de ese PC está en la cabina (`Hero Check`).
  - **Revisado en 0.26.0:** Con el volcado: los sólidos movidos por FSM que no seguía nadie eran O19, O21 y O22 (arreglados). Los `elev_main` van bien.
- [x] **O17 · baja · comprobado.** `SlopePlat` pone su altura según la x del caballero local (juego `SlopePlat.cs:20-35`):
  el compañero encima se ve desplazado.
  - **Volcado de FSM:** `SlopePlat` solo está dentro de los cuatro `elev_main` (ver O16).
  - **Revisado en 0.26.0:** Se deja: solo pasa en los cuatro ascensores grandes y es visual (el compañero se ve un poco más alto o más bajo mientras sube).
- [x] **O18 · baja · nota.** Los ids usan `string.GetHashCode` en algunos sitios: igual en la misma versión de Mono
  (la sesión ya exige la misma versión del mod), pero FNV sería más seguro.
  - **Arreglado en 0.26.0:** `ScenePaths.Fnv` en lugar de `string.GetHashCode` en todos los ids (`FsmId`, `DreamSync`, `BreakableSync`, `EnemySync`).
  - **Comprobado (12/10/2026):** Por las 16 rutas con los ids en FNV: los dos PCs siguen de acuerdo en objetos, rompibles, FSM y enemigos.

Del volcado de FSM:

- [x] **O19 · media · sin comprobar.** El tranvía no se sincroniza. En `Room_Tram` y `Room_Tram_RG`, ni
  `Tram Console L/R` (`Switch Active`) ni `Tram Control` entran en `Targets` ni en `IsSwitchLike`
  ([FsmStateSync.cs:115](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L115), [:797](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L797)):
  quien usa la consola viaja solo. `PlayerDataIntAdd tramLowerPosition ±1` se comparte, pero el `door1 [Door Target]` del
  otro solo lo relee con `TRAM ARRIVE` y sale a la estación vieja; si pulsan los dos a la vez, suma 2 (W5). En las
  estaciones (`Abyss_03/03_b/03_c`, `Crossroads_46/46b`) `Tram Main [Tram Control]` solo llega con el `CALL TRAM` local:
  el compañero no lo ve llegar ni puede subir. Arreglo: el tranvía como controlador y viaje juntos como `StagTogether`;
  como mínimo, ante un cambio de otro PC, repetir `Get Station` y mandar `CALL TRAM`.
  - **Arreglado en 0.26.0:** `Tram Control` es un objeto con controlador (`Targets`). Sin probar en el juego.
  - **Probado en el juego (12/10/2026):** `Crossroads_46`: el host llama al tranvía y llega en los dos; suben juntos, la consola del host los lleva a los dos (la estación suma 1, no 2) y bajan los dos en `Crossroads_46b`. Al probarlo salió que faltaba `Tram Main` (el tranvía de las estaciones): añadido.
- [x] **O20 · media · sin comprobar.** Las máquinas de peaje copian su diálogo al otro PC: `Toll Machine` (`Fungus1_31`
  ×2, `Mines_33` ×2, `Deepnest_41`) y `Toll Machine Bench` (`Abyss_18`, `Fungus3_50`, `Ruins1_31`) tienen
  `PersistentBoolItem` + `Activated` y se copia su cadena desde el reposo `Out Of Range` ([FsmStateSync.cs:1922](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1922)),
  con `In Range` (cartel), `Box Up` (`BOX UP YN`) y `Send Text` (`StartConversation TOLLBOOTH_*`); `PrepareCopy` solo
  apaga las llamadas al caballero. Cuando el compañero se acerca, a este jugador le sale el cartel, y cuando pregunta se
  le abre la caja de sí/no con el precio con el control puesto. Arreglo: copiar solo desde `Yes`/`Open Gates`, o saltar
  los estados con diálogo.
  - **Arreglado en 0.26.0:** Las copias no tocan al caballero local (ninguna acción sobre él), ni abren diálogos (`BOX *`, `StartConversation`), ni el cartel de "Inspeccionar". Sin probar en el juego.
  - **Probado en el juego (12/10/2026):** `Fungus1_31`: al host le sale el cuadro de pago y al invitado no (captura), y la puerta se abre en los dos; solo paga el host.
- [x] **O21 · baja · comprobado.** El ascensor de luz del Palacio Blanco (`White Palace Lift [Control]`; `White_Palace_01`,
  `White_Palace_03_hub`, `White_Palace_09`) sube al pisarlo el caballero local (`Collision2dEvent` con `Player`) y no es
  `Lift Control`, así que no se sigue: el compañero sube flotando y cada uno usa el suyo. Arreglo: a `Targets` con la
  lógica de ascensor (lo mueve quien va encima).
  - **Arreglado en 0.26.0:** `White Palace Lift` es un objeto con controlador. Sin probar en el juego.
  - **Probado en el juego (12/10/2026):** `White_Palace_01`: el ascensor sube en el invitado cuando lo pisa el host (los dos en `Hit Top`, a la misma altura).
- [x] **O22 · baja · comprobado.** Esperas al azar en plataformas de un controlador ya sincronizado:
  `Colosseum Platform [Control]` (Coliseo de Bronce, Plata y Oro: `Expand Pause` y `Slow Rand` con `WaitRandom` de 0 a
  1 s) y `radiant_plat` (`Dream_Final_Boss`, `GG_Radiance`: `Pre Vanish` de 2 a 3 s). `Abyss Pit [Ascend]` sube según la
  Y del caballero local. El compañero parece estar en el aire o caer (cada uno juega limpio en su PC). Arreglo: semilla
  fija por ruta y aviso (como `DropSeeds`), o seguir también los avisos dirigidos a un objeto.
  - **Arreglado en 0.26.0:** `Colosseum Platform` y `Radiant Plat` son objetos con controlador. `Abyss Pit` (sube según el caballero local) queda igual.
  - **Probado en el juego (12/10/2026):** Coliseo de Bronce entero: las plataformas coinciden en 32 de 36 muestras; las otras 4, un paso por detrás (retraso de la copia).
- [x] **O23 · baja · comprobado.** Pagar el ascensor no lo baja en el otro PC: `Toll Machine Lift` de `Crossroads_49b`
  pone `cityLift1` (compartido) y manda `TOLL LIFT PAY` solo en su PC, y `elev_main [Lift lower]` solo baja con ese aviso:
  el compañero lo ve arriba hasta volver a entrar. Arreglo: al llegar `cityLift1` de otro PC, mandar `TOLL LIFT PAY` en la
  sala.
  - **Arreglado en 0.26.0:** Al llegar `cityLift1` de otro PC, se manda `TOLL LIFT PAY` a las FSM de la sala que lo esperan.
  - **Probado en el juego (12/10/2026):** `Crossroads_49b`: al llegar `cityLift1` del host, el ascensor del invitado baja (`Open`).

## Rendimiento (P)

- [x] **P1 · media · comprobado.** `DropSeeds` crea memoria en acciones de PlayMaker muy frecuentes, también jugando
  solo ([DropSeeds.cs:35-95](../src/HKMultiplayer/Game/Scene/DropSeeds.cs#L35)): `SendRandomEvent.OnEnter`,
  `FlingObject.OnEnter` y los `FlingObjectsFromGlobalPool*` crean un cierre (`() => orig(self)`) en cada llamada antes
  de mirar `Enabled`; `SeededAction` hace `IsEnemyObject` y `ScenePaths.Hash` (cadena con `Insert(0, …)`) antes de
  mirarlo; `SpawnAndFling` lee `config.Prefab.name` y `Seeded` lee `scene.name` en cada lanzamiento. Arreglo: si
  `!Enabled` (o no es `Shiny Control`), `orig(self)` lo primero; pasar `orig`/`self` sin cierres; comparar
  `scene.handle` en vez del nombre.
  - **Arreglado en 0.26.0:** Sin partida, los ganchos llaman al juego lo primero; cada lanzamiento sembrado va en su propio método (sin cierres en el gancho); se compara `scene.handle`.
- [x] **P2 · media · sin comprobar.** Enumeradores por interfaz cada frame: `GameSession.Players` es
  `IEnumerable<SessionPlayer>` ([GameSession.cs:78](../src/HKMultiplayer/Session/GameSession.cs#L78)) y
  `PartyState.Members` `IReadOnlyList` ([PartyState.cs:128](../src/HKMultiplayer/Game/Features/PartyState.cs#L128)).
  Lo más caliente: `EnemySync.Update` → `ControllerOf` → `InScene` ([:2285](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2285)),
  una vez por enemigo y frame; `OthersInScene` ([:843](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L843)) en cada
  cambio de estado con sonido, golpe y creación. También `Revive.UpdateBodies` (962, 1000), `PartnerHud` (211, 382) y
  `Skins.LateUpdate` (939). Arreglo: dar `Dictionary<,>.ValueCollection`/`List<T>` y un `TryGetPlayer(id)`.
  - **Arreglado en 0.26.0:** `GameSession.Players` da la colección del diccionario (enumerador struct) y `TryGetPlayer`; `PartyState.Members` es `List`.
- [x] **P3 · media · sin comprobar.** Colecciones que crecen sin límite: `EnemyBars._tracked`
  ([EnemyBars.cs:56](../src/HKMultiplayer/Game/Features/EnemyBars.cs#L56), 79-91) suma en cada `HealthManager.OnEnable` y
  solo se limpia en `Update` después de mirar `ShowEnemyBars` (apagado por defecto), y `Track` la recorre entera cada
  vez. `SceneObjectSync.StartedItems` ([:498-500](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L498)) se llena en
  cada `PersistentBoolItem.Start` también jugando solo y solo se limpia en `Clear` de la sesión. Arreglo: limpiar
  aunque esté apagado y añadir solo con `_active != null`.
  - **Arreglado en 0.26.0:** `EnemyBars` quita los destruidos siempre; `StartedItems` solo se llena con partida.
- [x] **P4 · baja · sin comprobar.** `UdpTransport` crea un `IPEndPoint` por paquete recibido
  ([:318](../src/HKMultiplayer/Net/Transport/UdpTransport.cs#L318)) y `SendTo` lo vuelve a serializar en cada envío. En
  el invitado, `Connect()` + `Send`/`Receive` lo evita.
  - **Revisado en 0.26.0:** Se deja: la memoria la crea el propio `ReceiveFrom` de Mono (arma una dirección nueva en cada datagrama), y `Connect()` haría que el invitado ignorara datagramas de otra dirección.
- [x] **P5 · baja · sin comprobar.** Los nombres de evento y de estado van como texto: `ReadString` crea una cadena por
  mensaje ([EnemySync.cs:3702](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3702), 4011; `FsmStateSync` 6 veces). Un
  índice lo evitaría.
  - **Arreglado en 0.26.0:** `PacketReader.ReadName`: si los mismos bytes (ASCII) ya llegaron, devuelve la misma cadena. Lo usan los nombres de estado, evento y prefab de `FsmStateSync` y `EnemySync`. Con prueba en NetTests.
  - **Comprobado (12/10/2026):** Prueba de NetTests de `ReadName`.
- [x] **P6 · baja · sin comprobar.** `QuestNpcSync.Concerns` y `PlayerDataReads` ([:482](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L482),
  [:690](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L690)) usan reflexión y un iterador `yield` por acción, desde
  los ganchos de `GetBool`/`GetInt`/`SetInt` durante una conversación tras un cambio de otro PC. Guardar el resultado
  por (FSM, campo).
  - **Arreglado en 0.26.0:** `Concerns` calcula una vez por FSM los datos que lee (`_fsmReads`, se vacía al cambiar de sala).

## Red y robustez (N)

- [x] **N1 · media · comprobado.** Un mensaje del invitado de 998 o 999 bytes deja bloqueada esa conexión en el host.
  El invitado escribe hasta 999 de carga ([GameSession.cs:222](../src/HKMultiplayer/Session/GameSession.cs#L222)); al
  reenviarlo, el host añade 2 bytes de remitente ([:400-405](../src/HKMultiplayer/Session/GameSession.cs#L400)) y no
  cabe en 1000. `WriteBytes` falla con el mensaje a medio escribir, y `NetConnection.Begin` ([:154-157](../src/HKMultiplayer/Net/NetConnection.cs#L154))
  ya no deja empezar otro (solo `CloseConnection` llama a `CancelMessage`): todo lo que va a ese jugador falla y, como
  siguen llegando acuses, nunca se corta por tiempo. Lo mismo con cualquier fallo entre `BeginTo` y `EndTo`. El
  disparador más probable es un traspaso con muchas variables (N2). Arreglo: que el invitado reserve 2 bytes, que
  `Begin` cancele un mensaje viejo en vez de fallar, y `try/catch` + `CancelMessage` en `Relay` y `BeginTo`.
  - **Arreglado en 0.26.0:** El invitado deja 2 bytes libres; `Begin` descarta un mensaje viejo a medio escribir en vez de fallar; `Relay` cancela el que falla.
  - **Probado en el juego (12/10/2026):** Prueba nueva en NetTests: un mensaje del invitado del tamaño máximo se reenvía y lo que va detrás llega.
- [x] **N2 · media · sin comprobar.** Código del mod antes de `orig` en ganchos sin `try/catch`: si falla, el juego
  no hace lo suyo. `BeginSceneTransition` ([EnemySync.cs:5506](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5506),
  `ReleaseAll` → `SendHandover`; [DreamSync.cs:115](../src/HKMultiplayer/Game/Features/DreamSync.cs#L115)): la sala no
  cambia. `ReturnToMainMenu` ([Revive.cs:384](../src/HKMultiplayer/Game/Features/Revive.cs#L384)),
  `Fsm.SwitchState` ([EnemySync.cs:5430-5438](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5430)), `Fsm.ProcessEvent`
  ([FsmStateSync.cs:968](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L968), [EnemySync.cs:5464](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5464)),
  `SendEventByName` ([:5495](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5495)) y `HealthManager.Hit` en marionetas
  ([:5411](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5411)). Disparador concreto: `SendHandover`
  ([EnemySync.cs:2583-2655](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2583)) escribe hasta 48 números, 48 sí/no,
  48 textos de hasta 65 bytes y 48 objetos sin mirar el tamaño; una FSM de jefe con muchas variables puede pasar de
  ~1000 bytes y fallar dentro de `BeginSceneTransition`. Arreglo: un ayudante `try { mod } catch { registrar una vez }`
  y luego siempre `orig`; en `SendHandover`, mirar `writer.Remaining` y recortar o partir.
  - **Arreglado en 0.26.0:** `SendHandover` recorta los textos que no caben dejando sitio a lo que va detrás.
- [x] **N3 · media · sin comprobar.** El canal fiable puede quedarse parado para siempre.
  - `NetConnection.ReceiveData` ([:380-408](../src/HKMultiplayer/Net/NetConnection.cs#L380)) solo captura
    `PacketException` y alrededor de todo el bucle: si un registro falla, se pierden los mensajes fiables que iban
    detrás en el mismo paquete, que ya se confirmó y nunca se reenvía.
  - `ReliableReceiver.Receive` ([:216-222](../src/HKMultiplayer/Net/Channels/ReliableReceiver.cs#L216)) llama a
    `sink.Deliver` sin `try/finally`: si falla, no se entrega lo que había llegado desordenado (`DeliverStored`).
  - Sin `catch`: `HandleControl`, `ReceiveLargeChunk` (`BlockCopy` falla si dos trozos del mismo id dicen tamaños
    distintos) y `Listener.OnConnected`/`OnDisconnected` → quienes escuchan `PlayerJoined`/`PlayerLeft`.
  - `NetPeer.Update` ([:69-72](../src/HKMultiplayer/Net/NetPeer.cs#L69)) solo captura `PacketException`: un fallo al
    pasar por los tiempos de espera se salta `SendTick` del resto de conexiones.
  - Arreglo: capturar por registro (la longitud ya se saltó antes de `Deliver`), `DeliverStored` en un `finally` y
    envolver las llamadas a quienes escuchan.
  - **Arreglado en 0.26.0:** `NetConnection.Deliver` captura cualquier fallo por mensaje; `NetPeer` protege cada datagrama, el tick de cada conexión y los avisos de conexión y desconexión.
  - **Comprobado (12/10/2026):** Prueba de NetTests "un fallo al procesar un mensaje no detiene los siguientes".
- [x] **N4 · media · sin comprobar.** Seguridad de las transferencias grandes: `ReceiveLargeChunk`
  ([NetConnection.cs:504-507](../src/HKMultiplayer/Net/NetConnection.cs#L504)) reserva `new byte[total]` (hasta 64 MB)
  con el primer trozo de cada id, sin límite de cuántas a la vez ni caducidad: un compañero (con el código) puede
  agotar la memoria del otro con unos pocos paquetes de 1 KB. Arreglo: 1 o 2 en curso por conexión, límite por tipo y
  caducidad.
  - **Arreglado en 0.26.0:** Como mucho 2 transferencias grandes a medio recibir, caducan a los 30 s y un trozo con otro tamaño total la abandona.
- [x] **N5 · media · sin comprobar.** Saludo (`NetServer.HandleHandshake`, [:86-107](../src/HKMultiplayer/Net/NetServer.cs#L86)):
  un `Hello` sin autenticar desde la dirección de un invitado ya conectado, con claves nuevas, cierra esa conexión (se
  puede echar a alguien suplantando su dirección). Muchos `Hello` con claves nuevas desde una dirección obligan a un
  X25519 + HKDF por paquete en el hilo principal, porque el límite de conexiones a medio abrir se mira después de
  cerrar la anterior: puede congelar al host. Arreglo: nunca soltar una conexión autenticada por un `Hello` y limitar
  los `Hello` por dirección.
  - **Arreglado en 0.26.0:** Un `Hello` desde la dirección de un jugador que envió algo en los últimos 3 s se ignora; como mucho 10 saludos nuevos por segundo.
- [x] **N6 · baja · sin comprobar.** El código de partida (40 bits, 32^8, [RoomCode.cs](../src/HKMultiplayer/Net/Crypto/RoomCode.cs))
  se puede sacar a fuerza bruta sin conexión a partir de la prueba del saludo ([NetServer.cs:117-126](../src/HKMultiplayer/Net/NetServer.cs#L117))
  si alguien llega al puerto. Arreglo: un PAKE (CPace/SPAKE2), o al menos un KDF lento sobre el código y un código
  más largo.
  - **Arreglado en 0.26.0:** La sal es el código estirado con PBKDF2-HMAC-SHA256 de 100.000 vueltas (una vez por código, en otro hilo). `ProtocolVersion` pasa a 2.
  - **Comprobado (12/10/2026):** Pruebas de NetTests de conexión con el código correcto e incorrecto, y las rutas (que se conectan con el código estirado).
- [x] **N7 · baja · sin comprobar.** El nombre se lee con `ReadString(64)` ([NetServer.cs:161](../src/HKMultiplayer/Net/NetServer.cs#L161))
  pero `CleanName` deja 24 caracteres (72+ bytes en UTF-8 para chino, japonés...): el saludo falla y el invitado ve un
  "tiempo agotado" que no explica nada.
  - **Arreglado en 0.26.0:** El nombre y la versión se leen con 128 bytes.
  - **Probado en el juego (12/10/2026):** Prueba nueva en NetTests: un nombre de 24 caracteres chinos se une y se ve entero.
- [x] **N8 · baja · sin comprobar.** `MultiplayerRunner.LateUpdate` ([:233-260](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L233))
  tiene un solo `try/catch` para unas 20 partes: si una falla, se saltan las de detrás (también `session.Update()` si
  falla `SendLocalState`). Y `DetachAll`/`CloseSession` ([:482-523](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L482))
  no aíslan cada `Detach`: si uno falla, `session.Dispose()` no se llama y quedan el socket, el hilo y el puerto
  ocupados.
  - **Arreglado en 0.26.0:** `LateUpdate` va en bloques con su propio `catch` (errores con límite) y `DetachAll` aísla cada parte.
- [x] **N9 · baja · sin comprobar.** El mundo que llega del host se lee con `TypeNameHandling.Auto`
  ([WorldSync.cs:184](../src/HKMultiplayer/Game/World/WorldSync.cs#L184), [WorldStore.cs:55](../src/HKMultiplayer/Game/World/WorldStore.cs#L55)).
  Riesgo bajo (`PlayerData` y `SceneData` no tienen miembros polimórficos), pero con datos de la red mejor `None` o
  una lista de tipos permitidos.
  - **Arreglado en 0.26.0:** El mundo y los personajes que llegan por la red se leen sin `TypeNameHandling` (`WorldStore.NetworkSettings`).

## FSM compartidas, misiones, cinemáticas y presentaciones (F)

- [x] **F1 · media · comprobado.** Un encuentro con diálogo se traba si su controlador se va o se desconecta a mitad.
  En una copia, `OnConvoFinish` ([FsmStateSync.cs:1345-1356](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1345))
  avisa al controlador y descarta `CONVO_FINISH` sin apuntarlo. Si después el controlador sale, este PC pasa a serlo
  ([:511-520](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L511)) y solo repite lo retenido (`ReplayHeld`), que no
  tiene nada: la FSM se queda en el diálogo y el caballero sin control. Hornet en los Bordes del Reino o Grimm: el
  invitado cierra el diálogo, el host se cae o sale. Igual con los avisos descartados en [:1242-1244](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1242)
  (`ABSORB END` del Grimmchild). Arreglo: en la copia, `LocalConvoDone = true` (o "fin de diálogo pendiente") y, al
  pasar a ser controlador, `ReleaseConvo` si estaba puesto; guardar también esos avisos como retenidos.
  - **Arreglado en 0.26.0:** La copia apunta el estado de diálogo que cerró (`ConvoClosedIn`); al pasar a controlar, si sigue en él, espera a los demás y sigue.
  - **Probado en el juego (12/10/2026):** Hornet en los Bordes: el invitado cierra su diálogo y el host sale de la sala; el invitado toma el encuentro, pasa al segundo diálogo y empieza la pelea con control.
- [x] **F2 · media · sin comprobar.** Las copias de palancas y objetos golpeados siguen su cadena solas y luego repiten
  los pasos que llegan. Fuera de `ApplyRemoteState`, `HoldSwitch` deja pasar sus cambios ([FsmStateSync.cs:996](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L996))
  y `OnEvent` todo ([:1208](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1208)): tras entrar en el estado S que
  llegó, su `Wait` → `FINISHED` la lleva a S2. Cuando llega "S2", `OnTransition` solo lo salta si está justo en S2
  ([:1969](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1969)); si no, repite S2 y S3 con sus acciones (avisos a
  puertas no seguidas, sonidos, contadores, quizá el Grub Mimic). Lo que hace sola también se salta `PrepareCopy`
  ([:1125-1146](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1125)), así que sus `SetPlayerData*` y llamadas al
  caballero se hacen en la copia. Es una carrera: el `Update` de PlayMaker va antes que `session.Update`. Arreglo:
  mientras dura `RemoteChainUntil`, retener los cambios de la copia, o saltar un paso por el que ya pasó.
  - **Arreglado en 0.26.0:** La copia apunta los estados por los que pasa sola (`SelfVisited`) y no repite esos pasos cuando llegan.
- [x] **F3 · media · sin comprobar.** Dos `Claim` cruzados dejan a cada PC creyendo que controla el otro: `OnClaim`
  ([FsmStateSync.cs:414-422](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L414)) no desempata. Pasa si el `Claim`
  automático del dueño tras `ClaimGrace` ([:515](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L515)) se cruza con un
  `TakeControl` del invitado, o con 3+ jugadores cuando se va el controlador. Los dos retienen y nadie envía: un
  ascensor o una oleada del Coliseo se quedan parados. Arreglo: número de época en el `Claim`, o gana el id menor.
  - **Arreglado en 0.26.0:** Dos reclamaciones con menos de 1 s entre ellas: gana el número menor en los dos PCs.
  - **No se puede reproducir aquí (12/10/2026):** hacen falta 3 jugadores o más.
- [x] **F4 · baja · comprobado.** `CloseChallengePrompts` ([FsmStateSync.cs:1085](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1085))
  recorre la lista viva `PlayMakerFSM.FsmList` mientras manda `DOWN` y cambia estados; si con eso se enciende o apaga
  alguna FSM, la lista cambia (juego PlayMaker `PlayMakerFSM.cs:363-399`) y el bucle falla. Va antes del `try` de
  `ApplyRemoteState` ([:1014](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1014)): se pierde el estado de los
  Señores Mantis con `LastState` ya cambiado. PlayMaker copia la lista antes (`PlayMakerFSM.cs:472`). Arreglo:
  recorrer una copia.
  - **Arreglado en 0.26.0:** Se recorre una copia de `PlayMakerFSM.FsmList`.
- [x] **F5 · baja · sin comprobar.** Un estado que se repite (S → S) no llega al otro PC: `OnControllerState`
  ([:1908](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1908)) y `OnTransition` ([:1969](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1969))
  lo ignoran si ya está en ese estado (`OnTransition` lee el `from` y no lo usa, [:1960](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1960)).
  Un segundo golpe rápido aún en "Hit" no cuenta y los contadores se desvían. Volver a aplicarlo si `from == to`.
  - **Arreglado en 0.26.0:** `OnTransition` aplica el estado aunque ya esté en él si viene de sí mismo.
  - **No se puede reproducir aquí (12/10/2026):** el aguijón de prueba no llega a golpear la pared y `hitnearest` envía NAIL HIT, no TAKE DAMAGE.
- [x] **F6 · baja · sin comprobar.** El remitente se adivina con el `Fsm.EventData` estático
  (`eventData?.SentByFsm ?? Fsm.EventData?.SentByFsm`, [:1217](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1217),
  [:1437](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1437)), que está viejo con `PlayMakerFSM.BroadcastEvent` desde
  C# (`HAZARD RELOAD` del `GameManager`): si apunta a una copia seguida, el aviso se descarta ([:1268-1278](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L1268)).
  Con `eventData` no nulo, fiarse de él y no usar el estático.
  - **Arreglado en 0.26.0:** Con `eventData`, se usa su remitente aunque sea nulo (en `FsmStateSync` y en `EnemySync`).
- [x] **F7 · baja · sin comprobar.** `SharedCutscenes.OnSkip` ([:504-526](../src/HKMultiplayer/Game/Features/SharedCutscenes.cs#L504))
  pone `skippingCutscene = true` y llama a `SkipCutscene`, que no hace nada si `gameState != CUTSCENE` y no quita la
  marca. A quien llevaron a la cinemática se le cuenta antes de que empiece: con "saltar con uno", un voto temprano no
  la salta para él, la ve solo y su entrada se ignora (`InputHandler.cs:386`). Tampoco respeta
  `NOT_SKIPPABLE_DUE_TO_LOADING`. Arreglo: aplicar el salto solo con `CUTSCENE` (si no, guardarlo y reintentar) y
  quitar la marca si no se hizo.
  - **Arreglado en 0.26.0:** El salto se guarda y se hace cuando la cinemática está en marcha (`gameState` CUTSCENE y no cargando).
  - **Probado en el juego (12/10/2026), en parte:** con "Cualquiera", saltar la cinemática de Mr. Mushroom la salta en los dos a la vez. La carrera (un voto antes de que empiece en el otro PC) no se da con las dos copias en el mismo PC: entran a la vez.
- [x] **F8 · media · descartado en lo grave.** `QuestNpcSync.Recheck` ([:190-216](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L190))
  vuelve al estado inicial desde cualquier estado; solo mira que el caballero local tenga el control, no que el
  personaje esté en reposo. En las FSM de personaje no se aplica `NotOnRecheck` ([:305](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L305))
  y nada se apaga (como hace `PrepareCopy`): los `SetPlayerData`, `Spawn`, `Fling` y `SendEvent` del camino de inicio
  se repiten. Un personaje que suelta un objeto o geo tras devolver el control podría darlo dos veces. Arreglo: apagar
  esas acciones durante la vuelta y solo reiniciar FSM en reposo (esperando un aviso o `ListenFor`).
  - **Volcado de FSM:** Revisado el camino de inicio de todas las FSM de personaje (o de su padre o hijo) que leen un dato del mundo: ninguna suelta geo ni objetos. Lo que se repite es inofensivo: Cornifer (13 salas, `corn_*Left = true`), el ciervo (depende de `travelling`, de cada PC), `Quirrel … [Set Encounters]`, `Shaman Meeting` (`shaman = 2`), `GG_Boss_Door_Entrance`. La excepción es Millibelle (W14). Queda baja.
  - **Revisado en 0.26.0:** Revisado con el volcado (ver arriba): nada da geo ni objetos dos veces; Millibelle ya es de cada personaje (W14).
- [x] **F9 · baja · sin comprobar.** `ApplyRemoteState` y `Recheck` llaman a `SetState` sin mirar `Started` ni si
  está encendida (el juego ignora una FSM no `Active`, PlayMaker `Fsm.cs:2025`). En una apagada, las acciones corren
  en un objeto inactivo y `OnEnable` la devuelve al inicio (`Fsm.cs:1847-1855`); en una sin empezar, `Start()` vuelve a
  entrar en ese estado y sus acciones van dos veces (`Fsm.cs:1873-1884`). Esperar a `fsm.Started && isActiveAndEnabled`.
  - **Arreglado en 0.26.0:** Con la FSM apagada o sin empezar, el estado se guarda (`PendingRemoteState`) y se pone cuando esté en marcha.
- [x] **F10 · baja · sin comprobar.** Los ganchos de lectura vieja y "no avanzar dos veces" de `QuestNpcSync`
  ([:651-686](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L651)) se fían de `controlReqlinquished`, que también
  ponen las presentaciones de jefe (`BossPresentation`, hasta 30 s), el banco y los viajes: en ese rato una FSM puede
  leer el valor viejo o perder una suma de verdad. Contar solo una conversación real.
  - **Arreglado en 0.26.0:** No cuenta como conversación estar quieto por la presentación de otro PC (`BossPresentation.PartnerLocked`) ni sentado.
- [x] **F11 · baja · sin comprobar.** `BossPresentation`: `_sentLock` ([:107](../src/HKMultiplayer/Game/Features/BossPresentation.cs#L107),
  [:130](../src/HKMultiplayer/Game/Features/BossPresentation.cs#L130)) solo se quita en `RegainControl`, y al reaparecer el
  juego devuelve el control directamente (`HeroController.cs:3220-3225`): quien presentaba y murió a mitad no vuelve a
  bloquear al compañero en el siguiente intento (de la familia de J10, otra marca; quitarla al cambiar de sala). Su
  `Update` ([:238](../src/HKMultiplayer/Game/Features/BossPresentation.cs#L238)) también suelta al cambiar de sala el
  jugador local, llamando a `RegainControl` durante una muerte o reaparición.
  - **Arreglado en 0.26.0:** Ver J10; además `Release` no devuelve el control durante una muerte, una reaparición o un cambio de sala.
  - **Probado en el juego (12/10/2026):** el host cae a mitad de la presentación y, al revivir, ninguna marca queda puesta. Salió un bloqueo: si quien controla un encuentro cae a mitad del diálogo, el encuentro esperaba su diálogo para siempre y el compañero se quedaba sin control ante un cuadro vacío. Ahora el diálogo de un caído cuenta como leído; probado: la pelea empieza y el compañero tiene control.
- [x] **F12 · baja · nota.** Dos FSM con el mismo nombre en un objeto chocan en `FsmId` (`_byId` se queda con la
  primera, en orden sin ordenar). Raro.
  - **Arreglado en 0.26.0:** `FsmId` suma el puesto entre las FSM del mismo nombre del objeto (si no es la primera).

## Sesión: unirse, salir y reconectar (S)

- [x] **S1 · alta · comprobado.** Un invitado que sale con "Salir al menú" del juego sigue conectado en el menú
  principal. `ExitedWorld` solo cierra la sesión con el botón del mod (`_leaveOnMenu`) o siendo host
  ([MultiplayerRunner.cs:171-177](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L171), [:415-421](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L415)).
  Igual si el mundo llega fuera del menú o no vale ([WorldSync.cs:177-194](../src/HKMultiplayer/Game/World/WorldSync.cs#L177)):
  vuelve sin cerrar. El mundo solo se envía una vez por conexión, así que seguir conectado no le deja volver, solo
  estorba:
  - desde el menú se sigue informando "en pie" cada frame ([MultiplayerRunner.cs:268-271](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L268)),
    así que `Revive.CanBeDowned` y `FindStanding` ([Revive.cs:478-489](../src/HKMultiplayer/Game/Features/Revive.cs#L478),
    [:630-643](../src/HKMultiplayer/Game/Features/Revive.cs#L630)) lo cuentan: si el host muere, cae en vez de morir y
    sigue a alguien sin sala; nadie lo revive hasta que el invitado se desconecte;
  - si el invitado carga luego una partida suya, solo `SharedProgress` y `PartnerTravel` miran que se juega el mundo del
    host: lo demás corre sobre su partida normal (`SceneObjectSync` pone `Activated` en sus FSM y se guarda en su
    `SceneData`, rompibles igual, `Difficulty` activo, sus muertes pasan a "caído", avatares en salas que se llaman
    igual).
  - Arreglo: un invitado que llega al menú desde el mundo del host siempre hace `Leave()`; si falla `ReceiveWorld`,
    también, con un aviso; una sola guarda "en el mundo compartido" (como `PartnerTravel.InSharedWorld`) para todas las
    capas; y quien no está en el mundo no informa "en pie".
  - **Arreglado en 0.26.0:** Un invitado que llega al menú desde el mundo sale de la partida; si el mundo no se puede cargar (`WorldRejected`), también.
  - **Probado en el juego (12/10/2026):** El invitado sale al menú del juego y el host deja de tenerlo en la partida.
- [x] **S2 · media · sin comprobar.** Quien está cargando o viendo la introducción cuenta como "en pie": un invitado
  nuevo ve la introducción ([CoopSaves.cs:65-107](../src/HKMultiplayer/Game/World/CoopSaves.cs#L65)), 1-2 min si no la
  salta, con la sala vacía y `Standing`. Si alguien muere en ese rato, cae y sigue a quien no está. Arreglo: en
  `FindStanding` y `CanBeDowned`, contar solo a quien ya entró en el mundo (una bandera "en el mundo").
  - **Arreglado en 0.26.0:** Estado nuevo `Joining` (sin caballero, en el menú o en la introducción): no cuenta como en pie.
  - **Probado en el juego (12/10/2026):** Con el invitado "entrando en la partida", el host muere de verdad ("no queda nadie en pie").
- [x] **S3 · media · sin comprobar.** Si la conexión se corta mientras el invitado carga o ve la introducción, sigue
  jugando una copia sin conexión del mundo del host. El aviso de desconexión solo vuelve al menú si
  `HeroController.instance != null` ([MultiplayerRunner.cs:658](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L658)),
  y durante la carga y la introducción es nulo. Todo lo que guarda va a `GuestSaving` sin sesión
  ([WorldSync.cs:203](../src/HKMultiplayer/Game/World/WorldSync.cs#L203)) y se tira ([CoopSaves.cs:240-244](../src/HKMultiplayer/Game/World/CoopSaves.cs#L240)).
  El aviso de "reconectando" sale, pero `UpdateReconnect` espera a `CanStart`, que en partida nunca llega
  ([:580](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L580)). Arreglo: apuntar "volver al menú en cuanto haya
  caballero", o que `ContinueGame`/`LoadGame` enganchados fallen si ya no hay sesión de invitado.
  - **Arreglado en 0.26.0:** Si se corta mientras carga o ve la introducción, vuelve al menú en cuanto hay caballero (`_backToMenuWhenReady`).
  - **Probado en el juego (12/10/2026):** Cortar la sesión mientras el invitado entra: vuelve al menú sin quedarse en una copia sin conexión.
- [x] **S4 · baja · sin comprobar.** Geo regalado que se pierde o pasa a otra partida. `_pendingGeo`
  ([PartyActions.cs:33](../src/HKMultiplayer/Game/Features/PartyActions.cs#L33), 153-160) no se vacía en `Detach`
  ([:52-61](../src/HKMultiplayer/Game/Features/PartyActions.cs#L52)) y `Update` solo corre con sesión: lo que llega sin
  caballero (menú, carga) se paga en la sesión siguiente, quizá otro mundo. Quien regala paga al momento (`TakeGeo`,
  [:91](../src/HKMultiplayer/Game/Features/PartyActions.cs#L91)) pero `SendGeo` lo tira sin devolver si el destinatario
  se fue ([:120-126](../src/HKMultiplayer/Game/Features/PartyActions.cs#L120)). El host no puede comprobar el geo de
  quien regala (un cliente modificado podría crear geo; va con el modelo de confianza actual). "Todo" con 0 geo dice
  "Elige cuánto geo dar".
  - **Arreglado en 0.26.0:** Si el destinatario se fue, se devuelve el geo; lo pendiente se olvida al cerrar la sesión; "No tienes geo".
  - **Probado en el juego (12/10/2026):** Regalo al host (300 → 200 / 19 → 119); a alguien que no está se devuelve; con 0 geo sale "No tienes geo".
- [x] **S5 · baja · sin comprobar.** El personaje del invitado se puede guardar en mal estado: `OnSaveNow`
  ([WorldSync.cs:154-163](../src/HKMultiplayer/Game/World/WorldSync.cs#L154)) lo saca directamente, sin el gancho de
  guardado de `Revive` ([Revive.cs:389-408](../src/HKMultiplayer/Game/Features/Revive.cs#L389)). Y ni `SaveNow` ni el
  autoguardado (`IsQuietMoment`, [:313-318](../src/HKMultiplayer/Game/World/WorldSync.cs#L313)) miran
  `BossSequenceController.IsInSequence`: en un Panteón con amuletos atados, `equippedCharms` está vacío (juego
  `BossSequenceController.cs:294-298`). Si el host sale en ese momento, el invitado pierde todos sus amuletos
  equipados. Arreglo: no guardar durante un Panteón, o `RestoreBindings` sobre una copia antes de sacarlo.
  - **Arreglado en 0.26.0:** Ni `SaveNow` ni el autoguardado durante un Panteón.
  - **Probado en el juego (12/10/2026):** con un Panteón simulado (`bossseq`), el guardado del invitado no se envía. Salió que el guardado del propio juego sí se enviaba: ahora `OnGuestSaving` tampoco envía dentro de un Panteón.
- [x] **S6 · baja · sin comprobar.** Nada impide que dos conexiones usen la misma clave de personaje: `OnIdentify`
  ([WorldSync.cs:216-232](../src/HKMultiplayer/Game/World/WorldSync.cs#L216)) acepta varios `Identify` por conexión y no
  mira si esa clave ya está en uso. Dos copias del juego con el mismo usuario de Windows comparten la clave (la forma
  de probar) y se pisan el personaje; un invitado con el código puede mandar `Identify` sin parar y el host arma y envía
  el mundo entero cada vez en el hilo principal; con 4 jugadores, una reconexión que llega antes de que el host corte
  la vieja recibe "llena" y deja de reintentar ([:535-540](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L535)).
  Arreglo: un `Identify` por conexión, y si la clave ya está en uso, cortar la vieja.
  - **Arreglado en 0.26.0:** Un `Identify` por conexión; la misma clave en otra conexión: gana la nueva.
  - **No se puede reproducir aquí (12/10/2026):** las copias de prueba comparten la misma clave de personaje.
- [x] **S7 · baja · cosmético.** Los manejadores de `PlayerJoined`/`PlayerLeft` de `Wire`
  ([MultiplayerRunner.cs:668-679](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L668)) no se quitan: al cerrar el host
  su partida, `Dispose` → `PlayerLeft` le enseña "X salió de la partida".
  - **Arreglado en 0.26.0:** Los avisos de `PlayerLeft` miran que la sesión siga siendo la actual.
  - **Probado en el juego (12/10/2026):** Al cerrar el host su partida no sale "salió de la partida" (con salidas normales sí).
- [x] **S8 · baja · sin comprobar.** El aviso de desconexión empieza `ReturnToMainMenu` sin más
  ([:658-661](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L658), [:464-474](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L464)):
  puede caer a mitad de un cambio de sala, o mientras ya hay una vuelta al menú del juego en marcha (el caballero dura
  el fundido de 2 s) y empezar otra. Esperar a que no haya cambio de sala y una sola vuelta a la vez.
  - **Arreglado en 0.26.0:** Una sola vuelta al menú cada 10 s y nunca ya en el menú.
- [x] **S9 · baja · sin comprobar.** Dos cargas a la vez: `RunContinueGame` deja el juego en `MAIN_MENU` 2,6 s sin
  caballero (juego `GameManager.cs:3326-3336`), así que `CanStart` es cierto; si el mundo llega mientras el invitado
  carga una partida suya, `StartGuest` empieza otra carga. Ventana pequeña y necesita S1.
  - **Arreglado en 0.26.0:** `CanStart` es falso los 5 s siguientes a `ContinueGame` o `StartNewGame` (el juego sigue 2,6 s en el menú sin caballero). `ReturnToMenu` mira `AtMainMenu`.
  - **Probado en el juego (12/10/2026):** `newgame` y unirse a la vez: el mundo se rechaza y sigue su partida, sin segunda carga.
- [x] **S10 · baja · sin comprobar.** El mundo y los personajes pueden desfasarse: los personajes se guardan en cada
  banco y cada 30 s, el mundo cada 2 min. Si el host se cierra de golpe, los invitados conservan objetos cuyos datos del
  mundo volvieron atrás y los pueden recoger otra vez. Arreglo: `_saveSoon` al recibir un personaje.
  - **Arreglado en 0.26.0:** Al recibir un personaje, el mundo se guarda pronto si hace más de 30 s que no.
  - **Probado en el juego (12/10/2026):** El invitado guarda y el host guarda el mundo enseguida ("Mundo coop guardado").

## Skins (K)

- [x] **K1 · media · sin comprobar.** Elegir una skin que ya tiene un compañero pierde el HUD, el geo y el orbe propios.
  `SetLocal` ([Skins.cs:367-373](../src/HKMultiplayer/Game/Features/Skins.cs#L367)) reutiliza la skin conocida con esa
  huella y solo junta `Name` y `Package`, no `Files`; la huella (`HashOf`, [:431](../src/HKMultiplayer/Game/Features/Skins.cs#L431))
  solo cubre lo que se envía, y la recibida o de la caché no tiene `Hud.png`, `Geo.png` ni `OrbFull.png`
  (`ApplyShared` [:1090](../src/HKMultiplayer/Game/Features/Skins.cs#L1090) y `ApplyOrbFull` [:1158](../src/HKMultiplayer/Game/Features/Skins.cs#L1158)
  miran `_local.Files`). Igual al cambiar entre dos carpetas que solo se diferencian en el HUD. Arreglo: juntar también
  `Files` y rehacer sus texturas si cambian.
  - **Arreglado en 0.26.0:** `MergeLocalFiles` pone en la conocida los archivos propios que falten o sean otros, y se vuelven a aplicar.
  - **Probado en el juego (12/10/2026):** El host elige Rick, que ya llevaba el invitado: su orbe lleno sale con el marco de la skin (captura).
- [x] **K2 · media · sin comprobar.** `_requested` no se limpia si la transferencia falla ([Skins.cs:665-672](../src/HKMultiplayer/Game/Features/Skins.cs#L665),
  [:903-917](../src/HKMultiplayer/Game/Features/Skins.cs#L903)): `Forget` sale antes si la skin nunca llegó; `_waiting`
  tampoco se limpia. Si el dueño se desconecta a mitad y vuelve, nadie la vuelve a pedir ([:591](../src/HKMultiplayer/Game/Features/Skins.cs#L591),
  [:629](../src/HKMultiplayer/Game/Features/Skins.cs#L629)): "(recibiéndola...)" toda la sesión. Arreglo: en
  `OnPlayerLeft`, quitar de `_requested` y `_waiting` las huellas que no están en `_skins` (y quizá un tiempo máximo).
  - **Arreglado en 0.26.0:** Al irse un jugador, su skin pedida que no llegó se puede volver a pedir.
  - **Probado en el juego (12/10/2026):** el invitado se pone una skin nueva y sale a mitad del envío; al volver, el host la pide otra vez y la recibe (24 MB).
- [x] **K3 · baja · sin comprobar.** Activar "ver las skins de los demás" a mitad de sesión no pide nada
  ([Skins.cs:312-316](../src/HKMultiplayer/Game/Features/Skins.cs#L312)): solo se piden en `SetWorn` y con la opción ya
  encendida. En `SetShowOthers(true)`, recorrer `_worn` y cargar de la caché o pedir lo que falte.
  - **Arreglado en 0.26.0:** `SetShowOthers(true)` pide las que faltan (`RequestMissing`).
  - **Probado en el juego (12/10/2026):** Con "ver las skins" apagado el compañero se dibuja con el caballero del juego; al encenderlo, con su skin.
- [x] **K4 · baja · sin comprobar.** Las máscaras del HUD del compañero se crean una vez desde
  `definition.material.mainTexture` ([HudSprites.cs:104](../src/HKMultiplayer/UI/HudSprites.cs#L104)); si ya había un
  `Hud.png` puesto, es la textura de la skin local, y al cambiar o quitar la skin `Forget` la destruye
  ([Skins.cs:911](../src/HKMultiplayer/Game/Features/Skins.cs#L911)): las máscaras del compañero salen en blanco.
  Crearlas desde la textura del juego o rehacerlas al cambiar de skin.
  - **Arreglado en 0.26.0:** Se crean desde la textura del juego (`Skins.VanillaTextureOf`).
- [x] **K5 · baja · sin comprobar.** El cuerpo del caballero local pasa por un bloque de propiedades cada frame
  ([Skins.cs:934](../src/HKMultiplayer/Game/Features/Skins.cs#L934), [:1000-1011](../src/HKMultiplayer/Game/Features/Skins.cs#L1000)),
  también jugando solo y sin skin del mod: escribe `VanillaTextures["Knight"]`, tomada una vez ([:1079](../src/HKMultiplayer/Game/Features/Skins.cs#L1079)).
  Con CustomKnight activo, el cuerpo se queda con la primera textura y no se ven sus cambios de skin; si CustomKnight
  destruye esa textura, el bloque apunta a una destruida. Arreglo: sin skin del mod, quitar `_MainTex` del bloque (o
  usar la del material en vivo) y volver a tomar la del juego si es nula.
  - **Arreglado en 0.26.0:** Sin skin del mod, el cuerpo propio no se toca (si llevaba una, se quita el bloque una vez).
- [x] **K6 · baja · sin comprobar.** `KeepOrbFull` ([Skins.cs:1194-1223](../src/HKMultiplayer/Game/Features/Skins.cs#L1194))
  lee `sprite.name` hasta 3 veces por renderer del orbe y frame con `OrbFull.png` (cada lectura crea una cadena).
  Guardar los `Sprite` del orbe en un conjunto.
  - **Arreglado en 0.26.0:** Lo decidido de cada sprite se guarda (`_orbFullKnown`).
- [x] **K7 · baja · sin comprobar.** El reintento de cada 0,2 s sale caro ([Skins.cs:1065](../src/HKMultiplayer/Game/Features/Skins.cs#L1065),
  1109, 1170): si faltan texturas (p. ej. al quitar la pausa en una sala de jefe, con `CanCreateTextureNow` falso),
  `ApplyShared` hace `FindObjectsOfTypeAll<tk2dSpriteCollectionData>` y `<SpriteRenderer>` con LINQ 5 veces por
  segundo todo el combate. Esperar más entre intentos o no buscar el orbe si no cambió nada.
  - **Arreglado en 0.26.0:** Entre intentos se espera cada vez más, hasta 2 s.
- [x] **K8 · media · sin comprobar.** La animación de muerte sale con el caballero del juego: `HeroController` apaga su
  renderer y enseña `heroDeathPrefab`, otro `tk2dSprite` (juego `HeroController.cs:838-842`, `4245-4261`), y la skin
  solo se pone en el renderer del caballero. Ponerla también en los `tk2dSprite` de `heroDeathPrefab` mientras está
  encendido.
  - **Arreglado en 0.26.0:** `ApplyToDeath` pone la skin propia a los `tk2dSprite` de `heroDeathPrefab` mientras está encendido.
  - **Probado en el juego (12/10/2026):** Con la skin de Rick, la animación de muerte de verdad sale con ella (captura).
- [x] **K9 · baja · sin comprobar.** Un frame con la hoja equivocada: tk2d avanza la animación en
  `tk2dSpriteAnimatorCallbackHooks.LateUpdate`, en orden sin definir respecto a `MultiplayerRunner.LateUpdate` (sin
  `DefaultExecutionOrder`); cuando un cuadro cambia de colección (Knight → Sprint o Unn), puede dibujarse con la textura
  de la anterior. Aplicar en `Camera.onPreCull` o dar al runner un orden tardío.
  - **Arreglado en 0.26.0:** `MultiplayerRunner` con `[DefaultExecutionOrder(10000)]`.
- [x] **K10 · baja · sin comprobar.** `VanillaTextures` se guarda por nombre de colección y nunca se revisa: una
  colección que se carga con la sala y se vuelve a cargar deja de recibir la skin ([Skins.cs:1097](../src/HKMultiplayer/Game/Features/Skins.cs#L1097)
  compara con la textura vieja), y `RestoreShared` o los efectos del compañero podrían recibir una destruida.
  - **Arreglado en 0.26.0:** Se vuelve a tomar si la apuntada ya no existe.
- [x] **K11 · baja · sin comprobar.** Efectos del compañero que salen del pool del juego (`prefab.Spawn`,
  [RemoteEffects.cs:647](../src/HKMultiplayer/Game/Players/RemoteEffects.cs#L647)) no están en `LooseEffects`: si su
  colección es compartida, salen con la skin de hechizos del jugador local.
  - **Revisado en 0.26.0:** Se deja: son objetos del pool del juego; ponerles la skin del compañero haría que, al reciclarse, el jugador local los viera con ella.
- [x] **K12 · baja · sin comprobar.** Si `SaveCached` falla o el juego se cierra a mitad, quedan `<huella>.hksk.tmp`
  en la caché que `GetFiles("*.hksk")` nunca cuenta ni borra.
  - **Arreglado en 0.26.0:** Al guardar en la caché se borran los `.hksk.tmp` de hace más de una hora.
  - **Probado en el juego (12/10/2026):** Al guardar en la caché se borra un `.hksk.tmp` de hace tres horas y se deja uno reciente.
- [x] **K13 · baja · sin comprobar.** `tk2dSprite.UpdateMaterial` crea copias del material por sprite "(using
  keywords)" para los que tienen `materialKeywords`/`materialFloats` (tk2d `tk2dSprite.cs:262-306`); no están en
  `materials` ni `materialInsts`, así que `ApplyShared` y `RestoreShared` no las tocan.
  - **Revisado en 0.26.0:** Se deja: copias de material que tk2d crea solo para sprites con palabras clave; no se vio ninguna en las colecciones del caballero.
- [x] **K14 · baja · nota.** El `cref` de [DownedBody.cs:21](../src/HKMultiplayer/Game/Features/DownedBody.cs#L21) apunta
  a `Skins.ApplyToBody`, que no existe (es `ApplyToBodies`).
  - **Arreglado en 0.26.0:** Corregido el `cref`.
  - **Comprobado (12/10/2026):** Compila sin avisos de documentación.

## Interfaz y ajustes (U)

- [x] **U1 · media · sin comprobar.** El invitado no puede moverse con mando o teclado en *Ajustes de la partida*: sus
  opciones no son interactivas ([SessionMenu.cs:485-487](../src/HKMultiplayer/UI/SessionMenu.cs#L485)) y
  `GameStyledMenu.FirstSelectable/SelectFirst` ([:124-139](../src/HKMultiplayer/UI/GameStyledMenu.cs#L124)) elige la
  primera aunque no lo sea. `MenuOptionHorizontal.OnMove` no hace nada si no es interactiva, ni arriba ni abajo (juego
  `UnityEngine.UI/MenuOptionHorizontal.cs:69-75`): solo puede volver atrás y nunca llega a "Más ajustes" sin ratón. El
  juego usa `GetFirstInteractable` (`MenuScreen.cs:28`). Arreglo: saltar las no interactivas y, al invitado, darle el
  foco en "Más ajustes".
  - **Arreglado en 0.26.0:** El primero que se resalta es uno interactivo y, si hay, que no sea un campo de texto.
  - **Probado en el juego (12/10/2026):** En los ajustes de la partida del invitado lo resaltado es "Más ajustes" (usable) y las opciones son de solo ver.
- [x] **U2 · baja · sin comprobar.** Los campos de texto atrapan el mando y el teclado en el menú Multijugador
  (`AddInput`, [GameStyledMenu.cs:237-297](../src/HKMultiplayer/UI/GameStyledMenu.cs#L237)): en la primera página lo
  primero es el campo del nombre (sin `SetFirst`); `InputField` no tiene `ICancelHandler`, así que Atrás no hace nada,
  y con el campo enfocado `OnUpdateSelected` se come los eventos (juego `HollowKnightInputModule.cs:139-152`): con solo
  mando se queda atascado. Arreglo: foco en el primer botón, un componente con `ICancelHandler` en cada campo, y salir
  del campo con Cancelar/Aceptar del mando.
  - **Arreglado en 0.26.0:** Además, Cancelar sobre un campo de texto sale de él o vuelve atrás (`CancelToBack`).
  - **Probado en el juego (12/10/2026):** Lo primero resaltado es "Hostear un mundo"; con un campo de texto resaltado, Cancelar sale de él y otra vez vuelve al menú principal.
- [x] **U3 · baja · sin comprobar.** `UpdateCheck.Update` crea memoria cada frame toda la partida, también jugando
  solo ([UpdateCheck.cs:138](../src/HKMultiplayer/Game/UpdateCheck.cs#L138)): `NewerAvailable` → `IsNewer` → 2
  `Version.TryParse` con `Split` y subcadenas. Calcularlo una vez al cambiar `Latest`. La comparación es correcta.
  - **Arreglado en 0.26.0:** `NewerAvailable` se calcula al saber la versión, no en cada frame.
- [x] **U4 · baja · sin comprobar.** Los nombres de zona no cambian de idioma: `Zones.Clear()`
  ([Zones.cs:129](../src/HKMultiplayer/Game/Features/Zones.cs#L129)) no lo llama nadie y el HUD ("en <zona>") y
  *Compañeros* se quedan en el primer idioma. Vaciarlo con `GameManager.RefreshLanguageText`.
  - **Arreglado en 0.26.0:** `Zones.Name` vacía la caché si cambia el idioma.
- [x] **U5 · baja · sin comprobar.** Un jefe puede cambiar de tipo a mitad del combate: `Difficulty.IsBoss` vacía toda
  su caché al pasar de 1024 entradas ([Difficulty.cs:304-306](../src/HKMultiplayer/Game/Features/Difficulty.cs#L304)) y
  cada `OnEnable` de la sesión añade una. Tras vaciarla, un jefe sin `unlockBossScene` se vuelve a juzgar por su vida
  de ahora: por debajo de 300 pasa a "enemigo" y pierde la vida extra. Quitar solo las destruidas.
  - **Arreglado en 0.26.0:** Solo se quitan de las cachés los enemigos destruidos.
- [x] **U6 · media · sin comprobar.** El HUD del mod se dibuja encima del inventario, el mapa, los diálogos, los títulos
  de zona y jefe y los fundidos: `HudCanvas` es `ScreenSpaceOverlay` con orden 400 ([HudCanvas.cs:68-70](../src/HKMultiplayer/UI/HudCanvas.cs#L68))
  y `PartnerHud`, `BossBar` y `EnemyBars` solo se esconden con pausa o `gameState != PLAYING` (inventario, mapa rápido
  y diálogos siguen en `PLAYING`). El juego esconde su HUD con el aviso "OUT". Esconderlo también cuando el HUD del juego
  está fuera, con `isInventoryOpen` o durante `IsInSceneTransition`.
  - **Arreglado en 0.26.0:** `HudCanvas.GameHudHidden` (FSM `Slide Out` del HUD del juego): el HUD del compañero y las barras se esconden con el del juego.
  - **Probado en el juego (12/10/2026):** Con el inventario abierto, el HUD del compañero desaparece (captura).
- [x] **U7 · baja · sin comprobar.** Iconos del compañero en el mapa sobre negro: la cuenta es correcta (como
  `GameMap.Update`, juego `GameMap.cs:1158-1176`), pero `TryGetIconPosition` ([PartnerMap.cs:301-318](../src/HKMultiplayer/Game/Features/PartnerMap.cs#L301))
  no mira si la sala o su zona están activas (el mapa rápido apaga las demás zonas, `GameMap.cs:494-520`, y las salas
  sin mapa están apagadas). Pedir `room.activeInHierarchy`.
  - **Arreglado en 0.26.0:** Solo si la sala del mapa está activa.
- [x] **U8 · baja · sin comprobar.** El HUD del compañero puede no salir sin avisar: si se encuentra la colección del
  HUD pero no los nombres de sus sprites (tras un cambio de skin o atlas), `HudSprites.TryLoad`
  ([HudSprites.cs:56-77](../src/HKMultiplayer/UI/HudSprites.cs#L56)) devuelve falso siempre y `PartnerHud` nunca se
  crea (una sola línea en el registro).
  - **Arreglado en 0.26.0:** Se vuelve a intentar cada 10 s, con aviso en el registro.
- [x] **U9 · baja · sin comprobar.** `HostSettings` guarda `settings.json` con `File.WriteAllText`, que no es atómico:
  si el juego se cierra a mitad, ese mundo vuelve a los ajustes por defecto. Escribir a `.tmp` y mover.
  - **Arreglado en 0.26.0:** Se escribe a `.tmp` y se mueve; si falta el archivo y está el `.tmp`, se lee ese.
  - **Probado en el juego (12/10/2026):** Con solo `settings.json.tmp` (cierre a mitad), el host recupera la vida de los jefes a 2× y la reparte.

## Instalador y paquete (I)

- [x] **I1 · alta · comprobado.** Tras una actualización del juego en Steam, desinstalar repone un
  `Assembly-CSharp.dll` viejo y el juego no arranca. Los originales se copian solo en la primera instalación: al
  reinstalar, todo lo del manifiesto se salta ([install.ps1:80-83](../tools/installer/install.ps1#L80),
  [:112](../tools/installer/install.ps1#L112)), y al desinstalar se repone la copia sin mirar nada
  ([uninstall.ps1:65-73](../tools/installer/uninstall.ps1#L65)). Si Steam cambió `Assembly-CSharp.dll` y
  `TeamCherry.Localization.dll`, vuelve la versión anterior y hay que "Verificar integridad". Lo mismo tras una
  desinstalación que no acabó y otra instalación. Arreglo: apuntar la huella de lo instalado (`replaced|ruta|sha256`);
  al desinstalar, reponer solo si el archivo sigue siendo el instalado; al actualizar, si no es ninguno de los
  instalados, copiarlo otra vez como original.
  - **Arreglado en 0.26.0:** `Test-Modded` (los parcheados nombran `ModHooks`): al actualizar se guarda como original el que puso Steam; al desinstalar, si el actual ya no es el parcheado, se deja.
  - **Comprobado (12/10/2026):** `installer-test.sh`: las dos actualizaciones de Steam dejan el `Assembly-CSharp.dll` nuevo.
- [x] **I2 · media · sin comprobar.** La comprobación de versión es floja ([install.ps1:33-47](../tools/installer/install.ps1#L33),
  `Assert-CompatibleGame`): solo mira Unity 6 y `TeamCherry.TK2D.dll`. Un parche 1.5.x futuro con Unity 6 pasa y se le
  copia un `Assembly-CSharp.dll` hecho para 1.5.12620: juego roto. Arreglo: `package.sh` apunta la huella del
  `Assembly-CSharp.dll` original para el que se hizo la API, y el instalador solo sigue si el actual es ese, uno del
  paquete o uno ya apuntado.
  - **Arreglado en 0.26.0:** `Test-GameVersion`: el `Assembly-CSharp.dll` del juego tiene que llevar `1.5.12620`.
- [x] **I3 · baja · sin comprobar.** El consejo de permisos está viejo: [common.ps1:178-179](../tools/installer/common.ps1#L178)
  dice "clic derecho en el **.bat** → Ejecutar como administrador" y desde la 0.24 es un `.exe`; el `app.manifest`
  dice que pide administrador cuando hace falta y nada lo hace. Le pasa a GOG Galaxy en `Program Files (x86)`. Además
  el fallo llega tras `$script:Changing = $true` y se dice "si se paró a medias, desinstala..." sin haber cambiado
  nada: poner `Changing` después de crear la copia y el manifiesto.
  - **Arreglado en 0.26.0:** El mensaje habla del `.exe` y `Changing` se pone cuando ya están la copia y el manifiesto.
- [x] **I4 · baja · sin comprobar.** `libraryfolders.vdf` se lee como ANSI ([common.ps1:77](../tools/installer/common.ps1#L77)):
  `Get-Content` sin `-Encoding UTF8` en PowerShell 5.1 estropea rutas de bibliotecas de Steam con tildes. Añadir
  `-Encoding UTF8`.
  - **Arreglado en 0.26.0:** `libraryfolders.vdf` se lee como UTF-8.
- [x] **I5 · baja · sin comprobar.** `Get-PackageVersion` falla en una carpeta con `[ ]` ([common.ps1:18](../tools/installer/common.ps1#L18)):
  `Test-Path` sin `-LiteralPath` da falso, la versión sale vacía y no sale el aviso de versión nueva. Usar
  `-LiteralPath` ahí y en `Get-Content`.
  - **Arreglado en 0.26.0:** `-LiteralPath` en `Get-PackageVersion`.
- [x] **I6 · baja · sin comprobar.** Códigos de salida engañosos ([setup.ps1:32-38](../tools/installer/setup.ps1#L32)):
  desinstalar da 0 sin nada instalado y con problemas al reponer. Devolver 2 si `Invoke-Uninstall` da `$false`.
  - **Arreglado en 0.26.0:** Desinstalar sin dejarlo limpio devuelve 2.
  - **Comprobado (12/10/2026):** `installer-test.sh` y la prueba de I7 (códigos de salida 0 al desinstalar bien).
- [x] **I7 · baja · sin comprobar.** Desinstalar borra todo `Mods\HKMultiplayer` menos `Skins` ([uninstall.ps1:18-25](../tools/installer/uninstall.ps1#L18)):
  `SkinCache` (es caché, vale) y cualquier archivo que el usuario puso ahí.
  - **Arreglado en 0.26.0:** Se borran la caché (`SkinCache`) y lo que puso el instalador (sus líneas `added`); lo demás se queda y se dice qué. Instalado a mano: además los `.dll`, `.pdb` y `.xml`.
  - **Probado en el juego (12/10/2026):** Desinstalar borra lo del paquete y la caché y deja las skins y un `notas.txt`, diciéndolo. Salió que un mod puesto a mano antes no se devolvía (la regla de I1 se aplicaba a `Mods`): arreglado y probado.
- [x] **I8 · baja · sin comprobar.** Una Modding API puesta a mano antes se guarda como "el original": desinstalar
  repone una API y dice que el juego quedó como estaba. Si la huella del archivo es la del paquete, no tratarlo como
  original y avisar.
  - **Arreglado en 0.26.0:** Si el archivo del juego ya estaba parcheado, se avisa de que la copia no es el original.
- [x] **I9 · baja · sin comprobar.** Otros mods se apartan a `Mods\Disabled` en cada instalación y actualización sin
  preguntar ([install.ps1:150-172](../tools/installer/install.ps1#L150)): uno que el usuario volvió a activar se
  vuelve a apartar (el registro sí lo dice). Listarlos en la confirmación.
  - **Arreglado en 0.26.0:** Antes de instalar, la ventana pregunta con la lista de mods que se van a apartar (`Get-OtherMods`).
- [x] **I10 · baja · sin comprobar.** *Novedades* puede congelar la ventana ([setup.ps1:444](../tools/installer/setup.ps1#L444)):
  si la consulta en segundo plano falló, usa `WebClient.DownloadString` en el hilo de la ventana (hasta ~100 s). Las
  etiquetas que `[Version]` no entiende (`v0.26.0-hotfix`) se ignoran sin avisar.
  - **Arreglado en 0.26.0:** *Novedades* espera la consulta sin parar la ventana (como mucho 10 s); las etiquetas toman los números del principio (`v0.26.0-hotfix` es 0.26.0).
- [x] **I11 · baja · sin comprobar.** `package.sh` toma la versión solo de `const string Version` y no la compara con
  `<AssemblyVersion>` del csproj, que es la que lee `Get-InstalledVersion`: si se olvida subir una, sale mal la
  versión instalada. Fallar si no coinciden.
  - **Arreglado en 0.26.0:** `package.sh` falla si la versión del código y la del `.csproj` no coinciden.
  - **Comprobado (12/10/2026):** `package.sh` comprueba las versiones al generar el paquete.
- [x] **I12 · baja · sin comprobar.** El lanzador con la salida redirigida y sin consola padre
  (`launcher/Program.cs:89-104`): `powershell.exe` abre una consola en la página de códigos OEM y el lanzador lee UTF-8:
  las tildes salen mal.
  - **Arreglado en 0.26.0:** Sin consola padre y con la salida a un archivo, el lanzador crea una consola escondida en UTF-8 que hereda PowerShell.
  - **Probado en el juego (12/10/2026):** Lanzado desde un proceso sin consola con la salida a un archivo: llega en UTF-8 con las tildes bien.
- [x] **I13 · baja · sin comprobar.** Lumafly y Scarab cambian `Assembly-CSharp.dll` con copias `.v`/`.m` y también
  usan `Mods\Disabled`: alternar "vanilla" en ellos desfasa el manifiesto (como I1).
  - **Arreglado en 0.26.0:** El caso del archivo cambiado ya lo trata I1 (y I8 avisa). LEEME, README y README.md dicen que no se mezcle con Lumafly o Scarab y cómo arreglarlo.
- [x] **I14 · baja · sin comprobar.** Solo Windows con Steam o GOG: Mac y Linux no se mencionan en LEEME/README y las
  rutas de Xbox/Game Pass no se buscan (falla bien, con mensaje y *Buscar…*). Decirlo en el README.
  - **Arreglado en 0.26.0:** LEEME, README y README.md: Windows con Steam o GOG; Mac y Linux no; Xbox/Game Pass sin probar.
- [x] **I15 · baja · sin comprobar.** `Get-GameProcesses` ([common.ps1:150-154](../tools/installer/common.ps1#L150))
  salta procesos sin `.Path` (el juego abierto como administrador y el instalador no): el bloqueo sale como error de
  archivo (se trata), y buscar "proceso|process" en el mensaje no vale en otros idiomas (alemán...).
  - **Arreglado en 0.26.0:** Un `hollow_knight` sin ruta legible cuenta como abierto; el archivo en uso se reconoce por el código (32 o 33), no por el texto.
  - **Probado en el juego (12/10/2026):** Con J y K abiertos, instalar en H no bloquea y en J sí; un archivo bloqueado (0x80070020) da el mensaje de archivo en uso. Lo de administrador no se puede probar sin permisos.
- [x] **I16 · baja · pruebas.** `installer-test.sh` no prueba "Steam cambió `Assembly-CSharp.dll` entre instalar y
  desinstalar" (I1). Añadir ese paso.
  - **Arreglado en 0.26.0:** `installer-test.sh` prueba las dos actualizaciones de Steam (con y sin actualizar el mod entre medias).
  - **Comprobado (12/10/2026):** `installer-test.sh` con las dos actualizaciones de Steam: RESULTADO OK.

---

## Revisado y bien (no hace falta tocar)

- Todos los nombres por reflexión existen en esta versión, con una sola sobrecarga; ningún gancho se engancha dos
  veces (todos con su guarda estática) y, sin sesión, todos vuelven al juego normal (salvo P1 y P3).
- `Die()` está copiado dentro de `TakeDamage` como dice el doc; `PlayerData.TakeHealth` solo lo llama el caballero.
- `ObjectPool.Spawn` de 4 argumentos recibe todas las sobrecargas y `ObjectPoolExtensions` (y no se puede copiar
  dentro de otros: tiene `try/catch`). `SaveGame(int, Action)` recibe todas las de guardar. `Breakable.BreakSelf` pasa
  por `Break`. `StalactiteControl.Fall` (21 bytes) queda justo por encima del límite de copia.
- `SceneData` solo tiene tres listas y todo lo que escribe en ellas pasa por `SaveMyState` enganchado
  (`SaveDataUpgradeHandler` escribe directo, pero solo en `RunContinueGame`, antes de la foto). `ResetSemiPersistentItems`
  solo lo llama `PlayerDead` en C# (el resto, FSM). Todos los nombres de `CharacterData` y `Excluded` existen.
- Golpes del compañero que sí llegan al PC que mueve al enemigo: Thorns of Agony, Sharp Shadow, Dreamshield, rayos de
  Grubberfly, hechizos, crías de Glowing Womb, tejedoras y Grimmchild (todos por `HitTaker` y `Hit`). El aturdimiento
  va por `STUN DAMAGE`. Las FSM apagadas ignoran los avisos.
- Muertes: Panteones y Salón de los Dioses excluidos; en un sueño solo se cae con el compañero dentro; las de
  peligro a 0 de vida van por `Die` con el cuerpo en `hazardRespawnLocation`; Alma de Acero: caer no pone
  `permadeathMode = 2` y la caída de todos sí (`PermaDeath`); la sombra y el geo salen de `Hero Death` en el sitio del
  cuerpo. Un caído no recibe golpes ni cruza puertas.
- Red: acuses, ventana contra repeticiones, `nonce` y comprobación de la etiqueta en tiempo constante bien; lectura
  de paquetes con límites; la cola del hilo de recepción con candado y acotada; caché de skins por huella sin rutas de
  fuera; ids de mundo saneados; descompresión con límite y PNG comprobado antes de abrirlo.
- Los ganchos de `PlayerData.GetBool`/`GetInt` no crean memoria en el camino normal (`_beforeRemote.Count == 0`);
  los de `Fsm.SwitchState`/`ProcessEvent` no hacen nada sin partida y solo buscan en un diccionario para las FSM que
  no se siguen; `ObjectPool.Spawn`, `NailSlash` y los del caballero no crean memoria. `PlayerSync`, `SnapshotBuffer` y
  `SendParts` están limpios. Las marcas de reentrada se ponen y quitan en `try/finally`.
- El orden de los campos de los mensajes del mundo depende de que los dos tengan la misma versión, y la sesión ya
  lo exige. El gancho de guardado de `Revive` envuelve al de `CoopSaves` (se instala después) y se ejecuta.
- El alma de un golpe solo la gana quien golpea. Los acompañantes del compañero no tienen colisiones ni scripts.
  `IdleBuzzing`, `ConstrainPosition`, `FaceAngleSimple` y `EnemyBullet` se apagan bien en las marionetas.
- Sin sincronizar a propósito (cada PC el suyo, está bien): `BossStatueLever` (Dios Hogar, poco importante), raíces de esencia (`DreamPlant`), setas de rebote,
  cintas de la Cumbre de Cristal, zonas de reaparición y de cámara, `ShadowGateColliderControl`, `TriggerActivate*`,
  `InfectedBurst*`, `TownGrass`, `PushableRubble`, `DustyPlatform`, `LiftPlatform`, `FakeBat` de Grimm.
- Segunda pasada:
  - `EnemySync`: los 16 tipos de mensaje escriben y leen lo mismo en el mismo orden (también al partirse); todos los
    manejadores miran la sala; el control solo se acepta del dueño de la sala y las muertes de alguien en la sala; las
    marcas estáticas van en `try/finally`; los ids de lo creado llevan el jugador; los diccionarios con objetos de Unity
    se vacían al cambiar de sala.
  - FSM: retener anulando `switchToState` deja la FSM bien; `Continue()` entra sin pasar por el gancho; las transiciones
    globales se tratan; `DisabledActions` se repone; votos de cinemática al irse alguien y el host saliendo, bien.
  - Sesión: los tipos de mensaje 40-107 están una vez cada uno (el 50 libre) con ruta y fiabilidad coherentes; los
    desconocidos se descartan; la versión la mira el host; `PartnerTravel` comprueba que las respuestas vienen del host;
    `WorldStore.Save` usa `.tmp` + `.bak`; recoger personajes antes de salir funciona (4 s, también con el host caído);
    `TestDriver` solo con `-mptest`.
  - Skins: no se crean texturas al cambiar de sala, `Forget` las destruye, cuerpos y efectos del compañero usan
    bloques de propiedades (su skin nunca llega a los materiales compartidos); `AtlasRepair` acota y deja la copia no
    legible; sin una hoja, se usa la del juego.
  - Interfaz: `HostSettings` lee por clave y corrige valores raros (JSON roto, NaN, fuera de rango); la pausa y la
    vuelta atrás coinciden con el juego (`ignoreUnpause` existe); al salir de la sesión se esconde todo el HUD; la
    cuenta de la vida extra coincide con `HealthManager` (`RoundToInt`) y no se desborda; la cuenta del mapa coincide con
    `GameMap.Update`.
  - Instalador: no toca partidas ni mundos (en `LocalLow`); las rutas con espacios, apóstrofo, ñ o corchetes llegan
    bien a `powershell -File`; volver a instalar o desinstalar recupera un fallo a medias; `SHA256SUMS.txt` cubre
    exactamente `files/` y el zip trae todo lo que el instalador espera.
