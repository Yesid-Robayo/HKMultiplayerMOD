using System.Net;
using HKMultiplayer.Net.Crypto;
using HKMultiplayer.Net.Transport;

namespace HKMultiplayer.Net;

/// <summary>
/// Lado cliente: se conecta al host con su dirección y el código de la partida (ver el saludo en <see cref="NetServer"/>).
/// </summary>
public sealed class NetClient : NetPeer {
    /// <summary>Cada cuánto se repite el Hello mientras no llega respuesta.</summary>
    private const double HelloInterval = 0.25;

    /// <summary>Sin respuesta al Hello en este tiempo, se abandona.</summary>
    private const double ConnectTimeout = 10;

    private readonly PacketWriter _writer = new();
    private readonly byte[] _buffer = new byte[NetConstants.MaxDatagramSize];

    private IPEndPoint _server;
    private string _roomCode;
    private string _name;
    private string _version;
    private KeyExchange _exchange;
    private double _connectStarted;
    private double _nextHello;
    private bool _waitingChallenge;

    /// <summary>Conexión con el host (null si no hay).</summary>
    public NetConnection Connection { get; private set; }

    /// <summary>Si está conectado y aceptado.</summary>
    public bool IsConnected => Connection is { State: ConnectionState.Connected };

    /// <summary>Si hay un intento de conexión en curso (aún sin aceptar).</summary>
    public bool IsConnecting => _waitingChallenge || Connection is { State: ConnectionState.Handshaking };

    public NetClient(ITransport transport, INetListener listener) : base(transport, listener) {
    }

    /// <summary>Empieza a conectarse. El resultado llega por <see cref="INetListener.OnConnected"/> u OnDisconnected.</summary>
    public void Connect(IPEndPoint server, string roomCode, string name, string version) {
        if (Connection != null) {
            CloseConnection(Connection, DisconnectReason.Closed, true);
        }

        _server = server;
        _roomCode = roomCode;
        _name = name;
        _version = version;
        _exchange = new KeyExchange();
        _connectStarted = Now;
        _nextHello = 0;
        _waitingChallenge = true;
    }

    protected override void UpdateHandshakes(double now) {
        if (!_waitingChallenge) {
            return;
        }

        if (now - _connectStarted > ConnectTimeout) {
            _waitingChallenge = false;
            var failed = new NetConnection(this, _server, MessagePool, now) {
                State = ConnectionState.Handshaking
            };
            Listener.OnDisconnected(failed, DisconnectReason.Timeout);
            return;
        }

        if (now < _nextHello) {
            return;
        }

        _nextHello = now + HelloInterval;
        _writer.Reset(_buffer, 0, _buffer.Length);
        WriteMagic(_writer, DatagramType.Hello);
        _writer.WriteUShort(NetConstants.ProtocolVersion);
        _writer.WriteBytes(_exchange.PublicKey, 0, KeyExchange.PublicKeySize);
        _writer.WriteBytes(_exchange.RandomBytes, 0, KeyExchange.RandomSize);
        SendRaw(_buffer, _writer.Length, _server);
    }

    protected override void HandleHandshake(DatagramType type, byte[] buffer, int length, IPEndPoint from, double now) {
        if (type != DatagramType.Challenge || !_waitingChallenge || !from.Equals(_server)) {
            return;
        }

        var reader = ScratchReader;
        reader.Reset(buffer, 5, length);
        var version = reader.ReadUShort();
        if (version != NetConstants.ProtocolVersion) {
            Fail(DisconnectReason.VersionMismatch, now);
            return;
        }

        var serverPublic = new byte[KeyExchange.PublicKeySize];
        reader.ReadBytes(serverPublic, 0, serverPublic.Length);
        var serverRandom = new byte[KeyExchange.RandomSize];
        reader.ReadBytes(serverRandom, 0, serverRandom.Length);
        var headerLength = reader.Position;

        var keys = _exchange.Derive(serverPublic, _roomCode, _exchange.PublicKey, serverPublic, _exchange.RandomBytes,
            serverRandom);
        if (keys == null) {
            Fail(DisconnectReason.Rejected, now);
            return;
        }

        // Prueba: si el código es el mismo, la clave del servidor descifra la firma conocida
        var receiveCipher = new PacketCipher(keys.ServerToClient);
        var proof = new byte[NetConstants.MaxDatagramSize];
        var proofLength = receiveCipher.Decrypt(0, buffer, 0, headerLength, buffer, headerLength,
            length - headerLength, proof, 0);
        if (proofLength != NetServer.ChallengeProof.Length) {
            Fail(DisconnectReason.WrongCode, now);
            return;
        }

        for (var i = 0; i < proofLength; i++) {
            if (proof[i] != NetServer.ChallengeProof[i]) {
                Fail(DisconnectReason.WrongCode, now);
                return;
            }
        }

        _waitingChallenge = false;
        var connection = new NetConnection(this, _server, MessagePool, now) {
            KeyExchange = _exchange,
            RemotePublicKey = serverPublic,
            RemoteRandom = serverRandom
        };
        connection.SetCiphers(new PacketCipher(keys.ClientToServer), receiveCipher);
        Connections[_server] = connection;
        Connection = connection;

        var request = connection.BeginReliable();
        request.WriteByte(ControlMessage.ConnectRequest);
        request.WriteString(_name);
        request.WriteString(_version);
        connection.EndMessage();
    }

    private void Fail(DisconnectReason reason, double now) {
        _waitingChallenge = false;
        var failed = new NetConnection(this, _server, MessagePool, now) {
            State = ConnectionState.Handshaking
        };
        Listener.OnDisconnected(failed, reason);
    }

    protected override bool HandleControl(NetConnection connection, byte type, PacketReader reader) {
        if (type == ControlMessage.ConnectAccept && connection.State == ConnectionState.Handshaking) {
            connection.Id = reader.ReadUShort();
            connection.State = ConnectionState.Connected;
            Listener.OnConnected(connection);
            return true;
        }

        return false;
    }

    protected override void OnConnectionClosed(NetConnection connection, DisconnectReason reason, bool wasConnected) {
        if (connection == Connection) {
            Connection = null;
        }

        // El cliente avisa siempre: también si se cerró durante el saludo (rechazo, partida llena...)
        Listener.OnDisconnected(connection, reason);
    }
}
