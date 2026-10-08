# HKMultiplayer

Mod cooperativo para **Hollow Knight 1.5.12620** (la versión actual de Steam, Unity 6). Un jugador hostea su partida
y su compañero se une con el código de la partida: comparten el mundo y las habilidades, se ven en tiempo real,
pelean juntos y pueden revivirse.

Proyecto escrito desde cero. El diseño está en [docs/arquitectura.md](docs/arquitectura.md).

## Estado

En desarrollo (hito M1: transporte, sesión y jugadores). Ver los hitos en la arquitectura.

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/HKMultiplayer/` | El mod. `Net/` (transporte, sin Unity), `Session/`, `Game/Replication/`, `Game/Features/`. |
| `tests/` | Pruebas del transporte fuera del juego. |
| `tools/` | Compilar e instalar (`test-cycle.sh`), pruebas con dos copias del juego (`mp-test-lib.sh`), paquete e instalador para jugadores. |
| `docs/` | Diseño y notas. |
| `lib/` | *(solo local, no se sube)* DLL del juego y de la Modding API para compilar. |
| `vendor/` | *(solo local, no se sube)* Modding API compilada para el paquete. |
| `test-instances/` | *(solo local, no se sube)* Copias del juego para probar con dos jugadores en el mismo PC. |

Las DLL del juego son de Team Cherry y no se incluyen en el repositorio.

## Compilar

1. Copiar a `lib/` las DLL de `hollow_knight_Data/Managed` del juego con la Modding API instalada
   (`Assembly-CSharp.dll`, `MMHOOK_*.dll`, `PlayMaker.dll`, `UnityEngine.*.dll`, `TeamCherry.*.dll`,
   `MonoMod.*.dll`, `Newtonsoft.Json.dll`, `netstandard.dll`...).
2. `bash tools/test-cycle.sh` compila (.NET Framework 4.7.2) e instala en las copias de prueba;
   `bash tools/test-cycle.sh --game` también en el juego.
3. Pruebas del transporte: `dotnet run --project tests/NetTests`.
