using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
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
/// Una conexión con el otro extremo: cifrado, secuencias y acuses, 4 canales fiables independientes
/// (<see cref="ReliableChannel"/>), canal no fiable y datos grandes. No crea objetos por paquete: todos los búferes
/// son reservados o de reserva. En modo hilo dedicado, el hilo principal solo escribe en la cola de salida y lee
/// estadísticas atómicas; el hilo de red gestiona cifrado, canales, acuses y envío.
/// </summary>
public sealed class NetConnection : ReliableReceiver.ISink {
    /// <summary>
    /// Mensajes no fiables pendientes de enviar en este tick como mucho, y los bytes que caben (ver _unreliableData). El
    /// host reenvía a cada invitado lo de todos los demás: con 4 jugadores, el triple que con 2, y si el juego se paraba
    /// un momento (cargar una sala) lo de varios ticks llegaba junto y lo que no cabía se perdía sin avisar. Ahora cabe
    /// el triple que antes (24 KB, de sobra para los 56 paquetes de un tick) y se avisa si aun así se pierde algo.
    /// </summary>
    private const int MaxUnreliablePerTick = 768;

    private int _droppedUnreliable;

    /// <summary>Transferencia grande más grande que se acepta (protege la memoria).</summary>
    public const int MaxLargeTransfer = 64 * 1024 * 1024;

    /// <summary>Bytes de datos por trozo de una transferencia grande.</summary>
    private const int LargeChunkData = NetConstants.MaxMessageSize - 16;

    /// <summary>Paquetes enviados que se recuerdan para atribuir sus acuses (compartido entre los 4 canales fiables).</summary>
    private const int PacketHistory = 4096;

    private const int MaxMessagesPerPacket = ReliableSender.MaxMessagesPerPacket;

    private readonly NetPeer _peer;
    private readonly BufferPool _messagePool;
    private readonly ReliableSender[] _reliableSenders = new ReliableSender[ReliableChannels.Count];
    private readonly ReliableReceiver[] _reliableReceivers = new ReliableReceiver[ReliableChannels.Count];

    // Qué mensajes fiables (canal + id) llevaba cada paquete enviado, indexado por secuencia % PacketHistory
    private readonly uint[] _packetSequence = new uint[PacketHistory];
    private readonly byte[] _packetRecordChannel = new byte[PacketHistory * MaxMessagesPerPacket];
    private readonly ushort[] _packetRecordId = new ushort[PacketHistory * MaxMessagesPerPacket];
    private readonly int[] _packetRecordCount = new int[PacketHistory];

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

    // Mensajes no fiables del tick actual (solo hilo de red)
    private readonly byte[] _unreliableData = new byte[NetConstants.MaxMessageSize * 24];
    private readonly int[] _unreliableOffset = new int[MaxUnreliablePerTick];
    private readonly int[] _unreliableLength = new int[MaxUnreliablePerTick];
    private int _unreliableCount;
    private int _unreliableUsed;

    // Escritura de un mensaje en curso desde el hilo principal (BeginReliable/BeginUnreliable)
    private readonly PacketWriter _messageWriter = new();
    private byte[] _writingBuffer;
    private int _writing; // 0 = nada, 1 = fiable, 2 = no fiable
    private ReliableChannel _writingChannel = ReliableChannel.World;

    // Escritor propio del hilo de red para trozos de datos grandes (nunca comparte estado con _messageWriter)
    private readonly PacketWriter _chunkWriter = new();

    // Cola de salida del hilo principal hacia el hilo de red (doble cola intercambiada bajo bloqueo corto)
    private readonly object _outboundLock = new();
    private Queue<OutboundCommand> _outbound = new();
    private Queue<OutboundCommand> _outboundWork = new();
    private bool _outboundClosed;

    /// <summary>
    /// Ritmo de las transferencias grandes de baja prioridad (skins): unos 2 MB/s mientras la latencia no sube. Si sube
    /// (la subida del PC no da para tanto y los paquetes hacen cola en el router) baja a
    /// <see cref="LowPriorityCongestedBytesPerSecond"/>, para que no compitan con golpes ni eventos.
    /// </summary>
    public const int LowPriorityBytesPerSecond = 2 * 1024 * 1024;

    /// <summary>Ritmo de baja prioridad con la latencia alta (unos 0,25 MB/s).</summary>
    public const int LowPriorityCongestedBytesPerSecond = 256 * 1024;

    /// <summary>Cuánto puede subir la latencia sobre la más baja vista antes de frenar las transferencias de fondo.</summary>
    private const double CongestionRttMargin = 0.06;

    /// <summary>
    /// Trozos de baja prioridad que se dejan sin confirmar a la vez en el canal Bulk como mucho (unos 160 KB: los
    /// 2 MB/s con 80 ms de latencia), para no llenar la ventana ni las colas del router.
    /// </summary>
    private const int LowPriorityMaxInFlight = 160;

    // Transferencias grandes salientes y entrantes (solo hilo de red; las de baja prioridad, en su propia cola)
    private readonly Queue<LargeOutgoing> _largeOut = new();
    private readonly Queue<LargeOutgoing> _largeLow = new();
    private double _lowBudget;
    private double _lowBudgetAt = -1;
    private ushort _nextLargeId;
    private readonly Dictionary<ushort, LargeIncoming> _largeIn = new();

    // Estadísticas compartidas de forma atómica entre el hilo de red y el hilo principal (HUD / registro)
    private int _state = (int) ConnectionState.Handshaking;
    private double _lastReceive;
    private double _smoothedRtt = 0.1;
    private double _minRtt = double.MaxValue;
    private int _reliablePending;
    private long _reliableResends;
    private int _largePendingBytes;

    /// <summary>Dirección del otro extremo.</summary>
    public IPEndPoint EndPoint { get; }

    public ConnectionState State {
        get => (ConnectionState) Volatile.Read(ref _state);
        internal set => Volatile.Write(ref _state, (int) value);
    }

    /// <summary>Id del jugador asignado por el servidor (0 hasta que se acepta).</summary>
    public ushort Id { get; internal set; }

    /// <summary>Nombre del jugador (lo envía el cliente al conectarse).</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>Versión del mod del otro extremo.</summary>
    public string RemoteVersion { get; internal set; } = string.Empty;

    /// <summary>Latencia de ida y vuelta suavizada, en segundos.</summary>
    public double Rtt => Volatile.Read(ref _smoothedRtt);

    /// <summary>Datos libres para las capas superiores.</summary>
    public object UserData { get; set; }

    /// <summary>Por qué se cerró (si está cerrada).</summary>
    public DisconnectReason DisconnectReason { get; internal set; }

    // Saludo (solo hilo de red)
    internal KeyExchange KeyExchange;
    internal byte[] RemotePublicKey;
    internal byte[] RemoteRandom;
    internal byte[] ChallengeDatagram;
    internal int ChallengeLength;

    internal NetConnection(NetPeer peer, IPEndPoint endPoint, BufferPool messagePool, double now) {
        _peer = peer;
        EndPoint = endPoint;
        _messagePool = messagePool;
        for (var i = 0; i < ReliableChannels.Count; i++) {
            _reliableSenders[i] = new ReliableSender(messagePool);
            _reliableReceivers[i] = new ReliableReceiver(messagePool);
        }

        Volatile.Write(ref _lastReceive, now);
    }

    internal void SetCiphers(PacketCipher send, PacketCipher receive) {
        _sendCipher = send;
        _receiveCipher = receive;
    }

    internal double LastReceive => Volatile.Read(ref _lastReceive);

    /// <summary>Segundos desde el último paquete auténtico del otro extremo (para el aviso de conexión inestable).</summary>
    public double SilentSeconds => NetPeer.Now - Volatile.Read(ref _lastReceive);

    /// <summary>Mensajes fiables pendientes de confirmar entre todos los canales.</summary>
    public int ReliablePending => Volatile.Read(ref _reliablePending);

    /// <summary>Mensajes fiables reenviados en total entre todos los canales (registro y pruebas).</summary>
    public long ReliableResends => Volatile.Read(ref _reliableResends);

    /// <summary>Si ya se avisó en el registro de que no llega nada (para avisar también cuando vuelva a llegar).</summary>
    internal bool SilenceReported;

    /// <summary>
    /// Estado de la conexión para el registro: con él, un corte o un silencio se puede diagnosticar con solo el
    /// ModLog.txt (red saturada, el otro dejó de enviar o el juego se paró).
    /// </summary>
    internal string DescribeStats(double now) {
        var smoothed = Volatile.Read(ref _smoothedRtt);
        var minRtt = Volatile.Read(ref _minRtt);
        var min = minRtt == double.MaxValue ? 0 : minRtt;
        return $"latencia {smoothed * 1000:0} ms (mínima {min * 1000:0} ms), sin recibir desde hace " +
               $"{now - Volatile.Read(ref _lastReceive):0.0} s, fiables sin confirmar {ReliablePending}, datos grandes pendientes " +
               $"{LargeBytesPending / 1024} KB, reenvíos {ReliableResends}, enviado {_bytesSent / 1024} KB, " +
               $"recibido {_bytesReceived / 1024} KB";
    }

    /// <summary>Si ya llegó algún paquete auténtico (cifrado con las claves del código).</summary>
    internal bool ReceivedAny => _receivedAny;

    private void UpdateSenderStats() {
        var pending = 0;
        long resends = 0;
        for (var i = 0; i < ReliableChannels.Count; i++) {
            pending += _reliableSenders[i].InFlight;
            resends += _reliableSenders[i].Resends;
        }

        Volatile.Write(ref _reliablePending, pending);
        Volatile.Write(ref _reliableResends, resends);
    }

    // =====================================================================================================
    // Envío desde las capas superiores (hilo principal)
    // =====================================================================================================

    /// <summary>
    /// Empieza un mensaje fiable en el canal por defecto (<see cref="ReliableChannel.World"/>): se escribe en el
    /// escritor devuelto (el primer byte, el tipo de mensaje) y se termina con <see cref="EndMessage"/>.
    /// </summary>
    /// <param name="reserve">Bytes que se dejan libres al final (el host añade quién lo envía al reenviarlo).</param>
    public PacketWriter BeginReliable(int reserve = 0) => Begin(1, ReliableChannel.World, reserve);

    /// <summary>
    /// Empieza un mensaje fiable en el canal indicado (<see cref="ReliableChannel"/>). Llegará una vez y en orden
    /// dentro de ese canal, sin quedar retenido por paquetes perdidos de otros canales.
    /// </summary>
    /// <param name="channel">Canal fiable independiente por el que viajará el mensaje.</param>
    /// <param name="reserve">Bytes que se dejan libres al final (el host añade quién lo envía al reenviarlo).</param>
    public PacketWriter BeginReliable(ReliableChannel channel, int reserve = 0) => Begin(1, channel, reserve);

    /// <summary>
    /// Empieza un mensaje no fiable (estado continuo): se envía en el siguiente tick y no se reenvía si se pierde.
    /// </summary>
    public PacketWriter BeginUnreliable(int reserve = 0) => Begin(2, ReliableChannel.World, reserve);

    /// <summary>Mensajes empezados que se descartaron porque quien los escribía falló a medias.</summary>
    public int AbandonedMessages { get; private set; }

    private PacketWriter Begin(int kind, ReliableChannel channel, int reserve) {
        // Uno a medio escribir es de alguien que falló entre Begin y EndMessage: se descarta y se devuelve su búfer.
        // Antes se lanzaba una excepción y todo lo siguiente a esa conexión fallaba para siempre
        if (_writing != 0) {
            AbandonedMessages++;
            if (_writingBuffer != null) {
                _messagePool.Return(_writingBuffer);
                _writingBuffer = null;
            }
        }

        var buffer = _messagePool.Rent();
        _writing = kind;
        _writingChannel = channel;
        _writingBuffer = buffer;
        _messageWriter.Reset(buffer, 0, buffer.Length - Math.Max(0, reserve));
        return _messageWriter;
    }

    /// <summary>Descarta el mensaje empezado sin enviarlo (quien lo escribía falló a medias).</summary>
    public void CancelMessage() {
        _writing = 0;
        if (_writingBuffer != null) {
            _messagePool.Return(_writingBuffer);
            _writingBuffer = null;
        }
    }

    /// <summary>Termina el mensaje empezado con <see cref="BeginReliable"/> o <see cref="BeginUnreliable"/>.</summary>
    public void EndMessage() {
        var kind = _writing;
        var channel = _writingChannel;
        var buffer = _writingBuffer;
        _writing = 0;
        _writingBuffer = null;
        if (buffer == null) {
            return;
        }

        var length = _messageWriter.Length;
        if (length == 0 || State == ConnectionState.Disconnected) {
            _messagePool.Return(buffer);
            return;
        }

        if (_peer.IsThreaded) {
            bool queued;
            lock (_outboundLock) {
                if (_outboundClosed) {
                    queued = false;
                } else {
                    _outbound.Enqueue(new OutboundCommand {
                        Kind = kind == 1 ? OutboundKind.Reliable : OutboundKind.Unreliable,
                        Channel = channel,
                        Buffer = buffer,
                        Length = length
                    });
                    queued = true;
                }
            }

            if (!queued) {
                _messagePool.Return(buffer);
            } else {
                _peer.Wake();
            }

            return;
        }

        if (kind == 1) {
            _reliableSenders[(int) channel].Enqueue(buffer, length);
            UpdateSenderStats();
        } else {
            EnqueueUnreliableOwned(buffer, length);
        }
    }

    /// <summary>Envía un mensaje de control fiable desde el propio hilo de red (saludo).</summary>
    internal void EnqueueControlReliable(byte[] data, int length, ReliableChannel channel = ReliableChannel.World) {
        var buffer = _messagePool.Rent();
        System.Buffer.BlockCopy(data, 0, buffer, 0, length);
        _reliableSenders[(int) channel].Enqueue(buffer, length);
        UpdateSenderStats();
    }

    /// <summary>Envía un mensaje de control no fiable desde el propio hilo de red (aviso de cierre).</summary>
    internal void EnqueueControlUnreliable(byte[] data, int length) {
        AppendUnreliable(data, 0, length);
    }

    private void EnqueueUnreliableOwned(byte[] buffer, int length) {
        try {
            AppendUnreliable(buffer, 0, length);
        } finally {
            _messagePool.Return(buffer);
        }
    }

    private void AppendUnreliable(byte[] data, int offset, int length) {
        if (_unreliableCount < MaxUnreliablePerTick && _unreliableUsed + length <= _unreliableData.Length) {
            System.Buffer.BlockCopy(data, offset, _unreliableData, _unreliableUsed, length);
            _unreliableOffset[_unreliableCount] = _unreliableUsed;
            _unreliableLength[_unreliableCount] = length;
            _unreliableCount++;
            _unreliableUsed += length;
        } else if (++_droppedUnreliable == 1 || _droppedUnreliable % 500 == 0) {
            _peer.LogWarning($"No cabe en el tick un mensaje no fiable para {EndPoint}: se descarta (van {_droppedUnreliable})");
        }
    }

    /// <summary>
    /// Envía datos grandes (partida al unirse, skins...) troceados por el canal <see cref="ReliableChannel.Bulk"/>.
    /// El receptor los recibe enteros en <see cref="INetListener.OnLargeMessage"/> con el mismo
    /// <paramref name="kind"/>. Se copian: el array es del llamador.
    /// </summary>
    public void SendLarge(byte kind, byte[] data, int length) => SendLarge(kind, data, length, false);

    /// <summary>
    /// Como <see cref="SendLarge(byte, byte[], int)"/>; con <paramref name="lowPriority"/> va en segundo plano: solo
    /// cuando no hay otra transferencia grande pendiente y a <see cref="LowPriorityBytesPerSecond"/> como mucho.
    /// </summary>
    public void SendLarge(byte kind, byte[] data, int length, bool lowPriority) {
        if (length > MaxLargeTransfer) {
            throw new ArgumentException("Transferencia demasiado grande");
        }

        if (State == ConnectionState.Disconnected) {
            return;
        }

        var copy = new byte[length];
        System.Buffer.BlockCopy(data, 0, copy, 0, length);
        if (_peer.IsThreaded) {
            lock (_outboundLock) {
                if (_outboundClosed) {
                    return;
                }

                _outbound.Enqueue(new OutboundCommand {
                    Kind = OutboundKind.Large,
                    LargeKind = kind,
                    Buffer = copy,
                    Length = length,
                    LowPriority = lowPriority
                });
                Interlocked.Add(ref _largePendingBytes, length);
            }

            _peer.Wake();
            return;
        }

        Interlocked.Add(ref _largePendingBytes, length);
        (lowPriority ? _largeLow : _largeOut).Enqueue(new LargeOutgoing { Id = _nextLargeId++, Kind = kind, Data = copy });
    }

    /// <summary>Bytes de transferencias grandes que faltan por enviar (para mostrar progreso).</summary>
    public int LargeBytesPending => Math.Max(0, Volatile.Read(ref _largePendingBytes));

    /// <summary>Cierra la conexión avisando al otro extremo.</summary>
    public void Disconnect() => Disconnect(DisconnectReason.Closed);

    /// <summary>Cierra la conexión avisando al otro extremo de por qué.</summary>
    public void Disconnect(DisconnectReason reason) {
        if (_peer.IsThreaded) {
            lock (_outboundLock) {
                if (_outboundClosed) {
                    return;
                }

                _outbound.Enqueue(new OutboundCommand {
                    Kind = OutboundKind.Disconnect,
                    DisconnectReason = reason
                });
            }

            _peer.Wake();
            return;
        }

        _peer.CloseConnection(this, reason, true);
    }

    /// <summary>
    /// Drena en el hilo de red los comandos encolados por el hilo principal (mensajes, datos grandes y cierres).
    /// Devuelve false si la conexión se cerró durante el drenado.
    /// </summary>
    internal bool DrainOutbound() {
        Queue<OutboundCommand> batch;
        lock (_outboundLock) {
            if (_outbound.Count == 0) {
                return State != ConnectionState.Disconnected;
            }

            batch = _outbound;
            _outbound = _outboundWork;
            _outboundWork = batch;
        }

        var touchedReliable = false;
        while (batch.Count > 0) {
            var cmd = batch.Dequeue();
            if (State == ConnectionState.Disconnected) {
                ReturnOutboundBuffer(cmd);
                continue;
            }

            switch (cmd.Kind) {
                case OutboundKind.Reliable:
                    _reliableSenders[(int) cmd.Channel].Enqueue(cmd.Buffer, cmd.Length);
                    touchedReliable = true;
                    break;
                case OutboundKind.Unreliable:
                    EnqueueUnreliableOwned(cmd.Buffer, cmd.Length);
                    break;
                case OutboundKind.Large:
                    (cmd.LowPriority ? _largeLow : _largeOut).Enqueue(new LargeOutgoing {
                        Id = _nextLargeId++,
                        Kind = cmd.LargeKind,
                        Data = cmd.Buffer
                    });
                    break;
                case OutboundKind.Disconnect:
                    _peer.CloseConnection(this, cmd.DisconnectReason, true);
                    break;
            }
        }

        if (touchedReliable) {
            UpdateSenderStats();
        }

        return State != ConnectionState.Disconnected;
    }

    private void ReturnOutboundBuffer(OutboundCommand cmd) {
        if ((cmd.Kind == OutboundKind.Reliable || cmd.Kind == OutboundKind.Unreliable) && cmd.Buffer != null) {
            _messagePool.Return(cmd.Buffer);
        }
    }

    // =====================================================================================================
    // Construcción de paquetes (llamado por el peer en cada tick, en el hilo de red)
    // =====================================================================================================

    /// <summary>Sin recibir nada durante este tiempo, los reenvíos se espacian (ver <see cref="ResendDelay"/>).</summary>
    private const double ResendBackoffAfter = 1.0;

    /// <summary>Reenvío más espaciado durante un corte: cada mensaje pendiente, una vez por segundo como mucho.</summary>
    private const double MaxResendDelay = 1.0;

    /// <summary>
    /// Retraso de reenvío de los mensajes fiables según la latencia. Durante un corte (no llega nada del otro lado) se
    /// espacia con el silencio hasta <see cref="MaxResendDelay"/>: en cuanto llega algo, vuelve al ritmo normal y lo
    /// pendiente sale en el siguiente tick.
    /// </summary>
    private double ResendDelay(double now) {
        var delay = System.Math.Max(0.05, _smoothedRtt * 1.5);
        var silent = now - _lastReceive;
        if (silent <= ResendBackoffAfter) {
            return delay;
        }

        return System.Math.Max(delay, System.Math.Min(MaxResendDelay, silent * 0.25));
    }

    /// <summary>
    /// Encola trozos de transferencias grandes en el canal <see cref="ReliableChannel.Bulk"/> mientras quede sitio en
    /// su ventana. Las de baja prioridad, solo sin otras pendientes, con su presupuesto de bytes y con pocos trozos sin
    /// confirmar.
    /// </summary>
    private void FeedLargeTransfers(double now) {
        var bulk = _reliableSenders[(int) ReliableChannel.Bulk];
        var enqueued = false;
        while (_largeOut.Count > 0 && bulk.FreeSlots > 64) {
            EnqueueChunk(_largeOut, bulk);
            enqueued = true;
        }

        if (_largeLow.Count == 0) {
            _lowBudgetAt = -1;
            if (enqueued) {
                UpdateSenderStats();
            }

            return;
        }

        if (_lowBudgetAt < 0) {
            _lowBudgetAt = now;
        }

        // Presupuesto acumulado como mucho para un tick: cada acuse cubre los últimos 64 paquetes, y una ráfaga mayor se
        // quedaría sin confirmar y se reenviaría. Con la latencia alta, más lento
        var rate = _smoothedRtt > _minRtt + CongestionRttMargin
            ? LowPriorityCongestedBytesPerSecond
            : LowPriorityBytesPerSecond;
        _lowBudget = System.Math.Min(_lowBudget + (now - _lowBudgetAt) * rate, (double) rate / NetConstants.TickRate);
        _lowBudgetAt = now;
        while (_largeOut.Count == 0 && _largeLow.Count > 0 && _lowBudget >= LargeChunkData &&
               bulk.InFlight < LowPriorityMaxInFlight) {
            _lowBudget -= EnqueueChunk(_largeLow, bulk);
            enqueued = true;
        }

        if (enqueued) {
            UpdateSenderStats();
        }
    }

    /// <summary>Encola el siguiente trozo de la primera transferencia de la cola en Bulk. Devuelve los bytes de datos.</summary>
    private int EnqueueChunk(Queue<LargeOutgoing> queue, ReliableSender bulk) {
        var transfer = queue.Peek();
        var chunk = System.Math.Min(LargeChunkData, transfer.Data.Length - transfer.Offset);
        var buffer = _messagePool.Rent();
        _chunkWriter.Reset(buffer, 0, buffer.Length);
        _chunkWriter.WriteByte(ControlMessage.LargeChunk);
        _chunkWriter.WriteUShort(transfer.Id);
        _chunkWriter.WriteByte(transfer.Kind);
        _chunkWriter.WriteUInt((uint) transfer.Data.Length);
        _chunkWriter.WriteUInt((uint) transfer.Offset);
        _chunkWriter.WriteBytes(transfer.Data, transfer.Offset, chunk);
        bulk.Enqueue(buffer, _chunkWriter.Length);
        transfer.Offset += chunk;
        Interlocked.Add(ref _largePendingBytes, -chunk);
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
        var resendDelay = ResendDelay(now);
        for (var packet = 0; packet < NetConstants.MaxPacketsPerTick; packet++) {
            var sequence = _localSequence++;
            writer.Reset(plain, 0, NetConstants.MaxDatagramSize - NetConstants.DataHeaderSize - NetConstants.TagSize);
            writer.WriteUInt(_receivedAny ? _remoteSequence : 0);
            writer.WriteULong(_remoteAckBits);

            // Primero lo no fiable del tick (estado más reciente)
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

            // Después los 4 canales fiables en orden de prioridad: Combat → World → Social → Bulk
            var record = (int) (sequence % PacketHistory);
            var recordBase = record * MaxMessagesPerPacket;
            var recordCount = 0;
            _packetSequence[record] = sequence;

            var reliableLeft = false;
            for (var c = 0; c < ReliableChannels.Count; c++) {
                if (_reliableSenders[c].WriteMessages(writer, (byte) c, _packetRecordChannel, _packetRecordId,
                        recordBase, ref recordCount, MaxMessagesPerPacket, now, resendDelay)) {
                    reliableLeft = true;
                }
            }

            _packetRecordCount[record] = recordCount;
            SendData(sequence, plain, writer.Length, datagram, now);

            var unreliableLeft = nextUnreliable < _unreliableCount;
            if (!unreliableLeft && !reliableLeft) {
                break;
            }

            FeedLargeTransfers(now);
        }

        _unreliableCount = 0;
        _unreliableUsed = 0;
        UpdateSenderStats();
    }

    private void SendData(uint sequence, byte[] plain, int plainLength, byte[] datagram, double now) {
        // Cabecera en claro: firma, tipo y secuencia (también datos asociados del cifrado)
        WriteHeader(datagram, sequence);
        var cipherLength = _sendCipher.Encrypt(sequence, datagram, 0, NetConstants.DataHeaderSize,
            plain, 0, plainLength, datagram, NetConstants.DataHeaderSize);
        _sentTime[sequence % 1024] = now;
        _sentSequence[sequence % 1024] = sequence;
        _peer.Transport.Send(datagram, 0, NetConstants.DataHeaderSize + cipherLength, EndPoint);
        _bytesSent += NetConstants.DataHeaderSize + cipherLength;
    }

    // Bytes de datos enviados y recibidos en total (para el registro)
    private long _bytesSent;
    private long _bytesReceived;

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
    // Recepción (hilo de red)
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

        if (SilenceReported) {
            SilenceReported = false;
            // Sin cortar la conexión: lo fiable que quedó pendiente en los dos lados se reenvía ahora, en orden
            _peer.LogWarning($"Vuelve a llegar la red de {_peer.Describe(this)} tras {now - _lastReceive:0.0} s sin nada; " +
                             $"se ponen al día {ReliablePending} mensajes fiables pendientes");
        }

        Volatile.Write(ref _lastReceive, now);
        _bytesReceived += length;
        reader.Reset(plain, 0, plainLength);
        try {
            var ack = reader.ReadUInt();
            var ackBits = reader.ReadULong();
            ProcessAcks(ack, ackBits, now);

            while (reader.Remaining > 0) {
                var kindByte = reader.ReadByte();
                if (kindByte == (byte) RecordKind.Unreliable) {
                    var messageLength = (int) reader.ReadVarUInt();
                    var offset = reader.Position;
                    reader.Skip(messageLength);
                    Deliver(plain, offset, messageLength);
                } else if (kindByte >= (byte) RecordKind.ReliableBase &&
                           kindByte < (byte) RecordKind.ReliableBase + ReliableChannels.Count) {
                    var channel = kindByte - (byte) RecordKind.ReliableBase;
                    var id = reader.ReadUShort();
                    var messageLength = (int) reader.ReadVarUInt();
                    var offset = reader.Position;
                    reader.Skip(messageLength);
                    _reliableReceivers[channel].Receive(id, plain, offset, messageLength, this);
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

        var touchedChannels = 0;
        Acknowledge(ack, now, ref touchedChannels);
        for (var i = 0; i < 64; i++) {
            if ((ackBits & (1UL << i)) != 0 && ack > (uint) i + 1) {
                Acknowledge(ack - (uint) i - 1, now, ref touchedChannels);
            }
        }

        if (touchedChannels != 0) {
            for (var c = 0; c < ReliableChannels.Count; c++) {
                if ((touchedChannels & (1 << c)) != 0) {
                    _reliableSenders[c].AdvanceWindow();
                }
            }

            UpdateSenderStats();
        }
    }

    private void Acknowledge(uint sequence, double now, ref int touchedChannels) {
        var slot = sequence % 1024;
        if (_sentSequence[slot] == sequence) {
            _sentSequence[slot] = 0;
            var sample = now - _sentTime[slot];
            if (sample >= 0 && sample < 5) {
                var smoothed = _smoothedRtt + (sample - _smoothedRtt) * 0.125;
                Volatile.Write(ref _smoothedRtt, smoothed);
                if (sample < _minRtt) {
                    Volatile.Write(ref _minRtt, sample);
                }
            }
        }

        var record = (int) (sequence % PacketHistory);
        if (_packetSequence[record] != sequence) {
            return;
        }

        var count = _packetRecordCount[record];
        var recordBase = record * MaxMessagesPerPacket;
        for (var i = 0; i < count; i++) {
            var channel = _packetRecordChannel[recordBase + i];
            _reliableSenders[channel].AckMessage(_packetRecordId[recordBase + i]);
            touchedChannels |= 1 << channel;
        }

        _packetRecordCount[record] = 0;
        _packetSequence[record] = 0;
    }

    /// <summary>Entrega un mensaje (ya ordenado si es fiable) desde un búfer temporal.</summary>
    void ReliableReceiver.ISink.Deliver(byte[] buffer, int offset, int length) => Deliver(buffer, offset, length);

    /// <summary>Entrega un mensaje guardado en un búfer prestado de <see cref="BufferPool"/> sin volver a copiarlo.</summary>
    void ReliableReceiver.ISink.DeliverOwned(byte[] pooledBuffer, int length) {
        if (length <= 0 || State == ConnectionState.Disconnected) {
            _messagePool.Return(pooledBuffer);
            return;
        }

        try {
            var type = pooledBuffer[0];
            if (type == ControlMessage.LargeChunk) {
                try {
                    ReceiveLargeChunk(pooledBuffer, 0, length);
                } finally {
                    _messagePool.Return(pooledBuffer);
                }

                return;
            }

            if (type < ControlMessage.FirstUserType) {
                try {
                    _peer.DeliverControl(this, pooledBuffer, 0, length);
                } finally {
                    _messagePool.Return(pooledBuffer);
                }

                return;
            }

            if (_peer.IsThreaded) {
                // En modo hilo dedicado el evento toma la propiedad del búfer y el hilo principal lo devuelve al terminar
                _peer.EnqueueUserMessage(this, pooledBuffer, length);
                return;
            }

            try {
                _peer.DeliverUserMessage(this, pooledBuffer, 0, length);
            } finally {
                _messagePool.Return(pooledBuffer);
            }
        } catch (Exception e) {
            _messagePool.Return(pooledBuffer);
            ReportDeliverError(e);
        }
    }

    private void Deliver(byte[] buffer, int offset, int length) {
        if (length <= 0 || State == ConnectionState.Disconnected) {
            return;
        }

        // Un fallo al procesar un mensaje se queda en ese mensaje. Antes salía hasta ReceiveData: se perdían los registros
        // fiables que iban detrás en el mismo paquete (ya confirmado, nunca se reenvían) y el receptor los esperaba para
        // siempre; o, en el canal fiable, los guardados desordenados no se entregaban
        try {
            var type = buffer[offset];
            if (type == ControlMessage.LargeChunk) {
                ReceiveLargeChunk(buffer, offset, length);
                return;
            }

            if (type < ControlMessage.FirstUserType) {
                _peer.DeliverControl(this, buffer, offset, length);
                return;
            }

            if (_peer.IsThreaded) {
                var copy = _messagePool.Rent();
                System.Buffer.BlockCopy(buffer, offset, copy, 0, length);
                _peer.EnqueueUserMessage(this, copy, length);
                return;
            }

            _peer.DeliverUserMessage(this, buffer, offset, length);
        } catch (Exception e) {
            ReportDeliverError(e);
        }
    }

    private void ReportDeliverError(Exception e) {
        if (++_deliverErrors <= 20 || _deliverErrors % 500 == 0) {
            _peer.LogWarning($"Fallo al procesar un mensaje de {EndPoint} (van {_deliverErrors}): {e}");
        }
    }

    private int _deliverErrors;

    /// <summary>Transferencias grandes a medio recibir a la vez, como mucho (cada una reserva su tamaño entero).</summary>
    private const int MaxLargeIncoming = 2;

    /// <summary>Una transferencia grande sin trozos nuevos en este tiempo se abandona.</summary>
    private const double LargeIncomingTimeout = 30;

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

        var now = NetPeer.Now;
        if (!_largeIn.TryGetValue(id, out var transfer)) {
            // Cada transferencia reserva su tamaño entero con el primer trozo: sin límite, unos pocos paquetes de 1 KB con
            // ids distintos agotaban la memoria (hasta 64 MB cada uno). Las abandonadas caducan
            PruneLargeIncoming(now);
            if (_largeIn.Count >= MaxLargeIncoming) {
                _peer.LogWarning($"Demasiadas transferencias grandes a la vez de {EndPoint}: se ignora una más");
                return;
            }

            transfer = new LargeIncoming { Kind = kind, Data = new byte[total] };
            _largeIn[id] = transfer;
        } else if (transfer.Data.Length != total || transfer.Kind != kind) {
            // El mismo id con otro tamaño: el trozo no cabría donde dice
            _peer.LogWarning($"Transferencia grande incoherente de {EndPoint}: se abandona");
            _largeIn.Remove(id);
            return;
        }

        transfer.LastChunk = now;
        reader.ReadBytes(transfer.Data, (int) position, chunk);
        transfer.Received += chunk;
        if (transfer.Received >= transfer.Data.Length) {
            _largeIn.Remove(id);
            _peer.DeliverLarge(this, transfer.Kind, transfer.Data);
        }
    }

    /// <summary>Si hay algo pendiente de enviar fuera del tick normal.</summary>
    internal bool HasPendingReliable(double now) {
        var delay = ResendDelay(now);
        for (var c = 0; c < ReliableChannels.Count; c++) {
            if (_reliableSenders[c].HasDue(now, delay)) {
                return true;
            }
        }

        return _largeOut.Count > 0 || _largeLow.Count > 0;
    }

    internal void Release() {
        lock (_outboundLock) {
            _outboundClosed = true;
            while (_outbound.Count > 0) {
                ReturnOutboundBuffer(_outbound.Dequeue());
            }
        }

        for (var i = 0; i < ReliableChannels.Count; i++) {
            _reliableSenders[i].Clear();
            _reliableReceivers[i].Clear();
        }

        _largeOut.Clear();
        _largeLow.Clear();
        _largeIn.Clear();
        Volatile.Write(ref _reliablePending, 0);
        Interlocked.Exchange(ref _largePendingBytes, 0);
    }

    private enum OutboundKind : byte {
        Reliable,
        Unreliable,
        Large,
        Disconnect
    }

    private struct OutboundCommand {
        public OutboundKind Kind;
        public ReliableChannel Channel;
        public byte LargeKind;
        public bool LowPriority;
        public DisconnectReason DisconnectReason;
        public byte[] Buffer;
        public int Length;
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
        public double LastChunk;
    }

    private readonly List<ushort> _staleLarge = new();

    private void PruneLargeIncoming(double now) {
        _staleLarge.Clear();
        foreach (var pair in _largeIn) {
            if (now - pair.Value.LastChunk > LargeIncomingTimeout) {
                _staleLarge.Add(pair.Key);
            }
        }

        foreach (var id in _staleLarge) {
            _largeIn.Remove(id);
        }
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
