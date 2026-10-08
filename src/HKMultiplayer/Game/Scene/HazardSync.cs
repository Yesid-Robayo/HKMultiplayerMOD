using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Peligros y piezas animados de la sala (sierras del Palacio Blanco y del Camino del Dolor, paredes de pinchos que
/// entran y salen): se mueven con una animación en bucle que empieza al cargar la sala, así que cada PC llevaría su
/// propio ritmo y el compañero se vería atravesando sierras que en la pantalla de otro no estaban. El dueño de la sala
/// envía cada segundo en qué punto va cada una (estado y tiempo de la animación) y los demás se ajustan si se desvían.
/// El daño sigue siendo de cada PC; solo se alinea lo que se ve. No crea memoria por envío.
/// </summary>
internal sealed class HazardSync {
    public const MessageType Phases = (MessageType) 70;

    private const float SendInterval = 1f;
    private const float RescanDelay = 1.5f;
    private const int MaxHazards = 160;

    /// <summary>Cada uno ocupa 12 bytes: así cabe de sobra en un mensaje.</summary>
    private const int PerMessage = 60;

    /// <summary>Desfase (en fracción de vuelta) a partir del cual se corrige: menos no se nota y saltaría a cada rato.</summary>
    private const float Tolerance = 0.03f;

    private readonly List<(uint Id, Animator Animator)> _hazards = new();
    private readonly Dictionary<uint, Animator> _byId = new();
    private readonly MessageHandler _onPhases;
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;
    private float _nextSend;

    static HazardSync() {
        MessageRoutes.Register(Phases, Route.SameScene, reliable: false);
    }

    public HazardSync() {
        _onPhases = OnPhases;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(Phases, _onPhases);
        Clear();
    }

    public void Detach() {
        _session?.On(Phases, null);
        _session = null;
        Clear();
    }

    private void Clear() {
        _hazards.Clear();
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

        if (_hazards.Count == 0 || !_session.CanSend || !_session.IsLocalSceneOwner || Time.unscaledTime < _nextSend ||
            !OthersInScene()) {
            return;
        }

        _nextSend = Time.unscaledTime + SendInterval;
        Send();
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
        var builder = new System.Text.StringBuilder($"peligros animados: {_hazards.Count}");
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
