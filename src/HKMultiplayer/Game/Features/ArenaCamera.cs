using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Que una zona de cámara nunca deje al caballero fuera de la pantalla. En el juego no pasa: al cerrarse una arena el
/// caballero ya está dentro. En el coop, la arena la cierra el PC de quien entró, y en la copia de los demás su
/// `Battle Scene` enciende también la zona de cámara de la arena: el que esperaba fuera, dentro de esa zona (suele
/// llegar más allá de las rejas), se quedaba con la cámara fija en la arena y sin verse (issue #7). Ahora la zona solo
/// fija la cámara si con sus límites el caballero sigue a la vista; en cuanto entra, la fija como siempre (el juego la
/// vuelve a pedir en cada paso de física mientras está dentro).
/// Solo con el caballero fuera de una arena cerrada (<see cref="Scene.FsmStateSync.HeroOutsideClosedArena"/>): en las
/// salas normales el juego fija zonas con el caballero aún en el borde (al entrar por una puerta), y en la 0.34.0 este
/// filtro las rechazaba también y la cámara dejaba de seguir al juego (visto en los registros del issue #8).
/// </summary>
internal static class ArenaCamera {
    /// <summary>Media vista de la cámara del juego (los límites por defecto de `CameraController`).</summary>
    private const float HalfWidth = 14.6f;

    private const float HalfHeight = 8.3f;

    /// <summary>Cuánto tiene que quedar dentro de la vista el caballero.</summary>
    private const float Margin = 0.5f;

    private static bool _hooked;
    private static CameraLockArea _lastRefused;

    /// <summary>Se llama al cargar el mod.</summary>
    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.CameraController.LockToArea += (orig, self, area) => {
            if (area != null && LeavesHeroOut(area, out var hero)) {
                if (_lastRefused != area) {
                    _lastRefused = area;
                    Log.Info($"Zona de cámara {area.name} sin fijar: el caballero en ({hero.x:0.0}, {hero.y:0.0}) quedaría " +
                             $"fuera de la vista (cámara entre x {area.cameraXMin:0.0}-{area.cameraXMax:0.0}, " +
                             $"y {area.cameraYMin:0.0}-{area.cameraYMax:0.0})");
                }

                return;
            }

            if (_lastRefused == area) {
                _lastRefused = null;
            }

            orig(self, area);
        };
    }

    private static bool LeavesHeroOut(CameraLockArea area, out Vector3 position) {
        position = Vector3.zero;
        var session = MultiplayerRunner.Instance != null ? MultiplayerRunner.Instance.Session : null;
        var hero = HeroController.instance;
        if (session == null || !session.IsActive || hero == null) {
            return false;
        }

        position = hero.transform.position;
        // Un límite negativo es "sin límite" por ese lado (el del borde de la sala)
        var outOfView = area.cameraXMin >= 0f && position.x < area.cameraXMin - HalfWidth + Margin ||
                        area.cameraXMax >= 0f && position.x > area.cameraXMax + HalfWidth - Margin ||
                        area.cameraYMin >= 0f && position.y < area.cameraYMin - HalfHeight + Margin ||
                        area.cameraYMax >= 0f && position.y > area.cameraYMax + HalfHeight - Margin;
        return outOfView && Scene.FsmStateSync.HeroOutsideClosedArena();
    }
}
