using System;
using System.Collections.Generic;
using HKMultiplayer.Util;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.World;

/// <summary>
/// La esencia de los guerreros soñadores también para quien no estaba en la sala.
/// <para>La esencia (<c>dreamOrbs</c>) es de cada personaje. Un guerrero soñador (Xero, Gorb, Marmu...) la da al absorber
/// su fantasma vencido: estado "Get" de su "Conversation Control", que el encuentro compartido hace también en la copia
/// de cada PC de la sala. Al acabar, su dato del mundo (<c>xeroDefeated</c>...) pasa a 2 y el fantasma ya no vuelve, así
/// que quien estaba en otra sala se quedaba sin esa esencia para siempre (1.100 entre los siete).</para>
/// <para>Aquí, cuando llega de otro PC ese dato a 2, quien no está en la sala del guerrero recibe la misma esencia. Quien
/// sí está la recibe del juego, como siempre, y aquí no se le suma. Solo con cambios en vivo: lo que ya estaba en el
/// mundo al entrar no da nada (no se jugó con él).</para>
/// <para>No hace falta para los jefes de los sueños (Campeón Fallido, Tirano de Almas, Hermano Perdido): ganar dentro del
/// sueño no da esencia; la da después su fantasma en el mundo, al escucharlo, y lo guarda un dato de cada personaje
/// (<c>falseKnightOrbsCollected</c>...): cada jugador tiene el suyo, aunque no entrara en el sueño. Las raíces
/// susurrantes tampoco: cada jugador tiene la suya y recoge sus orbes (decidido así).</para>
/// </summary>
internal static class AbsentEssence {
    /// <summary>Valor del dato del guerrero al absorberlo (1 es vencido sin absorber).</summary>
    private const int Absorbed = 2;

    /// <summary>
    /// Por dato del guerrero: la esencia que da ("Dream Orbs" del "Conversation Control" de su fantasma) y las salas donde
    /// la da el juego (la del fantasma y la del combate, que se carga con ella).
    /// </summary>
    private static readonly Dictionary<string, (int Amount, string[] Scenes)> Warriors = new() {
        ["xeroDefeated"] = (100, new[] { "RestingGrounds_02", "RestingGrounds_02_boss" }),
        ["aladarSlugDefeated"] = (100, new[] { "Cliffs_02", "Cliffs_02_boss" }),
        ["elderHuDefeated"] = (100, new[] { "Fungus2_32" }),
        ["mumCaterpillarDefeated"] = (150, new[] { "Fungus3_40", "Fungus3_40_boss" }),
        ["noEyesDefeated"] = (200, new[] { "Fungus1_35" }),
        ["galienDefeated"] = (200, new[] { "Deepnest_40" }),
        ["markothDefeated"] = (250, new[] { "Deepnest_East_10" })
    };

    /// <summary>
    /// Llega de otro PC un cambio de un dato del mundo (<see cref="SharedProgress.RemoteFieldChanged"/>), ya aplicado.
    /// </summary>
    public static void OnRemoteChange(string field, int before, bool isBool, ushort origin) {
        if (isBool || !Warriors.TryGetValue(field, out var warrior)) {
            return;
        }

        var playerData = PlayerData.instance;
        var gm = GameManager.instance;
        if (playerData == null || gm == null || before >= Absorbed || playerData.GetInt(field) != Absorbed) {
            return;
        }

        var here = InScene(warrior.Scenes);
        if (here != null) {
            Log.Info($"Esencia de {field}: este jugador está en {here}, la da el juego");
            return;
        }

        gm.IntAdd("dreamOrbs", warrior.Amount);
        Log.Info($"Esencia de {field}: +{warrior.Amount} (se ganó en otra sala; ahora {playerData.GetInt("dreamOrbs")})");
    }

    private static string InScene(string[] scenes) {
        for (var i = 0; i < UnitySceneManager.sceneCount; i++) {
            var scene = UnitySceneManager.GetSceneAt(i);
            if (scene.isLoaded && Array.IndexOf(scenes, scene.name) >= 0) {
                return scene.name;
            }
        }

        return null;
    }
}
