using System.Collections.Generic;
using HKMultiplayer.Game;
using HKMultiplayer.Settings;
using Modding;
using UnityEngine;

namespace HKMultiplayer;

/// <summary>
/// Entrada del mod para la Modding API: carga los ajustes y crea el objeto persistente que mueve la red y la
/// sincronización (<see cref="MultiplayerRunner"/>).
/// </summary>
public sealed class HKMultiplayerMod : Mod, IGlobalSettings<GlobalSettings> {
    /// <summary>Versión del mod: el host solo acepta jugadores con la misma.</summary>
    public const string Version = "0.21.1";

    public static GlobalSettings Settings { get; private set; } = new();

    private static HKMultiplayerMod _instance;

    public HKMultiplayerMod() : base("HKMultiplayer") {
        _instance = this;
    }

    public override string GetVersion() => Version;

    public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects) {
        Util.Log.Info($"Iniciando HKMultiplayer {Version}");
        if (!Game.World.WorldSync.IsValidKey(Settings.PlayerKey)) {
            Settings.PlayerKey = Game.World.WorldSync.NewKey();
            SaveGlobalSettings();
        }

        var runner = new GameObject("HKMultiplayer");
        Object.DontDestroyOnLoad(runner);
        runner.AddComponent<MultiplayerRunner>();
    }

    public void OnLoadGlobal(GlobalSettings settings) {
        Settings = settings ?? new GlobalSettings();
        if (!Game.World.WorldSync.IsValidKey(Settings.PlayerKey)) {
            Settings.PlayerKey = Game.World.WorldSync.NewKey();
        }
    }

    public GlobalSettings OnSaveGlobal() => Settings;

    /// <summary>Guarda los ajustes ya (p. ej. al conectarse con un nombre o dirección nuevos).</summary>
    public static void SaveSettings() => _instance?.SaveGlobalSettings();
}
