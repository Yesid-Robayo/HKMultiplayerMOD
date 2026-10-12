using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Peligros y piezas animados de la sala (sierras del Palacio Blanco y del Camino del Dolor, paredes de pinchos que
/// entran y salen): se mueven con una animación en bucle que empieza al cargar la sala, así que cada PC llevaría su
/// propio ritmo y el compañero se vería atravesando sierras que en la pantalla de otro no estaban. El dueño de la sala
/// envía cada segundo en qué punto va cada una (estado y tiempo de la animación) y los demás se ajustan si se desvían.
/// El daño sigue siendo de cada PC; solo se alinea lo que se ve. No crea memoria por envío.
/// <para>
/// Igual los que salen y se esconden con una FSM de esperas fijas (los Goam de Cruces Olvidados, `Worm Control`: arriba,
/// se esconde, abajo, sale): no son enemigos (no tienen vida) y cada PC empezaba su vuelta al cargar la sala, así que
/// en uno estaban fuera y en el otro escondidos. El dueño envía el estado de espera en el que va cada uno y cuánto lleva;
/// los demás pasan a ese estado y ajustan su espera si se desvían.
/// </para>
/// <para>
/// Y los que flotan a la deriva sin vida (los Charged Lumafly del Cañón de Niebla, `zap drift`): cada PC los movía al azar
/// y acababan en sitios distintos. Los mueve el dueño de la sala, que envía dónde está cada uno 10 veces por segundo; en
/// los demás su deriva se para y siguen esa posición suavizada. Su descarga sigue siendo de cada PC (daña a su caballero).
/// </para>
/// <para>
/// Y los gusanos gigantes de Nido Profundo (Garpedes, `BigCentipede`): avanzan en línea recta a velocidad fija, se meten
/// bajo tierra en su salida y, tras una espera fija, vuelven a salir por su entrada. Su vuelta empieza al cargar la sala,
/// así que en cada PC iban por un sitio distinto. Por dónde van (a lo largo de su recorrido) dice en qué punto de la
/// vuelta están: el dueño envía cada segundo ese punto y los demás se ponen ahí si se desvían.
/// </para>
/// </summary>
internal sealed class HazardSync {
    public const MessageType Phases = (MessageType) 70;
    public const MessageType CyclePhases = (MessageType) 104;
    public const MessageType DriftPositions = (MessageType) 105;
    public const MessageType BurrowPhases = (MessageType) 107;

    /// <summary>Lo que retrocede el gusano desde su entrada al volver a salir (fijo en `BigCentipede.UnBurrow`).</summary>
    private const float BurrowBackOffset = 2.6f;

    /// <summary>Desfase de un gusano (s de su vuelta) a partir del cual se corrige.</summary>
    private const float BurrowTolerance = 0.1f;

    private static readonly System.Reflection.FieldInfo CentipedeSpeed = typeof(BigCentipede).GetField("moveSpeed", Private);
    private static readonly System.Reflection.FieldInfo CentipedeBurrowTime = typeof(BigCentipede).GetField("burrowTime", Private);
    private static readonly System.Reflection.FieldInfo CentipedeBurrowing = typeof(BigCentipede).GetField("isBurrowing", Private);
    private static readonly System.Reflection.FieldInfo CentipedeBurrowTimer = typeof(BigCentipede).GetField("burrowTimer", Private);
    private static readonly System.Reflection.MethodInfo CentipedeBurrow = typeof(BigCentipede).GetMethod("Burrow", Private);
    private static readonly System.Reflection.MethodInfo CentipedeUnBurrow = typeof(BigCentipede).GetMethod("UnBurrow", Private);

    private readonly List<(uint Id, BigCentipede Centipede)> _burrowers = new();
    private readonly Dictionary<uint, BigCentipede> _burrowerById = new();
    private readonly MessageHandler _onBurrowPhases;

    /// <summary>FSM de los peligros que flotan a la deriva.</summary>
    private const string DriftFsm = "zap drift";

    private const float DriftSendInterval = 0.1f;

    /// <summary>Lo que se acerca a la posición recibida cada segundo (fracción): suave pero sin quedarse atrás.</summary>
    private const float DriftFollow = 10f;

    private sealed class Drifter {
        public uint Id;
        public PlayMakerFSM Fsm;
        public Transform Transform;
        public Rigidbody2D Body;
        public bool Following;
        public bool HasTarget;
        public Vector2 Target;
        public RigidbodyType2D BodyType;
    }

    private readonly List<Drifter> _drifters = new();
    private readonly Dictionary<uint, Drifter> _drifterById = new();
    private readonly MessageHandler _onDriftPositions;
    private float _nextDriftSend;

    /// <summary>FSM de peligros que salen y se esconden solos con esperas fijas.</summary>
    private static readonly string[] CyclicFsms = { "Worm Control" };

    /// <summary>Desfase de una espera (s) a partir del cual se corrige.</summary>
    private const float CycleTolerance = 0.1f;

    private static readonly bool TestMode = HKMultiplayer.Testing.TestDriver.IsEnabled();

    /// <summary>
    /// Cerca del principio o del final de una espera no se corrige: ahí cada PC cambia de estado en un frame distinto.
    /// </summary>
    private const float CycleEdge = 0.25f;

    private const System.Reflection.BindingFlags Private =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static readonly System.Reflection.FieldInfo WaitTimer =
        typeof(HutongGames.PlayMaker.Actions.Wait).GetField("timer", Private);

    private static readonly System.Reflection.FieldInfo WaitStartTime =
        typeof(HutongGames.PlayMaker.Actions.Wait).GetField("startTime", Private);

    private const float SendInterval = 1f;
    private const float RescanDelay = 1.5f;
    private const int MaxHazards = 160;

    /// <summary>Cada uno ocupa 12 bytes: así cabe de sobra en un mensaje.</summary>
    private const int PerMessage = 60;

    /// <summary>Desfase (en fracción de vuelta) a partir del cual se corrige: menos no se nota y saltaría a cada rato.</summary>
    private const float Tolerance = 0.03f;

    private readonly List<(uint Id, Animator Animator)> _hazards = new();
    private readonly Dictionary<uint, Animator> _byId = new();
    private readonly List<(uint Id, PlayMakerFSM Fsm)> _cyclic = new();
    private readonly Dictionary<uint, PlayMakerFSM> _cyclicById = new();
    private readonly MessageHandler _onPhases;
    private readonly MessageHandler _onCyclePhases;
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;
    private float _nextSend;

    static HazardSync() {
        MessageRoutes.Register(Phases, Route.SameScene, reliable: false);
        MessageRoutes.Register(CyclePhases, Route.SameScene, reliable: false);
        MessageRoutes.Register(DriftPositions, Route.SameScene, reliable: false);
        MessageRoutes.Register(BurrowPhases, Route.SameScene, reliable: false);
    }

    public HazardSync() {
        _onPhases = OnPhases;
        _onCyclePhases = OnCyclePhases;
        _onDriftPositions = OnDriftPositions;
        _onBurrowPhases = OnBurrowPhases;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(Phases, _onPhases);
        session.On(CyclePhases, _onCyclePhases);
        session.On(DriftPositions, _onDriftPositions);
        session.On(BurrowPhases, _onBurrowPhases);
        Clear();
    }

    public void Detach() {
        _session?.On(Phases, null);
        _session?.On(CyclePhases, null);
        _session?.On(DriftPositions, null);
        _session?.On(BurrowPhases, null);
        foreach (var drifter in _drifters) {
            StopFollowing(drifter);
        }

        _session = null;
        Clear();
    }

    private void Clear() {
        _hazards.Clear();
        _byId.Clear();
        _cyclic.Clear();
        _cyclicById.Clear();
        _drifters.Clear();
        _drifterById.Clear();
        _burrowers.Clear();
        _burrowerById.Clear();
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
            _rescanAt = Time.unscaledTime + RescanDelay;
            return;
        }

        if (scene.Length == 0) {
            return;
        }

        if (_rescanAt > 0f && Time.unscaledTime >= _rescanAt) {
            _rescanAt = 0f;
            Scan();
        }

        UpdateDrifters();
        if (_hazards.Count + _cyclic.Count + _burrowers.Count == 0 || !_session.CanSend ||
            !_session.IsLocalSceneOwner || Time.unscaledTime < _nextSend || !OthersInScene()) {
            return;
        }

        _nextSend = Time.unscaledTime + SendInterval;
        Send();
        SendCycles();
        SendBurrowers();
    }

    /// <summary>Animados con algo que daña o que es sólido (lo que se puede tocar), sin enemigos ni el caballero.</summary>
    private void Scan() {
        var hero = HeroController.instance;
        foreach (var animator in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None)) {
            if (_hazards.Count >= MaxHazards) {
                break;
            }

            var transform = animator.transform;
            if (animator.gameObject.scene.buildIndex < 0 || EnemySync.IsEnemyObject(transform) ||
                hero != null && transform.IsChildOf(hero.transform) || !Touchable(animator)) {
                continue;
            }

            var id = ScenePaths.Hash(transform);
            if (_byId.ContainsKey(id)) {
                continue;
            }

            _hazards.Add((id, animator));
            _byId[id] = animator;
        }

        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)) {
            if (fsm.FsmName == DriftFsm && fsm.gameObject.scene.buildIndex >= 0 && _drifters.Count < MaxHazards) {
                var driftId = ScenePaths.Hash(fsm.transform);
                if (!_drifterById.ContainsKey(driftId)) {
                    var drifter = new Drifter {
                        Id = driftId, Fsm = fsm, Transform = fsm.transform, Body = fsm.GetComponent<Rigidbody2D>()
                    };
                    _drifters.Add(drifter);
                    _drifterById[driftId] = drifter;
                }

                continue;
            }

            if (_cyclic.Count >= MaxHazards || fsm.gameObject.scene.buildIndex < 0 ||
                System.Array.IndexOf(CyclicFsms, fsm.FsmName) < 0) {
                continue;
            }

            var id = ScenePaths.Hash(fsm.transform);
            if (_cyclicById.ContainsKey(id)) {
                continue;
            }

            _cyclic.Add((id, fsm));
            _cyclicById[id] = fsm;
        }

        foreach (var centipede in Object.FindObjectsByType<BigCentipede>(FindObjectsSortMode.None)) {
            if (_burrowers.Count >= MaxHazards || centipede.gameObject.scene.buildIndex < 0) {
                continue;
            }

            var id = ScenePaths.Hash(centipede.transform);
            if (_burrowerById.ContainsKey(id)) {
                continue;
            }

            _burrowers.Add((id, centipede));
            _burrowerById[id] = centipede;
        }
    }

    private static bool Touchable(Animator animator) {
        if (animator.GetComponentInChildren<DamageHero>() != null) {
            return true;
        }

        foreach (var collider in animator.GetComponentsInChildren<Collider2D>()) {
            if (!collider.isTrigger) {
                return true;
            }
        }

        return false;
    }

    private bool OthersInScene() {
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene) {
                return true;
            }
        }

        return false;
    }

    private void Send() {
        PacketWriter writer = null;
        var countPosition = 0;
        var count = 0;
        foreach (var (id, animator) in _hazards) {
            if (animator == null || !animator.isActiveAndEnabled) {
                continue;
            }

            if (writer == null || count == PerMessage) {
                if (writer != null) {
                    Finish(writer, countPosition, count);
                }

                writer = _session.Begin(Phases);
                writer.WriteUShort(_sceneHash);
                countPosition = writer.Position;
                writer.WriteByte(0);
                count = 0;
            }

            var state = animator.GetCurrentAnimatorStateInfo(0);
            writer.WriteUInt(id);
            writer.WriteInt(state.fullPathHash);
            writer.WriteFloat(state.normalizedTime);
            count++;
        }

        if (writer != null) {
            Finish(writer, countPosition, count);
        }
    }

    private void Finish(PacketWriter writer, int countPosition, int count) {
        var end = writer.Position;
        writer.Seek(countPosition);
        writer.WriteByte((byte) count);
        writer.Seek(end);
        _session.End();
    }

    /// <summary>
    /// Los que flotan a la deriva: el dueño de la sala los deja moverse solos y envía dónde están; los demás paran su
    /// deriva y se acercan a la posición recibida.
    /// </summary>
    private void UpdateDrifters() {
        if (_drifters.Count == 0) {
            return;
        }

        var owner = _session.IsLocalSceneOwner || !OthersInScene();
        var dt = Time.deltaTime;
        foreach (var drifter in _drifters) {
            if (drifter.Fsm == null) {
                continue;
            }

            if (owner) {
                StopFollowing(drifter);
                continue;
            }

            if (!drifter.Following) {
                drifter.Following = true;
                drifter.Fsm.enabled = false;
                if (drifter.Body != null) {
                    drifter.BodyType = drifter.Body.bodyType;
                    drifter.Body.velocity = Vector2.zero;
                    drifter.Body.bodyType = RigidbodyType2D.Kinematic;
                }
            }

            if (drifter.HasTarget) {
                var position = (Vector2) drifter.Transform.position;
                var next = Vector2.Lerp(position, drifter.Target, Mathf.Clamp01(DriftFollow * dt));
                drifter.Transform.position = new Vector3(next.x, next.y, drifter.Transform.position.z);
            }
        }

        if (!owner || !_session.CanSend || Time.unscaledTime < _nextDriftSend || !OthersInScene()) {
            return;
        }

        _nextDriftSend = Time.unscaledTime + DriftSendInterval;
        var writer = _session.Begin(DriftPositions);
        writer.WriteUShort(_sceneHash);
        var count = System.Math.Min(_drifters.Count, PerMessage);
        writer.WriteByte((byte) count);
        for (var i = 0; i < count; i++) {
            var drifter = _drifters[i];
            var position = drifter.Transform != null ? drifter.Transform.position : Vector3.zero;
            writer.WriteUInt(drifter.Id);
            writer.WriteFloat(position.x);
            writer.WriteFloat(position.y);
        }

        _session.End();
    }

    /// <summary>Vuelve a moverse solo en este PC (pasó a ser el dueño de la sala o se acaba la partida).</summary>
    private static void StopFollowing(Drifter drifter) {
        if (!drifter.Following || drifter.Fsm == null) {
            return;
        }

        drifter.Following = false;
        drifter.HasTarget = false;
        drifter.Fsm.enabled = true;
        if (drifter.Body != null) {
            drifter.Body.bodyType = drifter.BodyType;
        }
    }

    private void OnDriftPositions(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var count = reader.ReadByte();
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            var target = new Vector2(reader.ReadFloat(), reader.ReadFloat());
            if (sceneHash != _sceneHash || from.Scene != _scene || !_drifterById.TryGetValue(id, out var drifter)) {
                continue;
            }

            // La primera vez se pone en su sitio de golpe (al entrar en la sala cada PC lo tenía donde lo dejó su azar)
            if (!drifter.HasTarget && drifter.Transform != null) {
                drifter.Transform.position = new Vector3(target.x, target.y, drifter.Transform.position.z);
            }

            drifter.Target = target;
            drifter.HasTarget = true;
        }
    }

    /// <summary>Los que están en una espera: su estado (por su puesto en la FSM, igual en todos los PCs) y cuánto lleva.</summary>
    private void SendCycles() {
        PacketWriter writer = null;
        var countPosition = 0;
        var count = 0;
        foreach (var (id, fsm) in _cyclic) {
            if (fsm == null || !fsm.isActiveAndEnabled || !TryGetWait(fsm, out var state, out var wait)) {
                continue;
            }

            if (writer == null || count == PerMessage) {
                if (writer != null) {
                    Finish(writer, countPosition, count);
                }

                writer = _session.Begin(CyclePhases);
                writer.WriteUShort(_sceneHash);
                countPosition = writer.Position;
                writer.WriteByte(0);
                count = 0;
            }

            writer.WriteUInt(id);
            writer.WriteByte((byte) state);
            writer.WriteFloat(Elapsed(wait));
            count++;
        }

        if (writer != null) {
            Finish(writer, countPosition, count);
        }
    }

    private void OnCyclePhases(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var count = reader.ReadByte();
        // Lo que tardó en llegar: la espera del dueño ya va por delante
        var delay = (float) (_session.Rtt / 2);
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            var stateIndex = reader.ReadByte();
            var elapsed = reader.ReadFloat() + delay;
            if (sceneHash != _sceneHash || from.Scene != _scene || !_cyclicById.TryGetValue(id, out var fsm) ||
                fsm == null || !fsm.isActiveAndEnabled || stateIndex >= fsm.FsmStates.Length) {
                continue;
            }

            var remoteWait = FirstWait(fsm.FsmStates[stateIndex]);
            if (remoteWait == null || elapsed < CycleEdge || elapsed > remoteWait.time.Value - CycleEdge) {
                continue;
            }

            var inWait = TryGetWait(fsm, out var localState, out var localWait);
            if (inWait && localState == stateIndex && Mathf.Abs(Elapsed(localWait) - elapsed) <= CycleTolerance) {
                continue;
            }

            if (TestMode) {
                Log.Info($"[TEST] {fsm.name}: se alinea con el dueño ({(inWait ? fsm.ActiveStateName : "-")} -> " +
                         $"{fsm.FsmStates[stateIndex].Name}@{elapsed:0.00})");
            }

            // Al pasar a ese estado se hace lo suyo (animación, caja de daño) y su espera empieza de cero: se adelanta
            if (!inWait || localState != stateIndex) {
                fsm.SetState(fsm.FsmStates[stateIndex].Name);
            }

            SetElapsed(remoteWait, elapsed);
        }
    }

    /// <summary>Si su estado de ahora tiene una espera: el puesto del estado y la espera.</summary>
    private static bool TryGetWait(PlayMakerFSM fsm, out int stateIndex, out HutongGames.PlayMaker.Actions.Wait wait) {
        stateIndex = -1;
        wait = null;
        var active = fsm.Fsm.ActiveState;
        if (active == null) {
            return false;
        }

        stateIndex = System.Array.IndexOf(fsm.FsmStates, active);
        wait = FirstWait(active);
        return stateIndex >= 0 && stateIndex < 256 && wait != null;
    }

    private static HutongGames.PlayMaker.Actions.Wait FirstWait(HutongGames.PlayMaker.FsmState state) {
        foreach (var action in state.Actions) {
            if (action is HutongGames.PlayMaker.Actions.Wait wait && action.Enabled) {
                return wait;
            }
        }

        return null;
    }

    private static float Elapsed(HutongGames.PlayMaker.Actions.Wait wait) {
        if (wait.realTime) {
            return WaitStartTime != null
                ? HutongGames.PlayMaker.FsmTime.RealtimeSinceStartup - (float) WaitStartTime.GetValue(wait)
                : 0f;
        }

        return WaitTimer != null ? (float) WaitTimer.GetValue(wait) : 0f;
    }

    private static void SetElapsed(HutongGames.PlayMaker.Actions.Wait wait, float elapsed) {
        if (wait.realTime) {
            WaitStartTime?.SetValue(wait, HutongGames.PlayMaker.FsmTime.RealtimeSinceStartup - elapsed);
        } else {
            WaitTimer?.SetValue(wait, elapsed);
        }
    }

    private void OnPhases(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var count = reader.ReadByte();
        // Lo que tardó en llegar: la animación del dueño ya va por delante
        var delay = (float) (_session.Rtt / 2);
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            var stateHash = reader.ReadInt();
            var time = reader.ReadFloat();
            if (sceneHash != _sceneHash || from.Scene != _scene || !_byId.TryGetValue(id, out var animator) ||
                animator == null || !animator.isActiveAndEnabled) {
                continue;
            }

            var local = animator.GetCurrentAnimatorStateInfo(0);
            var expected = Mathf.Repeat(time + delay / Mathf.Max(local.length, 0.01f), 1f);
            if (local.fullPathHash == stateHash) {
                var difference = Mathf.Repeat(local.normalizedTime - expected + 0.5f, 1f) - 0.5f;
                if (Mathf.Abs(difference) <= Tolerance) {
                    continue;
                }
            }

            animator.Play(stateHash, 0, expected);
        }
    }

    /// <summary>Los gusanos ya empezados: en qué punto de su vuelta va cada uno.</summary>
    private void SendBurrowers() {
        PacketWriter writer = null;
        var countPosition = 0;
        var count = 0;
        foreach (var (id, centipede) in _burrowers) {
            if (centipede == null || !centipede.isActiveAndEnabled || !TryGetCycle(centipede, out var cycle)) {
                continue;
            }

            if (writer == null || count == PerMessage) {
                if (writer != null) {
                    Finish(writer, countPosition, count);
                }

                writer = _session.Begin(BurrowPhases);
                writer.WriteUShort(_sceneHash);
                countPosition = writer.Position;
                writer.WriteByte(0);
                count = 0;
            }

            writer.WriteUInt(id);
            writer.WriteFloat(PhaseOf(centipede, cycle));
            count++;
        }

        if (writer != null) {
            Finish(writer, countPosition, count);
        }
    }

    private void OnBurrowPhases(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var count = reader.ReadByte();
        // Lo que tardó en llegar: el gusano del dueño ya va por delante
        var delay = (float) (_session.Rtt / 2);
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            var phase = reader.ReadFloat() + delay;
            if (sceneHash != _sceneHash || from.Scene != _scene || !_burrowerById.TryGetValue(id, out var centipede) ||
                centipede == null || !centipede.isActiveAndEnabled || !TryGetCycle(centipede, out var cycle)) {
                continue;
            }

            // Por el camino más corto en la vuelta: justo antes y justo después de volver a salir es casi lo mismo
            var target = Mathf.Repeat(phase, cycle.Period);
            var local = PhaseOf(centipede, cycle);
            var difference = Mathf.Repeat(target - local + cycle.Period / 2f, cycle.Period) - cycle.Period / 2f;
            if (Mathf.Abs(difference) <= BurrowTolerance) {
                continue;
            }

            if (TestMode) {
                Log.Info($"[TEST] {centipede.name}: se alinea con el dueño ({local:0.00} -> {target:0.00} de {cycle.Period:0.00} s)");
            }

            SetPhase(centipede, cycle, target);
        }
    }

    /// <summary>
    /// La vuelta de un gusano, en unidades de su recorrido: dónde vuelve a salir (`Start`), dónde se mete (`Exit`), su
    /// velocidad y cuánto dura entera (el camino hasta meterse más la espera bajo tierra, en la que sigue avanzando).
    /// </summary>
    private struct CentipedeCycle {
        public Vector2 Direction;
        public float Start;
        public float Exit;
        public float Speed;
        public float Travel;
        public float Period;
    }

    /// <summary>Solo si ya pasó su `Start` (antes no tiene dirección ni entrada) y los campos del juego existen.</summary>
    private static bool TryGetCycle(BigCentipede centipede, out CentipedeCycle cycle) {
        cycle = default;
        var direction = centipede.Direction;
        if (direction.sqrMagnitude < 0.5f || CentipedeSpeed == null || CentipedeBurrowTime == null ||
            CentipedeBurrowing == null || CentipedeBurrowTimer == null || CentipedeBurrow == null ||
            CentipedeUnBurrow == null) {
            return false;
        }

        cycle.Direction = direction;
        cycle.Speed = (float) CentipedeSpeed.GetValue(centipede);
        cycle.Start = Vector2.Dot(centipede.EntryPoint, direction) - BurrowBackOffset;
        cycle.Exit = Vector2.Dot(centipede.ExitPoint, direction);
        if (cycle.Speed <= 0.01f || cycle.Exit <= cycle.Start) {
            return false;
        }

        cycle.Travel = (cycle.Exit - cycle.Start) / cycle.Speed;
        cycle.Period = cycle.Travel + (float) CentipedeBurrowTime.GetValue(centipede);
        return true;
    }

    /// <summary>Segundos desde que volvió a salir: se sabe por dónde va, también bajo tierra (ahí sigue avanzando).</summary>
    private static float PhaseOf(BigCentipede centipede, CentipedeCycle cycle) =>
        (Vector2.Dot(centipede.transform.position, cycle.Direction) - cycle.Start) / cycle.Speed;

    /// <summary>
    /// Lo pone en ese punto de la vuelta: lo mueve a lo largo de su recorrido (con sus secciones, que son sus hijas) y,
    /// si cambia de estar fuera a bajo tierra o al revés, hace lo que hace el juego en ese cambio (polvo, se esconde).
    /// Las secciones se esconden y se muestran solas según por dónde van.
    /// </summary>
    private static void SetPhase(BigCentipede centipede, CentipedeCycle cycle, float phase) {
        var transform = centipede.transform;
        var shift = cycle.Start + phase * cycle.Speed - Vector2.Dot(transform.position, cycle.Direction);
        var position = (Vector2) transform.position + cycle.Direction * shift;
        transform.position = new Vector3(position.x, position.y, transform.position.z);
        var body = centipede.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.position = position;
        }

        var burrowing = (bool) CentipedeBurrowing.GetValue(centipede);
        if (phase > cycle.Travel) {
            if (!burrowing) {
                CentipedeBurrow.Invoke(centipede, null);
            }

            CentipedeBurrowTimer.SetValue(centipede, phase - cycle.Travel);
        } else if (burrowing) {
            CentipedeUnBurrow.Invoke(centipede, new object[] { false });
        }
    }

    /// <summary>Para el modo de prueba: cuántos se siguen y en qué punto van los que están animándose.</summary>
    public string Describe() {
        var builder = new System.Text.StringBuilder($"peligros animados: {_hazards.Count}, con esperas: {_cyclic.Count}, " +
                                                    $"a la deriva: {_drifters.Count}, gusanos: {_burrowers.Count} " +
                                                    $"a las {System.DateTime.UtcNow:HH:mm:ss.fff}");
        foreach (var (_, centipede) in _burrowers) {
            if (centipede != null && TryGetCycle(centipede, out var cycle)) {
                builder.Append($" {centipede.name}={PhaseOf(centipede, cycle):0.00}/{cycle.Period:0.00}");
            }
        }

        foreach (var drifter in _drifters) {
            if (drifter.Transform != null) {
                builder.Append($" {drifter.Transform.name}=({drifter.Transform.position.x:0.00},{drifter.Transform.position.y:0.00})" +
                               $"{(drifter.Following ? " sigue" : "")}");
            }
        }

        foreach (var (_, fsm) in _cyclic) {
            if (fsm != null && TryGetWait(fsm, out _, out var wait)) {
                builder.Append($" {fsm.name}={fsm.ActiveStateName}@{Elapsed(wait):0.00}");
            } else if (fsm != null) {
                builder.Append($" {fsm.name}={fsm.ActiveStateName}");
            }
        }

        var shown = 0;
        foreach (var (_, animator) in _hazards) {
            if (animator == null || !animator.isActiveAndEnabled || shown >= 8) {
                continue;
            }

            var state = animator.GetCurrentAnimatorStateInfo(0);
            if (state.length <= 0.01f || state.normalizedTime <= 0f) {
                continue;
            }

            builder.Append($" {animator.name}={Mathf.Repeat(state.normalizedTime, 1f):0.00}");
            shown++;
        }

        return builder.ToString();
    }
}
