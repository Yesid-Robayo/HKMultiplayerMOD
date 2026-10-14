using UnityEngine;
using UnityEngine.SceneManagement;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Partes de una sala que el juego carga aparte, después de la sala (la del jefe: <c>Waterways_12_boss</c>, cargada por
/// <c>SceneAdditiveLoadConditional</c>). Los sistemas repasan la sala al entrar y otra vez a los 1,5 s; si esa parte
/// tardaba más (cuatro juegos abiertos, un PC lento), lo suyo no se repasaba nunca: Flukemarm no se sincronizaba y
/// cada PC tenía la suya. Con esto, cada sistema vuelve a repasar poco después de que se cargue una.
/// </summary>
internal static class SceneLoads {
    /// <summary>Lo que se espera tras cargarse una parte para repasar (que sus objetos ya estén encendidos).</summary>
    private const float RescanDelay = 0.3f;

    private static bool _installed;

    /// <summary>Cuántas partes se han cargado aparte desde que empezó el juego.</summary>
    private static int _version;

    public static void Install() {
        if (_installed) {
            return;
        }

        _installed = true;
        // El SceneManager del juego tapa el de Unity: con el nombre entero
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, mode) => {
            if (mode == LoadSceneMode.Additive) {
                _version++;
            }
        };
    }

    /// <summary>
    /// Llamar cada frame: true cuando toca repasar porque se cargó una parte desde la última vez (<paramref name="seen"/>
    /// y <paramref name="rescanAt"/> son del sistema que pregunta).
    /// </summary>
    public static bool ShouldRescan(ref int seen, ref float rescanAt) {
        var now = Time.unscaledTime;
        if (seen != _version) {
            seen = _version;
            rescanAt = now + RescanDelay;
        }

        if (rescanAt > 0f && now >= rescanAt) {
            rescanAt = 0f;
            return true;
        }

        return false;
    }
}
