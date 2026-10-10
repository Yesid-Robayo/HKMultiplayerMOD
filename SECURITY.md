# Seguridad

*English: please report security problems privately through **Security › Report a vulnerability** on GitHub, not in
a public issue.*

## Cómo avisar de un problema de seguridad

Si encuentras un fallo de seguridad (por ejemplo, una forma de entrar en una partida sin el código, de leer o cambiar
lo que se envía, de hacer que el juego de otro jugador ejecute algo o escriba archivos fuera de su sitio, o de colgarle
el juego desde la red), **no lo publiques en un *issue***. Usa el aviso privado de GitHub:

1. Pestaña **Security** del repositorio.
2. **Report a vulnerability**.
3. Describe qué pasa, cómo reproducirlo y con qué versión del mod.

Solo lo verá el autor. Se responderá lo antes posible y, una vez arreglado, se publicará una versión nueva.

## Versiones con soporte

Solo la última versión publicada en *Releases* recibe arreglos.

## Qué protege el mod

- Solo entra en una partida quien conoce su **código** (8 caracteres generados al azar con un generador
  criptográfico).
- Todo lo que se envía va **cifrado y autenticado** (X25519, HKDF-SHA256 con el código de la partida y
  ChaCha20-Poly1305 en cada paquete); los paquetes alterados o repetidos se descartan.
- La conexión es directa entre los PCs: no hay servidores intermedios ni cuentas.
- Lo que llega de otros jugadores se comprueba antes de usarlo: tamaños máximos de paquetes, textos y transferencias,
  nombres sin etiquetas de formato ni caracteres de control, archivos de personaje con nombres que no pueden salirse
  de la carpeta del mundo y skins que solo se guardan en memoria.

## Lo que no protege

- Los jugadores de una partida confían entre sí: quien tiene el código puede, por ejemplo, cambiar el progreso
  compartido del mundo. Comparte el código solo con quien quieras que entre.
- Para jugar por internet el host abre un puerto UDP en su router; ciérralo cuando no lo uses.
