using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Mensajes rápidos y marcas para el compañero, con teclado o mando: con el mapa rápido pulsado (que ya para al caballero),
/// cada dirección envía un mensaje a todos.
/// <list type="bullet">
/// <item>Arriba, «¡Aquí!»: marca el sitio donde está quien lo envía.</item>
/// <item>Derecha, «¡A por él!»: marca el enemigo más cercano (la marca lo sigue mientras vive).</item>
/// <item>Abajo, «¡Ayuda!», e izquierda, «¡Espera!»: solo el aviso.</item>
/// </list>
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
        MessageRoutes.Register(QuickMessage, Route.Everyone, reliable: true);
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

    /// <summary>Cada frame: lee la combinación del mapa rápido y mueve o quita las marcas.</summary>
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

    private void ReadInput() {
        var input = InputHandler.Instance;
        var hero = HeroController.instance;
        var gm = GameManager.instance;
        if (input == null || hero == null || gm == null || gm.isPaused || hero.cState.transitioning ||
            !input.inputActions.quickMap.IsPressed) {
            return;
        }

        var actions = input.inputActions;
        var kind = actions.up.WasPressed ? Here
            : actions.right.WasPressed ? Attack
            : actions.down.WasPressed ? Help
            : actions.left.WasPressed ? Wait
            : byte.MaxValue;
        if (kind != byte.MaxValue) {
            Send(kind);
        }
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
