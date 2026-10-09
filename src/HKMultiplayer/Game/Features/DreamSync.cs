using System;
using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Game.Scene;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using HutongGames.PlayMaker;
using UnityEngine;
using BeginSceneTransitionAction = HutongGames.PlayMaker.Actions.BeginSceneTransition;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Sueños compartidos: si un jugador entra en un sueño con el aguijón onírico, los compañeros que están en la misma sala
/// entran con él, con el mismo destello. Si están en otra sala, no. Al ganar (el sueño termina y lleva de vuelta a la
/// sala del mundo), salen todos los que están en ese sueño.
/// <para>Las entradas a un sueño son FSM de la sala con un cambio de sala de tipo sueño (el cuerpo del Falso Caballero,
/// Grimm dormido, el Hollow Knight vencido...). Se buscan al cargar la sala y se mira cada frame si alguna sale de su
/// reposo: eso es que el aguijón de este jugador la golpeó. Se avisa a la sala (<see cref="DreamEnter"/>) con el estado
/// al que pasó, y en los demás PCs la misma FSM pasa a ese estado: hace su destello, guarda la sala de vuelta y cambia
/// de sala como en el PC de origen. Si en un PC esa entrada no está (la de la Radiancia pide el Corazón del Vacío), se
/// entra directamente al mismo sueño por la misma puerta.</para>
/// <para>Al ganar (el jefe del sueño murió y el sueño lleva de vuelta al mundo), se avisa a la sala
/// (<see cref="DreamExit"/>) y los que siguen en ese sueño salen igual. Cualquier otra salida es solo de quien sale:
/// caer al vacío (el sueño lo devuelve al mundo), morir (si caen todos, cada uno despierta por su cuenta) o un sueño
/// sin jefe; los demás siguen dentro.</para>
/// </summary>
internal sealed class DreamSync {
    /// <summary>Este jugador entró en un sueño desde la sala: qué FSM y a qué estado pasó (→ sala).</summary>
    public const MessageType DreamEnter = (MessageType) 93;

    /// <summary>Este jugador sale del sueño en el que está, a qué sala y por qué puerta (→ sala).</summary>
    public const MessageType DreamExit = (MessageType) 94;

    /// <summary>Tiempo en un estado para darlo por reposo de la entrada.</summary>
    private const float RestSeconds = 1f;

    private sealed class Entry {
        public uint Id;
        public PlayMakerFSM Fsm;
        public BeginSceneTransitionAction Transition;
        public string LastState;
        public float LastStateTime;
        public string RestState;
    }

    private static DreamSync _active;
    private static bool _hooked;

    /// <summary>Se está entrando o saliendo por un aviso de otro PC (no se vuelve a avisar).</summary>
    private static bool _applying;

    private readonly MessageHandler _onEnter;
    private readonly MessageHandler _onExit;
    private readonly List<Entry> _entries = new();
    private GameSession _session;
    private Revive _revive;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;

    /// <summary>En este sueño se vio un jefe vivo (solo entonces una salida puede ser por ganar).</summary>
    private bool _bossSeen;

    private float _nextBossCheck;

    /// <summary>Si el jugador local está en un sueño (zona del mapa del sueño).</summary>
    public bool InDream { get; private set; }

    static DreamSync() {
        MessageRoutes.Register(DreamEnter, Route.SameScene, reliable: true);
        MessageRoutes.Register(DreamExit, Route.SameScene, reliable: true);
    }

    public DreamSync() {
        _onEnter = OnDreamEnter;
        _onExit = OnDreamExit;
    }

    public void Attach(GameSession session, Revive revive) {
        _session = session;
        _revive = revive;
        _active = this;
        _entries.Clear();
        _scene = string.Empty;
        session.On(DreamEnter, _onEnter);
        session.On(DreamExit, _onExit);
    }

    public void Detach() {
        if (_session != null) {
            _session.On(DreamEnter, null);
            _session.On(DreamExit, null);
            _session = null;
        }

        if (_active == this) {
            _active = null;
        }

        _revive = null;
        _entries.Clear();
        _scene = string.Empty;
        InDream = false;
    }

    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.GameManager.BeginSceneTransition += (orig, self, info) => {
            _active?.OnSceneTransition(info);
            orig(self, info);
        };
    }

    private static bool IsDreamVisualization(GameManager.SceneLoadVisualizations visualization) =>
        visualization is GameManager.SceneLoadVisualizations.Dream or GameManager.SceneLoadVisualizations.GrimmDream;

    private static bool LocalInDreamZone() => Vitals.CurrentZone() == (byte) GlobalEnums.MapZone.DREAM_WORLD;

    // =====================================================================================================
    // Cada frame
    // =====================================================================================================

    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            _scene = scene;
            _sceneHash = PlayerSnapshot.HashScene(scene);
            _entries.Clear();
            _bossSeen = false;
            if (scene.Length == 0) {
                return;
            }

            InDream = LocalInDreamZone();
            Scan();
            // Lo que la sala enciende al prepararse (el cuerpo del jefe vencido) puede tardar un poco
            _rescanAt = Time.unscaledTime + 2f;
            return;
        }

        if (scene.Length == 0) {
            return;
        }

        if (_rescanAt > 0f && Time.unscaledTime >= _rescanAt) {
            _rescanAt = 0f;
            Scan();
        }

        var now = Time.unscaledTime;
        if (InDream && !_bossSeen && now >= _nextBossCheck) {
            _nextBossCheck = now + 0.5f;
            _bossSeen = EnemySync.BossInRoom;
        }

        foreach (var entry in _entries) {
            if (entry.Fsm == null) {
                continue;
            }

            var state = entry.Fsm.ActiveStateName;
            if (state == entry.LastState) {
                if (entry.RestState == null && now - entry.LastStateTime >= RestSeconds) {
                    entry.RestState = state;
                }

                continue;
            }

            var from = entry.LastState;
            entry.LastState = state;
            entry.LastStateTime = now;
            // Sale del reposo: el aguijón onírico de este jugador la golpeó
            if (!_applying && entry.RestState != null && from == entry.RestState) {
                SendEnter(entry, state);
            }
        }
    }

    /// <summary>FSM de la sala con un cambio de sala de tipo sueño (entradas a un sueño).</summary>
    private void Scan() {
        // Dentro de un sueño no se busca: sus cambios de sala son salidas (van por el gancho del cambio de sala)
        if (InDream) {
            return;
        }

        foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            if (fsm.gameObject.scene.buildIndex < 0 || Contains(fsm)) {
                continue;
            }

            var transition = FindDreamTransition(fsm);
            if (transition == null) {
                continue;
            }

            var id = ScenePaths.Hash(fsm.transform) ^ (uint) fsm.FsmName.GetHashCode();
            _entries.Add(new Entry {
                Id = id, Fsm = fsm, Transition = transition, LastState = fsm.ActiveStateName, LastStateTime = Time.unscaledTime
            });
            Log.Info($"Entrada a un sueño: {fsm.gameObject.name}/{fsm.FsmName} → {transition.sceneName.Value}");
        }
    }

    private bool Contains(PlayMakerFSM fsm) {
        foreach (var entry in _entries) {
            if (entry.Fsm == fsm) {
                return true;
            }
        }

        return false;
    }

    private static BeginSceneTransitionAction FindDreamTransition(PlayMakerFSM fsm) {
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action is BeginSceneTransitionAction transition && transition.visualization?.Value is
                        GameManager.SceneLoadVisualizations visualization && IsDreamVisualization(visualization)) {
                    return transition;
                }
            }
        }

        return null;
    }

    // =====================================================================================================
    // Entrar
    // =====================================================================================================

    private void SendEnter(Entry entry, string state) {
        if (_session == null || !_session.CanSend || !AnyoneElseHere()) {
            return;
        }

        var transition = entry.Transition;
        var target = transition.sceneName.Value ?? string.Empty;
        Log.Info($"Entrando en el sueño {target} ({entry.Fsm.gameObject.name}): los compañeros de la sala vienen también");
        var writer = _session.Begin(DreamEnter);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(entry.Id);
        writer.WriteString(state);
        writer.WriteString(LeaveEvent(entry));
        writer.WriteString(target);
        writer.WriteString(transition.entryGateName.Value ?? string.Empty);
        writer.WriteByte((byte) (GameManager.SceneLoadVisualizations) transition.visualization.Value);
        writer.WriteFloat(transition.entryDelay.Value);
        writer.WriteBool(transition.preventCameraFadeOut);
        _session.End();
    }

    /// <summary>
    /// Aviso con el que la entrada sale de su reposo ("DREAM HIT" en las de los jefes). Se mira cada frame, así que
    /// pueden haber pasado varios estados de un frame (en el mismo frame "Take Control" e "Impact"): con el aviso, la
    /// copia pasa por todos ellos (el destello y la sala de vuelta) en lugar de saltar al último.
    /// </summary>
    private static string LeaveEvent(Entry entry) {
        foreach (var state in entry.Fsm.FsmStates) {
            if (state.Name != entry.RestState) {
                continue;
            }

            foreach (var transition in state.Transitions) {
                if (transition.ToState != entry.RestState && transition.EventName != "FINISHED") {
                    return transition.EventName;
                }
            }
        }

        return string.Empty;
    }

    private bool AnyoneElseHere() {
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene) {
                return true;
            }
        }

        return false;
    }

    private void OnDreamEnter(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var state = reader.ReadString(128);
        var leaveEvent = reader.ReadString(128);
        var target = reader.ReadString(128);
        var gate = reader.ReadString(128);
        var visualization = (GameManager.SceneLoadVisualizations) reader.ReadByte();
        var delay = reader.ReadFloat();
        var preventFade = reader.ReadBool();
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (sceneHash != _sceneHash || _scene.Length == 0 || gm == null || hero == null || gm.IsInSceneTransition ||
            hero.cState.dead || _revive != null && _revive.IsDowned || InDream) {
            return;
        }

        Entry entry = null;
        foreach (var candidate in _entries) {
            if (candidate.Id == id && candidate.Fsm != null) {
                entry = candidate;
            }
        }

        // La sala de vuelta (al ganar, caer o morir se despierta en ella, por su puerta "door_dreamReturn") es esta, la
        // misma que la de quien usó el aguijón: se pone antes de entrar, sin depender de la FSM de la entrada
        PlayerData.instance.SetString("dreamReturnScene", gm.sceneName);
        if (entry != null && entry.Fsm.gameObject.activeInHierarchy && entry.Fsm.ActiveStateName == entry.RestState &&
            HasState(entry.Fsm, state)) {
            // La misma entrada que en su PC: el mismo destello y el mismo cambio de sala
            Log.Info($"{from.Name} entró en el sueño {target}: se entra con él por la misma entrada " +
                     $"({(leaveEvent.Length > 0 ? leaveEvent : state)})");
            entry.LastStateTime = Time.unscaledTime;
            _applying = true;
            if (leaveEvent.Length > 0) {
                try {
                    entry.Fsm.SendEvent(leaveEvent);
                } finally {
                    _applying = false;
                }

                entry.LastState = entry.Fsm.ActiveStateName;
                return;
            }

            entry.LastState = state;
            var fsm = entry.Fsm.Fsm;
            FsmExecutionStack.PushFsm(fsm);
            try {
                fsm.SetState(state);
                fsm.UpdateStateChanges();
            } finally {
                FsmExecutionStack.PopFsm();
                _applying = false;
            }

            return;
        }

        // La entrada no está en este PC (p. ej. sin el Corazón del Vacío para la Radiancia): directo al mismo sueño
        Log.Info($"{from.Name} entró en el sueño {target}: se entra con él directamente (aquí no está esa entrada)");
        hero.RelinquishControl();
        _applying = true;
        try {
            gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = target,
                EntryGateName = gate,
                EntryDelay = delay,
                Visualization = visualization,
                PreventCameraFadeOut = true,
                WaitForSceneTransitionCameraFade = !preventFade,
                AlwaysUnloadUnusedAssets = false
            });
        } finally {
            _applying = false;
        }
    }

    private static bool HasState(PlayMakerFSM fsm, string name) => Array.Exists(fsm.FsmStates, s => s.Name == name);

    // =====================================================================================================
    // Salir
    // =====================================================================================================

    /// <summary>
    /// Todo cambio de sala pasa por aquí: si el jugador local sale de su sueño con una vuelta de tipo sueño y sin morir,
    /// los que siguen en el sueño salen con él.
    /// </summary>
    private void OnSceneTransition(GameManager.SceneLoadInfo info) {
        if (_applying || _session == null || !_session.CanSend || !InDream || _scene.Length == 0 || info == null ||
            !IsDreamVisualization(info.Visualization) || info.SceneName == _scene) {
            return;
        }

        // Solo al ganar: en un sueño con jefe y sin caer al vacío (la FSM que recoge al caballero que cae lo devuelve al
        // mundo) ni morir. Tras vencer al jefe, el juego saca al caballero con la salida de sueño de su FSM "Dream Nail"
        var executing = FsmExecutionStack.ExecutingFsm;
        var fell = executing != null && executing.GameObject != null &&
                   executing.GameObject.name.StartsWith("Dream Fall Catcher", StringComparison.Ordinal);
        if (!_bossSeen || fell) {
            Log.Info($"Saliendo del sueño {_scene} sin haber ganado: los compañeros siguen dentro");
            return;
        }

        // Morir en un sueño también despierta, pero cada uno por su cuenta
        var hero = HeroController.instance;
        if (hero == null || hero.cState.dead || hero.heroDeathPrefab != null && hero.heroDeathPrefab.activeSelf ||
            _revive != null && _revive.IsDowned) {
            return;
        }

        Log.Info($"Saliendo del sueño {_scene} hacia {info.SceneName}: los compañeros del sueño salen también");
        var writer = _session.Begin(DreamExit);
        writer.WriteUShort(_sceneHash);
        writer.WriteString(info.SceneName ?? string.Empty);
        writer.WriteString(info.EntryGateName ?? string.Empty);
        writer.WriteByte((byte) info.Visualization);
        writer.WriteFloat(info.EntryDelay);
        writer.WriteBool(info.PreventCameraFadeOut);
        writer.WriteBool(info.WaitForSceneTransitionCameraFade);
        _session.End();
    }

    private void OnDreamExit(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var target = reader.ReadString(128);
        var gate = reader.ReadString(128);
        var visualization = (GameManager.SceneLoadVisualizations) reader.ReadByte();
        var delay = reader.ReadFloat();
        var preventFade = reader.ReadBool();
        var waitFade = reader.ReadBool();
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (sceneHash != _sceneHash || !InDream || gm == null || hero == null || gm.IsInSceneTransition ||
            hero.cState.dead || _revive != null && _revive.IsDowned || target.Length == 0) {
            return;
        }

        Log.Info($"{from.Name} ganó el sueño y sale: se sale con él");
        _applying = true;
        try {
            if (!LeaveDreamLikeTheGame(hero)) {
                // Sin la salida del juego a mano: el mismo cambio de sala que el compañero
                hero.RelinquishControl();
                gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
                    SceneName = PlayerData.instance.GetString("dreamReturnScene") is { Length: > 0 } back ? back : target,
                    EntryGateName = gate,
                    EntryDelay = delay,
                    Visualization = visualization,
                    PreventCameraFadeOut = preventFade,
                    WaitForSceneTransitionCameraFade = waitFade,
                    AlwaysUnloadUnusedAssets = false
                });
            }
        } finally {
            _applying = false;
        }
    }

    /// <summary>
    /// Sale del sueño como lo hace el juego: el estado "Leave Dream" de la FSM "Dream Nail" del caballero marca la vuelta
    /// de sueño (al llegar, el caballero queda tendido y se levanta al pulsar, y el fundido blanco se quita), y cambia a
    /// la sala de vuelta de este jugador por su puerta. Con un cambio de sala hecho a mano, sin esa marca, la pantalla se
    /// quedaba en blanco y el caballero sin control.
    /// </summary>
    private static bool LeaveDreamLikeTheGame(HeroController hero) {
        var dreamNail = PlayMakerFSM.FindFsmOnGameObject(hero.gameObject, "Dream Nail");
        if (dreamNail == null || !HasState(dreamNail, LeaveDreamState)) {
            return false;
        }

        var fsm = dreamNail.Fsm;
        FsmExecutionStack.PushFsm(fsm);
        try {
            fsm.SetState(LeaveDreamState);
            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
        }

        return true;
    }

    private const string LeaveDreamState = "Leave Dream";

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() {
        var text = new System.Text.StringBuilder($"en sueño={InDream} jefe visto={_bossSeen} jefe vivo={EnemySync.BossInRoom} sala={_scene} entradas={_entries.Count}");
        foreach (var entry in _entries) {
            if (entry.Fsm != null) {
                text.Append($" | {entry.Fsm.gameObject.name}/{entry.Fsm.FsmName} {entry.LastState} (reposo {entry.RestState ?? "-"}) → " +
                            $"{entry.Transition.sceneName.Value}");
            }
        }

        return text.ToString();
    }

    /// <summary>Modo de prueba: hace que la primera entrada de la sala reciba el golpe del aguijón onírico.</summary>
    public bool TestDreamHit() {
        foreach (var entry in _entries) {
            if (entry.Fsm != null && entry.Fsm.gameObject.activeInHierarchy) {
                entry.Fsm.SendEvent("DREAM HIT");
                return true;
            }
        }

        return false;
    }
}
