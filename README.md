# HKMultiplayer

Mod cooperativo para **Hollow Knight 1.5.12620** (la versión actual de Steam, Unity 6). Un jugador hostea un mundo
coop y su compañero se une con la dirección del host y el código de la partida: comparten el mundo, se ven en tiempo
real y pelean juntos contra los mismos enemigos y jefes.

Proyecto escrito desde cero, pensado para dos jugadores (vale para algunos más). El diseño detallado está en
[docs/arquitectura.md](docs/arquitectura.md).

**Versión actual: 0.13.0** · Hitos M1 a M4 terminados (ver [Estado](#estado)).

---

## Funciones

### Partida y conexión

- **Mundos coop** propios: hasta 4, guardados en el PC del host (`HKMultiplayer/Mundos/`). Son aparte de las 4 ranuras
  del juego, que nunca se tocan. Desde el menú se crean, se hostean y se borran.
- **Hostear y unirse** desde el menú principal (*Multijugador*): el host abre su mundo y comparte su **dirección**, el
  **puerto** (26950 por defecto) y el **código de la partida** (formato `ABCD-EFGH`). Botones para copiar el código y
  mostrar u ocultar la IP.
- Funciona en **red local y por internet** (en internet el host abre o redirige el puerto UDP en su router).
- **Personaje de cada invitado** guardado en el mundo del host: se envía en los bancos, cada 30 segundos y al salir.
  Al volver a unirte, recuperas tu personaje. Si el host cierra la partida o el juego, primero recoge los personajes
  de los invitados.
- **Avisos** en pantalla: código de la partida, quién entra y sale, desconexiones y esperas de combate.
- **Panel en pausa** (*Pausa › Multijugador*): jugadores conectados, código, IP y cerrar la partida.

### Jugadores

- Se ve al compañero en tiempo real, con su **nombre encima**, sus animaciones y su orientación, con movimiento suave
  (interpolación adaptativa a la conexión).
- **Inventario propio** de cada uno: vida, alma, geo, máscaras y vasijas, aguijón y artes, amuletos y muescas, llaves,
  reliquias, mapas, esencia, compras, banco y sombra.

### Efectos del compañero

Se ven y se oyen las acciones del compañero con los mismos efectos del juego, a la vez que su animación:

- **Tajos del aguijón** (de lado, alternos, arriba, abajo y en la pared), más grandes con Mark of Pride y Longnail y con
  el color de Fury of the Fallen.
- **Artes del aguijón**: la carga (partículas, destello y sonido), Great Slash, Dash Slash y Cyclone Slash.
- **Hechizos**: Vengeful Spirit y Shade Soul (estallido y bola que vuela hasta chocar), Desolate Dive y Descending Dark
  (carga, caída, golpe en el suelo y pilar), Howling Wraiths y Abyss Shriek.
- **Movimiento**: dash y Shade Dash (ráfaga, estela y sombra), doble salto (alas y plumas), salto de pared, Crystal
  Heart (carga, salida, estela, choque y frenada).
- **Curación** (líneas, polvo, destello y sonido), **aguijón onírico** (carga y tajo) y **golpes recibidos**
  (partículas, destello y sonido).
- Son solo visuales: no dañan, no rompen nada, no gastan alma ni tocan a tu caballero; el daño real ya va por los
  golpes de cada uno.

### Mundo compartido

- **Progreso compartido en directo**: habilidades de movimiento y hechizos, jefes vencidos, puertas, palancas, paredes
  rotas, personajes y salas visitadas, mapas de sala.
- **Objetos de sala**: palancas, interruptores, puertas, paredes y suelos que se rompen, piedras de geo (golpes de los
  dos), rompibles de decoración, pasto y telas que se cortan, enredaderas, tarros de larva (la larva sale en los dos),
  estalactitas.
- **Ascensores** con controlador: los mueve quien los usa, con su caballero encima, y el otro lo ve moverse igual.
- **Rejas de batalla y de jefe de un solo sentido**: si se cierran con uno dentro, el otro puede entrar a ayudar pero
  no salir hasta que se abran. Quien entra o vuelve a la sala las encuentra como están.
- **Cofres** del mundo: si uno lo abre, el otro lo ve abrirse (o ya abierto); lo que sueltan es de cada uno (el objeto
  queda en el suelo para cada jugador y el geo le cae a quien esté en la sala).
- **Objetos individuales**: objetos brillantes, fragmentos de máscara y de vasija, orbes de sueño... lo que recoge uno
  no desaparece para el otro.
- **Peligros animados al mismo ritmo** (sierras y paredes de pinchos del Palacio Blanco y del Camino del Dolor).
- Quien entra en una sala recibe su estado: lo activado, lo roto, los enemigos muertos y los combates en marcha.

### Enemigos y jefes

- Cada enemigo lo mueve el PC del jugador **más cercano** y ataca a los dos. Su vida, sus golpes y su muerte son los
  mismos en los dos PCs; los golpes de cualquiera le quitan vida en ambos.
- **Proyectiles, crías e invocaciones** se crean en los dos y dañan a los dos (disparos de escupidores, cristales,
  láseres, orbes del Maestro de Almas, bolas de Mawlek, agujas de Hornet, tarros y bichos del Coleccionista...).
- **Efectos y sonidos** de los enemigos, impactos y destellos de los golpes, música de jefe y **nombre del jefe** al
  despertar, en los dos PCs.
- **Piezas y trampas** de los jefes iguales en los dos: el rayo del Guardián de Cristal y sus torretas del techo, la
  aguja de Hornet, las mazas, los brazos...
- **Cadáveres** que caen (y explotan, como el Gruzzer volátil) en el mismo sitio en los dos.
- **Arenas por oleadas** y combates llevados por la sala: Señores Mantis (desafío, jaula, suelo con pinchos, la
  pareja), Coliseo de los Tontos (gestor, oleadas y jaulas), Caballeros Vigía...
- **Diálogos de jefe compartidos** (Hornet en los Bordes del Reino y cualquier encuentro con diálogo antes de un
  combate): los dos ven el diálogo y cada uno espera a que el otro termine de leer.
- **Los combates de jefe esperan a que estéis los dos**: tanto los que empiezan al entrar en una zona como los que
  despiertan al ver al caballero. Mientras se espera, sale un aviso de a quién se espera y el jefe es invulnerable.
- Los enemigos muertos siguen muertos al volver a la sala, hasta descansar en un banco (como en el juego).
- Si el jefe cambia de PC a mitad del combate (porque el otro está más cerca), sigue donde iba, sin reiniciarse.
- **Jefes probados peleando con los dos jugadores**: Mamá Gruz, Falso Caballero, Hornet (1 y 2), Mawlek, Maestro de
  Almas, Defensor del Estiércol, Nosk, Vasija Rota, Guardián de Cristal (normal y enfurecido), Señores Mantis, Uumuu,
  Coleccionista, Señor Traidor y Caballeros Vigía. Caballero de la Colmena, Flukemarm, Cargador Musgoso y Rey Mosca:
  comprobado que mueren en los dos, falta verlos atacar.

---

## Integraciones y tecnología

| Componente | Para qué se usa |
|---|---|
| **Hollow Knight Modding API** (Unity 6) | Carga del mod, menú del mod, ajustes globales y ganchos del juego. |
| **MonoMod** (`MMHOOK_*.dll`, `MonoMod.RuntimeDetour`) | Ganchos sobre el código del juego: salud de los enemigos, guardado y carga, cambios de sala, ataques, objetos... |
| **PlayMaker** | Lectura y control de las máquinas de estados (FSM) del juego: lógica de enemigos, jefes, ascensores, rejas, palancas, arenas, diálogos. |
| **BouncyCastle.Cryptography** (MIT) | Cifrado: intercambio de claves X25519, derivación HKDF-SHA256 con el código de la partida y ChaCha20-Poly1305 en cada paquete. |
| **Newtonsoft.Json** (del juego) | Personajes de los invitados y datos de los mundos coop. |
| **Unity UI** | Menús con el estilo del juego (menú *Multijugador*, panel en pausa) y avisos en pantalla. |

- **Red propia sobre UDP**: un solo socket, canales fiable (eventos, en orden y sin duplicados) y no fiable (estado
  continuo), transferencias grandes troceadas (el mundo al unirse), latido y desconexión limpia. Sin memoria nueva por
  paquete para no provocar tirones.
- **Seguridad**: solo entra quien conoce el código de la partida y todo viaja cifrado y autenticado; paquetes alterados
  o repetidos se descartan.
- No depende de Steam ni de servicios externos: la conexión es directa entre los PCs.

---

## Cómo jugar

1. Instalar la Modding API y el mod en los dos PCs (ver [Instalar](#instalar)).
2. **Host**: menú principal › *Multijugador* › *Nuevo mundo* (o elegir uno) › *Hostear*. Pasa a tu compañero tu IP,
   el puerto y el código que aparece en pantalla (también en *Pausa › Multijugador*).
3. **Invitado**: menú principal › *Multijugador* › *Unirse a un mundo*, con la dirección, el puerto y el código.
4. Para jugar por internet, el host abre el puerto UDP (26950 por defecto) en su router o usa una VPN de red local.

---

## Instalar

El paquete para jugadores incluye la Modding API, el mod y un instalador:

- `instalar.bat`: busca la carpeta del juego, guarda una copia de los archivos que reemplaza y copia el mod.
- `desinstalar.bat`: deja el juego como estaba.

> El paquete se arma con `tools/package.sh` (pendiente de actualizar al nombre nuevo del mod, ver M6).

---

## Estado

| Hito | Contenido | Estado |
|---|---|---|
| M1 | Transporte, sesión y jugadores (verse y moverse) | Hecho |
| M2 | Mundo compartido (progreso, objetos persistentes, unirse) | Hecho |
| M3 | Enemigos, jefes y objetos de sala | Hecho |
| M4 | Efectos del otro jugador (tajos, hechizos, dash, curación) | Hecho |
| M5 | Revivir, sombras compartidas, barras de vida, cinemáticas compartidas, skins, ajustes del host | Pendiente |
| M6 | Instalador y paquete con el nombre nuevo | Pendiente |

Para M5 quedan también Grimm, los jefes de sueño, Hollow Knight y la Radiancia (dependen de cinemáticas o de entrar
juntos a un sueño). La lista completa de pendientes está al final de la arquitectura.

---

## Desarrollo

### Estructura

| Carpeta | Contenido |
|---|---|
| `src/HKMultiplayer/Net/` | Transporte UDP, canales, cifrado y código de partida (sin Unity, se prueba fuera del juego). |
| `src/HKMultiplayer/Session/` | Sesión: host y unirse, jugadores, salas y dueño de cada sala, reparto de mensajes. |
| `src/HKMultiplayer/Game/Players/` | Estado de los jugadores, interpolación, avatares del compañero y efectos de sus acciones. |
| `src/HKMultiplayer/Game/World/` | Mundos coop, guardado, personajes de los invitados y progreso compartido. |
| `src/HKMultiplayer/Game/Scene/` | Objetos de sala, rompibles, ascensores y rejas, controladores de batalla, enemigos y jefes, peligros animados. |
| `src/HKMultiplayer/UI/` | Menús y avisos. |
| `src/HKMultiplayer/Testing/` | Modo de prueba: órdenes por archivo para probar con dos copias del juego. |
| `tests/` | Pruebas del transporte fuera del juego y rutas de prueba con dos jugadores (`tests/routes`). |
| `tools/` | Compilar e instalar (`test-cycle.sh`), pruebas con dos copias (`mp-test-lib.sh`), paquete e instalador. |
| `docs/` | Diseño y notas. |
| `lib/` | *(solo local, no se sube)* DLL del juego y de la Modding API para compilar. |
| `vendor/` | *(solo local, no se sube)* Modding API compilada para el paquete. |
| `test-instances/` | *(solo local, no se sube)* Copias del juego para probar con dos jugadores en el mismo PC. |

Las DLL del juego son de Team Cherry y no se incluyen en el repositorio.

### Compilar

1. Copiar a `lib/` las DLL de `hollow_knight_Data/Managed` del juego con la Modding API instalada
   (`Assembly-CSharp.dll`, `MMHOOK_*.dll`, `PlayMaker.dll`, `UnityEngine.*.dll`, `TeamCherry.*.dll`,
   `MonoMod.*.dll`, `Newtonsoft.Json.dll`, `netstandard.dll`...).
2. `bash tools/test-cycle.sh` compila (.NET Framework 4.7.2) e instala en las copias de prueba;
   `bash tools/test-cycle.sh --game` también en el juego.
3. Pruebas del transporte: `dotnet run --project tests/NetTests`.

### Probar con dos jugadores en un PC

`tools/mp-test-lib.sh` abre dos copias del juego (`test-instances/b` y `c`) en modo de prueba y les manda órdenes por
archivo: hostear y unirse, ir a una sala, moverse, golpear, capturas de pantalla de las dos a la vez, estado de los
enemigos y objetos, simular una mala conexión (`netsim`), etc. Las órdenes están documentadas en
[docs/arquitectura.md](docs/arquitectura.md#pruebas).
