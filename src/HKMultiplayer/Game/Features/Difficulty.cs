using System.Reflection;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Más vida para enemigos y jefes según el host: no se les sube la vida (las fases de los jefes dependen de ella), se
/// divide el daño que reciben en el PC que los mueve (en las marionetas los golpes no quitan vida). Con "solo con
/// compañero en la sala", solo si otro jugador en pie está en la sala.
/// </summary>
internal static class Difficulty {
    /// <summary>Solo los jefes tienen estatua en el Salón de los Dioses (<c>unlockBossScene</c>).</summary>
    private static readonly FieldInfo UnlockBossSceneField =
        typeof(HealthManager).GetField("unlockBossScene", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool _installed;

    public static void Install() {
        if (_installed) {
            return;
        }

        _installed = true;
        On.HealthManager.TakeDamage += (orig, self, hit) => {
            var multiplier = Multiplier(self);
            if (multiplier > 0f && !Mathf.Approximately(multiplier, 1f)) {
                hit.Multiplier /= multiplier;
            }

            orig(self, hit);
        };
    }

    /// <summary>Si es un jefe (tiene estatua en el Salón de los Dioses).</summary>
    public static bool IsBoss(HealthManager health) => UnlockBossSceneField?.GetValue(health) as Object != null;

    /// <summary>Cuánta más vida tiene el enemigo: el daño que recibe se divide por esto.</summary>
    public static float Multiplier(HealthManager enemy) {
        var session = MultiplayerRunner.Instance != null ? MultiplayerRunner.Instance.Session : null;
        if (session == null || !session.IsActive || ShadeSync.IsRemoteShade(enemy.gameObject)) {
            return 1f;
        }

        var settings = HostSettings.Current;
        if (settings.OnlyWithPartnerInRoom && !PartnerInRoom(session)) {
            return 1f;
        }

        return IsBoss(enemy) ? settings.BossHealth : settings.EnemyHealth;
    }

    /// <summary>Si otro jugador en pie está en la sala del local.</summary>
    public static bool PartnerInRoom(Session.GameSession session) {
        var party = PartyState.Current;
        var scene = session.LocalPlayer?.Scene;
        if (party == null || string.IsNullOrEmpty(scene)) {
            return false;
        }

        foreach (var member in party.Members) {
            if (member.Id != session.LocalId && member.Status != PlayerStatus.Downed && member.Player != null &&
                member.Player.Scene == scene) {
                return true;
            }
        }

        return false;
    }
}
