<div align="center">

# HKMultiplayer

**Mod cooperativo para Hollow Knight: juega el mundo entero con tus amigos, en red local o por internet.**

![Versión](https://img.shields.io/badge/versión-0.31.2-7fb2ff)
![Estado](https://img.shields.io/badge/estado-beta-f0c86a)
![Hollow Knight](https://img.shields.io/badge/Hollow%20Knight-1.5.12620-e8ecf3)
![Plataforma](https://img.shields.io/badge/plataforma-Windows-2f5fb3)
![Idiomas](https://img.shields.io/badge/idiomas-español%20%7C%20english-7ddc8f)

**Español** · [English](README.md) · [Cambios por versión](CHANGELOG.es.md)

<img src="docs/media/coop-hornet.gif" alt="Dos PCs en la misma partida peleando contra Hornet" width="966">

<sub>Dos PCs en la misma partida contra Hornet: los dos ven lo mismo a la vez, cada caballero con su nombre y el HUD del
compañero arriba a la derecha. Hollow Knight es de Team Cherry.</sub>

</div>

> [!WARNING]
> **Versión beta.** Casi todo se ha probado jugando con hasta cuatro copias del juego en el mismo PC, pero todavía no
> se ha jugado una partida larga entre PCs reales por internet, y algunas partes no se han probado del todo (ver
> [Estado de las pruebas](#-estado-de-las-pruebas)). **Puede tener errores.** Haz una copia de tus partidas antes de
> jugar y, si algo falla, cuéntalo en un *issue* con una captura y tu `ModLog.txt`.

Un jugador **hostea** un mundo coop y sus compañeros **se unen** por **Steam** (con una invitación o con *Unirse a la
partida*, sin abrir puertos) o con la dirección del host y el código de la partida:
comparten el mundo, se ven en tiempo real y pelean juntos contra los mismos enemigos y jefes. Hasta 4 jugadores,
escrito desde cero sobre la Modding API de Hollow Knight.

## Índice

- [Funciones](#-funciones)
- [Estado de las pruebas](#-estado-de-las-pruebas)
- [Instalar](#-instalar)
- [Cómo jugar](#-cómo-jugar)
- [Ajustes de la partida](#%EF%B8%8F-ajustes-de-la-partida)
- [Tecnología](#-tecnología)
- [Desarrollo](#-desarrollo)
- [Créditos](#-créditos)
- [Licencia y contribuciones](#-licencia-y-contribuciones)

---

## ✨ Funciones

- 🎮 **El juego entero y sus DLC, con hasta 4 jugadores**: la historia hasta todos sus finales, Hidden Dreams, The Grimm
  Troupe, Lifeblood y Godmaster, con sus Panteones jugados juntos hasta el Panteón de Hallownest.
- 🌍 **Mundos coop**: hasta 4 mundos propios, guardados en el PC del host, aparte de tus partidas normales.
- 🔒 **Conexión**: por **Steam** (invitar y *Unirse a la partida*, sin abrir puertos) o por **IP** (red local o
  internet), las dos a la vez en la misma partida; con código de partida y todo cifrado, sin servidores propios. El
  invitado que pierde la conexión vuelve a entrar solo.
- 👥 **Jugadores**: los compañeros se ven en tiempo real con su nombre, animaciones, ataques, hechizos y amuletos, y
  también en el mapa.
- ⚔️ **Enemigos y jefes**: los mismos en todos los PCs (misma vida, mismos golpes, misma muerte); los jefes esperan a
  que estén todos.
- 🗺️ **Mundo compartido**: progreso, palancas, puertas, paredes rotas, ascensores, rejas, cofres y misiones de NPC, a la
  vez.
- ❤️ **Revivir**: si caes con un compañero en pie, quedas tendido y te revive manteniendo Focus junto a tu cuerpo.
- 🌙 **Sueños y cinemáticas**: se entra juntos a los sueños; los finales y los créditos se ven a la vez y se saltan por
  votación.
- 🗣️ **Personajes de cada jugador**: cada uno oye cada conversación desde el principio, y el personaje que se va tras
  hablar solo se va para quien habló con él.
- 🎨 **Skins**: compatibles con CustomKnight; los demás ven la tuya aunque no la tengan.
- 🛠️ **Panel en pausa**: ajustes del host, vida de los compañeros, dar geo, ir con un compañero, desatascarse y recargar
  la sala.
- 🌐 **Idiomas**: el mod sale en el idioma del juego (español, o inglés en cualquier otro idioma).
- 📦 **Instalador**: una ventana para instalar, actualizar y desinstalar, que deja el juego como estaba.

<details>
<summary><b>🌍 Partida y conexión</b></summary>

- **Mundos coop** propios: hasta 4, guardados en el PC del host, en
  `C:\Users\<usuario>\AppData\LocalLow\Team Cherry\Hollow Knight\HKMultiplayer\Mundos` (una carpeta por mundo). Son
  aparte de las 4 ranuras del juego, que nunca se tocan. Desde el menú se crean, se hostean y se borran, y la página de
  hostear tiene un botón para abrir esa carpeta (copias de seguridad o pasarlos a otro PC).
- **Hostear y unirse** desde el menú principal (*Multijugador*): el host abre su mundo y comparte su **dirección**, el
  **puerto** (26950 por defecto) y el **código de la partida** (formato `ABCD-EFGH`). Botones para copiar el código y
  mostrar u ocultar la IP.
- **Por Steam, sin abrir puertos**: al hostear se abre una sala de Steam. Tus amigos ven *Unirse a la partida* en su
  lista de amigos de Steam o tu partida en *Multijugador › Unirse a un mundo*, y desde *Pausa › Multijugador › Invitar
  amigos de Steam* invitas a quien quieras. Si el juego del amigo está cerrado, Steam lo abre y el mod entra solo. En
  *Multijugador* eliges quién puede entrar por Steam: **amigos**, **solo invitados** o **nadie**.
- **Steam e IP a la vez**: en la misma partida unos pueden entrar por Steam y otros por IP. Quien no tenga Steam
  entra al host por IP, en la misma red o por internet.
- Funciona en **red local y por internet** (por Steam no hace falta abrir nada; por IP, en internet el host abre el
  puerto UDP en su router o se usa una VPN de red local como Radmin VPN, ZeroTier o Tailscale).
- **Personaje de cada invitado** guardado en el mundo del host: se envía en los bancos, cada 30 segundos y al salir.
  Al volver a unirte, recuperas tu personaje. Si el host cierra la partida, primero recoge los de los invitados.
- **Reconexión automática**: si un invitado pierde la conexión con el host (no si el host cierra la partida), vuelve
  al menú principal e intenta volver a entrar solo, hasta 12 veces, y carga otra vez el mundo con su personaje. *Esc*
  en el menú principal lo cancela.
- **Avisos** en pantalla: código de la partida, quién entra y sale, desconexiones y esperas de combate.
- **Panel en pausa** (*Pausa › Multijugador*): jugadores conectados con su ping, código, IP y cerrar o salir de la
  partida; *Ajustes de la partida*, *Compañeros* y *Skin*.

</details>

<details>
<summary><b>🏛️ DLC y Dios Hogar</b></summary>

- **Hidden Dreams**: el Defensor Blanco y el Príncipe Gris Zote, entrando juntos con el aguijón onírico.
- **The Grimm Troupe**: la misión de la Compañía, Grimm y el Rey de las Pesadillas, y el Grimmchild para todos.
- **Lifeblood**: la Colmena y su caballero, y la sangre vital como en el juego.
- **Godmaster**: cada jugador entra en Dios Hogar por su cuenta con el aguijón onírico junto a la Buscadora de Dioses.
  - **Panteones juntos**: cuando un jugador empieza un Panteón en su puerta, los que están en el Atrio entran con él,
    con las mismas ataduras, y el grupo pasa junto a cada jefe (también al salir de la sala de descanso). El que cae
    revive junto al grupo en la sala del siguiente jefe.
  - **El final del Panteón de Hallownest es de cada jugador**: cada uno da los golpes finales con su propio botón de
    atacar y ve la cinemática del final al acabar el suyo, sin los caballeros ni los nombres de los compañeros.
  - La subida de la Radiancia: el abismo sube para todos cuando sube cualquiera, y cada uno reaparece en la última
    plataforma pasada.

</details>

<details>
<summary><b>👥 Jugadores y efectos del compañero</b></summary>

- Se ve al compañero en tiempo real, con su **nombre encima**, sus animaciones y su orientación, con movimiento suave
  (interpolación que se adapta a la conexión).
- **Inventario propio** de cada uno: vida, alma, geo, máscaras y vasijas, aguijón y artes, amuletos y muescas, llaves,
  reliquias, mapas, esencia, compras, banco y sombra.
- Se ven y se oyen sus acciones con los efectos del juego:
  - **Tajos del aguijón** (de lado, arriba, abajo y en la pared), más grandes con Mark of Pride y Longnail y con el
    color de Fury of the Fallen.
  - **Artes del aguijón**: carga, Great Slash, Dash Slash y Cyclone Slash.
  - **Hechizos**: Vengeful Spirit y Shade Soul, Desolate Dive y Descending Dark, Howling Wraiths y Abyss Shriek.
  - **Movimiento**: dash y Shade Dash, doble salto, salto de pared, Crystal Heart, polvo al correr y saltar.
  - **Curación**, **aguijón onírico**, **golpes recibidos** y parpadeo de invulnerabilidad.
  - **Amuletos y acompañantes**: Grubberfly's Elegy, Flukenest, Defender's Crest (y su aura), Thorns of Agony,
    Dreamshield, Sharp Shadow, Glowing Womb, Weaversong y Grimmchild; el escudo de Carefree Melody y el caparazón de
    Baldur Shell (al salir, al parar un golpe y al romperse).
  - **Muertes**: en pinchos y en ácido, y la muerte de verdad (en Dios Hogar, o cuando caen todos); el destello de la
    recarga del Shade Dash.
  - **En las salas oscuras** la oscuridad se abre alrededor de cada compañero como alrededor de ti: se ve el túnel a su
    alrededor, no una pantalla negra.
  - El pasto y las telas se mueven a su paso.
- Los efectos son solo visuales: el daño real va por los golpes de cada uno.

</details>

<details>
<summary><b>⚔️ Enemigos y jefes</b></summary>

- Cada enemigo lo mueve el PC del jugador **más cercano** y ataca a todos. Su vida, sus golpes y su muerte son los
  mismos en todos los PCs; los golpes de cualquiera le quitan vida en todos.
- **Proyectiles, crías e invocaciones** se crean en todos y dañan a todos (disparos, cristales, láseres, orbes del
  Maestro de Almas, bolas de Mawlek, agujas de Hornet, tarros del Coleccionista, barriles del Falso Caballero...).
- **Efectos y sonidos** de los enemigos, impactos, música de jefe, **nombre del jefe** al despertar y **barra de vida
  del jefe** con su nombre en el idioma del juego.
- **Piezas y trampas** de los jefes iguales en todos: el rayo del Guardián de Cristal y sus torretas, la aguja de
  Hornet, la cabeza del Falso Caballero...
- **Arenas por oleadas**: Señores Mantis, Coliseo de los Tontos (Bronce, Plata y Oro), Caballeros Vigía...
- **Los combates de jefe esperan a que estén todos los de la sala**, con un aviso de a quién se espera; mientras, el
  jefe es invulnerable. Los **diálogos antes de un combate** los ven todos y cada uno espera a que los demás terminen
  de leer.
- **Presentaciones de jefe** con todos los caballeros quietos, que recuperan el control a la vez.
- **Barras de vida de jefe** solo del jefe que está peleando; el Rey Vengamosca y la Madre Gruz cuentan como jefes.
- Si el jefe cambia de PC a mitad del combate (porque el otro está más cerca), sigue donde iba.
- Los enemigos muertos siguen muertos al volver a la sala, hasta descansar en un banco (como en el juego).

</details>

<details>
<summary><b>🗺️ Mundo compartido</b></summary>

- **Progreso compartido en directo**: jefes vencidos, puertas, palancas, paredes rotas, salas visitadas y mapas.
- **Habilidades de cada jugador** (de movimiento, hechizos, aguijón onírico): cada uno consigue las suyas y ve la
  escena de recogerlas. Los personajes guardados con una versión anterior conservan las que ya tenían.
- **El botín cae en el mismo sitio** en todos los PCs: el geo de los enemigos, de las rocas y de los cofres.
- **Viaje en ciervo juntos** (opción del host): cuando uno elige destino, los compañeros que están cerca de él en la
  estación viajan con él.
- **Bancos compartidos**: hasta 4 jugadores sentados uno al lado del otro en el mismo banco, nunca dentro de la
  pared.
- **Avisos de versión**: en el menú principal sale un aviso si hay una versión nueva (y *Multijugador* ofrece
  descargarla) o si el mod se acaba de actualizar (con sus novedades). Es una consulta a la lista pública de versiones
  de GitHub al abrir el juego; se desactiva en *Multijugador › Buscar actualizaciones*.
- **Los invitados nuevos ven la cinemática del principio** (se puede saltar) antes de entrar al mundo del host.
- **Objetos de sala**: palancas, interruptores, puertas, paredes y suelos que se rompen, piedras de geo, rompibles,
  pasto, enredaderas, tarros de larva, estalactitas, letreros y decorados con su propia lógica.
- **Ascensores**: los mueve quien los usa y el otro lo ve moverse igual.
- **Rejas de batalla de un solo sentido**: si se cierran con uno dentro, el otro puede entrar a ayudar pero no salir.
- **Cofres**: si uno lo abre, el otro lo ve abierto; lo que sueltan es de cada uno.
- **Objetos individuales** (objetos brillantes, fragmentos de máscara y vasija, orbes de sueño): lo que recoge uno no
  desaparece para el otro.
- **Misiones de NPC**: Mr. Mushroom, Quirrel, Bretta, Zote, Tiso, Myla, Hornet, el Forjaguijones, la Compañía de
  Grimm... La misión no avanza dos veces.
- **Conversaciones de cada jugador**: lo que un personaje ya le contó a uno no se lo salta al siguiente (Zote ya no le
  pregunta al segundo por qué lo sigue molestando), y el personaje que se va o cambia de sitio tras hablar (Zote,
  Quirrel, Cloth, Tiso...) solo lo hace para quien habló con él.
- **Peligros animados al mismo ritmo** (sierras y pinchos del Palacio Blanco y del Camino del Dolor).
- Quien entra en una sala la encuentra como está: lo activado, lo roto, los enemigos muertos y los combates en marcha.

</details>

<details>
<summary><b>❤️ Funciones del coop</b></summary>

- **Revivir**: al morir con un compañero en pie no se muere, se cae. El cuerpo queda tendido con "† Nombre" y la
  cámara del caído sigue a su compañero. El compañero recibe un aviso y una flecha hacia el cuerpo; junto a él,
  **manteniendo Focus 2 s** y gastando 33 de alma, lo revive con 1 máscara. Si el compañero se sienta en un banco, los
  caídos reviven a su lado con toda la vida. Si caen todos, cada uno muere de forma normal. Si mueres donde nadie
  podría ponerse a tu lado (en el aire sobre un pozo, en pinchos o en ácido), el cuerpo queda en el último suelo
  seguro, donde el juego te hace reaparecer tras un peligro.
- **Sombras compartidas**: cada uno ve la sombra del compañero y puede matarla; el dueño recupera su geo y su alma
  aunque esté en otra sala.
- **HUD del compañero** arriba a la derecha (nombre, máscaras, alma y dónde está), sus máscaras sobre su cabeza al
  cambiarle la vida y **flechas** en el borde de la pantalla hacia el compañero y los cuerpos.
- **Compañeros en el mapa**: con el mapa abierto, cada compañero sale como una brújula de su color, con su nombre,
  donde está, también dentro de las tiendas (si los dos llevan el Wayward Compass equipado), y en rojo en su cuerpo si
  está caído.
- **Barras de enemigos** opcionales sobre cada enemigo herido.
- **Cinemáticas compartidas**: los finales, los créditos y Mr. Mushroom se ven a la vez aunque estén en salas
  distintas; se saltan cuando todos votan ("Esperando a X para saltar 1/2"). La excepción es el final del Panteón de
  Hallownest: cada uno lo ve al acabar el suyo.
- **Sueños compartidos**: si uno entra en un sueño con el aguijón onírico, los compañeros de la misma sala entran con
  él. Al ganar salen todos; quien cae o muere sale solo. Jefes de sueño, Rey de las Pesadillas y Radiancia.
- **Grimmchild**: si uno lo recibe, el otro también.
- **Skins** compatibles con CustomKnight (*Pausa › Multijugador › Skin*): cuerpo, hechizos y efectos; se envían por la
  red del mod en segundo plano sin retrasar los golpes (la primera vez; luego quedan en una caché y cargan al
  momento), y se puede desactivar ver las de los demás.
- **Compañeros** (*Pausa › Multijugador › Compañeros*): la vida y el alma de cada uno y dónde está, **dar geo**, **ir
  con él** (apareces a su lado, también en otra sala), **desatascarse** (al último suelo seguro) y **recargar la
  sala**.

</details>

---

## 🧪 Estado de las pruebas

Las pruebas se hacen con hasta cuatro copias del juego en el mismo PC, con un modo de prueba del mod y rutas
automáticas que recorren el mundo comprobando que todos ven lo mismo.

✅ **Probado jugando:**

- Conexión, mundos coop y personajes de los invitados.
- Enemigos de todas las zonas.
- Jefes: Mamá Gruz, Falso Caballero (también con mala conexión), Hornet, Mawlek, Maestro de Almas, Defensor del
  Estiércol, Nosk, Vasija Rota, Guardián de Cristal y Guardián Furioso, Señores Mantis, Uumuu, Coleccionista, Señor
  Traidor, Caballeros Vigía, Flukemarm, Rey Vengamosca, Grimm, Tirano de Almas, Hollow Knight, Recipiente Puro,
  Radiancia y Radiancia Absoluta.
- Coliseo de los Tontos: Bronce, Plata y Oro.
- Sueños: Campeón Fallido (el Falso Caballero del sueño), Hermano Perdido, Tirano de Almas, Defensor Blanco, Príncipe Gris Zote y Rey
  de las Pesadillas.
- Revivir (también el cuerpo en el último sitio seguro, sobre pinchos y ácido), sombras, HUD del compañero,
  compañeros en el mapa (también dentro de las tiendas), ajustes del host, cinemáticas, skins, ir con un compañero,
  misiones y conversaciones de NPC, plataformas de liana, el Menderbug, el mod en inglés y la reconexión automática
  (también cancelarla y rendirse).
- Instalar, actualizar y desinstalar.
- 0.26.0: se revisó todo el mod contra el código del juego. Los 172 fallos encontrados se arreglaron o se revisaron, y
  la mayoría se comprobó en el juego; al probarlos salieron y se arreglaron 16 más.
- 0.29.0: la historia entera con 4 jugadores (cuatro copias del juego): todos los jefes con combates de verdad, los
  sueños y los guerreros soñadores, los tres Soñadores, el Huevo Negro, el Hollow Knight (los dos finales) y la
  Radiancia, la Compañía de Grimm, Grey Prince Zote, el Coliseo de los Tontos (Bronce, con Zote), Zote y Cloth, y el
  Panteón del Maestro de principio a fin.
- 0.30.0: todos los combates de jefes otra vez con 4 jugadores, uno a uno, y el Panteón de Hallownest hasta su final,
  que cada jugador acaba en su propio PC. Después, las 16 rutas automáticas con 4 jugadores y un mundo nuevo.
- 0.31.0: la conexión por Steam con un Steam simulado, en el que cada copia del juego hace de una cuenta distinta:
  unirse por la sala y por invitación, las salas privadas, invitados por Steam y por IP en la misma partida, y las
  rutas de prueba con dos jugadores entrando por Steam y uno por IP.

⚠️ **Probado en parte:**

- Caballero de la Colmena y Cargador Musgoso: mueren en todos los PCs, pero falta verlos atacar.

❓ **Sin probar:**

- Una partida entre PCs reales por internet.
- Steam de verdad entre dos cuentas: la parte que habla con Steam (crear la sala, invitar, unirse y la conexión por sus
  relés) solo se ha probado con el Steam simulado.
- Jugar 3 o 4: los casos que solo pasan con tres o más se arreglaron en la 0.28.0 y se probaron con cuatro copias del
  juego en un PC, pero aún no entre PCs reales.

Limitaciones conocidas:

- El jefe ataca al jugador cuyo PC lo mueve, que es el más cercano, y por eso ataca a todos. Cambia de PC como mucho
  cada 8 s y, desde la 0.27.0, solo entre dos ataques (o tras 4 s sin pausa).
- El compañero y los enemigos que mueve otro PC se dibujan unas centésimas en el pasado, para que se muevan suave. En
  lo que cae muy rápido se nota un poco.

La lista completa está en [docs/arquitectura.md](docs/arquitectura.md#pendientes).

---

## 📦 Instalar

> [!NOTE]
> Necesitas **Hollow Knight 1.5.12620 en Windows** (la versión actual de Steam). Todos los jugadores tienen que tener
> **la misma versión** de HKMultiplayer.

1. Descarga `HKMultiplayer-<versión>.zip` de la página de [*Releases*](../../releases).
2. Cierra el juego, **descomprime el zip entero** y haz doble clic en **`HKMultiplayer Setup.exe`**.
3. En la ventana que se abre, comprueba la carpeta del juego y pulsa **Instalar**.
4. Abre Hollow Knight desde Steam: en el menú principal aparece **Multijugador**.

Windows puede avisar al abrir un archivo descargado de internet: pulsa *Ejecutar* (o *Más información › Ejecutar de
todas formas*). El instalador es de código abierto: su código está en [`tools/installer/launcher`](tools/installer/launcher).
Si prefieres no usarlo, instálalo a mano (más abajo).

<details>
<summary><b>Qué hace el instalador</b></summary>

- Busca la carpeta del juego (Steam, sus bibliotecas y las rutas típicas); si hay varias, la eliges en la lista, y si
  no la encuentra, con *Buscar…*.
- Comprueba que el juego es la versión 1.5.12620 y que está cerrado, y que el paquete está completo (`SHA256SUMS.txt`).
- Guarda en `<juego>\HKMultiplayer-backup` una copia de cada archivo que cambia y lo apunta todo en `manifest.txt`.
- Quita el mod anterior **HollowKnightCoop** si está y aparta los demás mods a `Mods\Disabled`, diciéndote cuáles
  antes de instalar (los hechos para otra
  versión de la Modding API pueden romper el juego).
- Copia la Modding API y el mod, y comprueba lo copiado. Instalar una versión nueva encima es una actualización.
- Al abrirse mira en GitHub: si hay una versión más nueva que el paquete, un cartel arriba ofrece descargarla.
  **Novedades** (abajo a la derecha) enseña lo que trae esa versión, o la del paquete si no hay otra más nueva.
- **Desinstalar** deshace todo al revés: el juego queda como estaba. Tus mundos coop, partidas y skins no se tocan.
- También funciona en la consola: `start /wait "" "HKMultiplayer Setup.exe" /install "D:\Juegos\Hollow Knight"` (o
  `/uninstall`).

</details>

¿Prefieres no usar el instalador? El `LEEME.txt` (español) y el `README.txt` (inglés) del paquete explican la
**instalación a mano**, cómo abrir el puerto y qué hacer si algo falla.

---

## 🎮 Cómo jugar

1. **Host**: menú principal › *Multijugador* › *Hostear un mundo* › *Nuevo mundo* (o elige uno) › *Crear y hostear*.
   Con Steam, tus amigos ya pueden unirse; para invitar a alguien: *Pausa › Multijugador › Invitar amigos de Steam*.
   Por IP, pasa tu **IP**, el **puerto** (26950) y el **código** que sale en pantalla (también en
   *Pausa › Multijugador*).
2. **Invitado por Steam**: acepta la invitación o pulsa *Unirse a la partida* en la lista de amigos de Steam (también
   *Multijugador › Unirse a un mundo › Unirse a…*). Si estás jugando tu partida normal, sal al menú principal y entras
   solo.
3. **Invitado por IP**: menú principal › *Multijugador* › *Unirse a un mundo*, con la dirección, el puerto y el código.
4. **Por internet por IP**: el host abre el puerto **UDP 26950** en su router (o se usa Radmin VPN, ZeroTier o
   Tailscale). Por Steam o en la misma casa no hace falta abrir nada.

---

## ⚙️ Ajustes de la partida

Los decide el host en *Pausa › Multijugador › Ajustes de la partida*, se guardan en cada mundo y los invitados los ven
al momento sin poder cambiarlos.

| Ajuste | Valores | Por defecto |
|---|---|---|
| Vida de los jefes | 1× · 1,25× · 1,5× · 2× · 2,5× · 3× | 1,5× |
| Vida de los enemigos | 1× · 1,25× · 1,5× · 2× | 1× |
| Más vida solo con compañero en la sala | Sí · No | Sí |
| Daño recibido | 0,5× · 1× · 1,5× · 2× | 1× |
| Revivir | Sí · No | Sí |
| Tiempo para revivir | 1 · 2 · 3 · 5 s | 2 s |
| Alma para revivir | 0 · 33 · 66 · 99 | 33 |
| Máscaras al revivir | 1 · 2 · 3 · Todas | 1 |
| Invulnerable al revivir | 1 · 2 · 3 · 5 s | 2 s |
| Revivir en el banco del compañero | Sí · No | Sí |
| Nombres · Vida del compañero · Flechas · Compañeros en el mapa · Avisos · Barra del jefe | Sí · No | Sí |
| Barras de enemigos | Sí · No | No |
| Regalar geo · Permitir ir con un compañero | Sí · No | Sí |
| Saltar cinemáticas | Votación · Cualquiera | Votación |
| Viajar juntos en ciervo | Sí · No | Sí |

La vida de los jefes y de los enemigos es la vida extra con un compañero: cada compañero más en pie en la sala suma lo
mismo otra vez (1,5× es 2× con tres jugadores y 2,5× con cuatro). Los jefes que se aturden tras unos golpes (Hornet,
Vasija Rota, Grimm...) necesitan tantas veces más golpes como jugadores haya en la sala, así no se pasan el combate
aturdidos cuando pegan varios a la vez.

---

## 🔧 Tecnología

| Componente | Para qué se usa |
|---|---|
| **Hollow Knight Modding API** (Unity 6) | Carga del mod y ganchos del juego. Versión modificada para 1.5.12620, incluida en [`modding-api/`](modding-api). |
| **MonoMod** | Ganchos sobre el código del juego (`MMHOOK_*.dll`, `MonoMod.RuntimeDetour`). |
| **PlayMaker** | Lectura y control de las máquinas de estados del juego: enemigos, jefes, ascensores, rejas, arenas, diálogos. |
| **BouncyCastle** | Cifrado: X25519, HKDF-SHA256 con el código de la partida y ChaCha20-Poly1305 en cada paquete. |
| **Steamworks.NET** (el que trae el juego) | Salas de Steam, invitaciones y conexión entre cuentas (SteamNetworkingMessages, directa o por los relés de Valve) para jugar sin abrir puertos. |
| **Unity UI** | Menús con el estilo del juego, avisos y HUD del compañero (con los sprites del HUD del juego). |

- **Red propia sobre UDP o Steam**: canales fiable y no fiable, transferencias grandes troceadas (el mundo al unirse), latido y
  desconexión limpia, sin memoria nueva por paquete para no provocar tirones.
- **Seguridad**: solo entra quien conoce el código de la partida; todo viaja cifrado y autenticado, también por Steam
  (Steam solo lleva los paquetes). Por Steam solo se aceptan conexiones de amigos, de los de la sala o del host al que
  te unes.
- **Sin servidores propios**: por IP la conexión es directa entre los PCs; por Steam la lleva Steam.

---

## 💻 Desarrollo

> [!NOTE]
> Los identificadores del código están en inglés; los comentarios, los registros y las notas de diseño de `docs/`, en
> español.

<details>
<summary><b>Estructura del repositorio</b></summary>

| Carpeta | Contenido |
|---|---|
| `src/HKMultiplayer/Net/` | Transporte UDP, canales, cifrado y código de partida (sin Unity, se prueba fuera del juego). |
| `src/HKMultiplayer/Session/` | Sesión: host y unirse, jugadores, salas y dueño de cada sala, reparto de mensajes. |
| `src/HKMultiplayer/Game/Players/` | Estado de los jugadores, interpolación, avatares del compañero y sus efectos. |
| `src/HKMultiplayer/Game/World/` | Mundos coop, guardado, personajes de los invitados, progreso compartido y misiones de NPC. |
| `src/HKMultiplayer/Game/Features/` | Funciones del coop: estado de cada jugador, ajustes del host, revivir, sombras, HUD, compañeros en el mapa, barras, regalar geo, ir con un compañero, sueños, cinemáticas, skins. |
| `src/HKMultiplayer/Game/Scene/` | Objetos de sala, rompibles, ascensores y rejas, controladores de batalla, enemigos y jefes, peligros animados. |
| `src/HKMultiplayer/UI/` | Menús, avisos, idioma y el lienzo del HUD del coop. |
| `src/HKMultiplayer/Testing/` | Modo de prueba: órdenes por archivo para probar con varias copias del juego. |
| `modding-api/` | Código de la Modding API (hk-modding/api, MIT) para Unity 6: rama `hk-beta-branch-unity-6-update` de `SFGrenade/api` (PR hk-modding/api#164, commit `468f579`) con cambios propios. Sin los archivos del juego. |
| `tests/` | Pruebas del transporte fuera del juego y rutas de prueba del coop (`tests/routes/`). |
| `tools/` | Preparar el PC (`setup-dev.sh`), compilar e instalar (`test-cycle.sh`), pruebas con varias copias, rutas automáticas, paquete e instalador. |
| `docs/` | [Arquitectura](docs/arquitectura.md): diseño, decisiones, pruebas y pendientes. |
| `lib/`, `vendor/`, `test-instances/` | *(solo local, no se suben)* DLL del juego y de la Modding API, y copias del juego para probar. |
| `decomp/` | *(solo local, no se sube)* Código del juego descompilado con `tools/decompile.sh`, para consultar. |

Las DLL del juego son de Team Cherry y no están en el repositorio: se sacan del juego instalado.

</details>

### Compilar (también en otro PC)

Necesitas Hollow Knight 1.5.12620 instalado, el SDK de .NET (`dotnet`) y Git Bash.

```bash
git clone https://github.com/Yesid-Robayo/HKMultiplayerMOD.git
cd HKMultiplayerMOD
bash tools/setup-dev.sh "C:/Program Files (x86)/Steam/steamapps/common/Hollow Knight"   # una vez
bash tools/test-cycle.sh            # compila el mod e instala en las copias de prueba (--game: también en el juego)
dotnet run --project tests/NetTests # pruebas del transporte
bash tools/package.sh               # paquete para jugadores: out/package/HKMultiplayer-<versión>.zip
```

`setup-dev.sh` copia la carpeta `Managed` del juego **sin modificar** a `modding-api/Vanilla`, compila la Modding API
y deja `lib/` y `vendor/`. Si el juego ya tiene la Modding API, usa los originales que guardó el instalador
(`HKMultiplayer-backup`); si no los hay, pide verificar la integridad de los archivos del juego en Steam. Para cambiar la
Modding API se edita `modding-api/` y se vuelve a ejecutar.

`bash tools/decompile.sh` descompila el código del juego en `decomp/` para ver cómo hace algo (necesita `ilspycmd`).
Las FSM de PlayMaker no salen ahí; ver [Código del juego](docs/arquitectura.md#código-del-juego).

### Probar

- **Hasta cuatro jugadores en un PC**: `tools/mp-test-lib.sh` y `tools/routes/lib.sh` (`setup_group`) abren copias del
  juego (`test-instances/`) en modo de prueba y les mandan órdenes por archivo: hostear y unirse, ir a una sala,
  moverse, golpear, capturas de todas a la vez, estado de enemigos y objetos, mala conexión simulada (`netsim`)... Órdenes en
  [docs/arquitectura.md](docs/arquitectura.md#pruebas).
- **Rutas automáticas**: `bash tools/run-routes.sh` recorre el juego por zonas con un host e invitados y comprueba en
  cada sala que todos ven lo mismo (`--players 4` las juega con cuatro, `--pairs 3` reparte las zonas entre 3 pares y `--steam` hace entrar a
  los invitados por un Steam simulado, en el que cada copia es una cuenta). Formato:
  [tests/routes/README.md](tests/routes/README.md).
- **Instalador**: `bash tools/manual-tests/installer-test.sh <zip>` instala, arranca, desinstala y compara en copias
  limpias, sin tocar el juego de Steam.

---

## 🙏 Créditos

- **[Hollow Knight](https://www.hollowknight.com/)** es de **Team Cherry**. Este mod es un proyecto no oficial de
  aficionados y no está afiliado a Team Cherry. El repositorio no contiene archivos del juego; el paquete de jugadores,
  como las versiones oficiales de la Modding API, incluye `Assembly-CSharp.dll` y `TeamCherry.Localization.dll` del
  juego ya modificados por la Modding API.
- **[Hollow Knight Modding API](https://github.com/hk-modding/api)** (MIT), con la adaptación a Unity 6 de
  [SFGrenade](https://github.com/SFGrenade/api).
- **[MonoMod](https://github.com/MonoMod/MonoMod)**, **[Mono.Cecil](https://github.com/jbevain/cecil)**,
  **[unity-scene-repacker](https://github.com/jakobhellermann/unity-scene-repacker)**,
  **[BouncyCastle](https://www.bouncycastle.org/)** y **[Newtonsoft.Json](https://www.newtonsoft.com/json)**, todos MIT
  (avisos completos en [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)).
- Skins compatibles con **CustomKnight**.

---

## 📄 Licencia y contribuciones

- El código de HKMultiplayer tiene licencia **[MIT](LICENSE)**. Se entrega **tal cual, sin ninguna garantía**: úsalo
  bajo tu propia responsabilidad y haz copia de tus partidas. La Modding API de [`modding-api/`](modding-api) mantiene
  su propia licencia MIT.
- **No se aceptan *pull requests*** (se cierran automáticamente). Los errores y las ideas, en un *issue*; ver
  [CONTRIBUTING.md](CONTRIBUTING.md).
- Los problemas de seguridad se avisan en privado: [SECURITY.md](SECURITY.md).
