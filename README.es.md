<div align="center">

# HKMultiplayer

**Mod cooperativo para Hollow Knight: juega el mundo entero con tus amigos, en red local o por internet.**

![Versión](https://img.shields.io/badge/versión-0.21.1-7fb2ff)
![Estado](https://img.shields.io/badge/estado-beta-f0c86a)
![Hollow Knight](https://img.shields.io/badge/Hollow%20Knight-1.5.12620-e8ecf3)
![Plataforma](https://img.shields.io/badge/plataforma-Windows-2f5fb3)
![Idiomas](https://img.shields.io/badge/idiomas-español%20%7C%20english-7ddc8f)

**Español** · [English](README.md)

</div>

> [!WARNING]
> **Versión beta.** Casi todo se ha probado jugando con dos copias del juego en el mismo PC, pero todavía no se ha
> jugado una partida larga entre dos PCs por internet, y algunas partes no se han probado del todo (ver
> [Estado de las pruebas](#-estado-de-las-pruebas)). **Puede tener errores.** Haz una copia de tus partidas antes de
> jugar y, si algo falla, cuéntalo en un *issue* con una captura y tu `ModLog.txt`.

Un jugador **hostea** un mundo coop y sus compañeros **se unen** con la dirección del host y el código de la partida:
comparten el mundo, se ven en tiempo real y pelean juntos contra los mismos enemigos y jefes. Está pensado para dos
jugadores (admite algunos más) y escrito desde cero sobre la Modding API de Hollow Knight.

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

- 🌍 **Mundos coop**: hasta 4 mundos propios, guardados en el PC del host, aparte de tus partidas normales.
- 🔒 **Conexión**: red local o internet, con código de partida y todo cifrado, sin Steam ni servidores intermedios.
- 👥 **Jugadores**: el compañero se ve en tiempo real con su nombre, animaciones, ataques, hechizos y amuletos.
- ⚔️ **Enemigos y jefes**: los mismos en todos los PCs (misma vida, mismos golpes, misma muerte); los jefes esperan a
  que estéis los dos.
- 🗺️ **Mundo compartido**: progreso, palancas, puertas, paredes rotas, ascensores, rejas, cofres y misiones de NPC, a la
  vez.
- ❤️ **Revivir**: si caes con un compañero en pie, quedas tendido y te revive manteniendo Focus junto a tu cuerpo.
- 🌙 **Sueños y cinemáticas**: entráis juntos a los sueños; los finales y los créditos se ven a la vez y se saltan por
  votación.
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
- Funciona en **red local y por internet** (en internet el host abre el puerto UDP en su router o usáis una VPN de red
  local como Radmin VPN, ZeroTier o Tailscale).
- **Personaje de cada invitado** guardado en el mundo del host: se envía en los bancos, cada 30 segundos y al salir.
  Al volver a unirte, recuperas tu personaje. Si el host cierra la partida, primero recoge los de los invitados.
- **Avisos** en pantalla: código de la partida, quién entra y sale, desconexiones y esperas de combate.
- **Panel en pausa** (*Pausa › Multijugador*): jugadores conectados con su ping, código, IP y cerrar o salir de la
  partida; *Ajustes de la partida*, *Compañeros* y *Skin*.

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
    Dreamshield, Sharp Shadow, Glowing Womb, Weaversong y Grimmchild.
  - El pasto y las telas se mueven a su paso.
- Los efectos son solo visuales: el daño real va por los golpes de cada uno.

</details>

<details>
<summary><b>⚔️ Enemigos y jefes</b></summary>

- Cada enemigo lo mueve el PC del jugador **más cercano** y ataca a los dos. Su vida, sus golpes y su muerte son los
  mismos en todos los PCs; los golpes de cualquiera le quitan vida en todos.
- **Proyectiles, crías e invocaciones** se crean en todos y dañan a todos (disparos, cristales, láseres, orbes del
  Maestro de Almas, bolas de Mawlek, agujas de Hornet, tarros del Coleccionista, barriles del Falso Caballero...).
- **Efectos y sonidos** de los enemigos, impactos, música de jefe, **nombre del jefe** al despertar y **barra de vida
  del jefe** con su nombre en el idioma del juego.
- **Piezas y trampas** de los jefes iguales en todos: el rayo del Guardián de Cristal y sus torretas, la aguja de
  Hornet, la cabeza del Falso Caballero...
- **Arenas por oleadas**: Señores Mantis, Coliseo de los Tontos (Bronce, Plata y Oro), Caballeros Vigía...
- **Los combates de jefe esperan a que estéis los dos**, con un aviso de a quién se espera; mientras, el jefe es
  invulnerable. Los **diálogos antes de un combate** los ven los dos y cada uno espera a que el otro termine de leer.
- **Presentaciones de jefe** con los dos caballeros quietos, que recuperan el control a la vez.
- Si el jefe cambia de PC a mitad del combate (porque el otro está más cerca), sigue donde iba.
- Los enemigos muertos siguen muertos al volver a la sala, hasta descansar en un banco (como en el juego).

</details>

<details>
<summary><b>🗺️ Mundo compartido</b></summary>

- **Progreso compartido en directo**: habilidades de movimiento y hechizos, jefes vencidos, puertas, palancas, paredes
  rotas, salas visitadas y mapas.
- **Objetos de sala**: palancas, interruptores, puertas, paredes y suelos que se rompen, piedras de geo, rompibles,
  pasto, enredaderas, tarros de larva, estalactitas, letreros y decorados con su propia lógica.
- **Ascensores**: los mueve quien los usa y el otro lo ve moverse igual.
- **Rejas de batalla de un solo sentido**: si se cierran con uno dentro, el otro puede entrar a ayudar pero no salir.
- **Cofres**: si uno lo abre, el otro lo ve abierto; lo que sueltan es de cada uno.
- **Objetos individuales** (objetos brillantes, fragmentos de máscara y vasija, orbes de sueño): lo que recoge uno no
  desaparece para el otro.
- **Misiones de NPC**: Mr. Mushroom, Quirrel, Bretta, Zote, Tiso, Myla, Hornet, el Forjaguijones, la Compañía de
  Grimm... El personaje avanza o se va en los dos PCs y la misión no avanza dos veces.
- **Peligros animados al mismo ritmo** (sierras y pinchos del Palacio Blanco y del Camino del Dolor).
- Quien entra en una sala la encuentra como está: lo activado, lo roto, los enemigos muertos y los combates en marcha.

</details>

<details>
<summary><b>❤️ Funciones del coop</b></summary>

- **Revivir**: al morir con un compañero en pie no se muere, se cae. El cuerpo queda tendido con "† Nombre" y la
  cámara del caído sigue a su compañero. El compañero recibe un aviso y una flecha hacia el cuerpo; junto a él,
  **manteniendo Focus 2 s** y gastando 33 de alma, lo revive con 1 máscara. Si el compañero se sienta en un banco, los
  caídos reviven a su lado con toda la vida. Si caen todos, cada uno muere de forma normal.
- **Sombras compartidas**: cada uno ve la sombra del compañero y puede matarla; el dueño recupera su geo y su alma
  aunque esté en otra sala.
- **HUD del compañero** arriba a la derecha (nombre, máscaras, alma y dónde está), sus máscaras sobre su cabeza al
  cambiarle la vida y **flechas** en el borde de la pantalla hacia el compañero y los cuerpos.
- **Barras de enemigos** opcionales sobre cada enemigo herido.
- **Cinemáticas compartidas**: los finales, los créditos y Mr. Mushroom se ven a la vez aunque estéis en salas
  distintas; se saltan cuando todos votan ("Esperando a X para saltar 1/2").
- **Sueños compartidos**: si uno entra en un sueño con el aguijón onírico, el compañero de la misma sala entra con él.
  Al ganar salen los dos; quien cae o muere sale solo. Jefes de sueño, Rey de las Pesadillas y Radiancia.
- **Grimmchild**: si uno lo recibe, el otro también.
- **Skins** compatibles con CustomKnight (*Pausa › Multijugador › Skin*): cuerpo, hechizos y efectos; se envían por la
  red del mod en segundo plano sin retrasar los golpes, y se puede desactivar ver las de los demás.
- **Compañeros** (*Pausa › Multijugador › Compañeros*): la vida y el alma de cada uno y dónde está, **dar geo**, **ir
  con él** (apareces a su lado, también en otra sala), **desatascarse** (al último suelo seguro) y **recargar la
  sala**.

</details>

---

## 🧪 Estado de las pruebas

Las pruebas se hacen con dos (o tres) copias del juego en el mismo PC, con un modo de prueba del mod y rutas
automáticas que recorren el mundo comprobando que los dos ven lo mismo.

✅ **Probado jugando:**

- Conexión, mundos coop y personajes de los invitados.
- Enemigos de todas las zonas.
- Jefes: Mamá Gruz, Falso Caballero (también con mala conexión), Hornet, Mawlek, Maestro de Almas, Defensor del
  Estiércol, Nosk, Vasija Rota, Guardián de Cristal, Señores Mantis, Uumuu, Coleccionista, Señor Traidor, Caballeros
  Vigía, Grimm, Tirano de Almas, Hollow Knight y Radiancia.
- Coliseo de los Tontos: Bronce, Plata y Oro.
- Sueños: Campeón Fallido (el Falso Caballero del sueño), Hermano Perdido, Tirano de Almas, Defensor Blanco, Príncipe Gris Zote y Rey
  de las Pesadillas.
- Revivir, sombras, HUD del compañero, ajustes del host, cinemáticas, skins, ir con un compañero y misiones de NPC.
- Instalar, actualizar y desinstalar.

⚠️ **Probado en parte:**

- Caballero de la Colmena, Flukemarm, Cargador Musgoso y Rey Mosca: mueren en los dos PCs, pero falta verlos atacar.
- El rescate de Zote: el combate sí, la conversación de después no.

❓ **Sin probar:**

- Una partida entre dos PCs por internet.
- La traducción al inglés dentro del juego.
- El vapor naranja de la muerte del Falso Caballero (arreglado sin volver a probarlo).
- Cloth y Menderbug, la entrada a la Radiancia por el camino del juego y la vuelta de las cinemáticas de los Panteones.

Limitaciones conocidas:

- El jefe ataca al jugador cuyo PC lo mueve, que es el más cercano. Cada cambio de PC (como mucho cada 8 s) corta su
  ataque, y por eso ataca a los dos.
- El compañero y los enemigos que mueve otro PC se dibujan unas centésimas en el pasado, para que se muevan suave. En
  lo que cae muy rápido se nota un poco.
- Si un compañero cambia algo que hace aparecer a un personaje que el juego quitó al cargar tu sala, no aparece hasta
  que vuelvas a entrar.

La lista completa está en [docs/arquitectura.md](docs/arquitectura.md#pendientes).

---

## 📦 Instalar

> [!NOTE]
> Necesitas **Hollow Knight 1.5.12620 en Windows** (la versión actual de Steam). Todos los jugadores tienen que tener
> **la misma versión** de HKMultiplayer.

1. Descarga `HKMultiplayer-<versión>.zip` de la página de [*Releases*](../../releases).
2. Cierra el juego, **descomprime el zip entero** y haz doble clic en **`HKMultiplayer Setup.bat`**.
3. En la ventana que se abre, comprueba la carpeta del juego y pulsa **Instalar**.
4. Abre Hollow Knight desde Steam: en el menú principal aparece **Multijugador**.

Windows puede avisar al abrir un archivo descargado de internet: pulsa *Ejecutar* (o *Más información › Ejecutar de
todas formas*). El instalador es un script de PowerShell que puedes leer en la carpeta `scripts` del paquete.

<details>
<summary><b>Qué hace el instalador</b></summary>

- Busca la carpeta del juego (Steam, sus bibliotecas y las rutas típicas); si hay varias, la eliges en la lista, y si
  no la encuentra, con *Buscar…*.
- Comprueba que el juego es la versión 1.5.12620 y que está cerrado, y que el paquete está completo (`SHA256SUMS.txt`).
- Guarda en `<juego>\HKMultiplayer-backup` una copia de cada archivo que cambia y lo apunta todo en `manifest.txt`.
- Quita el mod anterior **HollowKnightCoop** si está y aparta los demás mods a `Mods\Disabled` (los hechos para otra
  versión de la Modding API pueden romper el juego).
- Copia la Modding API y el mod, y comprueba lo copiado. Instalar una versión nueva encima es una actualización.
- **Desinstalar** deshace todo al revés: el juego queda como estaba. Tus mundos coop, partidas y skins no se tocan.
- También funciona en la consola: `"HKMultiplayer Setup.bat" /install "D:\Juegos\Hollow Knight"` (o `/uninstall`).

</details>

¿Prefieres no usar el instalador? El `LEEME.txt` (español) y el `README.txt` (inglés) del paquete explican la
**instalación a mano**, cómo abrir el puerto y qué hacer si algo falla.

---

## 🎮 Cómo jugar

1. **Host**: menú principal › *Multijugador* › *Hostear un mundo* › *Nuevo mundo* (o elige uno) › *Crear y hostear*.
   Pasa a tus compañeros tu **IP**, el **puerto** (26950) y el **código** que sale en pantalla (también en
   *Pausa › Multijugador*).
2. **Invitado**: menú principal › *Multijugador* › *Unirse a un mundo*, con la dirección, el puerto y el código.
3. **Por internet**: el host abre el puerto **UDP 26950** en su router (o usáis Radmin VPN, ZeroTier o Tailscale). En
   la misma casa no hace falta abrir nada.

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
| Nombres · Vida del compañero · Flechas · Avisos · Barra del jefe | Sí · No | Sí |
| Barras de enemigos | Sí · No | No |
| Regalar geo · Permitir ir con un compañero | Sí · No | Sí |
| Saltar cinemáticas | Votación · Cualquiera | Votación |

---

## 🔧 Tecnología

| Componente | Para qué se usa |
|---|---|
| **Hollow Knight Modding API** (Unity 6) | Carga del mod y ganchos del juego. Versión modificada para 1.5.12620, incluida en [`modding-api/`](modding-api). |
| **MonoMod** | Ganchos sobre el código del juego (`MMHOOK_*.dll`, `MonoMod.RuntimeDetour`). |
| **PlayMaker** | Lectura y control de las máquinas de estados del juego: enemigos, jefes, ascensores, rejas, arenas, diálogos. |
| **BouncyCastle** | Cifrado: X25519, HKDF-SHA256 con el código de la partida y ChaCha20-Poly1305 en cada paquete. |
| **Unity UI** | Menús con el estilo del juego, avisos y HUD del compañero (con los sprites del HUD del juego). |

- **Red propia sobre UDP**: canales fiable y no fiable, transferencias grandes troceadas (el mundo al unirse), latido y
  desconexión limpia, sin memoria nueva por paquete para no provocar tirones.
- **Seguridad**: solo entra quien conoce el código de la partida; todo viaja cifrado y autenticado.
- **Sin servicios externos**: la conexión es directa entre los PCs.

---

## 💻 Desarrollo

<details>
<summary><b>Estructura del repositorio</b></summary>

| Carpeta | Contenido |
|---|---|
| `src/HKMultiplayer/Net/` | Transporte UDP, canales, cifrado y código de partida (sin Unity, se prueba fuera del juego). |
| `src/HKMultiplayer/Session/` | Sesión: host y unirse, jugadores, salas y dueño de cada sala, reparto de mensajes. |
| `src/HKMultiplayer/Game/Players/` | Estado de los jugadores, interpolación, avatares del compañero y sus efectos. |
| `src/HKMultiplayer/Game/World/` | Mundos coop, guardado, personajes de los invitados, progreso compartido y misiones de NPC. |
| `src/HKMultiplayer/Game/Features/` | Funciones del coop: estado de cada jugador, ajustes del host, revivir, sombras, HUD, barras, regalar geo, ir con un compañero, sueños, cinemáticas, skins. |
| `src/HKMultiplayer/Game/Scene/` | Objetos de sala, rompibles, ascensores y rejas, controladores de batalla, enemigos y jefes, peligros animados. |
| `src/HKMultiplayer/UI/` | Menús, avisos, idioma y el lienzo del HUD del coop. |
| `src/HKMultiplayer/Testing/` | Modo de prueba: órdenes por archivo para probar con varias copias del juego. |
| `modding-api/` | Código de la Modding API (hk-modding/api, MIT) para Unity 6: rama `hk-beta-branch-unity-6-update` de `SFGrenade/api` (PR hk-modding/api#164, commit `468f579`) con cambios propios. Sin los archivos del juego. |
| `tests/` | Pruebas del transporte fuera del juego y rutas de prueba del coop (`tests/routes/`). |
| `tools/` | Preparar el PC (`setup-dev.sh`), compilar e instalar (`test-cycle.sh`), pruebas con varias copias, rutas automáticas, paquete e instalador. |
| `docs/` | [Arquitectura](docs/arquitectura.md): diseño, decisiones, pruebas y pendientes. |
| `lib/`, `vendor/`, `test-instances/` | *(solo local, no se suben)* DLL del juego y de la Modding API, y copias del juego para probar. |

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

### Probar

- **Dos jugadores en un PC**: `tools/mp-test-lib.sh` abre dos copias del juego (`test-instances/b` y `c`) en modo de
  prueba y les manda órdenes por archivo: hostear y unirse, ir a una sala, moverse, golpear, capturas de las dos a la
  vez, estado de enemigos y objetos, mala conexión simulada (`netsim`)... Órdenes en
  [docs/arquitectura.md](docs/arquitectura.md#pruebas).
- **Rutas automáticas**: `bash tools/run-routes.sh` recorre el juego por zonas con un host y un invitado y comprueba en
  cada sala que los dos ven lo mismo (`--pairs 3` reparte las zonas entre 3 pares). Formato:
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
