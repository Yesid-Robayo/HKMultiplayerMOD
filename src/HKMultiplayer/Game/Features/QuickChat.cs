using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Mensajes rápidos y marcas para el compañero: en el teclado las teclas 1 a 4 y en el mando el stick derecho, que el juego
/// no usa (solo para mover el mapa, y con el mapa abierto no cuentan). Antes eran el mapa rápido más una dirección, y
/// quien abría el mapa mientras se movía los enviaba sin querer (issue #24).
/// <list type="bullet">
/// <item>1 o stick arriba, «¡Aquí!»: marca el sitio donde está quien lo envía.</item>
/// <item>2 o stick a la derecha, «¡A por él!»: marca el enemigo más cercano (la marca lo sigue mientras vive).</item>
/// <item>3 o stick abajo, «¡Ayuda!», y 4 o stick a la izquierda, «¡Espera!»: solo el aviso.</item>
/// </list>
/// Una tecla que el jugador asignó a una acción del juego no envía nada.
/// Todos ven el aviso con el nombre de quien lo envía; los que están en su sala ven además la marca, una flecha del color
/// del jugador (el de su brújula en el mapa) con el mensaje encima, que dura unos segundos.
/// </summary>
internal sealed class QuickChat {
    /// <summary>Un mensaje rápido (jugador → todos): tipo, sala, punto y enemigo marcado.</summary>
    public const MessageType QuickMessage = (MessageType) 122;

    public const byte Here = 0;
    public const byte Help = 1;
    public const byte Wait = 2;
    public const byte Attack = 3;

    /// <summary>Lo que dura una marca y lo que tarda en desvanecerse al final.</summary>
    private const float MarkerSeconds = 6f;

    private const float FadeSeconds = 1f;

    /// <summary>Tiempo mínimo entre dos mensajes del mismo jugador (también para los que llegan).</summary>
    private const float Cooldown = 0.75f;

    /// <summary>Hasta dónde se busca el enemigo a marcar.</summary>
    private const float AttackReach = 14f;

    private const float MaxCoordinate = 10000f;

    private sealed class Marker {
        public GameObject Root;
        public TextMesh Text;
        public Color Color;
        public float Until;
        public uint Enemy;
        public Vector3 Point;
        public ushort Player;
    }

    private readonly MessageHandler _onMessage;
    private readonly List<Marker> _markers = new();
    private readonly Dictionary<ushort, float> _lastFrom = new();
    private GameSession _session;
    private Scene.EnemySync _enemies;
    private Font _font;
    private string _scene = string.Empty;
    private float _nextSend;

    static QuickChat() {
        MessageRoutes.Register(QuickMessage, Route.Everyone, ReliableChannel.Social);
    }

    public QuickChat() {
        _onMessage = OnMessage;
    }

    /// <summary>Mensajes enviados y recibidos (diagnóstico).</summary>
    public int Sent { get; private set; }

    public int Received { get; private set; }

    public int MarkersShown => _markers.Count;

    public void Attach(GameSession session, Scene.EnemySync enemies) {
        _session = session;
        _enemies = enemies;
        session.On(QuickMessage, _onMessage);
    }

    public void Detach() {
        _session?.On(QuickMessage, null);
        _session = null;
        ClearMarkers();
        _lastFrom.Clear();
    }

    public static string Text(byte kind) => kind switch {
        Here => Lang.T("¡Aquí!", "Over here!"),
        Help => Lang.T("¡Ayuda!", "Help!"),
        Wait => Lang.T("¡Espera!", "Wait!"),
        _ => Lang.T("¡A por él!", "Get it!")
    };

    /// <summary>Cada frame: lee las teclas y el stick derecho y mueve o quita las marcas.</summary>
    public void Update(string scene) {
        if (scene != _scene) {
            _scene = scene;
            ClearMarkers();
        }

        if (_session == null) {
            return;
        }

        ReadInput();
        UpdateMarkers();
    }

    /// <summary>Si este PC envía mensajes rápidos (se elige en el menú; los de los demás siempre llegan).</summary>
    public static bool Enabled => HKMultiplayerMod.Settings.QuickMessages;

    /// <summary>Activa o no los mensajes rápidos en este PC (se guarda).</summary>
    public static void SetEnabled(bool enabled) {
        HKMultiplayerMod.Settings.QuickMessages = enabled;
        HKMultiplayerMod.SaveSettings();
        Log.Info($"Mensajes rápidos: {(enabled ? "activados" : "desactivados")}");
    }

    /// <summary>Teclas de cada mensaje (fila de números y teclado numérico), en el orden 1 a 4.</summary>
    private static readonly (KeyCode Row, InControl.Key RowKey, KeyCode Pad, InControl.Key PadKey, byte Kind)[] MessageKeys = {
        (KeyCode.Alpha1, InControl.Key.Key1, KeyCode.Keypad1, InControl.Key.Pad1, Here),
        (KeyCode.Alpha2, InControl.Key.Key2, KeyCode.Keypad2, InControl.Key.Pad2, Attack),
        (KeyCode.Alpha3, InControl.Key.Key3, KeyCode.Keypad3, InControl.Key.Pad3, Help),
        (KeyCode.Alpha4, InControl.Key.Key4, KeyCode.Keypad4, InControl.Key.Pad4, Wait)
    };

    /// <summary>
    /// Cuánto hay que inclinar el stick derecho para enviar, y por debajo de cuánto vuelve a estar en el centro (hay que
    /// soltarlo antes del siguiente: un roce o mantenerlo no envía varios).
    /// </summary>
    private const float StickPush = 0.6f;

    private const float StickRest = 0.3f;

    private bool _stickArmed = true;

    private void ReadInput() {
        var input = InputHandler.Instance;
        var stick = input != null ? input.inputActions.rightStick.Vector : Vector2.zero;
        if (stick.magnitude < StickRest) {
            _stickArmed = true;
        }

        if (!Enabled || input == null || !CanSendNow(input)) {
            return;
        }

        foreach (var (row, rowKey, pad, padKey, kind) in MessageKeys) {
            if (Input.GetKeyDown(row) && !BoundInGame(input, rowKey) || Input.GetKeyDown(pad) && !BoundInGame(input, padKey)) {
                Send(kind);
                return;
            }
        }

        // El stick derecho solo mueve el mapa: con el mapa rápido abierto no cuenta
        if (!_stickArmed || stick.magnitude < StickPush || input.inputActions.quickMap.IsPressed) {
            return;
        }

        _stickArmed = false;
        Send(Mathf.Abs(stick.x) > Mathf.Abs(stick.y)
            ? stick.x > 0f ? Attack : Wait
            : stick.y > 0f ? Here : Help);
    }

    /// <summary>
    /// Solo jugando con el caballero: no en pausa, ni cambiando de sala, ni sin control (inventario, mapa, cinemáticas,
    /// diálogos, banco), ni escribiendo en el chat (un «1» en el texto no es un mensaje).
    /// </summary>
    private static bool CanSendNow(InputHandler input) {
        var hero = HeroController.instance;
        var gm = GameManager.instance;
        var chat = MultiplayerRunner.Instance?.Chat;
        return hero != null && gm != null && !gm.isPaused && !hero.cState.transitioning && !hero.cState.dead &&
               hero.acceptingInput && !hero.controlReqlinquished && (chat == null || !chat.IsOpen) &&
               input.acceptingInput;
    }

    /// <summary>Si el jugador asignó esa tecla a alguna acción del juego (entonces no es un mensaje).</summary>
    private static bool BoundInGame(InputHandler input, InControl.Key key) {
        foreach (var action in input.inputActions.Actions) {
            foreach (var binding in action.Bindings) {
                if (binding is not InControl.KeyBindingSource keys) {
                    continue;
                }

                var combo = keys.Control;
                for (var i = 0; i < combo.IncludeCount; i++) {
                    if (combo.GetInclude(i) == key) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Envía un mensaje rápido del caballero local (también lo usan las órdenes de diagnóstico).</summary>
    public bool Send(byte kind) {
        var hero = HeroController.instance;
        var now = Time.unscaledTime;
        if (_session == null || !_session.CanSend || hero == null || _scene.Length == 0 || now < _nextSend || kind > Attack) {
            return false;
        }

        _nextSend = now + Cooldown;
        var position = hero.transform.position;
        var enemy = kind == Attack && _enemies != null ? _enemies.NearestEnemyId(position, AttackReach) : 0u;
        var writer = _session.Begin(QuickMessage);
        writer.WriteByte(kind);
        writer.WriteString(_scene);
        writer.WriteFloat(position.x);
        writer.WriteFloat(position.y);
        writer.WriteUInt(enemy);
        _session.End();
        Sent++;
        Log.Info($"Mensaje rápido: {Text(kind)}{(enemy != 0 ? $" (enemigo {enemy:x8})" : string.Empty)}");
        // La marca propia también se ve, para saber qué se marcó
        if (kind is Here or Attack) {
            ShowMarker(_session.LocalId, kind, new Vector3(position.x, position.y, 0f), enemy);
        }

        return true;
    }

    private void OnMessage(SessionPlayer from, PacketReader reader) {
        var kind = reader.ReadByte();
        var scene = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        var enemy = reader.ReadUInt();
        var now = Time.unscaledTime;
        if (from.IsLocal || kind > Attack || !Valid(x) || !Valid(y) ||
            _lastFrom.TryGetValue(from.Id, out var last) && now - last < Cooldown * 0.5f) {
            return;
        }

        _lastFrom[from.Id] = now;
        Received++;
        Notice.Show($"{from.Name}: {Text(kind)}", 4f);
        if (scene == _scene && kind is Here or Attack) {
            ShowMarker(from.Id, kind, new Vector3(x, y, 0f), enemy);
        }
    }

    private static bool Valid(float value) => !float.IsNaN(value) && Mathf.Abs(value) <= MaxCoordinate;

    private void ShowMarker(ushort playerId, byte kind, Vector3 point, uint enemy) {
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        _font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (_font == null) {
            return;
        }

        var root = new GameObject("HKMultiplayer marca del compañero");
        var text = root.AddComponent<TextMesh>();
        text.font = _font;
        text.text = $"{Text(kind)}\n▼";
        text.fontSize = 64;
        text.characterSize = 0.1f;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        var color = PartnerMap.ColorOf(playerId);
        text.color = color;
        var renderer = root.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = _font.material;
        var heroRenderer = hero.GetComponent<MeshRenderer>();
        if (heroRenderer != null) {
            renderer.sortingLayerID = heroRenderer.sortingLayerID;
            renderer.sortingOrder = heroRenderer.sortingOrder + 20;
        }

        // Cada jugador tiene una marca a la vez: la nueva sustituye a la anterior
        for (var i = _markers.Count - 1; i >= 0; i--) {
            if (_markers[i].Player == playerId) {
                if (_markers[i].Root != null) {
                    Object.Destroy(_markers[i].Root);
                }

                _markers.RemoveAt(i);
            }
        }

        var marker = new Marker {
            Root = root, Text = text, Color = color, Until = Time.unscaledTime + MarkerSeconds, Enemy = enemy,
            Point = point + new Vector3(0f, 2.4f, 0f), Player = playerId
        };
        _markers.Add(marker);
        Place(marker);
    }

    private void UpdateMarkers() {
        var now = Time.unscaledTime;
        for (var i = _markers.Count - 1; i >= 0; i--) {
            var marker = _markers[i];
            if (marker.Root == null || now >= marker.Until || !Place(marker)) {
                if (marker.Root != null) {
                    Object.Destroy(marker.Root);
                }

                _markers.RemoveAt(i);
                continue;
            }

            var left = marker.Until - now;
            var color = marker.Color;
            color.a = left < FadeSeconds ? left / FadeSeconds : 1f;
            marker.Text.color = color;
        }
    }

    /// <summary>Pone la marca en su sitio (sobre el enemigo si sigue vivo). False si su enemigo ya no está.</summary>
    private bool Place(Marker marker) {
        var bob = Mathf.Sin(Time.unscaledTime * 4f) * 0.15f;
        var z = HeroController.instance != null ? HeroController.instance.transform.position.z - 0.1f : 0f;
        if (marker.Enemy != 0) {
            if (_enemies == null || !_enemies.TryGetEnemyTop(marker.Enemy, out var top)) {
                return false;
            }

            marker.Root.transform.position = new Vector3(top.x, top.y + 0.3f + bob, z);
            return true;
        }

        marker.Root.transform.position = new Vector3(marker.Point.x, marker.Point.y + bob, z);
        return true;
    }

    private void ClearMarkers() {
        foreach (var marker in _markers) {
            if (marker.Root != null) {
                Object.Destroy(marker.Root);
            }
        }

        _markers.Clear();
    }
}
