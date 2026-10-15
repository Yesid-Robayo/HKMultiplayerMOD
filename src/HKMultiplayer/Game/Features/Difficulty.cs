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

            // Golpe que no cuenta para aturdir: TakeDamage avisa a la FSM de aturdimiento si la tiene; sin ella ese golpe no
            // llega, y el daño se hace igual
            var stun = SkipStunHit(self) ? StunControlField.GetValue(self) : null;
            if (stun != null) {
                StunControlField.SetValue(self, null);
            }

            try {
                orig(self, hit);
            } finally {
                if (stun != null) {
                    StunControlField.SetValue(self, stun);
                }
            }
        };
    }

    /// <summary>FSM de aturdimiento del enemigo ("Stun Control" o "Stun"), a la que TakeDamage manda STUN DAMAGE.</summary>
    private static readonly FieldInfo StunControlField =
        typeof(HealthManager).GetField("stunControlFSM", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>Golpes que aún no llegan a uno que cuente para aturdir, por enemigo.</summary>
    private static readonly Dictionary<HealthManager, float> StunCarry = new();

    /// <summary>
    /// Los jefes que se aturden tras unos golpes (Hornet, Vasija Rota, Maestro de Almas, Grimm, Hollow Knight...) cuentan
    /// cada golpe en su FSM de aturdimiento: el total ("Stun Hit Max") y los seguidos en poco tiempo ("Stun Combo"). Con
    /// varios jugadores pegando, los dos se llenaban antes y el jefe se pasaba el combate aturdido. Con N jugadores en la
    /// sala solo cuenta uno de cada N golpes: hacen falta N veces más, y como pegan N veces más rápido, se aturde más o
    /// menos tan a menudo como jugando solo. No se tocan sus variables: vale igual con Golpe Pesado (que las baja al
    /// empezar) y cuando el jefe cambia de PC a mitad del combate.
    /// </summary>
    private static bool SkipStunHit(HealthManager enemy) {
        if (StunControlField == null || StunControlField.GetValue(enemy) == null) {
            return false;
        }

        var session = MultiplayerRunner.Instance != null ? MultiplayerRunner.Instance.Session : null;
        if (session == null || !session.IsActive || ShadeSync.IsRemoteShade(enemy.gameObject)) {
            return false;
        }

        var players = 1 + PartnersInRoom(session);
        if (players <= 1) {
            return false;
        }

        if (StunCarry.Count > 64) {
            Prune(StunCarry);
        }

        StunCarry.TryGetValue(enemy, out var carry);
        carry += 1f / players;
        var counts = carry >= 0.999f;
        StunCarry[enemy] = counts ? carry - 1f : carry;
        return !counts;
    }

    /// <summary>
    /// Lo que sobra del redondeo en cada enemigo, para el golpe siguiente. El juego redondea el daño al entero más cercano
    /// (y los .5 al par): dividiendo sin más, con el aguijón inicial (5) 1,5× quitaba 3 por golpe (1,67×) y 2× quitaba 2
    /// (2,5×). Así la vida extra es la elegida en la suma de los golpes.
    /// </summary>
    private static readonly Dictionary<HealthManager, float> Carry = new();

    private static float ScaledMultiplier(HealthManager enemy, int damage, float hitMultiplier, float healthMultiplier) {
        if (Carry.Count > 256) {
            Prune(Carry);
        }

        Carry.TryGetValue(enemy, out var carry);
        var exact = damage * hitMultiplier / healthMultiplier + carry;
        var whole = Mathf.FloorToInt(exact + 0.001f);
        Carry[enemy] = Mathf.Max(0f, exact - whole);

        // El juego hace RoundToInt(daño × multiplicador): con este multiplicador sale justo "whole"
        return (float) whole / damage;
    }

    /// <summary>
    /// Daño que no pasa por TakeDamage (nubes de Spore Shroom y Defender's Crest, moscas de Flukenest) con la vida extra:
    /// antes esos golpes quitaban lo mismo con o sin ella.
    /// </summary>
    public static int ScaleDamage(HealthManager enemy, int damage) {
        var multiplier = enemy != null ? Multiplier(enemy) : 1f;
        if (damage <= 0 || multiplier <= 0f || Mathf.Approximately(multiplier, 1f)) {
            return damage;
        }

        return Mathf.RoundToInt(damage * ScaledMultiplier(enemy, damage, 1f, multiplier));
    }

    /// <summary>Quita las entradas de enemigos ya destruidos (vaciarlo entero cambiaba a mitad del combate lo decidido).</summary>
    private static void Prune<T>(Dictionary<HealthManager, T> map) {
        PruneScratch.Clear();
        foreach (var key in map.Keys) {
            if (key == null) {
                PruneScratch.Add(key);
            }
        }

        foreach (var key in PruneScratch) {
            map.Remove(key);
        }
    }

    private static readonly List<HealthManager> PruneScratch = new();

    /// <summary>Vida con la que aparece un jefe que no tiene estatua: ningún enemigo normal llega.</summary>
    private const int BossStartHealth = 300;

    /// <summary>
    /// Vida a partir de la cual no es un jefe sino un muñeco que no se puede matar: el maniquí de mantis que Cloth derriba
    /// al entrar en el combate del Señor Traidor tiene 999999 (salía como jefe, con barra). La Radiancia tiene 3000.
    /// </summary>
    private const int DummyHealth = 9000;

    /// <summary>Vida a partir de la cual cuenta como jefe lo que está en la parte de la sala del jefe (ver IsBoss).</summary>
    private const int BossSceneHealth = 150;

    /// <summary>
    /// Jefes del juego con poca vida y sin estatua en su sala: el Rey Vengamosca (55) y la Madre Gruz (90). No contaban
    /// como jefes: sin barra, sin la vida extra de jefe y sin esperar a los demás para empezar.
    /// </summary>
    private static readonly HashSet<string> SmallBosses = new() { "Giant Buzzer", "Giant Fly" };

    /// <summary>Lo decidido para cada enemigo la primera vez que se miró (con la vida del principio).</summary>
    private static readonly Dictionary<HealthManager, bool> BossCache = new();

    /// <summary>
    /// Si es un jefe: tiene estatua en el Salón de los Dioses (<c>unlockBossScene</c>) o aparece con mucha vida. Los de
    /// los sueños (Tirano de Almas, Príncipe Gris Zote) no tienen estatua: se quedaban sin barra y sin la vida extra. Se
    /// decide la primera vez que se mira (al encenderse, con la vida entera) y no cambia a mitad del combate.
    /// </summary>
    public static bool IsBoss(HealthManager health) {
        if (health == null) {
            return false;
        }

        if (BossCache.TryGetValue(health, out var boss)) {
            return boss;
        }

        // Solo se quitan los destruidos: vaciarlo entero hacía juzgar de nuevo a un jefe vivo por su vida de ese momento, y
        // por debajo de 300 pasaba a "enemigo" y perdía la vida extra a mitad del combate
        if (BossCache.Count > 1024) {
            Prune(BossCache);
        }

        // También lo de mucha vida en la parte de la sala que el juego carga para el jefe ("..._boss"): la primera fase del
        // Maestro de Almas (275) no tiene estatua (la lleva la segunda) y se quedaba sin barra y sin la vida extra
        boss = health.hp < DummyHealth && (SmallBosses.Contains(health.gameObject.name) || UnlockBossSceneField?.GetValue(health) as Object != null || health.hp >= BossStartHealth ||
               health.hp >= BossSceneHealth && health.gameObject.scene.name.EndsWith("_boss", System.StringComparison.Ordinal));
        BossCache[health] = boss;
        return boss;
    }

    /// <summary>Cuánta más vida tiene el enemigo: el daño que recibe se divide por esto.</summary>
    public static float Multiplier(HealthManager enemy) {
        var session = MultiplayerRunner.Instance != null ? MultiplayerRunner.Instance.Session : null;
        if (session == null || !session.IsActive || ShadeSync.IsRemoteShade(enemy.gameObject)) {
            return 1f;
        }

        var settings = HostSettings.Current;
        var partners = PartnersInRoom(session);
        if (settings.OnlyWithPartnerInRoom && partners == 0) {
            return 1f;
        }

        // La opción es la vida con un compañero; cada compañero más en la sala suma lo mismo otra vez (1,5× con dos
        // jugadores es 2× con tres y 2,5× con cuatro). Antes era igual con 2 que con 4 y con más jugadores el combate se
        // acababa enseguida. Sin nadie más en la sala cuenta como con uno (la opción tal cual)
        var chosen = IsBoss(enemy) ? settings.BossHealth : settings.EnemyHealth;
        return 1f + (chosen - 1f) * Mathf.Max(1, partners);
    }

    /// <summary>Cuántos otros jugadores en pie están en la sala del local.</summary>
    private static int PartnersInRoom(Session.GameSession session) {
        var party = PartyState.Current;
        var scene = session.LocalPlayer?.Scene;
        if (party == null || string.IsNullOrEmpty(scene)) {
            return 0;
        }

        var count = 0;
        foreach (var member in party.Members) {
            if (member.Id != session.LocalId && member.Status != PlayerStatus.Downed && member.Player != null &&
                member.Player.Scene == scene) {
                count++;
            }
        }

        return count;
    }
}
