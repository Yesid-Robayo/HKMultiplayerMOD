using System;
using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Objetos de sala con movimiento propio y objetos que se golpean, igual en todos los PCs.
/// <list type="bullet">
/// <item>Ascensores y rejas de arena: cada uno tiene un controlador, el último jugador que lo usó (al principio, el
/// dueño de la sala). Su objeto es el único que se mueve por sí mismo, con su caballero encima; envía su posición 30
/// veces por segundo y cada cambio de estado en el momento en que ocurre, y las copias de los demás los siguen
/// (interpoladas). Quien lo usa pasa a ser el controlador y avisa a los demás.</item>
/// <item>Palancas, interruptores y objetos guardables (paredes, puertas): su golpe lo detecta su propia FSM, así que se
/// copia el golpe y toda su cadena hasta volver al reposo. Las órdenes que la copia de una palanca manda a un ascensor
/// no lo mueven: ya lo mueve quien golpeó de verdad.</item>
/// </list>
/// </summary>
internal sealed class FsmStateSync {
    public const MessageType ControllerState = (MessageType) 51;
    public const MessageType Transition = (MessageType) 53;
    public const MessageType Pose = (MessageType) 54;
    public const MessageType Positions = (MessageType) 55;
    public const MessageType Claim = (MessageType) 56;
    public const MessageType Aboard = (MessageType) 57;
    public const MessageType Order = (MessageType) 58;

    /// <summary>Cada cuánto envía el controlador la posición y variables de un objeto quieto.</summary>
    private const float PoseInterval = 1f;

    /// <summary>Tiempo en un estado para considerar el objeto quieto (o de reposo, para las palancas).</summary>
    private const float StillSeconds = 1f;

    /// <summary>Partes del nombre de los objetos con golpe propio (palancas, interruptores).</summary>
    private static readonly string[] SwitchNames = { "Lever", "lever", "Switch", "switch", "Button" };

    /// <summary>Objetos con controlador: principio del nombre del objeto y nombre de su FSM.</summary>
    private static readonly (string Name, string Fsm)[] Targets = {
        ("Ruins Lift", "Lift Control"),
        ("Battle Gate", "BG Control")
    };

    private sealed class Tracked {
        public uint Id;
        public PlayMakerFSM Fsm;
        public string LastState;
        public float LastStateTime;
        public bool CopyTransitions;

        // Palancas y guardables
        public string RestState;
        public bool LocalChain;
        public float RemoteChainUntil;

        // Ascensores y rejas: 0 = aún sin controlador conocido (manda el dueño de la sala)
        public ushort Controller;
        public float NextPose;
        public readonly SnapshotBuffer Positions = new();
        public float LastSentX = float.NaN;
        public float LastSentY = float.NaN;
        public uint LastSentTick;

        // Quién va subido (ascensores): la orden de moverlo la ejecuta alguien que vaya encima
        public Collider2D Platform;
        public bool LocalAboard;
        public readonly List<ushort> RemoteRiders = new();
    }

    private readonly List<Tracked> _tracked = new();
    private readonly Dictionary<uint, Tracked> _byId = new();
    private readonly Dictionary<Fsm, Tracked> _byFsm = new();
    private readonly MessageHandler _onState;
    private readonly MessageHandler _onTransition;
    private readonly MessageHandler _onPose;
    private readonly MessageHandler _onPositions;
    private readonly MessageHandler _onClaim;
    private readonly MessageHandler _onAboard;
    private readonly MessageHandler _onOrderMessage;
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
        MessageRoutes.Register(Positions, Route.SameScene, reliable: false);
        MessageRoutes.Register(ControllerState, Route.SameScene, reliable: true);
        MessageRoutes.Register(Transition, Route.SameScene, reliable: true);
        MessageRoutes.Register(Pose, Route.SameScene, reliable: true);
        MessageRoutes.Register(Claim, Route.SameScene, reliable: true);
        MessageRoutes.Register(Aboard, Route.SameScene, reliable: true);
        MessageRoutes.Register(Order, Route.SameScene, reliable: true);
    }

    public FsmStateSync() {
        _onState = OnControllerState;
        _onTransition = OnTransition;
        _onPose = OnPose;
        _onPositions = OnPositions;
        _onClaim = OnClaim;
        _onAboard = OnAboard;
        _onOrderMessage = OnOrderMessage;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(ControllerState, _onState);
        session.On(Transition, _onTransition);
        session.On(Pose, _onPose);
        session.On(Positions, _onPositions);
        session.On(Claim, _onClaim);
        session.On(Aboard, _onAboard);
        session.On(Order, _onOrderMessage);
        _active = this;
        InstallHooks();
        Clear();
    }

    public void Detach() {
        if (_session != null) {
            _session.On(ControllerState, null);
            _session.On(Transition, null);
            _session.On(Pose, null);
            _session.On(Positions, null);
            _session.On(Claim, null);
            _session.On(Aboard, null);
            _session.On(Order, null);
        }

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

    // =====================================================================================================
    // Controlador de ascensores y rejas
    // =====================================================================================================

    /// <summary>Si este PC mueve el objeto: es su controlador, o no hay uno presente en la sala y aquí manda la sala.</summary>
    private bool IsController(Tracked item) {
        if (item.Controller == _session.LocalId) {
            return true;
        }

        return !ControllerPresent(item) && _session.IsLocalSceneOwner;
    }

    private bool ControllerPresent(Tracked item) {
        if (item.Controller == 0) {
            return false;
        }

        foreach (var player in _session.Players) {
            if (player.Id == item.Controller) {
                return player.Scene == _scene;
            }
        }

        return false;
    }

    /// <summary>Este jugador usa el objeto: pasa a controlarlo (su caballero va encima) y se avisa a los demás.</summary>
    private void TakeControl(Tracked item) {
        if (item.Controller == _session.LocalId) {
            return;
        }

        item.Controller = _session.LocalId;
        item.Positions.Clear();
        if (_session.CanSend) {
            var writer = _session.Begin(Claim);
            writer.WriteUShort(_sceneHash);
            writer.WriteUInt(item.Id);
            _session.End();
        }
    }

    private void OnClaim(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var item)) {
            item.Controller = from.Id;
            item.Positions.Clear();
        }
    }

    /// <summary>Lo que envía otro jugador sobre un objeto solo vale si es quien lo controla (o si aún no se sabe).</summary>
    private bool AcceptFrom(Tracked item, SessionPlayer from) {
        if (IsController(item) && item.Controller == _session.LocalId) {
            return false;
        }

        if (item.Controller == 0 || !ControllerPresent(item)) {
            item.Controller = from.Id;
        }

        return item.Controller == from.Id;
    }

    // =====================================================================================================
    // Cada frame
    // =====================================================================================================

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

        var now = Time.unscaledTime;
        var anyControlled = false;
        for (var i = 0; i < _tracked.Count; i++) {
            var item = _tracked[i];
            if (item.Fsm == null) {
                continue;
            }

            var state = item.Fsm.ActiveStateName;
            if (state != item.LastState) {
                item.LastState = state;
                item.LastStateTime = now;
            } else if (item.CopyTransitions && now - item.LastStateTime >= StillSeconds) {
                item.RestState = state;
            }

            if (item.CopyTransitions) {
                continue;
            }

            UpdateAboard(item);
            if (IsController(item)) {
                anyControlled = true;
                if (now - item.LastStateTime >= StillSeconds && now >= item.NextPose && _session.CanSend) {
                    item.NextPose = now + PoseInterval;
                    SendPose(item);
                }

                continue;
            }

            // Copia: su posición es la del controlador, interpolada (después de que su FSM se haya movido este frame)
            if (item.Positions.Sample(Time.unscaledTimeAsDouble, out var pose)) {
                var transform = item.Fsm.transform;
                var position = transform.position;
                transform.position = new Vector3(pose.X, pose.Y, position.z);
            }
        }

        if (anyControlled && _session.CanSend) {
            SendPositions();
        }
    }

    /// <summary>Posición de los objetos que controla este PC, 30 veces por segundo (los quietos, cada medio segundo).</summary>
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
            if (item.CopyTransitions || item.Fsm == null || !IsController(item)) {
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
            if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var item) && !item.CopyTransitions &&
                AcceptFrom(item, from)) {
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
                LastStateTime = Time.unscaledTime, Platform = target ? FindPlatform(fsm) : null
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

    // =====================================================================================================
    // Estados
    // =====================================================================================================

    private static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        // Cada cambio de estado, en el momento en que ocurre (también los de menos de un frame)
        On.HutongGames.PlayMaker.Fsm.SwitchState += (orig, self, toState) => {
            var sync = _active;
            if (sync != null && !_applyingRemote && toState != null && sync._byFsm.TryGetValue(self, out var item)) {
                if (item.CopyTransitions) {
                    sync.OnLocalSwitch(item, self.ActiveStateName, toState.Name);
                } else {
                    sync.OnControlledSwitch(item, toState.Name);
                }
            }

            orig(self, toState);
        };

        // Las órdenes de otras FSM (palancas, llamadores) llegan al objeto por aquí, con quién las envió
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += (orig, self, fsmEvent, eventData) => {
            if (_active != null && !_active.OnOrder(self, fsmEvent, eventData)) {
                return;
            }

            orig(self, fsmEvent, eventData);
        };
    }

    /// <summary>
    /// Orden de otra FSM a un ascensor o reja. Si viene de la copia de un golpe de otro PC, no se aplica (ya lo mueve
    /// quien golpeó). Si es de este jugador, pasa a controlarlo. Devuelve false si no debe aplicarse.
    /// </summary>
    private bool OnOrder(Fsm fsm, FsmEvent fsmEvent, FsmEventData eventData) {
        if (_applyingRemote || fsmEvent == null || _session == null || !_byFsm.TryGetValue(fsm, out var item) ||
            item.CopyTransitions) {
            return true;
        }

        // Las internas del propio objeto (llegadas, hacia dónde ir...) siguen su curso
        var sender = eventData?.SentByFsm ?? Fsm.EventData?.SentByFsm;
        if (sender == null || sender == fsm || fsmEvent.Name == "FINISHED") {
            return true;
        }

        if (_byFsm.TryGetValue(sender, out var source) && source.CopyTransitions &&
            Time.unscaledTime < source.RemoteChainUntil) {
            return false;
        }

        // El caballero de este jugador no va subido pero el de otro sí: lo mueve el PC de quien va encima (aquí el
        // ascensor iría vacío y saltaría directamente al piso, dejándolo atrás)
        if (!item.LocalAboard && item.RemoteRiders.Count > 0 && _session.CanSend) {
            var writer = _session.Begin(Order);
            writer.WriteUShort(_sceneHash);
            writer.WriteUInt(item.Id);
            writer.WriteUShort(item.RemoteRiders[0]);
            writer.WriteString(fsmEvent.Name);
            _session.End();
            return false;
        }

        TakeControl(item);
        return true;
    }

    private void OnOrderMessage(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var target = reader.ReadUShort();
        var name = reader.ReadString(128);
        if (target != _session.LocalId || sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) ||
            item.Fsm == null) {
            return;
        }

        // La ejecuta este PC, con su caballero encima, y pasa a controlarlo
        TakeControl(item);
        _applyingRemote = true;
        try {
            item.Fsm.SendEvent(name);
        } finally {
            _applyingRemote = false;
        }
    }

    /// <summary>Colisionador sólido más ancho del ascensor (su plataforma).</summary>
    private static Collider2D FindPlatform(PlayMakerFSM fsm) {
        Collider2D best = null;
        foreach (var collider in fsm.GetComponentsInChildren<Collider2D>(true)) {
            if (!collider.isTrigger && (best == null || collider.bounds.size.x > best.bounds.size.x)) {
                best = collider;
            }
        }

        return best;
    }

    /// <summary>Si el caballero local va encima de la plataforma; cuando cambia, se avisa a los demás.</summary>
    private void UpdateAboard(Tracked item) {
        var hero = HeroController.instance;
        var aboard = false;
        if (hero != null && item.Platform != null) {
            var bounds = item.Platform.bounds;
            var position = hero.transform.position;
            aboard = position.x >= bounds.min.x - 0.3f && position.x <= bounds.max.x + 0.3f &&
                     position.y >= bounds.max.y - 1f && position.y <= bounds.max.y + 3.5f;
        }

        // Los que se fueron de la sala ya no van subidos
        for (var i = item.RemoteRiders.Count - 1; i >= 0; i--) {
            if (!InScene(item.RemoteRiders[i])) {
                item.RemoteRiders.RemoveAt(i);
            }
        }

        if (aboard == item.LocalAboard) {
            return;
        }

        item.LocalAboard = aboard;
        if (_session.CanSend) {
            var writer = _session.Begin(Aboard);
            writer.WriteUShort(_sceneHash);
            writer.WriteUInt(item.Id);
            writer.WriteBool(aboard);
            _session.End();
        }
    }

    private bool InScene(ushort playerId) {
        foreach (var player in _session.Players) {
            if (player.Id == playerId) {
                return player.Scene == _scene;
            }
        }

        return false;
    }

    private void OnAboard(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var aboard = reader.ReadBool();
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item)) {
            return;
        }

        item.RemoteRiders.Remove(from.Id);
        if (aboard) {
            item.RemoteRiders.Add(from.Id);
        }
    }

    /// <summary>Cada estado del objeto que controla este PC, en el momento en que ocurre, a los demás.</summary>
    private void OnControlledSwitch(Tracked item, string state) {
        item.LastState = state;
        item.LastStateTime = Time.unscaledTime;
        if (!_session.CanSend || !IsController(item)) {
            return;
        }

        var writer = _session.Begin(ControllerState);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(item.Id);
        writer.WriteString(state);
        _session.End();
    }

    private void OnControllerState(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null ||
            !AcceptFrom(item, from) || item.Fsm.ActiveStateName == state) {
            return;
        }

        // Se pone el mismo estado en la copia (luces, cadenas, sonido); su posición la pone el flujo del controlador
        item.LastState = state;
        item.LastStateTime = Time.unscaledTime;
        _applyingRemote = true;
        try {
            item.Fsm.SetState(state);
        } finally {
            _applyingRemote = false;
        }
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
        reader.ReadString(128);
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null) {
            return;
        }

        // Se sigue la cadena del otro PC paso a paso (también si aquí ya volvió al reposo o va por otro paso)
        item.RemoteChainUntil = Time.unscaledTime + 3f;
        item.LocalChain = false;
        if (item.Fsm.ActiveStateName == state) {
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

    /// <summary>Variables del objeto quieto en el controlador (piso actual...), para que el siguiente que lo use parta igual.</summary>
    private void SendPose(Tracked item) {
        var ints = item.Fsm.FsmVariables.IntVariables;
        var bools = item.Fsm.FsmVariables.BoolVariables;
        var writer = _session.Begin(Pose);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(item.Id);
        writer.WriteString(item.LastState ?? string.Empty);
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
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null ||
            !AcceptFrom(item, from)) {
            return;
        }

        // Solo si aquí también está quieto en el mismo estado (si se está moviendo, ya llegará)
        var still = item.Fsm.ActiveStateName == state && Time.unscaledTime - item.LastStateTime >= StillSeconds;
        var ints = item.Fsm.FsmVariables.IntVariables;
        var bools = item.Fsm.FsmVariables.BoolVariables;
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
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    public IEnumerable<string> Describe() {
        foreach (var item in _tracked) {
            if (item.Fsm != null) {
                var p = item.Fsm.transform.position;
                var control = item.CopyTransitions
                    ? string.Empty
                    : $" control={(IsController(item) ? "aquí" : item.Controller.ToString())} subido={(item.LocalAboard ? "sí" : "no")}" +
                      $" otros=[{string.Join(",", item.RemoteRiders)}]";
                yield return $"{item.Fsm.gameObject.name}/{item.Fsm.FsmName} id={item.Id:x8} estado={item.Fsm.ActiveStateName} " +
                             $"pos=({p.x:0.00},{p.y:0.00}){control}";
            }
        }
    }

    /// <summary>Envía una orden a un objeto como si viniera de su palanca o botón en este PC.</summary>
    public bool TestSendEvent(string namePrefix, string eventName) {
        foreach (var item in _tracked) {
            if (item.Fsm != null && item.Fsm.gameObject.name.StartsWith(namePrefix, StringComparison.Ordinal)) {
                TakeControl(item);
                item.Fsm.SendEvent(eventName);
                return true;
            }
        }

        return false;
    }
}
