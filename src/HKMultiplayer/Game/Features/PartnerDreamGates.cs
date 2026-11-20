using System;
using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// El Portal Onírico de cada compañero se ve en su sala. En el juego, al cargar una sala, <c>SceneManager</c> crea el
/// portal del jugador local si está en ella (<c>dreamGateScene</c>, <c>dreamGateX</c>, <c>dreamGateY</c>, que son de cada
/// personaje): el de un compañero solo se veía un momento al ponerlo (como efecto de su caballero) y al volver a la sala
/// ya no estaba.
/// <para>
/// Cada jugador avisa a todos (fiable) de dónde tiene su portal cuando cambia y cuando entra alguien. En cada PC, en la
/// sala del portal de un compañero, se crea una copia del mismo objeto del juego en el mismo sitio que usa
/// <c>SceneManager</c>. Es solo visual: viajar con él sigue siendo cosa de su dueño.
/// </para>
/// </summary>
internal sealed class PartnerDreamGates {
    /// <summary>Dónde tiene su portal un jugador (jugador → todos, fiable).</summary>
    public const MessageType DreamGateInfo = (MessageType) 119;

    /// <summary>Cada cuánto se mira si cambió el portal propio.</summary>
    private const float CheckInterval = 1f;

    /// <summary>Lo que <c>SceneManager</c> baja el portal respecto a donde estaba el caballero al ponerlo.</summary>
    private const float GateDrop = 1.429361f;

    private const float GateZ = -0.002f;

    /// <summary>Límite de las coordenadas recibidas (las salas más grandes no llegan a 1000).</summary>
    private const float MaxCoordinate = 10000f;

    private sealed class Gate {
        public string Scene = string.Empty;
        public float X;
        public float Y;
        public GameObject Copy;
        public string CopyScene = string.Empty;
    }

    private readonly Dictionary<ushort, Gate> _gates = new();
    private readonly MessageHandler _onInfo;
    private readonly Action<SessionPlayer> _onJoined;
    private readonly Action<SessionPlayer> _onLeft;
    private GameSession _session;
    private float _nextCheck;
    private bool _resend;
    private string _sentScene;
    private float _sentX;
    private float _sentY;

    static PartnerDreamGates() {
        MessageRoutes.Register(DreamGateInfo, Route.Everyone, reliable: true);
    }

    public PartnerDreamGates() {
        _onInfo = OnInfo;
        _onJoined = _ => _resend = true;
        _onLeft = player => {
            if (_gates.TryGetValue(player.Id, out var gate)) {
                DestroyCopy(gate);
                _gates.Remove(player.Id);
            }
        };
    }

    /// <summary>Portales de compañeros que se ven ahora en la sala (diagnóstico).</summary>
    public int Shown {
        get {
            var shown = 0;
            foreach (var gate in _gates.Values) {
                shown += gate.Copy != null ? 1 : 0;
            }

            return shown;
        }
    }

    public void Attach(GameSession session) {
        _session = session;
        _sentScene = null;
        _resend = true;
        session.On(DreamGateInfo, _onInfo);
        session.PlayerJoined += _onJoined;
        session.PlayerLeft += _onLeft;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(DreamGateInfo, null);
            _session.PlayerJoined -= _onJoined;
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        foreach (var gate in _gates.Values) {
            DestroyCopy(gate);
        }

        _gates.Clear();
    }

    /// <summary>Llamar cada frame con la sala local: avisa del portal propio y pone o quita las copias.</summary>
    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        var now = Time.unscaledTime;
        if (now >= _nextCheck) {
            _nextCheck = now + CheckInterval;
            SendLocal();
        }

        var gameplay = scene.Length > 0 && GameManager.instance != null && GameManager.instance.IsGameplayScene();
        foreach (var gate in _gates.Values) {
            var wanted = gameplay && gate.Scene.Length > 0 && gate.Scene == scene;
            if (!wanted) {
                DestroyCopy(gate);
                continue;
            }

            // La copia se va con la sala al descargarla (queda en null) y se vuelve a crear al entrar otra vez
            if (gate.Copy == null || gate.CopyScene != scene) {
                Spawn(gate, scene);
            }
        }
    }

    private void SendLocal() {
        var data = PlayerData.instance;
        if (data == null || !_session.CanSend) {
            return;
        }

        var scene = data.GetString("dreamGateScene") ?? string.Empty;
        var x = data.GetFloat("dreamGateX");
        var y = data.GetFloat("dreamGateY");
        if (!_resend && scene == _sentScene && Mathf.Approximately(x, _sentX) && Mathf.Approximately(y, _sentY)) {
            return;
        }

        _resend = false;
        _sentScene = scene;
        _sentX = x;
        _sentY = y;
        var writer = _session.Begin(DreamGateInfo);
        writer.WriteString(scene);
        writer.WriteFloat(x);
        writer.WriteFloat(y);
        _session.End();
    }

    private void OnInfo(SessionPlayer from, PacketReader reader) {
        if (from.IsLocal) {
            return;
        }

        var scene = reader.ReadString(128) ?? string.Empty;
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        if (!IsValid(x) || !IsValid(y)) {
            Log.Warn($"Portal onírico de {from.Name} con posición no válida: se ignora");
            return;
        }

        if (!_gates.TryGetValue(from.Id, out var gate)) {
            gate = _gates[from.Id] = new Gate();
        }

        if (gate.Scene == scene && Mathf.Approximately(gate.X, x) && Mathf.Approximately(gate.Y, y)) {
            return;
        }

        // Lo puso en otro sitio: la copia de antes se quita y la nueva sale en el siguiente Update
        DestroyCopy(gate);
        gate.Scene = scene;
        gate.X = x;
        gate.Y = y;
        Log.Info(scene.Length > 0
            ? $"Portal onírico de {from.Name} en {scene} ({x:0.0}, {y:0.0})"
            : $"{from.Name} ya no tiene portal onírico");
    }

    private static bool IsValid(float value) => !float.IsNaN(value) && Mathf.Abs(value) <= MaxCoordinate;

    private static void Spawn(Gate gate, string scene) {
        DestroyCopy(gate);
        var prefab = GameManager.instance != null && GameManager.instance.sm != null
            ? GameManager.instance.sm.dreamgateObject
            : null;
        if (prefab == null) {
            return;
        }

        gate.Copy = Object.Instantiate(prefab, new Vector3(gate.X, gate.Y - GateDrop, GateZ), Quaternion.identity);
        gate.Copy.name = "HKMultiplayer portal onírico del compañero";
        gate.CopyScene = scene;
    }

    private static void DestroyCopy(Gate gate) {
        if (gate.Copy != null) {
            Object.Destroy(gate.Copy);
        }

        gate.Copy = null;
        gate.CopyScene = string.Empty;
    }
}
