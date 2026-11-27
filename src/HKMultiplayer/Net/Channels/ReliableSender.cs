using System.Collections.Generic;

namespace HKMultiplayer.Net.Channels;

/// <summary>
/// Lado emisor de un canal fiable. Cada mensaje recibe un id (ushort, con vuelta) y se incluye en los paquetes hasta
/// que llega el acuse de alguno de los paquetes que lo llevaban. Si pasa el tiempo de reenvío sin acuse, se vuelve a
/// meter en el siguiente paquete. El registro de qué llevaba cada paquete vive compartido en <see cref="NetConnection"/>
/// para todos los canales, evitando multiplicar la memoria por canal.
/// </summary>
internal sealed class ReliableSender {
    private const int Window = NetConstants.ReliableWindow;

    /// <summary>Mensajes fiables totales que caben en un paquete como mucho (entre todos los canales).</summary>
    public const int MaxMessagesPerPacket = 64;

    private readonly BufferPool _pool;

    // Mensajes en vuelo, indexados por id % Window
    private readonly byte[][] _data = new byte[Window][];
    private readonly int[] _length = new int[Window];
    private readonly double[] _lastSent = new double[Window];
    private readonly bool[] _pending = new bool[Window];
    private readonly ushort[] _slotId = new ushort[Window];

    /// <summary>Mensajes que esperan sitio en la ventana (solo si se llena).</summary>
    private readonly Queue<(byte[] Data, int Length)> _backlog = new();

    private ushort _nextId;
    private ushort _oldest;

    public ReliableSender(BufferPool pool) {
        _pool = pool;
    }

    /// <summary>Mensajes en vuelo o en espera.</summary>
    public int InFlight => (ushort) (_nextId - _oldest) + _backlog.Count;

    /// <summary>Mensajes que se volvieron a enviar porque no llegó su acuse a tiempo (para el registro).</summary>
    public long Resends { get; private set; }

    /// <summary>Sitio libre en la ventana.</summary>
    public int FreeSlots => Window - (ushort) (_nextId - _oldest);

    /// <summary>Encola un mensaje. El búfer pasa a ser del canal (de la reserva <see cref="_pool"/>).</summary>
    public void Enqueue(byte[] data, int length) {
        if (_backlog.Count > 0 || FreeSlots <= 0) {
            _backlog.Enqueue((data, length));
            return;
        }

        Store(data, length);
    }

    private void Store(byte[] data, int length) {
        var id = _nextId++;
        var slot = id % Window;
        _data[slot] = data;
        _length[slot] = length;
        _lastSent[slot] = double.MinValue;
        _pending[slot] = true;
        _slotId[slot] = id;
    }

    /// <summary>
    /// Escribe en el paquete los mensajes que tocan (nuevos o con el reenvío vencido) mientras quepan y haya hueco en
    /// el registro compartido de ese paquete.
    /// </summary>
    /// <returns>Si quedaron mensajes por enviar que no cupieron.</returns>
    public bool WriteMessages(PacketWriter writer, byte channel, byte[] packetChannels, ushort[] packetIds,
        int recordBase, ref int recordCount, int maxRecords, double now, double resendDelay) {
        var kind = (byte) ((byte) RecordKind.ReliableBase + channel);
        var leftOver = false;

        for (var id = _oldest; id != _nextId; id++) {
            var slot = id % Window;
            if (!_pending[slot] || now - _lastSent[slot] < resendDelay) {
                continue;
            }

            // tipo (1) + id (2) + longitud (hasta 2) + datos
            var size = 5 + _length[slot];
            if (size > writer.Remaining || recordCount == maxRecords) {
                leftOver = true;
                break;
            }

            writer.WriteByte(kind);
            writer.WriteUShort(id);
            writer.WriteVarUInt((uint) _length[slot]);
            writer.WriteBytes(_data[slot], 0, _length[slot]);
            if (_lastSent[slot] != double.MinValue) {
                Resends++;
            }

            _lastSent[slot] = now;
            packetChannels[recordBase + recordCount] = channel;
            packetIds[recordBase + recordCount] = id;
            recordCount++;
        }

        return leftOver;
    }

    /// <summary>Si hay algún mensaje cuyo envío o reenvío ya toca.</summary>
    public bool HasDue(double now, double resendDelay) {
        for (var id = _oldest; id != _nextId; id++) {
            var slot = id % Window;
            if (_pending[slot] && now - _lastSent[slot] >= resendDelay) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marca un mensaje concreto como confirmado (su búfer vuelve a la reserva).</summary>
    public void AckMessage(ushort id) {
        var slot = id % Window;
        if (_pending[slot] && _slotId[slot] == id) {
            _pending[slot] = false;
            _pool.Return(_data[slot]);
            _data[slot] = null;
        }
    }

    /// <summary>Avanza el inicio de la ventana tras confirmar mensajes y da sitio a los que esperaban.</summary>
    public void AdvanceWindow() {
        while (_oldest != _nextId && !_pending[_oldest % Window]) {
            _oldest++;
        }

        while (_backlog.Count > 0 && FreeSlots > 0) {
            var (data, length) = _backlog.Dequeue();
            Store(data, length);
        }
    }

    /// <summary>Libera todos los búferes (al cerrar la conexión).</summary>
    public void Clear() {
        for (var i = 0; i < Window; i++) {
            if (_data[i] != null) {
                _pool.Return(_data[i]);
                _data[i] = null;
            }

            _pending[i] = false;
        }

        while (_backlog.Count > 0) {
            _pool.Return(_backlog.Dequeue().Data);
        }

        _oldest = _nextId;
    }
}

/// <summary>
/// Tipo de cada registro dentro del cuerpo de un paquete: 0 no fiable; 1..4 fiable en los canales
/// <see cref="ReliableChannel"/> (0..3), sin añadir bytes extra por mensaje.
/// </summary>
internal enum RecordKind : byte {
    Unreliable = 0,
    ReliableBase = 1
}
