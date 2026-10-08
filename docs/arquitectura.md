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
    y no guarda nada en su PC: su personaje va al host en los bancos, al salir y cada minuto
    (`players/<clave>.json`). El host guarda el mundo solo cada 2 minutos.
  - **Personaje** (`CharacterData`): vida y alma actuales, vida máxima, geo, banco y reaparición, sombra, amuletos
    equipados, portal de sueño e interfaz. Todo lo demás es del mundo.
  - **Progreso compartido en directo** (`SharedProgress`): los campos del mundo se comparan cada 0,2 s con una copia
    (lectores compilados, sin memoria por lectura) y los cambios van al host, que los aplica y reenvía. Los objetos de
    sala (paredes, palancas, piedras de geo) se envían al guardarse y nunca retroceden.
- **Enemigos y objetos de sala**:
  - Manda el juego del dueño de la sala: sus enemigos funcionan normal y se envía su estado (transform, animación,
    activo, vida, estado de sus FSM) con detección de cambios sin crear objetos.
  - En los demás juegos los enemigos son "marionetas": su lógica (FSM, física) se detiene y se dibujan con el estado
    recibido e interpolación; sus colisiones de daño siguen funcionando para el jugador local.
  - Los golpes del jugador local a una marioneta se envían al dueño, que los aplica al enemigo real.
  - Los objetos que crean los enemigos (proyectiles, etc.) se replican como "aparecidos" con su prefab y transform.
  - Traspaso al cambiar el dueño: el nuevo dueño despierta sus enemigos en el estado de FSM recibido.
  - Casos especiales (jefes con escenarios propios) se añaden por separado a medida que se prueban.
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
  (`tools/mp-test-lib.sh`): conexión, movimiento, memoria por segundo, tirones, suavidad, sincronización.

## Hitos

| Hito | Contenido | Se da por bueno cuando |
|---|---|---|
| M1 | Transporte + sesión + jugadores (verse y moverse) | Dos jugadores conectados con código, se ven moverse fluido, memoria de red ≈ 0 |
| M2 | Mundo compartido (progreso, objetos persistentes, al unirse) | Las habilidades y el mundo coinciden entre los dos |
| M3 | Enemigos y objetos de sala | Pelear juntos en salas normales y con jefes probados |
| M4 | Efectos del otro jugador | Se ven sus ataques, hechizos y movimientos especiales |
| M5 | Funciones del coop (revivir, sombras, skins, panel...) | Igual o mejor que la 0.7.3 |
| M6 | Instalador y paquete | Tu amigo lo instala y jugáis |
