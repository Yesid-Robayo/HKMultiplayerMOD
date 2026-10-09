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
    receptor los entrega en orden y sin duplicados.
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
    sala que recoge cada uno (objetos brillantes, fragmentos, cofres, orbes de sueño). Del mundo y compartido:
    habilidades de movimiento y hechizos, jefes, puertas, palancas, paredes, personajes y salas visitadas.
  - **Progreso compartido en directo** (`SharedProgress`): los campos del mundo se comparan cada 0,2 s con una copia
    (lectores compilados, sin memoria por lectura) y los cambios van al host, que los aplica y reenvía. Los objetos de
    sala (paredes, palancas, piedras de geo) se envían al guardarse y nunca retroceden.
- **Enemigos y objetos de sala**:
  - Cada enemigo lo mueve el PC del jugador más cercano, porque su lógica solo ataca al caballero de su PC. Lo reparte
    el dueño de la sala; cambia solo si el otro está claramente más cerca (4 unidades) y hace un rato del último cambio
    (3 s; 8 s en los jefes, cada cambio corta su ataque). Ese PC lo hace funcionar normal y envía su estado (transform,
    animación, profundidad, activo, vida, daño al tocarlo) con detección de cambios sin crear objetos.
  - En los demás juegos el enemigo es una "marioneta": su lógica (FSM, física) se pausa (no se reinicia) y se dibuja
    con el estado recibido e interpolación; sus colisiones de daño siguen funcionando para el jugador local.
  - Traspaso: el que lo suelta envía cómo iba (estado, números y objetos de cada FSM, velocidad, gravedad y tipo de
    cuerpo) y el nuevo lo sigue desde ahí. Los objetos van como "su caballero" (en quien lo recibe, el suyo), "una
    pieza del enemigo" o "un objeto de la sala" por su ruta, y solo rellenan las variables vacías: la marioneta no pasó
    por los estados que las guardan (el escupidor guarda al caballero al alertarse). También al salir de la sala o morir (aún dentro, al empezar el cambio de sala): un jefe no
    repite su presentación.
  - Los golpes del jugador local a una marioneta van al PC que la mueve, que los aplica al enemigo real. Cualquier
    aviso de fuera que llegue a una marioneta (el de daño del ataque, la zona de la arena en la que entró el caballero,
    la escena de batalla) también va al PC que la mueve: con esos avisos despiertan o reaccionan muchos jefes. Cada golpe que
    recibe un enemigo real se avisa a la sala: los demás ven el impacto, el destello y oyen el golpe (quien golpeó ya
    los vio en su PC). La marioneta copia también su giro, si se dibuja, si choca y si es invencible (y desde dónde).
  - Lo que se ve y se oye de la lógica del enemigo también pasa en las marionetas: cuando su FSM entra en un estado,
    la marioneta hace solo sus efectos (sonidos y música de jefe; objetos de la arena que se activan, partículas,
    ráfagas como los disparos de Mawlek, efectos y avisos a otros objetos de la arena, como las animaciones de la
    presentación de la Vasija Rota), con los números de su FSM en ese momento (velocidad y ángulo de una ráfaga). No
    los avisos al caballero ni a todos, ni lo que suelta geo.
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
    hasta que se abra. Las que ya están cerradas al cargar la sala no se cruzan.
  - Controladores de batalla que no son enemigos (Señores Mantis: los tronos, `Mantis Battle`, `Battle Main` y
    `Battle Sub`, que coordina a la pareja; Coliseo de los Tontos: su gestor, sus oleadas y sus jaulas, algunas con
    esperas al azar): igual que ascensores y rejas, los lleva un PC y los demás siguen sus estados. Quien acepta el
    desafío pasa a controlarlos, y el combate (jaula, suelo con pinchos, qué señores salen, oleadas, final) avanza
    igual en todos. Lo que mueven con avisos a todos (jaula, suelos, puertas) va igual que ellos, y quien entra a mitad
    del combate se pone al día con su estado. En las copias no se hacen las llamadas al caballero (devolverle el control,
    girarlo), y el cartel para desafiar se apaga en los demás PCs (si no, se podría desafiar otra vez a mitad del
    combate y ese caballero se quedaría sin control). Las órdenes de esos controladores a un enemigo no se reenvían:
    ya las da su copia en el PC que lo mueve. Los avisos de una copia a otro objeto con controlador (p. ej. el `SPAWN`
    con 1 s de retraso que la batalla del Coliseo manda a sus jaulas, que llega fuera de la aplicación del estado) no
    toman el control: ya los manda el controlador. Si no, los dos PCs se quitaban las jaulas, alguna no soltaba su
    enemigo y la oleada esperaba para siempre (`Battle Enemies` no llegaba a 0).
  - Encuentros con diálogo antes de un combate (Hornet en los Bordes del Reino; se detectan solos: un objeto que no
    es enemigo, con un diálogo, que después despierta o enciende a un enemigo o cierra la arena): van como los
    controladores de batalla, así que los dos ven el diálogo, con su caballero quieto mientras, y la Hornet que habla
    desaparece en los dos al empezar. Cada diálogo espera a que todos los de la sala lo terminen de leer (si alguno no
    responde en 30 s, sigue). Los personajes que solo hablan no cuentan y siguen siendo de cada uno.
  - Combates de jefe: si la zona que los empieza (la que cierra la arena o empieza el diálogo) avisa sin estar dentro
    todos los jugadores de la sala, se espera ("Esperando a ... para empezar el combate") y empieza cuando entran.
  - Jefes que se despiertan al ver al caballero (Hornet 1, Guardián de Cristal...): se apunta en qué estado duerme su
    lógica principal y su primer cambio desde ahí es despertar. Si algún jugador de la sala está a más de 25 de él,
    ese cambio se retiene (el mismo aviso) y el jefe es invulnerable mientras: golpearlo no lo despierta ni le quita
    vida. Despierta en cuanto están todos cerca.
  - Las copias no tocan los datos del jugador (contadores como las larvas llegan una vez, del PC de origen). Los tarros
    de larva se rompen en todos y la larva sale también en los demás.
  - Cofres: abrirlos es del mundo (el otro lo ve abrirse en directo, o ya abierto si entra después), pero lo que
    sueltan es de cada uno: el objeto queda en el suelo para cada jugador hasta que lo recoge él, y el geo le cae a
    quien esté en la sala.
  - Peligros animados (sierras del Palacio Blanco y del Camino del Dolor, paredes de pinchos): se mueven con una
    animación en bucle que empieza al cargar la sala. El dueño de la sala envía cada segundo en qué punto va cada una
    (`HazardSync`) y los demás se ajustan si se desvían más de un 3 % de vuelta. En las zonas revisadas no hay
    plataformas sólidas que se muevan solas (los ascensores ya van con controlador).
  - Objetos guardables (paredes, palancas, cofres...): el directo (golpe, animación, sonido) lo lleva solo la copia
    de su cadena (`FsmStateSync`). El aviso de "activado" de `SceneObjectSync` no mueve la FSM mientras esa cadena va
    en marcha: se comprueba al acabar y solo se activa si no llegó (como mucho 10 s después). Lo activado por una
    cadena llegada de otro PC no se vuelve a avisar. La línea base de cada objeto se toma después de
    `PersistentBoolItem.Start` (el juego pone ahí lo guardado; en los inactivos llega tarde), y lo que llega de otro PC
    antes de ese `Start` se pone justo después. El reposo de una cadena se toma cuando el objeto lleva 1 s quieto: si
    no, el fin de su inicio al cargar la sala (`Pause` → `Idle`) parecía un golpe y se copiaba a los demás.
  - Rompibles con FSM propia (la plantilla genérica del juego: recibe `TAKE DAMAGE`, tiene `Break` y la variable
    `Activated`, sin componente rompible ni guardable, como el letrero de la bajada a Cruces Olvidados): se copia su
    cadena como la de una palanca. Los rompibles con componente (`Breakable`, `BreakableObject`, `BreakablePole`,
    `BreakablePoleSimple`, `BreakableInfectedVine`, `VinePlatformCut`, `GlobControl`, `GrassCut`) son todos los tipos
    del juego y van por `BreakableSync`.
  - Quien entra en una sala recibe del dueño lo ya activado (puertas, palancas, paredes), los golpes de cada piedra de
    geo y los rompibles rotos (también los de FSM propia: `SceneObjectSync` los sigue con un id por su ruta en la sala,
    `fsm:...`, y quien entra los pone en su estado de "ya roto", sin animación). Lo que se pone así no cuenta como un
    golpe de ese PC y no se copia a los demás. Al guardar, lo activado en el mundo no vuelve atrás.
  - Lo que crean los enemigos en combate (proyectiles, crías, invocaciones) se crea en los demás con la misma acción de
    su marioneta (mismo prefab). Si hace daño o tiene vida, es una copia que sigue al original hasta que desaparece; los
    efectos solo visuales del pool corren solos. Lo que crean al prepararse (cadáveres, piezas propias) no se replica.
  - Los tarros del Coleccionista sueltan su bicho con su propio código. Solo lo suelta el tarro original: el PC que
    lo tiene lo sigue como algo creado en combate (se reconoce al encenderse junto a su tarro) y los demás crean una
    copia que lo sigue; las copias del tarro van vacías y se rompen sin soltar nada. Una copia que acaba sola no borra
    el original, salvo un disparo: si su copia choca (con el caballero de ese PC o una pared), el original también.
  - Lo creado en combate se numera con el jugador que lo creó: cada PC cuenta por su cuenta y dos podían dar el mismo
    número (se pisaban y quedaba una copia huérfana y quieta).
  - Cadáveres: tras morir, el PC donde murió de verdad sigue enviando la posición de su cadáver unos segundos (sale
    despedido con física, y algunos explotan, como el Gruzzer volátil): cae y explota en el mismo sitio en todos.
  - Nombre del jefe al despertar: la marioneta hace también lo que le dice al título de zona qué nombre poner.
  - Enemigos creados por objetos de la sala que no son enemigos (las jaulas del Coliseo crean el suyo al prepararse):
    cada PC los crea igual. Si se crean después de repasar la sala, se siguen desde ese momento con un identificador
    sacado de quién los creó; los clonados en la raíz de la sala (mismo nombre y mismo puesto entre hermanos) se
    distinguen por su sitio al cargarla. No quedan muertos para siempre (el Coliseo se repite).
  - Piezas que se mueven aparte (hijos con animación o que dañan, como la aguja de Hornet): posición, profundidad,
    giro, escala, si están activas y si se dibujan, si su caja de daño está encendida, cuánto dañan y animación vienen
    del PC que mueve el enemigo. También las de fuera del enemigo que apuntan las variables de sus FSM (el rayo del
    Guardián de Cristal, que suelta al prepararse): se identifican por esa variable, no por su sitio en la sala.
  - Trampas de la sala que el enemigo dispara con un aviso a todos (las torretas del techo del Guardián con
    `LASER SHOOT`): sus FSM pasan a ser del enemigo, así que solo funcionan en el PC que lo mueve; en los demás se
    paran y su giro, rayos, sonidos y animaciones se copian. Solo las que dañan (las puertas de la arena van aparte) y
    de la misma sala (la sala y su escena `_boss`). Al morir el enemigo vuelven a ser de cada PC y sus rayos se apagan.
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
    velocidad hasta chocar con el terreno (rayo contra sus capas) o deshacerse. La de Flukenest no se reproduce.
  - **Al mismo tiempo que la animación**: cada evento lleva el tick del remitente (el mismo reloj que su estado) y
    espera en una cola fija hasta que el avatar, que se dibuja un poco en el pasado, llega a ese tick (como mucho medio
    segundo). Sin memoria nueva por evento; la memoria creada mientras el compañero ataca es la del juego.
  - Los amuletos que cambian cómo se ve (Mark of Pride, Longnail, furia, Shaman Stone, Flukenest) viajan en un byte.

### Interfaz (`UI/`)

- **Menú principal → Multijugador**: copia de los menús del juego (botón de "Iniciar partida", pantalla de "Extras").
  Nombre y puerto; hostear (mundos coop, borrar con confirmación, nuevo) y unirse (dirección y código).
- **Pausa → Multijugador** (solo en partida multijugador): mundo, código, IP oculta hasta pedirla, jugadores con su
  ping y cerrar o salir de la partida. Páginas *Ajustes de la partida* (dos, con filas "Opción < valor >" copiadas de
  las opciones del juego: `GameStyledMenu.AddOption`, que el invitado ve sin poder cambiar) y *Compañeros* (vida y alma
  de cada uno, dar geo, desatascarse y recargar la sala).
- Avisos en pantalla con la letra del juego (código al abrir la partida, quién entra o sale).

### Funciones del coop (`Game/Features/`)

Diseño aprobado en [m5-diseno.md](m5-diseno.md). Cada función es una clase con `Attach`/`Detach` que engancha
`MultiplayerRunner` como las demás capas; sus ganchos al juego se ponen al cargar el mod (`Die()` es tan corto que el
juego lo copia dentro de `TakeDamage` al compilarlo: si se engancha más tarde, la muerte no pasa por el gancho). Los
mensajes nuevos van del 73 al 86. La interfaz es uGUI (`UI/HudCanvas`): un lienzo que sigue la zona de la cámara del
juego (16:9; con otra proporción el juego deja franjas) y escala con su alto como el HUD del juego; las piezas se crean
una vez y cada frame solo se mueven o se encienden, y los textos se rehacen solo al cambiar.

- **Estado de cada jugador** (`PartyState`, mensaje 74): en pie, caído, en cinemática o en sueño. Lo decide el host y
  lo reparte entero al cambiar y a quien entra. Un caído no cuenta para repartir enemigos (`EnemySync.AssignControllers`:
  los suyos pasan enseguida a quien sigue en pie), ni para las esperas de los combates de jefe (`MissingNear`,
  `EveryoneInside`), ni para los diálogos (`AllConvoDone`), y su avatar no se dibuja.
- **Ajustes del host** (`HostSettings`, mensaje 73 con versión de formato): dificultad (vida de jefes 1,5× por defecto,
  solo con el compañero en la sala; enemigos; daño recibido), revivir (sí/no, tiempo, alma, máscaras,
  invulnerabilidad, en el banco), pantalla (nombres, vida del compañero, flechas, avisos, barra del jefe) y regalar geo.
  Se guardan en `settings.json` de cada mundo y se envían al cambiar y a quien entra. La vida extra se hace dividiendo
  el daño en `HealthManager.TakeDamage` en el PC que mueve al enemigo (`Difficulty`; las marionetas reciben los golpes
  con multiplicador 0); un jefe es lo que tiene `unlockBossScene`. El daño recibido se escala en
  `HeroController.TakeDamage`, salvo pinchos, ácido y pozos.
- **Revivir** (`Revive`, mensajes 75 a 80): `HeroController.Die` se sustituye por la caída si hay alguien en pie (en un
  sueño, solo si está en el mismo; en los Panteones, nunca). El caído envía `Downed` (sala y suelo bajo él); el host
  le asigna el compañero en pie al que seguir. El caballero del caído se oculta (sin colisión, sin gravedad,
  invencible, sin control; se reaplica cada frame) y, tras la animación de muerte, se pega al avatar del compañero, con
  las zonas de cámara emuladas; si el compañero cambia de sala, viaja detrás de él sin puerta de entrada (como la 0.7.3).
  Su cuerpo (`DownedBody`: animación "Death" y luego "Prostrate", con "† Nombre") se ve en su sala. Junto a él, Focus
  cobra el alma al empezar (se devuelve si se suelta o te golpean), no cura (`CanFocus`) y al completarse se pide
  `ReviveRequest`; el host lo valida (en pie y en la sala del cuerpo) y responde `Revived` (cuerpo y vida). El progreso
  va a la sala con `ReviveProgress` (no fiable): el caído lee "X te está reviviendo". `BenchRest` al sentarse revive a
  los caídos en ese banco con toda la vida; si no queda nadie en pie, `Wipe`: cada caído vuelve a su cuerpo y muere de
  verdad. Al guardar estando caído no se guardan la invencibilidad ni la vida a 0.
- **HUD del compañero** (`Vitals`, mensaje 81; `PartnerHud`): vida, vida máxima, azules, alma y zona de la sala de cada
  uno, al cambiar y como mucho 4 por segundo; el host guarda las últimas y se las da a quien entra. Mini-HUD arriba a la
  derecha, espejo exacto del HUD del compañero (sprites de `HUD Cln`: la llena `appear_v020004`, la vacía
  `health_backboard` y la azul `idle0000`, a escala entre sí por sus vértices de tk2d y girados si tk2d los guarda
  girados), con su orbe de alma y su estado; sus máscaras sobre su cabeza 3 s al cambiarle la vida y flechas en el borde.
- **Barra del jefe** (`BossBar`): los jefes se apuntan al encenderse (`HealthManager.OnEnable`), con el nombre de su
  entrada del diario; la vida máxima es la mayor vista.
- **Sombras** (`ShadeSync`, mensajes 82 a 84): cada uno informa de la suya al host, que la reenvía y la da a quien entra.
  En cada PC se crea una copia (`hollowShadeObject` con su lógica, sin "Dreamnail Kill"; su nombre empieza por
  "Hollow Shade" para que `EnemySync` no la comparta) que lee los datos del dueño (`GetPlayerDataInt`/`String`). Su
  muerte no va por la vía normal: avisa al host, que acepta el primer aviso y pide al dueño que mate la suya (si no está
  en su sala, se crea un momento junto a él).
- **Compañeros** (`PartyActions`, mensajes 85 y 86): regalo de geo validado por el host (o devuelto), desatascarse y
  recargar la sala al quitar la pausa.

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
  dañino que se mueve o gira, `hazards` los peligros animados alineados y `wake <nombre>` enciende un enemigo apagado.
- Funciones del coop: `settings` (ajustes que rigen), `party` (revivir y estado de cada jugador), `vitals`, `shades`,
  `killshade` (mata la copia de la sombra de un compañero), `bossbar`, `focus <s>` (Focus simulado), `tpbody`, `heal`
  (vida llena por la vía del juego: `pdset maxHealth` no redibuja el HUD del juego y el mini-HUD lo mostraría distinto),
  `soul <n>`, `mppage <partida|ajustes|ajustes2|compañeros>`, `mpoption <etiqueta>|<pasos>`, `mpoptions`,
  `spriteof <objeto>`, `sprites <colección>|<texto>`, `collections <texto>`, `hudsprite`, `hudrot` y `clips <texto>`.
  `fsmdump` muestra también las variables de objeto y cada efecto que reproduce una marioneta se registra
  ("efecto de enemigo ...").
- Efectos del otro jugador: `burst <nombre> <n> <segundos>` hace capturas seguidas (las órdenes se leen cada medio
  segundo, así que para ver un efecto en el otro PC su ráfaga empieza antes), `hurt` da un golpe de 1 al caballero,
  `effects` cuenta los enviados, recibidos y reproducidos, y cada efecto reproducido se registra
  ("efecto Slash de ..."). `tree <profundidad> <objeto>` y `fsmall <objeto>` muestran la jerarquía y todas las FSM de
  un objeto (también inactivo o del pool, como `Fireball Top`). Ruta: `tests/routes/effects.route`.

## Hitos

| Hito | Contenido | Se da por bueno cuando |
|---|---|---|
| M1 | Transporte + sesión + jugadores (verse y moverse) | Dos jugadores conectados con código, se ven moverse fluido, memoria de red ≈ 0 |
| M2 | Mundo compartido (progreso, objetos persistentes, al unirse) | Las habilidades y el mundo coinciden entre los dos |
| M3 | Enemigos y objetos de sala | Pelear juntos en salas normales y con jefes probados |
| M4 | Efectos del otro jugador | Se ven sus ataques, hechizos y movimientos especiales |
| M5 | Funciones del coop (revivir, sombras, skins, panel...) | Igual o mejor que la 0.7.3 |
| M6 | Instalador y paquete | Tu amigo lo instala y jugáis |

## Pendientes

- **M3**: revisadas con `arenas` las salas de los jefes del juego base (sin sueños ni Panteones): solo los Mantis y el
  Coliseo necesitaban controlador compartido; Señor Traidor, Caballero de la Colmena, Nosk y los Caballeros Vigía usan
  un `Battle Scene` que ya se copia por cadena. Probados con los dos dañados: Cazador de Cristal (cristales), bichos de
  cristal (láser), Madre Aspid (crías), Aspid primigenio (disparos), minero (pico), Uumuu (descargas), Coleccionista
  (tarros y sus bichos, cuerpo, embestida), Caballero de la Colmena, Flukemarm (sus moscas) y Rey Mosca (él y sus
  moscas). Mawlek espera a que estén los dos cerca para despertar. El Coliseo de Bronce se pasa entero, con todas sus
  oleadas y Zote, igual en los dos. Falta por ver en vivo:
  - Cargador Musgoso atacando.
  - Que la explosión del Gruzzer volátil dañe a los dos (la explosión sale en los dos; el daño no se ha visto).
  - Los jefes finales de Plata y Oro del Coliseo (Oblobbles, Domadora de Dioses) con la prueba entera.
  - Una espera en "WaveCheck 5" del Coliseo de Bronce en la prueba automática: un minero que sale de debajo de la tierra
    cuando el caballero se acerca, y en la prueba los caballeros no se mueven (jugando no debería pasar).
  Grimm, los jefes de sueño, Hollow Knight y la Radiancia pasan a M5. El sonido del minero del Coliseo ya suena en el
  PC que no lo mueve (comprobado en M5: tajo, lanzamiento del pico y voz al morir).
- **M4** (hecho): sin reproducir aún, porque no salen de una FSM de acciones del caballero o crean objetos con daño:
  los rayos de Grubberfly's Elegy, las moscas de Flukenest (y su versión con Defender's Crest), las nubes de Spore
  Shroom y Defender's Crest al curarse, Thorns of Agony, Sharp Shadow (su golpe; el sonido sí), la onda en pantalla de
  los gritos, la muerte del compañero (su animación ya se ve en su sprite, sin la cabeza ni el aguijón que salen
  despedidos), los aterrizajes fuertes y el destello parpadeante de invulnerabilidad.
- **M5**: hecha la primera parte (estado de cada jugador, ajustes del host y panel, HUD del compañero y barra del jefe,
  revivir y sombras), probada en vivo con dos copias. Queda la segunda: cinemáticas compartidas, sueños compartidos y sus
  jefes y skins. De la primera parte:
  - El estado "en cinemática" y "en sueño" de `PartyState` existe pero aún no lo pone nadie (llega con la segunda parte).
  - Caer en un sueño solo se permite con el compañero en el mismo sueño; "si caen los dos, despiertan los dos" sale de
    la muerte normal del juego en un sueño, sin probar (hacen falta los sueños compartidos).
  - Sin barras de vida de enemigos normales (el diseño solo pide la del jefe) ni opción "Saltar cinemáticas" en los
    ajustes (llega con las cinemáticas). La página "Skin" del panel llega con las skins.
  - No probado en vivo: revivir con 3 o más jugadores y el revivido parpadeando (la invulnerabilidad se pone con el
    `StartInvulnerable` del juego).
  Jefes que dependen de cinemáticas o de entrar juntos:
  - **Grimm y el Rey de las Pesadillas**: antes hay que completar la misión de la Compañía (llamas, farol).
  - **Jefes de sueño** (Campeón Fallido, Tirano de Almas, Kin Perdido, Zote el Poderoso, guerreros soñadores): se
    entra con el aguijón onírico y cada jugador entraría por su cuenta; hay que llevar a los dos al sueño.
  - **Hollow Knight y la Radiancia**: cinemáticas de entrada y final.
- **M6**: `tools/package.sh` aún usa los nombres del mod anterior (`HollowKnightCoop`).
- **Pasto y telas al paso del compañero** (solo estético, baja prioridad): hoy se sincroniza el corte, pero no el
  balanceo cuando el otro caballero pasa por encima. Sin red: en cada PC, cada pocos frames, buscar el pasto
  (`GrassBehaviour`) cercano al avatar del otro y darle el mismo empujón que da el héroe local, con la velocidad del
  avatar. No poner la etiqueta `Player` al avatar: muchas cosas del juego reaccionan a ella (puertas entre salas, zonas
  de cámara, NPCs, rangos de alerta de enemigos).
