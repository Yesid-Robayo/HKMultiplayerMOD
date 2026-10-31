# Arquitectura de HKMultiplayer

Mod cooperativo para dos jugadores (pensado para dos, válido para algunos más) de Hollow Knight 1.5.12620 (Unity 6),
sobre la Modding API. Todo el núcleo es propio. Objetivos de diseño, por orden:

1. **Sin tirones**: ni la red ni la sincronización crean memoria temporal por paquete ni por frame. El recolector de
   basura del juego solo debería actuar al cambiar de sala, como jugando solo.
2. **Sincronización fiable y fluida**: el estado continuo (posiciones, animaciones) llega a ritmo fijo y se interpola;
   los eventos importantes (golpes, muertes, palancas, objetos) llegan siempre, una vez y en orden.
3. **Seguro por defecto**: solo entra quien tiene el código de la partida y todo viaja cifrado y autenticado.
4. **Simple de mantener**: capas pequeñas con responsabilidades claras; el núcleo de red no depende de Unity y se
   prueba fuera del juego.

## Capas

```
┌──────────────────────────────────────────────────────────────┐
│ Funciones del coop: revivir, sombras, skins, panel, HUD...    │  Game/Features
├──────────────────────────────────────────────────────────────┤
│ Replicación: jugadores, mundo (guardado), enemigos, efectos   │  Game/Replication
├──────────────────────────────────────────────────────────────┤
│ Sesión: host/unirse, jugadores, salas, ajustes del host       │  Session
├──────────────────────────────────────────────────────────────┤
│ Transporte: UDP, cifrado, canales fiable / no fiable          │  Net (sin Unity)
└──────────────────────────────────────────────────────────────┘
```

### Transporte (`Net/`)

- **UDP** con un único socket. Un hilo recibe en búferes de un *pool* (sin crear arrays por paquete) y los deja en una
  cola; el hilo principal de Unity procesa la cola una vez por frame. Los envíos salen del hilo principal en el *tick*
  de red, con el búfer del paquete reutilizado.
- **Paquete**: cabecera (versión del protocolo, tipo) + carga cifrada con número de secuencia, último recibido y un
  campo de 32 bits de acuses (estilo "ack bitfield"). Dentro van mensajes: `[canal+tipo][longitud][datos]`.
- **Canales**:
  - *No fiable con secuencia*: estado continuo. Solo vale el más reciente; si llega uno viejo se descarta.
  - *Fiable ordenado*: eventos. Cada mensaje se reenvía hasta que llega el acuse de algún paquete que lo llevaba; el
    receptor los entrega en orden y sin duplicados. Un fallo de quien procesa un mensaje no sale del transporte (se
    registra, como mucho 20 veces y luego uno de cada 500) y el mensaje cuenta como entregado: su paquete ya se
    confirmó y nunca se reenvía. Antes la excepción cortaba la entrega, el receptor se quedaba esperando ese mensaje
    para siempre y todo lo fiable de esa conexión dejaba de llegar (en una partida real, tras un fallo de
    `EnemySync.OnSpawn`: el compañero se veía invisible al cambiar de sala, "Ir con" no tenía respuesta y las paredes
    rotas por él no se rompían en el otro PC). En el host, un fallo al procesarlo tampoco impide reenviarlo a los demás.
  - *Datos grandes*: sobre el canal fiable, troceados con ventana (partida al unirse, skins). Sustituye al servidor TCP
    de skins.
- **Serialización**: `PacketWriter` / `PacketReader` escriben y leen directamente en el búfer (little-endian, enteros
  variables, cadenas UTF-8 sin arrays intermedios). Los mensajes son *structs* con `Write`/`Read`.
- **Cifrado**:
  - Al conectar, intercambio de claves **X25519** (efímeras, secreto hacia adelante).
  - Claves de sesión con **HKDF-SHA256** mezclando el secreto compartido y el **código de partida**: sin el código no
    se puede derivar la clave, así que nadie sin él puede unirse ni hacerse pasar por el host. El código entra estirado
    con **PBKDF2-HMAC-SHA256** de 100.000 vueltas (`KeyExchange.Stretch`, una vez por código y en otro hilo): quien
    capture un saludo ya no puede probar códigos a millones por segundo. Desde la 0.26.0, protocolo 2.
  - Cada paquete con **ChaCha20-Poly1305** (cifrado autenticado); el *nonce* sale del número de secuencia y la
    dirección, y nunca se repite. Paquetes alterados o repetidos se descartan.
  - Primitivas de la biblioteca BouncyCastle (MIT), con los objetos de cifrado reutilizados.
- **Conexión**: saludo en dos pasos con versión del mod, nombre y prueba de que se conoce el código; latido, tiempo de
  espera y desconexión limpia. Límite de jugadores y de conexiones a medio abrir, y como mucho 10 saludos nuevos por
  segundo. Un saludo desde la dirección de un jugador que envió algo en los últimos 3 s se ignora (no tira su conexión).
- **Robustez**: un fallo al procesar un datagrama, el tick de una conexión o un mensaje no sale del transporte. Un
  mensaje a medio escribir que falló se descarta en el siguiente `Begin`. Las transferencias grandes a medio recibir son
  como mucho 2 y caducan a los 30 s.

### Sesión (`Session/`)

- El **host** ejecuta el servidor en su propio proceso. Su jugador habla con el servidor directamente (sin socket ni
  cifrado); los demás por la red.
- El servidor conoce a cada jugador, en qué sala está y quién **manda** en cada sala (el primero que entró; al irse,
  pasa al siguiente). Reenvía a cada jugador solo lo que pasa en su sala, salvo lo global (mundo, avisos).
- Ajustes de la partida: los decide el host y se envían a todos.

### Replicación (`Game/Replication/`)

- **Ritmo fijo** (60 Hz por defecto, adaptable si la conexión se congestiona). Cada tick se toma una "foto" del estado
  local y se envía solo lo que cambió.
- **Jugadores**: posición, orientación, animación (clip y fotograma), sala, vida. En los demás juegos se dibujan con
  un búfer de interpolación (pequeño retraso adaptativo y extrapolación corta si un paquete se retrasa). Un salto
  (más de 4 unidades entre dos estados) no se interpola: se dibuja el de antes hasta su momento y luego el nuevo, aunque
  no llegue ninguno más. Antes se esperaba a un tercero: un Mantis Lord que se va y se esconde deja de enviar (no
  cambia), y en el otro PC el último cuadro de su salida (la raya) se quedaba visible y quieto hasta que reaparecía.
- **Mundos coop** (`Game/World/`): partidas aparte de las 4 ranuras del juego, guardadas en el PC del host
  (`HKMultiplayer/Mundos/<id>/`, máximo 4). Mientras se juega uno, la carga y el guardado del juego se redirigen al
  mundo y el juego cree estar en la ranura -1 (que su guardado rechaza), así que nunca escribe en una partida normal.
  - Al unirse, el host envía el mundo comprimido con el personaje del jugador dentro (o uno nuevo); el jugador lo carga
    y no guarda nada en su PC: su personaje va al host en los bancos, al salir y cada 30 segundos
    (`players/<clave>.json`). Antes de cerrar la partida, salir de su mundo o cerrar el juego, el host pide a cada
    invitado su personaje y espera a recibirlo (unos segundos como mucho). El host guarda el mundo solo cada 2 minutos.
  - **Personaje** (`CharacterData`): estado (vida, alma, geo, banco, sombra, portal de sueño) e **inventario propio**
    (máscaras y vasijas, aguijón y artes, amuletos, llaves, reliquias, mapas, esencia, compras), más los objetos de
    sala que recoge cada uno (objetos brillantes, fragmentos, cofres, orbes de sueño). Del mundo y compartido: jefes,
    puertas, palancas, paredes, personajes y salas visitadas.
  - **Habilidades de cada jugador** (`CharacterData.AbilityFields`: dash, sombra, garra, doble salto, Corazón de
    Cristal, Lágrima de Isma, Marca del Rey, aguijón onírico y su mejora, portal de sueño, hechizos y su nivel): hasta
    la 0.23.0 eran del mundo, y todo lo que da una habilidad mira si este jugador ya la tiene (el objeto brillante de la
    Capa de Polilla se destruye, la Vidente no da el aguijón, el chamán no da el hechizo). Recogida por uno, al otro le
    llegaba puesta y se quedaba sin el objeto y sin su escena; cada caso pedía un parche aparte. Ahora cada uno consigue
    las suyas. Un personaje guardado sin esos campos (versión anterior) se queda con los del mundo (`WasShared`: lo que
    ya tenía, porque se compartía) y no con los de una partida nueva. Algunos personajes también miran una habilidad
    (Quirrel se va del santuario de Sendero Verde y de la Estación de la Reina con la Garra de Mantis): eso pasa ahora
    para cada uno según la suya.
  - **Introducción para un invitado nuevo** (`CoopSaves`): el invitado entra al mundo del host como quien continúa una
    partida, y el juego solo pone la introducción (el poema y la llegada a Hallownest, `Opening_Sequence`) en una
    partida nueva. Si su personaje no tiene tiempo jugado, `ContinueGame` se cambia por `RunStartNewGame`; la
    introducción hace lo del juego (se salta igual) pero al acabar no lleva al Paso del Rey: vuelve a `ContinueGame`,
    que entra donde reaparece el host, como antes.
  - **Progreso compartido en directo** (`SharedProgress`): los campos del mundo se comparan cada 0,2 s con una copia
    (lectores compilados, sin memoria por lectura) y los cambios van al host, que los aplica y reenvía. Los objetos de
    sala (paredes, palancas, piedras de geo) se envían al guardarse y nunca retroceden.
  - Lo que pasa con el Forjaguijones tras la última mejora (perdonarlo, matarlo, la cabaña de Sheo) es del personaje,
    como sus mejoras: si fuera del mundo, en cuanto uno lo perdonaba se iba y el otro no podía terminar las suyas.
  - **Personajes de misión** (`QuestNpcSync`): el avance de una misión es un dato del mundo y llega al otro PC, pero el
    personaje en la sala es de cada PC y el juego solo mira ese dato al cargar la sala. Cuando llega de otro PC un
    cambio de un dato del mundo (sí/no o número), en la sala se vuelve a hacer lo que hace el juego al cargarla con el
    valor nuevo:
    - Las FSM que miran ese dato al empezar (en los estados a los que llegan solas desde el inicial, con `FINISHED` o
      con lo que deciden sus acciones, hasta uno que espera al caballero) vuelven a su estado inicial. Solo las de un
      personaje (su objeto, un hijo o su padre tienen `npc_control` o `Conversation Control`) o las que al empezar solo
      enseñan, esconden o animan (sin crear objetos, avisar a otros, cambiar datos, sonar...): así no se repite la
      apertura de una arena ni un combate. La campana de las estaciones del ciervo (`Stag Bell`) se trata como un
      personaje: solo se enciende al cargar la sala si la estación está abierta, y al empezar avisa a su cartel (no
      contaba como "solo enseña"); abierta por otro jugador, aquí no había campana hasta volver a entrar.
      Mr. Mushroom se destruye en el otro PC en cuanto uno le habla (su `Control`
      compara `mrMushroomState` con su sitio), Bretta y Quirrel se van, el farol de la Pesadilla aparece.
    - En el registro queda cada personaje que su comprobación quita al cargar la sala, con el dato, su valor y si es del
      mundo o del jugador ("Quirrel Slug Shrine/deactivate: el personaje se quita en esta sala por hasWalljump=True (dato
      de este jugador)"): en la partida real a uno le salía Quirrel y al otro no, y el registro no decía por qué.
    - Los objetos que el juego enciende o apaga según un dato (`DeactivateIfPlayerdataTrue`, `...False`,
      `ActivateIfPlayerdataTrue`; Myla, las larvas del Padre de las Larvas) se encienden o apagan igual. No los enemigos.
    - Se mira al cargar la sala qué FSM y objetos miran cada dato (una vez, sin memoria al jugar). No se tocan los
      enemigos, los controladores compartidos ni lo que copia su cadena (llega de allí). Si el caballero local está sin
      control (hablando o en una cinemática) se espera a que lo recupere: si no, el personaje con el que habla se iría a
      mitad del diálogo.
    - La misma conversación no avanza la misión dos veces: si los dos hablan a la vez con su copia del personaje y el
      otro avanza el número mientras este habla (su caballero perdió el control antes de que llegara), la lógica del
      personaje (la FSM que lo cambia lee ese dato, o lo mira al empezar otra del mismo objeto) no lo vuelve a cambiar
      (`PlayerData.IncrementInt`, `DecrementInt`, `IntAdd`, `SetInt` enganchados) y lo lee como estaba al empezar la
      conversación (`GetInt`): cada uno lee el mismo texto. Un cambio de antes de la conversación no cuenta (hablar otra
      vez con un personaje que se queda sigue avanzando su diálogo). Antes, si uno hablaba con Mr. Mushroom y luego el
      otro con su copia, `mrMushroomState` subía dos y se saltaba un sitio.
    - En el PC de quien habla el personaje hace lo del juego (Bretta o Tiso siguen ahí hasta volver a entrar). En el
      otro, si el dato es un sí/no y quien habló está en la misma sala, también se queda hasta volver a entrar (antes
      Bretta desaparecía delante del otro a mitad de la conversación). Si quien lo cambió está en otra sala, o es un
      número (Mr. Mushroom: hablar con la copia que se quedara avanzaría la misión dos veces), se va enseguida, como si
      se cargara la sala.
- **Enemigos y objetos de sala**:
  - Cada enemigo lo mueve el PC del jugador más cercano, porque su lógica solo ataca al caballero de su PC. Lo reparte
    el dueño de la sala; cambia solo si el otro está claramente más cerca (4 unidades) y hace un rato del último cambio
    (5 s; 8 s en los jefes, cada cambio corta su ataque: con 3 s y los dos cerca, un caballero de musgo podía volver a
    empezar su estado cada pocos segundos sin llegar a atacar). Ese PC lo hace funcionar normal y envía su estado (transform,
    animación, profundidad, activo, vida, daño al tocarlo) con detección de cambios sin crear objetos.
  - Un jefe (150 de vida o más) solo cambia de PC entre dos ataques (0.27.0): cuando su lógica principal (la FSM de su
    objeto con más estados que tenga alguno de reposo) está en un estado de reposo, cuyo nombre lleva `Idle`, `Choice`,
    `Choose`, `Decide` o `Select` (en el volcado de las FSM, casi todos los jefes tienen alguno), o salió de uno hace
    menos de 0,15 s (los de elegir ataque duran cero fotogramas). El PC que lo mueve lo envía en el mensaje de estados
    (bit 4 de las banderas) y el dueño, si quería cambiarlo, lo cambia en cuanto llega, sin esperar al siguiente reparto.
    Si no reposa en 4 s, cambia igualmente; si quien lo mueve está caído o en pausa, enseguida. Antes cambiaba a mitad
    de un ataque: el nuevo PC lo retomaba desde el principio de ese estado hacia su caballero y el ataque se cortaba.
    Probado con Hornet en `Fungus1_04`, cambiando de lado cada 6 s (sin retraso y con 100 ms): los 15 cambios se
    retomaron en `Run Antic`, `Jump Antic` o `Evade Antic`, el principio de su siguiente movimiento.
  - Si quien lo mueve está fuera de su zona de alerta (la variable `Alert Range` de su lógica, o un hijo "Alert
    Range...", ampliada con el tamaño de la caja del caballero) y otro jugador está dentro, pasa a ese: su lógica solo
    ataca a un caballero dentro de la zona. El Rey Vengamosca se iba a esperar a su sitio (`Lurk`) siguiendo al más
    cercano, fuera de la zona, mientras el otro peleaba dentro, y se quedaba mucho rato sin atacar. Con los dos
    dentro, el otro tiene que estar 8 más cerca (no 4) para quitárselo: el Rey vuela a 13 de su caballero, quedaba más
    cerca del otro y pasaba de uno a otro sin parar.
  - El traspaso lleva también lo que llevaba esperando su estado (`Wait` o `WaitRandom`), y al tomarlo la espera sigue
    por ahí: cada cambio de PC empezaba de cero los 3-4 s del Rey Vengamosca antes de atacar.
  - El Moss Charger sale de la tierra a 14 del caballero de su PC y ya al salir daña: si ese punto cae encima de otro
    jugador, sale por el otro lado de su caballero (girado y embistiendo hacia el otro lado), si ese lado está en su
    zona y libre.
  - No cambia de PC mientras va rápido (más de 9 unidades/s entre dos repartos: embistiendo o lanzándose). El Moss
    Charger embiste a 15 y cruzaba de un jugador al otro: pasaba al otro PC a mitad de la embestida, su lógica seguía
    allí con ese caballero y daba la vuelta (se veía derrapar al revés). Tampoco con su muerte en marcha (`Dying`: su
    lógica atendió `ZERO HP` en este PC, o el PC que lo mueve avisó de que allí ya murió), mientras quien lo mueve siga
    en la sala. La muerte del Falso Caballero va por su cabeza y su cuerpo nunca queda muerto en su `HealthManager`: el
    cuerpo pasaba al otro PC a mitad de su muerte (`Cough`), seguía dañando y las rejas no se abrían.
  - Al tomarlo, su lógica sigue desde el estado en que iba (`SetState`), pero sin las acciones de ese estado que crean o
    lanzan objetos: ya las hizo el PC que lo movía y sus disparos siguen en el aire. Si se repetían, cada cambio de PC
    a mitad de un ataque era un ataque doble.
  - Los de un caído o de quien está en pausa pasan enseguida a otro, también los apagados: en una marioneta, apagado
    es solo lo último que envió quien la movía, y antes se saltaban; con un invitado caído junto a los enemigos que
    movía, el dueño creía que los movía el caído y el caído que el dueño, y quedaban quietos en los dos. Además el
    dueño repite su reparto entero a la sala cada 2 s (y 1,5 s después de que entre alguien): los cambios solo se
    avisaban al hacerse, y quien recargaba la sala o perdía uno por un cambio de dueño se quedaba con otro reparto.
  - En los demás juegos el enemigo es una "marioneta": su lógica (FSM, física) se pausa (no se reinicia) y se dibuja
    con el estado recibido e interpolación; sus colisiones de daño siguen funcionando para el jugador local. Quien
    entra en la sala recibe del dueño la vida de cada enemigo: se aplica aunque el enemigo aún no sea marioneta (lo
    pasa a ser un poco después de cargar la sala); solo se ignora la vida de los que mueve este PC.
  - Traspaso: el que lo suelta envía cómo iba (estado, números, textos y objetos de cada FSM, velocidad, gravedad y tipo de
    cuerpo) y el nuevo lo sigue desde ahí. Los objetos van como "su caballero" (en quien lo recibe, el suyo), "una
    pieza del enemigo" o "un objeto de la sala" por su ruta, y solo rellenan las variables vacías: la marioneta no pasó
    por los estados que las guardan (el escupidor guarda al caballero al alertarse). Los textos van desde 0.25.0: los
    Señores Mantis de la segunda fase eligen pared y guardan hacia dónde lanzar (`Shot Event`, "WIDE L"...) antes de
    lanzar; retomados entre medias, lanzaban con el texto de su lanzamiento anterior (o vacío) y la guadaña salía de la
    arena y volvía. También al salir de la sala o morir (aún dentro, al empezar el cambio de sala): un jefe no
    repite su presentación.
  - Los golpes del jugador local a una marioneta van al PC que la mueve, que los aplica al enemigo real. Cualquier
    aviso de fuera que llegue a una marioneta (el de daño del ataque, la zona de la arena en la que entró el caballero,
    la escena de batalla) también va al PC que la mueve: con esos avisos despiertan o reaccionan muchos jefes. Los
    avisos a todos de un objeto de la sala no llegan a la lógica parada de una marioneta: si en su estado de ahora
    esperaba uno, también se reenvía (la zona de abajo del Tirano de Almas, `PHASE 2`). Y un aguijonazo del jugador
    local a una marioneta cuya lógica esperaba un aguijonazo en su caja (`Trigger2dEvent` con "Nail Attack", el
    `FINAL HIT` de la Radiancia) manda ese aviso al PC que la mueve. El aviso lleva a qué PC va (el que la mueve
    según quien lo manda) y solo lo atiende ese. Si al llegar ya no la mueve (el cambio de PC se cruzó con el aviso),
    lo pasa al que la mueve ahora; si es él mismo quien está a punto de tomarla, lo guarda y lo atiende al tomarla
    (como mucho 3 s). Antes se perdía: el `BATTLE START` del Campeón Fallido no llegaba a nadie y el jefe se quedaba
    dormido. Cada golpe que
    recibe un enemigo real se avisa a la sala: los demás ven el impacto, el destello y oyen el golpe (quien golpeó ya
    los vio en su PC). La marioneta copia también su giro, si se dibuja, si choca y si es invencible (y desde dónde).
  - Lo que se ve y se oye de la lógica del enemigo también pasa en las marionetas: cuando su FSM entra en un estado,
    la marioneta hace solo sus efectos (sonidos y música de jefe; objetos de la arena que se activan, partículas,
    ráfagas como los disparos de Mawlek, efectos y avisos a otros objetos de la arena, como las animaciones de la
    presentación de la Vasija Rota), con los números de su FSM en ese momento (velocidad y ángulo de una ráfaga). No
    los avisos al caballero ni a todos, ni lo que suelta geo. Los avisos a todos sí cuando el jefe ya murió en el PC que
    lo mueve (`HealthManager` muerto o su lógica atendió `ZERO HP`): son lo que sigue tras vencerlo (`GRIMM DEFEATED`
    saca al Grimm que se inclina y habla). Solo muerto de verdad: el Hollow Knight pasa por vida negativa entre fases.
    Lo que su lógica suelta de sí al morir (`SetParent` sin padre: el cadáver del Hollow Knight) se suelta también en la
    marioneta y sus FSM pasan a funcionar solas en ese PC (allí es donde se absorbe el vacío para el final).
    Al pasar a marioneta se apagan sus FSM y PlayMaker les manda `DISABLE` (`PlayMakerFSM.OnDisable`, sin remitente):
    ese aviso no se reenvía. Antes llegaba al PC que acababa de tomar el enemigo y su lógica viva lo atendía; el Falso
    Caballero (y cualquier enemigo con `DISABLE` en sus transiciones) se quedaba parado tras cada cambio de quién lo movía.
    Un enemigo que cuelga de otro (la cabeza del Falso Caballero, `Head`, con vida propia) lo mueve siempre el mismo PC
    que a su padre: se repartía por distancia aparcado fuera de la arena y, al aturdirse el jefe, su cabeza no se podía
    golpear en el PC que movía al jefe. Al tomar un enemigo, si una variable de objeto apuntaba a algo que su lógica creó
    en el otro PC (el vapor de la muerte del Falso Caballero, `Death Smoke`, con `CreateObject`), se crea aquí con esa
    misma acción (`RecreateStored`). Y la marioneta repite también los `EaseFloat` con aviso al acabar, sin mandar el
    aviso (`WithoutFinishEvent`): antes su vapor no bajaba a 0 y se quedaba con las partículas quietas.
    Las FSM de daño (`damages_enemy`, `damages_hero`) no se siguen: solo pasan el golpe al objeto que tocan y su
    `Send Event` se enviaba en cada contacto (los barriles del Falso Caballero, unos 257 por minuto) sin hacer nada.
  - Un efecto continuo de una marioneta se acaba si el enemigo muere por la vía normal o se apaga: ya no llegan más
    estados que lo acaben y se quedaba pegado (salvo una muerte que va por su lógica, `Dying`).
  - Las FSM de los hijos que solo animan su propio objeto (sus acciones son esperas, animación, partículas, sonido,
    encenderse o apagarse a sí mismo, moverse o girarse: el `Spit Effect` del Gulka y del Elder Baldur) siguen
    funcionando en la marioneta. Paradas con las demás, el efecto que encendía la marioneta no se apagaba nunca.
  - Los efectos que su lógica repite cada frame mientras dura el estado también siguen en la marioneta
    (`EnemySync.UpdateRunningEffects`): partículas con `everyFrame`, salpicaduras continuas (`SpawnBloodTime`,
    `SpawnRandomObjectsOverTime`) y, si el estado tiene alguno, las cuentas con sus números que usan (`FloatAdd`,
    `FloatClamp`, `EaseFloat` sin aviso al acabar...). Se apuntan por estado al encontrar el enemigo, se repiten
    (`OnUpdate`) hasta que llega otro estado de esa FSM (el PC que lo mueve avisa también al salir de un estado así,
    aunque el siguiente no tenga efectos), y se acaban si pasa a moverlo este PC o deja de seguirse. Antes solo se
    hacían al entrar: el vapor del Falso Caballero y del Campeón Fallido al morir (su ritmo sube 5 cada frame) salía
    más flojo en el PC que no lo movía y su salpicadura continua no salía.
  - Un objeto con dos `HealthManager` (la segunda fase del Tirano de Almas del sueño) es un solo enemigo, el primero.
  - Los objetos de la arena que su lógica enciende y apaga se sincronizan además como estado (al cambiar, y todos a
    quien entra en la sala): no dependen de haber visto el efecto en directo.
  - Ascensores y rejas: los mueve quien los usa (su controlador). Las copias de los demás no avanzan solas: solo pasan
    por los estados que llegan, una vez cada uno, aplicados dentro de la ejecución de su FSM. Si no, los estados con
    efectos fuera del objeto (el ascensor voltea la dirección de su palanca al llegar) se harían dos veces. Solo
    "usarlo" da el control: un aviso que le cambia el estado (no los avisos a todos que no le hacen nada, como el título
    de la zona), o, en los que arrancan al pisarlos, su propio choque con el caballero de este PC encima. Son todos los
    que tienen la FSM `Lift Control` (antes solo los `Ruins Lift` de la Ciudad: el de Bocasucia a la Cumbre de Cristal y
    el de la estación del ciervo de Bocasucia se movían solo en el PC de quien los usaba). Si el controlador se va de la sala (p. ej. muere), quien se queda pasa a serlo; quien entra o vuelve
    espera a oír al controlador antes de tomarlo, y si su objeto está en otro estado se pone al día (una reja cerrada
    allí se cierra aquí con su animación y su colisión).
  - Rejas de batalla y de jefes: si una se cierra con un jugador dentro, el otro puede entrar a ayudar pero no salir
    hasta que se abra. Las que ya están cerradas al cargar la sala no se cruzan. Son también rejas las puertas de la
    arena de los sueños de jefe (`Dream Gate`/`Control`, y `Dream Gate Phase 2`/`Control2` del Tirano de Almas): las
    cierra y abre el jefe (`DREAM GATE CLOSE`/`OPEN`, `PHASE 2`) en el PC que lo mueve y antes solo se cerraban allí.
    Lo que manda un enemigo que mueve este PC a un objeto con controlador cuenta aunque llegue mientras se aplica la
    cadena copiada de otro objeto (la arena copiada despierta aquí al jefe y este cierra las puertas).
  - Controladores de batalla que no son enemigos (Señores Mantis: los tronos, `Mantis Battle`, `Battle Main` y
    `Battle Sub`, que coordina a la pareja; Coliseo de los Tontos: su gestor, sus oleadas y sus jaulas, algunas con
    esperas al azar): igual que ascensores y rejas, los lleva un PC y los demás siguen sus estados. Quien acepta el
    desafío pasa a controlarlos, y el combate (jaula, suelo con pinchos, qué señores salen, oleadas, final) avanza
    igual en todos. Lo que mueven con avisos a todos (jaula, suelos, puertas) va igual que ellos, y quien entra a mitad
    del combate se pone al día con su estado. En las copias no se hacen las llamadas al caballero (devolverle el control,
    girarlo), salvo la que fija dónde reaparece tras los pinchos (el Coliseo la pone en una plataforma antes de sacarlos;
    sin ella, el caballero del otro PC reaparecía en la entrada de la sala), y el cartel para desafiar se apaga en los demás PCs al llegar un estado de un controlador de batalla al que solo se
    llega con el aviso del desafío (`MLORD CHALLENGE`: los tronos levantándose). Antes bastaba cualquier cadena de otro
    PC (una palanca de la Aldea Mantis) o cualquier estado de un controlador de batalla (la reja de Deepnest se cierra
    cuando alguien entra, los tronos al pasar el control): tras morir contra los Señores Mantis, al volver nadie podía
    desafiarlos (si no, se podría desafiar otra vez a mitad del
    combate y ese caballero se quedaría sin control). Las órdenes de esos controladores a un enemigo no se reenvían:
    ya las da su copia en el PC que lo mueve. Los avisos de una copia a otro objeto con controlador (p. ej. el `SPAWN`
    con 1 s de retraso que la batalla del Coliseo manda a sus jaulas, que llega fuera de la aplicación del estado) no
    toman el control: ya los manda el controlador. Si no, los dos PCs se quitaban las jaulas, alguna no soltaba su
    enemigo y la oleada esperaba para siempre (`Battle Enemies` no llegaba a 0). El Coliseo tiene más de cien objetos
    con controlador: si un PC los toma todos a la vez (el host se va y queda el invitado), sus posiciones van en varios
    mensajes. Antes no cabían en uno, el mensaje quedaba a medio escribir y el invitado no enviaba nada más hasta que el
    host lo echaba por tiempo. Un mensaje que falla a medias se descarta (también antes del aviso de cambio de sala del
    invitado y del de desconexión), y un fallo de la sincronización de la sala no impide enviar los paquetes del frame.
  - El tarro del Grub Mimic (oleada del Coliseo de Plata; lo crea su jaula y no es guardable) se copia como una
    palanca: quien lo rompe con el aguijón lo rompe en todos y el bicho sale del tarro roto en los dos. Es un golpe, no
    una zona: no espera a que todos estén cerca. Sus FSM, como las de lo que crean las jaulas en la raíz de la sala, se
    identifican por su sitio (se llaman igual y tienen el mismo puesto entre hermanos).
  - Objetos de misión que se golpean con el aguijón (`Trigger2dEvent` con "Nail Attack" en un estado que sale con ese
    aviso) y cambian un dato del mundo también se copian como una palanca: Zote atrapado en Nido Profundo se descuelga en
    los dos (y cada uno habla con su Zote) y el farol de la Pesadilla se aviva con los golpes de los dos (cada golpe
    suma y se apaga solo; antes cada PC contaba los de su jugador y no se encendía) y se enciende a la vez en los dos.
    Un golpe de este jugador desde el reposo cuenta aunque acabe de llegar la cadena de otro PC (antes se descartaba
    durante 3 s).
  - La campana de las estaciones del ciervo (`Bell Control` de `Station Bell`) también se copia como una palanca: su
    aviso `STAG BELL RUNG` hace llegar el ciervo en todos los PCs de la sala. Cada uno se sube y viaja por su cuenta.
  - Encuentros con diálogo antes de un combate (Hornet en los Bordes del Reino; se detectan solos: un objeto que no
    es enemigo, con un diálogo, que después despierta o enciende a un enemigo o cierra la arena): van como los
    controladores de batalla, así que los dos ven el diálogo, con su caballero quieto mientras, y la Hornet que habla
    desaparece en los dos al empezar. Cada diálogo espera a que todos los de la sala lo terminen de leer (si alguno no
    responde en 30 s, sigue). Los personajes que solo hablan no cuentan y siguen siendo de cada uno. Empezado en otro
    PC, lo que le mandan otros objetos de este (el Grimmchild que llega a Grimm con `ABSORB END`) no lo hace avanzar ni
    toma el control: si no, los dos se quitaban el control y Grimm se quedaba en "Call Child". La copia sí pone los
    datos propios de cada jugador (`SetPlayerData*` de un campo de `CharacterData`: `metGrimm`, el amuleto, la
    invulnerabilidad de la presentación); sumar o restar no.
  - Grimm: su primer encuentro, el combate y el Grimm vencido (con quien habla cada uno) van en los dos. Las cadenas
    del Hollow Knight copian sus golpes como una palanca (cada una aguanta unos golpes y, rotas todas, empieza el
    combate). La presentación de la Radiancia (`Boss Control`/`Control`) es un controlador compartido.
  - Combates de jefe: si la zona que los empieza (la que cierra la arena o empieza el diálogo) avisa sin estar dentro
    todos los jugadores de la sala, se espera ("Esperando a ... para empezar el combate") y empieza cuando entran. Solo
    se retiene el aviso de la zona (un `Trigger2dEvent` del estado de ahora), no los demás del encuentro ya empezado.
    Una zona fina (menos de 2 de grosor: una línea a la entrada de la arena, como la del Campeón Fallido) se cruza:
    cuenta como dentro estar en ella o ya al otro lado, en el de la arena (hacia donde iba el caballero al tocarla). Si
    no, el primero en cruzarla salía de ella enseguida, la espera se olvidaba y el combate no empezaba nunca. Si todos
    están ya al mismo lado de la línea, cuenta como dentro: quien entraba quieto o mirando atrás dejaba el lado al revés.
  - Jefes que se despiertan al ver al caballero (Hornet 1, Guardián de Cristal...): se apunta en qué estado duerme su
    lógica principal y su primer cambio desde ahí es despertar. Si algún jugador de la sala está a más de 25 de él,
    ese cambio se retiene (el mismo aviso) y el jefe es invulnerable mientras: golpearlo no lo despierta ni le quita
    vida. Despierta en cuanto están todos cerca, sin límite de tiempo (también espera a quien está en pausa); no se
    espera a quien está caído, en otra sala o sin avatar en este PC. Si el jefe cambia de PC mientras espera, el
    traspaso lleva el despertar pendiente y el nuevo sigue esperando. No esperan los de una arena que ya espera a que entren todos (su
    `HealthManager` tiene `battleScene` y esa arena es una zona con FSM o la lleva un controlador compartido, como el
    Coliseo; Oblobble entra volando desde muy arriba y, si esperaba, se quedaba invulnerable para siempre) ni los que
    crea una jaula. La arena de Mawlek no tiene zona propia (solo las de la cámara): Mawlek sí espera. Tampoco se retiene
    si el caballero local no está cerca (no despierta por verlo: la Radiancia espera muy arriba de la arena).
  - Las copias no tocan los datos del jugador (contadores como las larvas llegan una vez, del PC de origen). Los tarros
    de larva se rompen en todos y la larva sale también en los demás.
  - Cofres: abrirlos es del mundo (el otro lo ve abrirse en directo, o ya abierto si entra después), pero lo que
    sueltan es de cada uno: el objeto queda en el suelo para cada jugador hasta que lo recoge él, y el geo le cae a
    quien esté en la sala.
  - Charged Lumafly (`zap drift`, Cañón de Niebla): flotan a la deriva sin vida. Los mueve el dueño de la sala, que
    envía dónde está cada uno 10 veces por segundo (mensaje 105, no fiable, a la sala); en los demás su deriva se para y
    siguen esa posición suavizada. Su descarga (`zap control`) sigue siendo de cada PC: daña a su caballero.
  - Garpedes (`BigCentipede`, los gusanos gigantes de Nido Profundo): avanzan en línea recta a velocidad fija, se meten
    bajo tierra en su salida y, tras una espera fija, salen otra vez por su entrada (2,6 antes). Su vuelta empieza al
    cargar la sala. Por dónde van a lo largo del recorrido dice en qué punto de la vuelta están (bajo tierra siguen
    avanzando): el dueño lo envía cada segundo (mensaje 107, no fiable, a la sala) y los demás, si se desvían más de
    0,1 s, se mueven ahí y se meten o salen como el juego (`Burrow`/`UnBurrow`). Sus secciones son hijas y se esconden
    solas según por dónde van.
  - Goam (`Worm Control`, Cruces Olvidados): salen y se esconden con esperas fijas desde que carga la sala y no tienen
    vida, así que no son enemigos compartidos. `HazardSync` los alinea (mensaje 104, no fiable, a la sala): el dueño
    envía cada segundo en qué estado de espera va cada uno y cuánto lleva, y los demás pasan a ese estado y ajustan la
    espera (`Wait`) si se desvían más de 0,1 s. Cerca del principio o del final de una espera no se corrige (ahí cada
    PC cambia de estado en un frame distinto).
  - Peligros animados (sierras del Palacio Blanco y del Camino del Dolor, paredes de pinchos): se mueven con una
    animación en bucle que empieza al cargar la sala. El dueño de la sala envía cada segundo en qué punto va cada una
    (`HazardSync`) y los demás se ajustan si se desvían más de un 3 % de vuelta. En las zonas revisadas no hay
    plataformas sólidas que se muevan solas (los ascensores ya van con controlador).
  - Objetos guardables (paredes, palancas, cofres...): el directo (golpe, animación, sonido) lo lleva solo la copia
    de su cadena (`FsmStateSync`). El aviso de "activado" de `SceneObjectSync` no mueve la FSM mientras esa cadena va
    en marcha: se comprueba al acabar y solo se activa si no llegó (como mucho 10 s después). Lo activado por una
    cadena llegada de otro PC no se vuelve a avisar. La línea base de cada objeto se toma después de
    `PersistentBoolItem.Start` (el juego pone ahí lo guardado; en los inactivos llega tarde), y lo que llega de otro PC
    antes de ese `Start` se pone justo después. Qué guardables ya pasaron su `Start` se apunta con el gancho de
    `PersistentBoolItem.Start` (o si se vieron encendidos en un frame anterior): su campo privado `started` no sirve,
    el juego nunca lo pone a verdadero, y por eso el que recibía la activación no ponía `Activated`. El reposo de una cadena se toma cuando el objeto lleva 1 s quieto: si
    no, el fin de su inicio al cargar la sala (`Pause` → `Idle`) parecía un golpe y se copiaba a los demás.
  - Las máscaras que tapan o destapan parte de la sala según dónde está el caballero local (`remasker`,
    `remasker_inverse`) son guardables y tienen `Activated`, pero son de cada PC: no se copian (`FsmStateSync.IsViewMask`).
  - Rompibles con FSM propia (la plantilla genérica del juego: recibe `TAKE DAMAGE`, tiene `Break` y la variable
    `Activated`, sin componente rompible ni guardable, como el letrero de la bajada a Cruces Olvidados): se copia su
    cadena como la de una palanca. Los rompibles con componente (`Breakable`, `BreakableObject`, `BreakablePole`,
    `BreakablePoleSimple`, `BreakableInfectedVine`, `VinePlatformCut`, `GlobControl`, `GrassCut`, `Grass`,
    `GrassSpriteBehaviour`, `HealthCocoon`) van por `BreakableSync`. `Grass` (recibe el golpe como un enemigo) y
    `GrassSpriteBehaviour` (las lianas y matas del Sendero Verde, que solo se cortan a la vista: se pone a la vista
    antes de cortarlo) no se seguían y lo cortado por uno seguía entero para el otro. El capullo de savia vital
    (`HealthCocoon`, de código y guardable, sin FSM: `SceneObjectSync` solo sigue los que tienen una) se rompe igual en
    los demás y suelta allí sus mariposas: cada jugador recoge su savia, como el geo. El mensaje dice si es el estado
    para quien entra: entonces el capullo se pone roto sin soltar nada (si no, se ganaría savia al entrar). Un
    `GrassCut` lejos de la cámara de este PC no tiene el material con el que colorea sus partículas (su `GrassBehaviour`
    lo borra al apagarse) y el corte que llegaba de otro PC fallaba antes de quitar el pasto: se corta sin colorearlas. Salvo los del pool del juego (guardados en él o sacados de él, como las gotas
    `Orange Glob` de los Bordes del Reino, que vuelven solas al pool): su ruta no es la misma en cada PC ni de una vez a
    otra, así que romper una aquí rompía otra distinta (o ninguna) en el otro PC. Son decoración y cada PC tiene las
    suyas.
  - Quien entra en una sala recibe del dueño lo ya activado (puertas, palancas, paredes), los golpes de cada piedra de
    geo y los rompibles rotos (también los de FSM propia: `SceneObjectSync` los sigue con un id por su ruta en la sala,
    `fsm:...`, y quien entra los pone en su estado de "ya roto", sin animación). Lo que se pone así no cuenta como un
    golpe de ese PC y no se copia a los demás. Al guardar, lo activado en el mundo no vuelve atrás.
  - Piedras de geo: cada golpe en vivo de un compañero se repite en los demás con su geo (estado `Check Direction` y,
    el último, `Destroy`), así cada jugador de la sala recoge el suyo, como el resto del geo. Al entrar en una sala con
    piedras ya golpeadas se ponen sin soltar geo.
  - Lo que crean los enemigos en combate (proyectiles, crías, invocaciones) se crea en los demás con la misma acción de
    su marioneta (mismo prefab). Si hace daño o tiene vida, es una copia que sigue al original hasta que desaparece; los
    efectos solo visuales del pool corren solos. Lo que crean al prepararse (cadáveres, piezas propias) no se replica.
    Se envía al acabar el estado que lo creó (o el frame), tal como lo dejó su lógica: posición, giro entero (el polvo
    y los chorros del juego son partículas giradas en X para salir hacia arriba), tamaño (el pool reutiliza objetos con
    el tamaño de su último uso), velocidad y los números de sus FSM. La velocidad la pone a veces otra acción al salir:
    el cristal del Crystal Flyer (`FireAtTarget`) daña con una FSM `damages_hero` y no con `DamageHero`, cuenta como
    visual y corre solo, y sin ella se quedaba en el aire, dañando, hasta salir de la sala (issue #3). Así una onda solo visual del Falso Caballero, que recibe su
    velocidad y dirección con `SetFsmFloat`/`SetFsmBool` justo después de salir, avanza igual en cada PC, echa sus
    chorros (que dañan al caballero de ese PC) y se acaba en la pared. Lo creado en combate no tiene tiempo de
    preparación: sus efectos (los barriles al romperse) cuentan desde que sale.
  - La copia de un proyectil se quita si su creador tiene el juego en pausa o si en 3 s no llega nada de él
    (`CopyAbandoned`): los enemigos de quien pausa pasan a otro PC, pero lo que disparó sigue con él y su copia se
    quedaba quieta, dañando.
  - El aviso lleva también el nombre del prefab: si la acción lo toma de una variable que ponen estados anteriores (el
    Elder Baldur elige en `Goop` o `Roller` qué escupe y `Fire` saca `{Projectile}` del pool), en la marioneta esa
    variable seguía vacía y no se creaba nada: el otro no veía sus disparos ni sus crías. Se busca por su nombre entre
    las variables y los `SetGameObject` de su lógica.
  - Lo que lanzan las acciones `FlingObjectsFromGlobalPool` y `…Vel` y hace daño (el vómito de los Ogros Hongo) también
    va como copia del original: se sigue lo que sacan del pool mientras se ejecutan. Antes la variante `Vel` no salía en
    el otro PC, y la otra se lanzaba aparte en la marioneta, con otras trayectorias al azar y dañando por su cuenta. Lo
    solo visual lo lanza cada PC.
  - Devolver de un aguijonazo lo que crean (el escupitajo del Gulka, `Spike Ball`): la copia no tiene vida, así que un
    `NailCatcher` en su caja avisa del aguijonazo; como nace parada en su estado inicial, se busca la espera del golpe
    (`Trigger2dEvent` o `Trigger2dEventLayer` con "Nail Attack") en todos sus estados. El PC que la mueve le pone como
    objeto que la golpeó un aguijón de referencia (apagado, con una FSM `damages_enemy`) con la dirección del golpe,
    deducida de dónde estaba ese caballero: su lógica la devuelve hacia ahí.
  - Los tarros del Coleccionista sueltan su bicho con su propio código. Solo lo suelta el tarro original: el PC que
    lo tiene lo sigue como algo creado en combate (se reconoce al encenderse junto a su tarro) y los demás crean una
    copia que lo sigue; las copias del tarro van vacías y se rompen sin soltar nada. Una copia que acaba sola no borra
    el original, salvo un disparo: si su copia choca (con el caballero de ese PC o una pared), el original también.
  - Lo creado en combate se numera con el jugador que lo creó: cada PC cuenta por su cuenta y dos podían dar el mismo
    número (se pisaban y quedaba una copia huérfana y quieta).
  - Enemigos de jaula (las crías `Hiveling Spawner` de la Colmena, plantilla de los Hatcher: su FSM tiene la variable
    `Cage` y sale de la jaula con `SPAWN`): esperan fuera de la vista en su jaula y al morir vuelven a ella con la vida
    repuesta; el `Zombie Hive` saca uno cualquiera (`GetRandomChild`). No son enemigos de la sala: los saca solo el PC
    que mueve al que los suelta (`SendEventByName` con `SPAWN` desde un enemigo real), que lo sigue como algo creado en
    combate (`EnemyRelease`, con la ruta del enemigo dentro de su jaula al cargar la sala). Los demás sacan ese mismo de
    su jaula (sale, se suelta de ella y se para como marioneta). La marioneta del que los suelta no repite ese `SPAWN`.
    Al morir, el original vuelve a su jaula y desaparece en todos; la copia muere a la vez y vuelve a la suya. Antes
    cada PC tenía vivos unos distintos.
  - Cadáveres: tras morir, el PC donde murió de verdad sigue enviando la posición de su cadáver unos segundos (sale
    despedido con física, y algunos explotan, como el Gruzzer volátil): cae y explota en el mismo sitio en todos.
  - Nombre del jefe al despertar: la marioneta hace también lo que le dice al título de zona qué nombre poner.
  - Enemigos creados por objetos de la sala que no son enemigos (las jaulas del Coliseo crean el suyo al prepararse):
    cada PC los crea igual. Si se crean después de repasar la sala, se siguen desde ese momento con un identificador
    sacado de quién los creó. Los clonados en la raíz de la sala (mismo nombre y mismo puesto entre hermanos) se
    identifican por la FSM que los apunta (la variable "Enemy" de su jaula; si los apuntan varias, la menor), y si no
    los apunta ninguna, por su sitio al cargarla. Por su sitio no bastaba: en Oro hay jaulas de oleadas distintas en
    el mismo punto, sus enemigos se repartían los identificadores en distinto orden en cada PC y el enemigo de una
    oleada era otro en el otro PC (no salía, o salía invisible, o quedaba su cadáver solo en un PC). No quedan muertos
    para siempre (el Coliseo se repite).
  - Zonas del enemigo que apuntan si el caballero está dentro (una FSM hija que al entrar y salir pone una variable
    de otra, como el "Hero Saver" de los Mage Blob, que no salen con el caballero donde aparecerían): al pasar a
    mover el enemigo se vuelven a poner según el caballero de ese PC. Lo que llega del anterior es su caballero; si
    no, el Mage Blob heredaba "dentro", aquí nunca llegaba la salida y la oleada esperaba para siempre.
  - Al pasar a mover un enemigo, queda como lo tenía el PC que lo movía: encendido o apagado, si se dibuja, si choca,
    si es invencible y cuánto daña (va en lo que envía al soltarlo; si no llega, lo último que se supo de él). La
    marioneta copia todo eso con el retraso de dibujo y la lógica del enemigo solo lo cambia en ciertos estados: la
    jaula del Coliseo enciende su enemigo en cada PC al abrirse y justo después la marioneta lo volvía a apagar con un
    estado de antes de salir. Si el caballero de ese PC estaba junto a la jaula y lo retomaba en ese momento, quedaba
    apagado y vivo en los dos PCs (el centinela volador de la oleada 44 de Oro) y la oleada no acababa. Igual con la
    caja del Electric Mage, que solo se enciende al despertar: retomado justo entonces, nadie podía golpearlo. Y si allí
    su lógica ya lo había soltado de su padre (los Mage del Coliseo cuelgan de su oleada hasta despertar y luego se
    teletransportan con posiciones locales), aquí también se suelta: si no, se teletransportaba fuera de la arena.
  - El host recuerda los enemigos muertos de cada sala (aunque se quede vacía) hasta que alguien descansa en un banco
    o muere: es cuando el juego hace que vuelvan (`GameManager.ResetSemiPersistentItems`, enganchado; lo llaman el banco
    y `PlayerDead`). Antes se miraba `SetBenchRespawn`, que el banco no llama (pone el punto de reaparición dato a dato):
    ni sentarse ni morir devolvía los enemigos. Solo los que el juego deja muertos hasta el banco (llevan
    `PersistentBoolItem`): los jefes de arena no lo llevan y vuelven cada vez que se carga la sala. Antes el host
    recordaba también a esos: tras matar al primer Señor Mantis y volver, el desafío cerraba la jaula y no salía. Con
    alguien en la sala, quien entra sabe qué murió por la foto del dueño de la sala, no por esta memoria.
  - Si descansa uno solo, los enemigos no vuelven para nadie: los datos de la sala del otro (semipersistentes, de cada
    PC) los siguen marcando muertos y al cargarla vuelven a morir. Los dos PCs coinciden; para que vuelvan, descansan
    (o mueren) los dos. Decidido así a propósito: si el descanso de uno los devolviera también en el otro, le
    aparecerían enemigos delante sin haber hecho nada, en mitad de su camino o de un combate en otra sala.
  - Un enemigo destruido durante un cambio de sala (`GameManager.IsInSceneTransition`) no cuenta como muerto: la sala
    se está descargando. Al recargar la misma sala ("Recargar sala", "Ir con" a la sala en la que ya se está) su nombre
    no cambia hasta que la nueva está lista, y antes todos sus enemigos se daban por muertos hasta el banco: el Falso
    Caballero no salía al volver a su sala. Igual los rompibles que se cortan (destruidos no son cortados).
  - Los que andan con una corrutina (`Climber`, los Tiktik; `Crawler`; la acción `WalkLeftRight` de los Reptantes)
    siguen andando con el componente o la FSM apagados: en la marioneta giraban, se volteaban y se movían solos, y el
    `Climber` la devolvía a donde estaba si se movía más de 0,1 en un frame (iban a saltos o quietos). Al hacerse
    marioneta se paran sus corrutinas; al retomarlo, `Climber` y `Crawler` repiten su `Start` (después de poner la
    velocidad del traspaso: el `Climber` solo la pone al echar a andar y al girar, y con la velocidad a 0 se quedaba
    quieto tras cada cambio de PC) y `WalkLeftRight` vuelve a entrar en su estado.
  - Un `Climber` sin suelo a 1 unidad gira 90° y lo vuelve a mirar: en el aire no acaba nunca y gira sobre sí mismo con
    su rayo encendido, sin volver a su pared (los Crystallised Lazer Bug de `Mines_20`, issue #4). Se soltaba al
    retomarlo cerca de una esquina: el giro de una marioneta llega a saltos y su posición interpolada, `Turn` se corta
    con el ángulo a medias y `previousTurnPos`, que evita girar otra vez en el mismo sitio, es de cada PC.
    `PlaceClimber` redondea el giro, lo apoya en la pared más cercana si no tiene suelo y apunta ahí su último giro.
    Por si se pierde igualmente, `UpdateClimbers` lo devuelve a la pared tras 1 s sin suelo. Probado en `Mines_20` con 63
    cambios de PC forzados en 6 minutos: antes 4 de sus 7 acabaron en el aire (a 2,1 de su repisa) y otro andando
    inclinado; después ninguno (a 0,27 como mucho, con 409 retomas) y sin que hiciera falta `UpdateClimbers`.
  - En las pruebas del Coliseo (`Room_Colosseum_*`) ningún enemigo queda muerto hasta el banco, tampoco los de la
    sala (los Mage de las oleadas): cada prueba empieza de cero, y al repetirla sin descansar la oleada 46 de Oro
    esperaba a un Mage que el host daba por muerto.
  - Piezas que se mueven aparte (hijos con animación o que dañan, como la aguja de Hornet): posición, profundidad,
    giro, escala, si están activas y si se dibujan, si su caja de daño está encendida, cuánto dañan y animación vienen
    del PC que mueve el enemigo. También las de fuera del enemigo que apuntan las variables de sus FSM (el rayo del
    Guardián de Cristal, que suelta al prepararse): se identifican por esa variable, no por su sitio en la sala.
  - Trampas de la sala que el enemigo dispara con un aviso a todos (las torretas del techo del Guardián con
    `LASER SHOOT`): sus FSM pasan a ser del enemigo, así que solo funcionan en el PC que lo mueve; en los demás se
    paran y su giro, rayos, sonidos y animaciones se copian. Solo las que dañan (las puertas de la arena van aparte) y
    de la misma sala (la sala y su escena `_boss`). Al morir el enemigo vuelven a ser de cada PC y sus rayos se apagan.
    Igual lo que el enemigo avisa directamente y crea cosas que dañan (el invocador de barriles del Falso Caballero,
    `SUMMON`): solo lo usa el PC que mueve al jefe y los barriles llegan como copias (antes caían al azar distintos en
    cada PC).
  - La sombra de cada jugador no es un enemigo compartido.
  - Probados: Mamá Gruz, Falso Caballero, Hornet, Mawlek, Maestro de Almas, Defensor del Estiércol, Nosk, Vasija Rota,
    Guardián de Cristal normal y enfurecido y Señores Mantis (despiertan, atacan al jugador que no es dueño de la sala y siguen el
    combate al cambiar de PC; el rayo y las torretas del Guardián se ven igual en los dos), escupidores y zombis. El
    contador del diario (`killsMegaBeamMiner`, que la sala del enfurecido usa para darlo por vencido) baja una sola vez.
- **Efectos del otro jugador** (`Game/Players/PlayerEffects`, `RemoteEffects`, `HeroEffectTemplates`): cuando el
  caballero local hace algo que se ve, se envía un `PlayerEvent` pequeño (fiable, a la sala) y cada compañero lo
  reproduce sobre el avatar de quien lo hizo con los efectos de **su propio** caballero. Son solo visuales: el daño
  real ya va por los golpes directos.
  - **Copia solo visual**: al cargar la partida se copian las carpetas de efectos del caballero (`Attacks`, `Effects`,
    `Spells`, `Focus Effects`, `Sounds`, `Dream Effects`) y se les quita todo lo que juega: colisiones, cuerpos, scripts
    y las FSM de daño o de colisión (`damages_enemy`, `Control Collider`...). Quedan sprites, animaciones, partículas,
    sonidos y las FSM que solo animan su objeto (aparecer, desvanecerse, apagarse). Cada compañero tiene una copia de
    esas plantillas pegada bajo el sprite de su avatar (que se voltea como el caballero), y lo que en el caballero
    apuntaba a él (su escala, dónde sonar) apunta a la copia. La copia dura toda la partida: al cambiar de sala se
    aparca apagada y como al crearla, y se pega al avatar nuevo (crearla cuesta; si una FSM sacó un objeto al mundo y
    la sala se descargó, se rehace).
  - **Estados de las FSM de acciones** (`Spell Control`, `Nail Arts`, `Superdash`, `Dream Nail`): un gancho a
    `Fsm.SwitchState` envía (FSM, estado) cuando el caballero entra en un estado con efectos (se calcula una vez por
    estado). El receptor lee las acciones del **mismo estado en su caballero** y las hace sobre la copia: activar,
    animar, emitir partículas, sonar, parar, y los avisos a las FSM visuales de los efectos (`PLAY`, `UP`, `DOWN`...).
    Lo que va al propio caballero (su animación, mostrarlo) ya llega en su estado; la cámara, la velocidad y los avisos
    al caballero no se hacen. Así cada hechizo, Crystal Heart, la curación o el aguijón onírico salen igual que en el
    juego sin reescribirlos uno a uno, y con la temporización real (cada estado llega cuando pasa).
  - **Lo que hace el código del caballero** y no una FSM tiene su método: tajos (gancho a `NailSlash.StartSlash`, con
    tamaño y animación según Mark of Pride, Longnail y furia), dash y Shade Dash (`HeroDash`, con dónde puso su ráfaga),
    doble salto, salto de pared, carga de las artes (se mira cada frame) y golpes recibidos (`TakeDamage`, con destello
    del avatar).
  - **Bolas de fuego**: lo que lanza `Spell Control` crea la bola real con daño, así que se reproduce aparte: el
    objeto que la lanza sin su lógica (estallido y partículas), y una copia visual del proyectil que vuela recta a su
    velocidad hasta chocar con el terreno (rayo contra sus capas) o deshacerse. Con Flukenest sale el estallido con su
    sonido y, en lugar de la bola, sus moscas (abajo).
  - **Al mismo tiempo que la animación**: cada evento lleva el tick del remitente (el mismo reloj que su estado) y
    espera en una cola fija hasta que el avatar, que se dibuja un poco en el pasado, llega a ese tick (como mucho medio
    segundo). Sin memoria nueva por evento; la memoria creada mientras el compañero ataca es la del juego.
  - Los amuletos que cambian cómo se ve (Mark of Pride, Longnail, furia, Shaman Stone, Flukenest) viajan en un byte.
  - **Lo que los amuletos y el código del caballero sacan del pool del juego** (`CharmTemplates`,
    `RemoteCharmEffects`): un gancho a `ObjectPool.Spawn` reconoce el prefab y lo apunta; al final del frame (ya con la
    velocidad, giro y escala que le pone quien lo crea) se envía cuál es, dónde y cómo. El PC que mira pone una copia
    sin colisiones, cuerpo ni lógica, que se mueve sola contra el terreno (rayos contra sus capas), y la reutiliza:
    - Rayos de Grubberfly's Elegy (normales y de furia, en las cuatro direcciones, con Mark of Pride): salen del
      avatar con la velocidad y el desplazamiento de su FSM, se deshacen o chocan con su impacto.
    - Nubes al curarse de Spore Shroom (más grande con Deep Focus) y de Defender's Crest, donde salieron.
    - Moscas de Flukenest, cada una con la posición y velocidad con las que salió: caen con su gravedad, rebotan hacia
      arriba como `SpellFluke` y revientan a los 2-3 s o al tocar un enemigo. Con Defender's Crest, la mosca grande
      vuela girando, tiembla y explota en su nube a los 2 s.
    - Ráfagas y anillo del dash sombra (también con Sharp Shadow y hacia abajo con Dashmaster), el polvo del dash en el
      suelo y el aterrizaje fuerte. M4 los buscaba como hijos del caballero y no salían.
  - **Onda de los gritos**: la crea el estado del grito como en el caballero (`CreateObject`, sin sus ondas, solo las
    líneas) y su final llega con el estado siguiente. Solo si el compañero está a la vista de la cámara local: su FSM y
    los temblores del grito mueven la cámara del que mira.
  - **Thorns of Agony y golpe recibido**: las FSM `Charm Effects/Thorn Counter` (su sonido; las espinas son la
    animación del caballero, que ya llega en su estado) y `Effects/Damage Effect/Knight Damage` (la grieta blanca y
    los tajos oscuros, que salen del pool como objetos visuales) se reproducen como las demás FSM de acciones.
  - **Acompañantes que atacan solos** (`Companions`: crías de Glowing Womb, tejedoras de Weaversong, Grimmchild y sus
    bolas de fuego): el dueño los apunta al salir del pool (los que ya estaban, al preparar los efectos) y envía unas
    20 veces por segundo dónde está cada uno, su giro, escala y el sprite de cada parte (mensaje 34, no fiable, a la
    sala). En los demás PCs son copias solo con sus sprites, interpoladas al tick en que se dibuja el avatar de su
    dueño. No dañan ni atacan: el daño ya lo hizo el PC del dueño.
  - **Dreamshield**: su escudo (`Orbit Shield`, que saca la FSM `Spawn Orbit Shield`) va como un acompañante más, con
    su brillo (sus partículas se conservan solo en él).
  - **Aura de Defender's Crest**: su humo y su zumbido (copia de `Charm Effects/Dung`) van pegados al avatar, detrás
    de él. Si está encendida viaja en el estado del jugador (una bandera en el byte de `PlayerSnapshot`, sin ocupar
    más), así que la ve también quien entra después. La nube que deja cada 0,75 s sale del pool y llega como los
    demás efectos sueltos, a la profundidad que le pone su FSM (detrás del caballero).
  - **Polvo al correr y saltar**: el del salto (`Jump Effects`) y el del aterrizaje suave (`Soft Land Effect`) son
    efectos sueltos que encienden solo el hijo del suelo de la sala, como `JumpEffects` y `SoftLandEffect` (polvo,
    pasto, hueso o salpicadura), con el sonido del aterrizaje. El de correr (`Run Effects`, `EffectKind.Run`) empieza
    cuando el caballero lo saca del pool y para cuando lo suelta: en el compañero va pegado al avatar con las
    partículas del suelo y al parar se suelta, deja de emitir y se apaga a los 2 s.
  - **Máscaras azules**: el estado `Blue Effects` de `Knight Damage` (gotas `Spatter Blue`, `Splat Explode Blue` y
    `Blue Flash`, con el tamaño de su `SetScale` sobre lo recién creado, y su `SpawnBlood` azul). Si el golpe se llevó
    una máscara azul (`EffectCharms.DamagedBlue`) no salen la grieta, las partículas ni los tajos oscuros del golpe
    normal: allí su lógica salta antes a `Blue Effects`.
  - **Parpadeo de invulnerabilidad**: si es invulnerable (`cState.invulnerable`, tras un golpe o al revivir) viaja
    como otra bandera del estado y el avatar va y vuelve entre su color y el de invulnerable, con el color y el ritmo
    del `InvulnerablePulse` del caballero local.
  - **Pasto y telas a su paso** (`PartnerGrass`, sin red): en cada PC se mira unas 20 veces por segundo qué pasto toca
    la caja de cada avatar (la del caballero local, `Physics2D.OverlapBox` sobre las colisiones del pasto de la sala,
    buscadas una vez al cambiar de sala) y se hace lo mismo que la entrada del caballero: `GrassBehaviour.WindReact`
    (también al andar por dentro, como su `FixedUpdate`), el `OnTriggerEnter2D` de `GrassSpriteBehaviour` y
    `Grass.Push` (también las telas). Al pasto le llega una caja apagada puesta donde está el avatar (solo mira su
    posición para inclinarse). El avatar sigue sin colisión ni la etiqueta `Player`.
  - Lo que vuela suelto (bolas de fuego, rayos, moscas, acompañantes) no cuelga del avatar, pero `Skins` le pone la
    skin de su dueño igual (`RemoteAvatar.LooseEffects`).
  - Comprobados igual en los dos PCs: Quick Slash (cada tajo llega), Mark of Pride y Longnail (tamaño del tajo),
    Shaman Stone (tamaño de la bola), Spell Twister (el alma del mini-HUD baja lo mismo) y Dashmaster y Sprintmaster
    (el dash hacia abajo y la carrera llegan con la posición y la animación).

### Interfaz (`UI/`)

- **Menú principal → Multijugador**: copia de los menús del juego (botón de "Iniciar partida", pantalla de "Extras").
  Nombre y puerto; hostear (mundos coop, borrar con confirmación, nuevo) y unirse (dirección y código).
- **Pausa → Multijugador** (solo en partida multijugador): mundo, código, IP oculta hasta pedirla, jugadores con su
  ping y cerrar o salir de la partida. Páginas *Ajustes de la partida* (dos, con filas "Opción < valor >" copiadas de
  las opciones del juego: `GameStyledMenu.AddOption`, que el invitado ve sin poder cambiar; la primera con dificultad
  y revivir, la segunda con pantalla y permisos) y *Compañeros* (vida y alma de cada uno y dónde está, dar geo, ir con
  él, desatascarse y recargar la sala; lo que el host no permite sale apagado con `SetButtonEnabled`). Las acciones
  que se hacen al quitar la pausa vuelven al menú de pausa y lo cierran en cuanto el juego deja quitarla
  (`UIManager.ignoreUnpause`, mientras el menú termina de aparecer; con una espera fija de 0,3 s a veces la partida se
  quedaba en pausa y la acción no se hacía).
- Avisos en pantalla con la letra del juego (código al abrir la partida, quién entra o sale).

### Funciones del coop (`Game/Features/`)

Decisiones aprobadas en [Decisiones de M5](#decisiones-de-m5). Cada función es una clase con `Attach`/`Detach` que engancha
`MultiplayerRunner` como las demás capas; sus ganchos al juego se ponen al cargar el mod (`Die()` es tan corto que el
juego lo copia dentro de `TakeDamage` al compilarlo: si se engancha más tarde, la muerte no pasa por el gancho). Los
mensajes nuevos van del 73 al 117 (el 98, el 108 y el 115 son de `EnemySync`, el 104, el 105 y el 107 de `HazardSync`,
el 106 de `StagTogether`, el 109 y el 110 de `SceneObjectSync`, el 111 y el 117 de `Revive`, el 112 de `PartyAbilities`,
el 113 y el 114 de `PantheonTogether` y el 116 de `MenderBugSync`). La interfaz es uGUI (`UI/HudCanvas`): un lienzo que sigue la zona de la cámara del
juego (16:9; con otra proporción el juego deja franjas) y escala con su alto como el HUD del juego; las piezas se crean
una vez y cada frame solo se mueven o se encienden, y los textos se rehacen solo al cambiar.

- **Estado de cada jugador** (`PartyState`, mensaje 74): en pie, caído, en cinemática, en sueño o entrando (`Joining`:
  conectado pero sin caballero, en el menú o en la introducción; no cuenta como en pie). Lo decide el host y
  lo reparte entero al cambiar y a quien entra. Un caído no cuenta para repartir enemigos (`EnemySync.AssignControllers`:
  los suyos pasan enseguida a quien sigue en pie), ni para las esperas de los combates de jefe (`MissingNear`,
  `EveryoneInside`), ni para los diálogos (`AllConvoDone`), y su avatar no se dibuja.
- **Ajustes del host** (`HostSettings`, mensaje 73 con versión de formato, ahora la 3): dificultad (vida de jefes 1,5×
  por defecto, solo con el compañero en la sala; enemigos; daño recibido), revivir (sí/no, tiempo, alma, máscaras,
  invulnerabilidad, en el banco), pantalla (nombres, vida del compañero, flechas, compañeros en el mapa, avisos, barra
  del jefe, barras de enemigos) y permisos (regalar geo, ir con un compañero, saltar cinemáticas). Se guardan en `settings.json` de cada
  mundo (por nombre de clave: los que falten en un archivo viejo quedan por defecto) y se envían al cambiar y a quien
  entra. La vida extra se hace dividiendo el daño en `HealthManager.TakeDamage` en el PC que mueve al enemigo
  (`Difficulty`; las marionetas reciben los golpes con multiplicador 0); un jefe es lo que tiene `unlockBossScene`. El
  juego redondea el daño (`RoundToInt`, y los .5 al par), así que se lleva por enemigo lo que sobra y se pone el
  multiplicador que da justo el entero: con el aguijón de 5, 1,5× quita 3, 3 y 4 (antes 3 siempre, 1,67×) y 2× quita 2
  y 3 (antes 2, 2,5×). El daño recibido se escala en `HeroController.TakeDamage`, salvo pinchos, ácido y pozos, con lo
  que sobra para el golpe siguiente; un golpe que queda en 0 se recibe igual (retroceso e invulnerabilidad: si no, el
  enemigo volvería a golpear en el frame siguiente) pero `PlayerData.TakeHealth` no quita vida. Antes, con 0,5× los
  golpes de 1 (casi todos) seguían quitando 1. Un golpe que ya llega con 0 (los Shrumeling, o un Mosscreep escondido:
  en el juego no hacen daño al tocarlos) pasa tal cual y el juego lo ignora; antes se convertía en uno de 1 sin quitar
  vida, con retroceso e invulnerabilidad, y parecía que pegaban. El alma para revivir nunca pasa de `maxMP` (con la sombra sin
  recuperar el orbe es de 66 y con "99" nadie podía revivir). "Avisos" apaga los avisos de lo que hacen los demás (se
  unió, salió, cayó, sombra recuperada, regalo de geo, Grimmchild, skin); los que responden a lo que hace uno
  (devoluciones, "Ir con ...", esperas de combate) salen siempre.
- **El botín cae igual en todos los PCs** (`DropSeeds`): cada jugador tiene su propio botín, así que cada PC lo crea, y
  la dirección y la fuerza de cada pieza las decide el juego al azar (en un PC el geo caía a la izquierda y en el otro
  a la derecha). Justo al lanzarlo se pone la misma semilla del azar en todos (de qué objeto es y cuántas veces lanzó ya
  eso) y después el azar vuelve a como estaba. Sin mensajes. Se hace en `FlingUtils.SpawnAndFling` (el geo de los
  enemigos: de un enemigo, solo el geo; su número compartido de `EnemySync`), `Breakable.Break` y los lanzamientos de
  las FSM de la sala que no son de un enemigo (rocas de geo, cofres; contados por estado y acción, porque en el PC que
  no golpea una roca solo se repiten algunos de sus estados), y en el lado y el ángulo de un objeto brillante que sale
  volando (`SendRandomEvent` y `FlingObject` de `Shiny Control`). La semilla no va en `HealthManager.Die`: antes de
  soltar el geo, la muerte gasta azar en cosas que dependen de quién golpeó (el giro del cadáver) y cada PC llegaba al
  geo con otra tirada.
- **Revivir** (`Revive`, mensajes 75 a 80): `HeroController.Die` se sustituye por la caída si hay alguien en pie (en un
  sueño, solo si está en el mismo; en los Panteones, nunca). El caído envía `Downed` (sala y suelo bajo él; si debajo no hay `Terrain` en 100 unidades, si ese suelo toca un
  `DamageHero` de peligro o si murió por un peligro, su `hazardRespawnLocation`: el cuerpo tiene que quedar donde el
  compañero pueda estar de pie a su lado, y antes un caído en el aire sobre un pozo quedaba flotando); el host
  le asigna el compañero en pie al que seguir. Lo que tenía debajo puede desaparecer (las plataformas del Coliseo se
  retraen entre oleadas): cada PC de pie en la sala mira cada 0,25 s si el cuerpo sigue apoyado y no toca un peligro; tras
  1 s sin suelo pide moverlo (`BodyMoved`, mensaje 117) al suelo de debajo o, si no hay o son pinchos, junto a él si está
  pisando suelo. El host lo valida y lo reparte con el estado de la partida. Antes quedaba flotando fuera del alcance de
  quien estaba en el suelo (issue #3) y solo se revivía saliendo a un banco. Probado en `Room_Colosseum_Silver` con el
  caído en cada PC (de `y=23,4` al suelo en `y=6,4`) y, con los pinchos fuera por la orden real del Coliseo, junto a quien
  estaba en otra plataforma (`fsmgo Colosseum Manager/Battle Control|Spikes`). Ponerlos en `Expand` con `fsmstate` no
  sirve para probarlo: así no dañaron. Con la orden real, el daño vino de `Colosseum Spike (19)`, que lleva `DamageHero`,
  a 17 unidades del caballero. El caballero del caído se oculta (sin colisión, sin gravedad,
  invencible, sin control ni entrada; se reaplica cada frame; se esconde con `Renderer.forceRenderingOff`, así lo que el
  juego encienda o apague entre medias queda como lo dejó, y la luz y la viñeta siguen) y, tras la animación de muerte, se pega al avatar del compañero, con
  las zonas de cámara emuladas; si el compañero cambia de sala, viaja detrás de él y entra por la misma puerta que él
  (la de esta sala hacia esa sala más cercana a donde se le vio por última vez). Los viajes a un punto (volver al
  cuerpo, "Recargar sala") entran por `dreamGate` como `SceneTravel`. Antes todos entraban sin puerta: el juego ponía
  al caballero en la primera puerta de la sala y en algunas salía por ella (sala de Madre Gruz → `Crossroads_50`).
  Su cuerpo (`DownedBody`: animación "Death" y luego "Prostrate", con "† Nombre") se ve en su sala; con un compañero
  encima (el que lo revive), el "† Nombre" sube por encima del nombre de este para no pisarse. Junto a él, Focus
  cobra el alma al empezar (se devuelve si se suelta o te golpean), no cura (`CanFocus`) y al completarse se pide
  `ReviveRequest`; el host lo valida (en pie y en la sala del cuerpo) y responde `Revived` (cuerpo y vida). El progreso
  va a la sala con `ReviveProgress` (no fiable): el caído lee "X te está reviviendo". `BenchRest` al sentarse revive a
  los caídos en ese banco con toda la vida; si no queda nadie en pie, `Wipe`: cada caído vuelve a su cuerpo y muere de
  verdad. Al guardar estando caído no se guardan la invencibilidad ni la vida a 0. Un caído con el cuerpo en un sueño
  del que ya salieron todos los que estaban en pie (el compañero ganó, cayó al vacío o despertó) revive junto a ese
  compañero (`Revived` sin posición: donde esté al llegar); antes lo seguía fuera del sueño como caído y nadie podía
  revivirlo.
- **HUD del compañero** (`Vitals`, mensaje 81; `PartnerHud`): vida, vida máxima, azules, alma y zona de la sala de cada
  uno, al cambiar y como mucho 4 por segundo; el host guarda las últimas y se las da a quien entra. Mini-HUD arriba a la
  derecha, espejo exacto del HUD del compañero (sprites de `HUD Cln`: la llena `appear_v020004`, la vacía
  `health_backboard` y la azul `idle0000`, a escala entre sí por sus vértices de tk2d y girados si tk2d los guarda
  girados), con su orbe de alma y su estado; sus máscaras sobre su cabeza 3 s al cambiarle la vida y flechas en el borde.
  Nunca pone una zona `NONE` (salas de paso del juego); mientras viaja en ciervo dice "viajando en ciervo".
- **Barra del jefe** (`BossBar`): los jefes se apuntan al encenderse (`HealthManager.OnEnable`), con el nombre de su
  entrada del diario; la vida máxima es la mayor vista. Jefe (`Difficulty.IsBoss`, también para la vida extra) es quien
  tiene estatua en el Salón de los Dioses (`unlockBossScene`) o aparece con 300 de vida o más; se decide una vez por
  enemigo. Los de los sueños (Tirano de Almas, Príncipe Gris Zote) no tienen estatua y antes se quedaban sin barra.
  Se quita en cuanto su muerte está en marcha (`EnemySync.IsDying`): el Falso Caballero muere por la cabeza y su
  cuerpo vuelve a tener vida en cada fase. Con barras visibles, `Notice.SetLift` sube los avisos (abajo a
  la derecha) por encima de ellas: el de dos líneas pisaba la barra.
- **Barras de enemigos** (`EnemyBars`, ajuste del host, apagado por defecto): como la barra del jefe, los enemigos que
  no son jefes se apuntan al encenderse; cada frame se dibuja una barra pequeña (hasta 16, creadas una vez) sobre los
  heridos que se ven, encima de su `Renderer`. En el PC que no los mueve se lee la vida de la marioneta.
- **Sombras** (`ShadeSync`, mensajes 82 a 84): cada uno informa de la suya al host, que la reenvía y la da a quien entra.
  En cada PC se crea una copia (`hollowShadeObject` con su lógica, sin "Dreamnail Kill"; su nombre empieza por
  "Hollow Shade" para que `EnemySync` no la comparta) que lee los datos del dueño (`GetPlayerDataInt`/`String`). Su
  muerte no va por la vía normal: avisa al host, que acepta el primer aviso y pide al dueño que mate la suya (si no está
  en su sala, se crea un momento junto a él).
- **Compañeros en el mapa** (`PartnerMap`, mensaje 103, no fiable, a todos): cada jugador envía 4 veces por segundo
  la sala del mapa y su punto en ella de 0 a 1, con la cuenta de `GameMap.Update` para la brújula (los campos privados
  `sceneWidth`, `sceneHeight` y `originOffsetX/Y`; en una sala que no sale en el mapa, `inRoom`, la puerta por la que
  se entró). Así quien lo recibe no necesita las medidas de una sala que no tiene cargada: busca la sala por nombre
  entre los hijos de las zonas del mapa y pone ahí una copia de `compassIcon` (sin FSM), azul, o roja en su cuerpo
  si el jugador está caído (calculada al caer, en la sala del cuerpo: el cuerpo puede no estar donde cayó, sino en el
  último sitio seguro; después no cambia aunque el caballero oculto siga al compañero). Se ve con el mapa abierto (`flamePins` activo: lo encienden el mapa del
  inventario y el rápido, y lo apaga `CloseQuickMap`), solo si los dos llevan el Wayward Compass equipado (quien no lo lleva envía su sitio como oculto; verlos siempre era demasiado fácil) y salvo en sueños, el Palacio
  Blanco y los Panteones (donde el juego tampoco muestra la brújula). Sin noticias en 3 s, el icono se quita.
- **Avisos de versión** (`UpdateCheck`): al cargar el mod se apunta la versión (`LastRunVersion` en los ajustes
  globales); si es otra que la de la última vez, o no había ninguna pero sí ajustes de antes, el menú principal avisa
  "se actualizó a la X" y *Multijugador* ofrece sus novedades (la página de su versión). Además, si
  `CheckForUpdates` está activado (sí por defecto, en *Multijugador › Buscar actualizaciones*), una consulta a la lista
  pública de versiones de GitHub (`UnityWebRequest`, 10 s como mucho, sin datos del jugador) busca la más nueva: si es
  más nueva que la instalada, el menú principal lo avisa y *Multijugador* la ofrece para descargar (todos necesitan la
  misma versión para jugar juntos). Los avisos salen 2 s después de llegar al menú principal, una vez por sesión.
  Prueba: `fakeupdate <versión>`.
- **Reconexión automática** (`MultiplayerRunner`): si el invitado pierde la conexión por tiempo después de haber
  entrado (no si el host cierra, ni por código, versión o partida llena), vuelve al menú como antes y, ya en el menú
  principal (el mundo solo se carga ahí), se vuelve a unir con la misma dirección, código y nombre: hasta 12 intentos,
  5 s después de que falle cada uno (cada intento espera hasta 10 s al host). Al conectar, el host le envía el mundo
  con su personaje como a cualquiera que entra. *Esc* en el menú principal (que además corta el intento en curso si
  aún no conectó), unirse, hostear o salir lo cancelan. Rendirse y cancelar quedan también en el registro.
- **Compañeros** (`PartyActions`, mensajes 85 y 86): regalo de geo validado por el host (o devuelto), desatascarse y
  recargar la sala al quitar la pausa.
- **Ir con un compañero** (`PartnerTravel`, mensajes 99 a 102): quien lo pide desde el panel lo envía al quitar la
  pausa (`GoToRequest`); el host comprueba el ajuste y el estado de los dos (caído, cinemática, sueño) y pregunta al
  compañero (`GoToQuery`), que responde (`GoToAnswer`) con su sala y el último suelo que pisó en ella (apuntado cada
  frame con `cState.onGround`, sin pinchos ni caída en curso; si aún no pisó suelo en la sala, su punto de
  reaparición) o por qué no se puede (en otro mundo, cambiando de sala, en un sitio al que no se sigue). El host lo
  pasa a quien lo pidió (`GoToResult`). Si es el host quien pide o a quien se pide, su parte se hace en el sitio. En
  la misma sala se coloca al lado; en otra, `SceneTravel` (sin puerta) y al llegar se coloca. "Al lado" es 0,9 a la
  izquierda o a la derecha si cabe el caballero (sin `Terrain`), hay suelo a la misma altura y nada con `DamageHero`;
  si no, en su mismo punto. Se fija ahí la reaparición tras pinchos (`SetHazardRespawn`). Este PC además no deja ir si
  está caído, en un sueño o una cinemática, cambiando de sala, sentado en un banco (movido así se quedaba sentado en
  el aire) o sin control, ni si no juega el mundo coop del host (`CoopSaves`: el host en el suyo, el invitado en el
  del host; con partidas propias las salas se llaman igual pero no son el mismo mundo). Sin respuesta en 6 s, se
  abandona con un aviso.
- **Viaje sin puerta** (`SceneTravel`, lo usan "Ir con ..." y la vuelta de las cinemáticas compartidas): se entra con la
  entrada `dreamGate` del juego (la de la Puerta de los sueños), que coloca al caballero en `dreamGateX/Y` antes de
  mostrar la sala; ese punto se cambia un momento por el pedido y se repone al llegar. Con la entrada vacía el juego
  ponía al caballero en la primera puerta de la sala (`FindTransitionPoint` con "cualquiera"): en Cruces Olvidados
  (sala de Madre Gruz) aparecía en `right1` y salía por ella a `Crossroads_50` antes de que el mod lo colocara.
- **Lo que hace cada uno** (`PartyState`, mensaje 91): cada jugador informa al host si está en una cinemática
  compartida, en un sueño o viajando en ciervo (`StagTravel`: la sala del viaje, `Cinematic_Stag_travel`, no es de
  juego ni de ninguna zona, así que su sala llega vacía y su zona es `NONE`; el HUD de los demás ponía "en None"); es
  su estado mientras no está caído (al revivir en un sueño vuelve a "en sueño"). El
  mini-HUD lo muestra si está en otra sala. Con eso va si tiene el juego en pausa (el host lo reparte en
  `PartyStatus`): con la escala de tiempo a 0 sus enemigos se quedaban congelados para los demás, así que quien está
  en pausa, como un caído, no se queda con enemigos (`PartyState.IsInactive` en `EnemySync.AssignControllers`). El HUD
  de los demás y su fila en *Compañeros* dicen "en pausa", también en la misma sala: su caballero se queda quieto y
  parecía que se había trabado.
- **Cinemáticas compartidas** (`SharedCutscenes`, mensajes 87 a 90): los finales, los créditos, Mr. Mushroom y las
  pantallas con las que acaba el final (partida completada, Alma de Acero). Quien entra en una lo dice al host, que
  lleva a los demás (con un cambio de sala, esperando si están en pausa o cambiando de sala; un caído no va). Al
  llevado, la malla del vídeo (`CinematicPlayer`) quedaba donde estaba su cámara en la sala anterior y se veía negro: se
  vuelve a colocar delante de la cámara como lo hace el juego. Saltar (`GameManager.SkipCutscene`) es un voto: el host
  manda saltar a todos cuando votaron todos los que la ven (o con uno, si así lo eligió en "Saltar cinemáticas") y
  todos ven "Esperando a X para saltar 1/2"; si el que faltaba se va, se salta. En el host su propio voto completa la
  votación en el sitio (su mensaje al servidor se procesa dentro de la misma llamada) y el salto ya había empezado
  cuando el gancho le quitaba la marca de "saltando" para no dejarla puesta: el host se quedaba en la cinemática
  mientras los demás seguían. Ahora la marca solo se quita si no llegó la orden de saltar durante el voto. Al terminar
  (`CutsceneHelper.DoSceneLoad`) la siguiente del final sigue; el final de la secuencia lleva a todos a su banco
  (`ContinueGame`) en lugar del menú, y quien fue llevado a una que acaba en una sala de juego vuelve a donde estaba
  (`SceneTravel`).
- **Presentaciones de jefe** (`BossPresentation`, mensaje 92): `RelinquishControl` llamado desde la lógica de un jefe
  (enemigo o lo que cuelga de él) o de un controlador de combate compartido, con un jefe vivo en la sala, avisa a la
  sala; los demás caballeros se quedan quietos hasta que esa lógica devuelve el control (como mucho 30 s, y se sueltan
  si quien presenta sale de la sala). Si su propia lógica también lo quitó, se espera a las dos.
- **Sueños compartidos** (`DreamSync`, mensajes 93 y 94): al cargar una sala se buscan las FSM con un cambio de sala de
  tipo sueño (`BeginSceneTransition` con `Dream` o `GrimmDream`: los cuerpos de los jefes, Grimm dormido, el Hollow
  Knight vencido); cada frame se mira si alguna sale de su reposo, lo que significa que el aguijón de este jugador la
  golpeó, y se avisa a la sala con el aviso con el que salió (`DREAM HIT`). En los demás PCs la misma FSM recibe ese
  aviso: mismo destello, sala de vuelta y cambio de sala. Antes se pone la sala de vuelta (`dreamReturnScene`) del
  llevado. Si la entrada no está encendida en ese PC (la de la Radiancia pide el Corazón del Vacío), se entra directo al
  mismo sueño. Al salir de un sueño con jefe sin caer al vacío (`Dream Fall Catcher`) ni morir, se avisa a la sala y los
  que siguen dentro salen como el juego, por el estado `Leave Dream` de la FSM `Dream Nail` del caballero (quedan
  tendidos y se levantan al pulsar); las demás salidas son de cada uno.
- **Grimmchild** (`GrimmchildGift`, mensaje 95): el amuleto es de cada jugador pero su nivel y la Compañía son del
  mundo; si uno lo recibe, los demás también.
- **Entrar juntos en un Panteón** (`PantheonTogether`, mensaje 113, fiable, a la sala): al pulsar empezar en el cuadro
  de una puerta del Atrio (`BossDoorChallengeUI.Begin`), se avisa a la sala con la puerta y las ataduras. Los demás que
  no estén ocupados (caídos, en un banco, en pausa, hablando o ya en un Panteón) hacen lo mismo que ese botón en su PC
  (`bossReturnEntryGate`, `BossSequenceController.SetupNewSequence`) y viajan a la primera sala de la secuencia como la
  puerta (`door_dreamEnter`, `GodsAndGlory`). Antes el viaje no se seguía (no es un sueño) y quien empezaba peleaba
  solo. Dentro, cada PC lleva su secuencia; cuando la sala acaba en un PC (`BossSceneController.EndBossScene`: el jefe
  muerto o la estatua de la sala de descanso cruzada) avisa a la sala (mensaje 114) y los demás la acaban también, así
  que pasan juntos al siguiente jefe. Llamarla dos veces no hace nada (`endedScene`). Antes, en la sala de descanso cada
  uno salía por su cuenta y el primero seguía solo el resto del Panteón.
- **Viajar juntos en ciervo** (`StagTogether`, mensaje 106, fiable, a la sala): cuando la FSM `Stag Control` de la
  estación de este jugador entra en `Fade` (ya eligió destino: `To Scene` y `To Position`), se avisa a la sala con
  dónde está. En los demás PCs su propio ciervo recibe el mismo destino y pasa a `Fade` (quitando el control al
  caballero), así que hace lo del juego: fundido, `Cinematic_Stag_travel` y llegada a la salida del ciervo de destino.
  Solo va quien está a menos de 10 de quien viaja (en la otra punta de la estación, haciendo otra cosa, no se le lleva)
  y no está caído, muerto, cambiando de sala, sentado en un banco ni en pausa (ve "X se fue en ciervo"). Quien sale así
  no vuelve a avisar. Es un ajuste del host (*Viajar juntos en ciervo*, sí por defecto; formato de ajustes 4): con no,
  nadie avisa y cada uno viaja por su cuenta. Antes cada uno viajaba por su cuenta y el que se quedaba veía irse el ciervo sin poder seguirlo.
- **Sentados en el mismo banco** (`PlayerSync.ShareBench`): el estado del jugador lleva si está sentado (bandera 16 del
  byte de `PlayerSnapshot`: `atBench` y el banco con su control; `atBench` solo no basta, el juego no lo quita si se sale
  de la sala sin levantarse). El centro del banco es donde el juego deja a este caballero (se toma 0,6 s después de
  sentarse: antes aún lo está colocando). Los sentados a menos de 2,5 de ese centro se reparten a 1,1 uno de otro
  alrededor de él por número de jugador (el menor a la izquierda), así todos los PCs los ven igual: este caballero se
  corre a su sitio y a los compañeros se les dibuja en el suyo. Al quedarse solo vuelve al centro. Antes los dos
  caballeros quedaban uno encima del otro.
- **Skins** (`Skins`, mensajes 96 y 97, transferencia grande 3): compatibles con CustomKnight, como la 0.7.3 (con
  `AtlasRepair` para las hojas antiguas del HUD y el geo). Cada uno anuncia su skin (nombre y huella SHA-256 de los
  archivos que ven los demás) y quien no la tiene la pide al host, que la envía o se la pide a su dueño. Van por la
  conexión cifrada del mod como transferencia grande de **baja prioridad** (`NetConnection`: cola aparte, solo sin otras
  transferencias pendientes, hasta unos 2 MB/s, como mucho un tick de trozos de golpe (cada acuse cubre 64 paquetes) y
  160 mensajes sin confirmar; si la latencia sube 60 ms sobre la más baja vista, baja a 0,25 MB/s, así los golpes y
  eventos no esperan detrás de la skin). Máximo 32 MB, solo PNG de
  hasta 4096x4096; la huella se comprueba en otro hilo. Las recibidas se guardan en `Mods/HKMultiplayer/SkinCache`
  (`<huella>.hksk`, hasta 256 MB, se borran las usadas hace más tiempo): la siguiente partida con esa skin la lee de ahí
  (comprobándola igual) y no la pide. Cuerpo con un bloque de propiedades por renderer; hechizos, amuletos y HUD propios en los
  materiales compartidos; efectos del compañero (sus copias bajo el avatar) con su skin. El cuerpo de un caído (otro
  objeto con la colección del caballero) lleva también la skin de su dueño, con el mismo bloque: antes salía con el
  dibujo del juego. Crear la textura de una hoja de
  4096 cuesta 0,15-0,25 s (el juego descomprime el PNG en el hilo principal): se hace en pausa o al cambiar de sala (si
  no llega ese momento en 3 s, fuera de un combate de jefe, con un tirón corto). Cada PC elige si ve las skins de los demás.
  El orbe de alma lleno propio (`OrbFull.png`) cambia el sprite de `Soul Orb/Orb Full` y de los fotogramas de su pulso
  (`Orb Full/Pulse Sprite`, `soul_orb_glow0000` a `0015`), reconocidos por el nombre del sprite: los del pulso van en un
  atlas (`sactx-...`) y, buscados por el nombre de su textura, no se cambiaban, así que con el orbe lleno parpadeaba
  entre la cara del juego y la de la skin. Si su imagen no se pudo crear en ese frame, se vuelve a intentar (antes el
  orbe se quedaba como el del juego hasta cambiar de skin).
- **Barra del jefe**: el nombre es el título con el que el juego presenta al jefe (hoja `Titles`, partes `_SUPER`,
  `_MAIN` y `_SUB`), buscado en los textos de sus FSM o en las palabras de su nombre; si no, el de su diario.

### Cambios de la revisión contra el juego (0.26.0)

Lo que cambió al revisar el mod contra el juego descompilado (12/10/2026) y no está ya contado arriba. La lista de
la revisión, con cada fallo, su arreglo y cómo se probó, estaba en `docs/review-backlog.md`: se quitó al cerrarla
(las 172 entradas) y sigue en el historial (`git show 6ea533d:docs/review-backlog.md`).

- **Ids de sala** (`ScenePaths`): la huella de cada objeto sincronizado se apunta al cargar su sala (`sceneLoaded`,
  antes del `Start` de los objetos) y se usa siempre esa (`StableHash`): un enemigo de la raíz que se destruye o un
  objeto brillante ya recogido (`Shiny Control` hace `DestroySelf`) ya no cambian los ids de lo que va detrás. Los
  textos entran en los ids con FNV-1a (`ScenePaths.Fnv`), no con `string.GetHashCode`. `FsmId` cuenta también el puesto
  entre las FSM del mismo nombre de un objeto.
- **Daño a enemigos** (mensaje 108, `EnemyDamage`): el daño que no pasa por `TakeDamage` (`ApplyExtraDamage`, las
  moscas de Flukenest) y la muerte de una marioneta van a quien la mueve. Una marioneta no hace retroceso ni muere en
  local. Los golpes que llegan a un enemigo a punto de cambiar de PC se guardan 3 s y se aplican al tomarlo.
- **Traspaso de enemigos**: lleva si el jefe ya despertó, el estado del `Walker` y los textos recortados para que
  quepa. Los creados en combate con vida pasan a otro si quien los mueve cae o pausa.
- **Objetos que activa solo un caballero** (`SceneObjectSync`): la palanca del puente (109) y, con el mensaje 110
  (`LocalTrigger`), las plataformas que giran o caen al pisarlas y los huevos de Cañón de Niebla.
- **FSM compartidas**: el tranvía (el vagón `Tram Control` y el de las estaciones `Tram Main`), el ascensor del Palacio
  Blanco y las plataformas del Coliseo y de la Radiancia tienen controlador. Las copias no tocan al caballero local ni
  abren diálogos.
- **Marioneta muriendo**: lo que dice quien mueve al enemigo ("ya muerto allí", con sus avisos) vale lo último que
  llega: un `ZERO HP` de cambio de fase se quita allí a los 0,5 s y antes se quedaba aquí el resto del combate.
- **Cuerpo del caído**: mira hacia donde miraba al caer en todos los PCs (va en el aviso de caída y en el estado del
  grupo) y al volver a la sala ya está tumbado.
- **Datos del personaje**: `maxMP`, estatuas y puertas de los Panteones, `blockerHits` y el robo del banquero son de cada
  personaje (`SharedUntil026`: los guardados antes toman el valor del mundo). Cada personaje guardado deja un `.bak`.
- **Cargas** (`CoopSaves.CanStart`): falso los 5 s siguientes a empezar una carga, para que el mundo del host no empiece
  otra encima mientras el juego sigue en el menú.
- **Red**: `PacketReader.ReadName` reutiliza las cadenas de nombres de estado, evento y prefab que ya llegaron. El
  servidor no envía nada a un saludo a medias hasta recibir de él un paquete auténtico (`NetServer.CanSend`): un `Hello`
  no va autenticado y su dirección se puede falsificar, y antes cada uno hacía que el host enviara 60 paquetes por
  segundo durante 10 s a esa dirección.
- **HUD del compañero**: el texto de estado ("en <zona>") se vuelve a escribir al cambiar el idioma del juego.
- **Efectos del compañero que faltaban** (J8): `HeroEffectTemplates` copia también `Brumm_shield` (escudo de Carefree
  Melody), `Hero Death` (sin su FSM, que hace la sombra y vacía el alma) y `Charm Effects/Blocker Shield` (su FSM
  `Control` se reproduce como las de acciones: el caparazón de Baldur Shell al concentrarse, su golpe, su rotura y sus
  trozos). Sus variables se rellenan si la FSM aún no pasó por `Init`. Avisos nuevos de `PlayerEvent`: `CarefreeShield`
  y `Death` (se envían al ver encenderse `carefreeShield` o `heroDeathPrefab`: el juego los enciende sin más) y
  `HazardDeath` (pinchos o ácido: salen del pool y van como los demás objetos del caballero; `HazardDeathEffect` hace lo
  que su FSM sin su lógica). El dash sombra enciende la copia de `Shadow Recharge` (su FSM espera la recarga). El aviso
  de golpe lleva el bit 4 si no hubo retroceso (peligro o golpe mortal): sin él, el juego no saca las partículas del golpe.
- **El compañero en salas oscuras** (`PartnerDarkness`): la oscuridad es la `Vignette` del caballero local, un sprite
  negro con un agujero a su alrededor encima de todo (los recortes de la cámara de oscuridad solo sirven para la franja
  de las pantallas ultrapanorámicas). Con `darknessLevel` ≥ 1 y algún compañero visible en la sala, la `Vignette` y
  los rectángulos negros de su borde se dejan de dibujar (`forceRenderingOff`) y en su lugar va una malla del tamaño de
  la vista, en su capa y su orden, con color por vértice: en cada uno, la oscuridad menor entre la `Vignette` centrada
  en el caballero local y la misma centrada en cada compañero. Su textura no se puede leer desde el código: se copia una
  vez pasándola por una `RenderTexture`. El avatar lleva una copia de `heroLight` debajo de la oscuridad, como el
  caballero. Dibujar al compañero y su halo encima de la oscuridad se descartó: parecía una imagen pegada sin el túnel.
- **Desinstalar**: la regla de "Steam cambió este archivo, se deja" es solo para los del juego; un mod puesto a mano
  en `Mods` antes de instalar se devuelve. La caché de skins se borra siempre.
- **Panteones**: dentro de uno (`BossSequenceController.IsInSequence`) el invitado no envía su personaje, ni al
  guardar el mod ni con el guardado del propio juego (`OnGuestSaving`): los amuletos atados dejan `equippedCharms` vacío.
- **Encuentros con diálogo** (`FsmStateSync`): si quien controla el encuentro cae a mitad del diálogo, su diálogo cuenta
  como leído. Antes el encuentro lo esperaba para siempre y el compañero se quedaba sin control ante un cuadro vacío.
- **Personajes que el juego quita al cargar la sala** (`QuestNpcSync.RemovedAtLoad`): si la FSM de un personaje lo
  destruye (`DestroySelf`, `DestroyObject`) en los 3 s siguientes a cargar la sala, o al volver a comprobarlo por un
  cambio de otro PC, y su comprobación lee datos del mundo, se apaga en vez de destruirse. Si uno de esos datos cambia
  en otro PC, se vuelve a encender y su FSM empieza de nuevo: Mr. Mushroom aparece en el otro PC sin volver a entrar
  (antes, destruido, no había nada que volver a comprobar) y se va y vuelve las veces que haga falta.
- **Invocadores creados de antemano** (`EnemySync`): el Rey Vengamosca del Salón de los Dioses crea sus 30
  invocadores apagados al empezar (`PreSpawnGameObjects`) y los enciende en "GG Summon"
  (`GetNextPreSpawnedGameObject`): eso se avisa como el `CreateObject` del mismo prefab de su FSM, que el otro PC ya
  sabe crear. Lo que guarda apagado un invocador (`EnemySpawner.spawnedEnemy`, creado en su `Awake`) no se apunta como
  enemigo de la sala: se sigue al soltarlo. Antes las mosquitas del Salón solo salían en el PC que movía al jefe, y al
  arreglarlo salían repetidas (apuntadas dos veces y encendidas también en el otro PC).
- **Órdenes de prueba nuevas**: `pdadd` (suma como el juego), `towall` (deslizarse por una pared), `fsmstates` (estado
  de todas las FSM de un nombre, sin límite), `givegeo`, `selected`, `uiselect` y `uicancel` (menús sin mando real),
  `transitionState` en `hero`, `pdget` con listas y estructuras (en JSON), `pdjson` (escribir un dato en JSON),
  `bossseq si|no` (simular un Panteón), `hitenemytype` (golpe de un tipo concreto), `langset` (cambiar el idioma como
  desde el menú), `toggles` (objetos que el juego enciende o apaga según un dato), `moveenemy` (llevar un enemigo a un
  punto), `moveobj` (llevar un objeto a un punto) y `enemytrace <segundos> <nombre>` (posición, si se ve y estado
  de sus FSM cada 0,1 s con la hora del reloj, para comparar los dos PCs momento a momento). `sdlist` marca lo semipersistente.

### Cambios para 3 y 4 jugadores (0.28.0)

Salieron de una revisión del código pensando en 3 o 4 jugadores (casi todo se había probado con 2) y se probaron con 4
copias a la vez (`setup_group` en `tools/routes/lib.sh`: el host y hasta tres invitados en la misma partida).

- **Traspaso de enemigo viejo** (`EnemySync.HandoverUsable`): el traspaso va a toda la sala y un PC que solo miraba lo
  guardaba; si mucho después pasaba a mover el enemigo (el dueño nunca recibe sus propios cambios, así que no se le
  borraba), lo tomaba con aquel estado y el jefe volvía a un ataque o una fase anterior. Ahora apunta quién lo envió
  y cuándo: solo vale si tiene menos de 5 s y lo envió quien lo movía antes según este PC (o alguien que ya salió de la
  sala). Un traspaso nuevo (su primer mensaje u otro remitente) empieza de cero en vez de mezclarse con el anterior.
- **Reclamaciones de objetos** (`FsmStateSync.OnClaim`): la reclamación lleva quién creía el que la hace que
  controlaba el objeto. Si ya conocía la anterior, va después y gana ella en todos los PCs; si no, se cruzaron y gana
  el número menor, también en un tercero que solo las recibe (apunta la hora al aceptar la de otro). Antes el desempate
  solo valía en los dos que reclamaban: un tercero se quedaba con la última que le llegaba, y quien reclamaba justo
  después de recibir otra se quedaba con el control solo en su PC.
- **Presentaciones de jefe** (`BossPresentation`): un conjunto de los PCs que tienen quieto al caballero, no uno solo;
  se suelta al quedar vacío.
- **Personajes de misión** (`QuestNpcSync`, `SharedProgress`): el cambio del mundo lleva quién lo hizo (`Origin`, lo
  pone el host al reenviarlo). El personaje se queda sin volver a comprobarse solo si quien lo cambió está en la sala;
  antes bastaba con que hubiera cualquier otro.
- **Caído que sigue a otro** (`Revive.FindStanding`, `RetargetUnfollowable`): prefiere a quien está en una sala que se
  puede seguir (no un sueño, una cinemática, Godhome ni el ciervo, salvo la sala del cuerpo) y, entre esos, en la sala
  del cuerpo. El host mira cada segundo si el seguido dejó de poder seguirse y pasa el caído a otro en pie si lo hay.
- **Banco para 4** (`PlayerSync.BenchSpread`): se mira la pared a cada lado del banco; si no caben con la separación
  normal (1,1) se juntan (hasta 0,4) y si el banco está en un rincón se corren hacia el lado libre.
- **Dos reviviendo a la vez** (`Revive.OnProgress`, mensaje 111 `ReviveDenied`): "X te está reviviendo" se queda con el
  que ya se mostraba mientras siga (antes parpadeaba entre los dos). Si los dos terminan casi a la vez, el host rechaza
  la segunda petición y se lo dice a ese jugador, que recupera el alma.
- **Mapa** (`PartnerMap`): cada compañero con su color por número de jugador (azul, verde, amarillo, morado; rojo si
  está caído) y su nombre encima, con una copia de un texto del propio mapa (un `TextMesh` no se veía).
- **Dificultad** (`Difficulty.Multiplier`): la vida extra elegida es la de un compañero; cada compañero más en pie en la
  sala suma lo mismo otra vez (1,5× con dos jugadores es 2× con tres y 2,5× con cuatro).
- **Aturdimiento** (`Difficulty.SkipStunHit`): los jefes con FSM "Stun Control"/"Stun" (Hornet 1 y 2, Vasija Rota, Hermano
  Perdido, Maestro y Tirano de Almas, Defensor del Estiércol, Coleccionista, Caballero de la Colmena, Grimm, Príncipe Gris
  Zote, Sly, Sheo, Hollow Knight y el del Panteón) cuentan cada golpe que no mata (`HealthManager.TakeDamage` manda
  `STUN DAMAGE`): total ("Stun Hit Max") y seguidos en 2 s ("Stun Combo"). Con N jugadores en la sala solo cuenta uno de
  cada N: durante el `TakeDamage` de los que no cuentan se quita `stunControlFSM` del `HealthManager` (y se devuelve), así
  el daño se hace igual. Se aturden más o menos tan a menudo como jugando solo; no se tocan sus variables (Golpe Pesado
  las baja al empezar, y el jefe cambia de PC). Elegido por jugadores y no por la vida extra: con la vida en 1× el
  problema volvía entero.
- **"Ir con"** (`PartnerTravel`): cuatro sitios junto al compañero (a un paso y a dos de cada lado) y cada número de
  jugador empieza por uno distinto; se salta un sitio en el que ya hay otro jugador dibujado.
- **Números reutilizados**: el servidor da el número libre más bajo. Al irse un jugador, lo que controlaba en
  `FsmStateSync`, los enemigos que movía (y su traspaso guardado) y su bloqueo de presentación se sueltan en todos los
  PCs; antes uno nuevo con su número los heredaba.
- **Red** (`NetConnection`): el host reenvía a cada invitado lo de todos los demás. El tope de mensajes no fiables por
  conexión y tick pasa de 256 mensajes y 8 KB a 768 y 24 KB, y lo que aun así no cabe queda en el registro ("No cabe en
  el tick un mensaje no fiable").

### Cambios de la historia completa con 4 jugadores (0.29.0)

Salieron de jugar la historia entera con 4 copias (jefes, sueños, soñadores, los dos finales del Hollow Knight, la
Radiancia, la compañía de Grimm y lo opcional), con el paso `fight` de las rutas (golpes que respetan la
invulnerabilidad y comparación de la sala en todos los PCs al acabar).

- **Habilidades del grupo** (`World/PartyAbilities`, mensaje 112): cada jugador avisa de sus habilidades. Mientras un
  `SceneAdditiveLoadConditional` decide si carga, y en las FSM que preparan la sala según una habilidad (pared del
  Falso Caballero, puerta del Abismo, puente del Pico de Cristal, alud), cuentan las de todo el grupo; luego vuelven
  las propias. El Señor Traidor exige la Sombra: antes solo existía en el PC de quien la tenía.
- **Partes de sala cargadas tarde** (`Scene/SceneLoads`): cuenta las partes que el juego carga aparte; enemigos,
  objetos, rompibles, peligros, FSM compartidas y sueños vuelven a repasar 0,3 s después de cada una.
- **FSM sin arrancar** (`EnemySync.PrepareUnstarted`): un PC que fue marioneta desde que entró nunca pasó por el estado
  inicial de las FSM del enemigo, que es donde buscan sus piezas. Al tomarlo se hacen solo esas búsquedas (`FindChild`,
  `GetOwner`...). El Hollow Knight se quedaba quieto en la escena de Hornet: su hilo era una variable vacía.
- **Avisos y aguijonazos a la marioneta** (`ForwardBroadcast`, `ForwardNailTriggers`): se reenvían al PC que mueve el
  enemigo si su FSM los atiende en algún estado, no según el estado de la marioneta (el del último traspaso, o
  ninguno). La Radiancia esperaba en "Abyss Up" y su golpe final solo valía con el jugador de ese PC.
- **Una acción que falla en la copia** no corta su muerte: queda como aviso en el registro (Xero seguía vivo).
- **Cadáveres con lógica propia**: de la copia solo se sigue la posición (el del Maestro de Almas se quedaba parado).
- **Jefe en la parte de sala del jefe**: lo de 150 de vida o más en una escena `..._boss` cuenta como jefe (barra y
  dificultad de la primera fase del Maestro de Almas).
- **Enemigos de jaula**: tienen que colgar de su jaula (Flukemarm no se sincronizaba).
- **FSM compartidas nuevas** (`FsmStateSync`): el orbe falso y el caballero falso del Maestro de Almas; como
  encuentros, el fantasma de los guerreros soñadores, la puerta del Huevo Negro y el cuerpo del Hollow Knight. Los dos
  últimos solo los empieza su zona o el caballero local (`ZoneOnlyEncounters`): el cuerpo mira en cada frame a qué
  distancia está el caballero. Lo que un encuentro recibe del propio caballero (el foco que absorbe el cuerpo) lo toma
  ese PC.
- **Sueños** (`DreamSync`): la salida de un compañero no pisa la propia si el jefe ya murió aquí (se espera la del juego
  8 s). Una entrada sin estado (apagada) no tiene reposo: la del Hollow Knight se disparaba sola al encenderse. Las
  entradas que cuelgan de un enemigo no se paran en las marionetas: cada jugador usa su aguijón onírico.
- **Panteones** (`PantheonTogether`, mensajes 113 y 114): ver "Entrar juntos en un Panteón".
- **Cloth** (`FsmStateSync`, "Cloth Fighter"): FSM compartida; lo que golpea la copia de un objeto compartido que mueve
  otro PC no cuenta en este, ni en un enemigo real ni en una marioneta (`IsRemoteCopy`).
- **Reposo y muñecos**: un enemigo sin despertar o en un estado de dormido (`dormant`, `sleep`) cuenta como en reposo
  para cambiar de PC (Grey Prince Zote cambiaba cada 4 s); uno con 9000 de vida o más no es jefe (el maniquí de mantis
  del combate del Señor Traidor, 999999).

### Cambios de cada jefe con 4 jugadores (0.30.0)

Salieron de repetir cada combate de jefe con 4 copias, uno a uno, golpeando desde un PC que no lo mueve, y de jugar el
Panteón de Hallownest hasta su final.

- **Final del Panteón de Hallownest por PC** (`EnemySync.StartFinale`): al llegar la Radiancia Absoluta a "Final
  Impact", cada PC deja de seguirla y su lógica sigue sola desde ahí (`FinaleRunning`). Si el caballero de ese PC está
  muy por debajo (en el abismo), se sube a la última plataforma (`BringHeroToFinale`). Mientras dura, los compañeros
  no se dibujan (`PlayerSync`), los nombres se esconden con el HUD del juego, las cinemáticas compartidas no se llevan
  a quien sigue en su final (`SharedCutscenes.OwnEndingScene`) y el fin de sala del Panteón no lo acaba
  (`PantheonTogether`). El botón de atacar de cada jugador llega al jefe que lo espera (`UpdateAttackListens`).
- **Efectos de la marioneta**: cuántas acciones del estado hizo el PC que lo mueve (`ranTo`, si salió del estado en
  el mismo frame); los eventos con retraso se envían desde `UpdateDelayedSends` (la FSM de la marioneta está parada y
  no atiende su cola: el cartel del Recipiente Puro); `SetPosition` de lo que solo se ve, `iTweenMoveTo`/`MoveBy` de
  objetos de la sala (el abismo), `SendEventToRegister` con `BLAST` (las burbujas, con `DropSeeds` en sus dados),
  `SetStaticVariable` de `ggCinematicEnding` y `SetHazardRespawn` al caballero. Los objetos de la sala van por su ruta
  (tabla en caché) y caben 128 por efecto; se siguen hasta 64 piezas por enemigo.
- **Traspaso en el mismo estado** (`RestartAnimationWaits`): si la FSM del nuevo PC ya estaba parada en ese estado, lo
  que espera el final de una animación se vuelve a lanzar (la Radiancia esperaba "Tele Out" para siempre).
- **Enemigo huérfano** (mensaje 115): uno que el dueño de la sala no tiene (una parte de sala no cargada en su PC) lo
  reparte el de número más bajo de los que lo tienen, con el mismo criterio de cercanía (`PickController`).
- **FSM de plantilla** (`FsmStateSync.Rekey`): estrenan lógica al encenderse su objeto; se vuelven a apuntar. Las
  zonas `raise_abyss_pit` de la subida se copian como palancas y la reaparición sigue al marcador de la zona, solo
  hacia arriba (`FollowAbyssRaise`).
- **Personajes por jugador** (`CharacterData.DialogueFields` y `NpcPresenceFields`): por dónde va cada conversación y
  si el personaje sigue en su sitio para ese jugador (incluidas las cadenas de `TimePasses`, como `zoteSpokenCity` →
  `zoteLeftCity`). Los de presencia están en `WasShared`.
- **Entrar en un Panteón con otro** (`PantheonTogether.CoverWithWhite`): como entre jefes, telón blanco al momento y
  viaje sin fundido de cámara (con fundido, `CameraFade` se quedaba en `FadingOut` y la pantalla negra).
- **Caído en Godhome** (`Revive.ReviveLeftInGodhome`): el host lo revive junto al compañero cuando el grupo pasa a otra
  sala de jefe, esperando a que su PC llegue a la misma sala.
- **Barras de jefe** (`BossBar`): solo el `HealthManager` principal y solo si ya pelea (se movió o recibió daño). El Rey
  Vengamosca y la Madre Gruz son jefes (`Difficulty.SmallBosses`).
- **Menderbug** (`MenderBugSync`, mensaje 116): si sale en un PC de la sala, sale en todos.
- **Desafío de los Señores Mantis**: un desafío que llega tarde a quien ya desafía se descarta.

### Órdenes de un controlador durante un traspaso y vida 4× (0.32.0)

De un reporte de una partida real por internet: "a veces un Señor Mantis se queda quieto" y "las bolas del Maestro de
Almas a veces no desaparecen y salen duplicadas"; también pidieron vida 4× para los jefes.

- **Órdenes de un controlador de batalla mientras se toma un enemigo** (`EnemySync.OnControllerOrder`): los Señores
  Mantis de la segunda fase esperan en `Sub Idle`, sin límite de tiempo, a que `Battle Sub` les diga qué hacer, y ese
  es un estado de reposo: ahí es donde cambian de PC. Una orden que caía entre que el PC de antes lo paraba y el nuevo
  lo tomaba (lo que tarda en llegar el traspaso: un ida y vuelta por internet) no la atendía nadie, y el señor se
  quedaba escondido sin hacer ese ataque hasta su siguiente orden (`Battle Sub` no lo espera). Ahora el PC que ya
  lo mueve según el dueño, pero que aún espera el traspaso, la guarda y la atiende al tomarlo (`KeepPendingEvent`,
  como los avisos reenviados). Se mira en `Fsm.BroadcastEventToGameObject`, no en `ProcessEvent`: PlayMaker solo
  entrega un aviso a un objeto a sus FSM encendidas (`PlayMakerFSM.FsmList`) y las de una marioneta están apagadas; y
  ahí pasan también los avisos con retraso de `Battle Sub` (0,5 a 1,5 s), que no vuelven a pasar por `SendEventByName`.
  Probado en D/E con 120 ± 30 ms y 2 % de pérdida, forzando traspasos (un jugador debajo del centro y el otro muy
  arriba, cambiando cada ~6 s) durante 300 s de segunda fase: 336 órdenes a marionetas, una (`HIGH THROW` a S1) llegó
  0,28 s después del cambio al PC que lo iba a tomar y se atendió al tomarlo. Ningún señor se quedó quieto. En el modo
  de prueba se registra cada orden a una marioneta ("orden ... a la marioneta ...").
- **Bolas del Maestro de Almas**: no se reprodujo. Con el mismo lag, 200 s de primera fase y 200 s de segunda, con un
  cambio de PC cada pocos segundos y fotos de las bolas activas tomadas a la vez en los dos PCs: las cuentas coinciden
  y las diferencias duran una foto (una bola que acaba de chocar en un PC y cuyo aviso aún viene por la red). El giro de
  4 bolas (`Orb Spinner`) también se probó cambiando de PC a mitad del ataque. Ojo al probarlo: la lógica de la segunda
  fase (`Mage Lord 2`, estado `Init`) arranca sola en cuanto un caballero baja de y = 11 (el piso de abajo); un `tp` ahí
  saca a las dos fases a la vez.
- **Vida de los jefes 4×**: nuevo valor al final de la opción (los índices de los demás no cambian; `settings.json`
  guarda el valor, no el índice).

### Tajos propios con la skin (0.32.1)

Issue #2: "el compañero ve los tajos de mi skin y yo no". Los tajos del aguijón (`Knight/Attacks`: `Slash`, `AltSlash`,
`UpSlash`, `DownSlash`, `WallSlash`) y las artes del aguijón (`Great Slash`, `Dash Slash`, `Cyclone Slash`) son
sprites de la colección `Knight` (`spriteof Great Slash` → `Knight/charge_slash_effect0002`), así que su dibujo está en
`Knight.png`. Esa colección no va en material compartido (es la misma de los avatares), y la skin propia se ponía con
un bloque solo en el renderer del cuerpo: en el PC del dueño los tajos salían con el dibujo del juego, mientras que en
el del compañero `ApplyToEffects` sí se la ponía a todas las copias bajo el avatar.

- `Skins.ApplyToHeroEffects`: cada frame, los sprites hijos del caballero (lista guardada por caballero y sala) cuya
  colección es de `CollectionFiles` y no de `SharedCollectionFiles` (`Knight`, `Sprint`, `Unn`) llevan la skin propia
  con un bloque, como el cuerpo; solo los encendidos. A las colecciones compartidas no se les pone bloque: taparía la
  skin del material. Al quitarse la skin, o si un sprite pasa a otra colección, su bloque se borra.
- Probado en B con Rick Knight en `Town` (ráfaga de capturas con `slashes 6 0.35`): el tajo sale verde, como en la
  skin; con `skin normal` vuelve el blanco del juego.

## Código del juego

Para ver cómo hace algo el juego antes de tocarlo. Todo queda en `decomp/` (en `.gitignore`): es código de Team Cherry
y nunca se sube.

- **C#:** `bash tools/decompile.sh` (después de `setup-dev.sh`; necesita `ilspycmd`) descompila con ILSpy las DLL de
  `lib/`, contra las que compila el mod: `Assembly-CSharp` (con la Modding API, así que trae `Modding/ModHooks.cs`),
  `PlayMaker` y `Assembly-CSharp-firstpass`. Sale un archivo `.cs` por clase, con los nombres originales
  (`HeroController`, `GameManager`, `HealthManager`...), y tarda unos segundos. Se busca y se lee solo lo que toca
  cada fallo; son unas 3000 clases.
- **FSM de PlayMaker:** buena parte de la lógica (jefes, NPC, ascensores, puertas, eventos) no está en el C#. En el
  descompilado salen las piezas, que son las acciones de PlayMaker (esperar, saltar hacia el caballero, lanzar del
  pool, comparar una variable...), pero no cómo las arma cada objeto: sus estados, transiciones, probabilidades,
  tiempos y valores. Eso son datos guardados en las escenas (`level*`). Dos formas de verlas:
  - **FSMViewAvalonia** (herramienta de la comunidad): abre las escenas del juego y dibuja cada FSM como un diagrama,
    sin abrir el juego. Es la cómoda para mirarlas a ojo.
  - **Volcarlas a texto desde el mod**: `bash tools/dump-fsms.sh` recorre todas las salas del juego (481, sin menús ni
    vídeos) con sus propias copias (`test-instances/l`, `m`, `n`, `o`; 3 a la vez, unos 20-30 minutos) y escribe
    `decomp/fsm/<sala>.txt` con la orden `fsmdumpall` del modo de prueba (`Testing/FsmDumper`): las FSM de la sala y de
    las que se cargan con ella (`_boss`), también las apagadas, tal como quedan en el juego después de lo que cambian el
    propio juego y la Modding API; por cada una, los componentes de su objeto (con la vida si es enemigo), sus variables
    con su valor, eventos, globales y cada estado con sus transiciones y acciones (campo=valor, `{variable}`). Una FSM
    idéntica a otra (rompibles, pasto) solo dice de cuál es copia. Al final, las FSM de los prefabs que crean sus
    acciones (disparos, crías), y al principio los componentes de C# de la sala. `decomp/fsm/_persistent.txt` tiene lo
    que no es de ninguna sala (el caballero, la cámara, el HUD). Cada copia empieza una partida nueva, se hace
    invulnerable (`god`) y entra en cada sala con `warp`; si una sala falla, la copia se reinicia y sigue. Lo ya
    volcado se salta (`--retry` repite las fallidas, `--only "Sala"` solo esas).
  - **Cruzarlas con el mod**: `python tools/analyze-fsms.py` escribe en `decomp/fsm-analysis/` los jefes con lo que
    crean, a quién avisan y qué llaman; cada acción que usan los enemigos y si el mod la nombra; lo que crean los
    enemigos y si `EnemySync` conoce esa acción; en qué salas sale cada componente de C# de la revisión; objetos sólidos
    que mueve una FSM; métodos que llaman las FSM; y quién atiende `ZERO HP`.

## Pruebas

- `tests/NetTests`: aplicación de consola que prueba el transporte fuera del juego (conexión, código incorrecto,
  pérdida y desorden simulados, canal fiable, datos grandes, memoria creada por paquete).
- En el juego: dos copias (`test-instances/b` y `c`) controladas por archivo con el modo de prueba del mod
  (`tools/mp-test-lib.sh`): conexión, movimiento, memoria por segundo, tirones, suavidad, sincronización. Capturas de
  pantalla de las dos a la vez (`shot`) para comparar lo que se ve; cada daño que recibe el caballero se registra con
  lo que lo hizo. La memoria creada en combate (≈300 KB/s) es la del juego: es la misma con la partida cerrada.
  `netsim <ms> <variación ms> <pérdida %>` simula una mala conexión en la próxima partida (`LaggyTransport`, solo en
  modo de prueba); probado con 80 ± 20 ms y 5 % de pérdida. `arenas` lista los objetos de la sala que dan órdenes a
  enemigos o los encienden (para ver qué combates necesitan un controlador compartido), `healthlist` todos los objetos
  con vida y su huella, y `die` mata al caballero; cada muerte de enemigo se registra con quién la provocó. `moving <s>` lista lo sólido o
  dañino que se mueve o gira, `hazards` los peligros animados alineados y `wake <nombre>` enciende un enemigo apagado. `hero` dice por qué el
  caballero no responde (si acepta órdenes, si cedió el control, reaparición tras pinchos, pausa) y `regain` le
  devuelve el control. `tp x y` no fija el punto de reaparición (`tp x y respawn` sí). `enemies` marca también los
  enemigos ocultos (sin dibujarse) y sin caja de colisión. Si algo apaga un enemigo vivo, el registro dice
  "apagado vivo" con la FSM que lo hizo y la pila de llamadas. `slashes <n> <s> [up|down]` da una ráfaga de
  aguijonazos seguidos (varios `press attack` en la misma lectura de órdenes serían uno solo).
- Personajes de misión: `questnpcs` (datos del mundo que miran al empezar las FSM de la sala y los objetos que se
  encienden o apagan según ellos), `fsmuses <texto>` (FSM de la sala, también apagadas, con una acción que nombra ese
  texto, p. ej. `mrMushroomState`) y `pressat <hora en ms UTC> <botón>` (pulsar a una hora del reloj: dos copias a la
  vez o una medio segundo después de la otra).
- Ajustes del host e "Ir con ...": `goto <nombre>|#<id>` (como pulsar "Ir con ..."; con `#id` aunque ya no esté),
  `travel` (lo pedido, el viaje y el último suelo pisado), `warp <sala> <x> <y>` (viaje sin puerta, p. ej. a un sueño
  o a una cinemática), `scenetrace <s>` (sala, cambio de sala y posición cada 0,2 s), `enemybars`, `invulwatch <s>`
  (cuándo empieza y acaba la invulnerabilidad), `hurt [daño] [tipo]` (tipo 2 = pinchos, 3 = ácido) y `status` dice el
  dueño de la sala.
- Funciones del coop: `settings` (ajustes que rigen), `party` (revivir y estado de cada jugador), `partnermap` (lo
  enviado al mapa y, de cada compañero, su sala, su punto y si su icono se ve), `vitals`, `shades`,
  `killshade` (mata la copia de la sombra de un compañero), `killobj <objeto>` (muere por `HealthManager.Die`, p. ej. la
  sombra propia), `tk2dwatch <s> <objeto>` (cada cambio de animación, cuadro y visibilidad de sus sprites tk2d), `bossbar`, `focus <s>` (Focus simulado), `tpbody`, `heal`
  (vida llena por la vía del juego: `pdset maxHealth` no redibuja el HUD del juego y el mini-HUD lo mostraría distinto),
  `soul <n>`, `mppage <partida|ajustes|ajustes2|compañeros>`, `mpoption <etiqueta>|<pasos>`, `mpoptions`,
  `spriteof <objeto>`, `sprites <colección>|<texto>`, `collections <texto>`, `hudsprite`, `hudrot` y `clips <texto>`.
  `nailtouch <objeto>` hace como si el aguijón de este jugador tocara esa copia sin vida propia (devolver el escupitajo
  del Gulka). Los golpes al caballero dicen si quien pega se veía, y los pasos de cadenas de otro PC se registran.
  `spritewatch <s> <texto>` registra cada cambio del sprite dibujado (al final del frame) de los `SpriteRenderer` cuyo
  objeto, sprite o textura contiene el texto (p. ej. `soul_orb` para el orbe del HUD). `enemies` da también el giro y
  la velocidad de cada enemigo, y `hazards` los Goam con su estado y cuánto llevan en su espera.
  Segunda parte de M5: `partystate`, `cutscenes`, `cinematic` (vídeos de la escena, dónde se dibujan y el fundido),
  `presentation`, `dreams`, `dreamhit [encender]`, `transitions` (cambios de sala de las FSM de la sala), `senders
  <evento>`, `covers` (lo que tapa la cámara), `langget <hoja> <clave>`, `charmupdate`, `skins`, `skin <nombre>|normal`
  y `skinshow si|no`; `findobj` muestra la caja del colisionador y `sdset` acepta ids con espacios. `listeners <evento>`
  lista las FSM que cambian de estado con un aviso y `bosses` dice la arena de cada jefe y si espera para despertar.
  `fsmdump` muestra también las variables de objeto y cada efecto que reproduce una marioneta se registra
  ("efecto de enemigo ...").
- Efectos del otro jugador: `burst <nombre> <n> <segundos>` hace capturas seguidas (las órdenes se leen cada medio
  segundo, así que para ver un efecto en el otro PC su ráfaga empieza antes), `hurt` da un golpe de 1 al caballero,
  `effects` cuenta los enviados, recibidos y reproducidos, y cada efecto reproducido se registra
  ("efecto Slash de ...", con la FSM y el estado o el objeto). `tree <profundidad> <objeto>` y `fsmall <objeto>`
  muestran la jerarquía y todas las FSM de un objeto (también inactivo o del pool, como `Fireball Top`); `ptree` es
  `tree` buscando también entre lo cargado, `fsmvars` da los valores de las variables de sus FSM, `heroprefabs` los
  objetos a los que apunta el caballero y `spawnlog <segundos>` registra lo que sale del pool (y quién apaga cada pieza
  de geo, con su pila de llamadas). `findobj` enseña como mucho 15 objetos y cuenta también los del pool: para el geo
  en el suelo, `findactive`. `bossscene` dice si el juego cree estar en una sala de jefe (el geo se recicla al salir). `effects` dice también
  qué copias de amuletos y cuántos acompañantes del compañero hay encendidos. Ruta: `tests/routes/effects.route`.
- FSM del juego: `fsmdumpall [carpeta]` vuelca las de las salas cargadas (ver [Código del juego](#código-del-juego)) y
  `scenelist` da todas las salas del juego.
- Paquete para jugadores: `tools/manual-tests/installer-test.sh <zip>` (ver [Paquete e instalador](#paquete-e-instalador)).

### Cómo probar (recordatorio)

- Copias del juego en `test-instances/` (enlaces duros al juego de Steam; `ensure_instance` las crea): B/C para
  pruebas sueltas (puerto 26950), D/E... para las rutas automáticas (27100+), F/G para el Coliseo y los jefes (27300),
  H/I para la prueba del instalador (`installer-test.sh` las rehace desde cero), L/M/N/O para volcar las FSM
  (`dump-fsms.sh`, sin sesión). Instalar solo en las que se usan (`bash tools/test-cycle.sh --only "f g"`) y cerrar solo
  esas (`stop_all F G`). `ensure_instance` copia los ajustes del juego de la plantilla con `MSYS_NO_PATHCONV=1`: sin
  eso Git Bash convertía `/s /f` de `reg copy` en rutas, la copia no tenía ajustes y se quedaba en la primera pantalla
  del juego (idioma y brillo) sin cargar el menú.
- Órdenes útiles: `hero` (por qué un caballero no responde) y `regain`; `enemies` marca "oculto", "sin-caja" e
  "(inactivo)"; `bosses` da la vida, si es marioneta y el estado de cada FSM del jefe; `tp x y` no fija el punto de
  reaparición (`tp x y respawn` sí); `fsmgo` salta a un estado de un objeto con controlador; `fsmstate
  <objeto>|<estado>|<fsm>` pone el estado de una FSM concreta (sin la tercera parte, la primera del objeto: en Lurien es
  `Bob`, la que lo hace flotar); `healthlist` dice si cada enemigo cuenta como jefe.
- Los juegos se abren con `Start-Process` (`_start_game`). Lanzados desde bash heredaban los handles abiertos, también
  el extremo de una tubería, y `setup_pair | tail` no acababa hasta cerrar los juegos (parecía que la prueba se había
  trabado).
- No usar `hitall` ni abusar de `hitenemy` para comprobar combates: quitan vida directamente, matan enemigos dormidos o
  invulnerables, falsean el contador de las oleadas y pueden saltarse la lógica del jefe (la cabeza del Falso
  Caballero). Mejor golpes de aguijón reales (`tools/manual-tests/colo-nail.sh`).
- Los combates de jefe empiezan al entrar en su zona (`Battle Scene`): un `tp` a un sitio fuera de ella no los
  despierta, y un `tp` junto al jefe puede dejar al caballero al otro lado de una pared.
- Poner tiempo máximo a todas las esperas de los guiones de prueba (si no, se quedan colgados esperando a un juego
  cerrado) y no lanzar dos guiones a la vez sobre las mismas copias.
- Pruebas a mano del Coliseo en `tools/manual-tests/` (`fg-lib.sh`, `colo-natural.sh`, `colo-nail.sh` y
  `oro-manual.sh`, Oro con los dos dentro de la arena).

## Decisiones de M5

Decisiones aprobadas por el usuario para M5 (las marcadas "elegido" las escogió él; el resto, las recomendadas).

- **Base**: funciones en `Game/Features/` con `Attach`/`Detach`; `PartyState` con el estado de cada jugador (un caído
  no cuenta para repartir enemigos ni para las esperas de combate); interfaz uGUI, nunca IMGUI.
- **Revivir (elegido: Focus 2 s, 33 de alma)**: el caído queda tendido con "† Nombre" y su cámara sigue al compañero;
  junto al cuerpo, mantener Focus a 2 unidades o menos; si golpean al que revive se pierde el progreso. Vuelve con 1
  máscara y 2 s de invulnerabilidad. Sin límite de tiempo caído; sentarse en un banco revive a los caídos a su lado con
  toda la vida; si caen todos, muerte normal para cada uno. En un sueño se cae y se revive dentro.
- **Sombras (elegido: copia en cada PC)**: cada PC mueve su copia de la sombra del compañero; matarla hace que el
  dueño recupere la suya (el host acepta solo el primer aviso). La de alguien desconectado no se ve. La copia se quita
  sin pasar por su muerte, así que al matarla se devuelve la música como hace su estado `Killed` (el audio a "Away"):
  su zona de música (`Music Control`) la había puesto en "Close" y seguía sonando la de sombra. Si la sombra propia
  sigue cerca con su música puesta, se deja.
- **HUD del compañero**: arriba a la derecha (el usuario lo prefirió allí), con su nombre, máscaras y alma con los
  sprites del juego y su estado; máscaras sobre su cabeza al cambiarle la vida; flechas en el borde; barra del jefe
  abajo con su nombre.
- **Cinemáticas**: finales, créditos y Mr. Mushroom a la vez para todos; saltar por votación ("Esperando a X para
  saltar 1/2"); presentaciones de jefe con los dos quietos; al acabar, quien fue llevado vuelve a donde estaba.
- **Ajustes del host** (Pausa › Multijugador), guardados por mundo; los invitados solo los ven. Por defecto: vida de
  jefes 1,5× (elegido) solo con el compañero en la sala, enemigos 1×, daño recibido 1×; revivir sí (2 s, 33 de alma, 1
  máscara, 2 s de invulnerabilidad, en el banco sí); nombres, vida del compañero, flechas, compañeros en el mapa, avisos
  y barra del jefe sí,
  barras de enemigos no; regalar geo sí; ir con un compañero sí; saltar cinemáticas por votación; viajar juntos en
  ciervo sí. La vida extra divide
  el daño en el PC que mueve al enemigo (las fases de los jefes dependen de su vida).
- **Ir con un compañero** (pedido después): en *Compañeros*, al quitar la pausa aparece a su lado; no con alguien
  caído, en un sueño, en una cinemática, cambiando de sala o en otro mundo.
- **Sueños**: quien está en la misma sala entra con quien golpea la entrada; al ganar salen los dos; la caída de uno no
  saca al otro. Grimmchild: si uno lo recibe, el otro también.
- **Skins (elegido: sí, al final)**: compatibles con CustomKnight, enviadas por la red del mod en segundo plano (unos
  0,5 MB/s, máximo 32 MB); cada PC elige si ve las de los demás.

## Idioma

- Los textos del mod que ve el jugador van en el idioma del juego (`UI/Lang.cs`: `Lang.T("español", "inglés")`): en
  español si el juego está en español (`TeamCherry.Localization.Language.CurrentLanguage() == LanguageCode.ES`); en
  cualquier otro, en inglés. Se mira cada vez que se pide un texto: avisos, estados, HUD y textos que se refrescan
  cambian en cuanto se cambia el idioma; las etiquetas fijas de los menús se ponen al crearlos (al abrir el juego). Los
  ajustes del host (`HostOption`) guardan los dos idiomas y eligen al mostrarse. Los nombres de jefes y zonas salen de
  los textos del propio juego. Los registros (`ModLog.txt`) siguen en español.


## Steam (0.31.0)

Jugar por Steam sin abrir puertos, además de por IP. La red del mod no cambia: el saludo, el cifrado con el código, la
fiabilidad y la sesión son los mismos; Steam solo sustituye el transporte entre cada invitado y el host.

- **Transporte** (`Net/Transport/PeerTransport.cs`, sin Unity ni Steam, en las NetTests): `IPeerNetwork` envía y recibe
  datagramas sin garantía de entrega a un par identificado por 64 bits (el SteamID). `PeerTransport` lo convierte en un
  `ITransport`: a cada par le da una dirección IPv6 local inventada (`fd48:4b4d:5053:544d::<n>`, "HKMPSTM" y un número de
  orden), así el resto de la red, que identifica a cada par por su `IPEndPoint`, no cambia. En la dirección va el número
  de orden y no el SteamID, para que el registro no lleve nada que identifique al jugador. `CompositeTransport` junta UDP
  y Steam en el host: recibe de los dos (alternando cuál mira primero) y envía por el del destino; los dos comparten la
  misma `BufferPool` (por eso `UdpTransport` acepta una), porque un búfer se devuelve al primero venga de cuál venga.
- **Steam** (`Steam/`): `ISteamPlatform` es lo que el mod usa de Steam: mensajes entre cuentas, salas, datos de la sala,
  invitar, amigos con partida abierta y el aviso de "unirse". `SteamworksPlatform` lo hace con el Steamworks.NET 2024.8
  que trae el juego (`com.rlabrecque.steamworks.net.dll`, referenciado con `Private=False`: no va en el paquete). El
  juego ya inicia Steam (`SteamOnlineSubsystem`) y llama a `SteamAPI.RunCallbacks` en cada frame, que es lo que entrega
  los `Callback`/`CallResult` del mod. Mensajes: `SteamNetworkingMessages` en el canal 0, sin agrupar
  (`UnreliableNoNagle`) y reabriendo la sesión si se rompe (`AutoRestartBrokenSession`); se copian a un búfer nativo fijo
  para no crear nada por paquete. Al iniciar se llama a `SteamNetworkingUtils.InitRelayNetworkAccess` para que la primera
  conexión no espere a los relés, y se suben el ritmo máximo de envío de Steam (de 1 a 4 MB/s) y su búfer (de 512 KB a
  2 MB): el mundo al unirse y las skins salen a 2-2,6 MB/s, y con los valores por defecto Steam los frenaba y descartaba
  paquetes con el búfer lleno (Steam sigue bajando el ritmo solo si la conexión no da). Los datagramas del mod (1200
  bytes como mucho) quedan muy por debajo del límite de un mensaje de Steam (512 KB). `SteamLink` decide qué plataforma hay: la real si el juego inició Steam; ninguna si el juego
  no trae Steam (sin el ensamblado, `CreateReal` falla al compilarse y se atrapa: por eso va aparte y sin inlining) y
  ninguna en el modo de prueba, donde se simula.
- **Sala de Steam del host** (`MultiplayerRunner.OpenSteamLobby`): al hostear, si hay Steam y el ajuste
  `SteamAccess` no es "Nadie", se crea una sala de hasta 4, solo para amigos (`k_ELobbyTypeFriendsOnly`) o privada con
  "Solo invitados" (`k_ELobbyTypePrivate`). Guarda `hkmp=1`, `version`, `code` (el código de la partida), `host` (el
  SteamID del host) y `name`. Con "Amigos", el host pone además la presencia de Steam `connect` = `+connect_lobby <sala>`:
  sin ella, la lista de amigos de Steam solo ofrecía "Invitar a ver" (visto con Steam de verdad), y con ella salen
  "Invitar a jugar" y "Unirse a la partida" (aviso `GameRichPresenceJoinRequested_t`, o esa línea de órdenes si Steam
  abre el juego). Steam no separa los dos: con "Solo invitados" también se pone (a petición del usuario, para poder
  invitar desde la lista de amigos), y a quien pulse "Unirse" sin invitación a la sala Steam no lo deja entrar
  (`LobbyJoinResult.NotAllowed`: se le dice que pida la invitación desde el juego). La invitación del botón de la pausa
  (`ActivateGameOverlayInviteDialog`) es una invitación a la sala y vale en todos los modos; la de "Invitar a jugar" de la
  lista de amigos puede no valer en una sala privada (sin probar). El host escucha por UDP y por Steam a la vez; si el puerto no se puede abrir, la partida se
  abre igual solo por Steam (la pausa lo dice en vez de la IP).
- **Unirse** (`JoinSteamLobby`): entra en la sala, comprueba `hkmp` y la versión (si no coincide, lo dice antes de
  conectar) y se conecta a la cuenta del host con el código de la sala (`StartJoinSteam`). Se llega desde la lista
  "Amigos de Steam" de *Unirse a un mundo* (amigos que juegan a Hollow Knight con una sala, `GetFriendGamePlayed`), desde
  el aviso de Steam al aceptar una invitación o pulsar "Unirse a la partida" (`GameLobbyJoinRequested_t`; si está en
  otra partida o en el juego, se apunta y entra al volver al menú principal) y desde `+connect_lobby <id>` cuando Steam
  abre el juego para unirse. El invitado se queda en la sala para poder invitar a otros. La reconexión vuelve a la
  cuenta del host directamente (sin la sala) y vuelve a entrar en la sala aparte.
- **Seguridad**: una petición de conexión de Steam solo se acepta si viene de un amigo, de alguien de la sala o del host
  al que se une (`Expect`); a cualquier otro se le ignora antes del saludo. El código de la sala lo leen los que pueden
  entrar en ella (con "Amigos", cualquier amigo: es lo que hace funcionar "Unirse"; con "Solo invitados", quien recibe la
  invitación). La conexión va cifrada con ese código igual que por IP.
- **Interfaz**: *Multijugador* tiene la opción "Partidas por Steam" (Amigos / Solo invitados / Nadie, en los ajustes del
  jugador: solo cuenta para sus partidas como host; sale solo si hay Steam) y, en *Unirse a un mundo*, la lista de amigos
  con partida abierta. La pausa tiene "Invitar amigos de Steam" (el panel de Steam con la sala) mientras se está en una.
- **Pruebas sin Steam**: todas las copias del juego son la misma cuenta, y en ellas el juego ni siquiera inicia Steam
  ("Steam failed to initialize" en su `Player.log`). `SimulatedSteamPlatform`, solo en el modo de prueba (orden `steamsim
  <n>`), hace que cada copia sea una cuenta distinta: mensajes por UDP en `127.0.0.1:27600+n`, salas e invitaciones como
  archivos en `%TEMP%\hkmp-steamsim` (una sala privada solo deja entrar a la cuenta invitada). No sustituye nada del
  juego ni de Steam: imita solo la interfaz del mod. Órdenes: `steamjoin <n> <nombre>`, `steaminvite <n>`,
  `steamaccess <0|1|2>` y `steamfriends`. `run-routes.sh --steam` (un solo par) pasa las rutas con los invitados
  entrando por la sala de Steam del host; con 4 jugadores, el tercero entra por IP, así la misma partida usa las dos
  vías. Las NetTests prueban el transporte con una red de pares en memoria (invitados por Steam y por IP a la vez,
  código incorrecto, pérdida del 20 %).
- **Sin probar todavía**: `SteamworksPlatform` contra Steam de verdad (dos cuentas que tengan el juego).

## Paquete e instalador

- `tools/package.sh` compila en Release y arma `out/package/HKMultiplayer-<versión>.zip` con una carpeta
  `HKMultiplayer-<versión>`: `HKMultiplayer Setup.exe`, `LEEME.txt` y `README.txt` (instrucciones para jugadores en
  español y en inglés, con la instalación a mano), `LICENSE.txt` y `THIRD_PARTY_NOTICES.txt` en la raíz, lo que se
  copia al juego en `files/` (la Modding API de `vendor/modding-api/OutputFinal` sin su README, y `Mods/HKMultiplayer`
  con el mod, su `.pdb` y BouncyCastle) y `SHA256SUMS.txt`. Del juego solo van los dos ensamblados que parchea la
  Modding API (`Assembly-CSharp.dll` y `TeamCherry.Localization.dll`). Fuentes del instalador en
  `tools/installer/launcher` (C#, .NET Framework 4.7.2, que viene con Windows 10 y 11).
- **Desde la 0.30.0 todo el instalador es el .exe**, sin PowerShell. Hasta entonces el .exe solo abría
  `scripts/setup.ps1` escondido con `-ExecutionPolicy Bypass`, el patrón que más vigilan los antivirus. El .exe es
  además **el mismo archivo en todas las versiones del mod** mientras no cambie su código: no lleva la versión del mod
  (la lee del `HKMultiplayer.dll` del paquete, `Installer.PackageVersion`) y se compila de forma determinista, sin
  ruta de depuración ni el commit (`SetupLauncher.csproj`). SmartScreen guarda la reputación de un programa sin firmar
  por archivo exacto: así la va sumando de versión en versión. Su propia versión (`Version`, 1.0.0) solo sube cuando
  cambia el instalador. Lleva autor, copyright y descripción. El aviso de SmartScreen solo desaparece del todo con una
  firma digital (p. ej. SignPath Foundation, gratis para código abierto) o con reputación suficiente.
- Partes: `Program.cs` (arranque, consola), `SetupForm.cs` (la ventana), `Installer.cs` (instalar y desinstalar),
  `Common.cs` (textos `L("español", "inglés")` en el idioma de Windows, búsqueda del juego, comprobaciones),
  `Releases.cs` (versiones de GitHub) y `setup.ico` (lo dibuja `make-icon.py`: una sombra con cuernos, ojos blancos y
  la capa deshecha en humo, sin nada del juego).
- Sin argumentos (o con la carpeta del juego arrastrada encima) abre una ventana de WinForms con el aspecto de los
  menús del juego (carpeta del juego con lista y *Buscar…*, estado de la instalación, *Instalar*/*Actualizar*,
  *Desinstalar*, *Instrucciones*, el registro de lo que hace y *Novedades* abajo a la derecha). La instalación va en
  otro hilo y escribe en el registro de la ventana. Al abrirse pide a la API de GitHub la lista de versiones sin parar
  la ventana (se publican como pre-release, así que no sirve `latest`; el JSON con `JavaScriptSerializer`). Si hay una
  más nueva que el paquete, sale un cartel arriba a la derecha con un enlace para descargarla. *Novedades* enseña en
  una ventana el apartado "Novedades de la X" (o "What's new in X") de las notas de esa versión, o de la del paquete si
  no hay otra más nueva, pasado de Markdown a texto; si la lista aún no llegó, la pide esperando como mucho 10 s. Un
  botón desactivado se vuelve a dibujar con el texto en blanco: Windows lo pinta en un gris oscuro que no se leía sobre
  el fondo, y `ForeColor` no cuenta en un botón desactivado.
- Con `/install` o `/uninstall` y la ruta trabaja en la consola (lo usa `installer-test.sh`): se une a la consola
  desde la que se llamó (o abre una y espera Intro al final), escribe en UTF-8 (en el archivo si la salida va a uno) y
  devuelve 0, 1 (error) o 2 (desinstalado con algo sin devolver). En un `.bat` o con `start /wait` se espera a que
  acabe. Sin `SHA256SUMS.txt` y `files\` al lado (abierto desde dentro del `.zip`) avisa con un cuadro de mensaje.
- Carpeta del juego pasada o buscada (registro de Steam en la vista de 64 bits, `libraryfolders.vdf`, desinstalación
  de Steam, rutas típicas en los discos locales, GOG). Exige Unity 6 (`UnityPlayer.dll` 6000.x) y
  `TeamCherry.TK2D.dll`: con otra versión del juego la Modding API lo dejaría sin arrancar. Además, el
  `Assembly-CSharp.dll` del juego tiene que llevar `1.5.12620` en UTF-16 (`IsGameVersion`). Solo bloquea si hay un
  `hollow_knight.exe` abierto desde esa carpeta (otras copias no molestan) o uno cuya ruta no se puede leer (abierto
  como administrador; por eso el proceso es de 64 bits, `Prefer32Bit` falso). Antes de instalar, la ventana pregunta
  con la lista de los otros mods que va a apartar.
- Cada cambio se apunta en `<juego>\HKMultiplayer-backup\manifest.txt` antes de hacerlo: `added-dir`, `added`,
  `replaced` (el original en `HKMultiplayer-backup\files\`), `old-mod` (el mod anterior `HollowKnightCoop`, movido a
  la copia porque también abre la red) y `moved` (otros mods apartados a `Mods\Disabled`). Volver a instalar es una
  actualización: lo ya apuntado no se vuelve a copiar, así que la copia sigue siendo la del juego antes de la primera
  instalación. El formato es el mismo que el del instalador de PowerShell, así que una instalación hecha con él se
  actualiza y se desinstala con el .exe nuevo.
- La desinstalación (`Installer.Uninstall`) recorre el manifiesto al revés. De `Mods\HKMultiplayer` borra lo que puso el
  instalador y la caché `SkinCache`; lo demás (las skins y lo que haya puesto el jugador) se queda y se dice qué. Las
  carpetas añadidas se borran si quedan vacías. Un ensamblado que ya no es el de la Modding API (`IsModded`: los
  parcheados nombran `ModHooks` o `MonoMod`; hasta la 0.31.1 solo `ModHooks`, y el `TeamCherry.Localization.dll` de
  la Modding API, que no lo nombra, se quedaba al desinstalar) lo volvió a poner Steam: se deja si es distinto de la copia. Si algo no se pudo devolver, conserva la
  copia y remite a "Verificar integridad" de Steam. Sin manifiesto (mod puesto a mano) quita solo el mod.

## Hitos

| Hito | Contenido | Se da por bueno cuando |
|---|---|---|
| M1 | Transporte + sesión + jugadores (verse y moverse) | Dos jugadores conectados con código, se ven moverse fluido, memoria de red ≈ 0 |
| M2 | Mundo compartido (progreso, objetos persistentes, al unirse) | Las habilidades y el mundo coinciden entre los dos |
| M3 | Enemigos y objetos de sala | Pelear juntos en salas normales y con jefes probados |
| M4 | Efectos del otro jugador | Se ven sus ataques, hechizos y movimientos especiales |
| M5 | Funciones del coop (revivir, sombras, skins, panel...) | Igual o mejor que la 0.7.3 |
| M6 | Instalador y paquete | Tu amigo lo instala y juegan juntos |

## Comprobado en vivo

Con dos copias del juego en el mismo PC (detalle de cada prueba en el historial de git):

- **Enemigos y jefes del juego base**: todos los combates de M3 (incluidos Mantis, Coliseo de Bronce, Plata y Oro
  enteros), Grimm, el Tirano de Almas, el Hollow Knight y la Radiancia; el Falso Caballero hasta el final con golpes
  reales, con red normal y con red mala (120 ms, 30 ms de variación, 2 % de pérdida).
- **Sueños**: Campeón Fallido, Falso Caballero, Hermano Perdido, Tirano de Almas, Defensor Blanco, Príncipe Gris Zote y
  Rey de las Pesadillas: entran los dos, el jefe es el mismo, salen juntos al ganar y el dato queda en los dos.
- **Funciones del coop**: revivir (también con 3 jugadores), sombras, HUD, ajustes del host, cinemáticas con votación,
  skins, ir con un compañero, recargar sala, regalar geo; misiones de NPC (Mr. Mushroom, Quirrel, Bretta, Zote en Nido
  Profundo, Tiso, Myla, Hornet, la Compañía de Grimm); objetos de sala y rompibles; efectos y amuletos del compañero.
- **Paquete**: instalar, actualizar y desinstalar en copias limpias (`installer-test.sh`); la ventana del instalador.
- **0.22.0** (copias D y E, 10 oct 2026): compañeros en el mapa rápido (el icono en su sitio de Bocasucia, se mueve con
  el compañero y se quita al cerrar el mapa; rojo en el cuerpo de un caído); el cuerpo en el último sitio seguro al
  morir sin suelo debajo (fuera del borde de Bocasucia) y en el aire sobre los pinchos del Paso del Rey, y en el suelo
  de debajo sobre suelo normal; revivir en ese sitio seguro; reconexión automática al cerrar de golpe el juego del host
  y volver a abrir su mundo (entró en el intento 4, con su personaje). Después: el icono también con el mapa del
  inventario (al acercar; se quita al cerrar el inventario); morir en el aire sobre el ácido de `Fungus1_07` (el
  cuerpo nunca queda dentro: sobre una plataforma si la hay debajo, si no en el último sitio seguro); Esc en el menú
  principal cancela la reconexión (con la tecla de verdad) y corta el intento en curso; sin host, se rinde tras los 12
  intentos y se queda en el menú. Las plataformas de liana de `Fungus1_06`: cortada a aguijonazos en un PC, cae en los
  dos, y quien entra después la encuentra caída (como en el juego: el sprite caído en el suelo y sin colisión arriba).
- **0.23.0** (copias D y E, 11 oct 2026), tras una partida real entre dos PCs: los Tiktik y Reptantes de
  `Crossroads_01` como marioneta van a velocidad 0 y siguen lo recibido, y andan al retomarlos en los dos sentidos; los
  17 Goam de `Crossroads_13` se alinean una vez al entrar y luego coinciden (entran en "Down" a la vez, vuelta de
  2,77 s); el Falso Caballero sale tras recargar su sala dos veces; la pared secreta de `Crossroads_10` rota a
  aguijonazos por el invitado se rompe en el host; revivir cuesta 33 de alma en los dos sentidos (el orbe baja también
  en pantalla); con la skin Rick Knight (copia B), el orbe lleno y su pulso salen con la imagen de la skin desde el
  principio y no vuelven al del juego. Ruta `crossroads` sin fallos.
- **0.24.0** (copias D y E, 11 oct 2026), con lo que siguió saliendo en la partida real: el Falso Caballero muere
  entero aunque el host esté junto al cuerpo y el invitado (que lo movía) lejos al matarle la cabeza (no cambia de PC,
  las 5 rejas se abren en los dos); la campana de `Crossroads_47` aparece en el host en cuanto el invitado abre la
  estación y su toque se copia paso a paso; las lianas de `Fungus1_01` cortadas en un PC salen cortadas en el otro; el
  capullo de savia de `Fungus2_15` roto por el invitado se rompe en el host y cada uno tiene sus mariposas; en la
  misma sala, una palanca golpeada por el invitado ya no apaga el cartel para desafiar a los Señores Mantis del host.
  El Musgoso Volátil de `Fungus1_01` y sus esporas se ven en el otro PC. Rutas `crossroads` y `greenpath` sin fallos.
  Después: los disparos y las crías del Elder Baldur de `Crossroads_11_alt` salen en el invitado; un aguijonazo del
  invitado a la copia del escupitajo del Gulka (`nailtouch`) lo devuelve en el host; el vómito de los Ogros Hongo de
  `Fungus2_05` se ve en el otro PC y, con los dos acabando el combate a la vez, las rejas se abren en los dos. Los
  Charged Lumafly de `Fungus3_26` están en el mismo sitio en los dos PCs; la capa de Polilla de `Fungus1_04` sigue para
  el host cuando el invitado ya tiene la habilidad (también al recargar la sala); tocar un Shrumeling de `Fungus2_28`
  ya no hace retroceder ni da invulnerabilidad; el Rey Vengamosca, con los dos en el suelo de su arena, ataca cada
  pocos segundos (embestidas e invocaciones) y casi no cambia de PC. El viaje en ciervo desde `Crossroads_47` a
  `Room_Town_Stag_Station` lleva al otro en los dos sentidos (lo elige el host o el invitado; los dos llegan a la salida
  del ciervo con control); a 3,6 del host va con él, a 20 o 28 se queda ("lejos de él"), y con el ajuste en no se queda
  aunque esté al lado; con el invitado en pausa, el HUD del host dice "en pausa"; sentados los dos en el banco de `Town`, cada uno ocupa una mitad y los dos PCs los ven igual.
  Habilidades de cada uno: un personaje de antes conserva las del mundo; la garra de uno y el doble salto del otro no
  pasan al compañero (el mundo, sí). Los soñadores de `RestingGrounds_04`: el host lee la placa, los dos entran juntos
  en `Dream_NailCollection`, cada uno habla con la Vidente y recoge su aguijón onírico, y los dos despiertan en
  `RestingGrounds_07`. Lurien: el host entra a su sueño con el aguijón onírico y el invitado va con él; al acabarlo, los
  dos ven la cinemática y la puerta (`Cutscene_Boss_Door`), vuelven a la Aguja y `guardiansDefeated` queda en 1 en los
  dos (no se cuenta dos veces). Un invitado nuevo ve la introducción, la salta y entra en la sala del host. El geo de
  los enemigos de `Crossroads_07` y el de sus rocas cae en el mismo sitio en los dos PCs (a 0,1-0,3; una pieza que rebota
  en el borde de una cornisa puede quedarse arriba en un PC y caer en el otro). Quirrel: hablarle en el templo y en el
  santuario de Sendero Verde no lo quita al otro, y en la Estación de la Reina está igual en los dos.
- **0.24.1** (copias D y E, 11 oct 2026), de la segunda partida real: el host habla con Bretta en `Fungus2_23` y en el
  invitado sigue en la sala (`brettaRescued` llega, no se vuelve a comprobar); al volver a entrar ya no está. En
  `Fungus2_15` el invitado entra con el host dentro y el cartel no se apaga; el host desafía y en el invitado se apaga
  al levantarse los tronos. En el PC que copia al Mantis Lord, al irse se oculta en el mismo cuadro que en el que lo
  mueve y sigue oculto lo mismo (~0,4 s; antes el último cuadro de la salida se quedaba ~0,36 s más en local).
  Ascensores de `Town` (`Mines Exit Lift`) y de `Room_Town_Stag_Station` (`Stag Lift`): el invitado se sube, baja y en
  el host baja a la vez; el host lo sube después desde abajo y en el invitado sube igual. Enemigos de `Crossroads_01`
  muertos: el invitado se sienta en el banco de `Town` y al volver están todos; muertos otra vez, mueren los dos y al
  volver están todos. En `Town`, el invitado mata su sombra y luego la copia de la del host: la música de sombra vuelve
  a la normal ("La música de la sombra vuelve a la normal"). En `Fungus2_15`: se desafía, muere el primer Señor Mantis,
  los dos salen vivos a `Fungus2_14` y vuelven; al desafiar otra vez sale con 210 de vida en los dos PCs. El host
  con la skin Rick Knight cae en `Town`: su cuerpo tendido sale con Rick en los dos PCs.
- **0.25.0** (copias D y E, 12 oct 2026), de la tercera partida real: en `Deepnest_16` los tres Garpedes van por el
  mismo punto en los dos PCs (a 0,01 s) y en 25 s no hace falta ninguna corrección más. Con 120 ms de lag simulado,
  en la segunda fase de los Señores Mantis y cambiando a los jugadores de lado cada 2,3 s, los Mantis se retoman
  entre elegir pared y lanzar (`Throw Antic 2`, `Throw 2`) y su `Shot Event` llega del otro PC (antes se quedaba el
  anterior, a veces vacío). Una Incubadora de Excavadores muerta en `Deepnest_26b` sigue muerta en los dos al salir
  y volver (los bichos normales vuelven al volver a entrar, como en el juego sin mod).
  En `Deepnest_01b` (la caída desde arriba) el invitado entra en la zona de una máscara negra (`Inverse Remasker`,
  `remasker_inverse`): sus máscaras cambian y las del host se quedan como estaban. Antes la cadena se copiaba al host
  ("paso de la cadena de Invitado (Fade Out → Fade In)") y a quien estaba lejos se le tapaba la vista hasta que el
  compañero volvía. Los Cavasuelos (`Baby Centipede`) de `Deepnest_16`, despiertos, van por el mismo sitio en los dos. Vuelto a
  mirar con `enemytrace` en los dos PCs (12/10/2026), luchando, al enterrarse, al elegir su punto nuevo y al volver a
  salir cuando llega alguien: la copia va como mucho 0,7 unidades por detrás y se ve o no igual que el original (salvo
  los 0,1 s del retraso de dibujo al aparecer y al enterrarse); el control pasa a quien se acerca a uno dormido.

- **0.28.0** (13 oct 2026, 4 copias a la vez: J host, K, L y M invitados): Hornet pasa de K a L con J mirando y luego
  a J: J descarta el traspaso viejo ("traspaso de hace 9,8 s, se descarta") y sigue con el nuevo. Reclamaciones de
  `Break Floor 1` seguidas (sin retraso) y cruzadas (3 s de retraso): los cuatro con el mismo controlador. Dos
  presentaciones a la vez (`presentlock`): J quieto por {2,3}, luego por {3}, y suelto al acabar la segunda. Un dato
  de misión cambiado por L en otra sala se comprueba en J; cambiado por K en la sala de J, se queda. K cae siguiendo a
  L; L entra en Godhome y el host pasa a K a seguir a J. J y M reviven a K a la vez (M con 1,5 s de retraso): K ve a J
  todo el rato, a M solo al acabar J, y a M se le devuelven 33 de alma. Mapa en Crossroads_01: tres brújulas de colores
  con su nombre. Cuatro en el banco de Bocasucia, a 1,1 uno del otro. Vida extra 2× con tres en pie: un golpe de 12 a
  Hornet quita 4 (3×). Tres van a la vez con el host: quedan en tres sitios distintos. M se va y vuelve con el mismo
  número: no hereda la reja que controlaba. Ningún mensaje no fiable perdido en el host. Repaso con 2 (J y K): los
  cambios de Hornet (8 en 90 s, al principio de un movimiento) y revivir.
  Después, las 16 rutas con 4 jugadores (`run-routes.sh --players 4`, mundo nuevo): 0 FALLO, y los mismos NOCONC que
  con 2 (los jefes que las rutas no despiertan y salas sin objetos ni enemigos). La de sesión necesitaba `die extras`:
  con 4, al morir el invitado y el host los otros dos seguían en pie y no caían todos.
- **0.30.0** (14 y 15 oct 2026, 4 copias: J host, K, L y M invitados), jefe a jefe, golpeando el usuario desde un PC que
  no lo mueve: Rey Vengamosca, Hornet (la Capa cae en todos y el sueño de los Soñadores al recogerla), Señores Mantis
  (también dos desafiando a la vez), Maestro de Almas (el orbe falso, la caída y el Desolador para todos), Caballeros
  Vigía, Defensor del Estiércol, Flukemarm, Guardián de Cristal y Guardián Furioso, Señor Traidor, Menderbug, Zote
  (cada conversación es de cada jugador y se va solo para quien habló con él), el Panteón del Maestro (caídos que
  reviven en la sala del siguiente jefe) y el Panteón de Hallownest hasta su final: sin pantalla negra al entrar, el
  nombre y las burbujas del Recipiente Puro en los 4, el abismo de la Radiancia igual en todos y la reaparición sobre
  él, y el final de cada uno en su PC, con su cinemática al acabar el suyo y sin ver a los demás. Además: el icono del
  compañero en la tienda de Iselda y en el Sendero Verde (con la Brújula Caprichosa), el menú y los avisos del mod en
  inglés (con el juego abierto ya en inglés) y el vapor del Falso Caballero. Las 16 rutas no se volvieron a pasar.

## Pendientes

Sin probar:
- De 0.24.0, sin reproducir en las copias: los caballeros de musgo B y C de las arenas, que se quedaban quietos (no
  existen en el mundo de prueba); la curación del compañero pegada (cortada o no, acaba bien). Devolver el escupitajo
  del Gulka con un aguijonazo físico (probado con `nailtouch`, no con el choque). Un objeto brillante que sale volando
  (mismo mecanismo que las rocas de geo, sin probar en vivo). En la partida real, a uno le salió Quirrel en su segunda
  ubicación y al otro no; en las copias no se reprodujo (sin saber cuál era la ubicación). Monomon y Herrah (probado
  Lurien).
- De 0.23.0: todo entre dos PCs reales. En la partida real el invitado dijo que revivir no le gastaba alma; en las
  copias sí la gasta (se cobra entera al empezar Focus y se devuelve si se suelta o le golpean, y su registro tiene
  tres "Golpeado mientras revivía").
- De 0.22.0: la reconexión entre dos PCs.
- Volver de una cinemática que acaba en una sala de juego (el final del Panteón de Hallownest; va por `SceneTravel`).
  La vuelta del Panteón del Maestro al Atrio sí está probada (0.29.0). Que su puerta quede completada no se miró
  directamente: lo apunta el juego en cada PC al acabar su secuencia.
- La revisita del Núcleo.
- Solo en una sesión real de dos PCs: instalar desde el zip en un juego sin mods y jugar por internet.

Con 3 o 4 jugadores: lo de la revisión del 12/10/2026 está arreglado y probado con 4 copias (ver "Cambios para 3 y 4
jugadores"). En 0.29.0, la historia entera con 4 copias (ver "Cambios de la historia completa con 4 jugadores"): los
jefes con golpes de verdad, los sueños, los soñadores, los dos finales del Hollow Knight y la Radiancia por el camino
del juego, la Compañía de Grimm, Grey Prince Zote, el Coliseo de Bronce (con Zote), Zote tras el Rey Vengamosca, Cloth
y el Panteón del Maestro entero; después, las 16 rutas con un mundo nuevo y 0 FALLO. En 0.30.0, cada jefe uno a uno y
el Panteón de Hallownest hasta su final (ver "Cambios de cada jefe con 4 jugadores"), y lo que faltaba: el icono del
compañero en una tienda y en otra zona (con la Brújula Caprichosa), el menú y los avisos en inglés, el vapor del Falso
Caballero, el Menderbug y Zote, que se va solo para quien habló con él. No hay ningún banco con una pared más cerca
que el reparto de los que se sientan (que ya la respeta). Pendiente en 0.30.0: pasar otra vez las 16 rutas con un
mundo nuevo (se pausó antes de acabar). Sin probar: todo con 3 o 4 PCs reales.

Limitaciones conocidas:
- El jefe ataca al caballero del PC que lo mueve, y lo mueve el jugador más cercano: cambia como mucho cada 8 s, y
  desde 0.27.0 solo entre dos ataques (ver "Enemigos y objetos de sala"). Es lo que hace que ataque a los dos. Un jefe
  sin estados de reposo en su lógica, o que no reposa en 4 s (Hornet a veces encadena esquivas y ataques sin pasar por
  `Idle`), sigue cambiando a mitad de un ataque.
- La marioneta se dibuja unas centésimas en el pasado (para moverse suave): en lo que cae rápido se nota un poco.
- Los nombres fijos de los menús del mod cambian de idioma al reiniciar el juego.
