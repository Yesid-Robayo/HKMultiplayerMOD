using System;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Enemigos que no atacan a quien lleva un amuleto: las abejas de la Colmena con Hiveblood (<c>equippedCharm_29</c>) y
/// los hermanos del Abismo con el Corazón del Vacío (<c>royalCharmState</c> 4). Su FSM lo mira en el estado
/// <c>Friendly?</c> con los datos del PC que la mueve y le pone el daño al tocarlo, y ese daño llegaba igual a todos: si
/// ese jugador no llevaba el amuleto, la abeja dañaba también a quien sí lo llevaba, y al revés.
/// <para>
/// Ahora cada PC decide el daño a su caballero con su propio amuleto. Una abeja amistosa a la que golpean deja de serlo
/// (su estado <c>Unfriendly</c>): se nota en que ya no tiene toda su vida. Cómo se mueve (a quién persigue) lo sigue
/// decidiendo el PC que la mueve.
/// </para>
/// </summary>
internal static class CharmFriendship {
    public const byte None = 0;
    public const byte Hiveblood = 1;
    public const byte VoidHeart = 2;

    private const string FriendlyState = "Friendly?";

    /// <summary>Qué amuleto hace amistoso a este enemigo (por el estado <c>Friendly?</c> de sus FSM), o <see cref="None"/>.</summary>
    public static byte KindOf(GameObject enemy) {
        foreach (var fsm in enemy.GetComponents<PlayMakerFSM>()) {
            foreach (var state in fsm.FsmStates) {
                if (state.Name != FriendlyState) {
                    continue;
                }

                foreach (var action in state.Actions) {
                    switch (action) {
                        case GetPlayerDataBool test when test.boolName?.Value == "equippedCharm_29":
                            return Hiveblood;
                        case GetPlayerDataInt test when test.intName?.Value == "royalCharmState":
                            return VoidHeart;
                    }
                }
            }
        }

        return None;
    }

    /// <summary>
    /// El daño al tocarlo para el caballero de este PC: 0 si lleva el amuleto (y la abeja no se volvió hostil); si no, el
    /// que dice la lógica del enemigo, o su daño normal cuando esa lógica lo puso a 0 por el amuleto de otro jugador.
    /// </summary>
    public static int LocalDamage(byte kind, int received, int baseDamage, HealthManager health, int maxHp) {
        var data = PlayerData.instance;
        if (kind == None || data == null) {
            return received;
        }

        var friendly = kind == Hiveblood
            ? data.equippedCharm_29 && (health == null || health.hp >= maxHp)
            : data.royalCharmState == 4;
        if (friendly) {
            return 0;
        }

        return received > 0 ? received : Math.Max(baseDamage, 1);
    }
}
