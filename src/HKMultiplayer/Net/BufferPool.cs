using System.Collections.Generic;

namespace HKMultiplayer.Net;

/// <summary>
/// Reserva de búferes de tamaño fijo reutilizables entre hilos (el de recepción los toma y el principal los devuelve),
/// para no crear un array por cada paquete. Se usa una pila con bloqueo: ConcurrentBag crearía un nodo por cada
/// devolución.
/// </summary>
public sealed class BufferPool {
    private readonly Stack<byte[]> _buffers = new();
    private readonly object _lock = new();
    private readonly int _size;

    public BufferPool(int size) {
        _size = size;
    }

    /// <summary>Tamaño de los búferes de esta reserva.</summary>
    public int Size => _size;

    public byte[] Rent() {
        lock (_lock) {
            if (_buffers.Count > 0) {
                return _buffers.Pop();
            }
        }

        return new byte[_size];
    }

    public void Return(byte[] buffer) {
        if (buffer == null || buffer.Length != _size) {
            return;
        }

        lock (_lock) {
            _buffers.Push(buffer);
        }
    }
}
