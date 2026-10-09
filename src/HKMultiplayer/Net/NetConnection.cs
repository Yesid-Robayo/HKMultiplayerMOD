using System;
using System.Collections.Generic;
using System.Net;
using HKMultiplayer.Net.Channels;
using HKMultiplayer.Net.Crypto;

namespace HKMultiplayer.Net;

/// <summary>Estado de una conexión.</summary>
public enum ConnectionState : byte {
    /// <summary>Saludo en curso (claves intercambiadas, esperando la petición o la aceptación).</summary>
    Handshaking,
    Connected,
    Disconnected
}

/// <summary>
/// Una conexión con el otro extremo: cifrado, secuencias y acuses, canal fiable ordenado, canal no fiable y datos
/// grandes. Se usa desde el hilo principal. No crea objetos por paquete: todos los búferes son reservados o de reserva.
/// </summary>
public sealed class NetConnection : ReliableReceiver.ISink {
    /// <summary>Mensajes no fiables pendientes de enviar en este tick como mucho.</summary>
    private const int MaxUnreliablePerTick = 256;

    /// <summary>Transferencia grande más grande que se acepta (protege la memoria).</summary>
    public const int MaxLargeTransfer = 64 * 1024 * 1024;

    /// <summary>Bytes de datos por trozo de una transferencia grande.</summary>
    private const int LargeChunkData = NetConstants.MaxMessageSize - 16;

    private readonly NetPeer _peer;
    private readonly BufferPool _messagePool;
    private readonly ReliableSender _reliableSender;
    private readonly ReliableReceiver _reliableReceiver;

    private PacketCipher _sendCipher;
    private PacketCipher _receiveCipher;

    // Secuencias: la primera de datos es 1 (la 0 la usa la prueba del saludo)
    private uint _localSequence = 1;
    private uint _remoteSequence;
    private ulong _remoteAckBits;
    private bool _receivedAny;

    // Momento de envío de cada paquete, para medir la latencia
    private readonly double[] _sentTime = new double[1024];
    private readonly uint[] _sentSequence = new uint[1024];

    // Mensajes no fiables del tick actual
    private readonly byte[] _unreliableData = new byte[NetConstants.MaxMessageSize * 8];
    private readonly int[] _unreliableOffset = new int[MaxUnreliablePerTick];
    private readonly int[] _unreliableLength = new int[MaxUnreliablePerTick];
    private int _unreliableCount;
    private int _unreliableUsed;

    // Escritura de un mensaje en curso (BeginReliable/BeginUnreliable)
    private readonly PacketWriter _messageWriter = new();
    private readonly byte[] _messageScratch = new byte[NetConstants.MaxMessageSize];
    private int _writing; // 0 = nada, 1 = fiable, 2 = no fiable

    /// <summary>
    /// Ritmo de las transferencias grandes de baja prioridad (skins): unos 0,5 MB/s, para que no retrasen golpes ni
    /// eventos.
    /// </summary>
    public const int LowPriorityBytesPerSecond = 512 * 1024;

    /// <summary>
    /// Trozos de baja prioridad que se dejan sin confirmar a la vez como mucho. El canal fiable es ordenado: un evento que
    /// sale después de un trozo espera a que ese trozo llegue, así que se dejan pocos por delante.
    /// </summary>
    private const int LowPriorityMaxInFlight = 48;

    // Transferencias grandes salientes y entrantes (las de baja prioridad, en su propia cola y a su ritmo)
    private readonly Queue<LargeOutgoing> _largeOut = new();
    private readonly Queue<LargeOutgoing> _largeLow = new();
    private double _lowBudget;
    private double _lowBudgetAt = -1;
    private ushort _nextLargeId;
    private readonly Dictionary<ushort, LargeIncoming> _largeIn = new();

    private double _lastReceive;
    private double _smoothedRtt = 0.1;

    /// <summary>Dirección del otro extremo.</summary>
    public IPEndPoint EndPoint { get; }

    public ConnectionState State { get; internal set; } = ConnectionState.Handshaking;

    /// <summary>Id del jugador asignado por el servidor (0 hasta que se acepta).</summary>
    public ushort Id { get; internal set; }

    /// <summary>Nombre del jugador (lo envía el cliente al conectarse).</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>Versión del mod del otro extremo.</summary>
    public string RemoteVersion { get; internal set; } = string.Empty;

    /// <summary>Latencia de ida y vuelta suavizada, en segundos.</summary>
    public double Rtt => _smoothedRtt;

    /// <summary>Datos libres para las capas superiores.</summary>
    public object UserData { get; set; }

    /// <summary>Por qué se cerró (si está cerrada).</summary>
    public DisconnectReason DisconnectReason { get; internal set; }

    // Saludo
    internal KeyExchange KeyExchange;
    internal byte[] RemotePublicKey;
    internal byte[] RemoteRandom;
    internal byte[] ChallengeDatagram;
    internal int ChallengeLength;

    internal NetConnection(NetPeer peer, IPEndPoint endPoint, BufferPool messagePool, double now) {
        _peer = peer;
        EndPoint = endPoint;
        _messagePool = messagePool;
        _reliableSender = new ReliableSender(messagePool);
        _reliableReceiver = new ReliableReceiver(messagePool);
        _lastReceive = now;
    }

    internal void SetCiphers(PacketCipher send, PacketCipher receive) {
        _sendCipher = send;
        _receiveCipher = receive;
    }

    internal double LastReceive => _lastReceive;

    // =====================================================================================================
    // Envío desde las capas superiores
    // =====================================================================================================

    /// <summary>
    /// Empieza un mensaje fiable: se escribe en el escritor devuelto (el primer byte, el tipo de mensaje) y se termina con
    /// <see cref="EndMessage"/>. Llegará una vez y en orden.
    /// </summary>
    public PacketWriter BeginReliable() => Begin(1);

    /// <summary>
    /// Empieza un mensaje no fiable (estado continuo): se envía en el siguiente tick y no se reenvía si se pierde.
    /// </summary>
    public PacketWriter BeginUnreliable() => Begin(2);

    private PacketWriter Begin(int kind) {
        if (_writing != 0) {
            throw new InvalidOperationException("Ya hay un mensaje a medio escribir en esta conexión");
        }

        _writing = kind;
        _messageWriter.Reset(_messageScratch, 0, _messageScratch.Length);
        return _messageWriter;
    }

    /// <summary>Descarta el mensaje empezado sin enviarlo (quien lo escribía falló a medias).</summary>
    public void CancelMessage() => _writing = 0;

    /// <summary>Termina el mensaje empezado con <see cref="BeginReliable"/> o <see cref="BeginUnreliable"/>.</summary>
    public void EndMessage() {
        var kind = _writing;
        _writing = 0;
        var length = _messageWriter.Length;
        if (length == 0 || State == ConnectionState.Disconnected) {
            return;
        }

        if (kind == 1) {
            var buffer = _messagePool.Rent();
            System.Buffer.BlockCopy(_messageScratch, 0, buffer, 0, length);
            _reliableSender.Enqueue(buffer, length);
        } else if (_unreliableCount < MaxUnreliablePerTick && _unreliableUsed + length <= _unreliableData.Length) {
            System.Buffer.BlockCopy(_messageScratch, 0, _unreliableData, _unreliableUsed, length);
            _unreliableOffset[_unreliableCount] = _unreliableUsed;
            _unreliableLength[_unreliableCount] = length;
            _unreliableCount++;
            _unreliableUsed += length;
        }
    }

    /// <summary>
    /// Envía datos grandes (partida al unirse, skins...) troceados por el canal fiable. El receptor los recibe enteros en
    /// <see cref="INetListener.OnLargeMessage"/> con el mismo <paramref name="kind"/>. Se copian: el array es del llamador.
    /// </summary>
    public void SendLarge(byte kind, byte[] data, int length) => SendLarge(kind, data, length, false);

    /// <summary>
    /// Como <see cref="SendLarge(byte, byte[], int)"/>; con <paramref name="lowPriority"/> va en segundo plano: solo
    /// cuando no hay otra transferencia grande pendiente, a <see cref="LowPriorityBytesPerSecond"/> como mucho y con
    /// pocos trozos por delante de los mensajes normales (una skin no retrasa golpes ni eventos).
    /// </summary>
    public void SendLarge(byte kind, byte[] data, int length, bool lowPriority) {
        if (length > MaxLargeTransfer) {
            throw new ArgumentException("Transferencia demasiado grande");
        }

        var copy = new byte[length];
        System.Buffer.BlockCopy(data, 0, copy, 0, length);
        (lowPriority ? _largeLow : _largeOut).Enqueue(new LargeOutgoing { Id = _nextLargeId++, Kind = kind, Data = copy });
    }

    /// <summary>Bytes de transferencias grandes que faltan por enviar (para mostrar progreso).</summary>
    public int LargeBytesPending {
        get {
            var total = 0;
            foreach (var transfer in _largeOut) {
                total += transfer.Data.Length - transfer.Offset;
            }

            foreach (var transfer in _largeLow) {
                total += transfer.Data.Length - transfer.Offset;
            }

            return total;
        }
    }

    /// <summary>Cierra la conexión avisando al otro extremo.</summary>
    public void Disconnect() => _peer.CloseConnection(this, DisconnectReason.Closed, true);

    // =====================================================================================================
    // Construcción de paquetes (llamado por el peer en cada tick)
    // =====================================================================================================

    /// <summary>Retraso de reenvío de los mensajes fiables según la latencia.</summary>
    private double ResendDelay => System.Math.Max(0.05, _smoothedRtt * 1.5);

    /// <summary>
    /// Encola trozos de transferencias grandes mientras quede sitio en la ventana fiable. Las de baja prioridad, solo sin
    /// otras pendientes, con el presupuesto de bytes que da su ritmo y con pocos mensajes sin confirmar.
    /// </summary>
    private void FeedLargeTransfers(double now) {
        while (_largeOut.Count > 0 && _reliableSender.FreeSlots > 64) {
            EnqueueChunk(_largeOut);
        }

        if (_largeLow.Count == 0) {
            _lowBudgetAt = -1;
            return;
        }

        if (_lowBudgetAt < 0) {
            _lowBudgetAt = now;
        }

        // Presupuesto acumulado como mucho para una décima de segundo (sin ráfagas tras una pausa)
        _lowBudget = System.Math.Min(_lowBudget + (now - _lowBudgetAt) * LowPriorityBytesPerSecond,
            LowPriorityBytesPerSecond / 10.0);
        _lowBudgetAt = now;
        while (_largeOut.Count == 0 && _largeLow.Count > 0 && _lowBudget >= LargeChunkData &&
               _reliableSender.InFlight < LowPriorityMaxInFlight) {
            _lowBudget -= EnqueueChunk(_largeLow);
        }
    }

    /// <summary>Encola el siguiente trozo de la primera transferencia de la cola. Devuelve los bytes de datos.</summary>
    private int EnqueueChunk(Queue<LargeOutgoing> queue) {
        var transfer = queue.Peek();
        var chunk = System.Math.Min(LargeChunkData, transfer.Data.Length - transfer.Offset);
        var buffer = _messagePool.Rent();
        _messageWriter.Reset(buffer, 0, buffer.Length);
        _messageWriter.WriteByte(ControlMessage.LargeChunk);
        _messageWriter.WriteUShort(transfer.Id);
        _messageWriter.WriteByte(transfer.Kind);
        _messageWriter.WriteUInt((uint) transfer.Data.Length);
        _messageWriter.WriteUInt((uint) transfer.Offset);
        _messageWriter.WriteBytes(transfer.Data, transfer.Offset, chunk);
        _reliableSender.Enqueue(buffer, _messageWriter.Length);
        transfer.Offset += chunk;
        if (transfer.Offset >= transfer.Data.Length) {
            queue.Dequeue();
        }

        return chunk;
    }

    /// <summary>Envía los paquetes de este tick: al menos uno (acuses y latido) y más si hay mensajes pendientes.</summary>
    internal void SendTick(double now, byte[] plain, byte[] datagram, PacketWriter writer) {
        if (_sendCipher == null) {
            return;
        }

        FeedLargeTransfers(now);
        var nextUnreliable = 0;
        for (var packet = 0; packet < NetConstants.MaxPacketsPerTick; packet++) {
            var sequence = _localSequence++;
            writer.Reset(plain, 0, NetConstants.MaxDatagramSize - NetConstants.DataHeaderSize - NetConstants.TagSize);
            writer.WriteUInt(_receivedAny ? _remoteSequence : 0);
            writer.WriteULong(_remoteAckBits);

            // Primero lo no fiable del tick (estado reciente), luego lo fiable
            while (nextUnreliable < _unreliableCount) {
                var length = _unreliableLength[nextUnreliable];
                if (1 + 2 + length > writer.Remaining) {
                    break;
                }

                writer.WriteByte((byte) RecordKind.Unreliable);
                writer.WriteVarUInt((uint) length);
                writer.WriteBytes(_unreliableData, _unreliableOffset[nextUnreliable], length);
                nextUnreliable++;
            }

            var reliableLeft = _reliableSender.WriteMessages(writer, sequence, now, ResendDelay);
            SendData(sequence, plain, writer.Length, datagram, now);

            var unreliableLeft = nextUnreliable < _unreliableCount;
            if (!unreliableLeft && !reliableLeft) {
                break;
            }

            FeedLargeTransfers(now);
        }

        _unreliableCount = 0;
        _unreliableUsed = 0;
    }

    private void SendData(uint sequence, byte[] plain, int plainLength, byte[] datagram, double now) {
        // Cabecera en claro: firma, tipo y secuencia (también datos asociados del cifrado)
        WriteHeader(datagram, sequence);
        var cipherLength = _sendCipher.Encrypt(sequence, datagram, 0, NetConstants.DataHeaderSize,
            plain, 0, plainLength, datagram, NetConstants.DataHeaderSize);
        _sentTime[sequence % 1024] = now;
        _sentSequence[sequence % 1024] = sequence;
        _peer.Transport.Send(datagram, 0, NetConstants.DataHeaderSize + cipherLength, EndPoint);
    }

    private static void WriteHeader(byte[] datagram, uint sequence) {
        var magic = NetConstants.Magic;
        datagram[0] = (byte) magic;
        datagram[1] = (byte) (magic >> 8);
        datagram[2] = (byte) (magic >> 16);
        datagram[3] = (byte) (magic >> 24);
        datagram[4] = (byte) DatagramType.Data;
        datagram[5] = (byte) sequence;
        datagram[6] = (byte) (sequence >> 8);
        datagram[7] = (byte) (sequence >> 16);
        datagram[8] = (byte) (sequence >> 24);
    }

    // =====================================================================================================
    // Recepción
    // =====================================================================================================

    /// <summary>
    /// Procesa un datagrama de datos: lo descifra, descarta repetidos, aplica los acuses y entrega los mensajes.
    /// </summary>
    /// <returns>false si no se pudo descifrar (código incorrecto o datos alterados).</returns>
    internal bool ReceiveData(byte[] datagram, int length, byte[] plain, PacketReader reader, double now) {
        if (_receiveCipher == null || length < NetConstants.DataHeaderSize + NetConstants.TagSize) {
            return false;
        }

        var sequence = (uint) (datagram[5] | datagram[6] << 8 | datagram[7] << 16 | datagram[8] << 24);
        var plainLength = _receiveCipher.Decrypt(sequence, datagram, 0, NetConstants.DataHeaderSize,
            datagram, NetConstants.DataHeaderSize, length - NetConstants.DataHeaderSize, plain, 0);
        if (plainLength < 12) {
            return false;
        }

        // Solo tras comprobar que es auténtico se mira si es repetido (un atacante no puede mover la ventana)
        if (!AcceptSequence(sequence)) {
            return true;
        }

        _lastReceive = now;
        reader.Reset(plain, 0, plainLength);
        try {
            var ack = reader.ReadUInt();
            var ackBits = reader.ReadULong();
            ProcessAcks(ack, ackBits, now);

            while (reader.Remaining > 0) {
                var kind = (RecordKind) reader.ReadByte();
                if (kind == RecordKind.Unreliable) {
                    var messageLength = (int) reader.ReadVarUInt();
                    var offset = reader.Position;
                    reader.Skip(messageLength);
                    Deliver(plain, offset, messageLength);
                } else if (kind == RecordKind.Reliable) {
                    var id = reader.ReadUShort();
                    var messageLength = (int) reader.ReadVarUInt();
                    var offset = reader.Position;
                    reader.Skip(messageLength);
                    _reliableReceiver.Receive(id, plain, offset, messageLength, this);
                } else {
                    throw new PacketException("Registro desconocido");
                }

                if (State == ConnectionState.Disconnected) {
                    break;
                }
            }
        } catch (PacketException e) {
            _peer.LogWarning($"Paquete mal formado de {EndPoint}: {e.Message}");
        }

        return true;
    }

    /// <summary>Ventana anti-repetición: acepta cada secuencia una sola vez y nada más viejo de 64 paquetes.</summary>
    private bool AcceptSequence(uint sequence) {
        if (!_receivedAny) {
            _receivedAny = true;
            _remoteSequence = sequence;
            _remoteAckBits = 0;
            return true;
        }

        if (sequence > _remoteSequence) {
            var shift = sequence - _remoteSequence;
            _remoteAckBits = shift >= 64 ? 0 : (_remoteAckBits << (int) shift) | (1UL << (int) (shift - 1));
            _remoteSequence = sequence;
            return true;
        }

        if (sequence == _remoteSequence) {
            return false;
        }

        var back = _remoteSequence - sequence - 1;
        if (back >= 64) {
            return false;
        }

        var bit = 1UL << (int) back;
        if ((_remoteAckBits & bit) != 0) {
            return false;
        }

        _remoteAckBits |= bit;
        return true;
    }

    private void ProcessAcks(uint ack, ulong ackBits, double now) {
        if (ack == 0) {
            return;
        }

        Acknowledge(ack, now);
        for (var i = 0; i < 64; i++) {
            if ((ackBits & (1UL << i)) != 0 && ack > (uint) i + 1) {
                Acknowledge(ack - (uint) i - 1, now);
            }
        }
    }

    private void Acknowledge(uint sequence, double now) {
        var slot = sequence % 1024;
        if (_sentSequence[slot] == sequence) {
            _sentSequence[slot] = 0;
            var sample = now - _sentTime[slot];
            if (sample >= 0 && sample < 5) {
                _smoothedRtt += (sample - _smoothedRtt) * 0.125;
            }
        }

        _reliableSender.OnPacketAcked(sequence);
    }

    /// <summary>Entrega un mensaje (ya ordenado si es fiable): los de control los procesa el peer.</summary>
    void ReliableReceiver.ISink.Deliver(byte[] buffer, int offset, int length) => Deliver(buffer, offset, length);

    private void Deliver(byte[] buffer, int offset, int length) {
        if (length <= 0 || State == ConnectionState.Disconnected) {
            return;
        }

        var type = buffer[offset];
        if (type == ControlMessage.LargeChunk) {
            ReceiveLargeChunk(buffer, offset, length);
            return;
        }

        _peer.DeliverMessage(this, buffer, offset, length);
    }

    private void ReceiveLargeChunk(byte[] buffer, int offset, int length) {
        var reader = _peer.ScratchReader;
        reader.Reset(buffer, offset + 1, offset + length);
        var id = reader.ReadUShort();
        var kind = reader.ReadByte();
        var total = reader.ReadUInt();
        var position = reader.ReadUInt();
        var chunk = reader.Remaining;
        if (total > MaxLargeTransfer || position + (ulong) chunk > total) {
            _peer.LogWarning($"Transferencia grande no válida de {EndPoint}");
            return;
        }

        if (!_largeIn.TryGetValue(id, out var transfer)) {
            transfer = new LargeIncoming { Kind = kind, Data = new byte[total] };
            _largeIn[id] = transfer;
        }

        reader.ReadBytes(transfer.Data, (int) position, chunk);
        transfer.Received += chunk;
        if (transfer.Received >= transfer.Data.Length) {
            _largeIn.Remove(id);
            _peer.DeliverLarge(this, transfer.Kind, transfer.Data);
        }
    }

    /// <summary>Si hay algo pendiente de enviar fuera del tick normal.</summary>
    internal bool HasPendingReliable(double now) =>
        _reliableSender.HasDue(now, ResendDelay) || _largeOut.Count > 0 || _largeLow.Count > 0;

    internal void Release() {
        _reliableSender.Clear();
        _reliableReceiver.Clear();
        _largeOut.Clear();
        _largeLow.Clear();
        _largeIn.Clear();
    }

    private sealed class LargeOutgoing {
        public ushort Id;
        public byte Kind;
        public byte[] Data;
        public int Offset;
    }

    private sealed class LargeIncoming {
        public byte Kind;
        public byte[] Data;
        public int Received;
    }
}

/// <summary>Mensajes internos del transporte (primer byte del mensaje). Los de las capas superiores van de 16 en adelante.</summary>
public static class ControlMessage {
    /// <summary>Cliente → servidor (fiable): nombre y versión del mod.</summary>
    public const byte ConnectRequest = 1;

    /// <summary>Servidor → cliente (fiable): aceptado, con el id asignado.</summary>
    public const byte ConnectAccept = 2;

    /// <summary>Cualquiera (no fiable, se repite): cierra la conexión con el motivo (también para rechazar).</summary>
    public const byte Disconnect = 4;

    /// <summary>Trozo de una transferencia grande.</summary>
    public const byte LargeChunk = 5;

    /// <summary>Primer tipo libre para las capas superiores.</summary>
    public const byte FirstUserType = 16;
}
