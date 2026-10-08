using System.Collections.Generic;
using System.Reflection;
using InControl;
using UnityEngine;

namespace HKMultiplayer.Testing;

/// <summary>
/// Solo para el modo de prueba: mantiene pulsado un botón del juego (acción de InControl) durante un tiempo. InControl
/// calcula por sí mismo "recién pulsado", "pulsado" y "soltado", igual que con un mando real.
/// </summary>
internal static class InputSimulator {
    private static readonly Dictionary<OneAxisInputControl, float> PressedUntil = new();
    private static bool _installed;

    public static void Install() {
        if (_installed) {
            return;
        }

        _installed = true;
        On.InControl.OneAxisInputControl.UpdateWithValue += (orig, self, value, tick, deltaTime) =>
            orig(self, IsForced(self) ? 1f : value, tick, deltaTime);
    }

    /// <summary>Pulsa el botón <paramref name="action"/> (campo de HeroActions: left, jump, attack...).</summary>
    /// <returns>false si no existe ese botón.</returns>
    public static bool Press(string action, float seconds) {
        var actions = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
        var field = typeof(HeroActions).GetField(action, BindingFlags.Public | BindingFlags.Instance);
        if (actions == null || field?.GetValue(actions) is not PlayerAction playerAction) {
            return false;
        }

        PressedUntil[playerAction] = Time.unscaledTime + seconds;
        return true;
    }

    private static bool IsForced(OneAxisInputControl control) =>
        PressedUntil.Count > 0 && PressedUntil.TryGetValue(control, out var until) && Time.unscaledTime < until;
}
