# Cambios por versión

**Español** · [English](CHANGELOG.md)

Qué trajo y qué arregló cada versión de HKMultiplayer, de la más nueva a la más vieja. Las versiones publicadas
tienen su zip, sus notas completas en inglés y en español y su SHA256 en
[Releases](https://github.com/Yesid-Robayo/HKMultiplayerMOD/releases). **Todos los jugadores necesitan la misma
versión**: dos versiones distintas no pueden jugar juntas.

Al publicar una versión nueva, se añade aquí arriba con lo mismo que su sección *Novedades* de las notas de la release.

## 0.32.1

- **Tus tajos con tu skin**: con una skin, los tajos y las artes del aguijón se veían como los del juego en tu pantalla,
  aunque los demás jugadores ya los veían con tu skin. Ahora también los ves con tu skin.

## 0.32.0

- **Los Señores Mantis ya no se saltan un ataque**: en la segunda fase, un Señor Mantis podía quedarse escondido y quieto
  un rato cuando el combate pasaba de un PC a otro, porque la orden de su siguiente ataque llegaba justo durante el
  cambio. Pasaba más jugando por internet. Ahora el PC que toma al señor guarda esa orden y la cumple.
- **Vida de los jefes 4×**: nuevo valor en la opción *Vida de los jefes*.

## 0.31.3

- **Jefes aturdidos menos seguido con varios jugadores**: los jefes que se aturden tras unos golpes (Hornet, Vasija
  Rota, Hermano Perdido, Maestro y Tirano de Almas, Defensor del Estiércol, Coleccionista, Caballero de la Colmena, Grimm,
  Príncipe Gris Zote, Sly, Sheo y el Hollow Knight) necesitan tantas veces más golpes como jugadores haya en la sala. Con
  dos, Hornet pide 20 golpes en vez de 10; como pegan el doble de rápido, se aturde más o menos tan a menudo como jugando
  solo. El daño y la vida no cambian.
- **El HUD de la skin también en las salas de jefe**: al entrar directo en la sala de un jefe, el HUD de la skin (como el
  frasco de Rick Knight) no se ponía en todo el combate y se veía el normal. Ahora se prepara mientras carga la sala.
- El README tiene un GIF de dos PCs peleando juntos contra Hornet y un enlace a Ko-fi para apoyar el proyecto.

## 0.31.2

- **Desinstalar devuelve el juego entero como estaba**: se quedaba el `TeamCherry.Localization.dll` de la Modding API en
  vez de volver el original del juego (pasaba con todas las versiones del instalador). Si ya desinstalaste una versión
  anterior y el juego sin mods da problemas: Steam › clic derecho en Hollow Knight › Propiedades › Archivos instalados ›
  Verificar integridad.
- El paquete ya no lleva rutas del PC donde se compiló.
- Instrucciones en español neutro.

## 0.31.1

Arreglos de la conexión por Steam, vistos con Steam de verdad.

- **"Invitar a jugar" en la lista de amigos de Steam**: antes, fuera del juego solo salía "Invitar a ver"; ahora sale
  "Invitar a jugar" mientras hosteas, y tus amigos ven "Unirse a la partida". También con "Solo invitados": ahí, a quien
  pulse "Unirse" sin invitación no lo deja entrar. Lo seguro con "Solo invitados" es invitar desde *Pausa › Multijugador
  › Invitar amigos de Steam*.
- **Por qué no puedes entrar**: si no se puede entrar en una partida de Steam, se dice el motivo (solo con invitación,
  llena o ya cerrada) en vez de un error general.
- Cuando el host cierra la partida, el invitado sale también de su sala de Steam.

## 0.31.0

**Jugar por Steam, sin abrir puertos.**

- **Sala de Steam**: al hostear se abre una sala de Steam. Tus amigos ven *Unirse a la partida* en su lista de amigos
  de Steam, o tu partida en *Multijugador › Unirse a un mundo* con un botón para entrar, sin escribir dirección ni
  código.
- **Invitar**: *Pausa › Multijugador › Invitar amigos de Steam* abre el panel de Steam para invitar a quien quieras
  (también desde la lista de amigos de Steam). Al aceptar, el mod entra solo en la partida; si el juego estaba
  cerrado, Steam lo abre, y si el amigo está jugando su partida normal, entra al volver al menú principal.
- **Quién entra por Steam**: en *Multijugador*, *Partidas por Steam*: **Amigos** (cualquier amigo de Steam),
  **Solo invitados** (sala privada: solo entra quien recibe la invitación) o **Nadie** (solo por IP).
- **Steam e IP a la vez**: en la misma partida unos entran por Steam y otros por IP. Si no se puede abrir el puerto,
  la partida se abre igual por Steam. Quien no tenga Steam sigue entrando al host por IP.
- **Seguridad**: la conexión por Steam va cifrada con el código de la partida, como por IP (Steam solo lleva los
  paquetes), y solo se aceptan conexiones de amigos, de los de la sala o del host al que te unes.
- La reconexión automática también funciona por Steam, y el host comprueba la versión antes de dejar entrar.

## 0.30.0

Todos los combates de jefes jugados otra vez con 4 jugadores, uno a uno, y el Panteón de Hallownest hasta su final.

- **Final del Panteón de Hallownest**: cada jugador acaba la Radiancia en su PC. Todos dan los golpes finales con su
  propio botón de atacar y ven la cinemática del final al acabar el suyo; antes el primero en acabar se llevaba a los
  demás. Durante el final no se ven los caballeros ni los nombres de los compañeros.
- **Subida de la Radiancia**: el abismo sube para todos cuando sube cualquiera, y cada jugador reaparece en la última
  plataforma pasada en vez de caer al abismo una y otra vez. Sus rayos, la sombra que sube y las plataformas se ven en
  todos los PCs, sus orbes salen donde se cargan y ya no se queda quieta en el aire si cambia de PC a mitad de un
  teletransporte.
- **Recipiente Puro**: su nombre y su ataque de burbujas salen para todos, en los mismos sitios.
- **Panteones**: los compañeros que entran con el que empieza un Panteón ya no se quedan con la pantalla negra, y el
  que cae en un Panteón revive junto al grupo en la sala del siguiente jefe.
- **Personajes, de cada jugador**: cada uno oye cada conversación desde el principio (Zote ya no le pregunta al
  segundo por qué lo sigue molestando), y el personaje que se va tras hablar (Zote, Quirrel, Cloth, Tiso...) solo se
  va para el jugador que habló con él.
- **Barras de vida**: solo tiene barra el jefe que está peleando (los seis Caballeros Vigía mostraban seis). El Rey
  Vengamosca y la Madre Gruz cuentan como jefes: barra, vida extra y espera a todos.
- **Señores Mantis**: dos jugadores pueden desafiarlos a la vez. **Maestro de Almas**: el orbe falso desaparece para
  todos, todos caen al piso de abajo y su orbe sale donde se carga. **Hornet**: la Capa de Polilla cae en todos los
  PCs y el sueño de los Soñadores empieza en cuanto cada jugador la recoge.
- **Enemigos que solo tienen algunos PCs** (una parte de la sala que no se cargó en el PC del dueño de la sala): los
  mueve uno de esos jugadores en vez de quedarse quietos hasta que alguien los golpea. El **Menderbug** sale para
  todos si le sale a cualquiera.
- **Instalador**: ahora es un solo programa, sin PowerShell por debajo, y con un icono nuevo (una sombra). Es el
  mismo archivo en todas las versiones, así que Windows debería avisar menos al abrirlo con el tiempo. Una
  instalación hecha con el instalador anterior se actualiza y se desinstala igual.

## 0.29.0

La historia entera jugada con 4 jugadores (cuatro copias del juego a la vez), de los primeros jefes a los dos finales
del Hollow Knight y la Radiancia, además de la Compañía de Grimm, Grey Prince Zote, el Coliseo y un Panteón entero.

- **Panteones juntos**: cuando un jugador empieza un Panteón, los que están en el Atrio entran con él, con las mismas
  ataduras, y el grupo pasa junto a cada jefe (también al salir de la sala de descanso). Antes lo peleaba solo.
- **Hollow Knight**: ya no se queda quieto para siempre en la escena de Hornet. Todos ven la absorción del cuerpo y la
  apertura del Huevo Negro, la haga quien la haga. Cualquiera entra en la Radiancia con el aguijón onírico, y la
  entrada ya no se dispara sola.
- **Radiancia**: pasa de fase y recibe el golpe final de cualquier jugador, no solo del que la mueve en su PC.
- **Partes de sala que piden una habilidad** (el combate del Señor Traidor, la pared del Falso Caballero, la puerta
  del Abismo, el puente del Pico de Cristal...): se cargan si alguien del grupo la tiene, así la sala es igual para
  todos.
- **Maestro de Almas**: todos ven la secuencia del orbe falso y caen juntos al piso de abajo; su primera fase tiene
  barra de vida.
- **Guerreros soñadores**: empiezan para todos, y al salir de los **sueños** ya no se quedan compañeros de pie sin
  control.
- **Cloth** pelea contra el Señor Traidor en todos los PCs; Xero, Flukemarm y el cuerpo del Maestro de Almas ya no se
  traban en algunos PCs; un jefe dormido ya no cambia de PC una y otra vez.

## 0.28.0

Partidas de 3 y 4 jugadores, probadas con cuatro copias del juego a la vez.

- **Jefes**: un PC que solo miraba ya no retoma un jefe con un estado viejo (volvía a un ataque o fase anterior).
- **Ascensores, Coliseo y Señores Mantis**: cuando dos jugadores los usan a la vez, todos los PCs acaban de acuerdo
  en quién los mueve (antes se paraban para un tercero).
- **Presentaciones de jefe**: con dos a la vez, el caballero sigue quieto hasta que acaban las dos.
- **Personajes de misión**: si el cambio lo hace alguien en otra sala, el personaje se vuelve a comprobar.
- **Caídos**: un caído ya no se queda siguiendo a alguien que está en un sueño, una cinemática o Godhome.
- **Bancos**: cuatro en el mismo banco, sin meterse en la pared.
- **Revivir**: con dos reviviendo a la vez, el aviso ya no parpadea y quien termina segundo recupera el alma.
- **Mapa**: cada compañero con su color y su nombre.
- **Dificultad**: la vida extra de enemigos y jefes crece con cada compañero en la sala (1,5× con dos jugadores es 2×
  con tres y 2,5× con cuatro).
- **"Ir con"**: varios que van a la vez con el mismo compañero aparecen en sitios distintos.
- Un jugador nuevo ya no hereda lo que controlaba otro que se fue, y el host ya no pierde datos cuando reenvía mucho.

## 0.27.0

- **Los jefes ya no cortan su ataque al cambiar de jugador.** Cada jefe lo mueve el PC del jugador más cercano, y
  cuando el otro se acercaba el jefe cambiaba de PC a mitad de un ataque: lo empezaba otra vez hacia el otro
  caballero. Ahora espera a terminar el ataque y cambia entre uno y otro (como mucho 4 s después).

## 0.26.0

Revisión completa del mod contra el código del juego: 172 fallos encontrados, todos arreglados o revisados, y la
mayoría comprobados en el juego, donde salieron 16 más.

- **El compañero**: la oscuridad de las salas oscuras se abre a su alrededor; se ven su escudo de Carefree Melody, su
  Baldur Shell, la recarga del Shade Dash y sus muertes en pinchos, en ácido y de verdad.
- **Enemigos y jefes**: el daño que no es un aguijonazo (Flukenest, Spore Shroom, Defender's Crest) cuenta para
  todos; los jefes esperan a todos sin límite de tiempo; un jefe que cambia de PC sigue peleando; el Falso Caballero
  cuenta cada armadura rota como una fase; las invocaciones y disparos "con el tiempo" (Rey Vengamosca, Hornet en los
  Bordes del Reino, Nosk, Hollow Knight) salen en todos los PCs.
- **Mundo compartido**: el puente de la Ciudad de las Lágrimas, los tranvías, las máquinas de peaje, el ascensor del
  Palacio Blanco, las plataformas del Coliseo y de la Radiancia y los huevos del Cañón de Niebla, iguales para todos.
  Las estatuas de Dios Hogar, el alma máxima y el robo de Millibelle, de cada jugador.
- **Revivir y sesión**: un caído ya no lanza hechizos ni hace dash; un invitado que sale al menú sale de la partida; en
  un Panteón el personaje del invitado nunca se guarda sin amuletos; un encuentro con un jefe ya no se traba si quien
  lo lleva cae en el diálogo.
- **Interfaz e instalador**: el HUD del compañero se esconde con el inventario y cambia de idioma; los menús se usan
  con mando; el instalador soporta una actualización del juego en Steam y carpetas con `[ ]`.
- **Red y seguridad**: el código de la partida se estira con PBKDF2 y el host no responde a quien no lo conoce; los
  paquetes falsos ya no pueden agotar la memoria ni echar a un jugador.

## 0.25.0

Arreglos de la tercera partida real entre dos PCs.

- **Garpedes** (Nido Profundo): van por el mismo sitio y a la vez para los dos.
- **Señores Mantis, segunda fase**: la guadaña ya no sale primero hacia fuera de la arena.
- **Oscuridad en Nido Profundo**: la vista de un jugador ya no se queda negra cuando su compañero se aleja.

## 0.24.1

Arreglos de la segunda partida real entre dos PCs.

- **Señores Mantis**: al repetir el desafío sale siempre la primera mantis; el cartel ya no se apaga antes de tiempo;
  su estela ya no se queda congelada.
- **Bretta y otros personajes de misión** ya no desaparecen delante del compañero que está en la sala.
- **Ascensores** de Bocasucia y de la estación del ciervo: se mueven para los dos.
- **Los enemigos vuelven** al descansar en un banco y al morir, como en el juego normal.
- La música de la sombra se quita al matar la sombra del compañero, y el cuerpo de un caído lleva su skin.

## 0.24.0

- **Nuevo**: habilidades de cada jugador; viajar juntos en ciervo; bancos para dos; los invitados nuevos ven la
  cinemática del principio; el botín cae en el mismo sitio para todos; compañeros en el mapa solo con la Brújula
  Caprichosa; "en pausa" en el HUD; avisos de versión nueva en el juego; instalador `HKMultiplayer Setup.exe` con
  *Novedades*.
- **Arreglos de la primera partida real**: el Falso Caballero muere entero y abre las rejas; disparos del Elder
  Baldur, escupitajo del Gulka y vómito de los Ogros Hongo en el otro PC; sin ataques dobles al cambiar de PC; el Rey
  Vengamosca ya no se queda en el aire; efectos que se quedaban en pantalla; pasto, capullo de savia y campana del
  ciervo del Sendero Verde.

## 0.23.0

Arreglos de la primera partida real entre dos PCs.

- Un error al procesar un mensaje ya no corta la conexión el resto de la partida.
- El Falso Caballero vuelve a salir tras *Recargar sala*.
- Tiktik y Reptantes ya no van a saltos; los Goam salen y se esconden a la vez para todos.
- El orbe de alma de una skin ya no parpadea.

## 0.22.1

- Instalador: el texto de los botones desactivados se lee (en blanco).

## 0.22.0

- **Compañeros en el mapa**, en rojo si están caídos (opción del host).
- **Reconexión automática** del invitado, hasta 12 intentos (*Esc* la cancela).
- **Cuerpos siempre alcanzables**: quien muere sobre un pozo, pinchos o ácido queda en el último suelo seguro.

## 0.21.2

- Las piedras de geo sueltan geo para todos.
- Skins más rápidas y guardadas en caché (hasta 256 MB).

## 0.21.1

- Primera versión pública.

## Antes de publicarse (0.2.0 a 0.21.0)

Versiones de desarrollo, sin release.

- **0.21.0**: el mod en español o en inglés según el juego, e instalador con ventana.
- **0.20.x**: misiones de NPC sin desincronizar ni avanzar dos veces; jefes que cambian de dueño, cabeza del Falso
  Caballero y barras de los sueños.
- **0.19.0**: ir con un compañero y ajustes del host.
- **0.18.x**: efectos de amuletos y acompañantes del compañero; Grimm, Tirano de Almas, Hollow Knight, Radiancia,
  Campeón Fallido, Falso Caballero y crías de la Colmena.
- **0.16.0 a 0.17.0**: progreso compartido (M5) y el Coliseo de Oro sin atascos.
- **0.15.x**: efectos del otro jugador (M4); Coliseo de Plata entero.
- **0.14.x**: rompibles y guardables compartidos; rutas de prueba automáticas; el Coliseo ya no se atasca entre oleadas.
- **0.12.0 a 0.13.0**: diálogos de jefe compartidos y combates que esperan a todos.
- **0.9.0 a 0.11.0**: cofres, peligros animados al mismo ritmo y el Coleccionista con sus tarros.
- **0.7.0 a 0.8.0**: Señores Mantis, traspaso de enemigos entre PCs, entrar a mitad de un combate y el Coliseo.
- **0.4.0 a 0.6.x**: jefes iguales en los dos PCs (daño, arena, efectos) y el Guardián de Cristal con sus rayos.
- **0.3.0**: los enemigos atacan a los dos jugadores.
- **0.2.0**: golpes y sonidos de enemigos en todos los PCs.
