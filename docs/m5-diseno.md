# Diseño del hito M5 (aprobado)

Decisiones aprobadas por el usuario para M5. Las marcadas como "elegido" las escogió el usuario; el resto son las
recomendadas de la propuesta. La referencia es la 0.7.3 del mod (`HollowKnightCoop/src/HollowKnightCoop/Coop`).

## Base común

- Funciones en `Game/Features/`, enganchadas en `MultiplayerRunner` con Attach/Detach como las demás capas.
- `PartyState`: estado de cada jugador (en pie, caído, en cinemática, en sueño). Un caído no cuenta para repartir
  enemigos (`EnemySync.AssignControllers`), ni para las esperas de M3 ("Esperando a ..."), ni para los diálogos.
- Mensajes nuevos desde el 73 (el 50 también está libre).
- Interfaz con uGUI (como `Notice` y `Banner`), nunca IMGUI (`OnGUI`): sin memoria nueva por frame.
- Control "Opción < valor >" en `GameStyledMenu`, como el `MenuOptionHorizontal` de las opciones del juego.

## 1. Revivir (elegido: Focus 2 s, 33 de alma)

- El caído hace la animación de muerte y queda tendido (sprite de muerte del caballero, etiqueta "✝ Nombre"). Su
  cámara sigue al compañero, también entre salas. Textos: "Caído — X puede revivirte", "X te está reviviendo".
- El compañero recibe "X cayó en <zona>" y una flecha hacia el cuerpo. Junto al cuerpo: "Mantén Focus para revivir a X"
  con barra de progreso; ese Focus no cura.
- Mantener Focus 2 s a 2 unidades o menos, cuesta 33 de alma. Si golpean al que revive, se pierde el progreso.
- Vuelve con 1 máscara y 2 s de invulnerabilidad (parpadeo).
- Sin límite de tiempo caído. Si el compañero se sienta en un banco, el caído revive a su lado con toda la vida.
- Si caen todos: cada uno muere de forma normal (sombra y vuelta a su banco).
- En un sueño también se cae y se revive dentro; si caen los dos, despiertan los dos.
- Implementación: `On.HeroController.Die` sustituido; mensajes `Downed`, `PartyStatus`, `ReviveRequest` (lo valida el
  host), `Revived`, `Wipe`, `BenchRest`, `ReviveProgress` (no fiable, a la sala). Viaje del caído como la 0.7.3.

## 2. Sombras (elegido: copia en cada PC, como la 0.7.3)

- En cada PC se crea una copia de la sombra del compañero (`hollowShadeObject` con su IA, leyendo los datos del dueño;
  sin "Dreamnail Kill"). Cada PC mueve su copia: puede no estar en el mismo sitio en los dos.
- Si alguien mata la copia, el dueño mata su propia sombra por la vía normal y recupera geo y alma, aunque esté en otra
  sala ("Recuperaste tu sombra gracias a X"). El host acepta solo el primer aviso (el geo no se cobra dos veces).
- La sombra de alguien que no está conectado no se ve.

## 3. HUD del compañero

- Mini-HUD en la esquina superior derecha (cambiado por el usuario: debajo del HUD propio parecía parte de él y sus
  máscaras eran puntos ilegibles). Alineado a la derecha y separado del borde como el HUD propio: el nombre arriba con
  la fuente del juego, debajo sus máscaras con los sprites reales de `HUD Cln` a unos 2/3 del tamaño de las propias
  (también azules), un orbe de alma pequeño a la izquierda de las máscaras y el estado (caído / en otra zona con su
  nombre). Con varios compañeros, uno debajo de otro.
- Sus máscaras sobre su cabeza unos segundos cuando le cambia la vida, legibles (aprox. la mitad del tamaño de las del
  HUD) y por encima de su nombre. Flechas en el borde de la pantalla.
- Barras: solo la del jefe (abajo, con su nombre). El compañero en el mapa queda para después de M5.
- Mensaje `Vitals` (al cambiar, máximo 4 por segundo, fiable); el host da el último a quien entra. Sprites de `HUD Cln`.

## 4. Cinemáticas compartidas

- Finales, créditos y Mr. Mushroom a la vez (también si el compañero está en otra sala). Saltar: votan todos ("Esperando
  a X para saltar 1/2").
- Presentaciones de jefe en la sala: los dos caballeros quietos y recuperan el control a la vez.
- Al terminar, quien fue llevado vuelve a donde estaba. Tras los créditos del final, todos a su banco y la partida sigue.

## 5. Ajustes del host (Pausa › Multijugador)

- Guardados por mundo, en su carpeta. Los invitados los ven sin poder cambiarlos. Mensaje `HostSettings` con versión.
- Páginas: Partida (lo de hoy), Ajustes de la partida, Compañeros (vida de cada uno, Dar geo, Desatascar, Recargar
  sala), Skin.
- Por defecto: vida de jefes 1,5× (elegido) solo con el compañero en la sala; enemigos 1×; daño recibido 1×. Revivir
  activado, 2 s, 33 de alma, 1 máscara, 2 s de invulnerabilidad, revivir en el banco sí. Pantalla: nombres, vida del
  compañero, flechas, avisos y barra de jefe sí; barras de enemigos no. Regalar geo sí. Saltar cinemáticas por votación.
- La vida extra se hace dividiendo el daño en `HealthManager.TakeDamage` en el PC que mueve al enemigo (no subiendo su
  vida: las fases de los jefes dependen de ella). El daño recibido se escala en `HeroController.TakeDamage` sin tocar
  pinchos ni ácido.

## 6. Sueños compartidos y jefes

- Si uno entra a un sueño con el aguijón onírico, el compañero en la misma sala entra con él (mismo destello). Si está
  en otra sala, no. Al ganar, salen los dos (`DreamExit`).
- Jefes de sueño (Campeón Fallido, Tirano de Almas, Kin Perdido, Zote el Poderoso, Defensor Blanco), Rey de las
  Pesadillas (`GrimmDream`) y Radiancia (el compañero entra aunque no tenga el Corazón del Vacío).
- Grimm: por los encuentros con diálogo de M3. Grimmchild: si uno lo recibe, el otro también.
- Hollow Knight: espera de M3, presentación compartida y final compartido.
- Sonido del minero del Coliseo que no suena en el PC que no lo mueve (`EnemySync`, efectos de "Audio"/"Sound").

## 7. Skins (elegido: sí, al final)

- Compatibles con CustomKnight, como la 0.7.3 (`Skins.cs`, `AtlasRepair.cs`). Cuerpo, hechizos y efectos.
- Se envían por la red del propio mod (cifrada, sin abrir otro puerto), en segundo plano a unos 0,5 MB/s con una cola
  de baja prioridad (si no, una skin retrasaría golpes y eventos). Máximo 32 MB. Opción de cada PC "ver las skins de
  los demás" activada por defecto.

## Orden

1. Base (`PartyState`, control "Opción < >"). 2. Ajustes del host y panel. 3. HUD del compañero. 4. Revivir.
5. Sombras. 6. Sonido del minero. 7. Cinemáticas. 8. Sueños y jefes (Grimm, jefes de sueño, Rey de las Pesadillas,
Hollow Knight y Radiancia). 9. Skins.
