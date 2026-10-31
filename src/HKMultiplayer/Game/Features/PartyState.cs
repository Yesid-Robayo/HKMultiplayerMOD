using System;
using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;

namespace HKMultiplayer.Game.Features;

/// <summary>Cómo está un jugador de la partida.</summary>
internal enum PlayerStatus : byte {
    /// <summary>Jugando con normalidad.</summary>
    Standing = 0,

    /// <summary>Caído: espera a que lo revivan (su cuerpo queda en una sala).</summary>
    Downed = 1,

    /// <summary>Viendo una cinemática compartida.</summary>
    Cutscene = 2,

    /// <summary>Dentro de un sueño.</summary>
    Dream = 3,

    /// <summary>
    /// Viajando en ciervo: la sala del viaje (<c>Cinematic_Stag_travel</c>) no es de juego ni de ninguna zona del mapa, así
    /// que su sala y su zona no dicen nada (el HUD de los demás ponía "en None").
    /// </summary>
    StagTravel = 4,

    /// <summary>
    /// Entrando en la partida: cargando el mundo, viendo la introducción o en el menú. Antes contaba como "en pie": si
    /// alguien moría mientras un invitado nuevo veía la introducción (1-2 minutos), caía en vez de morir y seguía a quien
    /// no estaba.
    /// </summary>
    Joining = 5
}

/// <summary>
/// Estado de cada jugador de la partida (en pie, caído, en cinemática, en sueño). Lo decide el host y lo reparte entero
/// a todos con <see cref="PartyStatus"/> cada vez que cambia y a quien entra. Las demás capas lo consultan: un caído no
/// cuenta para repartir enemigos, ni para las esperas de los combates ("Esperando a ..."), ni para los diálogos.
/// </summary>
internal sealed class PartyState {
    /// <summary>Estado de todos (host → todos).</summary>
    public const MessageType PartyStatus = (MessageType) 74;

    /// <summary>
    /// Qué hace el jugador cuando no está caído: en pie, viendo una cinemática compartida o en un sueño (jugador → host).
    /// </summary>
    public const MessageType ActivityReport = (MessageType) 91;

    /// <summary>Lo que se sabe de un jugador.</summary>
    public sealed class Member {
        public ushort Id;
        public string Name = string.Empty;

        /// <summary>Su jugador de la sesión (sala en la que está, si es el local...).</summary>
        public SessionPlayer Player;

        public PlayerStatus Status;

        /// <summary>Caído: sala y posición de su cuerpo.</summary>
        public string BodyScene = string.Empty;

        public float BodyX;
        public float BodyY;

        /// <summary>Caído: hacia dónde miraba al caer (su cuerpo mira igual en todos los PCs).</summary>
        public bool BodyFacingRight;

        /// <summary>Caído: el compañero en pie al que sigue su cámara.</summary>
        public ushort Partner;

        /// <summary>
        /// Lo último que informó el jugador de sí mismo (en pie, en cinemática o en sueño). Es su estado mientras no está
        /// caído: al revivir dentro de un sueño vuelve a "en sueño", no a "en pie".
        /// </summary>
        public PlayerStatus Activity;

        /// <summary>
        /// Con el menú de pausa abierto: su juego está parado (escala de tiempo 0), así que no puede mover enemigos (se
        /// quedarían congelados para los demás). Se lo dice él mismo al host, como lo que hace.
        /// </summary>
        public bool Paused;
    }

    private static PartyState _current;

    private readonly Dictionary<ushort, Member> _members = new();

    /// <summary>Los mismos, en una lista (para recorrerlos sin crear memoria).</summary>
    private readonly List<Member> _list = new();

    private readonly MessageHandler _onStatus;
    private readonly MessageHandler _onActivity;
    private GameSession _session;

    /// <summary>Lo último que el jugador local informó de sí mismo (se envía solo al cambiar).</summary>
    private PlayerStatus _reportedActivity;

    private bool _reportedPaused;

    /// <summary>Cambió el estado de un jugador (jugador y estado anterior).</summary>
    public event Action<Member, PlayerStatus> StatusChanged;

    static PartyState() {
        MessageRoutes.Register(PartyStatus, Route.Server, reliable: true);
        MessageRoutes.Register(ActivityReport, Route.Server, reliable: true);
    }

    public PartyState() {
        _onStatus = OnStatus;
        _onActivity = OnActivity;
    }

    /// <summary>Estado de la partida en curso (null sin partida).</summary>
    public static PartyState Current => _current;

    /// <summary>Estado del jugador (en pie si no se conoce).</summary>
    public static PlayerStatus StatusOf(ushort id) =>
        _current != null && _current._members.TryGetValue(id, out var member) ? member.Status : PlayerStatus.Standing;

    /// <summary>Si el jugador está caído.</summary>
    public static bool IsDown(ushort id) =>
        _current != null && _current._members.TryGetValue(id, out var member) && member.Status == PlayerStatus.Downed;

    /// <summary>Si el jugador tiene el juego en pausa.</summary>
    public static bool IsPaused(ushort id) =>
        _current != null && _current._members.TryGetValue(id, out var member) && member.Paused;

    /// <summary>
    /// Si el jugador no puede mover enemigos: caído o con el juego en pausa. Los suyos pasan enseguida a otro de la sala.
    /// </summary>
    public static bool IsInactive(ushort id) =>
        _current != null && _current._members.TryGetValue(id, out var member) &&
        (member.Status == PlayerStatus.Downed || member.Paused);

    /// <summary>Jugadores conocidos (incluido el local).</summary>
    public List<Member> Members => _list;

    public Member Get(ushort id) => _members.TryGetValue(id, out var member) ? member : null;

    public Member Local => _session != null ? Get(_session.LocalId) : null;

    public void Attach(GameSession session) {
        _session = session;
        _current = this;
        _members.Clear();
        _list.Clear();
        _reportedActivity = PlayerStatus.Standing;
        _reportedPaused = false;
        session.On(PartyStatus, _onStatus);
        session.On(ActivityReport, _onActivity);
        session.PlayerJoined += OnPlayerJoined;
        session.PlayerLeft += OnPlayerLeft;
        session.Connected += OnConnected;
        foreach (var player in session.Players) {
            Add(player);
        }
    }

    public void Detach() {
        if (_session != null) {
            _session.On(PartyStatus, null);
            _session.On(ActivityReport, null);
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.Connected -= OnConnected;
            _session = null;
        }

        if (_current == this) {
            _current = null;
        }

        _members.Clear();
        _list.Clear();
    }

    private Member Add(SessionPlayer player) {
        if (_members.TryGetValue(player.Id, out var member)) {
            member.Name = player.Name;
            member.Player = player;
            return member;
        }

        member = new Member { Id = player.Id, Name = player.Name, Player = player };
        _members[player.Id] = member;
        _list.Add(member);
        return member;
    }

    private void OnConnected() {
        // El cliente ya sabe su id: se apunta a sí mismo
        var local = _session?.LocalPlayer;
        if (local != null) {
            Add(local);
        }
    }

    private void OnPlayerJoined(SessionPlayer player) {
        Add(player);
        // Quien entra recibe cómo está cada uno (los caídos, con su cuerpo)
        if (_session != null && _session.IsHost && player.Connection != null) {
            SendTo(player);
        }
    }

    private void OnPlayerLeft(SessionPlayer player) {
        if (_members.TryGetValue(player.Id, out var member)) {
            _members.Remove(player.Id);
            _list.Remove(member);
        }
    }

    // =====================================================================================================
    // Host
    // =====================================================================================================

    /// <summary>
    /// El host cambia el estado de un jugador y lo reparte a todos. Para un caído, dónde está su cuerpo y a quién sigue.
    /// </summary>
    public void SetStatus(ushort id, PlayerStatus status, string bodyScene = null, float x = 0f, float y = 0f,
        ushort partner = 0, bool facingRight = false) {
        if (_session == null || !_session.IsHost || !_members.TryGetValue(id, out var member)) {
            return;
        }

        // Quien deja de estar caído vuelve a lo que estaba haciendo (p. ej. revivido dentro de un sueño)
        if (status == PlayerStatus.Standing) {
            status = member.Activity;
        }

        var previous = member.Status;
        member.Status = status;
        member.BodyScene = status == PlayerStatus.Downed ? bodyScene ?? string.Empty : string.Empty;
        member.BodyX = x;
        member.BodyY = y;
        member.BodyFacingRight = facingRight;
        member.Partner = status == PlayerStatus.Downed ? partner : (ushort) 0;
        Broadcast();
        StatusChanged?.Invoke(member, previous);
    }

    /// <summary>El host mueve el cuerpo de un caído (lo que tenía debajo desapareció).</summary>
    public void SetBody(ushort id, float x, float y) {
        if (_session == null || !_session.IsHost || !_members.TryGetValue(id, out var member) ||
            member.Status != PlayerStatus.Downed) {
            return;
        }

        member.BodyX = x;
        member.BodyY = y;
        Broadcast();
    }

    /// <summary>El host cambia solo a quién sigue un caído (su compañero se fue o cayó).</summary>
    public void SetPartner(ushort id, ushort partner) {
        if (_session == null || !_session.IsHost || !_members.TryGetValue(id, out var member) ||
            member.Status != PlayerStatus.Downed || member.Partner == partner) {
            return;
        }

        member.Partner = partner;
        Broadcast();
        StatusChanged?.Invoke(member, member.Status);
    }

    /// <summary>
    /// Lo que hace el jugador local cuando no está caído (en pie, en cinemática o en sueño) y si tiene el juego en pausa.
    /// Barato: llamar cada frame; solo se envía al host cuando cambia.
    /// </summary>
    public void ReportLocal(PlayerStatus activity, bool paused) {
        if (_session == null || !_session.CanSend || activity == PlayerStatus.Downed ||
            activity == _reportedActivity && paused == _reportedPaused) {
            return;
        }

        _reportedActivity = activity;
        _reportedPaused = paused;
        var writer = _session.Begin(ActivityReport);
        writer.WriteByte((byte) activity);
        writer.WriteBool(paused);
        _session.End();
    }

    private void OnActivity(SessionPlayer from, PacketReader reader) {
        var activity = (PlayerStatus) reader.ReadByte();
        var paused = reader.ReadBool();
        if (_session == null || !_session.IsHost || activity == PlayerStatus.Downed || activity > PlayerStatus.Joining ||
            !_members.TryGetValue(from.Id, out var member)) {
            return;
        }

        member.Activity = activity;
        // Un caído sigue caído: lo que hace cuenta al levantarse
        if (member.Status != PlayerStatus.Downed && member.Status != activity) {
            member.Paused = paused;
            SetStatus(from.Id, activity);
        } else if (member.Paused != paused) {
            member.Paused = paused;
            Log.Info($"{member.Name}: {(paused ? "en pausa" : "sin pausa")}");
            Broadcast();
        }
    }

    private void Broadcast() {
        foreach (var player in _session.Players) {
            if (player.Connection != null) {
                SendTo(player);
            }
        }
    }

    private void SendTo(SessionPlayer target) {
        var writer = _session.BeginTo(target, PartyStatus);
        writer.WriteByte((byte) _list.Count);
        foreach (var member in _list) {
            writer.WriteUShort(member.Id);
            writer.WriteByte((byte) member.Status);
            writer.WriteUShort(member.Partner);
            writer.WriteString(member.BodyScene);
            writer.WriteFloat(member.BodyX);
            writer.WriteFloat(member.BodyY);
            writer.WriteBool(member.BodyFacingRight);
            writer.WriteBool(member.Paused);
        }

        _session.EndTo();
    }

    // =====================================================================================================
    // Cliente
    // =====================================================================================================

    private void OnStatus(SessionPlayer from, PacketReader reader) {
        // Solo lo decide el host
        if (_session == null || _session.IsHost || from.Id != GameSession.HostPlayerId) {
            return;
        }

        var count = reader.ReadByte();
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUShort();
            var status = (PlayerStatus) reader.ReadByte();
            var partner = reader.ReadUShort();
            var scene = reader.ReadString(128);
            var x = reader.ReadFloat();
            var y = reader.ReadFloat();
            var facingRight = reader.ReadBool();
            var paused = reader.ReadBool();
            if (!_members.TryGetValue(id, out var member)) {
                continue;
            }

            member.Paused = paused;

            var previous = member.Status;
            var changed = previous != status || member.Partner != partner || member.BodyScene != scene;
            member.Status = status;
            member.Partner = partner;
            member.BodyScene = scene;
            member.BodyX = x;
            member.BodyY = y;
            member.BodyFacingRight = facingRight;
            if (changed) {
                Log.Info($"{member.Name}: {Describe(status)}");
                StatusChanged?.Invoke(member, previous);
            }
        }
    }

    public static string Describe(PlayerStatus status) => status switch {
        PlayerStatus.Downed => "caído",
        PlayerStatus.Cutscene => "en una cinemática",
        PlayerStatus.Dream => "en un sueño",
        PlayerStatus.StagTravel => "viajando en ciervo",
        PlayerStatus.Joining => "entrando en la partida",
        _ => "en pie"
    };

    /// <summary>Resumen para el modo de prueba.</summary>
    public string DescribeAll() {
        var text = new System.Text.StringBuilder();
        foreach (var member in _list) {
            text.Append($"{member.Id}:{member.Name}={Describe(member.Status)}{(member.Paused ? " (en pausa)" : "")}");
            if (member.Status == PlayerStatus.Downed) {
                text.Append($"@{member.BodyScene}({member.BodyX:0.0},{member.BodyY:0.0}) sigue a {member.Partner}");
            }

            text.Append("; ");
        }

        return text.ToString();
    }
}
