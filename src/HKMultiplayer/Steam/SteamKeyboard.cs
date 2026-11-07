using System;
using HKMultiplayer.Util;
using Steamworks;

namespace HKMultiplayer.Steam;

/// <summary>
/// Teclado en pantalla de Steam para escribir con el mando en los campos del menú (la dirección y el código para unirse
/// por IP). Solo si el juego se abrió con Steam y Steam lo deja abrir (con la interfaz de Steam activada); si no, el
/// campo se usa con el teclado como siempre. El juego llama a SteamAPI.RunCallbacks en cada frame, que es lo que
/// entrega aquí el texto escrito.
/// </summary>
internal static class SteamKeyboard {
    private static Callback<GamepadTextInputDismissed_t> _dismissed;
    private static Action<string> _pending;

    /// <summary>Abre el teclado con el texto de ahora. Al aceptar, se llama a <paramref name="done"/> con lo escrito.</summary>
    public static bool TryOpen(string description, int maxChars, string current, Action<string> done) {
        try {
            if (!SteamAPI.IsSteamRunning()) {
                return false;
            }

            _dismissed ??= Callback<GamepadTextInputDismissed_t>.Create(OnDismissed);
            var opened = SteamUtils.ShowGamepadTextInput(EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
                EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine, description ?? string.Empty,
                (uint) Math.Max(1, maxChars), current ?? string.Empty);
            if (!opened) {
                Log.Info("Steam no abrió su teclado en pantalla (¿interfaz de Steam desactivada?)");
                return false;
            }

            _pending = done;
            return true;
        } catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) {
            // El juego no inició Steam (abierto sin él)
            return false;
        }
    }

    private static void OnDismissed(GamepadTextInputDismissed_t result) {
        var done = _pending;
        _pending = null;
        if (done == null || !result.m_bSubmitted) {
            return;
        }

        var length = SteamUtils.GetEnteredGamepadTextLength();
        if (SteamUtils.GetEnteredGamepadTextInput(out var text, length + 1)) {
            done(text ?? string.Empty);
        }
    }
}
