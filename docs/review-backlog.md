# Revisión contra el juego descompilado (12/10/2026)

Lista de fallos y mejoras que salieron al revisar todo el mod (con la 0.25.0, commit `84fff73`) contra el código del
juego en `decomp/`. Se revisaron seis partes: datos del mundo y del personaje, ganchos y reflexión, enemigos, objetos
de sala, jugador y funciones del coop, y rendimiento y red.

Cómo leerla:

- **Comprobado**: se miró en el código del mod y del juego que pasa así. **Sin comprobar**: lo encontró la revisión
  leyendo el código, pero falta mirarlo a mano o en el juego antes de arreglarlo.
- Prioridad: **alta** (rompe una partida o da ventaja/pierde progreso), **media** (se nota jugando), **baja** (raro,
  cosmético o de mantenimiento).
- Las líneas son las de ese commit; pueden moverse al ir arreglando. Las del juego son de `decomp/Assembly-CSharp/`.
- Al arreglar uno: marcar `[x]`, poner la versión y pasar lo que cambie a `arquitectura.md`.

## Orden sugerido

1. Alta y comprobados: J1, E1, W1, W2.
2. Media y comprobados: W3, E3, O1, O2, H1, N1, P1.
3. Los sin comprobar de prioridad alta o media, mirándolos primero: E2, H2, E4, E5, E6, E11, J2, J3, J4, J5, W4,
   W5, W6, O3, N2, N3, N4, N5, P2, P3.
4. El resto (baja y notas).

Son 84 entradas: enemigos 14, jugador 16, mundo 12, ganchos 9, objetos de sala 18, rendimiento 6, red 9 (más lo
revisado que está bien, al final).

---

## Enemigos (E)

- [ ] **E1 · alta · comprobado.** El daño que no pasa por `HealthManager.Hit` no llega al PC que mueve al enemigo.
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
- [ ] **E2 · alta · sin comprobar.** Una marioneta que muere en este PC no se corrige nunca.
  - `UpdatePuppets` y `Update` la saltan ([EnemySync.cs:1176](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1176),
    [:816](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L816)), `AssignControllers` también
    ([:2319](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2319)) y la foto del dueño de la sala la manda como
    `AlreadyDead` a quien entra ([:886-891](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L886)) aunque en el PC que la
    mueve siga viva.
  - Arreglo: además de E1, que una marioneta no pueda llegar a `Die` en local (prefijo en `Die` que lo impida salvo
    cuando lo pide `ApplyHealth(Died)`).
- [ ] **E3 · media · comprobado.** Los Tiktik (`Climber`) vuelven a andar solos tras cada golpe a la marioneta.
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
- [ ] **E4 · media · sin comprobar.** Un aguijonazo del compañero a un enemigo con escudo hace retroceder al caballero
  del PC que lo mueve.
  - `OnHit` ([EnemySync.cs:5209-5238](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5209)) llama a `Health.Hit`, y
    `HealthManager.Invincible` hace `HeroController.instance.RecoilLeft/Right`, `FreezeMoment(1)` y temblor de cámara
    (juego `HealthManager.cs:359-372`).
  - Afecta a los Baldur, caballeros de musgo con escudo, centinelas pesados y jefes invulnerables.
  - Arreglo: mientras se aplica un golpe de otro (`_applyingRemoteHit`), no hacer el retroceso, la congelación ni el
    temblor.
- [ ] **E5 · media · sin comprobar.** Los golpes que llegan mientras se traspasa un enemigo se pierden.
  - `OnHit` descarta el golpe si es marioneta ([EnemySync.cs:5222](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5222));
    el que lo suelta ya es marioneta ([:2560-2563](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2560)) y el nuevo lo es
    hasta que llega el traspaso ([:822-827](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L822)). Quien golpeó ya
    cobró el alma y vio el impacto.
  - Arreglo: guardarlo como `KeepPendingEvent` y aplicarlo en `TakeOver`, o pasarlo al que lo mueve ahora.
- [ ] **E6 · media · sin comprobar.** El estado del componente `Walker` no se traspasa.
  - Guarda `state` y `currentFacing` privados y pone la velocidad según `currentFacing` (juego `Walker.cs:151-169`,
    `338-367`). Al retomarlo sigue con lo que tenía antes de ser marioneta: anda de espaldas, gira de golpe o anda
    cuando su FSM lo paró.
  - Afecta a los enemigos que usan las acciones `StartWalker`, `StopWalker` y `SetWalkerFacing`.
  - Arreglo: al retomarlo, recalcular `currentFacing` por la escala (`ChangeFacing` o reflexión) y llamar a `Stop` o
    `StartMoving` según el traspaso.
- [ ] **E7 · baja · sin comprobar.** Si la marioneta ya está en el estado traspasado no se aplica `KeepWaited`
  ([EnemySync.cs:2841-2862](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2841)): la espera se queda en la de cuando
  se pausó (lo que se arregló para el Rey Vengamosca no vale si los dos están en el mismo estado). Aplicarlo siempre.
- [ ] **E8 · baja · sin comprobar.** `MuteSpawns` ([EnemySync.cs:3404-3411](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3404))
  solo calla `SpawnObjectFromGlobalPool`, `CreateObject` y `FlingObjectsFromGlobalPool`. Faltan
  `FlingObjectsFromGlobalPoolVel` (el vómito de los Ogros Hongo), `FlingObjectsFromGlobalPoolTime`,
  `SpawnObjectFromGlobalPoolOverTime*`, `SpawnRandomObjects*`, `FlingObject(s)` y `CreateObjectPreInst`: retomar a
  mitad de ese estado puede repetir el ataque.
- [ ] **E9 · baja · sin comprobar.** El aviso de muerte no lleva tipo de ataque ni dirección: `ApplyHealth` usa
  `Die(null, AttackTypes.Nail, true)` ([EnemySync.cs:5131-5138](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5131)).
  Las muertes por `Splatter` (no sueltan geo) o `RuinsWater` (sin geo), y las de ácido, agua o hechizo (otro cadáver),
  salen distintas en el PC de la marioneta (juego `HealthManager.cs:605-614`, `676-682`).
- [ ] **E10 · baja · sin comprobar.** El PC que mueve al enemigo aplica su ventana de 0,2 s sin daño tras un golpe
  (juego `HealthManager.cs:334`, `539`): los golpes del compañero que rechaza ya le dieron alma y efectos en su PC.
- [ ] **E11 · media · sin comprobar.** Doble daño de cosas del mundo que pasan en los dos PCs: el gancho de `Hit`
  reenvía cualquier golpe a una marioneta, no solo los del caballero local. Candidatos: la explosión del cadáver del
  Gruzzer volátil (si tiene `damages_enemy`), estalactitas, ácido. Arreglo: reenviar solo los golpes cuyo `Source` es
  del caballero local, sus hechizos o sus acompañantes.
- [ ] **E12 · baja · sin comprobar.** El aguijón onírico a una marioneta hace `Recoil` sin `_replaying`, que manda
  `RECOIL HORIZONTAL`, `HIT LEFT/RIGHT/UP/DOWN` y `FREEZE IN PLACE`; el gancho de `ProcessEvent` los pasa al PC que lo
  mueve ([EnemySync.cs:5464-5491](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5464)) y su FSM puede girarse o
  encogerse.
- [ ] **E13 · baja · sin comprobar.** `EnemySpawner` tira su `spawnChance` en cada PC por separado (juego
  `EnemySpawner.cs:46-67`): el enemigo puede existir solo en uno y su control caer en el PC donde no está.
- [ ] **E14 · baja · sin comprobar.** Lifeseeds (`ScuttlerControl`): huyen del caballero local, mueren solo en el PC
  de quien los golpea y cada jugador puede cobrar su savia (juego `ScuttlerControl.cs:189-232`). Decidir si es de cada
  uno (como el geo) o se comparte.

## Jugador y funciones del coop (J)

- [ ] **J1 · alta · comprobado.** Un caído recupera los controles tras seguir al compañero a otra sala: puede lanzar
  hechizos y hacer dash invisible.
  - `FinishedEnteringScene` siempre llama a `AcceptInput()` (juego `HeroController.cs:4193-4200`) y
    `SetStartingMotionState` lo pone en `grounded`/`airborne`. Lo llama el mod al llegar
    ([Revive.cs:1261](../src/HKMultiplayer/Game/Features/Revive.cs#L1261)) y el propio juego al entrar por una puerta.
  - `ApplyHiddenState` solo llama a `RelinquishControl` si `!controlReqlinquished`
    ([Revive.cs:1351](../src/HKMultiplayer/Game/Features/Revive.cs#L1351)), y ese dato sigue en verdadero.
  - `CanCast`, `CanJump` y `CanDash` miran `acceptingInput` y `hero_state`, no `controlReqlinquished` (juego
    `HeroController.cs:3416`, `5276`, `5321`). Los hechizos dañan donde está el compañero y gastan alma.
  - Arreglo: en `ApplyHiddenState`, cada frame, `IgnoreInput()` si `acceptingInput` y forzar `no_input`.
- [ ] **J2 · media · sin comprobar.** Un caído que entra siguiendo al compañero por una puerta de abajo se queda en
  `transitionState = DROPPING_DOWN` (juego `HeroController.cs:2899-2904`; solo lo acaba `OnCollisionEnter2D`, y su
  colisión está apagada). Si lo reviven en esa sala no recibe daño hasta cambiar de sala (`CanTakeDamage`,
  `HeroController.cs:5361`). Arreglo: en `RestoreVisibleState` ([Revive.cs:1381-1411](../src/HKMultiplayer/Game/Features/Revive.cs#L1381))
  y tras cada llegada, `transitionState = WAITING_TO_TRANSITION` y `cState.transitioning = false`.
- [ ] **J3 · media · sin comprobar.** El viaje por `dreamGate` mueve la marca real de la Puerta de los sueños.
  `SceneTravel` y `Revive` cambian `dreamGateX/Y` ([SceneTravel.cs:75-77](../src/HKMultiplayer/Game/Features/SceneTravel.cs#L75),
  [Revive.cs:1212-1214](../src/HKMultiplayer/Game/Features/Revive.cs#L1212)) y los reponen al llegar, pero si el destino
  es la sala de la marca (`dreamGateScene`), el juego la coloca en `dreamGateX/Y` al empezar la sala (juego
  `SceneManager.cs:524-528`). Un guardado a mitad del viaje también se los lleva. Arreglo: guardar y vaciar también
  `dreamGateScene` durante el viaje.
- [ ] **J4 · media · sin comprobar.** `DreamSync` se lleva al sueño (y saca de él) a un compañero en pausa, sentado en
  un banco o en un diálogo o tienda: solo mira cambio de sala, muerte, caído y si ya está en un sueño
  ([DreamSync.cs:307-310](../src/HKMultiplayer/Game/Features/DreamSync.cs#L307), [:428-430](../src/HKMultiplayer/Game/Features/DreamSync.cs#L428)).
  Un cambio de sala con el menú de pausa abierto lo deja colgado. Arreglo: las mismas negativas que `StagTogether`
  ([StagTogether.cs:132-139](../src/HKMultiplayer/Game/Features/StagTogether.cs#L132)).
- [ ] **J5 · media · sin comprobar.** Un `atBench` que se queda puesto: los viajes de `SharedCutscenes`
  ([:362-391](../src/HKMultiplayer/Game/Features/SharedCutscenes.cs#L362)) y `DreamSync` sacan de la sala a quien está
  sentado sin levantarlo, y solo la FSM del banco quita `atBench`. Después "Ir con" lo rechaza siempre
  (`PartnerTravel.BusyError`, [:221](../src/HKMultiplayer/Game/Features/PartnerTravel.cs#L221)), revivir en el banco
  no salta (`Revive.UpdateBench` espera el paso de falso a verdadero, [:938-947](../src/HKMultiplayer/Game/Features/Revive.cs#L938))
  y el inventario se abre siempre. Arreglo: no llevar a quien está sentado, o quitar `atBench` al llegar.
- [ ] **J6 · baja · sin comprobar.** `RestoreVisibleState` vuelve a encender todos los renderers que apagó
  ([Revive.cs:1382-1386](../src/HKMultiplayer/Game/Features/Revive.cs#L1382)), aunque el juego los hubiera apagado
  después: si cayó a mitad de Focus, un hechizo o un efecto de amuleto, pueden quedarse pegados. Además se apagan
  `heroLight` y `vignette` (juego `HeroController.cs:708-710`): el caído ve las salas oscuras sin oscuridad. Arreglo:
  ocultar y reponer solo el `MeshRenderer` del caballero y dejar la luz y la viñeta.
- [ ] **J7 · baja · sin comprobar.** Se puede perder la recuperación de la sombra: `KillOwnShade`
  ([ShadeSync.cs:575-577](../src/HKMultiplayer/Game/Features/ShadeSync.cs#L575)) sale antes si no hay prefab o
  caballero (el dueño está en una cinemática o en el ciervo) y no pone `_ownSent = false` ([:593-594](../src/HKMultiplayer/Game/Features/ShadeSync.cs#L593)).
  El host ya la dio por muerta y el dueño no vuelve a avisar: la sombra desaparece para los demás y no devuelve el geo.
  Arreglo: poner `_ownSent = false` en un `finally` y reintentar más tarde.
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
- [ ] **J9 · baja · sin comprobar.** Caer a mitad de Crystal Heart, Desolate Dive o Cyclone Slash: `OnHeroDie` no corta
  nada ([Revive.cs:430-455](../src/HKMultiplayer/Game/Features/Revive.cs#L430)) y las FSM `Superdash` y
  `Spell Control` pueden seguir (sonido en bucle, velocidad) y retomarse al revivir. Probar; si pasa, mandar sus avisos
  de cancelar o `hero.ResetState()` al caer.
- [ ] **J10 · baja · sin comprobar.** `BossPresentation._localWantsLock` ([:106](../src/HKMultiplayer/Game/Features/BossPresentation.cs#L106),
  [:212](../src/HKMultiplayer/Game/Features/BossPresentation.cs#L212)) se queda en verdadero si a un `RelinquishControl`
  no le sigue un `RegainControl` (el juego a veces devuelve el control directamente, `HeroController.cs:3222`,
  `3225`): el siguiente bloqueo por el compañero no devolvería el control. Apuntar aparte "este bloqueo nos lo quitó"
  y limpiarlo al entrar en una sala.
- [ ] **J11 · baja · sin comprobar.** `StagTogether` pone `Fade` aunque el ciervo de quien recibe no esté en esa
  estación, o esté en un diálogo o reapareciendo tras pinchos ([StagTogether.cs:146-158](../src/HKMultiplayer/Game/Features/StagTogether.cs#L146));
  la cinemática usa su `stagPosition`.
- [ ] **J12 · baja · sin comprobar.** La copia de la sombra del compañero solo lee del dueño `GetPlayerDataInt` y
  `shadeMapZone`: un `GetPlayerDataBool` (Void Heart, amuletos) o lo que lean FSM hijas usaría los datos del jugador
  local.
- [ ] **J13 · baja · sin comprobar.** Salas de sueño que no estén marcadas `DREAM_WORLD` (quizá la Radiancia o la
  Pesadilla de Grimm): `CanBeDowned` ([Revive.cs:495](../src/HKMultiplayer/Game/Features/Revive.cs#L495)) las trataría
  como salas normales.
- [ ] **J14 · baja · sin comprobar.** Salir de la partida estando caído en Alma de Acero evita la muerte (truco).
- [ ] **J15 · baja · sin comprobar.** El avatar no tiene colisión: las salpicaduras al entrar en agua o ácido no salen
  en el compañero.
- [ ] **J16 · baja · sin comprobar.** `DownedBody` repite la animación "Death" cada vez que se vuelve a entrar en la
  sala y mira siempre a la izquierda.

## Datos del mundo y del personaje (W)

- [ ] **W1 · alta · comprobado.** `maxMP` se comparte y es de cada jugador. No está en `CharacterData` ni en
  `Excluded`. `AddMPCharge` lo reescribe en cada ganancia de alma: 66 con la sombra fuera (`soulLimited`), 99 sin ella,
  33 con alma atada (juego `PlayerData.cs:4804-4815`); también `StartSoulLimiter`/`EndSoulLimiter` (4877-4886). Los dos
  PCs se lo pisan sin parar y `ReviveCost` ([Revive.cs:1543-1547](../src/HKMultiplayer/Game/Features/Revive.cs#L1543))
  y `Vitals` ([:153](../src/HKMultiplayer/Game/Features/Vitals.cs#L153)) leen el del otro: puede volver el "nadie puede
  revivir" con la sombra fuera. Arreglo: a `CharacterData` (estado).
- [ ] **W2 · alta · comprobado.** El progreso de Dios Hogar no se guarda para el invitado: los 45
  `statueState*` (`BossStatue.Completion`), `bossDoorStateTier1-5` (`BossSequenceDoor.Completion`) y
  `currentBossStatueCompletionKey` son structs o texto, y `SharedProgress` solo mira `bool`, `int` y `List<string>`
  ([SharedProgress.cs:99-101](../src/HKMultiplayer/Game/World/SharedProgress.cs#L99)). Lo escriben
  `BossChallengeUI.cs:268-283`, `BossSequenceDoor.cs:213`, `BossSequenceController.cs:454` y `BossStatue.cs:166-201`.
  Decidir: de cada uno (a `CharacterData`; el JSON guarda los structs) o del mundo (un tipo nuevo en `SharedProgress`
  que junte con "o" cada bool).
- [ ] **W3 · media · comprobado.** `blockerHits` (lo que aguanta Baldur Shell) se comparte: los bloqueos de uno
  rompen el escudo del otro y el banco de uno lo repone a los dos (juego `PlayerData.cs:4648`, `HeroController.cs:2330`).
  Arreglo: a `CharacterData`.
- [ ] **W4 · media · sin comprobar.** Carrera en las listas: al aplicar un `ListAdd` de otro PC,
  `_listCounts[i] = list.Count` ([SharedProgress.cs:476-480](../src/HKMultiplayer/Game/World/SharedProgress.cs#L476)).
  Si este PC añadió algo hace menos de 0,2 s y aún no se comparó, nunca se envía (`scenesVisited`,
  `scenesGrubRescued`, `scenesFlameCollected`, `unlockedBossScenes`...). Arreglo: avanzar la cuenta solo si no había
  nada local pendiente; el `Contains` de quien recibe ya quita los repetidos.
- [ ] **W5 · media · sin comprobar.** Los números compartidos van como valor absoluto
  ([SharedProgress.cs:239-245](../src/HKMultiplayer/Game/World/SharedProgress.cs#L239), [:463-468](../src/HKMultiplayer/Game/World/SharedProgress.cs#L463)):
  si los dos suman al mismo contador a la vez (RTT + 0,2 s), se pierde una suma. `grubsCollected` (recompensas del
  Padre de las Larvas), `kills*` del diario (`EnemyDeathEffects.cs:270-274`), `journalEntriesCompleted`,
  `journalNotesCompleted` (`:288`) y `flamesCollected`. Una muerte solo local de una marioneta (E2) y la real después
  cuentan dos veces. Arreglo: enviar la diferencia en los contadores o que el host los recalcule (`grubsCollected`
  desde `scenesGrubRescued.Count`).
- [ ] **W6 · media · sin comprobar.** `PersistentIntData` puede ir hacia atrás: los ganchos de `PersistentBoolData` y
  de las rocas de geo pasan por `KeepActivated`/`KeepRockHits`, el de enteros no
  ([SharedProgress.cs:276-279](../src/HKMultiplayer/Game/World/SharedProgress.cs#L276)). Una sala cargada antes de que
  llegue una subida guarda su valor viejo al salir (juego `PersistentIntItem.cs:62-67`), el host lo guarda y nunca se
  envían bajadas. Arreglo: un `KeepInt` que se quede con el mayor conocido (comprobar antes que ningún
  `PersistentIntItem` baje a propósito, p. ej. una palanca de varias posiciones).
- [ ] **W7 · baja · sin comprobar.** `ReturnToMainMenu` llama a `TimePasses` justo antes de salir (juego
  `GameManager.cs:3358`, `1021-1083`) y la comparación de cada 0,2 s puede no llegar a enviarlo. Tres dependen de los
  datos de quien sale: `fatGrubKing` (`finalGrubRewardCollected`), `extraFlowerAppear` (`xunFlowerGiven`) y
  `nailsmithCorpseAppeared` (`nailsmithKilled`). `brettaPosition` se tira al azar en cada PC y gana el último.
  Arreglo: forzar la comparación en `CoopSaves.OnSaveGame` antes de `GuestSaving`.
- [ ] **W8 · baja · sin comprobar.** El guardado y la carga del mundo coop se saltan los ganchos de la Modding API:
  `OnLoadLocalSettings`, `OnAfterSaveGameLoad`, `OnSavegameLoad`, `OnBeforeSaveGameSave`, `OnSaveLocalSettings`,
  `OnSavegameSave`, además del volcado de logros y `profileID` (juego `GameManager.cs:2708-2826`, `2921-2964`;
  [CoopSaves.cs:202-257](../src/HKMultiplayer/Game/World/CoopSaves.cs#L202)). Otros mods no guardan ni cargan sus datos
  en un mundo coop y `gm.moddedData` queda el de la última ranura normal.
- [ ] **W9 · baja · sin comprobar.** `WorldStore.SaveCharacter` borra el archivo y luego mueve el nuevo
  ([WorldStore.cs:202-206](../src/HKMultiplayer/Game/World/WorldStore.cs#L202)): si el juego se cierra en medio, el
  invitado se queda sin personaje (`LoadCharacter` no mira el `.tmp`) y empieza uno nuevo. Arreglo: `File.Replace` o
  un `.bak` como en `Save`.
- [ ] **W10 · baja · nota.** `permadeathMode` se comparte. Hoy los mundos coop empiezan con `StartNewGame()` y no
  hay Alma de Acero; si algún día se permite, la muerte de un invitado (juego `HeroController.cs:4248-4250`) marcaría
  el mundo del host como perdido.
- [ ] **W11 · baja · sin comprobar.** Grimmchild para quien entra después: `GrimmchildGift` solo actúa si el jugador
  recibe el amuleto con el otro conectado ([GrimmchildGift.cs:67-73](../src/HKMultiplayer/Game/Features/GrimmchildGift.cs#L67)).
  Un invitado que entra luego no lo recibe, pero `grimmChildLevel` y la Compañía son del mundo y `metGrimm` es suyo:
  la FSM de la tienda puede comportarse raro. `OnGift` tampoco mira si se juega un mundo coop.
- [ ] **W12 · baja · sin comprobar.** Campos compartidos que parecen de inventario: `bossStatueTargetLevel` (lo que
  se elige en el menú del Salón, `BossChallengeUI.cs:284`), `hasGodfinder` y `hasJournal`. Probablemente da igual.

## Ganchos (H)

- [ ] **H1 · media · comprobado.** Los enemigos vuelven al cruzar un punto de reaparición. Los ganchos a
  `SetBenchRespawn` ([EnemySync.cs:5529-5540](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5529)) llaman a
  `OnLocalRest`, pero el banco no los usa: los llama `RespawnTrigger.OnTriggerEnter2D` (juego `RespawnTrigger.cs:55`,
  `62`; los que no son de un solo uso, cada vez que se cruzan) y `HeroController.SetBenchRespawn` (`:2141`). El host
  devuelve los enemigos antes de tiempo. El banco y la muerte ya van por `ResetSemiPersistentItems`. Arreglo: quitar
  los tres ganchos (y su comentario, que dice lo contrario).
- [ ] **H2 · alta · sin comprobar.** `QuestNpcSync` engancha `PlayerData.GetBool`, `GetInt` y `SetInt` en `Attach`
  ([QuestNpcSync.cs:83](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L83), [:562-576](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L562)).
  Son métodos de una línea (8-9 bytes, juego `PlayerData.cs:4534`, `4576`, `4581`), como el `Die()` que el doc dice
  que Mono copia dentro de quien lo llama. Si quien los llama (`GameManager.GetPlayerDataBool`,
  `SetPlayerDataInt`, `GameManager.cs:1137`, `1167`) ya se compiló antes de la primera sesión (tras una partida
  normal en el mismo proceso), el gancho no se ve y vuelven los fallos de misión que avanzan dos veces, solo en ese
  caso. Arreglo: usar `ModHooks.GetPlayerBoolHook`, `GetPlayerIntHook` y `SetPlayerIntHook` (`ModHooks.cs:72-78`;
  también cubren `IncrementInt`, `IntAdd` y `DecrementInt`, que llaman a `ModHooks.SetPlayerInt`), o al menos
  engancharlos al cargar el mod. En general, mover todos los `InstallHooks()` de `Attach` a `MultiplayerRunner.Awake`
  (ya miran `_active`).
- [ ] **H3 · baja · sin comprobar.** `ObjectPool.Spawn` puede avisar dos veces del mismo objeto: si el pool no tiene
  copia del prefab, `Spawn` llama a `CreatePool` y otra vez a `Spawn` (juego `ObjectPool.cs:631`), que pasa por el
  gancho. `EnemySync` ([:5403](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5403)) apunta dos creaciones con dos ids
  (el otro PC tendría una copia huérfana) y `PlayerEffects` ([:761](../src/HKMultiplayer/Game/Players/PlayerEffects.cs#L761))
  envía el efecto dos veces. Arreglo: contar la profundidad y avisar solo en la llamada de fuera.
- [ ] **H4 · baja · sin comprobar.** Los avisos tras `orig` en `Fsm.SwitchState`
  ([EnemySync.cs:5442](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5442), [PlayerEffects.cs:753](../src/HKMultiplayer/Game/Players/PlayerEffects.cs#L753))
  pueden llegar al revés: entrar en un estado puede provocar otro cambio dentro (juego PlayMaker `Fsm.cs:2339-2343`) y
  el último enviado sería el viejo. Arreglo: avisar antes de `orig` (como `FsmStateSync`) o comprobar que
  `ActiveState` sigue siendo ese.
- [ ] **H5 · baja · sin comprobar.** El gancho de `ProcessEvent` de `EnemySync` (hacia [:5464-5490](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L5464))
  lee `Fsm.EventData.SentByFsm` (estático) después de `orig`, y los avisos de dentro lo cambian (juego PlayMaker
  `Fsm.cs:2084`). Guardarlo antes de llamar a `orig`.
- [ ] **H6 · baja · sin comprobar.** `BossPresentation` solo engancha `RelinquishControl`; falta
  `RelinquishControlNotVelocity` (juego `HeroController.cs:3312`), si alguna FSM lo usa.
- [ ] **H7 · baja · sin comprobar.** `QuestNpcSync` no trata `DeactivateIfPlayerdataFalseDelayed`.
- [ ] **H8 · baja · sin comprobar.** Vueltas al menú que no pasan por los ganchos de `ReturnToMainMenu`
  (`CollectGuestsThen`, `CancelDowned`): `QuitToMenu` (juego `GameManager.cs:3419`), `EmergencyReturnToMenu` (`3513`)
  y la muerte en Alma de Acero (va a `PermaDeath`).
- [ ] **H9 · nota.** El gancho de `Die` tiene que seguir antes que el de `TakeDamage` ([Revive.cs:323](../src/HKMultiplayer/Game/Features/Revive.cs#L323)
  y [:333](../src/HKMultiplayer/Game/Features/Revive.cs#L333)): MonoMod compila la copia de `TakeDamage` que usa como
  `orig` al añadir el gancho. Bien hoy, pero frágil: dejar un comentario.

## Objetos de sala (O)

- [ ] **O1 · media · comprobado.** Las estalactitas pueden no caer en el otro PC. Al cargar la sala se apuntan por
  `ScenePaths.Hash` ([SceneObjectSync.cs:403-406](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L403)) y al caer
  se vuelve a calcular ([:509](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L509)). La huella usa el puesto entre
  hermanos ([ScenePaths.cs:15-17](../src/HKMultiplayer/Game/Scene/ScenePaths.cs#L15)), que cambia si muere un enemigo
  de la raíz (`EnemyDeathEffects.cs:235`, `HealthManager.cs:613` lo destruyen) o si `EnemySync` suelta hijos de su
  padre ([EnemySync.cs:1612](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L1612), 1935, 2941, 4585). Nido Profundo y
  Cumbre de Cristal. Arreglo: buscar el id apuntado por referencia (diccionario `StalactiteControl → uint`).
- [ ] **O2 · media · comprobado.** El puente de la Ciudad de las Lágrimas no se abre en el otro PC. `BridgeLever`
  (`cityBridge1`, `cityBridge2`) es C# sin FSM ni `PersistentBoolItem` (juego `BridgeLever.cs:34-90`): solo mira el
  dato en `Start`, así que el compañero en la sala no puede cruzar hasta volver a entrar. Arreglo: aviso "palanca
  golpeada"; al recibirlo, `activated = true`, `bridgeCollider.enabled = true` y `Open` de cada sección (sin
  `ROAR ENTER` y sin volver a escribir el dato).
- [ ] **O3 · media · sin comprobar del todo.** `HazardSync.OnPhases` trata toda animación como un bucle
  ([HazardSync.cs:563-572](../src/HKMultiplayer/Game/Scene/HazardSync.cs#L563)): en una que no se repite y ya acabó,
  `normalizedTime` pasa de 1 y la parte decimal casi nunca coincide, así que `Play(state, 0, frac)` la vuelve a
  empezar cada segundo (repite sus eventos y lo que encienda o apague). Si el otro está en otro estado, lo fuerza al
  del dueño, y hay animadores que son de cada PC: `DreamPlatform` aparece o se esconde según el caballero local (juego
  `DreamPlatform.cs:26-74`), `BossDoorCage` pone "Unlock". Si `DreamPlatform` apaga su colisión con la animación, la
  plataforma puede desaparecer bajo el compañero. Arreglo: alinear solo si el estado es un bucle en los dos y no forzar
  otro estado en una animación que no se repite.
- [ ] **O4 · baja · sin comprobar.** Al entrar en una sala, los `Breakable` ya rotos se rompen en directo (`Break`,
  no hay `SetBroken` para ese tipo, [BreakableSync.cs:415-418](../src/HKMultiplayer/Game/Scene/BreakableSync.cs#L415)):
  cada uno suena, tiembla la cámara (`EnemyKillShake`, juego `Breakable.cs:420-437`) y saltan los trozos. Lo roto en
  directo por el compañero también mueve la cámara de este PC esté donde esté. Arreglo: `SetBroken` para `Breakable`
  (`isBroken = true`, `SetStaticPartsActivation(true)`, apagar `bodyCollider`).
- [ ] **O5 · baja · sin comprobar.** Plataformas que solo activa el caballero local: `FlipPlatform` (Cumbre de
  Cristal, se gira con pinchos 4 s, juego `FlipPlatform.cs:65-78`) y `DropPlatform` (capa 9, `DropPlatform.cs:42-48`).
  El daño es justo en cada PC, pero el compañero parece estar sobre pinchos o en el aire. Arreglo: aviso "activada" con
  su id y arrancar la misma corrutina.
- [ ] **O6 · baja · sin comprobar.** Los huevos de Cañón de Niebla (`JellyEgg`, también el que explota) se rompen solo
  en un PC (juego `JellyEgg.cs:58-75`): el compañero sigue viendo el huevo y le explota después.
- [ ] **O7 · baja · sin comprobar.** La primera pasada de `FsmStateSync.Scan` ([:620-628](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L620))
  no descarta los objetos de `DontDestroyOnLoad` ni los del caballero (`AddBattleListeners` sí, [:691-693](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L691)):
  una FSM persistente con "Button" o "Switch" en el nombre se seguiría. Añadir `buildIndex < 0` e `IsChildOf(hero)`.
- [ ] **O8 · baja · sin comprobar.** Ids distintos para objetos clonados en la raíz: los `fsm:` de `SceneObjectSync`
  ([:362](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L362)) usan `ScenePaths.Hash` y `FsmStateSync.FsmId`
  ([:894-897](../src/HKMultiplayer/Game/Scene/FsmStateSync.cs#L894)) usa `PlacedId`. Un rompible de FSM clonado en
  partida tendría un id que depende del orden.
- [ ] **O9 · baja · sin comprobar.** Dos golpes a la vez a la misma roca de geo cuentan uno: se envía el número de
  golpes absoluto ([SceneObjectSync.cs:309](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L309), [:435](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L435)).
  Enviar la resta.
- [ ] **O10 · baja · sin comprobar.** `BreakableSync._sendSnapshot` no se pone a cero al cambiar de sala
  ([:255-271](../src/HKMultiplayer/Game/Scene/BreakableSync.cs#L255)), al contrario que `SceneObjectSync.Clear`
  ([:179](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L179)): una petición de foto puede pasar a la sala
  siguiente.
- [ ] **O11 · baja · sin comprobar.** `HazardSync.Scan` para en `MaxHazards` y `FindObjectsByType` no ordena
  ([:216-218](../src/HKMultiplayer/Game/Scene/HazardSync.cs#L216)): en una sala con más de 160 animadores cada PC puede
  seguir otros.
- [ ] **O12 · baja · sin comprobar.** El botín puede salir distinto: `DropSeeds` calcula la huella en el momento del
  lanzamiento ([DropSeeds.cs:88](../src/HKMultiplayer/Game/Scene/DropSeeds.cs#L88), [:109](../src/HKMultiplayer/Game/Scene/DropSeeds.cs#L109)),
  con el mismo problema que O1: tras morir un enemigo de la raíz, cada PC usa otra semilla. Solo cosmético. Arreglo:
  guardar la clave por `Transform` al cargar la sala (arregla también P1).
- [ ] **O13 · baja · sin comprobar.** Ids frágiles al cargar la sala: si `Shiny Control` destruye (en vez de apagar) un
  objeto brillante que uno ya recogió, el puesto de los objetos de la raíz cambia entre jugadores y fallarían los ids
  de todo lo que va detrás (rompibles, peligros, FSM, la foto para quien entra). Lo encontrado en la segunda pasada
  (1,5 s) se apunta después de posibles cambios. Arreglo posible: ids por nombre y posición (como `PlacedId`) o sin el
  puesto de la raíz cuando el nombre es único.
- [ ] **O14 · baja · sin comprobar.** Una `VinePlatform` que cae por un corte llegado de otro PC (o de la foto) cae en
  directo en quien lo recibe, y su `enemyDetector` mata con `Die()` a los enemigos de ese PC que tiene debajo (juego
  `VinePlatform.cs:101-108`): muertes dobles o solo locales.
- [ ] **O15 · baja · sin comprobar.** Plataformas que se desmoronan y vuelven, llevadas por una FSM sin `Activated`
  ni nombre de palanca: probablemente no se copian y cada PC tiene la suya (como O5).
- [ ] **O16 · baja · sin comprobar.** Plataformas sólidas movidas por FSM (`iTweenMoveTo/By/Add/Update`,
  `MoveTowards`): el doc dice que no hay en las zonas revisadas, y en el C# no hay ninguna aparte de las ya tratadas,
  pero en las FSM no se puede ver desde `decomp/`. Una orden del modo de prueba que registre colisiones no trigger que
  se mueven sin ser enemigo, caballero ni ascensor lo resolvería.
- [ ] **O17 · baja · visual.** `SlopePlat` pone su altura según la x del caballero local (juego `SlopePlat.cs:20-35`):
  el compañero encima se ve desplazado.
- [ ] **O18 · baja · nota.** Los ids usan `string.GetHashCode` en algunos sitios: igual en la misma versión de Mono
  (la sesión ya exige la misma versión del mod), pero FNV sería más seguro.

## Rendimiento (P)

- [ ] **P1 · media · comprobado.** `DropSeeds` crea memoria en acciones de PlayMaker muy frecuentes, también jugando
  solo ([DropSeeds.cs:35-95](../src/HKMultiplayer/Game/Scene/DropSeeds.cs#L35)): `SendRandomEvent.OnEnter`,
  `FlingObject.OnEnter` y los `FlingObjectsFromGlobalPool*` crean un cierre (`() => orig(self)`) en cada llamada antes
  de mirar `Enabled`; `SeededAction` hace `IsEnemyObject` y `ScenePaths.Hash` (cadena con `Insert(0, …)`) antes de
  mirarlo; `SpawnAndFling` lee `config.Prefab.name` y `Seeded` lee `scene.name` en cada lanzamiento. Arreglo: si
  `!Enabled` (o no es `Shiny Control`), `orig(self)` lo primero; pasar `orig`/`self` sin cierres; comparar
  `scene.handle` en vez del nombre.
- [ ] **P2 · media · sin comprobar.** Enumeradores por interfaz cada frame: `GameSession.Players` es
  `IEnumerable<SessionPlayer>` ([GameSession.cs:78](../src/HKMultiplayer/Session/GameSession.cs#L78)) y
  `PartyState.Members` `IReadOnlyList` ([PartyState.cs:128](../src/HKMultiplayer/Game/Features/PartyState.cs#L128)).
  Lo más caliente: `EnemySync.Update` → `ControllerOf` → `InScene` ([:2285](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L2285)),
  una vez por enemigo y frame; `OthersInScene` ([:843](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L843)) en cada
  cambio de estado con sonido, golpe y creación. También `Revive.UpdateBodies` (962, 1000), `PartnerHud` (211, 382) y
  `Skins.LateUpdate` (939). Arreglo: dar `Dictionary<,>.ValueCollection`/`List<T>` y un `TryGetPlayer(id)`.
- [ ] **P3 · media · sin comprobar.** Colecciones que crecen sin límite: `EnemyBars._tracked`
  ([EnemyBars.cs:56](../src/HKMultiplayer/Game/Features/EnemyBars.cs#L56), 79-91) suma en cada `HealthManager.OnEnable` y
  solo se limpia en `Update` después de mirar `ShowEnemyBars` (apagado por defecto), y `Track` la recorre entera cada
  vez. `SceneObjectSync.StartedItems` ([:498-500](../src/HKMultiplayer/Game/Scene/SceneObjectSync.cs#L498)) se llena en
  cada `PersistentBoolItem.Start` también jugando solo y solo se limpia en `Clear` de la sesión. Arreglo: limpiar
  aunque esté apagado y añadir solo con `_active != null`.
- [ ] **P4 · baja · sin comprobar.** `UdpTransport` crea un `IPEndPoint` por paquete recibido
  ([:318](../src/HKMultiplayer/Net/Transport/UdpTransport.cs#L318)) y `SendTo` lo vuelve a serializar en cada envío. En
  el invitado, `Connect()` + `Send`/`Receive` lo evita.
- [ ] **P5 · baja · sin comprobar.** Los nombres de evento y de estado van como texto: `ReadString` crea una cadena por
  mensaje ([EnemySync.cs:3702](../src/HKMultiplayer/Game/Scene/EnemySync.cs#L3702), 4011; `FsmStateSync` 6 veces). Un
  índice lo evitaría.
- [ ] **P6 · baja · sin comprobar.** `QuestNpcSync.Concerns` y `PlayerDataReads` ([:482](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L482),
  [:690](../src/HKMultiplayer/Game/World/QuestNpcSync.cs#L690)) usan reflexión y un iterador `yield` por acción, desde
  los ganchos de `GetBool`/`GetInt`/`SetInt` durante una conversación tras un cambio de otro PC. Guardar el resultado
  por (FSM, campo).

## Red y robustez (N)

- [ ] **N1 · media · comprobado.** Un mensaje del invitado de 998 o 999 bytes deja bloqueada esa conexión en el host.
  El invitado escribe hasta 999 de carga ([GameSession.cs:222](../src/HKMultiplayer/Session/GameSession.cs#L222)); al
  reenviarlo, el host añade 2 bytes de remitente ([:400-405](../src/HKMultiplayer/Session/GameSession.cs#L400)) y no
  cabe en 1000. `WriteBytes` falla con el mensaje a medio escribir, y `NetConnection.Begin` ([:154-157](../src/HKMultiplayer/Net/NetConnection.cs#L154))
  ya no deja empezar otro (solo `CloseConnection` llama a `CancelMessage`): todo lo que va a ese jugador falla y, como
  siguen llegando acuses, nunca se corta por tiempo. Lo mismo con cualquier fallo entre `BeginTo` y `EndTo`. El
  disparador más probable es un traspaso con muchas variables (N2). Arreglo: que el invitado reserve 2 bytes, que
  `Begin` cancele un mensaje viejo en vez de fallar, y `try/catch` + `CancelMessage` en `Relay` y `BeginTo`.
- [ ] **N2 · media · sin comprobar.** Código del mod antes de `orig` en ganchos sin `try/catch`: si falla, el juego
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
- [ ] **N3 · media · sin comprobar.** El canal fiable puede quedarse parado para siempre.
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
- [ ] **N4 · media · sin comprobar.** Seguridad de las transferencias grandes: `ReceiveLargeChunk`
  ([NetConnection.cs:504-507](../src/HKMultiplayer/Net/NetConnection.cs#L504)) reserva `new byte[total]` (hasta 64 MB)
  con el primer trozo de cada id, sin límite de cuántas a la vez ni caducidad: un compañero (con el código) puede
  agotar la memoria del otro con unos pocos paquetes de 1 KB. Arreglo: 1 o 2 en curso por conexión, límite por tipo y
  caducidad.
- [ ] **N5 · media · sin comprobar.** Saludo (`NetServer.HandleHandshake`, [:86-107](../src/HKMultiplayer/Net/NetServer.cs#L86)):
  un `Hello` sin autenticar desde la dirección de un invitado ya conectado, con claves nuevas, cierra esa conexión (se
  puede echar a alguien suplantando su dirección). Muchos `Hello` con claves nuevas desde una dirección obligan a un
  X25519 + HKDF por paquete en el hilo principal, porque el límite de conexiones a medio abrir se mira después de
  cerrar la anterior: puede congelar al host. Arreglo: nunca soltar una conexión autenticada por un `Hello` y limitar
  los `Hello` por dirección.
- [ ] **N6 · baja · sin comprobar.** El código de partida (40 bits, 32^8, [RoomCode.cs](../src/HKMultiplayer/Net/Crypto/RoomCode.cs))
  se puede sacar a fuerza bruta sin conexión a partir de la prueba del saludo ([NetServer.cs:117-126](../src/HKMultiplayer/Net/NetServer.cs#L117))
  si alguien llega al puerto. Arreglo: un PAKE (CPace/SPAKE2), o al menos un KDF lento sobre el código y un código
  más largo.
- [ ] **N7 · baja · sin comprobar.** El nombre se lee con `ReadString(64)` ([NetServer.cs:161](../src/HKMultiplayer/Net/NetServer.cs#L161))
  pero `CleanName` deja 24 caracteres (72+ bytes en UTF-8 para chino, japonés...): el saludo falla y el invitado ve un
  "tiempo agotado" que no explica nada.
- [ ] **N8 · baja · sin comprobar.** `MultiplayerRunner.LateUpdate` ([:233-260](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L233))
  tiene un solo `try/catch` para unas 20 partes: si una falla, se saltan las de detrás (también `session.Update()` si
  falla `SendLocalState`). Y `DetachAll`/`CloseSession` ([:482-523](../src/HKMultiplayer/Game/MultiplayerRunner.cs#L482))
  no aíslan cada `Detach`: si uno falla, `session.Dispose()` no se llama y quedan el socket, el hilo y el puerto
  ocupados.
- [ ] **N9 · baja · sin comprobar.** El mundo que llega del host se lee con `TypeNameHandling.Auto`
  ([WorldSync.cs:184](../src/HKMultiplayer/Game/World/WorldSync.cs#L184), [WorldStore.cs:55](../src/HKMultiplayer/Game/World/WorldStore.cs#L55)).
  Riesgo bajo (`PlayerData` y `SceneData` no tienen miembros polimórficos), pero con datos de la red mejor `None` o
  una lista de tipos permitidos.

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
