using System;
using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Objetos de sala con movimiento propio y objetos que se golpean, igual en todos los PCs.
/// <list type="bullet">
/// <item>Ascensores y rejas de arena: cada uno tiene un controlador, el último jugador que lo usó (al principio, el
/// dueño de la sala). Su objeto es el único que se mueve por sí mismo, con su caballero encima; envía su posición 30
/// veces por segundo y cada cambio de estado en el momento en que ocurre, y las copias de los demás los siguen
/// (interpoladas). Las copias no avanzan solas: pasan solo por los estados que llegan, una vez cada uno (si no,
/// estados con efectos fuera del objeto, como el que voltea la palanca al llegar, se harían dos veces). Quien lo usa
/// pasa a ser el controlador y avisa a los demás.</item>
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
    public const MessageType Vars = (MessageType) 59;

    /// <summary>Un jugador terminó de leer el diálogo de un encuentro (al que lo controla).</summary>
    public const MessageType ConvoDone = (MessageType) 72;

    /// <summary>Evento con el que el juego avisa de que se cerró un diálogo.</summary>
    private const string ConvoFinish = "CONVO_FINISH";

    /// <summary>Lo más que se espera a que los demás terminen de leer (si alguno no responde, se sigue).</summary>
    private const float ConvoWaitSeconds = 30f;

    /// <summary>Máximo de variables de una FSM que se copian (los ascensores tienen unas pocas).</summary>
    private const int MaxVars = 32;

    /// <summary>Cada cuánto envía el controlador la posición y variables de un objeto quieto.</summary>
    private const float PoseInterval = 1f;

    /// <summary>Al entrar en la sala, lo que se espera a oír al controlador de un objeto antes de tomar su control.</summary>
    private const float ClaimGrace = 2.5f;

    /// <summary>Tiempo en un estado para considerar el objeto quieto (o de reposo, para las palancas).</summary>
    private const float StillSeconds = 1f;

    /// <summary>
    /// Posiciones por mensaje (12 bytes cada una). El Coliseo tiene más de cien objetos con controlador: cuando un PC
    /// los toma todos a la vez no caben en un mensaje y van en varios.
    /// </summary>
    private const int PositionsPerMessage = 64;

    /// <summary>Partes del nombre de los objetos con golpe propio (palancas, interruptores).</summary>
    private static readonly string[] SwitchNames = { "Lever", "lever", "Switch", "switch", "Button" };

    private const string GateFsm = "BG Control";

    /// <summary>
    /// Distancia a la reja (además de su grosor) a la que el caballero ya cruzó del todo: entonces vuelve a ser sólida sin
    /// empujarlo de vuelta.
    /// </summary>
    private const float GateClearance = 0.9f;

    private const string LiftFsm = "Lift Control";

    /// <summary>Cartel para desafiar a un jefe (Señores Mantis) y su estado de reposo.</summary>
    private const string ChallengeFsm = "Challenge Start";

    private const string ChallengeIdle = "Idle 2";

    /// <summary>
    /// Objetos con controlador: principio del nombre del objeto y nombre de su FSM. Además de ascensores y rejas, los
    /// controladores de batalla que no son enemigos (los tronos y la batalla de los Señores Mantis: quién acepta el
    /// desafío, cuándo baja la jaula, qué señores salen y la IA que coordina a la pareja; el Coliseo de los Tontos: su
    /// entrada, sus oleadas y las jaulas que sueltan enemigos, algunas con esperas al azar). Así el combate empieza y
    /// avanza igual en todos, y lo que decide al azar lo decide un solo PC.
    /// </summary>
    private static readonly (string Name, string Fsm)[] Targets = {
        ("Ruins Lift", LiftFsm),
        ("Battle Gate", GateFsm),
        ("Mantis Lord Throne", "Mantis Throne Main"),
        ("Mantis Lord Throne", "Mantis Throne Sub"),
        ("Mantis Battle", "Battle Control"),
        ("Battle Main", "Start"),
        ("Battle Sub", "Start"),
        ("Colosseum Manager", "Manager"),
        ("Colosseum Manager", "Battle Control"),
        ("Colosseum Cage", "Spawn"),
        ("Colosseum Cage Zote", "Control"),
        ("Hornet Outskirts Battle Encounter", "Encounter")
    };

    /// <summary>
    /// Encuentros con diálogo antes del combate (Hornet en los Bordes del Reino): los empieza quien entra en su zona,
    /// los dos lo ven (diálogos incluidos, con su caballero quieto mientras) y no siguen hasta que todos los de la sala
    /// terminan de leer. Si no, el combate empezaba mientras el otro leía y quedaba la Hornet que habla en su PC.
    /// </summary>
    private static readonly (string Name, string Fsm)[] Encounters = {
        ("Hornet Outskirts Battle Encounter", "Encounter")
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

        // Variables de la FSM ya enviadas (piso actual, siguiente...), para enviar solo cuando cambian
        public readonly int[] SentInts = new int[MaxVars];
        public readonly bool[] SentBools = new bool[MaxVars];
        public bool VarsSent;

        // La copia sigue al controlador solo cuando ya terminó de iniciarse (si no, se saltaría su preparación)
        public bool Ready;

        // Cambio de estado propio retenido por ser copia; si este PC toma el control en ese estado, se hace entonces
        public string HeldFrom;
        public string HeldTo;

        // Ascensores de la ciudad: sus pisos y el piso actual, para no mandarlo nunca fuera de su recorrido
        public RuinsLift Lift;
        public FsmInt CurrentPosition;

        // Rejas de batalla y de jefes: cerradas por otro, se dejan cruzar hacia dentro pero no hacia fuera
        public BoxCollider2D Gate;
        public int InsideSign;
        public bool Passing;

        // Se vio abierta (ya iniciada): solo entonces su cierre es de una batalla. Las que empiezan cerradas lo siguen
        public bool GateWasOpen;

        // Controlador de batalla (o lo que mueve con sus avisos): quien entra tarde se pone en su estado
        public bool Battle;

        // Combate de jefe que su zona quiso empezar sin estar todos dentro: el aviso, para darlo cuando entren
        public string PendingStart;
        public Collider2D Zone;
        public float NextWaitNotice;

        // Encuentro con diálogo: quién terminó de leer el de ahora (el controlador espera a todos)
        public bool Encounter;
        public bool LocalConvoDone;
        public readonly HashSet<ushort> ConvoDone = new();
        public bool WaitingConvo;
        public float WaitingSince;
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
    private readonly MessageHandler _onVars;
    private readonly MessageHandler _onConvoDone;
    private uint _lastPositionTick;
    private PlayerSnapshot _incoming;
    private GameSession _session;
    private PlayerSync _players;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;
    private float _scannedAt;

    private static FsmStateSync _active;
    private static bool _hooked;

    /// <summary>Objeto al que se está poniendo un estado llegado de otro PC (null si ninguno).</summary>
    private static Tracked _applying;

    /// <summary>Si ahora mismo se está poniendo un estado llegado de otro PC (lo que provoque no es de este PC).</summary>
    public static bool IsApplyingRemote => _applying != null;

    /// <summary>
    /// Si la FSM es de un objeto con controlador: sus estados llegan a todos los PCs, así que sus órdenes a un enemigo
    /// ya se dan en el PC que lo mueve (no hay que reenviárselas).
    /// </summary>
    public static bool IsSharedController(Fsm fsm) =>
        _active != null && fsm != null && _active._byFsm.TryGetValue(fsm, out var item) && !item.CopyTransitions;

    /// <summary>
    /// Si el directo de un guardable (su golpe y su animación) lo lleva la copia de su cadena, y si ahora mismo va a
    /// mitad de una (propia o llegada de otro PC: <paramref name="remote"/>). Mientras, nadie más debe moverle el estado.
    /// </summary>
    public static bool CopiesChain(PlayMakerFSM fsm, out bool busy, out bool remote) {
        busy = false;
        remote = false;
        if (_active == null || fsm == null || !_active._byFsm.TryGetValue(fsm.Fsm, out var item) || !item.CopyTransitions) {
            return false;
        }

        remote = Time.unscaledTime < item.RemoteChainUntil;
        busy = remote || item.LocalChain || item.Fsm.ActiveStateName != item.RestState;
        return true;
    }

    /// <summary>
    /// Se está poniendo un guardable en su estado de "ya activado" por lo que llegó de otro PC (lo que ve quien entra):
    /// no es un golpe de este PC y no se copia a los demás.
    /// </summary>
    internal static bool ApplyingSavedState;

    /// <summary>El primer cambio de estado al aplicar uno de otro PC es el pedido; los que encadene, se retienen.</summary>
    private static bool _requested;

    /// <summary>Se ejecuta una orden o un cambio retenido en este PC, que ya es el controlador: pasa sin más.</summary>
    private static bool _executing;

    /// <summary>Cambio de estado pendiente de una FSM (privado en PlayMaker): se anula al retener un cambio.</summary>
    private static readonly System.Reflection.FieldInfo SwitchToStateField =
        typeof(Fsm).GetField("switchToState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    static FsmStateSync() {
        MessageRoutes.Register(Positions, Route.SameScene, reliable: false);
        MessageRoutes.Register(ControllerState, Route.SameScene, reliable: true);
        MessageRoutes.Register(Transition, Route.SameScene, reliable: true);
        MessageRoutes.Register(Pose, Route.SameScene, reliable: true);
        MessageRoutes.Register(Claim, Route.SameScene, reliable: true);
        MessageRoutes.Register(Aboard, Route.SameScene, reliable: true);
        MessageRoutes.Register(Order, Route.SameScene, reliable: true);
        MessageRoutes.Register(Vars, Route.SameScene, reliable: true);
        MessageRoutes.Register(ConvoDone, Route.SameScene, reliable: true);
    }

    public FsmStateSync() {
        _onState = OnControllerState;
        _onTransition = OnTransition;
        _onPose = OnPose;
        _onPositions = OnPositions;
        _onClaim = OnClaim;
        _onAboard = OnAboard;
        _onOrderMessage = OnOrderMessage;
        _onVars = OnVars;
        _onConvoDone = OnConvoDone;
    }

    public void Attach(GameSession session, PlayerSync players) {
        _session = session;
        _players = players;
        session.On(ControllerState, _onState);
        session.On(Transition, _onTransition);
        session.On(Pose, _onPose);
        session.On(Positions, _onPositions);
        session.On(Claim, _onClaim);
        session.On(Aboard, _onAboard);
        session.On(Order, _onOrderMessage);
        session.On(Vars, _onVars);
        session.On(ConvoDone, _onConvoDone);
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
            _session.On(Vars, null);
            _session.On(ConvoDone, null);
        }

        // Las rejas que se dejaban cruzar vuelven a frenar
        foreach (var item in _tracked) {
            if (item.Gate != null) {
                RestoreGate(item);
            }
        }

        _session = null;
        _players = null;
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
    private void TakeControl(Tracked item, string reason) {
        if (item.Controller == _session.LocalId) {
            return;
        }

        Log.Info($"{item.Fsm.gameObject.name}/{item.Fsm.FsmName}: lo controla este PC (antes {item.Controller}; {reason})");
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
            Log.Info($"{item.Fsm.gameObject.name}/{item.Fsm.FsmName}: lo controla {from.Id}");
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
            _scannedAt = Time.unscaledTime;
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
                // Quieto: ese es su reposo y la cadena (si la había) terminó
                item.RestState = state;
                item.LocalChain = false;
            }

            if (item.WaitingConvo && (now - item.WaitingSince > ConvoWaitSeconds || AllConvoDone(item))) {
                ReleaseConvo(item);
            }

            UpdatePendingStart(item, now);

            if (item.CopyTransitions) {
                // La cadena de otro PC se cortó a medias (se fue de la sala): sigue sola desde donde quedó
                if (item.HeldTo != null && now >= item.RemoteChainUntil) {
                    ReplayHeld(item);
                }

                continue;
            }

            if (!item.Ready && now - item.LastStateTime >= StillSeconds) {
                item.Ready = true;
            }

            UpdateAboard(item);
            if (item.Gate != null) {
                UpdateGatePassage(item);
            }

            if (IsController(item)) {
                // Lo mueve este PC porque el controlador se fue (o nadie lo había usado): pasa a serlo de verdad. Así,
                // si vuelve (p. ej. tras morir), no manda con lo que cargó su sala; antes se espera un poco por si ya
                // hay quien lo controle aquí (al entrar, su pose llega en un segundo)
                if (item.Controller != _session.LocalId && now - _scannedAt >= ClaimGrace) {
                    TakeControl(item, "no está quien lo controlaba");
                }

                anyControlled = true;
                ReplayHeld(item);
                SendVarsIfChanged(item);
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
            if (writer == null || count == PositionsPerMessage) {
                if (writer != null) {
                    FinishPositions(writer, countPosition, count);
                }

                writer = _session.Begin(Positions);
                writer.WriteUShort(_sceneHash);
                writer.WriteUInt(tick);
                countPosition = writer.Position;
                writer.WriteByte(0);
                count = 0;
            }

            writer.WriteUInt(item.Id);
            writer.WriteFloat(position.x);
            writer.WriteFloat(position.y);
            count++;
        }

        if (writer != null) {
            FinishPositions(writer, countPosition, count);
        }
    }

    private void FinishPositions(PacketWriter writer, int countPosition, int count) {
        var end = writer.Position;
        writer.Seek(countPosition);
        writer.WriteByte((byte) count);
        writer.Seek(end);
        _session.End();
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
            var target = IsTarget(fsm) || IsEncounter(fsm);
            if (!target && !IsSwitchLike(fsm)) {
                continue;
            }

            var id = FsmId(fsm);
            if (_byId.ContainsKey(id) || _byFsm.ContainsKey(fsm.Fsm)) {
                continue;
            }

            var item = new Tracked {
                Id = id, Fsm = fsm, LastState = fsm.ActiveStateName, CopyTransitions = !target,
                LastStateTime = Time.unscaledTime, Platform = target && fsm.FsmName is LiftFsm or GateFsm ? FindPlatform(fsm) : null,
                Lift = target ? fsm.GetComponent<RuinsLift>() : null,
                CurrentPosition = target ? fsm.FsmVariables.FindFsmInt("Current Position") : null,
                Gate = target && fsm.FsmName == GateFsm ? fsm.GetComponent<BoxCollider2D>() : null,
                Battle = target && fsm.FsmName != LiftFsm && fsm.FsmName != GateFsm,
                Encounter = target && IsEncounter(fsm)
            };
            if (item.Encounter) {
                Log.Info($"{fsm.gameObject.name}/{fsm.FsmName}: encuentro con diálogo (los dos lo ven y esperan a leerlo)");
            }

            Add(item);
        }

        AddBattleListeners();
    }

    private void Add(Tracked item) {
        // Un guardable recién cargado aún se está iniciando (p. ej. "Pause" → "Idle"): su reposo se toma cuando lleva un
        // rato quieto; si no, el fin de su inicio parecía un golpe y se copiaba a los demás al entrar en la sala
        item.RestState = item.CopyTransitions ? null : item.LastState;
        _tracked.Add(item);
        _byId[item.Id] = item;
        _byFsm[item.Fsm.Fsm] = item;
    }

    /// <summary>
    /// Lo que mueven los controladores de batalla con avisos a toda la sala (la jaula, el suelo que se retira dejando
    /// pinchos y las puertas de los Señores Mantis) va igual que ellos: lo lleva un PC y los demás siguen sus estados.
    /// Así quien entra a mitad del combate también lo ve como está. Sin enemigos ni el caballero.
    /// </summary>
    private void AddBattleListeners() {
        HashSet<string> events = null;
        foreach (var item in _tracked) {
            if (!item.Battle || item.Fsm == null) {
                continue;
            }

            foreach (var state in item.Fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is HutongGames.PlayMaker.Actions.SendEventByName send &&
                        send.eventTarget.target == FsmEventTarget.EventTarget.BroadcastAll &&
                        !string.IsNullOrEmpty(send.sendEvent?.Value)) {
                        (events ??= new HashSet<string>()).Add(send.sendEvent.Value);
                    }
                }
            }
        }

        if (events == null) {
            return;
        }

        var hero = HeroController.instance;
        foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            if (_byFsm.ContainsKey(fsm.Fsm) || fsm.gameObject.scene.buildIndex < 0 ||
                EnemySync.IsEnemyObject(fsm.transform) ||
                hero != null && fsm.transform.IsChildOf(hero.transform) || !Listens(fsm, events)) {
                continue;
            }

            var id = FsmId(fsm);
            if (_byId.ContainsKey(id)) {
                continue;
            }

            Add(new Tracked {
                Id = id, Fsm = fsm, LastState = fsm.ActiveStateName, LastStateTime = Time.unscaledTime, Battle = true
            });
        }
    }

    /// <summary>Si la FSM cambia de estado con alguno de esos avisos.</summary>
    private static bool Listens(PlayMakerFSM fsm, HashSet<string> events) {
        foreach (var transition in fsm.FsmGlobalTransitions) {
            if (events.Contains(transition.EventName)) {
                return true;
            }
        }

        foreach (var state in fsm.FsmStates) {
            foreach (var transition in state.Transitions) {
                if (events.Contains(transition.EventName)) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsEncounter(PlayMakerFSM fsm) {
        foreach (var (name, fsmName) in Encounters) {
            if (fsm.FsmName == fsmName && fsm.gameObject.name.StartsWith(name, StringComparison.Ordinal)) {
                return true;
            }
        }

        return HasBattleDialogue(fsm);
    }

    /// <summary>Avisos con los que un encuentro da paso al combate.</summary>
    private static readonly HashSet<string> BattleStartEvents = new() { "BATTLE START", "WAKE", "BG CLOSE" };

    /// <summary>
    /// Encuentro con diálogo antes de un combate, sea cual sea el jefe: un objeto de la sala que no es enemigo, con un
    /// diálogo, que después despierta o enciende a un enemigo o cierra la arena. Los diálogos normales (personajes que
    /// solo hablan) no lo cumplen y siguen siendo de cada jugador.
    /// </summary>
    private static bool HasBattleDialogue(PlayMakerFSM fsm) {
        if (EnemySync.IsEnemyObject(fsm.transform)) {
            return false;
        }

        bool dialogue = false, battle = false;
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                switch (action) {
                    case HutongGames.PlayMaker.Actions.CallMethodProper call:
                        dialogue |= call.methodName?.Value == "StartConversation";
                        break;
                    case HutongGames.PlayMaker.Actions.SendEventByName send:
                        if (BattleStartEvents.Contains(send.sendEvent?.Value ?? string.Empty)) {
                            battle = true;
                        } else if (send.eventTarget.target is FsmEventTarget.EventTarget.GameObject
                                   or FsmEventTarget.EventTarget.GameObjectFSM) {
                            var receiver = fsm.Fsm.GetOwnerDefaultTarget(send.eventTarget.gameObject);
                            battle |= receiver != null && EnemySync.IsEnemyObject(receiver.transform);
                        }

                        break;
                    case HutongGames.PlayMaker.Actions.ActivateGameObject activate:
                        var target = fsm.Fsm.GetOwnerDefaultTarget(activate.gameObject);
                        battle |= activate.activate.Value && target != null &&
                                  target.GetComponentsInChildren<HealthManager>(true).Length > 0;
                        break;
                }
            }
        }

        return dialogue && battle;
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
        // Lo que recoge cada jugador por su cuenta nunca se copia, se llame como se llame
        var persistent = fsm.GetComponent<PersistentBoolItem>();
        var id = persistent != null ? persistent.persistentBoolData?.id : null;
        if (persistent != null && World.CharacterData.IsIndividualItem(string.IsNullOrEmpty(id) ? persistent.name : id)) {
            return false;
        }

        var name = fsm.gameObject.name;
        if (IsMimicBottle(fsm)) {
            return true;
        }

        foreach (var part in SwitchNames) {
            if (name.Contains(part)) {
                return true;
            }
        }

        return persistent != null && fsm.FsmVariables.FindFsmBool("Activated") != null || IsBreakableTemplate(fsm);
    }

    /// <summary>
    /// Tarro de una larva falsa (la oleada del Grub Mimic del Coliseo de Plata): se rompe con el aguijón de quien lo
    /// golpea y suelta al enemigo. El del Coliseo lo crea su jaula y no es guardable, así que no entra como los demás
    /// tarros: sin copiar su cadena, en los otros PCs el tarro seguía entero con el enemigo ya fuera.
    /// </summary>
    private static bool IsMimicBottle(PlayMakerFSM fsm) =>
        fsm.FsmName == "Bottle Control" && fsm.gameObject.name.StartsWith("Grub Mimic Bottle", StringComparison.Ordinal);

    /// <summary>
    /// Identificador de la FSM igual en todos los PCs: por su ruta en la sala. Los clonados en la raíz (lo que crean las
    /// jaulas del Coliseo) se llaman igual y tienen el mismo puesto entre hermanos: van por su sitio, como los enemigos.
    /// </summary>
    private static uint FsmId(PlayMakerFSM fsm) {
        var transform = fsm.transform;
        var cloned = transform.parent == null && transform.name.EndsWith("(Clone)", StringComparison.Ordinal);
        return (cloned ? EnemySync.PlacedId(transform) : ScenePaths.Hash(transform)) ^ (uint) fsm.FsmName.GetHashCode();
    }

    /// <summary>
    /// La FSM genérica de rompible del juego (recibe "TAKE DAMAGE", tiene "Break" y la variable "Activated"), usada en
    /// objetos que no son componentes rompibles ni guardables: el letrero de Cruces Olvidados, decorados... Se copia su
    /// cadena como la de una palanca. Fuera los enemigos y las piedras de geo (van por su cuenta).
    /// </summary>
    internal static bool IsBreakableTemplate(PlayMakerFSM fsm) {
        if (fsm.FsmVariables.FindFsmBool("Activated") == null || fsm.GetComponent<GeoRock>() != null ||
            EnemySync.IsEnemyObject(fsm.transform)) {
            return false;
        }

        var takesDamage = false;
        var breaks = false;
        foreach (var state in fsm.FsmStates) {
            breaks |= state.Name == "Break";
            foreach (var transition in state.Transitions) {
                takesDamage |= transition.EventName == "TAKE DAMAGE";
            }
        }

        return takesDamage && breaks;
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
            if (sync != null && toState != null && sync._byFsm.TryGetValue(self, out var item)) {
                if (sync.HoldSwitch(item, toState)) {
                    // Se anula el cambio pendiente: la FSM sigue en su estado hasta que llegue el siguiente
                    SwitchToStateField.SetValue(self, null);
                    return;
                }

                // Lo retenido era del estado que se deja
                item.HeldTo = null;
                if (_applying == null) {
                    if (item.CopyTransitions) {
                        sync.OnLocalSwitch(item, self.ActiveStateName, toState.Name);
                    } else {
                        sync.OnControlledSwitch(item, toState.Name);
                    }
                }
            }

            orig(self, toState);
        };

        // Todos los eventos que recibe el objeto (los suyos y las órdenes de palancas y llamadores) pasan por aquí
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += (orig, self, fsmEvent, eventData) => {
            if (_active != null && !_active.OnEvent(self, ref fsmEvent, eventData)) {
                return;
            }

            orig(self, fsmEvent, eventData);
        };
    }

    /// <summary>
    /// Si un cambio de estado se retiene en vez de hacerse (se guarda por si este PC toma el control en ese estado):
    /// <list type="bullet">
    /// <item>Objeto al que se pone un estado de otro PC: lo que encadene por sí mismo, porque el siguiente estado
    /// también llega de allí (si no, cada paso se haría dos veces; p. ej. el ascensor voltearía la palanca dos veces al
    /// llegar).</item>
    /// <item>Copia de un ascensor o reja: no avanza sola, solo pasa por los estados del controlador.</item>
    /// </list>
    /// </summary>
    private bool HoldSwitch(Tracked item, FsmState toState) {
        if (_executing) {
            return false;
        }

        if (_applying == item) {
            if (_requested) {
                _requested = false;
                return false;
            }
        } else if (item.CopyTransitions || !item.Ready || IsController(item)) {
            return false;
        }

        item.HeldFrom = item.Fsm.ActiveStateName;
        item.HeldTo = toState.Name;
        return true;
    }

    /// <summary>
    /// Pone un estado llegado de otro PC como lo haría la propia FSM: dentro de su ejecución, así una transición que
    /// pidan sus acciones corta las acciones siguientes (igual que en el PC de origen) y se retiene.
    /// </summary>
    private static void ApplyRemoteState(Tracked item, string state) {
        if (item.Fsm.FsmName != LiftFsm && item.Fsm.FsmName != GateFsm) {
            CloseChallengePrompts();
        }

        var fsm = item.Fsm.Fsm;
        PrepareCopy(item, state);
        _applying = item;
        _requested = true;
        FsmExecutionStack.PushFsm(fsm);
        try {
            fsm.SetState(state);
            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
            _applying = null;
            _requested = false;
            foreach (var action in DisabledActions) {
                action.Enabled = true;
            }

            DisabledActions.Clear();
        }
    }

    /// <summary>
    /// Un combate de batalla empezó en otro PC: el cartel para desafiar (Señores Mantis) se apaga también aquí, como
    /// hace el juego en el PC que lo aceptó (le quita la colisión y esconde la flecha). Si no, este jugador podría
    /// desafiar otra vez a mitad del combate y su caballero se quedaría sin control.
    /// </summary>
    private static void CloseChallengePrompts() {
        foreach (var fsm in PlayMakerFSM.FsmList) {
            if (fsm == null || fsm.FsmName != ChallengeFsm) {
                continue;
            }

            var collider = fsm.GetComponent<Collider2D>();
            if (collider == null || !collider.enabled) {
                continue;
            }

            collider.enabled = false;
            var arrow = fsm.FsmVariables.FindFsmGameObject("Prompt")?.Value;
            var arrowFsm = arrow != null ? arrow.GetComponent<PlayMakerFSM>() : null;
            if (arrowFsm != null) {
                arrowFsm.SendEvent("DOWN");
            }

            // De vuelta al reposo (si el caballero estaba al lado, su FSM esperaba la tecla para desafiar)
            if (fsm.ActiveStateName != ChallengeIdle && Array.Exists(fsm.FsmStates, state => state.Name == ChallengeIdle)) {
                fsm.SetState(ChallengeIdle);
            }

            Log.Info($"{fsm.gameObject.name}: el combate empezó en otro PC, ya no se puede desafiar aquí");
        }
    }

    /// <summary>Acciones que cambian los datos del jugador: en una copia no se hacen (llegan del PC de origen).</summary>
    private static readonly HashSet<string> PlayerDataActions = new() {
        "IncrementPlayerDataInt", "DecrementPlayerDataInt", "SetPlayerDataBool", "SetPlayerDataInt", "SetPlayerDataFloat",
        "SetPlayerDataString", "PlayerDataIntAdd"
    };

    private static readonly List<FsmStateAction> DisabledActions = new();

    /// <summary>
    /// Antes de copiar un estado de otro PC: sus acciones que cambian los datos del jugador no se hacen aquí (contadores
    /// como las larvas se sumarían dos veces; ya llegan por el progreso compartido). Y el tarro de larva se rompe aunque
    /// el caballero local no esté cerca (si no, su comprobación de alcance cortaría la rotura y la larva no saldría).
    /// </summary>
    private static void PrepareCopy(Tracked item, string state) {
        if (item.Fsm.FsmName == "Bottle Control" && state == "Shatter") {
            var heroRange = item.Fsm.FsmVariables.FindFsmBool("Hero Range");
            if (heroRange != null) {
                heroRange.Value = true;
            }
        }

        foreach (var fsmState in item.Fsm.FsmStates) {
            if (fsmState.Name != state) {
                continue;
            }

            foreach (var action in fsmState.Actions) {
                if (action.Enabled && (PlayerDataActions.Contains(action.GetType().Name) || !item.Encounter && TouchesHero(action))) {
                    action.Enabled = false;
                    DisabledActions.Add(action);
                }
            }
        }
    }

    /// <summary>
    /// Llamadas al caballero (devolverle el control, girarlo): en el PC de origen son para quien aceptó el desafío; aquí
    /// el caballero no lo había perdido.
    /// </summary>
    private static bool TouchesHero(FsmStateAction action) {
        var hero = HeroController.instance;
        if (hero == null) {
            return false;
        }

        // El punto de reaparición tras los pinchos (el Coliseo lo pone en una plataforma antes de sacarlos) es para el
        // caballero de cada PC: si no, el de la copia reaparecía en la entrada de la sala
        if (action is HutongGames.PlayMaker.Actions.CallMethodProper { methodName.Value: "SetHazardRespawn" }) {
            return false;
        }

        var target = action switch {
            HutongGames.PlayMaker.Actions.CallMethodProper call => action.Fsm.GetOwnerDefaultTarget(call.gameObject),
            HutongGames.PlayMaker.Actions.SendMessage message => action.Fsm.GetOwnerDefaultTarget(message.gameObject),
            _ => null
        };
        return target != null && target == hero.gameObject;
    }

    /// <summary>
    /// Evento que va a procesar la FSM de un objeto seguido. Devuelve false si no debe aplicarse: las órdenes que
    /// manda a un ascensor o reja otro objeto copiado de otro PC (ya lo mueve quien lo hizo). Las de este jugador hacen
    /// que pase a controlarlo (o las ejecuta quien va subido).
    /// </summary>
    private bool OnEvent(Fsm fsm, ref FsmEvent fsmEvent, FsmEventData eventData) {
        if (fsmEvent == null || _session == null || !_byFsm.TryGetValue(fsm, out var item)) {
            return true;
        }

        if (!_executing && _applying == null && WaitForEveryone(item, fsm, fsmEvent, eventData)) {
            return false;
        }

        if (item.CopyTransitions) {
            return true;
        }

        if (_executing) {
            fsmEvent = CheckLiftDirection(item, fsmEvent, null);
            return true;
        }

        if (_applying != null) {
            return _applying == item;
        }

        var sender = eventData?.SentByFsm ?? Fsm.EventData?.SentByFsm;
        if (item.Encounter) {
            if (fsmEvent.Name == ConvoFinish) {
                return OnConvoFinish(item);
            }

            // Su caballero entró en la zona del encuentro (el aviso de la zona no lleva remitente): lo empieza este PC
            if ((sender == null || sender == fsm) && fsmEvent.Name != "FINISHED" && Handles(fsm, fsmEvent.Name) &&
                !IsController(item)) {
                TakeControl(item, $"{fsmEvent.Name}: su caballero empieza el encuentro");
                return true;
            }
        }

        // Los propios del objeto (llegadas, esperas...): si es una copia, el cambio de estado que pidan se retiene
        if (sender == null || sender == fsm || fsmEvent.Name == "FINISHED") {
            return true;
        }

        // Los avisos a todos que no le hacen nada (el título de la zona, la curación del caballero...) no son usarlo
        if (!Handles(fsm, fsmEvent.Name)) {
            return true;
        }

        if (_byFsm.TryGetValue(sender, out var source) && source.CopyTransitions &&
            Time.unscaledTime < source.RemoteChainUntil) {
            return false;
        }

        // Aviso de una copia que sigue los estados de otro PC (p. ej. el SPAWN con retraso que la batalla del Coliseo
        // manda a sus jaulas, que llega fuera de la aplicación del estado): el controlador ya lo manda allí. Si se
        // tomara el control aquí, los dos PCs se quitaban las jaulas y alguna no soltaba su enemigo (la oleada no acababa)
        if (source != null && !source.CopyTransitions && !IsController(source)) {
            return false;
        }

        fsmEvent = CheckLiftDirection(item, fsmEvent, sender);

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

        TakeControl(item, $"{fsmEvent.Name} de {sender.GameObjectName}/{sender.Name}");
        return true;
    }

    /// <summary>Si el evento cambia el estado de la FSM desde donde está (transición del estado o global).</summary>
    private static bool Handles(Fsm fsm, string eventName) {
        foreach (var transition in fsm.GlobalTransitions) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        var state = fsm.ActiveState;
        if (state == null) {
            return false;
        }

        foreach (var transition in state.Transitions) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Este jugador cerró el diálogo de un encuentro. El que lo controla solo sigue cuando todos los de la sala lo
    /// cerraron; una copia avisa al controlador y espera a que llegue el siguiente estado.
    /// </summary>
    private bool OnConvoFinish(Tracked item) {
        if (!IsController(item)) {
            if (_session.CanSend) {
                var writer = _session.Begin(ConvoDone);
                writer.WriteUShort(_sceneHash);
                writer.WriteUInt(item.Id);
                writer.WriteString(item.Fsm.ActiveStateName);
                _session.End();
            }

            return false;
        }

        item.LocalConvoDone = true;
        if (AllConvoDone(item)) {
            ResetConvo(item);
            return true;
        }

        item.WaitingConvo = true;
        item.WaitingSince = Time.unscaledTime;
        Log.Info($"{item.Fsm.gameObject.name}: se espera a que los demás terminen de leer");
        return false;
    }

    private void OnConvoDone(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null || !item.Encounter ||
            item.Fsm.ActiveStateName != state) {
            return;
        }

        item.ConvoDone.Add(from.Id);
        if (item.WaitingConvo && IsController(item) && AllConvoDone(item)) {
            ReleaseConvo(item);
        }
    }

    /// <summary>Si este jugador y todos los demás de la sala terminaron de leer.</summary>
    private bool AllConvoDone(Tracked item) {
        if (!item.LocalConvoDone) {
            return false;
        }

        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene && !item.ConvoDone.Contains(player.Id)) {
                return false;
            }
        }

        return true;
    }

    private static void ResetConvo(Tracked item) {
        item.LocalConvoDone = false;
        item.ConvoDone.Clear();
        item.WaitingConvo = false;
    }

    /// <summary>Todos terminaron de leer (o se acabó la espera): el encuentro sigue.</summary>
    private static void ReleaseConvo(Tracked item) {
        ResetConvo(item);
        _executing = true;
        try {
            item.Fsm.SendEvent(ConvoFinish);
        } finally {
            _executing = false;
        }
    }

    /// <summary>
    /// La zona que empieza el combate de un jefe (la que cierra la arena o empieza el diálogo) avisa de que entró el
    /// caballero de este PC: si algún otro jugador de la sala no está dentro, no empieza; se espera a que entren todos.
    /// Solo con un jefe en la sala (las arenas normales empiezan como siempre).
    /// </summary>
    private bool WaitForEveryone(Tracked item, Fsm fsm, FsmEvent fsmEvent, FsmEventData eventData) {
        // Ya esperando: las zonas que avisan mientras el caballero sigue dentro tampoco lo empiezan
        if (item.PendingStart != null) {
            return fsmEvent.Name == item.PendingStart;
        }

        // El tarro del Grub Mimic se rompe con un golpe, no es una zona: no espera a nadie
        if (fsmEvent.Name == "FINISHED" || fsmEvent.Name == ConvoFinish || IsMimicBottle(item.Fsm) ||
            !(item.Encounter || item.CopyTransitions && item.Fsm.FsmVariables.FindFsmBool("Activated") != null)) {
            return false;
        }

        // El aviso de una zona no lleva remitente; y solo cuenta si empieza algo desde su reposo
        var sender = eventData?.SentByFsm ?? Fsm.EventData?.SentByFsm;
        if (sender != null && sender != fsm || !Handles(fsm, fsmEvent.Name) || !EnemySync.BossInRoom) {
            return false;
        }

        item.Zone ??= item.Fsm.GetComponent<Collider2D>();
        if (item.Zone == null || EveryoneInside(item)) {
            return false;
        }

        item.PendingStart = fsmEvent.Name;
        Log.Info($"{item.Fsm.gameObject.name}: el combate espera a que entren todos");
        return true;
    }

    /// <summary>Si los caballeros de todos los jugadores de la sala están dentro de la zona del combate.</summary>
    private bool EveryoneInside(Tracked item) {
        var bounds = item.Zone.bounds;
        bounds.Expand(new Vector3(0.5f, 0.5f, 1000f));
        var hero = HeroController.instance;
        if (hero == null || !bounds.Contains(hero.transform.position)) {
            return false;
        }

        foreach (var player in _session.Players) {
            if (player.IsLocal || player.Scene != _scene) {
                continue;
            }

            if (!_players.TryGetAvatarObject(player.Id, out var avatar) || !bounds.Contains(avatar.transform.position)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Combate retenido: empieza cuando están todos dentro; si este caballero sale, se olvida (al volver a
    /// entrar la zona avisa otra vez).</summary>
    private void UpdatePendingStart(Tracked item, float now) {
        if (item.PendingStart == null || item.Zone == null) {
            return;
        }

        var hero = HeroController.instance;
        var bounds = item.Zone.bounds;
        bounds.Expand(new Vector3(1.5f, 1.5f, 1000f));
        if (hero == null || !bounds.Contains(hero.transform.position)) {
            item.PendingStart = null;
            return;
        }

        if (!EveryoneInside(item)) {
            if (now >= item.NextWaitNotice) {
                item.NextWaitNotice = now + 4f;
                HKMultiplayer.UI.Notice.Show($"Esperando a {MissingNames(item)} para empezar el combate", 3.5f);
            }

            return;
        }

        var start = item.PendingStart;
        item.PendingStart = null;
        _executing = true;
        try {
            item.Fsm.SendEvent(start);
        } finally {
            _executing = false;
        }
    }

    private string MissingNames(Tracked item) {
        var bounds = item.Zone.bounds;
        bounds.Expand(new Vector3(0.5f, 0.5f, 1000f));
        var names = new List<string>();
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene &&
                (!_players.TryGetAvatarObject(player.Id, out var avatar) || !bounds.Contains(avatar.transform.position))) {
                names.Add(player.Name);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : "los demás";
    }

    /// <summary>Hace el cambio de estado retenido si el objeto sigue en el estado desde el que se pidió.</summary>
    private static void ReplayHeld(Tracked item) {
        var to = item.HeldTo;
        if (to == null) {
            return;
        }

        item.HeldTo = null;
        if (item.Fsm.ActiveStateName != item.HeldFrom) {
            return;
        }

        var fsm = item.Fsm.Fsm;
        _executing = true;
        FsmExecutionStack.PushFsm(fsm);
        try {
            fsm.SetState(to);
            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
            _executing = false;
        }
    }

    /// <summary>
    /// Bajar suma un piso y subir lo resta. Fuera del recorrido el juego lo convierte en el piso 1 y el ascensor
    /// aparece allí de golpe, así que en un extremo la orden se da la vuelta y la palanca queda apuntando hacia donde va.
    /// </summary>
    private static FsmEvent CheckLiftDirection(Tracked item, FsmEvent fsmEvent, Fsm sender) {
        if (item.Lift == null || item.CurrentPosition == null || item.Lift.stopPositions == null) {
            return fsmEvent;
        }

        var down = fsmEvent.Name == "CONTROL DOWN";
        if (!down && fsmEvent.Name != "CONTROL UP") {
            return fsmEvent;
        }

        var current = item.CurrentPosition.Value;
        if (down ? current < item.Lift.stopPositions.Length : current > 1) {
            return fsmEvent;
        }

        Log.Warn($"{item.Fsm.gameObject.name}: {fsmEvent.Name} en el piso {current}, se da la vuelta");
        var leverGoingDown = sender?.Variables.FindFsmBool("Going Down");
        if (leverGoingDown != null) {
            leverGoingDown.Value = !down;
        }

        return FsmEvent.GetFsmEvent(down ? "CONTROL UP" : "CONTROL DOWN");
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
        TakeControl(item, "orden de quien no va subido");
        _executing = true;
        try {
            item.Fsm.SendEvent(name);
        } finally {
            _executing = false;
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

    /// <summary>
    /// Reja de batalla o de jefe cerrada por otro jugador (aquí es copia): se queda del lado de dentro el que tenía ese
    /// jugador al cerrarse. Si el caballero local está fuera, la reja no le frena (puede entrar a ayudar); en cuanto cruza
    /// del todo, vuelve a ser sólida y ya no puede salir hasta que se abra. Para quien la cerró, normal.
    /// </summary>
    private void UpdateGatePassage(Tracked item) {
        var closed = item.Fsm.ActiveStateName.IndexOf("Close", StringComparison.Ordinal) >= 0;
        if (!closed) {
            // Al abrirse, la FSM ya la deja sin colisión
            item.GateWasOpen |= item.Ready;
            item.InsideSign = 0;
            item.Passing = false;
            return;
        }

        // Una reja cerrada desde que se cargó la sala (p. ej. la derecha de Mamá Gruz) no es de una batalla: sólida
        var hero = HeroController.instance;
        if (!item.GateWasOpen || IsController(item) || hero == null) {
            RestoreGate(item);
            return;
        }

        var gate = item.Gate;
        var scale = gate.transform.lossyScale;
        var width = Mathf.Abs(gate.size.x * scale.x);
        var height = Mathf.Abs(gate.size.y * scale.y);
        var vertical = height >= width;
        var center = gate.transform.TransformPoint(gate.offset);
        if (item.InsideSign == 0) {
            // Quien la cerró: su controlador o, si aún no se sabe, el dueño de la sala
            var closerId = item.Controller != 0 ? item.Controller : _session.OwnerOf(_scene);
            if (!_players.TryGetAvatarPosition(closerId, out var closer)) {
                return;
            }

            item.InsideSign = Side(closer, center, vertical);
            if (item.InsideSign == 0) {
                return;
            }
        }

        var heroPosition = hero.transform.position;
        var along = vertical ? heroPosition.x - center.x : heroPosition.y - center.y;
        var halfThickness = (vertical ? width : height) * 0.5f;
        if (along * item.InsideSign > halfThickness + GateClearance) {
            // Ya dentro del todo: sólida
            RestoreGate(item);
        } else if (along * item.InsideSign < 0f || item.Passing) {
            // Fuera (o cruzándola): no frena
            item.Passing = true;
            gate.enabled = false;
        }
    }

    private static void RestoreGate(Tracked item) {
        if (item.Passing) {
            item.Passing = false;
            if (item.Gate != null) {
                item.Gate.enabled = true;
            }
        }
    }

    private static int Side(Vector3 position, Vector3 center, bool vertical) {
        var along = vertical ? position.x - center.x : position.y - center.y;
        return along > 0.05f ? 1 : along < -0.05f ? -1 : 0;
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

    /// <summary>
    /// El controlador envía las variables de su FSM cuando cambian (después del estado, en orden): así quien tome el
    /// control parte del piso real y no de uno viejo, que haría saltar el ascensor.
    /// </summary>
    private void SendVarsIfChanged(Tracked item) {
        if (!_session.CanSend) {
            return;
        }

        var ints = item.Fsm.FsmVariables.IntVariables;
        var bools = item.Fsm.FsmVariables.BoolVariables;
        var changed = !item.VarsSent;
        for (var i = 0; i < ints.Length && i < MaxVars && !changed; i++) {
            changed = ints[i].Value != item.SentInts[i];
        }

        for (var i = 0; i < bools.Length && i < MaxVars && !changed; i++) {
            changed = bools[i].Value != item.SentBools[i];
        }

        if (!changed) {
            return;
        }

        item.VarsSent = true;
        var writer = _session.Begin(Vars);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(item.Id);
        writer.WriteByte((byte) Math.Min(ints.Length, MaxVars));
        for (var i = 0; i < ints.Length && i < MaxVars; i++) {
            item.SentInts[i] = ints[i].Value;
            writer.WriteInt(ints[i].Value);
        }

        writer.WriteByte((byte) Math.Min(bools.Length, MaxVars));
        for (var i = 0; i < bools.Length && i < MaxVars; i++) {
            item.SentBools[i] = bools[i].Value;
            writer.WriteBool(bools[i].Value);
        }

        _session.End();
    }

    private void OnVars(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        _byId.TryGetValue(id, out var item);
        if (sceneHash != _sceneHash || item?.Fsm == null || !AcceptFrom(item, from)) {
            item = null;
        }

        var ints = item?.Fsm.FsmVariables.IntVariables;
        var bools = item?.Fsm.FsmVariables.BoolVariables;
        var intCount = reader.ReadByte();
        for (var i = 0; i < intCount; i++) {
            var value = reader.ReadInt();
            if (ints != null && i < ints.Length) {
                ints[i].Value = value;
            }
        }

        var boolCount = reader.ReadByte();
        for (var i = 0; i < boolCount; i++) {
            var value = reader.ReadBool();
            if (bools != null && i < bools.Length) {
                bools[i].Value = value;
            }
        }

        // Si este PC toma el control más tarde, ya parte de estos valores; al dejar de ser copia se reenvían
        if (item != null) {
            item.VarsSent = false;
        }
    }

    private void OnControllerState(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var state = reader.ReadString(128);
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var item) || item.Fsm == null ||
            !AcceptFrom(item, from) || !item.Ready || item.Fsm.ActiveStateName == state) {
            return;
        }

        // Se pone el mismo estado en la copia (luces, cadenas, sonido); su posición la pone el flujo del controlador
        item.LastState = state;
        item.LastStateTime = Time.unscaledTime;
        ApplyRemoteState(item, state);
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
        if (ApplyingSavedState) {
            // Puesto como ya activado por lo que llegó de otro PC: no es un golpe de aquí
            item.LocalChain = false;
        } else if (now < item.RemoteChainUntil) {
            // Cadena que llegó de otro PC: no se devuelve
        } else if (from != null && from == item.RestState) {
            item.LocalChain = true;
            send = true;
            Log.Info($"{item.Fsm.gameObject.name}/{item.Fsm.FsmName}: golpe en este PC ({from} → {state}), se copia la cadena");
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

        // Lo copiado no se vuelve a enviar ni encadena solo: el siguiente paso también llega de allí
        item.LastState = state;
        item.LastStateTime = Time.unscaledTime;
        ApplyRemoteState(item, state);
    }

    /// <summary>
    /// Estado al que salta una copia que se perdió los pasos del controlador. Un controlador de batalla, al mismo. Una
    /// reja, a su cierre o a su apertura (el estado que pone su colisión y su animación: "Close 2" o "Double Close"
    /// solos no la harían sólida). Los ascensores no: su piso llega con su posición.
    /// </summary>
    private static string CatchUpState(Tracked item, string remote) {
        if (item.Battle) {
            return HasState(item, remote) ? remote : null;
        }

        if (item.Gate == null) {
            return null;
        }

        var remoteClosed = remote.IndexOf("Close", StringComparison.Ordinal) >= 0;
        var localClosed = item.Fsm.ActiveStateName.IndexOf("Close", StringComparison.Ordinal) >= 0;
        if (remoteClosed == localClosed) {
            return null;
        }

        var target = remoteClosed ? "Close 1" : "Open";
        return HasState(item, target) ? target : null;
    }

    private static bool HasState(Tracked item, string name) => Array.Exists(item.Fsm.FsmStates, s => s.Name == name);

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
        var localStill = Time.unscaledTime - item.LastStateTime >= StillSeconds;
        var still = item.Fsm.ActiveStateName == state && localStill;

        // Quieto allí en otro estado: este PC entró (o volvió) a mitad del combate y se perdió sus pasos
        var catchUp = item.Ready && localStill && !IsController(item) && state.Length > 0 && item.Fsm.ActiveStateName != state
            ? CatchUpState(item, state)
            : null;
        if (catchUp != null) {
            Log.Info($"{item.Fsm.gameObject.name}/{item.Fsm.FsmName}: se pone al día en {catchUp} (allí {state})");
            item.LastState = catchUp;
            item.LastStateTime = Time.unscaledTime;
            ApplyRemoteState(item, catchUp);
        }
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
                    : $" control={(IsController(item) ? "aquí" : item.Controller.ToString())} lista={(item.Ready ? "sí" : "no")}" +
                      $" retenido={item.HeldTo ?? "-"} subido={(item.LocalAboard ? "sí" : "no")}" +
                      $" otros=[{string.Join(",", item.RemoteRiders)}] vars=[" +
                      string.Join(",", Array.ConvertAll(item.Fsm.FsmVariables.IntVariables, v => $"{v.Name}={v.Value}")) + "]";
                var bools = string.Join(",", Array.ConvertAll(item.Fsm.FsmVariables.BoolVariables, v => $"{v.Name}={v.Value}"));
                if (item.Gate != null) {
                    var b = item.Gate.bounds;
                    control += $" reja=[dentro={item.InsideSign} pasa={(item.Passing ? "sí" : "no")} " +
                               $"colisión={(item.Gate.enabled ? "sí" : "no")} x={b.min.x:0.00}..{b.max.x:0.00} y={b.min.y:0.00}..{b.max.y:0.00}]";
                }

                yield return $"{item.Fsm.gameObject.name}/{item.Fsm.FsmName} id={item.Id:x8} estado={item.Fsm.ActiveStateName} " +
                             $"pos=({p.x:0.00},{p.y:0.00}){control} bools=[{bools}]";
            }
        }
    }

    /// <summary>Envía una orden a un objeto como si viniera de su palanca o botón en este PC.</summary>
    public bool TestSendEvent(string namePrefix, string eventName) {
        // "objeto/FSM" para elegir una FSM concreta de un objeto con varias
        var slash = namePrefix.IndexOf('/');
        var fsmName = slash >= 0 ? namePrefix.Substring(slash + 1) : null;
        namePrefix = slash >= 0 ? namePrefix.Substring(0, slash) : namePrefix;
        foreach (var item in _tracked) {
            if (item.Fsm != null && item.Fsm.gameObject.name.StartsWith(namePrefix, StringComparison.Ordinal) &&
                (fsmName == null || item.Fsm.FsmName == fsmName)) {
                TakeControl(item, "prueba");
                _executing = true;
                try {
                    item.Fsm.SendEvent(eventName);
                } finally {
                    _executing = false;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Este PC toma el control del objeto y pone su FSM en ese estado, que se copia a los demás como cualquier otro
    /// (modo de prueba: saltar oleadas del Coliseo hasta su jefe final).
    /// </summary>
    public bool TestSetState(string namePrefix, string stateName) {
        var slash = namePrefix.IndexOf('/');
        var fsmName = slash >= 0 ? namePrefix.Substring(slash + 1) : null;
        namePrefix = slash >= 0 ? namePrefix.Substring(0, slash) : namePrefix;
        foreach (var item in _tracked) {
            if (item.Fsm != null && item.Fsm.gameObject.name.StartsWith(namePrefix, StringComparison.Ordinal) &&
                (fsmName == null || item.Fsm.FsmName == fsmName)) {
                TakeControl(item, "prueba");
                _executing = true;
                try {
                    item.Fsm.SetState(stateName);
                } finally {
                    _executing = false;
                }

                return true;
            }
        }

        return false;
    }
}
