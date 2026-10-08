using System;
using System.Collections.Generic;
using System.Net;
using HKMultiplayer.Net.Crypto;
using HKMultiplayer.Net.Transport;

namespace HKMultiplayer.Net;

/// <summary>
/// Lado servidor (lo ejecuta el host). Acepta conexiones de quien conozca el código de la partida.
/// <list type="number">
/// <item>Cliente → Hello: su clave pública efímera y un valor aleatorio.</item>
/// <item>Servidor → Challenge: su clave pública, su valor aleatorio y una prueba cifrada con la clave derivada (que
/// incluye el código). El cliente sabe así si el código es correcto.</item>
/// <item>Cliente → (cifrado) ConnectRequest con nombre y versión. Si no conoce el código no puede cifrarlo y se ignora.</item>
/// <item>Servidor → ConnectAccept (id del jugador), o cierra la conexión con el motivo del rechazo.</item>
/// </list>
/// </summary>
public sealed class NetServer : NetPeer {
    /// <summary>Un saludo sin completar se descarta pasado este tiempo.</summary>
    private const double HandshakeTimeout = 10;

    private readonly string _roomCode;
    private readonly int _maxClients;
    private readonly PacketWriter _writer = new();
    private readonly byte[] _buffer = new byte[NetConstants.MaxDatagramSize];
    private readonly List<NetConnection> _expired = new();
    private readonly HashSet<ushort> _usedIds = new();

    /// <summary>Decide si se acepta a un jugador (nombre y versión). Devuelve null para aceptar o el motivo del rechazo.</summary>
    public Func<NetConnection, DisconnectReason?> Approve { get; set; }

    /// <summary>Jugadores aceptados.</summary>
    public IEnumerable<NetConnection> Clients {
        get {
            foreach (var connection in Connections.Values) {
                if (connection.State == ConnectionState.Connected) {
                    yield return connection;
                }
            }
        }
    }

    public int LocalPort => Transport.LocalPort;

    /// <param name="firstId">
    /// Primer id que se asigna a los clientes (los anteriores quedan para jugadores locales, como el propio host).
    /// </param>
    public NetServer(ITransport transport, INetListener listener, string roomCode, int maxClients, ushort firstId = 1)
        : base(transport, listener) {
        _roomCode = roomCode;
        _maxClients = maxClients;
        for (ushort id = 0; id < firstId; id++) {
            _usedIds.Add(id);
        }
    }

    private int ConnectedCount {
        get {
            var count = 0;
            foreach (var connection in Connections.Values) {
                if (connection.State == ConnectionState.Connected) {
                    count++;
                }
            }

            return count;
        }
    }

    private int PendingCount => Connections.Count - ConnectedCount;

    protected override void HandleHandshake(DatagramType type, byte[] buffer, int length, IPEndPoint from, double now) {
        if (type != DatagramType.Hello) {
            return;
        }

        var reader = ScratchReader;
        reader.Reset(buffer, 5, length);
        var version = reader.ReadUShort();
        var clientPublic = new byte[KeyExchange.PublicKeySize];
        reader.ReadBytes(clientPublic, 0, clientPublic.Length);
        var clientRandom = new byte[KeyExchange.RandomSize];
        reader.ReadBytes(clientRandom, 0, clientRandom.Length);

        if (Connections.TryGetValue(from, out var existing)) {
            // Hello repetido (se perdió el Challenge): se reenvía el mismo
            if (existing.State == ConnectionState.Handshaking && existing.ChallengeDatagram != null &&
                SameBytes(existing.RemotePublicKey, clientPublic)) {
                SendRaw(existing.ChallengeDatagram, existing.ChallengeLength, from);
                return;
            }

            // Mismo puerto con claves nuevas: el jugador se reconecta; la conexión vieja ya no sirve
            CloseConnection(existing, DisconnectReason.Closed, false);
        }

        if (PendingCount >= NetConstants.MaxPendingConnections) {
            return;
        }

        var exchange = new KeyExchange();
        var keys = exchange.Derive(clientPublic, _roomCode, clientPublic, exchange.PublicKey, clientRandom,
            exchange.RandomBytes);
        if (keys == null) {
            return;
        }

        var connection = new NetConnection(this, from, MessagePool, now) {
            KeyExchange = exchange,
            RemotePublicKey = clientPublic,
            RemoteRandom = clientRandom
        };
        var sendCipher = new PacketCipher(keys.ServerToClient);
        connection.SetCiphers(sendCipher, new PacketCipher(keys.ClientToServer));

        // Challenge: versión, clave pública, aleatorio y prueba (cifrado de la firma con la secuencia 0)
        _writer.Reset(_buffer, 0, _buffer.Length);
        WriteMagic(_writer, DatagramType.Challenge);
        _writer.WriteUShort(NetConstants.ProtocolVersion);
        _writer.WriteBytes(exchange.PublicKey, 0, KeyExchange.PublicKeySize);
        _writer.WriteBytes(exchange.RandomBytes, 0, KeyExchange.RandomSize);
        var headerLength = _writer.Length;
        var proof = ChallengeProof;
        var proofLength = sendCipher.Encrypt(0, _buffer, 0, headerLength, proof, 0, proof.Length, _buffer, headerLength);
        var total = headerLength + proofLength;

        connection.ChallengeDatagram = new byte[total];
        System.Buffer.BlockCopy(_buffer, 0, connection.ChallengeDatagram, 0, total);
        connection.ChallengeLength = total;
        Connections[from] = connection;
        SendRaw(connection.ChallengeDatagram, total, from);

        if (version != NetConstants.ProtocolVersion) {
            // El cliente verá la versión del Challenge y se desconectará con el motivo
            Listener.Log($"Cliente {from} con protocolo {version} (el nuestro es {NetConstants.ProtocolVersion})");
        }
    }

    /// <summary>Texto que el servidor cifra en el Challenge para demostrar que conoce el código.</summary>
    internal static readonly byte[] ChallengeProof = System.Text.Encoding.ASCII.GetBytes("HKMU-OK!");

    protected override void UpdateHandshakes(double now) {
        _expired.Clear();
        foreach (var connection in Connections.Values) {
            if (connection.State == ConnectionState.Handshaking && now - connection.LastReceive > HandshakeTimeout) {
                _expired.Add(connection);
            }
        }

        foreach (var connection in _expired) {
            CloseConnection(connection, DisconnectReason.Timeout, false);
        }
    }

    protected override bool HandleControl(NetConnection connection, byte type, PacketReader reader) {
        if (type != ControlMessage.ConnectRequest || connection.State != ConnectionState.Handshaking) {
            return false;
        }

        connection.Name = SanitizeName(reader.ReadString(64));
        connection.RemoteVersion = reader.ReadString(64);

        DisconnectReason? reject = null;
        if (ConnectedCount >= _maxClients) {
            reject = DisconnectReason.Full;
        } else if (Approve != null) {
            reject = Approve(connection);
        }

        if (reject != null) {
            // Se cierra avisando con el motivo (el aviso se repite por si se pierde algún paquete)
            CloseConnection(connection, reject.Value, true);
            return true;
        }

        connection.Id = NextId();
        connection.State = ConnectionState.Connected;
        var accept = connection.BeginReliable();
        accept.WriteByte(ControlMessage.ConnectAccept);
        accept.WriteUShort(connection.Id);
        connection.EndMessage();
        Listener.OnConnected(connection);
        return true;
    }

    private ushort NextId() {
        for (ushort id = 1; id < ushort.MaxValue; id++) {
            if (_usedIds.Add(id)) {
                return id;
            }
        }

        throw new InvalidOperationException("Sin ids libres");
    }

    /// <summary>Nombre visible: sin caracteres de control y de longitud razonable.</summary>
    private static string SanitizeName(string name) {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) {
            if (!char.IsControl(c)) {
                builder.Append(c);
            }
        }

        var clean = builder.ToString().Trim();
        if (clean.Length > 24) {
            clean = clean.Substring(0, 24);
        }

        return clean.Length == 0 ? "Jugador" : clean;
    }

    protected override void OnConnectionClosed(NetConnection connection, DisconnectReason reason, bool wasConnected) {
        if (connection.Id != 0) {
            _usedIds.Remove(connection.Id);
        }

        if (wasConnected) {
            Listener.OnDisconnected(connection, reason);
        }
    }

    private static bool SameBytes(byte[] a, byte[] b) {
        if (a == null || b == null || a.Length != b.Length) {
            return false;
        }

        for (var i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }
}
