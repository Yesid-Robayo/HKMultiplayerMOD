# Cambios por versión

Qué trajo y qué arregló cada versión de HKMultiplayer, de la más nueva a la más vieja. Las versiones publicadas
tienen su zip, sus notas completas en inglés y en español y su SHA256 en
[Releases](https://github.com/Yesid-Robayo/HKMultiplayerMOD/releases). **Todos los jugadores necesitan la misma
versión**: dos versiones distintas no pueden jugar juntas.

Al publicar una versión nueva, se añade aquí arriba con lo mismo que su sección *Novedades* de las notas de la release.

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
