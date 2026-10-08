using System;
using System.Reflection;
using HKMultiplayer.Util;

namespace HKMultiplayer.Game.World;

/// <summary>
/// Redirige la carga y el guardado del juego a un mundo coop mientras se juega en él. Las ranuras normales no se
/// tocan: durante el mundo coop el juego cree estar en la ranura -1, que su propio guardado rechaza, así que aunque
/// algún camino se saltara este desvío no escribiría en ninguna partida normal.
/// </summary>
internal static class CoopSaves {
    /// <summary>Ranura que se usa durante un mundo coop (el juego no guarda en ranuras negativas).</summary>
    public const int CoopSlot = -1;

    private static readonly MethodInfo ResetGameTimer =
        typeof(GameManager).GetMethod("ResetGameTimer", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool _installed;
    private static SaveGameData _pendingLoad;

    /// <summary>Mundo en el que se está jugando (null = partida normal o menú).</summary>
    public static CoopWorldInfo ActiveWorld { get; private set; }

    /// <summary>Se avisa tras cada guardado del mundo (en el host).</summary>
    public static event Action<CoopWorldInfo> Saved;

    public static void Install() {
        if (_installed) {
            return;
        }

        _installed = true;
        On.GameManager.LoadGame += OnLoadGame;
        On.GameManager.SaveGame_int_Action1 += OnSaveGame;
    }

    /// <summary>Si se puede empezar un mundo ahora (desde el menú principal).</summary>
    public static bool CanStart {
        get {
            var gm = GameManager.instance;
            return gm != null && gm.gameState == GlobalEnums.GameState.MAIN_MENU && HeroController.instance == null;
        }
    }

    /// <summary>Empieza a jugar un mundo coop desde el menú principal: partida nueva si es nuevo, si no lo carga.</summary>
    public static bool Start(CoopWorldInfo world) {
        if (!CanStart) {
            Log.Warn("Los mundos coop se abren desde el menú principal");
            return false;
        }

        var gm = GameManager.instance;
        _enteredGame = false;
        if (world.IsNew) {
            ActiveWorld = world;
            gm.playerData.Reset();
            gm.sceneData.Reset();
            gm.profileID = CoopSlot;
            Log.Info($"Mundo coop nuevo: {world.Name}");
            gm.StartNewGame();
            return true;
        }

        var data = WorldStore.Load(world.Id);
        if (data == null) {
            Log.Error($"No se pudo cargar el mundo {world.Name}");
            return false;
        }

        ActiveWorld = world;
        _pendingLoad = data;
        Log.Info($"Cargando el mundo coop {world.Name}");
        // El juego hace su recorrido normal de "continuar partida"; la lectura de la ranura se cambia por el mundo
        gm.LoadGameFromUI(1);
        return true;
    }

    /// <summary>Si ya se llegó a jugar en el mundo activo (antes de eso el juego aún está en estado de menú).</summary>
    private static bool _enteredGame;

    /// <summary>Llamar cada frame: detecta la vuelta al menú principal tras haber jugado en el mundo.</summary>
    public static void Update() {
        if (ActiveWorld == null) {
            return;
        }

        if (HeroController.instance != null) {
            _enteredGame = true;
        } else if (_enteredGame && GameManager.instance != null &&
                   GameManager.instance.gameState == GlobalEnums.GameState.MAIN_MENU) {
            OnMainMenu();
        }
    }

    /// <summary>Se volvió al menú: el mundo deja de estar activo (el juego ya guardó al salir).</summary>
    private static void OnMainMenu() {
        if (ActiveWorld != null) {
            Log.Info($"Fuera del mundo coop {ActiveWorld.Name}");
        }

        ActiveWorld = null;
        _pendingLoad = null;
        _enteredGame = false;
    }

    private static void OnLoadGame(On.GameManager.orig_LoadGame orig, GameManager self, int slot, Action<bool> callback) {
        var data = _pendingLoad;
        if (data == null) {
            orig(self, slot, callback);
            return;
        }

        _pendingLoad = null;
        try {
            // Lo mismo que hace el juego tras leer una ranura
            PlayerData.instance = data.playerData;
            self.playerData = data.playerData;
            SceneData.instance = data.sceneData;
            self.sceneData = data.sceneData;
            self.profileID = CoopSlot;
            self.inputHandler.RefreshPlayerData();
            callback?.Invoke(true);
        } catch (Exception e) {
            Log.Error($"Error al cargar el mundo coop: {e}");
            ActiveWorld = null;
            callback?.Invoke(false);
        }
    }

    private static void OnSaveGame(On.GameManager.orig_SaveGame_int_Action1 orig, GameManager self, int slot,
        Action<bool> callback) {
        var world = ActiveWorld;
        if (world == null) {
            orig(self, slot, callback);
            return;
        }

        // Nunca se llama al guardado original durante un mundo coop
        try {
            self.SaveLevelState();
            var playerData = self.playerData;
            playerData.SetFloat("playTime", self.PlayTime);
            ResetGameTimer?.Invoke(self, null);
            playerData.SetString("version", "1.5.12620");
            playerData.CountGameCompletion();
            WorldStore.Save(world, new SaveGameData(playerData, self.sceneData));
            Log.Info($"Mundo coop guardado: {world.Name}");
            Saved?.Invoke(world);
            callback?.Invoke(true);
        } catch (Exception e) {
            Log.Error($"Error al guardar el mundo coop: {e}");
            callback?.Invoke(false);
        }
    }
}
