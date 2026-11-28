using GlobalEnums;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Con «La pausa para el juego» en No (por defecto en coop), abrir la pausa durante una partida coop no para el tiempo en
/// ese PC: el juego pone el tiempo a 0 al pausar (<c>GameManager.SetPausedState</c>) y así, con el menú abierto, no llegaba
/// ningún golpe; pausar en mitad de un combate dejaba a salvo. El caballero sigue quieto (el juego le quita las teclas al
/// pausar) y el menú funciona igual, pero el mundo sigue moviéndose y el caballero recibe daño real sin invulnerabilidad.
/// Si cae o muere con la pausa abierta, la pausa se cierra sola para no tapar la caída ni la pantalla de revivir.
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

        On.HeroController.Die += (orig, self) => {
            ClosePauseIfLive();
            return orig(self);
        };
    }

    private static void ClosePauseIfLive() {
        var gm = GameManager.instance;
        var ui = UIManager.instance;
        if (gm == null || ui == null || !gm.isPaused || gm.gameState != GameState.PAUSED) {
            return;
        }

        if (MultiplayerRunner.Instance?.Session == null || HostSettings.Current.PauseStopsGame) {
            return;
        }

        ui.TogglePauseGame();
    }
}
