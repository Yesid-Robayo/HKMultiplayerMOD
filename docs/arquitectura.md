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
    se puede derivar la clave, así que nadie sin él puede unirse ni hacerse pasar por el host.
  - Cada paquete con **ChaCha20-Poly1305** (cifrado autenticado); el *nonce* sale del número de secuencia y la
    dirección, y nunca se repite. Paquetes alterados o repetidos se descartan.
  - Primitivas de la biblioteca BouncyCastle (MIT), con los objetos de cifrado reutilizados.
- **Conexión**: saludo en dos pasos con versión del mod, nombre y prueba de que se conoce el código; latido, tiempo de
  espera y desconexión limpia. Límite de jugadores y de conexiones a medio abrir.

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
  un búfer de interpolación (pequeño retraso adaptativo y extrapolación corta si un paquete se retrasa).
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
    - En el PC de quien habla el personaje hace lo del juego (Bretta o Tiso siguen ahí hasta volver a entrar); en el otro
      se va enseguida, como si se cargara la sala.
- **Enemigos y objetos de sala**:
  - Cada enemigo lo mueve el PC del jugador más cercano, porque su lógica solo ataca al caballero de su PC. Lo reparte
    el dueño de la sala; cambia solo si el otro está claramente más cerca (4 unidades) y hace un rato del último cambio
    (5 s; 8 s en los jefes, cada cambio corta su ataque: con 3 s y los dos cerca, un caballero de musgo podía volver a
    empezar su estado cada pocos segundos sin llegar a atacar). Ese PC lo hace funcionar normal y envía su estado (transform,
    animación, profundidad, activo, vida, daño al tocarlo) con detección de cambios sin crear objetos.
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
  - Traspaso: el que lo suelta envía cómo iba (estado, números y objetos de cada FSM, velocidad, gravedad y tipo de
    cuerpo) y el nuevo lo sigue desde ahí. Los objetos van como "su caballero" (en quien lo recibe, el suyo), "una
    pieza del enemigo" o "un objeto de la sala" por su ruta, y solo rellenan las variables vacías: la marioneta no pasó
    por los estados que las guardan (el escupidor guarda al caballero al alertarse). También al salir de la sala o morir (aún dentro, al empezar el cambio de sala): un jefe no
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
    de la zona). Si el controlador se va de la sala (p. ej. muere), quien se queda pasa a serlo; quien entra o vuelve
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
    sin ella, el caballero del otro PC reaparecía en la entrada de la sala), y el cartel para desafiar se apaga en los demás PCs al llegar un estado de un controlador de batalla (antes con cualquier cadena de otro PC: una palanca de la Aldea Mantis lo apagaba sin combate) (si no, se podría desafiar otra vez a mitad del
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
    no, el primero en cruzarla salía de ella enseguida, la espera se olvidaba y el combate no empezaba nunca.
  - Jefes que se despiertan al ver al caballero (Hornet 1, Guardián de Cristal...): se apunta en qué estado duerme su
    lógica principal y su primer cambio desde ahí es despertar. Si algún jugador de la sala está a más de 25 de él,
    ese cambio se retiene (el mismo aviso) y el jefe es invulnerable mientras: golpearlo no lo despierta ni le quita
    vida. Despierta en cuanto están todos cerca. No esperan los de una arena que ya espera a que entren todos (su
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
    el tamaño de su último uso) y los números de sus FSM. Así una onda solo visual del Falso Caballero, que recibe su
    velocidad y dirección con `SetFsmFloat`/`SetFsmBool` justo después de salir, avanza igual en cada PC, echa sus
    chorros (que dañan al caballero de ese PC) y se acaba en la pared. Lo creado en combate no tiene tiempo de
    preparación: sus efectos (los barriles al romperse) cuentan desde que sale.
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
mensajes nuevos van del 73 al 106 (el 98 es de `EnemySync`, el 104 y el 105 de `HazardSync` y el 106 de
`StagTogether`). La interfaz es uGUI (`UI/HudCanvas`): un lienzo que sigue la zona de la cámara del
juego (16:9; con otra proporción el juego deja franjas) y escala con su alto como el HUD del juego; las piezas se crean
una vez y cada frame solo se mueven o se encienden, y los textos se rehacen solo al cambiar.

- **Estado de cada jugador** (`PartyState`, mensaje 74): en pie, caído, en cinemática o en sueño. Lo decide el host y
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
  le asigna el compañero en pie al que seguir. El caballero del caído se oculta (sin colisión, sin gravedad,
  invencible, sin control; se reaplica cada frame) y, tras la animación de muerte, se pega al avatar del compañero, con
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
  enemigo. Los de los sueños (Tirano de Almas, Príncipe Gris Zote) no tienen estatua y antes se quedaban sin barra. Con barras visibles, `Notice.SetLift` sube los avisos (abajo a
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
  materiales compartidos; efectos del compañero (sus copias bajo el avatar) con su skin. Crear la textura de una hoja de
  4096 cuesta 0,15-0,25 s (el juego descomprime el PNG en el hilo principal): se hace en pausa o al cambiar de sala (si
  no llega ese momento en 3 s, fuera de un combate de jefe, con un tirón corto). Cada PC elige si ve las skins de los demás.
  El orbe de alma lleno propio (`OrbFull.png`) cambia el sprite de `Soul Orb/Orb Full` y de los fotogramas de su pulso
  (`Orb Full/Pulse Sprite`, `soul_orb_glow0000` a `0015`), reconocidos por el nombre del sprite: los del pulso van en un
  atlas (`sactx-...`) y, buscados por el nombre de su textura, no se cambiaban, así que con el orbe lleno parpadeaba
  entre la cara del juego y la de la skin. Si su imagen no se pudo crear en ese frame, se vuelve a intentar (antes el
  orbe se quedaba como el del juego hasta cambiar de skin).
- **Barra del jefe**: el nombre es el título con el que el juego presenta al jefe (hoja `Titles`, partes `_SUPER`,
  `_MAIN` y `_SUB`), buscado en los textos de sus FSM o en las palabras de su nombre; si no, el de su diario.

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
  `killshade` (mata la copia de la sombra de un compañero), `bossbar`, `focus <s>` (Focus simulado), `tpbody`, `heal`
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
- Paquete para jugadores: `tools/manual-tests/installer-test.sh <zip>` (ver [Paquete e instalador](#paquete-e-instalador)).

### Cómo probar (recordatorio)

- Copias del juego en `test-instances/` (enlaces duros al juego de Steam; `ensure_instance` las crea): B/C para
  pruebas sueltas (puerto 26950), D/E... para las rutas automáticas (27100+), F/G para el Coliseo y los jefes (27300),
  H/I para la prueba del instalador (`installer-test.sh` las rehace desde cero). Instalar solo en las que se usan
  (`bash tools/test-cycle.sh --only "f g"`) y cerrar solo esas (`stop_all F G`).
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
  dueño recupere la suya (el host acepta solo el primer aviso). La de alguien desconectado no se ve.
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

## Paquete e instalador

- `tools/package.sh` compila en Release y arma `out/package/HKMultiplayer-<versión>.zip` con una carpeta
  `HKMultiplayer-<versión>`: `HKMultiplayer Setup.bat`, `LEEME.txt` y `README.txt` (instrucciones para jugadores en
  español y en inglés, con la instalación a mano) en la raíz, los scripts de PowerShell en `scripts/` (con `version.txt`), lo que se copia al juego en `files/` (la Modding API de
  `vendor/modding-api/OutputFinal` sin su README, y `Mods/HKMultiplayer` con el mod, su `.pdb` y BouncyCastle) y
  `SHA256SUMS.txt`. Del juego solo van los dos ensamblados que parchea la Modding API (`Assembly-CSharp.dll` y
  `TeamCherry.Localization.dll`). Fuentes del instalador en `tools/installer/`.
- `HKMultiplayer Setup.bat` (→ `scripts/setup.ps1`): sin argumentos abre una ventana de WinForms con el aspecto de los
  menús del juego (carpeta del juego con lista y *Buscar…*, estado de la instalación, *Instalar*/*Actualizar*,
  *Desinstalar*, *Instrucciones* y el registro de lo que hace); con `/install` o `/uninstall` y la ruta trabaja en la
  consola (lo usa `installer-test.sh`). Un botón desactivado (*Desinstalar* sin nada instalado, *Instalar* sin carpeta
  del juego) se vuelve a dibujar con el texto en blanco: Windows lo pinta en un gris oscuro que no se leía sobre el
  fondo, y `ForeColor` no cuenta en un botón desactivado. Los textos van en el idioma de Windows (`common.ps1`: `L "español" "inglés"`).
  El .bat abre PowerShell minimizado y `setup.ps1` esconde su consola al abrir la ventana: con `-WindowStyle Hidden` en
  la orden (combinación que algunos antivirus vigilan) se veía igual, pero parecía sospechoso.
  `install.ps1` y `uninstall.ps1` solo definen `Invoke-Install` e `Invoke-Uninstall`, que escriben por `Write-Out` en la
  ventana o en la consola. Carpeta del juego pasada (también arrastrada sobre el .bat) o buscada (registro de Steam,
  `libraryfolders.vdf`, desinstalación de Steam, rutas típicas en los discos locales, GOG). Exige Unity 6 (`UnityPlayer.dll` 6000.x) y `TeamCherry.TK2D.dll`:
  con otra versión del juego la Modding API lo dejaría sin arrancar. Solo bloquea si hay un `hollow_knight.exe`
  abierto desde esa carpeta (otras copias no molestan).
- Cada cambio se apunta en `<juego>\HKMultiplayer-backup\manifest.txt` antes de hacerlo: `added-dir`, `added`,
  `replaced` (el original en `HKMultiplayer-backup\files\`), `old-mod` (el mod anterior `HollowKnightCoop`, movido a
  la copia porque también abre la red) y `moved` (otros mods apartados a `Mods\Disabled`). Volver a instalar es una
  actualización: lo ya apuntado no se vuelve a copiar, así que la copia sigue siendo la del juego antes de la primera
  instalación.
- La desinstalación (`uninstall.ps1`) recorre el manifiesto al revés. De `Mods\HKMultiplayer` solo deja
  `Skins` si tiene algo; las carpetas añadidas se borran si quedan vacías. Si algo no se pudo devolver, conserva la
  copia y remite a "Verificar integridad" de Steam. Sin manifiesto (mod puesto a mano) quita solo el mod.

## Hitos

| Hito | Contenido | Se da por bueno cuando |
|---|---|---|
| M1 | Transporte + sesión + jugadores (verse y moverse) | Dos jugadores conectados con código, se ven moverse fluido, memoria de red ≈ 0 |
| M2 | Mundo compartido (progreso, objetos persistentes, al unirse) | Las habilidades y el mundo coinciden entre los dos |
| M3 | Enemigos y objetos de sala | Pelear juntos en salas normales y con jefes probados |
| M4 | Efectos del otro jugador | Se ven sus ataques, hechizos y movimientos especiales |
| M5 | Funciones del coop (revivir, sombras, skins, panel...) | Igual o mejor que la 0.7.3 |
| M6 | Instalador y paquete | Tu amigo lo instala y jugáis |

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
- La traducción al inglés dentro del juego (0.21.0) y el vapor naranja de la muerte del Falso Caballero (0.20.3).
- De 0.22.0: el icono del compañero en una sala que no sale en el mapa (tiendas) y en otra zona; la reconexión entre
  dos PCs.
- La conversación con Zote tras el Rey Vengamosca, Zote en el Coliseo y Cloth (no aparecen en el mundo de prueba);
  Menderbug (por análisis).
- Volver de una cinemática que acaba en una sala de juego (finales de los Panteones; va por `SceneTravel`).
- La entrada a la Radiancia por el camino del juego (una vez se trabó la escena de Hornet atando al Hollow Knight;
  pudo ser el `DISABLE` de los cambios de quién mueve al jefe, arreglado en 0.20.3); la revisita del Núcleo.
- Solo en una sesión real de dos PCs: instalar desde el zip en un juego sin mods y jugar por internet.

Limitaciones conocidas:
- Si un dato cambiado en otro PC hace aparecer a alguien que el juego destruyó al cargar la sala (otro sitio de
  Mr. Mushroom), no aparece hasta volver a entrar.
- El jefe ataca al caballero del PC que lo mueve, y lo mueve el jugador más cercano: cada cambio (como mucho cada 8 s)
  corta su ataque. Es lo que hace que ataque a los dos.
- La marioneta se dibuja unas centésimas en el pasado (para moverse suave): en lo que cae rápido se nota un poco.
- Los nombres fijos de los menús del mod cambian de idioma al reiniciar el juego.
