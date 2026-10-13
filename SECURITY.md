# Security · Seguridad

[English](#english) · [Español](#español)

## English

### How to report a security problem

If you find a security flaw (for example, a way to join a game without the code, to read or change what is sent, to
make another player's game run something or write files outside its folder, or to crash their game over the network),
**do not post it in an *issue***. Use GitHub's private report:

1. The repository's **Security** tab.
2. **Report a vulnerability**.
3. Describe what happens, how to reproduce it and with which mod version.

Only the author will see it. You will get an answer as soon as possible and, once it is fixed, a new version will be
released.

### Supported versions

Only the latest version published in *Releases* receives fixes.

### What the mod protects

- Only someone who knows the game's **code** can join it (8 characters generated at random with a cryptographic
  generator).
- Everything sent is **encrypted and authenticated** (X25519, HKDF-SHA256 with the game code stretched with
  PBKDF2-HMAC-SHA256, and ChaCha20-Poly1305 on every packet); altered or replayed packets are discarded.
- The host answers a connection attempt with a single packet and sends nothing else until it receives an
  authenticated one (a forged address cannot be used to send traffic to someone else), limits new attempts per
  second, and never drops a connected player because of a new attempt from their address.
- The connection goes directly between the PCs: there are no relay servers or accounts.
- What arrives from other players is checked before it is used: maximum sizes for packets, texts and transfers, names
  without formatting tags or control characters, character files with names that cannot escape the world's folder,
  and skins that are only stored as verified PNG images, in a cache named by their hash.

### What it does not protect

- Players in a game trust each other: whoever has the code can, for example, change the world's shared progress. Share
  the code only with people you want to let in.
- To play over the internet the host opens a UDP port on their router; close it when you are not using it.

## Español

### Cómo avisar de un problema de seguridad

Si encuentras un fallo de seguridad (por ejemplo, una forma de entrar en una partida sin el código, de leer o cambiar
lo que se envía, de hacer que el juego de otro jugador ejecute algo o escriba archivos fuera de su sitio, o de colgarle
el juego desde la red), **no lo publiques en un *issue***. Usa el aviso privado de GitHub:

1. Pestaña **Security** del repositorio.
2. **Report a vulnerability**.
3. Describe qué pasa, cómo reproducirlo y con qué versión del mod.

Solo lo verá el autor. Se responderá lo antes posible y, una vez arreglado, se publicará una versión nueva.

### Versiones con soporte

Solo la última versión publicada en *Releases* recibe arreglos.

### Qué protege el mod

- Solo entra en una partida quien conoce su **código** (8 caracteres generados al azar con un generador
  criptográfico).
- Todo lo que se envía va **cifrado y autenticado** (X25519, HKDF-SHA256 con el código de la partida estirado con
  PBKDF2-HMAC-SHA256, y ChaCha20-Poly1305 en cada paquete); los paquetes alterados o repetidos se descartan.
- El host responde a un intento de conexión con un solo paquete y no envía nada más hasta recibir uno auténtico (con
  una dirección falsificada no se puede mandar tráfico contra otro), limita los intentos nuevos por segundo y nunca
  echa a un jugador conectado por un intento nuevo desde su dirección.
- La conexión es directa entre los PCs: no hay servidores intermedios ni cuentas.
- Lo que llega de otros jugadores se comprueba antes de usarlo: tamaños máximos de paquetes, textos y transferencias,
  nombres sin etiquetas de formato ni caracteres de control, archivos de personaje con nombres que no pueden salirse
  de la carpeta del mundo y skins que solo se guardan como imágenes PNG comprobadas, en una caché con su huella como
  nombre.

### Lo que no protege

- Los jugadores de una partida confían entre sí: quien tiene el código puede, por ejemplo, cambiar el progreso
  compartido del mundo. Comparte el código solo con quien quieras que entre.
- Para jugar por internet el host abre un puerto UDP en su router; ciérralo cuando no lo uses.
