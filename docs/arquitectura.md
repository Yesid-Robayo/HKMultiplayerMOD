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
  - Traspaso: el que lo suelta envía cómo iba (estado y números de cada FSM, velocidad, gravedad y tipo de cuerpo) y el
    nuevo lo sigue desde ahí. También al salir de la sala o morir (aún dentro, al empezar el cambio de sala): un jefe no
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
    efectos fuera del objeto (el ascensor voltea la dirección de su palanca al llegar) se harían dos veces.
  - Rejas de batalla y de jefes: si una se cierra con un jugador dentro, el otro puede entrar a ayudar pero no salir
    hasta que se abra. Las que ya están cerradas al cargar la sala no se cruzan.
  - Las copias no tocan los datos del jugador (contadores como las larvas llegan una vez, del PC de origen). Los tarros
    de larva se rompen en todos y la larva sale también en los demás.
  - Quien entra en una sala recibe del dueño lo ya activado (puertas, palancas, paredes), los golpes de cada piedra de
    geo y los rompibles rotos. Al guardar, lo activado en el mundo no vuelve atrás.
  - Lo que crean los enemigos en combate (proyectiles, crías, invocaciones) se crea en los demás con la misma acción de
    su marioneta (mismo prefab). Si hace daño o tiene vida, es una copia que sigue al original hasta que desaparece; los
    efectos solo visuales del pool corren solos. Lo que crean al prepararse (cadáveres, piezas propias) no se replica.
  - Piezas que se mueven aparte (hijos con animación o que dañan, como la aguja de Hornet): posición, profundidad,
    giro, si se ven, si su caja de daño está encendida, cuánto dañan y animación vienen del PC que mueve el enemigo.
  - La sombra de cada jugador no es un enemigo compartido.
  - Probados: Mamá Gruz, Falso Caballero, Hornet, Mawlek, Maestro de Almas, Defensor del Estiércol, Nosk y Vasija Rota
    (despiertan, atacan al jugador que no es dueño de la sala y siguen el combate al cambiar de PC), escupidores y
    zombis.
- **Efectos del otro jugador**: eventos (ataque, hechizo, dash, curación...) que el receptor reproduce con los prefabs
  del propio héroe. Son solo visuales: el daño real va por los golpes directos.

### Interfaz (`UI/`)

- **Menú principal → Multijugador**: copia de los menús del juego (botón de "Iniciar partida", pantalla de "Extras").
  Nombre y puerto; hostear (mundos coop, borrar con confirmación, nuevo) y unirse (dirección y código).
- **Pausa → Multijugador** (solo en partida multijugador): mundo, código, IP oculta hasta pedirla, jugadores con su
  ping y cerrar o salir de la partida.
- Avisos en pantalla con la letra del juego (código al abrir la partida, quién entra o sale).

### Funciones del coop (`Game/Features/`)

Revivir, sombras compartidas, skins, panel de pausa, barras de vida, cinemáticas compartidas, estalactitas, rejas de
arena, regalos de geo, autoguardado y reconexión: se reescriben sobre las capas anteriores.

## Pruebas

- `tests/NetTests`: aplicación de consola que prueba el transporte fuera del juego (conexión, código incorrecto,
  pérdida y desorden simulados, canal fiable, datos grandes, memoria creada por paquete).
- En el juego: dos copias (`test-instances/b` y `c`) controladas por archivo con el modo de prueba del mod
  (`tools/mp-test-lib.sh`): conexión, movimiento, memoria por segundo, tirones, suavidad, sincronización. Capturas de
  pantalla de las dos a la vez (`shot`) para comparar lo que se ve; cada daño que recibe el caballero se registra con
  lo que lo hizo. La memoria creada en combate (≈300 KB/s) es la del juego: es la misma con la partida cerrada.

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

- **M3**: probar el resto de jefes (fases, plataformas, cinemáticas propias). Guardián de Cristal y Señores Mantis sin
  probar (necesitan entrar por su zona de activación). Lo que crea un objeto que no es enemigo (controladores de arena
  sin vida) aún no se replica.
- **M4**: efectos del otro jugador (tajos, hechizos, dash, curación). `PlayerEvent` está reservado y sin usar.
- **M5**: revivir, sombras compartidas, barras de vida y HUD del compañero, cinemáticas compartidas, skins y ajustes
  del host en el panel (la 0.7 los tenía en `HollowKnightCoop/src/HollowKnightCoop/Coop`).
- **M6**: `tools/package.sh` aún usa los nombres del mod anterior (`HollowKnightCoop`).
- **Pasto y telas al paso del compañero** (solo estético, baja prioridad): hoy se sincroniza el corte, pero no el
  balanceo cuando el otro caballero pasa por encima. Sin red: en cada PC, cada pocos frames, buscar el pasto
  (`GrassBehaviour`) cercano al avatar del otro y darle el mismo empujón que da el héroe local, con la velocidad del
  avatar. No poner la etiqueta `Player` al avatar: muchas cosas del juego reaccionan a ella (puertas entre salas, zonas
  de cámara, NPCs, rangos de alerta de enemigos).
- Auditoría: los objetos guardables los siguen a la vez `SceneObjectSync` y la copia de cadenas de `FsmStateSync`;
  funciona, pero conviene que los lleve uno solo. Sin confirmar: posible aviso falso de "roto" justo al entrar en una
  sala, si se escanea antes del `Start` de los objetos.
