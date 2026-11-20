namespace HKMultiplayer.Game.Features;

/// <summary>
/// Con «La pausa para el juego» en No (ajuste del host), abrir la pausa durante una partida coop no para el tiempo en ese
/// PC: el juego pone el tiempo a 0 al pausar (<c>GameManager.SetPausedState</c>) y así, con el menú abierto, no llegaba
/// ningún golpe; pausar en mitad de un combate dejaba a salvo. El caballero sigue quieto (el juego le quita las teclas al
/// pausar) y el menú funciona igual; solo el mundo sigue moviéndose.
/// </summary>
internal static class PauseKeepsRunning {
    private static bool _hooked;

    public static void Install() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.GameManager.SetPausedState += (orig, self, value) => {
            orig(self, value);
            if (value && MultiplayerRunner.Instance?.Session != null && !HostSettings.Current.PauseStopsGame) {
                TimeController.GenericTimeScale = 1f;
            }
        };
    }
}
