namespace HKMultiplayer.Net.Channels;

/// <summary>
/// Lado receptor del canal fiable: entrega cada mensaje una sola vez y en el orden en que se envió. Los que llegan antes
/// de tiempo se guardan (copiados a búferes de la reserva) hasta que llegan los que faltan.
/// </summary>
internal sealed class ReliableReceiver {
    private const int Window = NetConstants.ReliableWindow;

    private readonly BufferPool _pool;
    private readonly byte[][] _stored = new byte[Window][];
    private readonly int[] _length = new int[Window];
    private readonly bool[] _has = new bool[Window];

    private ushort _nextExpected;

    public ReliableReceiver(BufferPool pool) {
        _pool = pool;
    }

    /// <summary>Quién procesa los mensajes entregados.</summary>
    public interface ISink {
        void Deliver(byte[] buffer, int offset, int length);
    }

    /// <summary>Llegó el mensaje <paramref name="id"/>: se entrega (con los siguientes guardados) o se guarda.</summary>
    public void Receive(ushort id, byte[] buffer, int offset, int length, ISink sink) {
        var ahead = (ushort) (id - _nextExpected);
        if (ahead >= 32768) {
            // Ya entregado (reenvío de algo cuyo acuse se perdió)
            return;
        }

        if (ahead >= Window) {
            // Demasiado adelantado: el emisor nunca supera la ventana, así que no es legítimo
            return;
        }

        if (ahead == 0) {
            // Se da por entregado antes de entregarlo: si quien lo procesa falla, el siguiente no debe esperar a este
            // (su paquete ya se confirmó y el emisor no lo vuelve a enviar)
            _nextExpected++;
            sink.Deliver(buffer, offset, length);
            DeliverStored(sink);
            return;
        }

        var slot = id % Window;
        if (_has[slot] || length > _pool.Size) {
            return;
        }

        var copy = _pool.Rent();
        System.Buffer.BlockCopy(buffer, offset, copy, 0, length);
        _stored[slot] = copy;
        _length[slot] = length;
        _has[slot] = true;
    }

    private void DeliverStored(ISink sink) {
        while (true) {
            var slot = _nextExpected % Window;
            if (!_has[slot]) {
                return;
            }

            var data = _stored[slot];
            _has[slot] = false;
            _stored[slot] = null;
            _nextExpected++;
            try {
                sink.Deliver(data, 0, _length[slot]);
            } finally {
                _pool.Return(data);
            }
        }
    }

    public void Clear() {
        for (var i = 0; i < Window; i++) {
            if (_stored[i] != null) {
                _pool.Return(_stored[i]);
                _stored[i] = null;
            }

            _has[i] = false;
        }
    }
}
