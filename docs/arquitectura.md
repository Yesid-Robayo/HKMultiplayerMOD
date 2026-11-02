# Arquitectura de HKMultiplayer

Mod cooperativo de hasta 4 jugadores para Hollow Knight 1.5.12620 (Unity 6), sobre la Modding API. Todo el núcleo es
propio. Objetivos de diseño, por orden:

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
│ Replicación: jugadores, mundo (guardado), enemigos, efectos   │  Game/Players, World, Scene
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
    confirmó y nunca se reenvía. Si no, el receptor se quedaría esperando ese mensaje para siempre y todo lo fiable de
    esa conexión dejaría de llegar. En el host, un fallo al procesarlo tampoco impide reenviarlo a los demás.
  - *Datos grandes*: sobre el canal fiable, troceados con ventana (partida al unirse, skins).
- **Serialización**: `PacketWriter` / `PacketReader` escriben y leen directamente en el búfer (little-endian, enteros
  variables, cadenas UTF-8 sin arrays intermedios). Los mensajes son *structs* con `Write`/`Read`.
- **Cifrado**:
  - Al conectar, intercambio de claves **X25519** (efímeras, secreto hacia adelante).
  - Claves de sesión con **HKDF-SHA256** mezclando el secreto compartido y el **código de partida**: sin el código no
    se puede derivar la clave, así que nadie sin él puede unirse ni hacerse pasar por el host. El código entra estirado
    con **PBKDF2-HMAC-SHA256** de 100.000 vueltas (`KeyExchange.Stretch`, una vez por código y en otro hilo): quien
    capture un saludo no puede probar códigos a millones por segundo (protocolo 2).
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

### Replicación (`Game/Players/`, `Game/World/`, `Game/Scene/`)

- **Ritmo fijo** (60 Hz por defecto, adaptable si la conexión se congestiona). Cada tick se toma una "foto" del estado
  local y se envía solo lo que cambió.
- **Jugadores**: posición, orientación, animación (clip y fotograma), sala, vida. En los demás juegos se dibujan con
  un búfer de interpolación (pequeño retraso adaptativo y extrapolación corta si un paquete se retrasa). Un salto
  (más de 4 unidades entre dos estados) no se interpola: se dibuja el de antes hasta su momento y luego el nuevo, aunque
  no llegue ninguno más (un Mantis Lord que se va y se esconde deja de enviar, porque no cambia).
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
    Cristal, Lágrima de Isma, Marca del Rey, aguijón onírico y su mejora, portal de sueño, hechizos y su nivel): cada uno
    consigue las suyas, con su objeto y su escena. Todo lo que da una habilidad mira si este jugador ya la tiene (el
    objeto brillante de la Capa de Polilla se destruye, la Vidente no da el aguijón, el chamán no da el hechizo). Un
    personaje guardado sin esos campos (versión anterior) se queda con los del mundo (`WasShared`: lo que ya tenía,
    porque se compartía) y no con los de una partida nueva. Algunos personajes también miran una habilidad
    (Quirrel se va del santuario de Sendero Verde y de la Estación de la Reina con la Garra de Mantis): eso pasa ahora
    para cada uno según la suya.
  - **Introducción para un invitado nuevo** (`CoopSaves`): el invitado entra al mundo del host como quien continúa una
    partida, y el juego solo pone la introducción (el poema y la llegada a Hallownest, `Opening_Sequence`) en una
    partida nueva. Si su personaje no tiene tiempo jugado, `ContinueGame` se cambia por `RunStartNewGame`; la
    introducción hace lo del juego (se salta igual) pero al acabar no lleva al Paso del Rey: vuelve a `ContinueGame`,
    que entra donde reaparece el host, como cualquier invitado.
  - **Progreso compartido en directo** (`SharedProgress`): los campos del mundo se comparan cada 0,2 s con una copia
    (lectores compilados, sin memoria por lectura) y los cambios van al host, que los aplica y reenvía. Los objetos de
    sala (paredes, palancas, piedras de geo) se envían al guardarse y nunca retroceden.
  - Lo que pasa con el Forjaguijones tras la última mejora (perdonarlo, matarlo, la cabaña de Sheo) es del personaje,
    como sus mejoras: si fuera del mundo, en cuanto uno lo perdonara se iría y el otro no podría terminar las suyas.
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
      cuenta como "solo enseña"), así que sale en cuanto otro jugador abre la estación. Así, en el otro PC, Mr. Mushroom
      se va en cuanto uno le habla (su `Control` compara `mrMushroomState` con su sitio), Bretta y Quirrel se van y el
      farol de la Pesadilla aparece.
    - En el registro queda cada personaje que su comprobación quita al cargar la sala, con el dato, su valor y si es del
      mundo o del jugador ("Quirrel Slug Shrine/deactivate: el personaje se quita en esta sala por hasWalljump=True (dato
      de este jugador)"): si a uno le sale Quirrel y al otro no, ahí se ve el motivo.
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
      vez con un personaje que se queda sigue avanzando su diálogo).
    - En el PC de quien habla el personaje hace lo del juego (Bretta o Tiso siguen ahí hasta volver a entrar). En el
      otro, si el dato es un sí/no y quien habló está en la misma sala, también se queda hasta volver a entrar (Bretta no
      desaparece delante del otro a mitad de la conversación). Si quien lo cambió está en otra sala, o es un
      número (Mr. Mushroom: hablar con la copia que se quedara avanzaría la misión dos veces), se va enseguida, como si
      se cargara la sala.
- **Enemigos y objetos de sala**:
  - Cada enemigo lo mueve el PC del jugador más cercano, porque su lógica solo ataca al caballero de su PC. Lo reparte
    el dueño de la sala; cambia solo si el otro está claramente más cerca (4 unidades) y hace un rato del último cambio
    (5 s; 8 s en los jefes, porque cada cambio corta su ataque). Ese PC lo hace funcionar normal y envía su estado
    (transform, animación, profundidad, activo, vida, daño al tocarlo) con detección de cambios sin crear objetos.
  - Un jefe (150 de vida o más) solo cambia de PC entre dos ataques: cuando su lógica principal (la FSM de su objeto con
    más estados que tenga alguno de reposo) está en un estado de reposo, cuyo nombre lleva `Idle`, `Choice`, `Choose`,
    `Decide` o `Select`, o salió de uno hace menos de 0,15 s (los de elegir ataque duran cero fotogramas). El PC que lo
    mueve lo envía en el mensaje de estados (bit 4 de las banderas) y el dueño, si quería cambiarlo, lo cambia en cuanto
    llega, sin esperar al siguiente reparto. Si no reposa en 4 s, cambia igualmente; si quien lo mueve está caído o en
    pausa, enseguida. Así no cambia a mitad de un ataque: el nuevo PC lo retomaría desde el principio de ese estado
    hacia su caballero y el ataque se cortaría.
  - Si quien lo mueve está fuera de su zona de alerta (la variable `Alert Range` de su lógica, o un hijo "Alert
    Range...", ampliada con el tamaño de la caja del caballero) y otro jugador está dentro, pasa a ese: su lógica solo
    ataca a un caballero dentro de la zona (si no, el Rey Vengamosca se iría a esperar a su sitio, `Lurk`, siguiendo al
    más cercano mientras el otro pelea dentro). Con los dos dentro, el otro tiene que estar 8 más cerca (no 4) para
    quitárselo: el Rey vuela a 13 de su caballero y, con 4, pasaría de uno a otro sin parar.
  - El traspaso lleva también lo que llevaba esperando su estado (`Wait` o `WaitRandom`), y al tomarlo la espera sigue
    por ahí (si no, cada cambio de PC empezaría de cero los 3-4 s del Rey Vengamosca antes de atacar).
  - El Moss Charger sale de la tierra a 14 del caballero de su PC y ya al salir daña: si ese punto cae encima de otro
    jugador, sale por el otro lado de su caballero (girado y embistiendo hacia el otro lado), si ese lado está en su
    zona y libre.
  - No cambia de PC mientras va rápido (más de 9 unidades/s entre dos repartos: embistiendo o lanzándose). El Moss
    Charger embiste a 15 y cruza de un jugador al otro: si pasara al otro PC a mitad de la embestida, su lógica seguiría
    allí con ese caballero y daría la vuelta. Tampoco con su muerte en marcha (`Dying`: su lógica atendió `ZERO HP` en
    este PC, o el PC que lo mueve avisó de que allí ya murió), mientras quien lo mueve siga en la sala. La muerte del
    Falso Caballero va por su cabeza y su cuerpo nunca queda muerto en su `HealthManager`: si su cuerpo pasara al otro
    PC a mitad de su muerte (`Cough`), seguiría dañando y las rejas no se abrirían.
  - Al tomarlo, su lógica sigue desde el estado en que iba (`SetState`), pero sin las acciones de ese estado que crean o
    lanzan objetos: ya las hizo el PC que lo movía y sus disparos siguen en el aire. Si se repitieran, cada cambio de PC
    a mitad de un ataque sería un ataque doble.
  - Los de un caído o de quien está en pausa pasan enseguida a otro, también los apagados: en una marioneta, apagado
    es solo lo último que envió quien la movía. Además el dueño repite su reparto entero a la sala cada 2 s (y 1,5 s
    después de que entre alguien), así quien recarga la sala o se pierde un cambio de dueño vuelve al mismo reparto.
  - En los demás juegos el enemigo es una "marioneta": su lógica (FSM, física) se pausa (no se reinicia) y se dibuja
    con el estado recibido e interpolación; sus colisiones de daño siguen funcionando para el jugador local. Quien
    entra en la sala recibe del dueño la vida de cada enemigo: se aplica aunque el enemigo aún no sea marioneta (lo
    pasa a ser un poco después de cargar la sala); solo se ignora la vida de los que mueve este PC.
  - Traspaso: el que lo suelta envía cómo iba (estado, números, textos y objetos de cada FSM, velocidad, gravedad y tipo de
    cuerpo) y el nuevo lo sigue desde ahí. Los objetos van como "su caballero" (en quien lo recibe, el suyo), "una
    pieza del enemigo" o "un objeto de la sala" por su ruta, y solo rellenan las variables vacías: la marioneta no pasó
    por los estados que las guardan (el escupidor guarda al caballero al alertarse). Los textos también: los Señores
    Mantis de la segunda fase eligen pared y guardan hacia dónde lanzar (`Shot Event`, "WIDE L"...) antes de lanzar, y
    retomados entre medias lanzarían con el texto de su lanzamiento anterior. También al salir de la sala o morir (aún
    dentro, al empezar el cambio de sala): un jefe no repite su presentación.
  - Los golpes del jugador local a una marioneta van al PC que la mueve, que los aplica al enemigo real. Cualquier
    aviso de fuera que llegue a una marioneta (el de daño del ataque, la zona de la arena en la que entró el caballero,
    la escena de batalla) también va al PC que la mueve: con esos avisos despiertan o reaccionan muchos jefes. Los
    avisos a todos de un objeto de la sala no llegan a la lógica parada de una marioneta: si en su estado de ahora
    esperaba uno, también se reenvía (la zona de abajo del Tirano de Almas, `PHASE 2`). Y un aguijonazo del jugador
    local a una marioneta cuya lógica esperaba un aguijonazo en su caja (`Trigger2dEvent` con "Nail Attack", el
    `FINAL HIT` de la Radiancia) manda ese aviso al PC que la mueve. El aviso lleva a qué PC va (el que la mueve
    según quien lo manda) y solo lo atiende ese. Si al llegar ya no la mueve (el cambio de PC se cruzó con el aviso),
    lo pasa al que la mueve ahora; si es él mismo quien está a punto de tomarla, lo guarda y lo atiende al tomarla
    (como mucho 3 s): así no se pierde, por ejemplo, el `BATTLE START` que despierta al Campeón Fallido. Cada golpe que
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
    ese aviso no se reenvía: si llegara al PC que acaba de tomar el enemigo, su lógica viva lo atendería y el Falso
    Caballero (y cualquier enemigo con `DISABLE` en sus transiciones) se quedaría parado tras cada cambio de quién lo
    mueve. Un enemigo que cuelga de otro (la cabeza del Falso Caballero, `Head`, con vida propia) lo mueve siempre el
    mismo PC que a su padre: así, al aturdirse el jefe, su cabeza se puede golpear en el PC que lo mueve. Al tomar un
    enemigo, si una variable de objeto apuntaba a algo que su lógica creó en el otro PC (el vapor de la muerte del Falso
    Caballero, `Death Smoke`, con `CreateObject`), se crea aquí con esa misma acción (`RecreateStored`). Y la marioneta
    repite también los `EaseFloat` con aviso al acabar, sin mandar el aviso (`WithoutFinishEvent`), así su vapor baja
    a 0. Las FSM de daño (`damages_enemy`, `damages_hero`) no se siguen: solo pasan el golpe al objeto que tocan, y
    seguirlas enviaría su `Send Event` en cada contacto sin hacer nada.
  - Un efecto continuo de una marioneta se acaba si el enemigo muere por la vía normal o se apaga: ya no llegan más
    estados que lo acaben (salvo una muerte que va por su lógica, `Dying`).
  - Las FSM de los hijos que solo animan su propio objeto (sus acciones son esperas, animación, partículas, sonido,
    encenderse o apagarse a sí mismo, moverse o girarse: el `Spit Effect` del Gulka y del Elder Baldur) siguen
    funcionando en la marioneta. Paradas con las demás, el efecto que enciende la marioneta no se apagaría nunca.
  - Los efectos que su lógica repite cada frame mientras dura el estado también siguen en la marioneta
    (`EnemySync.UpdateRunningEffects`): partículas con `everyFrame`, salpicaduras continuas (`SpawnBloodTime`,
    `SpawnRandomObjectsOverTime`) y, si el estado tiene alguno, las cuentas con sus números que usan (`FloatAdd`,
    `FloatClamp`, `EaseFloat` sin aviso al acabar...). Se apuntan por estado al encontrar el enemigo, se repiten
    (`OnUpdate`) hasta que llega otro estado de esa FSM (el PC que lo mueve avisa también al salir de un estado así,
    aunque el siguiente no tenga efectos), y se acaban si pasa a moverlo este PC o deja de seguirse. Así el vapor del
    Falso Caballero y del Campeón Fallido al morir (su ritmo sube 5 cada frame) sale igual en el PC que no lo mueve, con
    su salpicadura continua.
  - Un objeto con dos `HealthManager` (la segunda fase del Tirano de Almas del sueño) es un solo enemigo, el primero.
  - Los objetos de la arena que su lógica enciende y apaga se sincronizan además como estado (al cambiar, y todos a
    quien entra en la sala): no dependen de haber visto el efecto en directo.
  - Ascensores y rejas: los mueve quien los usa (su controlador). Las copias de los demás no avanzan solas: solo pasan
    por los estados que llegan, una vez cada uno, aplicados dentro de la ejecución de su FSM. Si no, los estados con
    efectos fuera del objeto (el ascensor voltea la dirección de su palanca al llegar) se harían dos veces. Solo
    "usarlo" da el control: un aviso que le cambia el estado (no los avisos a todos que no le hacen nada, como el título
    de la zona), o, en los que arrancan al pisarlos, su propio choque con el caballero de este PC encima. Son todos los
    que tienen la FSM `Lift Control`: los `Ruins Lift` de la Ciudad, el de Bocasucia a la Cumbre de Cristal y el de la
    estación del ciervo de Bocasucia. Si el controlador se va de la sala (p. ej. muere), quien se queda pasa a serlo;
    quien entra o vuelve espera a oír al controlador antes de tomarlo, y si su objeto está en otro estado se pone al día
    (una reja cerrada allí se cierra aquí con su animación y su colisión).
  - Rejas de batalla y de jefes: si una se cierra con un jugador dentro, el otro puede entrar a ayudar pero no salir
    hasta que se abra. Las que ya están cerradas al cargar la sala no se cruzan. Son también rejas las puertas de la
    arena de los sueños de jefe (`Dream Gate`/`Control`, y `Dream Gate Phase 2`/`Control2` del Tirano de Almas): las
    cierra y abre el jefe (`DREAM GATE CLOSE`/`OPEN`, `PHASE 2`) en el PC que lo mueve, y se cierran igual en todos.
    Lo que manda un enemigo que mueve este PC a un objeto con controlador cuenta aunque llegue mientras se aplica la
    cadena copiada de otro objeto (la arena copiada despierta aquí al jefe y este cierra las puertas).
  - Controladores de batalla que no son enemigos (Señores Mantis: los tronos, `Mantis Battle`, `Battle Main` y
    `Battle Sub`, que coordina a la pareja; Coliseo de los Tontos: su gestor, sus oleadas y sus jaulas, algunas con
    esperas al azar): igual que ascensores y rejas, los lleva un PC y los demás siguen sus estados. Quien acepta el
    desafío pasa a controlarlos, y el combate (jaula, suelo con pinchos, qué señores salen, oleadas, final) avanza
    igual en todos. Lo que mueven con avisos a todos (jaula, suelos, puertas) va igual que ellos, y quien entra a mitad
    del combate se pone al día con su estado. En las copias no se hacen las llamadas al caballero (devolverle el control,
    girarlo), salvo la que fija dónde reaparece tras los pinchos (el Coliseo la pone en una plataforma antes de sacarlos;
    sin ella, el caballero del otro PC reaparecería en la entrada de la sala). El cartel para desafiar se apaga en los
    demás PCs (si no, se podría desafiar otra vez a mitad del combate y ese caballero se quedaría sin control), pero
    solo al llegar un estado de un controlador de batalla al que solo se llega con el aviso del desafío
    (`MLORD CHALLENGE`: los tronos levantándose). No basta cualquier cadena de otro PC (una palanca de la Aldea Mantis)
    ni cualquier estado de un controlador de batalla (la reja de Deepnest se cierra cuando alguien entra, los tronos al
    pasar el control): así, tras morir contra los Señores Mantis, al volver se los puede desafiar. Las órdenes de esos
    controladores a un enemigo no se reenvían: ya las da su copia en el PC que lo mueve. Los avisos de una copia a otro
    objeto con controlador (p. ej. el `SPAWN` con 1 s de retraso que la batalla del Coliseo manda a sus jaulas, que
    llega fuera de la aplicación del estado) no toman el control: ya los manda el controlador. Si no, los dos PCs se
    quitarían las jaulas, alguna no soltaría su enemigo y la oleada esperaría para siempre (`Battle Enemies` no
    llegaría a 0). El Coliseo tiene más de cien objetos con controlador: si un PC los toma todos a la vez (el host se va
    y queda el invitado), sus posiciones van en varios mensajes, porque no caben en uno. Un mensaje que falla a medias
    se descarta (también antes del aviso de cambio de sala del invitado y del de desconexión), y un fallo de la
    sincronización de la sala no impide enviar los paquetes del frame.
  - El tarro del Grub Mimic (oleada del Coliseo de Plata; lo crea su jaula y no es guardable) se copia como una
    palanca: quien lo rompe con el aguijón lo rompe en todos y el bicho sale del tarro roto en los dos. Es un golpe, no
    una zona: no espera a que todos estén cerca. Sus FSM, como las de lo que crean las jaulas en la raíz de la sala, se
    identifican por su sitio (se llaman igual y tienen el mismo puesto entre hermanos).
  - Objetos de misión que se golpean con el aguijón (`Trigger2dEvent` con "Nail Attack" en un estado que sale con ese
    aviso) y cambian un dato del mundo también se copian como una palanca: Zote atrapado en Nido Profundo se descuelga en
    los dos (y cada uno habla con su Zote) y el farol de la Pesadilla se aviva con los golpes de los dos (cada golpe
    suma y se apaga solo) y se enciende a la vez en los dos. Un golpe de este jugador desde el reposo cuenta aunque
    acabe de llegar la cadena de otro PC.
  - La campana de las estaciones del ciervo (`Bell Control` de `Station Bell`) también se copia como una palanca: su
    aviso `STAG BELL RUNG` hace llegar el ciervo en todos los PCs de la sala. Cada uno se sube y viaja por su cuenta.
  - Encuentros con diálogo antes de un combate (Hornet en los Bordes del Reino; se detectan solos: un objeto que no
    es enemigo, con un diálogo, que después despierta o enciende a un enemigo o cierra la arena): van como los
    controladores de batalla, así que los dos ven el diálogo, con su caballero quieto mientras, y la Hornet que habla
    desaparece en los dos al empezar. Cada diálogo espera a que todos los de la sala lo terminen de leer (si alguno no
    responde en 30 s, sigue). Los personajes que solo hablan no cuentan y siguen siendo de cada uno. Empezado en otro
    PC, lo que le mandan otros objetos de este (el Grimmchild que llega a Grimm con `ABSORB END`) no lo hace avanzar ni
    toma el control: si no, los dos se quitarían el control y Grimm se quedaría en "Call Child". La copia sí pone los
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
    no, el primero en cruzarla saldría de ella enseguida, la espera se olvidaría y el combate no empezaría nunca. Si
    todos están ya al mismo lado de la línea, cuenta como dentro (quien entra quieto o mirando atrás deja el lado al
    revés).
  - Jefes que se despiertan al ver al caballero (Hornet 1, Guardián de Cristal...): se apunta en qué estado duerme su
    lógica principal y su primer cambio desde ahí es despertar. Si algún jugador de la sala está a más de 25 de él,
    ese cambio se retiene (el mismo aviso) y el jefe es invulnerable mientras: golpearlo no lo despierta ni le quita
    vida. Despierta en cuanto están todos cerca, sin límite de tiempo (también espera a quien está en pausa); no se
    espera a quien está caído, en otra sala o sin avatar en este PC. Si el jefe cambia de PC mientras espera, el
    traspaso lleva el despertar pendiente y el nuevo sigue esperando. No esperan los de una arena que ya espera a que
    entren todos (su `HealthManager` tiene `battleScene` y esa arena es una zona con FSM o la lleva un controlador
    compartido, como el Coliseo; Oblobble entra volando desde muy arriba y, si esperara, se quedaría invulnerable para
    siempre) ni los que crea una jaula. La arena de Mawlek no tiene zona propia (solo las de la cámara): Mawlek sí espera. Tampoco se retiene
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
    (`HazardSync`) y los demás se ajustan si se desvían más de un 3 % de vuelta.
  - Objetos guardables (paredes, palancas, cofres...): el directo (golpe, animación, sonido) lo lleva solo la copia
    de su cadena (`FsmStateSync`). El aviso de "activado" de `SceneObjectSync` no mueve la FSM mientras esa cadena va
    en marcha: se comprueba al acabar y solo se activa si no llegó (como mucho 10 s después). Lo activado por una
    cadena llegada de otro PC no se vuelve a avisar. La línea base de cada objeto se toma después de
    `PersistentBoolItem.Start` (el juego pone ahí lo guardado; en los inactivos llega tarde), y lo que llega de otro PC
    antes de ese `Start` se pone justo después. Qué guardables ya pasaron su `Start` se apunta con el gancho de
    `PersistentBoolItem.Start` (o si se vieron encendidos en un frame anterior), y con eso el que recibe la activación
    pone `Activated`. El reposo de una cadena se toma cuando el objeto lleva 1 s quieto: si no, el fin de su inicio al
    cargar la sala (`Pause` → `Idle`) parecería un golpe y se copiaría a los demás.
  - Las máscaras que tapan o destapan parte de la sala según dónde está el caballero local (`remasker`,
    `remasker_inverse`) son guardables y tienen `Activated`, pero son de cada PC: no se copian (`FsmStateSync.IsViewMask`).
  - Rompibles con FSM propia (la plantilla genérica del juego: recibe `TAKE DAMAGE`, tiene `Break` y la variable
    `Activated`, sin componente rompible ni guardable, como el letrero de la bajada a Cruces Olvidados): se copia su
    cadena como la de una palanca. Los rompibles con componente (`Breakable`, `BreakableObject`, `BreakablePole`,
    `BreakablePoleSimple`, `BreakableInfectedVine`, `VinePlatformCut`, `GlobControl`, `GrassCut`, `Grass`,
    `GrassSpriteBehaviour`, `HealthCocoon`) van por `BreakableSync`, salvo los del pool del juego (guardados en él o
    sacados de él, como las gotas `Orange Glob` de los Bordes del Reino, que vuelven solas al pool): su ruta no es la
    misma en cada PC ni de una vez a otra, así que romper uno aquí rompería otro distinto (o ninguno) en el otro PC. Son
    decoración y cada PC tiene los suyos. `Grass` (recibe el golpe como un enemigo) y `GrassSpriteBehaviour` (las
    lianas y matas del Sendero Verde, que solo se cortan a la vista: se pone a la vista antes de cortarlo) también se
    siguen: lo cortado por uno se corta para el otro. El capullo de savia vital (`HealthCocoon`, de código y guardable,
    sin FSM: `SceneObjectSync` solo sigue los que tienen una) se rompe igual en los demás y suelta allí sus mariposas:
    cada jugador recoge su savia, como el geo. El mensaje dice si es el estado para quien entra: entonces el capullo se
    pone roto sin soltar nada (si no, se ganaría savia al entrar). Un `GrassCut` lejos de la cámara de este PC no tiene
    el material con el que colorea sus partículas (su `GrassBehaviour` lo borra al apagarse): el corte que llega de otro
    PC lo corta sin colorearlas.
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
    visual y corre solo, y sin esa velocidad se quedaría quieto en el aire, dañando. Así una onda solo visual del Falso
    Caballero, que recibe su velocidad y dirección con `SetFsmFloat`/`SetFsmBool` justo después de salir, avanza igual
    en cada PC, echa sus chorros (que dañan al caballero de ese PC) y se acaba en la pared. Lo creado en combate no tiene
    tiempo de preparación: sus efectos (los barriles al romperse) cuentan desde que sale.
  - La copia de un proyectil se quita si su creador tiene el juego en pausa o si en 3 s no llega nada de él
    (`CopyAbandoned`): los enemigos de quien pausa pasan a otro PC, pero lo que disparó sigue con él y su copia se
    quedaría quieta, dañando.
  - El aviso lleva también el nombre del prefab: si la acción lo toma de una variable que ponen estados anteriores (el
    Elder Baldur elige en `Goop` o `Roller` qué escupe y `Fire` saca `{Projectile}` del pool), en la marioneta esa
    variable está vacía, así que se busca por su nombre entre las variables y los `SetGameObject` de su lógica.
  - Lo que lanzan las acciones `FlingObjectsFromGlobalPool` y `…Vel` y hace daño (el vómito de los Ogros Hongo) también
    va como copia del original: se sigue lo que sacan del pool mientras se ejecutan. Lo solo visual lo lanza cada PC.
  - Devolver de un aguijonazo lo que crean (el escupitajo del Gulka, `Spike Ball`): la copia no tiene vida, así que un
    `NailCatcher` en su caja avisa del aguijonazo; como nace parada en su estado inicial, se busca la espera del golpe
    (`Trigger2dEvent` o `Trigger2dEventLayer` con "Nail Attack") en todos sus estados. El PC que la mueve le pone como
    objeto que la golpeó un aguijón de referencia (apagado, con una FSM `damages_enemy`) con la dirección del golpe,
    deducida de dónde estaba ese caballero: su lógica la devuelve hacia ahí.
  - Los tarros del Coleccionista sueltan su bicho con su propio código. Solo lo suelta el tarro original: el PC que
    lo tiene lo sigue como algo creado en combate (se reconoce al encenderse junto a su tarro) y los demás crean una
    copia que lo sigue; las copias del tarro van vacías y se rompen sin soltar nada. Una copia que acaba sola no borra
    el original, salvo un disparo: si su copia choca (con el caballero de ese PC o una pared), el original también.
  - Lo creado en combate se numera con el jugador que lo creó: cada PC cuenta por su cuenta y, sin el jugador, dos
    podrían dar el mismo número.
  - Enemigos de jaula (las crías `Hiveling Spawner` de la Colmena, plantilla de los Hatcher: su FSM tiene la variable
    `Cage` y sale de la jaula con `SPAWN`): esperan fuera de la vista en su jaula y al morir vuelven a ella con la vida
    repuesta; el `Zombie Hive` saca uno cualquiera (`GetRandomChild`). No son enemigos de la sala: los saca solo el PC
    que mueve al que los suelta (`SendEventByName` con `SPAWN` desde un enemigo real), que lo sigue como algo creado en
    combate (`EnemyRelease`, con la ruta del enemigo dentro de su jaula al cargar la sala). Los demás sacan ese mismo de
    su jaula (sale, se suelta de ella y se para como marioneta). La marioneta del que los suelta no repite ese `SPAWN`.
    Al morir, el original vuelve a su jaula y desaparece en todos; la copia muere a la vez y vuelve a la suya.
  - Cadáveres: tras morir, el PC donde murió de verdad sigue enviando la posición de su cadáver unos segundos (sale
    despedido con física, y algunos explotan, como el Gruzzer volátil): cae y explota en el mismo sitio en todos.
  - Nombre del jefe al despertar: la marioneta hace también lo que le dice al título de zona qué nombre poner.
  - Enemigos creados por objetos de la sala que no son enemigos (las jaulas del Coliseo crean el suyo al prepararse):
    cada PC los crea igual. Si se crean después de repasar la sala, se siguen desde ese momento con un identificador
    sacado de quién los creó. Los clonados en la raíz de la sala (mismo nombre y mismo puesto entre hermanos) se
    identifican por la FSM que los apunta (la variable "Enemy" de su jaula; si los apuntan varias, la menor), y si no
    los apunta ninguna, por su sitio al cargarla. Por su sitio no basta: en Oro hay jaulas de oleadas distintas en el
    mismo punto, y sus enemigos podrían repartirse los identificadores en distinto orden en cada PC. No quedan muertos
    para siempre (el Coliseo se repite).
  - Zonas del enemigo que apuntan si el caballero está dentro (una FSM hija que al entrar y salir pone una variable
    de otra, como el "Hero Saver" de los Mage Blob, que no salen con el caballero donde aparecerían): al pasar a
    mover el enemigo se vuelven a poner según el caballero de ese PC. Lo que llega del anterior es su caballero; si
    no, el Mage Blob heredaría "dentro", aquí nunca llegaría la salida y la oleada esperaría para siempre.
  - Al pasar a mover un enemigo, queda como lo tenía el PC que lo movía: encendido o apagado, si se dibuja, si choca,
    si es invencible y cuánto daña (va en lo que envía al soltarlo; si no llega, lo último que se supo de él). La
    marioneta copia todo eso con el retraso de dibujo y la lógica del enemigo solo lo cambia en ciertos estados: la
    jaula del Coliseo enciende su enemigo en cada PC al abrirse y justo después la marioneta podría volver a apagarlo
    con un estado de antes de salir; retomado en ese momento, quedaría apagado y vivo en todos los PCs y la oleada no
    acabaría. Igual con la caja del Electric Mage, que solo se enciende al despertar. Y si allí su lógica ya lo había
    soltado de su padre (los Mage del Coliseo cuelgan de su oleada hasta despertar y luego se teletransportan con
    posiciones locales), aquí también se suelta: si no, se teletransportaría fuera de la arena.
  - El host recuerda los enemigos muertos de cada sala (aunque se quede vacía) hasta que alguien descansa en un banco
    o muere: es cuando el juego hace que vuelvan (`GameManager.ResetSemiPersistentItems`, enganchado; lo llaman el banco
    y `PlayerDead`). Solo los que el juego deja muertos hasta el banco (llevan `PersistentBoolItem`): los jefes de arena
    no lo llevan y vuelven cada vez que se carga la sala. Con alguien en la sala, quien entra sabe qué murió por la foto
    del dueño de la sala, no por esta memoria.
  - Si descansa uno solo, los enemigos no vuelven para nadie: los datos de la sala del otro (semipersistentes, de cada
    PC) los siguen marcando muertos y al cargarla vuelven a morir. Los dos PCs coinciden; para que vuelvan, descansan
    (o mueren) los dos. Decidido así a propósito: si el descanso de uno los devolviera también en el otro, le
    aparecerían enemigos delante sin haber hecho nada, en mitad de su camino o de un combate en otra sala.
  - Un enemigo destruido durante un cambio de sala (`GameManager.IsInSceneTransition`) no cuenta como muerto: la sala
    se está descargando. Al recargar la misma sala ("Recargar sala", "Ir con" a la sala en la que ya se está) su nombre
    no cambia hasta que la nueva está lista: si contara, todos sus enemigos se darían por muertos hasta el banco. Igual
    los rompibles que se cortan (destruidos no son cortados).
  - Los que andan con una corrutina (`Climber`, los Tiktik; `Crawler`; la acción `WalkLeftRight` de los Reptantes)
    siguen andando con el componente o la FSM apagados (en la marioneta girarían, se voltearían y se moverían solos, y
    el `Climber` la devolvería a donde estaba si se mueve más de 0,1 en un frame). Al hacerse marioneta se paran sus
    corrutinas; al retomarlo, `Climber` y `Crawler` repiten su `Start` (después de poner la velocidad del traspaso: el
    `Climber` solo la pone al echar a andar y al girar) y `WalkLeftRight` vuelve a entrar en su estado.
  - Un `Climber` sin suelo a 1 unidad gira 90° y lo vuelve a mirar: en el aire no acaba nunca y gira sobre sí mismo con
    su rayo encendido, sin volver a su pared (los Crystallised Lazer Bug de `Mines_20`). Retomado cerca de una esquina
    se podría soltar: el giro de una marioneta llega a saltos y su posición interpolada, `Turn` se corta con el ángulo
    a medias y `previousTurnPos`, que evita girar otra vez en el mismo sitio, es de cada PC. `PlaceClimber` redondea el
    giro, lo apoya en la pared más cercana si no tiene suelo y apunta ahí su último giro. Por si se pierde igualmente,
    `UpdateClimbers` lo devuelve a la pared tras 1 s sin suelo.
  - En las pruebas del Coliseo (`Room_Colosseum_*`) ningún enemigo queda muerto hasta el banco, tampoco los de la
    sala (los Mage de las oleadas): cada prueba empieza de cero, y al repetirla sin descansar una oleada esperaría a un
    enemigo que el host da por muerto.
  - Piezas que se mueven aparte (hijos con animación o que dañan, como la aguja de Hornet): posición, profundidad,
    giro, escala, si están activas y si se dibujan, si su caja de daño está encendida, cuánto dañan y animación vienen
    del PC que mueve el enemigo. También las de fuera del enemigo que apuntan las variables de sus FSM (el rayo del
    Guardián de Cristal, que suelta al prepararse): se identifican por esa variable, no por su sitio en la sala.
  - Trampas de la sala que el enemigo dispara con un aviso a todos (las torretas del techo del Guardián con
    `LASER SHOOT`): sus FSM pasan a ser del enemigo, así que solo funcionan en el PC que lo mueve; en los demás se
    paran y su giro, rayos, sonidos y animaciones se copian. Solo las que dañan (las puertas de la arena van aparte) y
    de la misma sala (la sala y su escena `_boss`). Al morir el enemigo vuelven a ser de cada PC y sus rayos se apagan.
    Igual lo que el enemigo avisa directamente y crea cosas que dañan (el invocador de barriles del Falso Caballero,
    `SUMMON`): solo lo usa el PC que mueve al jefe y los barriles llegan como copias.
  - La sombra de cada jugador no es un enemigo compartido.
  - El contador del diario (`killsMegaBeamMiner`, que la sala del enfurecido usa para darlo por vencido) baja una sola vez.
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
      suelo y el aterrizaje fuerte.
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

### Interfaz (`UI/`)

- **Menú principal → Multijugador**: copia de los menús del juego (botón de "Iniciar partida", pantalla de "Extras").
  Nombre y puerto; hostear (mundos coop, borrar con confirmación, nuevo) y unirse (dirección y código).
- **Pausa → Multijugador** (solo en partida multijugador): mundo, código, IP oculta hasta pedirla, jugadores con su
  ping y cerrar o salir de la partida. Páginas *Ajustes de la partida* (dos, con filas "Opción < valor >" copiadas de
  las opciones del juego: `GameStyledMenu.AddOption`, que el invitado ve sin poder cambiar; la primera con dificultad
  y revivir, la segunda con pantalla y permisos) y *Compañeros* (vida y alma de cada uno y dónde está, dar geo, ir con
  él, desatascarse y recargar la sala; lo que el host no permite sale apagado con `SetButtonEnabled`). Las acciones
  que se hacen al quitar la pausa vuelven al menú de pausa y lo cierran en cuanto el juego deja quitarla
  (`UIManager.ignoreUnpause`, mientras el menú termina de aparecer), no tras una espera fija.
- Avisos en pantalla con la letra del juego (código al abrir la partida, quién entra o sale).

### Funciones del coop (`Game/Features/`)

Resumen en [Comportamiento de las funciones](#comportamiento-de-las-funciones). Cada función es una clase con
`Attach`/`Detach` que engancha `MultiplayerRunner` como las demás capas; sus ganchos al juego se ponen al cargar el
mod, antes de que empiece la partida. Los mensajes nuevos van del 73 al 117 (el 98, el 108 y el 115 son de
`EnemySync`, el 104, el 105 y el 107 de `HazardSync`, el 106 de `StagTogether`, el 109 y el 110 de `SceneObjectSync`,
el 111 y el 117 de `Revive`, el 112 de `PartyAbilities`, el 113 y el 114 de `PantheonTogether` y el 116 de
`MenderBugSync`). La interfaz es uGUI (`UI/HudCanvas`): un lienzo que sigue la zona de la cámara del juego (16:9; con
otra proporción el juego deja franjas) y escala con su alto como el HUD del juego; las piezas se crean una vez y cada
frame solo se mueven o se encienden, y los textos se rehacen solo al cambiar.

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
  multiplicador que da justo el entero: con el aguijón de 5, 1,5× quita 3, 3 y 4, y 2× quita 2 y 3. El daño recibido se
  escala en `HeroController.TakeDamage`, salvo pinchos, ácido y pozos, con lo que sobra para el golpe siguiente; un
  golpe que queda en 0 se recibe igual (retroceso e invulnerabilidad: si no, el enemigo volvería a golpear en el frame
  siguiente) pero `PlayerData.TakeHealth` no quita vida. Un golpe que ya llega con 0 (los Shrumeling, o un Mosscreep
  escondido: en el juego no hacen daño al tocarlos) pasa tal cual y el juego lo ignora. El alma para revivir nunca pasa
  de `maxMP` (con la sombra sin recuperar el orbe es de 66: con "99" nadie podría revivir). "Avisos" apaga los avisos de lo que hacen los demás (se
  unió, salió, cayó, sombra recuperada, regalo de geo, Grimmchild, skin); los que responden a lo que hace uno
  (devoluciones, "Ir con ...", esperas de combate) salen siempre.
- **El botín cae igual en todos los PCs** (`DropSeeds`): cada jugador tiene su propio botín, así que cada PC lo crea, y
  la dirección y la fuerza de cada pieza las decide el juego al azar (en un PC el geo caería a la izquierda y en el
  otro a la derecha). Justo al lanzarlo se pone la misma semilla del azar en todos (de qué objeto es y cuántas veces lanzó ya
  eso) y después el azar vuelve a como estaba. Sin mensajes. Se hace en `FlingUtils.SpawnAndFling` (el geo de los
  enemigos: de un enemigo, solo el geo; su número compartido de `EnemySync`), `Breakable.Break` y los lanzamientos de
  las FSM de la sala que no son de un enemigo (rocas de geo, cofres; contados por estado y acción, porque en el PC que
  no golpea una roca solo se repiten algunos de sus estados), y en el lado y el ángulo de un objeto brillante que sale
  volando (`SendRandomEvent` y `FlingObject` de `Shiny Control`). La semilla no va en `HealthManager.Die`: antes de
  soltar el geo, la muerte gasta azar en cosas que dependen de quién golpeó (el giro del cadáver) y cada PC llegaría al
  geo con otra tirada.
- **Revivir** (`Revive`, mensajes 75 a 80): `HeroController.Die` se sustituye por la caída si hay alguien en pie (en un
  sueño, solo si está en el mismo; en los Panteones, nunca). El caído envía `Downed` (sala y suelo bajo él; si debajo
  no hay `Terrain` en 100 unidades, si ese suelo toca un `DamageHero` de peligro o si murió por un peligro, su
  `hazardRespawnLocation`: el cuerpo tiene que quedar donde el compañero pueda estar de pie a su lado, no en el aire
  sobre un pozo); el host le asigna el compañero en pie al que seguir. Lo que tenía debajo puede desaparecer (las
  plataformas del Coliseo se retraen entre oleadas): cada PC de pie en la sala mira cada 0,25 s si el cuerpo sigue
  apoyado y no toca un peligro; tras 1 s sin suelo pide moverlo (`BodyMoved`, mensaje 117) al suelo de debajo o, si no
  hay o son pinchos, junto a él si está pisando suelo. El host lo valida y lo reparte con el estado de la partida. El
  caballero del caído se oculta (sin colisión, sin gravedad, invencible, sin control ni entrada; se reaplica cada frame;
  se esconde con `Renderer.forceRenderingOff`, así lo que el juego encienda o apague entre medias queda como lo dejó, y
  la luz y la viñeta siguen) y, tras la animación de muerte, se pega al avatar del compañero, con las zonas de cámara
  emuladas; si el compañero cambia de sala, viaja detrás de él y entra por la misma puerta que él (la de esta sala hacia
  esa sala más cercana a donde se le vio por última vez). Los viajes a un punto (volver al cuerpo, "Recargar sala")
  entran por `dreamGate` como `SceneTravel`.
  Su cuerpo (`DownedBody`: animación "Death" y luego "Prostrate", con "† Nombre") se ve en su sala; con un compañero
  encima (el que lo revive), el "† Nombre" sube por encima del nombre de este para no pisarse. Junto a él, Focus
  cobra el alma al empezar (se devuelve si se suelta o te golpean), no cura (`CanFocus`) y al completarse se pide
  `ReviveRequest`; el host lo valida (en pie y en la sala del cuerpo) y responde `Revived` (cuerpo y vida). El progreso
  va a la sala con `ReviveProgress` (no fiable): el caído lee "X te está reviviendo". `BenchRest` al sentarse revive a
  los caídos en ese banco con toda la vida; si no queda nadie en pie, `Wipe`: cada caído vuelve a su cuerpo y muere de
  verdad. Al guardar estando caído no se guardan la invencibilidad ni la vida a 0. Un caído con el cuerpo en un sueño
  del que ya salieron todos los que estaban en pie (el compañero ganó, cayó al vacío o despertó) revive junto a ese
  compañero (`Revived` sin posición: donde esté al llegar).
- **HUD del compañero** (`Vitals`, mensaje 81; `PartnerHud`): vida, vida máxima, azules, alma y zona de la sala de cada
  uno, al cambiar y como mucho 4 por segundo; el host guarda las últimas y se las da a quien entra. Mini-HUD arriba a la
  derecha, espejo exacto del HUD del compañero (sprites de `HUD Cln`: la llena `appear_v020004`, la vacía
  `health_backboard` y la azul `idle0000`, a escala entre sí por sus vértices de tk2d y girados si tk2d los guarda
  girados), con su orbe de alma y su estado; sus máscaras sobre su cabeza 3 s al cambiarle la vida y flechas en el borde.
  Nunca pone una zona `NONE` (salas de paso del juego); mientras viaja en ciervo dice "viajando en ciervo".
- **Barra del jefe** (`BossBar`): los jefes se apuntan al encenderse (`HealthManager.OnEnable`), con el nombre de su
  entrada del diario; la vida máxima es la mayor vista. Jefe (`Difficulty.IsBoss`, también para la vida extra) es quien
  tiene estatua en el Salón de los Dioses (`unlockBossScene`) o aparece con 300 de vida o más; se decide una vez por
  enemigo. Los de los sueños (Tirano de Almas, Príncipe Gris Zote) no tienen estatua: por eso cuenta también la vida.
  Se quita en cuanto su muerte está en marcha (`EnemySync.IsDying`): el Falso Caballero muere por la cabeza y su
  cuerpo vuelve a tener vida en cada fase. Con barras visibles, `Notice.SetLift` sube los avisos (abajo a la derecha)
  por encima de ellas, para que un aviso de dos líneas no pise la barra.
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
  último sitio seguro; después no cambia aunque el caballero oculto siga al compañero). Se ve con el mapa abierto
  (`flamePins` activo: lo encienden el mapa del inventario y el rápido, y lo apaga `CloseQuickMap`), solo si los dos
  llevan el Wayward Compass equipado (quien no lo lleva envía su sitio como oculto: verlos siempre haría el mapa
  demasiado fácil) y salvo en sueños, el Palacio Blanco y los Panteones (donde el juego tampoco muestra la brújula).
  Sin noticias en 3 s, el icono se quita.
- **Avisos de versión** (`UpdateCheck`): al cargar el mod se apunta la versión (`LastRunVersion` en los ajustes
  globales); si es otra que la de la última vez, o no había ninguna pero sí ajustes de antes, el menú principal avisa
  "se actualizó a la X" y *Multijugador* ofrece sus novedades (la página de su versión). Además, si
  `CheckForUpdates` está activado (sí por defecto, en *Multijugador › Buscar actualizaciones*), una consulta a la lista
  pública de versiones de GitHub (`UnityWebRequest`, 10 s como mucho, sin datos del jugador) busca la más nueva: si es
  más nueva que la instalada, el menú principal lo avisa y *Multijugador* la ofrece para descargar (todos necesitan la
  misma versión para jugar juntos). Los avisos salen 2 s después de llegar al menú principal, una vez por sesión.
- **Reconexión automática** (`MultiplayerRunner`): si el invitado pierde la conexión por tiempo después de haber
  entrado (no si el host cierra, ni por código, versión o partida llena), vuelve al menú y, ya en el menú principal
  (el mundo solo se carga ahí), se vuelve a unir con la misma dirección, código y nombre: hasta 12 intentos,
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
  está caído, en un sueño o una cinemática, cambiando de sala, sentado en un banco (movido así se quedaría sentado en
  el aire) o sin control, ni si no juega el mundo coop del host (`CoopSaves`: el host en el suyo, el invitado en el
  del host; con partidas propias las salas se llaman igual pero no son el mismo mundo). Sin respuesta en 6 s, se
  abandona con un aviso.
- **Viaje sin puerta** (`SceneTravel`, lo usan "Ir con ..." y la vuelta de las cinemáticas compartidas): se entra con la
  entrada `dreamGate` del juego (la de la Puerta de los sueños), que coloca al caballero en `dreamGateX/Y` antes de
  mostrar la sala; ese punto se cambia un momento por el pedido y se repone al llegar. Con la entrada vacía el juego
  pondría al caballero en la primera puerta de la sala (`FindTransitionPoint` con "cualquiera") y en algunas salas
  saldría por ella antes de que el mod lo colocara.
- **Lo que hace cada uno** (`PartyState`, mensaje 91): cada jugador informa al host si está en una cinemática
  compartida, en un sueño o viajando en ciervo (`StagTravel`: la sala del viaje, `Cinematic_Stag_travel`, no es de
  juego ni de ninguna zona, así que su sala llega vacía y su zona es `NONE`); es su estado mientras no está caído (al
  revivir en un sueño vuelve a "en sueño"). El mini-HUD lo muestra si está en otra sala. Con eso va si tiene el juego
  en pausa (el host lo reparte en `PartyStatus`): con la escala de tiempo a 0 sus enemigos se quedarían congelados para
  los demás, así que quien está en pausa, como un caído, no se queda con enemigos (`PartyState.IsInactive` en
  `EnemySync.AssignControllers`). El HUD de los demás y su fila en *Compañeros* dicen "en pausa", también en la misma
  sala: su caballero se queda quieto y podría parecer trabado.
- **Cinemáticas compartidas** (`SharedCutscenes`, mensajes 87 a 90): los finales, los créditos, Mr. Mushroom y las
  pantallas con las que acaba el final (partida completada, Alma de Acero). Quien entra en una lo dice al host, que
  lleva a los demás (con un cambio de sala, esperando si están en pausa o cambiando de sala; un caído no va). Al
  llevado, la malla del vídeo (`CinematicPlayer`) se le vuelve a colocar delante de la cámara como lo hace el juego (si
  no, quedaría donde estaba su cámara en la sala anterior y se vería negro). Saltar (`GameManager.SkipCutscene`) es un
  voto: el host manda saltar a todos cuando votaron todos los que la ven (o con uno, si así lo eligió en "Saltar
  cinemáticas") y todos ven "Esperando a X para saltar 1/2"; si el que faltaba se va, se salta. En el host su propio
  voto completa la votación en el sitio (su mensaje al servidor se procesa dentro de la misma llamada), así que la
  marca de "saltando" solo se quita si no llegó la orden de saltar durante el voto. Al terminar
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
  aviso: mismo destello, sala de vuelta y cambio de sala. Antes de eso se pone la sala de vuelta (`dreamReturnScene`)
  del llevado. Si la entrada no está encendida en ese PC (la de la Radiancia pide el Corazón del Vacío), se entra directo al
  mismo sueño. Al salir de un sueño con jefe sin caer al vacío (`Dream Fall Catcher`) ni morir, se avisa a la sala y los
  que siguen dentro salen como el juego, por el estado `Leave Dream` de la FSM `Dream Nail` del caballero (quedan
  tendidos y se levantan al pulsar); las demás salidas son de cada uno.
- **Grimmchild** (`GrimmchildGift`, mensaje 95): el amuleto es de cada jugador pero su nivel y la Compañía son del
  mundo; si uno lo recibe, los demás también.
- **Entrar juntos en un Panteón** (`PantheonTogether`, mensaje 113, fiable, a la sala): al pulsar empezar en el cuadro
  de una puerta del Atrio (`BossDoorChallengeUI.Begin`), se avisa a la sala con la puerta y las ataduras. Los demás que
  no estén ocupados (caídos, en un banco, en pausa, hablando o ya en un Panteón) hacen lo mismo que ese botón en su PC
  (`bossReturnEntryGate`, `BossSequenceController.SetupNewSequence`) y viajan a la primera sala de la secuencia como la
  puerta (`door_dreamEnter`, `GodsAndGlory`). Dentro, cada PC lleva su secuencia; cuando la sala acaba en un PC
  (`BossSceneController.EndBossScene`: el jefe muerto o la estatua de la sala de descanso cruzada) avisa a la sala
  (mensaje 114) y los demás la acaban también, así que pasan juntos al siguiente jefe. Llamarla dos veces no hace nada
  (`endedScene`).
- **Viajar juntos en ciervo** (`StagTogether`, mensaje 106, fiable, a la sala): cuando la FSM `Stag Control` de la
  estación de este jugador entra en `Fade` (ya eligió destino: `To Scene` y `To Position`), se avisa a la sala con
  dónde está. En los demás PCs su propio ciervo recibe el mismo destino y pasa a `Fade` (quitando el control al
  caballero), así que hace lo del juego: fundido, `Cinematic_Stag_travel` y llegada a la salida del ciervo de destino.
  Solo va quien está a menos de 10 de quien viaja (en la otra punta de la estación, haciendo otra cosa, no se le lleva)
  y no está caído, muerto, cambiando de sala, sentado en un banco ni en pausa (ve "X se fue en ciervo"). Quien sale así
  no vuelve a avisar. Es un ajuste del host (*Viajar juntos en ciervo*, sí por defecto; formato de ajustes 4): con no,
  nadie avisa y cada uno viaja por su cuenta.
- **Sentados en el mismo banco** (`PlayerSync.ShareBench`): el estado del jugador lleva si está sentado (bandera 16 del
  byte de `PlayerSnapshot`: `atBench` y el banco con su control; `atBench` solo no basta, el juego no lo quita si se sale
  de la sala sin levantarse). El centro del banco es donde el juego deja a este caballero (se toma 0,6 s después de
  sentarse: antes aún lo está colocando). Los sentados a menos de 2,5 de ese centro se reparten a 1,1 uno de otro
  alrededor de él por número de jugador (el menor a la izquierda), así todos los PCs los ven igual: este caballero se
  corre a su sitio y a los compañeros se les dibuja en el suyo. Al quedarse solo vuelve al centro.
- **Skins** (`Skins`, mensajes 96 y 97, transferencia grande 3): con el formato de skins de CustomKnight (el de su
  versión 0.7.3), y `AtlasRepair` para las hojas antiguas del HUD y el geo. Cada uno anuncia su skin (nombre y huella
  SHA-256 de los archivos que ven los demás) y quien no la tiene la pide al host, que la envía o se la pide a su dueño.
  Van por la conexión cifrada del mod como transferencia grande de **baja prioridad** (`NetConnection`: cola aparte,
  solo sin otras transferencias pendientes, hasta unos 2 MB/s, como mucho un tick de trozos de golpe (cada acuse cubre
  64 paquetes) y 160 mensajes sin confirmar; si la latencia sube 60 ms sobre la más baja vista, baja a 0,25 MB/s, así
  los golpes y eventos no esperan detrás de la skin). Máximo 32 MB, solo PNG de hasta 4096x4096; la huella se
  comprueba en otro hilo. Las recibidas se guardan en `Mods/HKMultiplayer/SkinCache` (`<huella>.hksk`, hasta 256 MB,
  se borran las usadas hace más tiempo): la siguiente partida con esa skin la lee de ahí (comprobándola igual) y no la pide. Cuerpo con un
  bloque de propiedades por renderer; hechizos, amuletos y HUD propios en los materiales compartidos; efectos del
  compañero (sus copias bajo el avatar) con su skin. El cuerpo de un caído (otro objeto con la colección del caballero)
  lleva también la skin de su dueño, con el mismo bloque. Crear la textura de una hoja de 4096 cuesta 0,15-0,25 s (el
  juego descomprime el PNG en el hilo principal): se hace en pausa o al cambiar de sala (si no llega ese momento en
  3 s, fuera de un combate de jefe, con un tirón corto). Cada PC elige si ve las skins de los demás.
  El orbe de alma lleno propio (`OrbFull.png`) cambia el sprite de `Soul Orb/Orb Full` y de los fotogramas de su pulso
  (`Orb Full/Pulse Sprite`, `soul_orb_glow0000` a `0015`), reconocidos por el nombre del sprite y no por el de su
  textura: los del pulso van en un atlas (`sactx-...`). Si su imagen no se pudo crear en ese frame, se vuelve a
  intentar.
- **Barra del jefe**: el nombre es el título con el que el juego presenta al jefe (hoja `Titles`, partes `_SUPER`,
  `_MAIN` y `_SUB`), buscado en los textos de sus FSM o en las palabras de su nombre; si no, el de su diario.

## Pruebas del transporte

`tests/NetTests` es una aplicación de consola que prueba el transporte fuera del juego (conexión, código incorrecto,
pérdida y desorden simulados, canal fiable, datos grandes, memoria creada por paquete) y la conexión por Steam con una
red de pares en memoria (invitados por Steam y por IP a la vez, código incorrecto, pérdida del 20 %).

## Comportamiento de las funciones

- **Base**: funciones en `Game/Features/` con `Attach`/`Detach`; `PartyState` con el estado de cada jugador (un caído
  no cuenta para repartir enemigos ni para las esperas de combate); interfaz uGUI, nunca IMGUI.
- **Revivir (Focus 2 s, 33 de alma)**: el caído queda tendido con "† Nombre" y su cámara sigue al compañero;
  junto al cuerpo, mantener Focus a 2 unidades o menos; si golpean al que revive se pierde el progreso. Vuelve con 1
  máscara y 2 s de invulnerabilidad. Sin límite de tiempo caído; sentarse en un banco revive a los caídos a su lado con
  toda la vida; si caen todos, muerte normal para cada uno. En un sueño se cae y se revive dentro.
- **Sombras (copia en cada PC)**: cada PC mueve su copia de la sombra del compañero; matarla hace que el
  dueño recupere la suya (el host acepta solo el primer aviso). La de alguien desconectado no se ve. La copia se quita
  sin pasar por su muerte, así que al matarla se devuelve la música como hace su estado `Killed` (el audio a "Away"):
  su zona de música (`Music Control`) la había puesto en "Close" y, si no, seguiría sonando la de sombra. Si la sombra
  propia sigue cerca con su música puesta, se deja.
- **HUD del compañero**: arriba a la derecha, con su nombre, máscaras y alma con los sprites del juego y su estado;
  máscaras sobre su cabeza al cambiarle la vida; flechas en el borde; barra del jefe abajo con su nombre.
- **Cinemáticas**: finales, créditos y Mr. Mushroom a la vez para todos; saltar por votación ("Esperando a X para
  saltar 1/2"); presentaciones de jefe con los dos quietos; al acabar, quien fue llevado vuelve a donde estaba.
- **Ajustes del host** (Pausa › Multijugador), guardados por mundo; los invitados solo los ven. Por defecto: vida de
  jefes 1,5× solo con el compañero en la sala, enemigos 1×, daño recibido 1×; revivir sí (2 s, 33 de alma, 1
  máscara, 2 s de invulnerabilidad, en el banco sí); nombres, vida del compañero, flechas, compañeros en el mapa, avisos
  y barra del jefe sí, barras de enemigos no; regalar geo sí; ir con un compañero sí; saltar cinemáticas por votación;
  viajar juntos en ciervo sí. La vida extra divide el daño en el PC que mueve al enemigo (las fases de los jefes
  dependen de su vida).
- **Ir con un compañero**: en *Compañeros*, al quitar la pausa aparece a su lado; no con alguien caído, en un sueño,
  en una cinemática, cambiando de sala o en otro mundo.
- **Sueños**: quien está en la misma sala entra con quien golpea la entrada; al ganar salen los dos; la caída de uno no
  saca al otro. Grimmchild: si uno lo recibe, el otro también.
- **Skins**: compatibles con CustomKnight, enviadas por la red del mod en segundo plano (unos 2 MB/s, máximo 32 MB);
  cada PC elige si ve las de los demás.

## Idioma

- Los textos del mod que ve el jugador van en el idioma del juego (`UI/Lang.cs`: `Lang.T("español", "inglés")`): en
  español si el juego está en español (`TeamCherry.Localization.Language.CurrentLanguage() == LanguageCode.ES`); en
  cualquier otro, en inglés. Se mira cada vez que se pide un texto: avisos, estados, HUD y textos que se refrescan
  cambian en cuanto se cambia el idioma; las etiquetas fijas de los menús se ponen al crearlos (al abrir el juego). Los
  ajustes del host (`HostOption`) guardan los dos idiomas y eligen al mostrarse. Los nombres de jefes y zonas salen de
  los textos del propio juego. Los registros (`ModLog.txt`) siguen en español.


## Steam

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
  2 MB): el mundo al unirse y las skins salen a 2-2,6 MB/s, y con los valores por defecto Steam los frenaría y
  descartaría paquetes con el búfer lleno (Steam sigue bajando el ritmo solo si la conexión no da). Los datagramas del
  mod (1200 bytes como mucho) quedan muy por debajo del límite de un mensaje de Steam (512 KB). `SteamLink` decide si
  hay Steam: `SteamworksPlatform` si el juego inició Steam; nada si el juego no trae Steam (sin el ensamblado, el
  método que crea la plataforma falla al compilarse y se atrapa: por eso va aparte y sin inlining).
- **Sala de Steam del host** (`MultiplayerRunner.OpenSteamLobby`): al hostear, si hay Steam y el ajuste
  `SteamAccess` no es "Nadie", se crea una sala de hasta 4, solo para amigos (`k_ELobbyTypeFriendsOnly`) o privada con
  "Solo invitados" (`k_ELobbyTypePrivate`). Guarda `hkmp=1`, `version`, `code` (el código de la partida), `host` (el
  SteamID del host) y `name`. Con "Amigos", el host pone además la presencia de Steam `connect` = `+connect_lobby <sala>`:
  sin ella, la lista de amigos de Steam solo ofrecería "Invitar a ver", y con ella salen "Invitar a jugar" y "Unirse a
  la partida" (aviso `GameRichPresenceJoinRequested_t`, o esa línea de órdenes si Steam abre el juego). Steam no separa
  los dos: con "Solo invitados" también se pone (para poder invitar desde la lista de amigos), y a quien pulse "Unirse"
  sin invitación a la sala Steam no lo deja entrar (`LobbyJoinResult.NotAllowed`: se le dice que pida la invitación
  desde el juego). La invitación del botón de la pausa (`ActivateGameOverlayInviteDialog`) es una invitación a la sala
  y vale en todos los modos; la de "Invitar a jugar" de la lista de amigos puede no valer en una sala privada. El host
  escucha por UDP y por Steam a la vez; si el puerto no se puede abrir, la partida se abre igual solo por Steam (la
  pausa lo dice en vez de la IP).
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

## Paquete e instalador

- `tools/package.sh` compila en Release y arma `out/package/HKMultiplayer-<versión>.zip` con una carpeta
  `HKMultiplayer-<versión>`: `HKMultiplayer Setup.exe`, `LEEME.txt` y `README.txt` (instrucciones para jugadores en
  español y en inglés, con la instalación a mano), `LICENSE.txt` y `THIRD_PARTY_NOTICES.txt` en la raíz, lo que se
  copia al juego en `files/` (la Modding API de `vendor/modding-api/OutputFinal` sin su README, y `Mods/HKMultiplayer`
  con el mod, su `.pdb` y BouncyCastle) y `SHA256SUMS.txt`. Del juego solo van los dos ensamblados que parchea la
  Modding API (`Assembly-CSharp.dll` y `TeamCherry.Localization.dll`). Fuentes del instalador en
  `tools/installer/launcher` (C#, .NET Framework 4.7.2, que viene con Windows 10 y 11).
- **Todo el instalador es el .exe**, sin scripts por debajo. El .exe es además **el mismo archivo en todas las
  versiones del mod** mientras no cambie su código: no lleva la versión del mod (la lee del `HKMultiplayer.dll` del
  paquete, `Installer.PackageVersion`) y se compila de forma determinista, sin ruta de depuración ni el commit
  (`SetupLauncher.csproj`). Su propia versión (`Version`) solo sube cuando cambia el instalador. Lleva autor, copyright
  y descripción.
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
- Con `/install` o `/uninstall` y la ruta trabaja en la consola: se une a la consola desde la que se llamó (o abre una y espera Intro al final), escribe en UTF-8 (en el archivo si la salida va a uno) y
  devuelve 0, 1 (error) o 2 (desinstalado con algo sin devolver). En un `.bat` o con `start /wait` se espera a que
  acabe. Sin `SHA256SUMS.txt` y `files\` al lado (abierto desde dentro del `.zip`) avisa con un cuadro de mensaje.
- Carpeta del juego pasada o buscada (registro de Steam en la vista de 64 bits, `libraryfolders.vdf`, desinstalación
  de Steam, rutas típicas en los discos locales, GOG). Exige Unity 6 (`UnityPlayer.dll` 6000.x) y
  `TeamCherry.TK2D.dll`: con otra versión del juego la Modding API lo dejaría sin arrancar. Además, el
  `Assembly-CSharp.dll` del juego tiene que llevar `1.5.12620` en UTF-16 (`IsGameVersion`). Solo bloquea si hay un
  `hollow_knight.exe` abierto desde esa carpeta (uno de otra carpeta no molesta) o uno cuya ruta no se puede leer (abierto
  como administrador; por eso el proceso es de 64 bits, `Prefer32Bit` falso). Antes de instalar, la ventana pregunta
  con la lista de los otros mods que va a apartar.
- Cada cambio se apunta en `<juego>\HKMultiplayer-backup\manifest.txt` antes de hacerlo: `added-dir`, `added`,
  `replaced` (el original en `HKMultiplayer-backup\files\`), `old-mod` (el mod anterior `HollowKnightCoop`, movido a
  la copia porque también abre la red) y `moved` (otros mods apartados a `Mods\Disabled`). Volver a instalar es una
  actualización: lo ya apuntado no se vuelve a copiar, así que la copia sigue siendo la del juego antes de la primera
  instalación. El formato es el mismo que el del instalador de las versiones anteriores, así que una instalación hecha
  con él se actualiza y se desinstala con este.
- La desinstalación (`Installer.Uninstall`) recorre el manifiesto al revés. De `Mods\HKMultiplayer` borra lo que puso el
  instalador y la caché `SkinCache`; lo demás (las skins y lo que haya puesto el jugador) se queda y se dice qué. Las
  carpetas añadidas se borran si quedan vacías. Un ensamblado que ya no es el de la Modding API (`IsModded`: los
  parcheados nombran `ModHooks` o `MonoMod`; el `TeamCherry.Localization.dll` de la Modding API no nombra `ModHooks`)
  lo volvió a poner Steam: se deja si es distinto de la copia. Si algo no se pudo devolver, conserva la copia y remite
  a "Verificar integridad" de Steam. Sin manifiesto (mod puesto a mano) quita solo el mod.

## Limitaciones conocidas

- El jefe ataca al caballero del PC que lo mueve, y lo mueve el jugador más cercano: cambia como mucho cada 8 s, y
  solo entre dos ataques (ver "Enemigos y objetos de sala"). Es lo que hace que ataque a los dos. Un jefe
  sin estados de reposo en su lógica, o que no reposa en 4 s (Hornet a veces encadena esquivas y ataques sin pasar por
  `Idle`), sigue cambiando a mitad de un ataque.
- La marioneta se dibuja unas centésimas en el pasado (para moverse suave): en lo que cae rápido se nota un poco.
- Los nombres fijos de los menús del mod cambian de idioma al reiniciar el juego.
