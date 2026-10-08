using System;
using System.Collections.Generic;
using System.Net;
using HKMultiplayer.Net;
using HKMultiplayer.Net.Transport;

namespace HKMultiplayer.Session;

/// <summary>Un jugador de la partida.</summary>
public sealed class SessionPlayer {
    public ushort Id { get; internal set; }
    public string Name { get; internal set; } = string.Empty;

    /// <summary>Sala en la que está (vacía si aún no se sabe).</summary>
    public string Scene { get; internal set; } = string.Empty;

    /// <summary>Si es el jugador de este PC.</summary>
    public bool IsLocal { get; internal set; }

    /// <summary>Conexión (solo en el servidor y para jugadores remotos).</summary>
    internal NetConnection Connection;

    /// <summary>Latencia con el host en milisegundos (0 para el host).</summary>
    public int Ping { get; internal set; }

    /// <summary>Datos libres de las capas de juego (p. ej. su avatar).</summary>
    public object UserData { get; set; }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>Procesa un mensaje de juego. El lector apunta a los datos (tras el tipo) y solo vale durante la llamada.</summary>
public delegate void MessageHandler(SessionPlayer from, PacketReader reader);

/// <summary>
/// La partida en curso, igual para el host que para quien se une. El host ejecuta el servidor en su proceso: su jugador
/// (id 1) le habla directamente, sin red. El servidor sabe en qué sala está cada jugador, quién manda en cada sala (el
/// primero que entró) y reenvía cada mensaje según su ruta (<see cref="MessageRoutes"/>), copiando los bytes sin crear
/// memoria.
/// <para>Formato: cliente → servidor <c>[tipo][datos]</c>; servidor → cliente <c>[tipo][remitente][datos]</c> para los de
/// juego y <c>[tipo][datos]</c> para los de sesión.</para>
/// </summary>
public sealed class GameSession : INetListener, IDisposable {
    /// <summary>Id del jugador del host.</summary>
    public const ushort HostPlayerId = 1;

    private readonly NetServer _server;
    private readonly NetClient _client;
    private readonly Dictionary<ushort, SessionPlayer> _players = new();
    private readonly Dictionary<string, List<ushort>> _sceneMembers = new();
    private readonly MessageHandler[] _handlers = new MessageHandler[256];
    // Mensaje del jugador del host en curso (se reparte al terminar)
    private readonly byte[] _localScratch = new byte[NetConstants.MaxMessageSize];
    private readonly PacketWriter _localWriter = new();
    private byte _localType;
    private bool _writingLocal;

    // Un manejador puede enviar otro mensaje mientras se reparte el suyo (p. ej. un golpe que provoca el aviso de sus
    // efectos): cada nivel de anidamiento tiene su lista de destinatarios, su lector y su copia del mensaje
    private readonly List<List<SessionPlayer>> _targetsByDepth = new();
    private readonly List<PacketReader> _readersByDepth = new();
    private readonly List<byte[]> _scratchByDepth = new();
    private int _depth;

    private readonly Action<string> _log;

    public bool IsHost => _server != null;

    /// <summary>Conectado y con id asignado (el host lo está desde que arranca).</summary>
    public bool IsActive { get; private set; }

    public ushort LocalId { get; private set; }

    public SessionPlayer LocalPlayer => _players.TryGetValue(LocalId, out var player) ? player : null;

    /// <summary>Todos los jugadores, incluido el local.</summary>
    public IEnumerable<SessionPlayer> Players => _players.Values;

    /// <summary>Código de la partida (solo el host).</summary>
    public string RoomCode { get; }

    /// <summary>Puerto en el que escucha el host.</summary>
    public int Port => _server?.LocalPort ?? 0;

    /// <summary>Latencia con el host en segundos (0 en el host).</summary>
    public double Rtt => _client?.Connection?.Rtt ?? 0;

    public event Action Connected;
    public event Action<DisconnectReason> Disconnected;
    public event Action<SessionPlayer> PlayerJoined;
    public event Action<SessionPlayer> PlayerLeft;
    public event Action<SessionPlayer> PlayerSceneChanged;

    /// <summary>Cambió quién manda en una sala (sala, id del nuevo dueño o 0 si quedó vacía).</summary>
    public event Action<string, ushort> SceneOwnerChanged;

    /// <summary>Quién manda en cada sala, según el servidor.</summary>
    private readonly Dictionary<string, ushort> _sceneOwners = new();

    /// <summary>Nombre del jugador local.</summary>
    private readonly string _localName;

    // Host: crea el servidor con la sesión como receptora de sus eventos
    private GameSession(ITransport transport, string roomCode, string localName, string scene, string version,
        Action<string> log, int maxPlayers) {
        RoomCode = roomCode;
        _localName = localName;
        _version = version;
        _log = log;
        _server = new NetServer(transport, this, roomCode, maxPlayers - 1, firstId: HostPlayerId + 1) {
            Approve = ApproveConnection
        };
        IsActive = true;
        LocalId = HostPlayerId;
        var local = new SessionPlayer { Id = HostPlayerId, Name = localName, IsLocal = true };
        _players[HostPlayerId] = local;
        MoveToScene(local, scene ?? string.Empty);
    }

    // Cliente: crea el cliente con la sesión como receptora y empieza a conectarse
    private GameSession(ITransport transport, IPEndPoint host, string roomCode, string name, string version,
        Action<string> log) {
        RoomCode = roomCode;
        _localName = name;
        _version = version;
        _log = log;
        _client = new NetClient(transport, this);
        _client.Connect(host, roomCode, name, version);
    }

    /// <summary>Hostea: el servidor escucha en el transporte dado y el jugador local (id 1) queda dentro.</summary>
    public static GameSession Host(ITransport transport, string roomCode, string localName, string scene,
        string version, Action<string> log, int maxPlayers = 4) =>
        new(transport, roomCode, localName, scene, version, log, maxPlayers);

    /// <summary>Se une a una partida. El resultado llega por <see cref="Connected"/> o <see cref="Disconnected"/>.</summary>
    public static GameSession Join(ITransport transport, IPEndPoint host, string roomCode, string name, string version,
        Action<string> log) =>
        new(transport, host, roomCode, name, version, log);

    /// <summary>Versión del mod: el host solo acepta la misma.</summary>
    private readonly string _version;

    private DisconnectReason? ApproveConnection(NetConnection connection) {
        return connection.RemoteVersion == _version ? null : DisconnectReason.VersionMismatch;
    }

    /// <summary>Procesa la red. Llamar una vez por frame.</summary>
    public void Update() {
        if (_server != null) {
            _server.Update();
            BroadcastPings();
        } else {
            _client.Update();
            var local = LocalPlayer;
            if (local != null && _client.Connection != null) {
                local.Ping = (int) (_client.Connection.Rtt * 1000);
            }
        }
    }

    private const double PingInterval = 1.0;
    private double _nextPings;

    /// <summary>El host reparte cada segundo la latencia de cada jugador, para que todos la vean.</summary>
    private void BroadcastPings() {
        var now = NetPeer.Now;
        if (now < _nextPings) {
            return;
        }

        _nextPings = now + PingInterval;
        foreach (var player in _players.Values) {
            player.Ping = player.Connection != null ? (int) (player.Connection.Rtt * 1000) : 0;
        }

        foreach (var target in _players.Values) {
            if (target.Connection == null) {
                continue;
            }

            var writer = target.Connection.BeginReliable();
            writer.WriteByte((byte) MessageType.Pings);
            writer.WriteByte((byte) _players.Count);
            foreach (var player in _players.Values) {
                writer.WriteUShort(player.Id);
                writer.WriteUShort((ushort) Math.Min(player.Ping, ushort.MaxValue));
            }

            target.Connection.EndMessage();
        }
    }

    /// <summary>Registra quién procesa un tipo de mensaje de juego.</summary>
    public void On(MessageType type, MessageHandler handler) => _handlers[(byte) type] = handler;

    /// <summary>Quién manda en una sala (0 si nadie).</summary>
    public ushort OwnerOf(string scene) => scene != null && _sceneOwners.TryGetValue(scene, out var owner) ? owner : (ushort) 0;

    /// <summary>Si el jugador local manda en la sala en la que está.</summary>
    public bool IsLocalSceneOwner => LocalPlayer != null && OwnerOf(LocalPlayer.Scene) == LocalId;

    // =====================================================================================================
    // Envío desde el jugador local
    // =====================================================================================================

    /// <summary>
    /// Empieza un mensaje de juego del jugador local; se escribe en el escritor devuelto (sin el tipo) y se termina con
    /// <see cref="End"/>. Se envía fiable o no y a quien diga su ruta.
    /// </summary>
    public PacketWriter Begin(MessageType type) {
        // Quien escribía el anterior falló a medias (su error ya se registró): se descarta ese mensaje en lugar de
        // dejar la partida sin poder enviar nada más
        if (_writingLocal) {
            _writingLocal = false;
            _client?.Connection.CancelMessage();
            AbandonedMessages++;
        }

        _writingLocal = true;
        _localType = (byte) type;
        if (_server != null) {
            // Host: se escribe aparte y al terminar se reparte a cada destinatario
            _localWriter.Reset(_localScratch, 0, _localScratch.Length - 3);
            return _localWriter;
        }

        var connection = _client.Connection;
        var writer = MessageRoutes.IsReliable(_localType) ? connection.BeginReliable() : connection.BeginUnreliable();
        writer.WriteByte(_localType);
        return writer;
    }

    /// <summary>Mensajes empezados que se descartaron a medias (su escritor falló).</summary>
    public int AbandonedMessages { get; private set; }

    public void End() {
        if (!_writingLocal) {
            return;
        }

        _writingLocal = false;
        if (_server == null) {
            _client.Connection.EndMessage();
            return;
        }

        var local = _players[HostPlayerId];
        if (MessageRoutes.RouteOf(_localType) == Route.Server) {
            // Mensaje del host a su propio servidor: se procesa en el sitio, desde una copia (si su manejador envía algo,
            // el búfer del mensaje local se vuelve a usar)
            var depth = _depth++;
            try {
                var length = _localWriter.Length;
                var copy = ScratchAt(depth);
                Buffer.BlockCopy(_localScratch, 0, copy, 0, length);
                var reader = ReaderAt(depth);
                reader.Reset(copy, 0, length);
                _handlers[_localType]?.Invoke(local, reader);
            } finally {
                _depth--;
            }

            return;
        }

        Relay(local, _localType, _localScratch, 0, _localWriter.Length);
    }

    private List<SessionPlayer> TargetsAt(int depth) {
        while (_targetsByDepth.Count <= depth) {
            _targetsByDepth.Add(new List<SessionPlayer>());
        }

        return _targetsByDepth[depth];
    }

    private PacketReader ReaderAt(int depth) {
        while (_readersByDepth.Count <= depth) {
            _readersByDepth.Add(new PacketReader());
        }

        return _readersByDepth[depth];
    }

    private byte[] ScratchAt(int depth) {
        while (_scratchByDepth.Count <= depth) {
            _scratchByDepth.Add(new byte[NetConstants.MaxMessageSize]);
        }

        return _scratchByDepth[depth];
    }

    /// <summary>Si se puede enviar ahora (conectado).</summary>
    public bool CanSend => IsActive && (_server != null || _client.IsConnected);

    /// <summary>
    /// Mensaje del servidor a un jugador concreto (solo en el host). Al cliente le llega con remitente el host.
    /// </summary>
    public PacketWriter BeginTo(SessionPlayer target, MessageType type) {
        if (_server == null || target.Connection == null) {
            throw new InvalidOperationException("Solo el host envía a un jugador concreto, y no a sí mismo");
        }

        var writer = MessageRoutes.IsReliable((byte) type) ? target.Connection.BeginReliable() : target.Connection.BeginUnreliable();
        writer.WriteByte((byte) type);
        writer.WriteUShort(HostPlayerId);
        _pendingTarget = target;
        return writer;
    }

    private SessionPlayer _pendingTarget;

    public void EndTo() {
        _pendingTarget?.Connection?.EndMessage();
        _pendingTarget = null;
    }

    /// <summary>El jugador local entró en otra sala.</summary>
    public void SetLocalScene(string scene) {
        var local = LocalPlayer;
        if (local == null) {
            // Aún sin bienvenida: se envía al recibirla
            _pendingScene = scene;
            return;
        }

        if (local.Scene == scene) {
            return;
        }

        if (_server != null) {
            MoveToScene(local, scene);
            return;
        }

        if (_client.IsConnected) {
            var writer = _client.Connection.BeginReliable();
            writer.WriteByte((byte) MessageType.EnterScene);
            writer.WriteString(scene);
            _client.Connection.EndMessage();
        }

        // Se apunta ya en local (el servidor confirmará quién manda)
        local.Scene = scene;
    }

    // =====================================================================================================
    // Servidor: reparto y salas
    // =====================================================================================================

    /// <summary>Reenvía un mensaje de <paramref name="from"/> según su ruta (en el host).</summary>
    private void Relay(SessionPlayer from, byte type, byte[] buffer, int offset, int length) {
        var depth = _depth++;
        try {
            var targets = TargetsAt(depth);
            targets.Clear();
            var route = MessageRoutes.RouteOf(type);
            foreach (var player in _players.Values) {
                if (player == from) {
                    continue;
                }

                if (route == Route.Everyone ||
                    route == Route.SameScene && player.Scene == from.Scene && from.Scene.Length > 0 ||
                    route == Route.SceneOwner && player.Id == OwnerOf(from.Scene)) {
                    targets.Add(player);
                }
            }

            var reliable = MessageRoutes.IsReliable(type);
            for (var i = 0; i < targets.Count; i++) {
                var target = targets[i];
                if (target.IsLocal) {
                    var reader = ReaderAt(depth);
                    reader.Reset(buffer, offset, offset + length);
                    _handlers[type]?.Invoke(from, reader);
                    continue;
                }

                var connection = target.Connection;
                var writer = reliable ? connection.BeginReliable() : connection.BeginUnreliable();
                writer.WriteByte(type);
                writer.WriteUShort(from.Id);
                writer.WriteBytes(buffer, offset, length);
                connection.EndMessage();
            }
        } finally {
            _depth--;
        }
    }

    private void MoveToScene(SessionPlayer player, string scene) {
        var oldScene = player.Scene;
        if (oldScene == scene) {
            return;
        }

        if (oldScene.Length > 0 && _sceneMembers.TryGetValue(oldScene, out var oldMembers)) {
            oldMembers.Remove(player.Id);
            UpdateOwner(oldScene, oldMembers);
        }

        // Sala vacía = fuera de una partida (menú, cargando): también se avisa
        player.Scene = scene;
        BroadcastSession(MessageType.PlayerScene, w => {
            w.WriteUShort(player.Id);
            w.WriteString(scene);
        });
        if (scene.Length > 0) {
            if (!_sceneMembers.TryGetValue(scene, out var members)) {
                members = new List<ushort>();
                _sceneMembers[scene] = members;
            }

            members.Add(player.Id);
            UpdateOwner(scene, members);
        }

        PlayerSceneChanged?.Invoke(player);
    }

    private void UpdateOwner(string scene, List<ushort> members) {
        var owner = members.Count > 0 ? members[0] : (ushort) 0;
        if (OwnerOf(scene) == owner) {
            return;
        }

        if (owner == 0) {
            _sceneOwners.Remove(scene);
        } else {
            _sceneOwners[scene] = owner;
        }

        BroadcastSession(MessageType.SceneOwner, w => {
            w.WriteString(scene);
            w.WriteUShort(owner);
        });
        SceneOwnerChanged?.Invoke(scene, owner);
    }

    /// <summary>Mensaje de sesión del servidor a todos los clientes remotos (los cambios de sala no son frecuentes).</summary>
    private void BroadcastSession(MessageType type, Action<PacketWriter> write) {
        if (_server == null) {
            return;
        }

        foreach (var player in _players.Values) {
            if (player.Connection == null) {
                continue;
            }

            var writer = player.Connection.BeginReliable();
            writer.WriteByte((byte) type);
            write(writer);
            player.Connection.EndMessage();
        }
    }

    // =====================================================================================================
    // Eventos de la red
    // =====================================================================================================

    void INetListener.OnConnected(NetConnection connection) {
        if (_server != null) {
            var player = new SessionPlayer { Id = connection.Id, Name = connection.Name, Connection = connection };
            connection.UserData = player;

            // Bienvenida: su id y quién está ya, con sus salas y dueños
            var welcome = connection.BeginReliable();
            welcome.WriteByte((byte) MessageType.Welcome);
            welcome.WriteUShort(player.Id);
            welcome.WriteByte((byte) _players.Count);
            foreach (var other in _players.Values) {
                welcome.WriteUShort(other.Id);
                welcome.WriteString(other.Name);
                welcome.WriteString(other.Scene);
            }

            connection.EndMessage();
            foreach (var pair in _sceneOwners) {
                var owner = connection.BeginReliable();
                owner.WriteByte((byte) MessageType.SceneOwner);
                owner.WriteString(pair.Key);
                owner.WriteUShort(pair.Value);
                connection.EndMessage();
            }

            BroadcastSession(MessageType.PlayerJoined, w => {
                w.WriteUShort(player.Id);
                w.WriteString(player.Name);
            });
            _players[player.Id] = player;
            _log($"{player} se unió");
            PlayerJoined?.Invoke(player);
            return;
        }

        // Cliente: falta la bienvenida para saber el id propio y quién está
    }

    void INetListener.OnDisconnected(NetConnection connection, DisconnectReason reason) {
        if (_server != null) {
            if (connection.UserData is not SessionPlayer player) {
                return;
            }

            _players.Remove(player.Id);
            if (player.Scene.Length > 0 && _sceneMembers.TryGetValue(player.Scene, out var members)) {
                members.Remove(player.Id);
                UpdateOwner(player.Scene, members);
            }

            BroadcastSession(MessageType.PlayerLeft, w => w.WriteUShort(player.Id));
            _log($"{player} se fue ({reason})");
            PlayerLeft?.Invoke(player);
            return;
        }

        var wasActive = IsActive;
        IsActive = false;
        foreach (var player in _players.Values) {
            if (!player.IsLocal) {
                PlayerLeft?.Invoke(player);
            }
        }

        _players.Clear();
        _sceneOwners.Clear();
        _log(wasActive ? $"Desconectado del host ({reason})" : $"No se pudo conectar ({reason})");
        Disconnected?.Invoke(reason);
    }

    void INetListener.OnMessage(NetConnection connection, PacketReader reader) {
        var type = reader.ReadByte();
        if (_server != null) {
            if (connection.UserData is not SessionPlayer from) {
                return;
            }

            if (type == (byte) MessageType.EnterScene) {
                MoveToScene(from, reader.ReadString(128));
                return;
            }

            if (type < (byte) MessageType.PlayerState) {
                return;
            }

            if (MessageRoutes.RouteOf(type) == Route.Server) {
                _handlers[type]?.Invoke(from, reader);
                return;
            }

            Relay(from, type, reader.Buffer, reader.Position, reader.Remaining);
            return;
        }

        if (type < (byte) MessageType.PlayerState) {
            HandleSessionMessage((MessageType) type, reader);
            return;
        }

        var fromId = reader.ReadUShort();
        if (_players.TryGetValue(fromId, out var sender)) {
            _handlers[type]?.Invoke(sender, reader);
        }
    }

    private void HandleSessionMessage(MessageType type, PacketReader reader) {
        switch (type) {
            case MessageType.Welcome: {
                LocalId = reader.ReadUShort();
                var count = reader.ReadByte();
                var localScene = _pendingScene;
                _players.Clear();
                _players[LocalId] = new SessionPlayer {
                    Id = LocalId, Name = _localName, IsLocal = true, Scene = string.Empty
                };
                for (var i = 0; i < count; i++) {
                    var player = new SessionPlayer {
                        Id = reader.ReadUShort(), Name = reader.ReadString(64), Scene = reader.ReadString(128)
                    };
                    _players[player.Id] = player;
                }

                IsActive = true;
                Connected?.Invoke();
                foreach (var player in _players.Values) {
                    if (!player.IsLocal) {
                        PlayerJoined?.Invoke(player);
                    }
                }

                // La sala en la que ya estaba el jugador local
                if (!string.IsNullOrEmpty(localScene)) {
                    SetLocalScene(localScene);
                }

                break;
            }
            case MessageType.PlayerJoined: {
                var player = new SessionPlayer { Id = reader.ReadUShort(), Name = reader.ReadString(64) };
                _players[player.Id] = player;
                PlayerJoined?.Invoke(player);
                break;
            }
            case MessageType.PlayerLeft: {
                var id = reader.ReadUShort();
                if (_players.TryGetValue(id, out var player)) {
                    _players.Remove(id);
                    PlayerLeft?.Invoke(player);
                }

                break;
            }
            case MessageType.PlayerScene: {
                var id = reader.ReadUShort();
                var scene = reader.ReadString(128);
                if (_players.TryGetValue(id, out var player) && !player.IsLocal) {
                    player.Scene = scene;
                    PlayerSceneChanged?.Invoke(player);
                }

                break;
            }
            case MessageType.Pings: {
                var count = reader.ReadByte();
                for (var i = 0; i < count; i++) {
                    var id = reader.ReadUShort();
                    var ping = reader.ReadUShort();
                    if (_players.TryGetValue(id, out var player) && !player.IsLocal) {
                        player.Ping = ping;
                    }
                }

                break;
            }
            case MessageType.SceneOwner: {
                var scene = reader.ReadString(128);
                var owner = reader.ReadUShort();
                if (owner == 0) {
                    _sceneOwners.Remove(scene);
                } else {
                    _sceneOwners[scene] = owner;
                }

                SceneOwnerChanged?.Invoke(scene, owner);
                break;
            }
        }
    }

    /// <summary>Sala del jugador local antes de recibir la bienvenida (se envía al recibirla).</summary>
    private string _pendingScene;

    void INetListener.OnLargeMessage(NetConnection connection, byte kind, byte[] data) {
        LargeMessage?.Invoke(connection.UserData as SessionPlayer, kind, data);
    }

    /// <summary>Envía datos grandes al host (troceados por el canal fiable). Solo en el cliente.</summary>
    public void SendLargeToHost(byte kind, byte[] data) {
        if (_client?.Connection is { State: ConnectionState.Connected } connection) {
            connection.SendLarge(kind, data, data.Length);
        }
    }

    /// <summary>Envía datos grandes a un jugador remoto. Solo en el host.</summary>
    public void SendLargeTo(SessionPlayer target, byte kind, byte[] data) {
        if (target.Connection is { State: ConnectionState.Connected } connection) {
            connection.SendLarge(kind, data, data.Length);
        }
    }

    /// <summary>Transferencia grande recibida (remitente en el servidor; null en el cliente: viene del host).</summary>
    public event Action<SessionPlayer, byte, byte[]> LargeMessage;

    void INetListener.Log(string message) => _log(message);

    public void Dispose() {
        if (_server != null) {
            _server.Dispose();
        } else {
            _client.Dispose();
        }

        IsActive = false;
    }
}
