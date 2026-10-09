using System.Collections.Generic;
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
            if (multiplier > 0f && !Mathf.Approximately(multiplier, 1f) && hit.DamageDealt > 0 && hit.Multiplier > 0f) {
                hit.Multiplier = ScaledMultiplier(self, hit.DamageDealt, hit.Multiplier, multiplier);
            }

            orig(self, hit);
        };
    }

    /// <summary>
    /// Lo que sobra del redondeo en cada enemigo, para el golpe siguiente. El juego redondea el daño al entero más cercano
    /// (y los .5 al par): dividiendo sin más, con el aguijón inicial (5) 1,5× quitaba 3 por golpe (1,67×) y 2× quitaba 2
    /// (2,5×). Así la vida extra es la elegida en la suma de los golpes.
    /// </summary>
    private static readonly Dictionary<HealthManager, float> Carry = new();

    private static float ScaledMultiplier(HealthManager enemy, int damage, float hitMultiplier, float healthMultiplier) {
        if (Carry.Count > 256) {
            Carry.Clear();
        }

        Carry.TryGetValue(enemy, out var carry);
        var exact = damage * hitMultiplier / healthMultiplier + carry;
        var whole = Mathf.FloorToInt(exact + 0.001f);
        Carry[enemy] = Mathf.Max(0f, exact - whole);

        // El juego hace RoundToInt(daño × multiplicador): con este multiplicador sale justo "whole"
        return (float) whole / damage;
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
