using System;
using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Objetos de sala con movimiento propio (ascensores, rejas de arena) que se mueven igual en todos los PCs copiando el
/// estado de su FSM.
/// <list type="bullet">
/// <item>El dueño de la sala envía cada cambio de estado; los demás ponen el mismo estado si no están ya en él.</item>
/// <item>Si otro jugador lo activa (p. ej. llama al ascensor) mientras el objeto está quieto en el mismo estado que el
/// dueño, el cambio va al dueño, que lo aplica y lo reparte.</item>
/// </list>
/// Los cambios se vigilan cada frame comparando el estado actual (sin crear memoria).
/// </summary>
internal sealed class FsmStateSync {
    public const MessageType StateChanged = (MessageType) 50;
    public const MessageType StateRequest = (MessageType) 51;

    /// <summary>Objetos sincronizados: principio del nombre del objeto y nombre de su FSM.</summary>
    private static readonly (string Name, string Fsm)[] Targets = {
        ("Ruins Lift", "Lift Control"),
        ("Battle Gate", "BG Control")
    };

    /// <summary>Tiempo que el estado del dueño debe llevar quieto para aceptar que otro lo active.</summary>
    private const float StableSeconds = 0.5f;

    private sealed class Tracked {
        public uint Id;
        public PlayMakerFSM Fsm;
        public string LastState;
        public string OwnerState;
        public float OwnerStateTime;
    }

    private readonly List<Tracked> _tracked = new();
    private readonly Dictionary<uint, Tracked> _byId = new();
    private readonly MessageHandler _onChanged;
    private readonly MessageHandler _onRequest;
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;

    static FsmStateSync() {
        MessageRoutes.Register(StateChanged, Route.SameScene, reliable: true);
        MessageRoutes.Register(StateRequest, Route.SceneOwner, reliable: true);
    }

    public FsmStateSync() {
        _onChanged = OnChanged;
        _onRequest = OnRequest;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(StateChanged, _onChanged);
        session.On(StateRequest, _onRequest);
        Clear();
    }

    public void Detach() {
        _session?.On(StateChanged, null);
        _session?.On(StateRequest, null);
        _session = null;
        Clear();
    }

    private void Clear() {
        _tracked.Clear();
        _byId.Clear();
        _scene = string.Empty;
        _sceneHash = 0;
    }

    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            Clear();
            _scene = scene;
            _sceneHash = PlayerSnapshot.HashScene(scene);
            if (scene.Length == 0) {
                return;
            }

            Scan();
            _rescanAt = Time.unscaledTime + 1.5f;
            return;
        }

        if (scene.Length == 0 || _tracked.Count == 0 && _rescanAt <= 0f) {
            return;
        }

        if (_rescanAt > 0f && Time.unscaledTime >= _rescanAt) {
            _rescanAt = 0f;
            Scan();
        }

        var isOwner = _session.IsLocalSceneOwner;
        for (var i = 0; i < _tracked.Count; i++) {
            var item = _tracked[i];
            if (item.Fsm == null) {
                continue;
            }

            var state = item.Fsm.ActiveStateName;
            if (ReferenceEquals(state, item.LastState) || state == item.LastState) {
                continue;
            }

            var from = item.LastState;
            item.LastState = state;
            if (!_session.CanSend) {
                continue;
            }

            if (isOwner) {
                Send(StateChanged, item.Id, null, state);
            } else if (from != null && from == item.OwnerState &&
                       Time.unscaledTime - item.OwnerStateTime >= StableSeconds) {
                // Lo activó este jugador desde el estado quieto en que estaba también en el dueño
                Send(StateRequest, item.Id, from, state);
            }
        }
    }

    private void Send(MessageType type, uint id, string from, string state) {
        var writer = _session.Begin(type);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(id);
        if (type == StateRequest) {
            writer.WriteString(from);
        }

        writer.WriteString(state);
        _session.End();
    }

    private void Scan() {
        foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            if (!IsTarget(fsm)) {
                continue;
            }

            var id = ScenePaths.Hash(fsm.transform) ^ (uint) fsm.FsmName.GetHashCode();
            if (_byId.ContainsKey(id)) {
                continue;
            }

            var item = new Tracked { Id = id, Fsm = fsm, LastState = fsm.ActiveStateName };
            item.OwnerState = item.LastState;
            item.OwnerStateTime = Time.unscaledTime;
            _tracked.Add(item);
            _byId[id] = item;
        }
    }

    private static bool IsTarget(PlayMakerFSM fsm) {
        foreach (var (name, fsmName) in Targets) {
            if (fsm.FsmName == fsmName && fsm.gameObject.name.StartsWith(name, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    private void OnChanged(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null) {
            return;
        }

        item.OwnerState = state;
        item.OwnerStateTime = Time.unscaledTime;
        if (item.Fsm.ActiveStateName != state) {
            item.Fsm.SetState(state);
        }

        // Lo puesto desde fuera no se vuelve a enviar
        item.LastState = item.Fsm.ActiveStateName;
    }

    private void OnRequest(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var fromState = reader.ReadString(128);
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null ||
            !_session.IsLocalSceneOwner || item.Fsm.ActiveStateName != fromState) {
            return;
        }

        Log.Info($"{from.Name} activó {item.Fsm.gameObject.name} ({fromState} → {state})");
        // Se aplica aquí; el siguiente Update lo detecta como cambio local y lo reparte a todos
        item.Fsm.SetState(state);
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    public IEnumerable<string> Describe() {
        foreach (var item in _tracked) {
            if (item.Fsm != null) {
                var p = item.Fsm.transform.position;
                yield return $"{item.Fsm.gameObject.name}/{item.Fsm.FsmName} id={item.Id:x8} estado={item.Fsm.ActiveStateName} " +
                             $"pos=({p.x:0.00},{p.y:0.00})";
            }
        }
    }

    /// <summary>Pone un objeto en un estado como si lo hubiera hecho este jugador.</summary>
    public bool TestSetState(string namePrefix, string state) {
        foreach (var item in _tracked) {
            if (item.Fsm != null && item.Fsm.gameObject.name.StartsWith(namePrefix, StringComparison.Ordinal)) {
                item.Fsm.SetState(state);
                return true;
            }
        }

        return false;
    }
}
