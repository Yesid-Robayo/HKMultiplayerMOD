using System;
using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Objetos de sala con movimiento propio (ascensores, rejas de arena) que se mueven igual en todos los PCs.
/// <list type="bullet">
/// <item>Ascensores y rejas: manda el dueño de la sala. Su objeto es el único que se mueve por sí mismo; envía su
/// posición 30 veces por segundo y cada cambio de estado, y las copias de los demás los siguen (interpolados). Las
/// órdenes de quien no manda (golpear la palanca) van al dueño.</item>
/// <item>Palancas, interruptores y objetos guardables (paredes, puertas): su golpe lo detecta su propia FSM, así que se
/// copia la transición: cuando salen de su estado de reposo (por un golpe), los demás PCs hacen la misma transición si
/// están en ese mismo estado. Cada golpe se ve en todos con su animación.</item>
/// </list>
/// </summary>
internal sealed class FsmStateSync {
    public const MessageType EventFired = (MessageType) 50;
    public const MessageType OwnerState = (MessageType) 51;
    public const MessageType Transition = (MessageType) 53;
    public const MessageType Pose = (MessageType) 54;
    public const MessageType Positions = (MessageType) 55;

    /// <summary>Cada cuánto envía el dueño la posición y variables de un objeto quieto.</summary>
    private const float PoseInterval = 1f;

    /// <summary>Tiempo en un estado para considerar el objeto quieto (o de reposo, para las palancas).</summary>
    private const float StillSeconds = 1f;

    /// <summary>Partes del nombre de los objetos con golpe propio (palancas, interruptores).</summary>
    private static readonly string[] SwitchNames = { "Lever", "lever", "Switch", "switch", "Button" };


    /// <summary>Objetos sincronizados: principio del nombre del objeto y nombre de su FSM.</summary>
    private static readonly (string Name, string Fsm)[] Targets = {
        ("Ruins Lift", "Lift Control"),
        ("Battle Gate", "BG Control")
    };

    /// <summary>La misma orden en otro PC dentro de este margen se considera la misma (no se repite).</summary>
    private const float DuplicateWindow = 1f;


    private sealed class Tracked {
        public uint Id;
        public PlayMakerFSM Fsm;
        public string LastState;
        public string OwnerState;
        public float OwnerStateTime;
        public string LastLocalEvent;
        public float LastLocalEventTime = -10f;
        public string LastRemoteEvent;
        public float LastRemoteEventTime = -10f;
        public bool CopyTransitions;
        public float LastStateTime;
        public string RestState;
        public float NextPose;
        public bool LocalChain;
        public float RemoteChainUntil;
        public readonly SnapshotBuffer Positions = new();
        public float LastSentX = float.NaN;
        public float LastSentY = float.NaN;
        public uint LastSentTick;
    }

    private readonly List<Tracked> _tracked = new();
    private readonly Dictionary<uint, Tracked> _byId = new();
    private readonly Dictionary<Fsm, Tracked> _byFsm = new();
    private readonly MessageHandler _onEvent;
    private readonly MessageHandler _onOwnerState;
    private readonly MessageHandler _onTransition;
    private readonly MessageHandler _onPose;
    private readonly MessageHandler _onPositions;
    private uint _lastPositionTick;
    private PlayerSnapshot _incoming;
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;

    private static FsmStateSync _active;
    private static bool _hooked;
    private static bool _applyingRemote;

    static FsmStateSync() {
        // Las órdenes de quien no manda van al dueño: su objeto es el único que se mueve por sí mismo
        MessageRoutes.Register(EventFired, Route.SceneOwner, reliable: true);
        MessageRoutes.Register(Positions, Route.SameScene, reliable: false);
        MessageRoutes.Register(OwnerState, Route.SameScene, reliable: true);
        MessageRoutes.Register(Transition, Route.SameScene, reliable: true);
        MessageRoutes.Register(Pose, Route.SameScene, reliable: true);
    }

    public FsmStateSync() {
        _onEvent = OnEvent;
        _onOwnerState = OnOwnerState;
        _onTransition = OnTransition;
        _onPose = OnPose;
        _onPositions = OnPositions;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(EventFired, _onEvent);
        session.On(OwnerState, _onOwnerState);
        session.On(Transition, _onTransition);
        session.On(Pose, _onPose);
        session.On(Positions, _onPositions);
        _active = this;
        InstallHooks();
        Clear();
    }

    public void Detach() {
        _session?.On(EventFired, null);
        _session?.On(OwnerState, null);
        _session?.On(Transition, null);
        _session?.On(Pose, null);
        _session?.On(Positions, null);
        _session = null;
        if (_active == this) {
            _active = null;
        }

        Clear();
    }

    private void Clear() {
        _tracked.Clear();
        _byId.Clear();
        _byFsm.Clear();
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

        if (scene.Length == 0) {
            return;
        }

        if (_rescanAt > 0f && Time.unscaledTime >= _rescanAt) {
            _rescanAt = 0f;
            Scan();
        }

        var isOwner = _session.IsLocalSceneOwner;
        var now = Time.unscaledTime;
        for (var i = 0; i < _tracked.Count; i++) {
            var item = _tracked[i];
            if (item.Fsm == null) {
                continue;
            }

            var state = item.Fsm.ActiveStateName;
            if (item.CopyTransitions) {
                // Los cambios se envían desde el enganche de SwitchState (también los que duran menos de un frame);
                // aquí solo se averigua cuál es su estado de reposo
                if (state != item.LastState) {
                    item.LastState = state;
                    item.LastStateTime = now;
                } else if (now - item.LastStateTime >= StillSeconds) {
                    item.RestState = state;
                }

                continue;
            }

            // Los estados del dueño se envían desde el enganche de SwitchState (también los instantáneos, como la
            // llegada, que para las cadenas y la animación); aquí solo se mira si está quieto
            if (state != item.LastState) {
                item.LastState = state;
                item.LastStateTime = now;
            }

            if (isOwner) {
                if (now - item.LastStateTime >= StillSeconds && now >= item.NextPose && _session.CanSend) {
                    item.NextPose = now + PoseInterval;
                    SendPose(item);
                }

                continue;
            }

            // Marioneta: su posición es la del dueño, interpolada (después de que su FSM se haya movido este frame)
            if (item.Positions.Sample(Time.unscaledTimeAsDouble, out var pose)) {
                var transform = item.Fsm.transform;
                var position = transform.position;
                transform.position = new Vector3(pose.X, pose.Y, position.z);
            }
        }

        if (isOwner && _session.CanSend) {
            SendPositions();
        }
    }

    /// <summary>El dueño envía la posición de los objetos móviles 30 veces por segundo (los quietos, cada medio segundo).</summary>
    private void SendPositions() {
        var tick = (uint) (Time.unscaledTimeAsDouble * NetConstants.TickRate);
        if (tick / 2 == _lastPositionTick / 2) {
            return;
        }

        _lastPositionTick = tick;
        PacketWriter writer = null;
        var countPosition = 0;
        var count = 0;
        foreach (var item in _tracked) {
            if (item.CopyTransitions || item.Fsm == null) {
                continue;
            }

            var position = item.Fsm.transform.position;
            var moved = Mathf.Abs(position.x - item.LastSentX) > 0.001f || Mathf.Abs(position.y - item.LastSentY) > 0.001f;
            if (!moved && tick - item.LastSentTick < 30) {
                continue;
            }

            item.LastSentX = position.x;
            item.LastSentY = position.y;
            item.LastSentTick = tick;
            if (writer == null) {
                writer = _session.Begin(Positions);
                writer.WriteUShort(_sceneHash);
                writer.WriteUInt(tick);
                countPosition = writer.Position;
                writer.WriteByte(0);
            }

            writer.WriteUInt(item.Id);
            writer.WriteFloat(position.x);
            writer.WriteFloat(position.y);
            count++;
        }

        if (writer != null) {
            var end = writer.Position;
            writer.Seek(countPosition);
            writer.WriteByte((byte) count);
            writer.Seek(end);
            _session.End();
        }
    }

    private void OnPositions(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var tick = reader.ReadUInt();
        var count = reader.ReadByte();
        var time = Time.unscaledTimeAsDouble;
        var reliableTiming = Time.unscaledDeltaTime < 0.1f;
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            _incoming.Tick = tick;
            _incoming.SceneHash = sceneHash;
            _incoming.X = reader.ReadFloat();
            _incoming.Y = reader.ReadFloat();
            _incoming.Visible = true;
            if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var item) && !item.CopyTransitions) {
                item.Positions.Add(_incoming, time, reliableTiming);
            }
        }
    }

    private void Scan() {
        foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            var target = IsTarget(fsm);
            if (!target && !IsSwitchLike(fsm)) {
                continue;
            }

            var id = ScenePaths.Hash(fsm.transform) ^ (uint) fsm.FsmName.GetHashCode();
            if (_byId.ContainsKey(id)) {
                continue;
            }

            var item = new Tracked {
                Id = id, Fsm = fsm, LastState = fsm.ActiveStateName, CopyTransitions = !target,
                LastStateTime = Time.unscaledTime
            };
            item.RestState = item.LastState;
            _tracked.Add(item);
            _byId[id] = item;
            _byFsm[fsm.Fsm] = item;
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

    /// <summary>
    /// Objetos con golpe propio: palancas e interruptores (por su nombre) y la FSM guardable (variable "Activated") de
    /// los objetos guardables que no son de cada jugador.
    /// </summary>
    private static bool IsSwitchLike(PlayMakerFSM fsm) {
        var name = fsm.gameObject.name;
        foreach (var part in SwitchNames) {
            if (name.Contains(part)) {
                return true;
            }
        }

        var persistent = fsm.GetComponent<PersistentBoolItem>();
        if (persistent == null || fsm.FsmVariables.FindFsmBool("Activated") == null) {
            return false;
        }

        var id = persistent.persistentBoolData?.id;
        return !World.CharacterData.IsIndividualItem(string.IsNullOrEmpty(id) ? persistent.name : id);
    }

    /// <summary>Cada estado del ascensor o la reja del dueño, en el momento en que ocurre, a los demás.</summary>
    private void OnOwnerSwitch(Tracked item, string state) {
        item.LastState = state;
        item.LastStateTime = Time.unscaledTime;
        if (_session == null || !_session.CanSend || !_session.IsLocalSceneOwner) {
            return;
        }

        var writer = _session.Begin(OwnerState);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(item.Id);
        writer.WriteString(state);
        _session.End();
    }

    /// <summary>
    /// Un golpe local (salida del reposo) y toda su cadena hasta volver al reposo se copian, también las decisiones (p.
    /// ej. si el héroe estaba al alcance): el otro PC sigue la cadena paso a paso.
    /// </summary>
    private void OnLocalSwitch(Tracked item, string from, string state) {
        var now = Time.unscaledTime;
        item.LastState = state;
        item.LastStateTime = now;
        var send = false;
        if (now < item.RemoteChainUntil) {
            // Cadena que llegó de otro PC: no se devuelve
        } else if (from != null && from == item.RestState) {
            item.LocalChain = true;
            send = true;
        } else if (item.LocalChain) {
            send = true;
        }

        if (state == item.RestState) {
            item.LocalChain = false;
        }

        if (!send || _session == null || !_session.CanSend || from == null) {
            return;
        }

        var writer = _session.Begin(Transition);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(item.Id);
        writer.WriteString(from);
        writer.WriteString(state);
        _session.End();
    }

    private void OnTransition(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var fromState = reader.ReadString(128);
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null) {
            return;
        }

        // Se sigue la cadena del otro PC paso a paso (también si aquí ya volvió al reposo o va por otro paso)
        var current = item.Fsm.ActiveStateName;
        item.RemoteChainUntil = Time.unscaledTime + 3f;
        item.LocalChain = false;
        if (current == state) {
            return;
        }

        // Lo copiado (y lo que encadene en el acto) no se vuelve a enviar
        item.LastState = state;
        item.LastStateTime = Time.unscaledTime;
        _applyingRemote = true;
        try {
            item.Fsm.SetState(state);
        } finally {
            _applyingRemote = false;
        }
    }

    // =====================================================================================================
    // Órdenes (eventos globales de la FSM)
    // =====================================================================================================

    private static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        // Cada cambio de estado de las palancas y objetos guardables, en el momento en que ocurre
        On.HutongGames.PlayMaker.Fsm.SwitchState += (orig, self, toState) => {
            var sync = _active;
            if (sync != null && !_applyingRemote && toState != null && sync._byFsm.TryGetValue(self, out var item)) {
                if (item.CopyTransitions) {
                    sync.OnLocalSwitch(item, self.ActiveStateName, toState.Name);
                } else {
                    sync.OnOwnerSwitch(item, toState.Name);
                }
            }

            orig(self, toState);
        };

        // Las órdenes de otras FSM (palancas, llamadores) llegan al objeto por aquí, con quién las envió
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += (orig, self, fsmEvent, eventData) => {
            // La misma orden ya llegó de otro PC (p. ej. la copia del golpe a la palanca la vuelve a mandar): no se
            // aplica dos veces, que pararía o daría la vuelta al ascensor
            if (_active != null && !_active.OnLocalEvent(self, fsmEvent, eventData)) {
                return;
            }

            orig(self, fsmEvent, eventData);
        };
    }

    /// <summary>Orden local a una FSM vigilada: se reenvía. Devuelve false si es repetida y no debe aplicarse.</summary>
    private bool OnLocalEvent(Fsm fsm, FsmEvent fsmEvent, FsmEventData eventData) {
        if (_applyingRemote || fsmEvent == null || _session == null || !_session.CanSend ||
            !_byFsm.TryGetValue(fsm, out var item) || item.CopyTransitions) {
            return true;
        }

        // Solo las órdenes de otra FSM (palanca, llamador). Las internas del propio objeto (llegadas, hacia dónde ir
        // según dónde está el héroe local...) no: cada PC las decide por su cuenta. Quien envía queda apuntado en el
        // dato global del juego justo antes de entregarla
        var sender = eventData?.SentByFsm ?? Fsm.EventData?.SentByFsm;
        if (sender == null || sender == fsm || fsmEvent.Name == "FINISHED") {
            return true;
        }

        var now = Time.unscaledTime;
        var name = fsmEvent.Name;
        if (_session.IsLocalSceneOwner) {
            // La misma orden ya llegó de otro jugador hace un instante (p. ej. la copia del golpe a su palanca la vuelve
            // a mandar aquí): no se aplica dos veces
            if (name == item.LastRemoteEvent && now - item.LastRemoteEventTime < DuplicateWindow) {
                item.LastRemoteEvent = null;
                return false;
            }

            item.LastLocalEvent = name;
            item.LastLocalEventTime = now;
            return true;
        }

        // No manda aquí: la copia no se mueve por su cuenta; la orden va al dueño y su movimiento llega a todos
        var writer = _session.Begin(EventFired);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(item.Id);
        writer.WriteString(name);
        _session.End();
        return false;
    }

    private void OnEvent(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var name = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null) {
            return;
        }

        var now = Time.unscaledTime;
        if (!_session.IsLocalSceneOwner) {
            return;
        }

        // La misma orden ocurrió aquí también hace un instante (la copia del golpe a la palanca): ya está aplicada
        if (name == item.LastLocalEvent && now - item.LastLocalEventTime < DuplicateWindow) {
            item.LastLocalEvent = null;
            return;
        }

        item.LastRemoteEvent = name;
        item.LastRemoteEventTime = now;
        _applyingRemote = true;
        try {
            item.Fsm.SendEvent(name);
        } finally {
            _applyingRemote = false;
        }
    }

    /// <summary>Posición y variables del objeto quieto en el dueño (para que los demás estén en el mismo sitio).</summary>
    private void SendPose(Tracked item) {
        var position = item.Fsm.transform.position;
        var ints = item.Fsm.FsmVariables.IntVariables;
        var bools = item.Fsm.FsmVariables.BoolVariables;
        var writer = _session.Begin(Pose);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(item.Id);
        writer.WriteString(item.LastState ?? string.Empty);
        writer.WriteFloat(position.x);
        writer.WriteFloat(position.y);
        writer.WriteByte((byte) Math.Min(ints.Length, 32));
        for (var i = 0; i < ints.Length && i < 32; i++) {
            writer.WriteInt(ints[i].Value);
        }

        writer.WriteByte((byte) Math.Min(bools.Length, 32));
        for (var i = 0; i < bools.Length && i < 32; i++) {
            writer.WriteBool(bools[i].Value);
        }

        _session.End();
    }

    private void OnPose(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var state = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null) {
            return;
        }

        item.OwnerState = state;
        var ints = item.Fsm.FsmVariables.IntVariables;
        var bools = item.Fsm.FsmVariables.BoolVariables;
        // Solo se coloca si aquí también está quieto en el mismo estado (si se está moviendo, ya llegará)
        var still = item.Fsm.ActiveStateName == state && Time.unscaledTime - item.LastStateTime >= StillSeconds;
        var intCount = reader.ReadByte();
        for (var i = 0; i < intCount; i++) {
            var value = reader.ReadInt();
            if (still && i < ints.Length) {
                ints[i].Value = value;
            }
        }

        var boolCount = reader.ReadByte();
        for (var i = 0; i < boolCount; i++) {
            var value = reader.ReadBool();
            if (still && i < bools.Length) {
                bools[i].Value = value;
            }
        }

        if (!still) {
            return;
        }

    }

    private void OnOwnerState(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null) {
            return;
        }

        item.OwnerState = state;
        item.OwnerStateTime = Time.unscaledTime;
        if (item.Fsm.ActiveStateName != state && !_session.IsLocalSceneOwner) {
            item.LastState = state;
            item.LastStateTime = Time.unscaledTime;
            _applyingRemote = true;
            try {
                item.Fsm.SetState(state);
            } finally {
                _applyingRemote = false;
            }
        }
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

    /// <summary>Envía una orden a un objeto como si viniera de su palanca o botón en este PC.</summary>
    public bool TestSendEvent(string namePrefix, string eventName) {
        foreach (var item in _tracked) {
            if (item.Fsm != null && item.Fsm.gameObject.name.StartsWith(namePrefix, StringComparison.Ordinal)) {
                item.Fsm.SendEvent(eventName);
                return true;
            }
        }

        return false;
    }
}
