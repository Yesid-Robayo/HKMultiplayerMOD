using System;
using System.Collections;
using System.Reflection;
using GlobalEnums;
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

    private static readonly FieldInfo OpeningChainField =
        typeof(OpeningSequence).GetField("chainSequence", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool _installed;
    private static SaveGameData _pendingLoad;

    /// <summary>El personaje del invitado es nuevo en este mundo: antes de entrar verá la introducción del juego.</summary>
    private static bool _introPending;

    /// <summary>Se está viendo esa introducción (al acabar se entra al mundo).</summary>
    private static bool _introPlaying;

    /// <summary>Un invitado nuevo está viendo la introducción (aún no entró en el mundo).</summary>
    public static bool IntroPlaying => _introPlaying;

    /// <summary>Mundo en el que se está jugando (null = partida normal o menú).</summary>
    public static CoopWorldInfo ActiveWorld { get; private set; }

    /// <summary>Si el mundo activo es de este PC (host). Si no, es el de otro y aquí no se guarda nada.</summary>
    public static bool IsHostWorld { get; private set; }

    /// <summary>Mientras se juega el mundo de otro: el juego quiere guardar y hay que enviar el personaje al host.</summary>
    public static event Action<PlayerData> GuestSaving;

    /// <summary>Se volvió al menú principal desde un mundo coop.</summary>
    public static event Action ExitedWorld;

    /// <summary>Se avisa tras cada guardado del mundo (en el host).</summary>
    public static event Action<CoopWorldInfo> Saved;

    public static void Install() {
        if (_installed) {
            return;
        }

        _installed = true;
        On.GameManager.LoadGame += OnLoadGame;
        On.GameManager.SaveGame_int_Action1 += OnSaveGame;
        On.GameManager.ContinueGame += OnContinueGame;
        On.GameManager.StartNewGame += (orig, self, permadeath, bossRush) => {
            _loadStartedAt = UnityEngine.Time.unscaledTime;
            orig(self, permadeath, bossRush);
        };
        On.OpeningSequence.Start += (orig, self) => _introPlaying ? GuestIntro(self) : orig(self);
    }

    /// <summary>
    /// Un invitado nuevo entra al mundo del host como quien continúa una partida (ya está empezada), y el juego solo pone
    /// la introducción (el poema y la llegada a Hallownest) en una partida nueva: no la veía nunca. Ahora la ve primero y
    /// después entra como siempre, donde reaparece el host.
    /// </summary>
    private static void OnContinueGame(On.GameManager.orig_ContinueGame orig, GameManager self) {
        _loadStartedAt = UnityEngine.Time.unscaledTime;
        if (!_introPending || ActiveWorld == null || IsHostWorld) {
            orig(self);
            return;
        }

        _introPending = false;
        _introPlaying = true;
        Log.Info("Personaje nuevo: primero la introducción del juego");
        self.StartCoroutine(self.RunStartNewGame());
    }

    /// <summary>
    /// La introducción como la pone el juego (se puede saltar igual), pero al acabar no lleva al Paso del Rey de una
    /// partida nueva: sigue con la entrada normal al mundo del host.
    /// </summary>
    private static IEnumerator GuestIntro(OpeningSequence opening) {
        var gm = GameManager.instance;
        var cameras = GameCameras.instance;
        var chain = OpeningChainField?.GetValue(opening) as ChainSequence;
        cameras.cameraFadeFSM.Fsm.Event("FADE SCENE IN");
        cameras.DisableImageEffects();
        PlayMakerFSM.BroadcastEvent("START FADE OUT");
        gm.ui.SetState(UIState.CUTSCENE);
        gm.RegisterSkippable(opening);
        if (chain != null) {
            chain.Begin();
            while (chain.IsPlaying) {
                var mode = chain.IsCurrentSkipped ? SkipPromptMode.NOT_SKIPPABLE : SkipPromptMode.SKIP_PROMPT;
                if (gm.inputHandler.skipMode != mode) {
                    gm.inputHandler.SetSkipMode(mode);
                }

                yield return null;
            }
        }

        gm.inputHandler.SetSkipMode(SkipPromptMode.NOT_SKIPPABLE);
        gm.DeregisterSkippable(opening);
        _introPlaying = false;
        Log.Info("Introducción terminada: entrando en el mundo del host");
        gm.ContinueGame();
    }

    /// <summary>Si el juego está en el menú principal, sin caballero.</summary>
    public static bool AtMainMenu {
        get {
            var gm = GameManager.instance;
            return gm != null && gm.gameState == GlobalEnums.GameState.MAIN_MENU && HeroController.instance == null;
        }
    }

    /// <summary>
    /// Cuándo empezó a cargarse una partida (`ContinueGame` o `StartNewGame`). El juego sigue 2,6 s en el menú principal
    /// sin caballero durante el fundido: si el mundo del host llegaba entonces, se empezaba otra carga encima.
    /// </summary>
    private static float _loadStartedAt = -100f;

    private const float LoadStartWindow = 5f;

    /// <summary>Si se puede empezar un mundo ahora: en el menú principal y sin otra partida empezando a cargarse.</summary>
    public static bool CanStart => AtMainMenu && UnityEngine.Time.unscaledTime - _loadStartedAt > LoadStartWindow;

    /// <summary>Empieza a jugar un mundo coop desde el menú principal: partida nueva si es nuevo, si no lo carga.</summary>
    public static bool Start(CoopWorldInfo world) {
        if (!CanStart) {
            Log.Warn("Los mundos coop se abren desde el menú principal");
            return false;
        }

        var gm = GameManager.instance;
        _enteredGame = false;
        IsHostWorld = true;
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

    /// <summary>Juega el mundo de otro (recibido del host) desde el menú principal. Aquí no se guarda nada.</summary>
    public static bool StartGuest(SaveGameData world) {
        if (!CanStart) {
            Log.Warn("El mundo del host llegó fuera del menú principal");
            return false;
        }

        _enteredGame = false;
        IsHostWorld = false;
        ActiveWorld = new CoopWorldInfo { Id = string.Empty, Name = "Mundo del host" };
        _pendingLoad = world;
        // Un personaje nuevo empieza sin tiempo jugado (el tiempo es de cada personaje)
        _introPending = world.playerData.playTime < 1f;
        _introPlaying = false;
        Log.Info("Entrando en el mundo del host");
        GameManager.instance.LoadGameFromUI(1);
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
        _introPending = false;
        _introPlaying = false;
        _enteredGame = false;
        IsHostWorld = false;
        ExitedWorld?.Invoke();
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
            SharedProgress.FlushNow();
            self.SaveLevelState();
            var playerData = self.playerData;
            playerData.SetFloat("playTime", self.PlayTime);
            ResetGameTimer?.Invoke(self, null);
            if (!IsHostWorld) {
                // Mundo de otro: el personaje va al host, aquí no se escribe nada
                GuestSaving?.Invoke(playerData);
                callback?.Invoke(true);
                return;
            }

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
