using System;
using System.Collections.Generic;
using System.Text;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.UI;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Cinemáticas compartidas: los finales, los créditos y Mr. Mushroom. Si un jugador entra en una, a los demás se les
/// lleva a verla a la vez (estén en la sala que estén) y se salta cuando lo dice el host: cuando todos los que la ven
/// votan saltarla ("Esperando a X para saltar 1/2"), o en cuanto la salta cualquiera si así lo eligió el host. Al
/// terminar, quien fue llevado vuelve a donde estaba; tras los créditos del final, todos vuelven a su banco y la
/// partida sigue (sin pasar por la pantalla de partida completada ni por el menú, que cerraría el mundo coop).
/// <para>Lo lleva el host: cada jugador le informa con <see cref="CutsceneReport"/> de la cinemática que empieza o
/// termina y de su voto; él lleva a los demás (<see cref="CutscenePull"/>), reparte la votación
/// (<see cref="CutsceneVotes"/>) y da la orden de saltar a todos a la vez (<see cref="CutsceneSkip"/>).</para>
/// </summary>
internal sealed class SharedCutscenes {
    /// <summary>Empieza o termina una cinemática compartida, o el jugador vota saltarla (jugador → host).</summary>
    public const MessageType CutsceneReport = (MessageType) 87;

    /// <summary>Otro jugador empezó una cinemática compartida: verla también (host → jugador).</summary>
    public const MessageType CutscenePull = (MessageType) 88;

    /// <summary>Cuántos la ven y quiénes votaron saltarla (host → los que la ven).</summary>
    public const MessageType CutsceneVotes = (MessageType) 89;

    /// <summary>Saltar ya, todos a la vez (host → los que la ven).</summary>
    public const MessageType CutsceneSkip = (MessageType) 90;

    private enum ReportKind : byte {
        Started = 1,
        Ended = 2,
        SkipVote = 3
    }

    /// <summary>
    /// Escenas que se ven juntos: los finales, los créditos y Mr. Mushroom (que sale tras los créditos si se completó su
    /// búsqueda). Todas son parte del final de la partida, igual que las dos pantallas con las que acaba: la de partida
    /// completada y la que desbloquea el modo Alma de Acero. Esas dos también se comparten: al saltarlas (o al acabar)
    /// el juego iría al menú principal, que cerraría el mundo coop, y en su lugar todos vuelven a su banco.
    /// </summary>
    private static readonly HashSet<string> SharedScenes = new() {
        "Cinematic_Ending_A", "Cinematic_Ending_B", "Cinematic_Ending_C", "Cinematic_Ending_D", "Cinematic_Ending_E",
        "End_Credits", "Cinematic_MrMushroom", "End_Game_Completion", "PermaDeath_Unlock"
    };

    /// <summary>Escenas fuera del juego a las que se llega sin volver a ninguna sala (menú, cierre).</summary>
    private static readonly HashSet<string> OutOfGameScenes = new() {
        "Menu_Title", "Quit_To_Menu", "PermaDeath", "BetaEnd"
    };

    /// <summary>Lo más que se espera para llevar a alguien que está cambiando de sala o en pausa.</summary>
    private const float PullWaitSeconds = 60f;

    private static SharedCutscenes _active;
    private static bool _hooked;

    private readonly MessageHandler _onReport;
    private readonly MessageHandler _onPull;
    private readonly MessageHandler _onVotes;
    private readonly MessageHandler _onSkip;
    private readonly Action<SessionPlayer> _onLeft;
    private readonly UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.Scene>
        _onSceneChanged;

    private GameSession _session;
    private Revive _revive;

    // ---- Jugador local ----

    /// <summary>Cinemática compartida que se está viendo (null si ninguna).</summary>
    private string _current;

    private bool _voted;
    private int _votes;
    private int _watchers;
    private string _waitingFor = string.Empty;

    /// <summary>Mientras es true, la llamada a SkipCutscene es la que ordenó el host y se deja pasar.</summary>
    private bool _skipApproved;

    /// <summary>La orden de saltar llegó durante el voto (el del host completa la votación en el sitio).</summary>
    private bool _skipIssued;

    /// <summary>Se llegó a la cinemática llevado por otro: al terminar vuelve a donde estaba.</summary>
    private bool _pulled;

    /// <summary>Dónde estaba antes de que lo llevaran.</summary>
    private string _returnScene;

    private Vector3 _returnPosition;

    /// <summary>Cinemática a la que ir en cuanto se pueda (llegó la orden durante un cambio de sala o en pausa).</summary>
    private string _pendingPull;

    private float _pendingPullUntil;

    /// <summary>La cinemática terminó en una sala de juego: quien fue llevado vuelve a la suya al terminar de cargar.</summary>
    private bool _returnPending;

    private readonly SceneTravel _travel = new();

    /// <summary>Reproductores de vídeo de la cinemática actual (se buscan al entrar en ella).</summary>
    private CinematicPlayer[] _videoPlayers;

    private float _nextVideoCheck;

    /// <summary>Coloca el vídeo delante de la cámara y a su escala (privado en el juego).</summary>
    private static readonly System.Reflection.MethodInfo PlaceVideoMethod = typeof(CinematicPlayer).GetMethod(
        "OnCameraAspectChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private readonly object[] _placeVideoArgs = new object[1];

    // ---- Host ----

    private sealed class Watch {
        public string Scene;
        public readonly List<ushort> Watchers = new();
        public readonly List<ushort> Votes = new();
    }

    private readonly List<Watch> _watches = new();

    // ---- Interfaz ----
    private GameObject _overlay;
    private Text _overlayText;
    private string _shownText;

    static SharedCutscenes() {
        MessageRoutes.Register(CutsceneReport, Route.Server, reliable: true);
        MessageRoutes.Register(CutscenePull, Route.Server, reliable: true);
        MessageRoutes.Register(CutsceneVotes, Route.Server, reliable: true);
        MessageRoutes.Register(CutsceneSkip, Route.Server, reliable: true);
    }

    public SharedCutscenes() {
        _onReport = OnReport;
        _onPull = OnPull;
        _onVotes = OnVotes;
        _onSkip = OnSkip;
        _onLeft = OnPlayerLeft;
        _onSceneChanged = OnActiveSceneChanged;
    }

    /// <summary>Si el jugador local está viendo una cinemática compartida.</summary>
    public bool Watching => _current != null;

    public void Attach(GameSession session, Revive revive) {
        _session = session;
        _revive = revive;
        _active = this;
        ResetLocal();
        _watches.Clear();
        session.On(CutsceneReport, _onReport);
        session.On(CutscenePull, _onPull);
        session.On(CutsceneVotes, _onVotes);
        session.On(CutsceneSkip, _onSkip);
        session.PlayerLeft += _onLeft;
        UnitySceneManager.activeSceneChanged += _onSceneChanged;

        // Ya dentro de una cinemática compartida al abrir o unirse a la partida: cuenta como empezada
        var scene = UnitySceneManager.GetActiveScene().name;
        if (SharedScenes.Contains(scene)) {
            _current = scene;
            Report(ReportKind.Started, scene, false);
        }
    }

    public void Detach() {
        if (_session != null) {
            _session.On(CutsceneReport, null);
            _session.On(CutscenePull, null);
            _session.On(CutsceneVotes, null);
            _session.On(CutsceneSkip, null);
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        UnitySceneManager.activeSceneChanged -= _onSceneChanged;
        if (_active == this) {
            _active = null;
        }

        _revive = null;
        _watches.Clear();
        ResetLocal();
        SetOverlay(string.Empty);
    }

    private void ResetLocal() {
        _current = null;
        _voted = false;
        _votes = 0;
        _watchers = 0;
        _waitingFor = string.Empty;
        _pulled = false;
        _returnScene = null;
        _pendingPull = null;
        _returnPending = false;
        _travel.Cancel();
    }

    // =====================================================================================================
    // Ganchos (al cargar el mod)
    // =====================================================================================================

    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.GameManager.SkipCutscene += (orig, self) => {
            var active = _active;
            // (la llamada de la orden del host vuelve a pasar por aquí: esa no borra la marca)
            if (active != null && !active._skipApproved) {
                active._skipIssued = false;
            }

            if (active == null || active.AllowSkip()) {
                orig(self);
                return;
            }

            // El juego marca "saltando" antes de llamar y solo lo desmarca al saltar de verdad: si se quedara marcado ya
            // no aceptaría más pulsaciones en esta ni en las siguientes cinemáticas. Pero en el host su propio voto
            // puede completar la votación aquí mismo (el mensaje al servidor se procesa en el sitio) y el salto ya
            // empezó: quitarle la marca lo cortaba y el host se quedaba en la cinemática mientras los demás seguían
            if (self.inputHandler != null && !active._skipIssued) {
                self.inputHandler.skippingCutscene = false;
            }
        };

        On.CutsceneHelper.DoSceneLoad += (orig, self) => {
            var active = _active;
            if (active == null || !active.RedirectAfterCutscene(self)) {
                orig(self);
            }
        };
    }

    // =====================================================================================================
    // Escenas
    // =====================================================================================================

    private void OnActiveSceneChanged(UnityEngine.SceneManagement.Scene oldScene, UnityEngine.SceneManagement.Scene newScene) {
        if (_session == null) {
            return;
        }

        // No se usa oldScene.name: al salir de una cinemática la escena anterior puede llegar sin nombre
        var name = newScene.name;
        if (_current != null && name != _current) {
            Report(ReportKind.Ended, _current, false);
        }

        if (SharedScenes.Contains(name)) {
            if (_current == name) {
                return;
            }

            _current = name;
            _voted = false;
            _votes = 0;
            _watchers = 1;
            _waitingFor = string.Empty;
            _pendingPull = null;
            _videoPlayers = UnityEngine.Object.FindObjectsByType<CinematicPlayer>(FindObjectsSortMode.None);
            _nextVideoCheck = 0f;
            Log.Info($"Cinemática compartida: {name}{(_pulled ? " (llevado por un compañero)" : string.Empty)}");
            Report(ReportKind.Started, name, _pulled);
            RefreshOverlay();
            return;
        }

        if (_current == null) {
            return;
        }

        // La cinemática pasó por sí sola a una sala de juego (sin CutsceneHelper): quien fue llevado vuelve a la suya
        if (_pulled && _returnScene != null && !OutOfGameScenes.Contains(name) && name != _returnScene) {
            _returnPending = true;
        }

        _current = null;
        _voted = false;
        _pulled = false;
        _videoPlayers = null;
        SetOverlay(string.Empty);
    }

    /// <summary>
    /// Fin de una cinemática compartida (su CutsceneHelper carga lo siguiente). Si sigue otra del final (los créditos,
    /// Mr. Mushroom), se deja seguir. Si el final acaba (pantalla de partida completada o menú), todos vuelven a su
    /// banco. Si iba a una sala de juego, quien fue llevado vuelve a la suya. Devuelve true si se cambió el destino.
    /// </summary>
    private bool RedirectAfterCutscene(CutsceneHelper helper) {
        if (_session == null || _current == null || UnitySceneManager.GetActiveScene().name != _current) {
            return false;
        }

        var gm = GameManager.instance;
        if (gm == null) {
            return false;
        }

        switch (helper.nextSceneType) {
            case CutsceneHelper.NextScene.EndCredits:
                return false;
            case CutsceneHelper.NextScene.MrMushroomUnlock when gm.playerData.GetInt("mrMushroomState") >= 8:
                return false;
            case CutsceneHelper.NextScene.SpecifyScene:
            case CutsceneHelper.NextScene.GGReturn:
                if (SharedScenes.Contains(helper.nextScene) || !_pulled || _returnScene == null) {
                    return false;
                }

                Log.Info($"Fin de {_current}: de vuelta a donde estaba");
                ReturnToSavedPlace();
                return true;
            default:
                // Fin del final de la partida: a su banco, sin pasar por el menú (cerraría el mundo coop)
                Log.Info($"Fin de {_current}: todos a su banco");
                _pulled = false;
                _returnScene = null;
                gm.ContinueGame();
                return true;
        }
    }

    private void ReturnToSavedPlace() {
        var scene = _returnScene;
        var position = _returnPosition;
        _pulled = false;
        _returnScene = null;
        _returnPending = false;
        if (!_travel.Begin(scene, position)) {
            // Sin poder viajar ahora: a su banco como en el juego
            GameManager.instance?.ContinueGame();
        }
    }

    // =====================================================================================================
    // Cada frame
    // =====================================================================================================

    public void Update() {
        if (_session == null) {
            return;
        }

        _travel.Update();
        KeepVideoInFront();
        var gm = GameManager.instance;
        var ready = gm != null && !gm.IsInSceneTransition && HeroController.instance != null && !gm.isPaused &&
                    gm.gameState == GlobalEnums.GameState.PLAYING;

        if (_returnPending && ready && !_travel.Active) {
            ReturnToSavedPlace();
        }

        if (_pendingPull != null) {
            if (Time.unscaledTime > _pendingPullUntil || _current != null || _revive != null && _revive.IsDowned) {
                _pendingPull = null;
            } else if (ready && !_travel.Active) {
                // Con el menú de pausa abierto no se cambia de escena (el menú se trabaría): se espera a que se cierre
                var scene = _pendingPull;
                _pendingPull = null;
                _pulled = true;
                _returnScene = gm.sceneName;
                _returnPosition = HeroController.instance.transform.position;
                Log.Info($"Un compañero empezó {scene}: viéndola juntos");
                gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
                    SceneName = scene,
                    EntryGateName = string.Empty,
                    EntryDelay = 0f,
                    PreventCameraFadeOut = false,
                    WaitForSceneTransitionCameraFade = true,
                    AlwaysUnloadUnusedAssets = true
                });
            }
        }

    }

    /// <summary>
    /// El vídeo de la cinemática se dibuja en una malla que el juego pone delante de la cámara al empezar a
    /// reproducirlo. A quien se lleva a la cinemática con un cambio de sala, la cámara aún no ha llegado a la escena en
    /// ese momento: la malla quedaba donde estaba su cámara en la sala anterior y se veía todo negro. Mientras se ve una
    /// cinemática, si la malla no está delante de la cámara se vuelve a colocar como lo hace el juego.
    /// </summary>
    private void KeepVideoInFront() {
        if (_videoPlayers == null || Time.unscaledTime < _nextVideoCheck) {
            return;
        }

        _nextVideoCheck = Time.unscaledTime + 0.25f;
        var cameras = GameCameras.instance;
        var camera = cameras != null ? cameras.mainCamera : null;
        if (camera == null) {
            return;
        }

        var cameraPosition = camera.transform.position;
        foreach (var player in _videoPlayers) {
            if (player == null) {
                continue;
            }

            var position = player.transform.position;
            if (Mathf.Abs(position.x - cameraPosition.x) < 0.01f && Mathf.Abs(position.y - cameraPosition.y) < 0.01f) {
                continue;
            }

            Log.Info($"Vídeo de {player.name} fuera de la cámara ({position.x:0.0}, {position.y:0.0}): se coloca delante");
            if (PlaceVideoMethod != null) {
                _placeVideoArgs[0] = ForceCameraAspect.CurrentViewportAspect;
                PlaceVideoMethod.Invoke(player, _placeVideoArgs);
            }

            position = player.transform.position;
            if (Mathf.Abs(position.x - cameraPosition.x) >= 0.01f || Mathf.Abs(position.y - cameraPosition.y) >= 0.01f) {
                player.transform.position = new Vector3(cameraPosition.x, cameraPosition.y, position.z);
            }
        }
    }

    /// <summary>
    /// "Esperando a X para saltar 1/2" en todos los que la ven en cuanto alguien votó y mientras falten otros (a quien
    /// falta se le nombra como "ti"). Se rehace solo al cambiar.
    /// </summary>
    private void RefreshOverlay() {
        SetOverlay(_current != null && _votes > 0 && _watchers > 1 && _votes < _watchers
            ? Lang.T($"Esperando a {_waitingFor} para saltar {_votes}/{_watchers}", $"Waiting for {_waitingFor} to skip {_votes}/{_watchers}")
            : string.Empty);
    }

    // =====================================================================================================
    // Saltar
    // =====================================================================================================

    /// <summary>Lo llama el gancho de <c>SkipCutscene</c>: true si se salta ya; si no, cuenta como voto.</summary>
    private bool AllowSkip() {
        if (_skipApproved || _session == null || !_session.CanSend || _current == null ||
            UnitySceneManager.GetActiveScene().name != _current) {
            return true;
        }

        if (!_voted) {
            _voted = true;
            Log.Info($"Voto para saltar {_current}");
            Report(ReportKind.SkipVote, _current, false);
            RefreshOverlay();
        }

        return false;
    }

    private void Report(ReportKind kind, string scene, bool pulled) {
        if (_session == null || !_session.CanSend) {
            return;
        }

        var writer = _session.Begin(CutsceneReport);
        writer.WriteByte((byte) kind);
        writer.WriteString(scene);
        writer.WriteBool(pulled);
        _session.End();
    }

    private void OnVotes(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var watchers = reader.ReadByte();
        var count = reader.ReadByte();
        if (from.Id != GameSession.HostPlayerId || scene != _current) {
            return;
        }

        _watchers = watchers;
        _votes = count;
        // Los que faltan por votar
        var text = new StringBuilder();
        var missing = reader.ReadByte();
        for (var i = 0; i < missing; i++) {
            var id = reader.ReadUShort();
            foreach (var player in _session.Players) {
                if (player.Id == id) {
                    text.Append(text.Length > 0 ? ", " : string.Empty).Append(player.IsLocal ? Lang.T("ti", "you") : player.Name);
                }
            }
        }

        _waitingFor = text.Length > 0 ? text.ToString() : Lang.T("los demás", "the others");
        RefreshOverlay();
    }

    private void OnSkip(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        if (from.Id != GameSession.HostPlayerId || scene != _current || GameManager.instance == null) {
            return;
        }

        Log.Info($"Saltando {_current} (orden del host)");
        _voted = false;
        _votes = 0;
        RefreshOverlay();
        var gm = GameManager.instance;
        if (gm.inputHandler != null) {
            gm.inputHandler.skippingCutscene = true;
        }

        _skipApproved = true;
        _skipIssued = true;
        try {
            gm.SkipCutscene();
        } finally {
            _skipApproved = false;
        }
    }

    // =====================================================================================================
    // Llevar a los demás
    // =====================================================================================================

    private void OnPull(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        if (from.Id != GameSession.HostPlayerId || !SharedScenes.Contains(scene)) {
            return;
        }

        // Ya viendo una cinemática compartida (p. ej. de un final a los créditos): se llega por su curso normal. Un
        // caído no va (su compañero lo revive al volver o muere de forma normal si no queda nadie)
        if (_current != null || UnitySceneManager.GetActiveScene().name == scene || _revive != null && _revive.IsDowned) {
            return;
        }

        _pendingPull = scene;
        _pendingPullUntil = Time.unscaledTime + PullWaitSeconds;
    }

    // =====================================================================================================
    // Host
    // =====================================================================================================

    private void OnReport(SessionPlayer from, PacketReader reader) {
        var kind = (ReportKind) reader.ReadByte();
        var scene = reader.ReadString(128);
        var pulled = reader.ReadBool();
        if (_session == null || !_session.IsHost || !SharedScenes.Contains(scene)) {
            return;
        }

        switch (kind) {
            case ReportKind.Started:
                OnStarted(from, scene, pulled);
                break;
            case ReportKind.Ended:
                RemoveWatcher(from.Id, scene);
                break;
            case ReportKind.SkipVote:
                var watch = Find(scene);
                if (watch == null || !watch.Watchers.Contains(from.Id) || watch.Votes.Contains(from.Id)) {
                    return;
                }

                watch.Votes.Add(from.Id);
                Log.Info($"{from.Name} votó saltar {scene} ({watch.Votes.Count}/{watch.Watchers.Count})");
                CheckVotes(watch);
                break;
        }
    }

    private void OnStarted(SessionPlayer from, string scene, bool pulled) {
        // Solo se ve una a la vez: si seguía apuntado en otra, sale de ella
        for (var i = _watches.Count - 1; i >= 0; i--) {
            if (_watches[i].Scene != scene) {
                RemoveWatcher(from.Id, _watches[i].Scene);
            }
        }

        var watch = Find(scene);
        if (watch == null) {
            watch = new Watch { Scene = scene };
            _watches.Add(watch);
        }

        var isNew = watch.Watchers.Count == 0;
        if (!watch.Watchers.Contains(from.Id)) {
            watch.Watchers.Add(from.Id);
        }

        Log.Info($"{from.Name} ve {scene} ({watch.Watchers.Count} viéndola)");

        // Quien fue llevado no arrastra a nadie: si el que la empezó ya salió, la ve solo
        if (isNew && !pulled) {
            foreach (var player in _session.Players) {
                if (player.Id != from.Id) {
                    SendPull(player, scene);
                }
            }
        }

        SendVotes(watch);
    }

    private Watch Find(string scene) {
        foreach (var watch in _watches) {
            if (watch.Scene == scene) {
                return watch;
            }
        }

        return null;
    }

    private void RemoveWatcher(ushort id, string scene) {
        var watch = Find(scene);
        if (watch == null || !watch.Watchers.Remove(id)) {
            return;
        }

        watch.Votes.Remove(id);
        if (watch.Watchers.Count == 0) {
            _watches.Remove(watch);
            return;
        }

        // Si se fue el único que faltaba por votar, los demás ya pueden saltar
        CheckVotes(watch);
    }

    private void OnPlayerLeft(SessionPlayer player) {
        if (_session == null || !_session.IsHost) {
            return;
        }

        for (var i = _watches.Count - 1; i >= 0; i--) {
            RemoveWatcher(player.Id, _watches[i].Scene);
        }
    }

    private void CheckVotes(Watch watch) {
        var needed = HostSettings.Current.AnyoneSkipsCutscenes ? 1 : watch.Watchers.Count;
        var all = watch.Votes.Count >= needed;
        if (!HostSettings.Current.AnyoneSkipsCutscenes) {
            foreach (var id in watch.Watchers) {
                all &= watch.Votes.Contains(id);
            }
        }

        if (watch.Votes.Count > 0 && all) {
            watch.Votes.Clear();
            Log.Info($"Se salta {watch.Scene}");
            foreach (var id in watch.Watchers.ToArray()) {
                SendSkip(id, watch.Scene);
            }

            return;
        }

        SendVotes(watch);
    }

    private SessionPlayer PlayerById(ushort id) {
        foreach (var player in _session.Players) {
            if (player.Id == id) {
                return player;
            }
        }

        return null;
    }

    private void SendPull(SessionPlayer player, string scene) {
        if (player.IsLocal) {
            var reader = LocalReader(scene);
            OnPull(player, reader);
            return;
        }

        if (player.Connection == null) {
            return;
        }

        var writer = _session.BeginTo(player, CutscenePull);
        writer.WriteString(scene);
        _session.EndTo();
    }

    private void SendSkip(ushort id, string scene) {
        var player = PlayerById(id);
        if (player == null) {
            return;
        }

        if (player.IsLocal) {
            OnSkip(player, LocalReader(scene));
            return;
        }

        if (player.Connection == null) {
            return;
        }

        var writer = _session.BeginTo(player, CutsceneSkip);
        writer.WriteString(scene);
        _session.EndTo();
    }

    private void SendVotes(Watch watch) {
        foreach (var id in watch.Watchers) {
            var player = PlayerById(id);
            if (player == null) {
                continue;
            }

            var writer = player.IsLocal ? LocalWriter() : player.Connection != null ? _session.BeginTo(player, CutsceneVotes) : null;
            if (writer == null) {
                continue;
            }

            writer.WriteString(watch.Scene);
            writer.WriteByte((byte) watch.Watchers.Count);
            writer.WriteByte((byte) watch.Votes.Count);
            var missingAt = writer.Position;
            writer.WriteByte(0);
            var missing = 0;
            foreach (var other in watch.Watchers) {
                if (!watch.Votes.Contains(other)) {
                    writer.WriteUShort(other);
                    missing++;
                }
            }

            var end = writer.Position;
            writer.Seek(missingAt);
            writer.WriteByte((byte) missing);
            writer.Seek(end);
            if (player.IsLocal) {
                _localReader.Reset(_localBuffer, 0, writer.Length);
                OnVotes(player, _localReader);
            } else {
                _session.EndTo();
            }
        }
    }

    // ---- Mensajes del host a su propio jugador (sin red) ----
    private readonly byte[] _localBuffer = new byte[NetConstants.MaxMessageSize];
    private readonly PacketWriter _localWriter = new();
    private readonly PacketReader _localReader = new();

    private PacketWriter LocalWriter() {
        _localWriter.Reset(_localBuffer, 0, _localBuffer.Length);
        return _localWriter;
    }

    private PacketReader LocalReader(string scene) {
        var writer = LocalWriter();
        writer.WriteString(scene);
        _localReader.Reset(_localBuffer, 0, writer.Length);
        return _localReader;
    }

    // =====================================================================================================
    // Interfaz
    // =====================================================================================================

    /// <summary>Texto abajo en el centro, sobre la cinemática (lienzo propio: el del HUD no se ve en ellas).</summary>
    private void SetOverlay(string text) {
        if (ReferenceEquals(text, _shownText) || text == _shownText) {
            return;
        }

        _shownText = text;
        if (text.Length == 0) {
            if (_overlay != null) {
                _overlay.SetActive(false);
            }

            return;
        }

        if (_overlay == null) {
            CreateOverlay();
        }

        _overlayText.text = text;
        _overlay.SetActive(true);
    }

    private void CreateOverlay() {
        _overlay = new GameObject("HKMultiplayer Cinemática", typeof(Canvas), typeof(CanvasScaler));
        UnityEngine.Object.DontDestroyOnLoad(_overlay);
        var canvas = _overlay.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 520;
        var scaler = _overlay.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var textObject = new GameObject("Texto", typeof(RectTransform), typeof(Text), typeof(Shadow));
        textObject.transform.SetParent(_overlay.transform, false);
        var rect = (RectTransform) textObject.transform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, 90f);
        rect.sizeDelta = new Vector2(-120f, 80f);
        _overlayText = textObject.GetComponent<Text>();
        _overlayText.font = HudCanvas.BodyFont;
        _overlayText.fontSize = 40;
        _overlayText.alignment = TextAnchor.MiddleCenter;
        _overlayText.color = Color.white;
        _overlayText.raycastTarget = false;
        textObject.GetComponent<Shadow>().effectDistance = new Vector2(2f, -2f);
    }

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() {
        var text = new StringBuilder();
        text.Append($"cinemática={_current ?? "-"} votó={_voted} votos={_votes}/{_watchers} esperando=[{_waitingFor}] " +
                    $"llevado={_pulled} vuelta={_returnScene ?? "-"} pendiente={_pendingPull ?? "-"} viaje={_travel.Active}");
        if (_session != null && _session.IsHost) {
            foreach (var watch in _watches) {
                text.Append($" | {watch.Scene}: ven=[{string.Join(",", watch.Watchers)}] votos=[{string.Join(",", watch.Votes)}]");
            }
        }

        return text.ToString();
    }
}
