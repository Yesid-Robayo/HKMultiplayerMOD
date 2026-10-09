using System;
using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Vida y alma de cada jugador para el HUD del compañero. Cada uno envía las suyas al host cuando cambian (como mucho 4
/// veces por segundo, fiable); el host las guarda y las reenvía a los demás, y a quien entra le da las últimas de cada
/// uno. Sin memoria nueva por mensaje.
/// </summary>
internal sealed class Vitals {
    /// <summary>Vida y alma de un jugador (jugador → host → los demás). Lleva delante de quién son.</summary>
    public const MessageType VitalsMessage = (MessageType) 81;

    private const float MinInterval = 0.25f;

    /// <summary>Lo que se sabe de un jugador.</summary>
    public sealed class Entry {
        public ushort Id;
        public byte Health;
        public byte MaxHealth;
        public byte Blue;
        public byte Soul;
        public byte MaxSoul;
        public byte Reserve;

        /// <summary>Zona del mapa en la que está (<see cref="GlobalEnums.MapZone"/>).</summary>
        public byte Zone;

        /// <summary>Cuándo cambió su vida por última vez (para mostrar sus máscaras sobre su cabeza un rato).</summary>
        public float HealthChangedAt = -100f;

        public bool Known;

        public bool SameAs(Entry other) =>
            Health == other.Health && MaxHealth == other.MaxHealth && Blue == other.Blue && Soul == other.Soul &&
            MaxSoul == other.MaxSoul && Reserve == other.Reserve && Zone == other.Zone;

        public void CopyFrom(Entry other) {
            Health = other.Health;
            MaxHealth = other.MaxHealth;
            Blue = other.Blue;
            Soul = other.Soul;
            MaxSoul = other.MaxSoul;
            Reserve = other.Reserve;
            Zone = other.Zone;
        }

        public void Write(PacketWriter writer) {
            writer.WriteByte(Health);
            writer.WriteByte(MaxHealth);
            writer.WriteByte(Blue);
            writer.WriteByte(Soul);
            writer.WriteByte(MaxSoul);
            writer.WriteByte(Reserve);
            writer.WriteByte(Zone);
        }

        public void Read(PacketReader reader) {
            Health = reader.ReadByte();
            MaxHealth = reader.ReadByte();
            Blue = reader.ReadByte();
            Soul = reader.ReadByte();
            MaxSoul = reader.ReadByte();
            Reserve = reader.ReadByte();
            Zone = reader.ReadByte();
        }
    }

    private readonly Dictionary<ushort, Entry> _entries = new();

    /// <summary>Jugadores remotos conectados (el host les reenvía; una lista para no crear memoria al recorrerla).</summary>
    private readonly List<SessionPlayer> _remotes = new();
    private readonly Entry _local = new();
    private readonly Entry _sample = new();
    private readonly Entry _incoming = new();
    private readonly MessageHandler _onVitals;
    private readonly Action<SessionPlayer> _onJoined;
    private readonly Action<SessionPlayer> _onLeft;
    private GameSession _session;
    private float _nextSample;
    private bool _sentOnce;

    static Vitals() {
        MessageRoutes.Register(VitalsMessage, Route.Server, reliable: true);
    }

    public Vitals() {
        _onVitals = OnVitals;
        _onJoined = OnPlayerJoined;
        _onLeft = player => {
            _entries.Remove(player.Id);
            _remotes.Remove(player);
        };
    }

    /// <summary>
    /// Zona del mapa de la sala actual (la de la sala, no <c>PlayerData.mapZone</c>, que es la de reaparecer).
    /// </summary>
    public static byte CurrentZone() {
        var gm = GameManager.instance;
        if (gm != null && gm.sm != null) {
            return (byte) gm.sm.mapZone;
        }

        return PlayerData.instance != null ? (byte) PlayerData.instance.mapZone : (byte) 0;
    }

    /// <summary>Lo último de un jugador remoto (null si aún no llegó nada).</summary>
    public Entry Get(ushort id) => _entries.TryGetValue(id, out var entry) && entry.Known ? entry : null;

    public void Attach(GameSession session) {
        _session = session;
        _entries.Clear();
        _remotes.Clear();
        _sentOnce = false;
        session.On(VitalsMessage, _onVitals);
        session.PlayerJoined += _onJoined;
        session.PlayerLeft += _onLeft;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(VitalsMessage, null);
            _session.PlayerJoined -= _onJoined;
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        _entries.Clear();
    }

    /// <summary>Llamar cada frame: si cambió la vida o el alma del caballero local, se envía.</summary>
    public void Update() {
        var now = Time.unscaledTime;
        if (_session == null || !_session.CanSend || now < _nextSample) {
            return;
        }

        var pd = PlayerData.instance;
        if (pd == null || HeroController.instance == null) {
            return;
        }

        _nextSample = now + MinInterval;
        _sample.Health = (byte) Mathf.Clamp(pd.health, 0, 255);
        _sample.MaxHealth = (byte) Mathf.Clamp(pd.maxHealth, 0, 255);
        _sample.Blue = (byte) Mathf.Clamp(pd.healthBlue, 0, 255);
        _sample.Soul = (byte) Mathf.Clamp(pd.MPCharge, 0, 255);
        _sample.MaxSoul = (byte) Mathf.Clamp(pd.maxMP, 0, 255);
        _sample.Reserve = (byte) Mathf.Clamp(pd.MPReserve, 0, 255);
        _sample.Zone = CurrentZone();
        if (_sentOnce && _sample.SameAs(_local)) {
            return;
        }

        _local.CopyFrom(_sample);
        _sentOnce = true;
        var writer = _session.Begin(VitalsMessage);
        writer.WriteUShort(_session.LocalId);
        _local.Write(writer);
        _session.End();
    }

    private void OnVitals(SessionPlayer from, PacketReader reader) {
        var subject = reader.ReadUShort();
        _incoming.Read(reader);
        if (_session == null) {
            return;
        }

        // En el host, de quién son lo dice la conexión; en un invitado, solo vale lo que reenvía el host
        if (_session.IsHost) {
            subject = from.Id;
        } else if (from.Id != GameSession.HostPlayerId) {
            return;
        }

        if (subject != _session.LocalId) {
            Store(subject, _incoming);
        }

        if (!_session.IsHost) {
            return;
        }

        // El host lo reenvía a los demás
        foreach (var player in _remotes) {
            if (player.Id == subject) {
                continue;
            }

            var writer = _session.BeginTo(player, VitalsMessage);
            writer.WriteUShort(subject);
            _incoming.Write(writer);
            _session.EndTo();
        }
    }

    private void Store(ushort id, Entry data) {
        if (!_entries.TryGetValue(id, out var entry)) {
            entry = new Entry { Id = id };
            _entries[id] = entry;
        }

        if (entry.Known && (entry.Health != data.Health || entry.Blue != data.Blue)) {
            entry.HealthChangedAt = Time.unscaledTime;
        }

        entry.CopyFrom(data);
        entry.Known = true;
    }

    /// <summary>Quien entra recibe del host las últimas de cada uno (las del host incluidas).</summary>
    private void OnPlayerJoined(SessionPlayer player) {
        if (_session == null || !_session.IsHost || player.Connection == null) {
            return;
        }

        _remotes.Add(player);
        if (_sentOnce) {
            var writer = _session.BeginTo(player, VitalsMessage);
            writer.WriteUShort(_session.LocalId);
            _local.Write(writer);
            _session.EndTo();
        }

        foreach (var entry in _entries.Values) {
            if (!entry.Known || entry.Id == player.Id) {
                continue;
            }

            var writer = _session.BeginTo(player, VitalsMessage);
            writer.WriteUShort(entry.Id);
            entry.Write(writer);
            _session.EndTo();
        }
    }
}
