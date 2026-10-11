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
/// </summary>
internal sealed class HazardSync {
    public const MessageType Phases = (MessageType) 70;
    public const MessageType CyclePhases = (MessageType) 104;

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
    }

    public HazardSync() {
        _onPhases = OnPhases;
        _onCyclePhases = OnCyclePhases;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(Phases, _onPhases);
        session.On(CyclePhases, _onCyclePhases);
        Clear();
    }

    public void Detach() {
        _session?.On(Phases, null);
        _session?.On(CyclePhases, null);
        _session = null;
        Clear();
    }

    private void Clear() {
        _hazards.Clear();
        _byId.Clear();
        _cyclic.Clear();
        _cyclicById.Clear();
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

        if (_hazards.Count + _cyclic.Count == 0 || !_session.CanSend || !_session.IsLocalSceneOwner ||
            Time.unscaledTime < _nextSend || !OthersInScene()) {
            return;
        }

        _nextSend = Time.unscaledTime + SendInterval;
        Send();
        SendCycles();
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

    /// <summary>Para el modo de prueba: cuántos se siguen y en qué punto van los que están animándose.</summary>
    public string Describe() {
        var builder = new System.Text.StringBuilder($"peligros animados: {_hazards.Count}, con esperas: {_cyclic.Count} " +
                                                    $"a las {System.DateTime.UtcNow:HH:mm:ss.fff}");
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
