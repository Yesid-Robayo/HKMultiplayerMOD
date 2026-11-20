using System;
using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Las marcas que cada jugador pone en su mapa (las cuatro chinchetas de colores, <c>placedMarkers_r/_b/_y/_w</c>, que son
/// de cada personaje) se ven también en el mapa de los compañeros, con «Mapa compartido» activado.
/// <para>
/// Cada jugador envía a todos (fiable) sus cuatro listas cuando cambian y cuando entra alguien. Al abrir el mapa, después
/// de que el juego coloque las propias (<c>GameMap.SetupMapMarkers</c>), se añaden copias de las mismas chinchetas del
/// juego en los sitios de los compañeros. Son solo para ver: no gastan chinchetas de nadie ni se pueden quitar.
/// </para>
/// </summary>
internal sealed class SharedMapMarkers {
    /// <summary>Las marcas del mapa de un jugador (jugador → todos, fiable): cuatro listas de puntos.</summary>
    public const MessageType MapMarkers = (MessageType) 124;

    private const float CheckInterval = 1f;

    /// <summary>Como mucho las que caben en el juego por color (y un margen): lo recibido no puede crear miles de objetos.</summary>
    private const int MaxPerColor = 32;

    private const float MaxCoordinate = 1000f;

    private static readonly string[] Lists = { "placedMarkers_r", "placedMarkers_b", "placedMarkers_y", "placedMarkers_w" };

    private static bool _hooked;
    private static SharedMapMarkers _active;

    private readonly MessageHandler _onMarkers;
    private readonly Action<SessionPlayer> _onJoined;
    private readonly Action<SessionPlayer> _onLeft;

    /// <summary>Las marcas de cada compañero, por color.</summary>
    private readonly Dictionary<ushort, List<Vector3>[]> _partners = new();

    private readonly List<GameObject> _copies = new();
    private readonly List<Vector3>[] _sent = { new(), new(), new(), new() };
    private GameSession _session;
    private float _nextCheck;
    private bool _resend;

    static SharedMapMarkers() {
        MessageRoutes.Register(MapMarkers, Route.Everyone, reliable: true);
    }

    public SharedMapMarkers() {
        _onMarkers = OnMarkers;
        _onJoined = _ => _resend = true;
        _onLeft = player => _partners.Remove(player.Id);
    }

    /// <summary>Marcas de compañeros puestas en el mapa ahora (diagnóstico).</summary>
    public int Shown => _copies.Count;

    public void Attach(GameSession session) {
        _session = session;
        _active = this;
        _resend = true;
        session.On(MapMarkers, _onMarkers);
        session.PlayerJoined += _onJoined;
        session.PlayerLeft += _onLeft;
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.GameMap.SetupMapMarkers += (orig, self) => {
            orig(self);
            _active?.AddPartnerMarkers(self);
        };
    }

    public void Detach() {
        if (_session != null) {
            _session.On(MapMarkers, null);
            _session.PlayerJoined -= _onJoined;
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        if (_active == this) {
            _active = null;
        }

        DestroyCopies();
        _partners.Clear();
        foreach (var list in _sent) {
            list.Clear();
        }
    }

    /// <summary>Cada frame: si cambiaron las marcas propias, se envían.</summary>
    public void Update() {
        var now = Time.unscaledTime;
        var pd = PlayerData.instance;
        if (_session == null || !_session.CanSend || pd == null || now < _nextCheck) {
            return;
        }

        _nextCheck = now + CheckInterval;
        var changed = _resend;
        for (var i = 0; i < Lists.Length; i++) {
            var list = pd.GetVariable<List<Vector3>>(Lists[i]);
            if (list == null || Same(list, _sent[i])) {
                continue;
            }

            _sent[i].Clear();
            _sent[i].AddRange(list);
            changed = true;
        }

        if (!changed) {
            return;
        }

        _resend = false;
        var writer = _session.Begin(MapMarkers);
        foreach (var list in _sent) {
            var count = Mathf.Min(list.Count, MaxPerColor);
            writer.WriteByte((byte) count);
            for (var i = 0; i < count; i++) {
                writer.WriteFloat(list[i].x);
                writer.WriteFloat(list[i].y);
                writer.WriteFloat(list[i].z);
            }
        }

        _session.End();
    }

    private static bool Same(List<Vector3> a, List<Vector3> b) {
        if (a.Count != b.Count) {
            return false;
        }

        for (var i = 0; i < a.Count; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }

    private void OnMarkers(SessionPlayer from, PacketReader reader) {
        if (from.IsLocal) {
            return;
        }

        var lists = new List<Vector3>[Lists.Length];
        for (var c = 0; c < Lists.Length; c++) {
            var count = Mathf.Min(reader.ReadByte(), (byte) MaxPerColor);
            lists[c] = new List<Vector3>(count);
            for (var i = 0; i < count; i++) {
                var point = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
                if (Valid(point.x) && Valid(point.y) && Valid(point.z)) {
                    lists[c].Add(point);
                }
            }
        }

        _partners[from.Id] = lists;
        Log.Info($"Marcas del mapa de {from.Name}: {lists[0].Count + lists[1].Count + lists[2].Count + lists[3].Count}");
    }

    private static bool Valid(float value) => !float.IsNaN(value) && Mathf.Abs(value) <= MaxCoordinate;

    /// <summary>Después de colocar las propias: copias de las mismas chinchetas en los sitios de los compañeros.</summary>
    private void AddPartnerMarkers(GameMap map) {
        DestroyCopies();
        if (!HostSettings.Current.SharedMap || map == null) {
            return;
        }

        var templates = new[] { map.mapMarkersRed, map.mapMarkersBlue, map.mapMarkersYellow, map.mapMarkersWhite };
        foreach (var lists in _partners.Values) {
            for (var c = 0; c < Lists.Length; c++) {
                var template = templates[c] != null && templates[c].Length > 0 ? templates[c][0] : null;
                if (template == null) {
                    continue;
                }

                foreach (var point in lists[c]) {
                    var copy = Object.Instantiate(template, template.transform.parent, false);
                    copy.name = "HKMultiplayer marca del compañero";
                    copy.transform.localPosition = point;
                    copy.SetActive(true);
                    _copies.Add(copy);
                }
            }
        }
    }

    private void DestroyCopies() {
        foreach (var copy in _copies) {
            if (copy != null) {
                Object.Destroy(copy);
            }
        }

        _copies.Clear();
    }
}
