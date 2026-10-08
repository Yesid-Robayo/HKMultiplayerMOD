using System.Collections.Generic;

namespace HKMultiplayer.Net.Channels;

/// <summary>
/// Lado emisor del canal fiable. Cada mensaje recibe un id (ushort, con vuelta) y se incluye en los paquetes hasta que
/// llega el acuse de alguno de los paquetes que lo llevaban. Si pasa el tiempo de reenvío sin acuse, se vuelve a meter
/// en el siguiente paquete. Todo con arrays reservados al crear la conexión.
/// </summary>
internal sealed class ReliableSender {
    private const int Window = NetConstants.ReliableWindow;

    /// <summary>Mensajes que caben en un paquete como mucho (para el registro de qué llevaba cada paquete).</summary>
    public const int MaxMessagesPerPacket = 64;

    /// <summary>Paquetes enviados que se recuerdan para atribuir sus acuses.</summary>
    private const int PacketHistory = 4096;

    private readonly BufferPool _pool;

    // Mensajes en vuelo, indexados por id % Window
    private readonly byte[][] _data = new byte[Window][];
    private readonly int[] _length = new int[Window];
    private readonly double[] _lastSent = new double[Window];
    private readonly bool[] _pending = new bool[Window];

    // Qué mensajes llevaba cada paquete enviado, indexado por secuencia % PacketHistory
    private readonly uint[] _packetSequence = new uint[PacketHistory];
    private readonly ushort[][] _packetMessages = new ushort[PacketHistory][];
    private readonly int[] _packetMessageCount = new int[PacketHistory];

    /// <summary>Mensajes que esperan sitio en la ventana (solo si se llena).</summary>
    private readonly Queue<(byte[] Data, int Length)> _backlog = new();

    private ushort _nextId;
    private ushort _oldest;

    public ReliableSender(BufferPool pool) {
        _pool = pool;
        for (var i = 0; i < PacketHistory; i++) {
            _packetMessages[i] = new ushort[MaxMessagesPerPacket];
        }
    }

    /// <summary>Mensajes en vuelo o en espera.</summary>
    public int InFlight => (ushort) (_nextId - _oldest) + _backlog.Count;

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
        var slot = _nextId % Window;
        _data[slot] = data;
        _length[slot] = length;
        _lastSent[slot] = double.MinValue;
        _pending[slot] = true;
        _nextId++;
    }

    /// <summary>
    /// Escribe en el paquete los mensajes que tocan (nuevos o con el reenvío vencido) mientras quepan.
    /// </summary>
    /// <returns>Si quedaron mensajes por enviar que no cupieron.</returns>
    public bool WriteMessages(PacketWriter writer, uint packetSequence, double now, double resendDelay) {
        var record = (int) (packetSequence % PacketHistory);
        ReleaseRecord(record);
        _packetSequence[record] = packetSequence;
        var count = 0;
        var leftOver = false;

        for (var id = _oldest; id != _nextId; id++) {
            var slot = id % Window;
            if (!_pending[slot] || now - _lastSent[slot] < resendDelay) {
                continue;
            }

            // tipo (1) + id (2) + longitud (hasta 2) + datos
            var size = 5 + _length[slot];
            if (size > writer.Remaining || count == MaxMessagesPerPacket) {
                leftOver = true;
                break;
            }

            writer.WriteByte((byte) RecordKind.Reliable);
            writer.WriteUShort(id);
            writer.WriteVarUInt((uint) _length[slot]);
            writer.WriteBytes(_data[slot], 0, _length[slot]);
            _lastSent[slot] = now;
            _packetMessages[record][count++] = id;
        }

        _packetMessageCount[record] = count;
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

    /// <summary>Llegó el acuse de un paquete: sus mensajes ya están entregados.</summary>
    public void OnPacketAcked(uint packetSequence) {
        var record = (int) (packetSequence % PacketHistory);
        if (_packetSequence[record] != packetSequence) {
            return;
        }

        var messages = _packetMessages[record];
        for (var i = 0; i < _packetMessageCount[record]; i++) {
            var slot = messages[i] % Window;
            if (_pending[slot]) {
                _pending[slot] = false;
                _pool.Return(_data[slot]);
                _data[slot] = null;
            }
        }

        _packetMessageCount[record] = 0;
        _packetSequence[record] = 0;

        // Avanzar el inicio de la ventana y dar sitio a los que esperaban
        while (_oldest != _nextId && !_pending[_oldest % Window]) {
            _oldest++;
        }

        while (_backlog.Count > 0 && FreeSlots > 0) {
            var (data, length) = _backlog.Dequeue();
            Store(data, length);
        }
    }

    private void ReleaseRecord(int record) {
        _packetMessageCount[record] = 0;
        _packetSequence[record] = 0;
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

/// <summary>Tipo de cada registro dentro del cuerpo de un paquete.</summary>
internal enum RecordKind : byte {
    Unreliable = 0,
    Reliable = 1
}
