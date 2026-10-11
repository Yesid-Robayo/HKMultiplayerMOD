using System;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Random = UnityEngine.Random;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Lo que suelta algo al morir o romperse (el geo de un enemigo, de una roca o de un cofre, los trozos de una vasija)
/// sale volando igual en todos los PCs. Cada jugador tiene su propio botín, así que cada PC lo crea por su cuenta, y la
/// dirección y la fuerza de cada pieza las decide el juego al azar: en un PC el geo caía a la izquierda y en el otro a la
/// derecha. Justo al lanzarlo se pone la misma semilla del azar en los dos (sale de qué objeto es y cuántas veces soltó
/// ya algo) y después el azar vuelve a como estaba, así que el resto del juego no cambia. No hace falta enviar nada.
/// <para>La semilla va en el lanzamiento y no en la muerte: antes de soltar el geo, la muerte gasta azar en cosas que
/// dependen de quién golpeó (el giro del cadáver según la dirección del golpe) y cada PC llegaba al geo con otra tirada.</para>
/// </summary>
internal static class DropSeeds {
    private static bool _hooked;

    /// <summary>Si hay una partida: fuera de ella el juego va como siempre.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Cuántas veces soltó algo cada objeto en esta sala (una roca de geo suelta en cada golpe).</summary>
    private static readonly Dictionary<uint, int> Drops = new();

    private static string _scene = string.Empty;

    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.FlingUtils.SpawnAndFling += (orig, config, spawnPoint, offset) => {
            if (spawnPoint == null) {
                return orig(config, spawnPoint, offset);
            }

            // De un enemigo, solo su geo (lo demás que lance va con su sincronización y solo en el PC que lo mueve: contarlo
            // descuadraría la cuenta). Un enemigo compartido tiene el mismo número en todos los PCs aunque sea una copia
            var health = spawnPoint.GetComponent<HealthManager>();
            var enemy = health != null || EnemySync.IsEnemyObject(spawnPoint);
            if (enemy && (config.Prefab == null || !config.Prefab.name.StartsWith("Geo", StringComparison.Ordinal))) {
                return orig(config, spawnPoint, offset);
            }

            GameObject[] spawned = null;
            Seeded(spawnPoint, EnemySync.SyncIdOf(health), () => spawned = orig(config, spawnPoint, offset));
            return spawned;
        };
        On.Breakable.Break += (orig, self, angleMin, angleMax, impact) =>
            Seeded(self.transform, 0u, () => orig(self, angleMin, angleMax, impact));
        On.HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPool.OnEnter += (orig, self) =>
            SeededAction(self, () => orig(self));
        On.HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPoolVel.OnEnter += (orig, self) =>
            SeededAction(self, () => orig(self));
        // Un objeto brillante que sale volando (de un cofre, de un jefe) elige al azar el lado y luego el ángulo
        On.HutongGames.PlayMaker.Actions.SendRandomEvent.OnEnter += (orig, self) => ShinyAction(self, () => orig(self));
        On.HutongGames.PlayMaker.Actions.FlingObject.OnEnter += (orig, self) => ShinyAction(self, () => orig(self));
    }

    private const string ShinyFsm = "Shiny Control";

    /// <summary>Solo en la lógica de los objetos brillantes: en las demás, ese azar es su comportamiento de siempre.</summary>
    private static void ShinyAction(HutongGames.PlayMaker.FsmStateAction action, Action run) {
        if (action.Fsm?.Name != ShinyFsm) {
            run();
            return;
        }

        SeededAction(action, run);
    }

    /// <summary>
    /// Lanzamientos de la lógica de la sala (rocas de geo, cofres): los de un enemigo no, que van con su sincronización
    /// (sus disparos llegan como copias y su geo sale de su muerte). Se cuentan por estado y acción, no por objeto: en el
    /// PC que no golpea una roca solo se repiten algunos de sus estados (no las piedritas de cada golpe), y una cuenta por
    /// objeto llegaba descuadrada al pago final.
    /// </summary>
    private static void SeededAction(HutongGames.PlayMaker.FsmStateAction action, Action run) {
        var owner = action.Owner;
        if (owner == null || action.State == null || EnemySync.IsEnemyObject(owner.transform)) {
            run();
            return;
        }

        var key = ScenePaths.Hash(owner.transform);
        foreach (var c in action.State.Name) {
            key = (key ^ c) * 16777619u;
        }

        key = (key ^ (uint) Array.IndexOf(action.State.Actions, action)) * 16777619u;
        Seeded(owner.transform, key, run);
    }

    private static void Seeded(Transform source, uint id, Action run) {
        if (!Enabled || source == null || source.gameObject.scene.buildIndex < 0) {
            run();
            return;
        }

        var scene = source.gameObject.scene.name;
        if (scene != _scene) {
            _scene = scene;
            Drops.Clear();
        }

        var key = id != 0 ? id : ScenePaths.Hash(source);
        Drops.TryGetValue(key, out var count);
        Drops[key] = count + 1;

        var saved = Random.state;
        Random.InitState(unchecked((int) (key * 31u + (uint) count)));
        try {
            run();
        } finally {
            Random.state = saved;
        }
    }
}
