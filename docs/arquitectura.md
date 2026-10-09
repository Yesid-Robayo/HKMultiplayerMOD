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
    sin ella, el caballero del otro PC reaparecía en la entrada de la sala), y el cartel para desafiar se apaga en los demás PCs (si no, se podría desafiar otra vez a mitad del
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
    `BreakablePoleSimple`, `BreakableInfectedVine`, `VinePlatformCut`, `GlobControl`, `GrassCut`) son todos los tipos
    del juego y van por `BreakableSync`. Salvo los del pool del juego (guardados en él o sacados de él, como las gotas
    `Orange Glob` de los Bordes del Reino, que vuelven solas al pool): su ruta no es la misma en cada PC ni de una vez a
    otra, así que romper una aquí rompía otra distinta (o ninguna) en el otro PC. Son decoración y cada PC tiene las
    suyas.
  - Quien entra en una sala recibe del dueño lo ya activado (puertas, palancas, paredes), los golpes de cada piedra de
    geo y los rompibles rotos (también los de FSM propia: `SceneObjectSync` los sigue con un id por su ruta en la sala,
    `fsm:...`, y quien entra los pone en su estado de "ya roto", sin animación). Lo que se pone así no cuenta como un
    golpe de ese PC y no se copia a los demás. Al guardar, lo activado en el mundo no vuelve atrás.
  - Lo que crean los enemigos en combate (proyectiles, crías, invocaciones) se crea en los demás con la misma acción de
    su marioneta (mismo prefab). Si hace daño o tiene vida, es una copia que sigue al original hasta que desaparece; los
    efectos solo visuales del pool corren solos. Lo que crean al prepararse (cadáveres, piezas propias) no se replica.
    Se envía al acabar el estado que lo creó (o el frame), tal como lo dejó su lógica: posición, giro entero (el polvo
    y los chorros del juego son partículas giradas en X para salir hacia arriba), tamaño (el pool reutiliza objetos con
    el tamaño de su último uso) y los números de sus FSM. Así una onda solo visual del Falso Caballero, que recibe su
    velocidad y dirección con `SetFsmFloat`/`SetFsmBool` justo después de salir, avanza igual en cada PC, echa sus
    chorros (que dañan al caballero de ese PC) y se acaba en la pared. Lo creado en combate no tiene tiempo de
    preparación: sus efectos (los barriles al romperse) cuentan desde que sale.
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
  las opciones del juego: `GameStyledMenu.AddOption`, que el invitado ve sin poder cambiar) y *Compañeros* (vida y alma
  de cada uno, dar geo, desatascarse y recargar la sala).
- Avisos en pantalla con la letra del juego (código al abrir la partida, quién entra o sale).

### Funciones del coop (`Game/Features/`)

Diseño aprobado en [m5-diseno.md](m5-diseno.md). Cada función es una clase con `Attach`/`Detach` que engancha
`MultiplayerRunner` como las demás capas; sus ganchos al juego se ponen al cargar el mod (`Die()` es tan corto que el
juego lo copia dentro de `TakeDamage` al compilarlo: si se engancha más tarde, la muerte no pasa por el gancho). Los
mensajes nuevos van del 73 al 97. La interfaz es uGUI (`UI/HudCanvas`): un lienzo que sigue la zona de la cámara del
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
- **Barra del jefe** (`BossBar`): los jefes se apuntan al encenderse (`HealthManager.OnEnable`), con el nombre de su
  entrada del diario; la vida máxima es la mayor vista.
- **Sombras** (`ShadeSync`, mensajes 82 a 84): cada uno informa de la suya al host, que la reenvía y la da a quien entra.
  En cada PC se crea una copia (`hollowShadeObject` con su lógica, sin "Dreamnail Kill"; su nombre empieza por
  "Hollow Shade" para que `EnemySync` no la comparta) que lee los datos del dueño (`GetPlayerDataInt`/`String`). Su
  muerte no va por la vía normal: avisa al host, que acepta el primer aviso y pide al dueño que mate la suya (si no está
  en su sala, se crea un momento junto a él).
- **Compañeros** (`PartyActions`, mensajes 85 y 86): regalo de geo validado por el host (o devuelto), desatascarse y
  recargar la sala al quitar la pausa.
- **Lo que hace cada uno** (`PartyState`, mensaje 91): cada jugador informa al host si está en una cinemática
  compartida o en un sueño; es su estado mientras no está caído (al revivir en un sueño vuelve a "en sueño"). El
  mini-HUD lo muestra si está en otra sala. Con eso va si tiene el juego en pausa (el host lo reparte en
  `PartyStatus`): con la escala de tiempo a 0 sus enemigos se quedaban congelados para los demás, así que quien está
  en pausa, como un caído, no se queda con enemigos (`PartyState.IsInactive` en `EnemySync.AssignControllers`).
- **Cinemáticas compartidas** (`SharedCutscenes`, mensajes 87 a 90): los finales, los créditos, Mr. Mushroom y las
  pantallas con las que acaba el final (partida completada, Alma de Acero). Quien entra en una lo dice al host, que
  lleva a los demás (con un cambio de sala, esperando si están en pausa o cambiando de sala; un caído no va). Al
  llevado, la malla del vídeo (`CinematicPlayer`) quedaba donde estaba su cámara en la sala anterior y se veía negro: se
  vuelve a colocar delante de la cámara como lo hace el juego. Saltar (`GameManager.SkipCutscene`) es un voto: el host
  manda saltar a todos cuando votaron todos los que la ven (o con uno, si así lo eligió en "Saltar cinemáticas") y
  todos ven "Esperando a X para saltar 1/2"; si el que faltaba se va, se salta. Al terminar
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
- **Skins** (`Skins`, mensajes 96 y 97, transferencia grande 3): compatibles con CustomKnight, como la 0.7.3 (con
  `AtlasRepair` para las hojas antiguas del HUD y el geo). Cada uno anuncia su skin (nombre y huella SHA-256 de los
  archivos que ven los demás) y quien no la tiene la pide al host, que la envía o se la pide a su dueño. Van por la
  conexión cifrada del mod como transferencia grande de **baja prioridad** (`NetConnection`: cola aparte, solo sin otras
  transferencias pendientes, a unos 0,5 MB/s y con como mucho 48 mensajes sin confirmar, así los golpes y eventos no
  esperan detrás de la skin). Máximo 32 MB, solo PNG de hasta 4096x4096; la huella se comprueba en otro hilo y todo
  queda en memoria. Cuerpo con un bloque de propiedades por renderer; hechizos, amuletos y HUD propios en los
  materiales compartidos; efectos del compañero (sus copias bajo el avatar) con su skin. Crear la textura de una hoja de
  4096 cuesta 0,15-0,25 s (el juego descomprime el PNG en el hilo principal): se hace en pausa o al cambiar de sala (si
  no llega ese momento en 60 s, fuera de un combate de jefe). Cada PC elige si ve las skins de los demás.
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
- Funciones del coop: `settings` (ajustes que rigen), `party` (revivir y estado de cada jugador), `vitals`, `shades`,
  `killshade` (mata la copia de la sombra de un compañero), `bossbar`, `focus <s>` (Focus simulado), `tpbody`, `heal`
  (vida llena por la vía del juego: `pdset maxHealth` no redibuja el HUD del juego y el mini-HUD lo mostraría distinto),
  `soul <n>`, `mppage <partida|ajustes|ajustes2|compañeros>`, `mpoption <etiqueta>|<pasos>`, `mpoptions`,
  `spriteof <objeto>`, `sprites <colección>|<texto>`, `collections <texto>`, `hudsprite`, `hudrot` y `clips <texto>`.
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
  objetos a los que apunta el caballero y `spawnlog <segundos>` registra lo que sale del pool. `effects` dice también
  qué copias de amuletos y cuántos acompañantes del compañero hay encendidos. Ruta: `tests/routes/effects.route`.
- Paquete para jugadores: `tools/manual-tests/installer-test.sh <zip>` (ver [Paquete e instalador](#paquete-e-instalador)).

## Paquete e instalador

- `tools/package.sh` compila en Release y arma `out/package/HKMultiplayer-<versión>.zip` con una carpeta
  `HKMultiplayer-<versión>`: `instalar.bat`, `desinstalar.bat` y `LEEME.txt` (instrucciones para jugadores) en la raíz,
  los scripts de PowerShell en `scripts/` (con `version.txt`), lo que se copia al juego en `files/` (la Modding API de
  `vendor/modding-api/OutputFinal` sin su README, y `Mods/HKMultiplayer` con el mod, su `.pdb` y BouncyCastle) y
  `SHA256SUMS.txt`. Del juego solo van los dos ensamblados que parchea la Modding API (`Assembly-CSharp.dll` y
  `TeamCherry.Localization.dll`). Fuentes del instalador en `tools/installer/`.
- `instalar.bat` (→ `scripts/install.ps1`): carpeta del juego pasada (también arrastrada sobre el .bat) o buscada
  (registro de Steam, `libraryfolders.vdf`, desinstalación de Steam, rutas típicas en los discos locales, GOG); si hay
  varias pregunta, si no hay ninguna pide la ruta. Exige Unity 6 (`UnityPlayer.dll` 6000.x) y `TeamCherry.TK2D.dll`:
  con otra versión del juego la Modding API lo dejaría sin arrancar. Solo bloquea si hay un `hollow_knight.exe`
  abierto desde esa carpeta (otras copias no molestan).
- Cada cambio se apunta en `<juego>\HKMultiplayer-backup\manifest.txt` antes de hacerlo: `added-dir`, `added`,
  `replaced` (el original en `HKMultiplayer-backup\files\`), `old-mod` (el mod anterior `HollowKnightCoop`, movido a
  la copia porque también abre la red) y `moved` (otros mods apartados a `Mods\Disabled`). Volver a instalar es una
  actualización: lo ya apuntado no se vuelve a copiar, así que la copia sigue siendo la del juego antes de la primera
  instalación.
- `desinstalar.bat` (→ `scripts/uninstall.ps1`) recorre el manifiesto al revés. De `Mods\HKMultiplayer` solo deja
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

## Pendientes

- **M3**: revisadas con `arenas` las salas de los jefes del juego base (sin sueños ni Panteones): solo los Mantis y el
  Coliseo necesitaban controlador compartido; Señor Traidor, Caballero de la Colmena, Nosk y los Caballeros Vigía usan
  un `Battle Scene` que ya se copia por cadena. Probados con los dos dañados: Cazador de Cristal (cristales), bichos de
  cristal (láser), Madre Aspid (crías), Aspid primigenio (disparos), minero (pico), Uumuu (descargas), Coleccionista
  (tarros y sus bichos, cuerpo, embestida), Caballero de la Colmena, Flukemarm (sus moscas) y Rey Mosca (él y sus
  moscas). Mawlek espera a que estén los dos cerca para despertar. El Coliseo de Bronce se pasa entero, con todas sus
  oleadas y Zote, igual en los dos. Comprobados en vivo (0.15.2):
  - Cargador Musgoso: sale, embiste y daña a los dos (embestida y golpe agachado), cambia de PC sin cortar el combate
    y al morir queda vencido en los dos.
  - Gruzzer volátil: su cadáver explota al caer (`CorpseGoopExplode`, 1,7 s después de tocar el suelo) con
    `Gas Explosion M`, que sí daña al caballero en el juego base; en coop daña a los dos (2 de daño a cada uno), lo
    mate quien lo mate.
  - Coliseo de Plata entero con la prueba sin atajos: todas las oleadas y Oblobbles iguales en los dos hasta el final.
    La oleada del Grub Mimic espera a que alguien rompa su tarro con el aguijón (en la prueba lo hace el invitado); se
    rompe en los dos y la oleada sigue.
  - Shielded Fool, Sturdy Fool y Winged Fool dañan a los dos, también desde la marioneta.
  Falta por ver en vivo:
  - El Coliseo de Oro entero (Domadora de Dioses). Con la 0.16.1 los 137 enemigos del Oro recién cargado tienen el
    mismo identificador y sitio en los dos PCs; la vuelta entera está pendiente. La prueba automática no basta: hay
    oleadas que esperan a que el caballero se acerque a enemigos escondidos (magos, Mage Blob) y voladores que
    esquivan el tajo, así que conviene jugarlo a mano.
  - Que el arreglo de las zonas ("Hero Saver" de los Mage Blob, oleada 23 de Oro) desatasca la oleada en vivo.
  - Una espera en "WaveCheck 5" del Coliseo de Bronce en la prueba automática: un minero que sale de debajo de la tierra
    cuando el caballero se acerca, y en la prueba los caballeros no se mueven (jugando no debería pasar).
  Grimm, los jefes de sueño, Hollow Knight y la Radiancia pasan a M5. El sonido del minero del Coliseo ya suena en el
  PC que no lo mueve (comprobado en M5: tajo, lanzamiento del pico y voz al morir).
- **M4** (hecho, con los amuletos del compañero): sin reproducir aún la muerte del compañero (su animación ya se ve en
  su sprite, sin la cabeza ni el aguijón que salen despedidos), el destello parpadeante de invulnerabilidad, el aura de
  Defender's Crest mientras se lleva (humo y nubecillas al andar), Dreamshield, el polvo al correr y saltar, y los
  efectos azules de los golpes con vidas azules. Sharp Shadow no tiene efecto propio: su dash es el dash sombra y sus
  golpes a los enemigos se ven por `EnemyHitFx` (M3); comprobado en vivo contra un Zombie Runner de Crossroads_01,
  con los acompañantes atacándolo a la vez.
- **M5**: hecho, probado en vivo con dos copias:
  - Cinemáticas: los finales A, B y C, Mr. Mushroom, los créditos y las pantallas del final (el llevado ve el mismo
    vídeo a la vez), votación y vuelta al banco. Los vídeos de los finales y Mr. Mushroom no dejan saltar la primera
    vez (es del juego): la votación se probó en los créditos y en las pantallas del final.
  - Sueños completos (entrada juntos, caída y muerte de cada uno, victoria y salida de los dos): Campeón Fallido, Kin
    Perdido, Defensor Blanco (su mensaje del final espera a que cada uno pulse), Zote el Poderoso y el Rey de las
    Pesadillas. La entrada directa a la Radiancia sin el Corazón del Vacío.
  - Skins: POPKnight y Pale King, cada una en un PC, vistas en el otro (cuerpo y hechizos), el HUD propio y el envío
    sin tirones ni retrasos de golpes.
  - Jefes de M5 comprobados en vivo con dos copias (0.18.3):
    - Grimm: primer encuentro (los dos ven el diálogo; los dos quedan con `metGrimm` y Grimmchild), combate (espera
      a que entren los dos, diálogos leídos por los dos, "Call Child" sigue en los dos) y el Grimm vencido sale en los
      dos; quien no movía a Grimm habla con él y sube Grimmchild a nivel 3 (`killedGrimm` y el nivel en los dos).
    - Tirano de Almas: espera a los dos para despertar, primera fase, el suelo se rompe en los dos, segunda fase
      (también si cae solo quien no mueve al jefe), victoria y los dos salen tendidos junto al cuerpo.
    - Hollow Knight: entrando desde el Atrio, las cadenas rotas entre los dos (dos cada uno) empiezan el combate en
      los dos; las cuatro fases; el cadáver sale en los dos; absorber el vacío lleva a los dos al final A, los créditos
      y al banco del Atrio.
    - Radiancia: el desafío en su cartel empieza la presentación en los dos; combate, subida, golpe final (lo puede dar
      quien no la mueve), final C compartido, créditos y banco.
    - Hornet 1 y Mawlek siguen esperando a los dos.
  Sin probar en vivo, o solo en parte:
  - Radiancia por el camino del juego: tras la fase 4 del Hollow Knight con el Corazón del Vacío, Hornet lo ata; en la
    prueba la lógica del Hollow Knight se quedó en "H Thread" (el hilo de Hornet, pieza del jefe, apagado en el PC que
    lo movía) y no se pudo entrar golpeándolo con el aguijón onírico. La prueba entró directa al sueño (`goscene`).
    Además ese camino depende del Corazón del Vacío de quien mueve al Hollow Knight en la fase 4 (su lógica lo mira).
  - Radiancia y Hollow Knight: el jefe pasa al jugador más cercano también en sus secuencias finales; quien lo mueve en
    ese momento es quien debe pulsar ataque en los últimos golpes de la Radiancia (la lógica escucha su botón).
  - La entrada al Núcleo con `killedHollowKnight` ya puesto (la revisita) no se probó.
  - No probado en vivo: revivir con 3 o más jugadores y el revivido parpadeando (la invulnerabilidad se pone con el
    `StartInvulnerable` del juego).
  Jefes que dependen de cinemáticas o de entrar juntos:
  - **Grimm y el Rey de las Pesadillas**: antes hay que completar la misión de la Compañía (llamas, farol).
  - **Jefes de sueño** (Campeón Fallido, Tirano de Almas, Kin Perdido, Zote el Poderoso, guerreros soñadores): se
    entra con el aguijón onírico y cada jugador entraría por su cuenta; hay que llevar a los dos al sueño.
  - **Hollow Knight y la Radiancia**: cinemáticas de entrada y final.
- **M6** (hecho sobre 0.18.4): paquete, instalador, desinstalador y `LEEME.txt` con el nombre nuevo. Probado con
  `tools/manual-tests/installer-test.sh` en copias limpias (`test-instances/h` e `i`), sin tocar el juego de Steam.
  Falta: instalarlo en el PC de otro jugador desde el zip descargado y jugar por internet con el puerto abierto. La
  copia "limpia" de la prueba no tiene la `Assembly-CSharp.dll` original del juego (en este PC no hay ninguna sin la
  Modding API): usa otra versión parcheada en su lugar, que sirve para ver que el original se guarda y se devuelve.
- **Pasto y telas al paso del compañero** (solo estético, baja prioridad): hoy se sincroniza el corte, pero no el
  balanceo cuando el otro caballero pasa por encima. Sin red: en cada PC, cada pocos frames, buscar el pasto
  (`GrassBehaviour`) cercano al avatar del otro y darle el mismo empujón que da el héroe local, con la velocidad del
  avatar. No poner la etiqueta `Player` al avatar: muchas cosas del juego reaccionan a ella (puertas entre salas, zonas
  de cámara, NPCs, rangos de alerta de enemigos).
