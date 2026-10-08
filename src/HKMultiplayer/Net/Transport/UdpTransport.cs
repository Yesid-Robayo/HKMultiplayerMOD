using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace HKMultiplayer.Net.Transport;

/// <summary>
/// Transporte UDP. Un hilo recibe en búferes de la reserva y los deja en una cola; el hilo principal la vacía.
/// Envía directamente desde el hilo que llama (el principal), sin copiar el búfer.
/// </summary>
public sealed class UdpTransport : ITransport {
    /// <summary>Datagramas en cola como mucho: si el juego se atasca, se descartan los más nuevos.</summary>
    private const int MaxQueued = 4096;

    private readonly Socket _socket;
    private readonly BufferPool _pool = new(NetConstants.MaxDatagramSize);
    private readonly Queue<Datagram> _queue = new();
    private readonly object _queueLock = new();
    private readonly Thread _thread;
    private volatile bool _running = true;

    public int LocalPort { get; }

    /// <summary>Errores del hilo de recepción (para el registro).</summary>
    public event Action<string> Error;

    /// <param name="port">Puerto en el que escuchar (0 = cualquiera libre, para el cliente).</param>
    public UdpTransport(int port) {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows: sin esto, un ICMP "puerto inalcanzable" cierra la recepción del socket
        try {
            const int sioUdpConnReset = -1744830452;
            _socket.IOControl(sioUdpConnReset, new byte[] { 0 }, null);
        } catch (Exception) {
            // No disponible fuera de Windows: no hace falta
        }

        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        _socket.ReceiveBufferSize = 1 << 20;
        _socket.SendBufferSize = 1 << 20;
        LocalPort = ((IPEndPoint) _socket.LocalEndPoint).Port;

        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "HKMultiplayer UDP" };
        _thread.Start();
    }

    private void ReceiveLoop() {
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (_running) {
            var buffer = _pool.Rent();
            int length;
            try {
                length = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref any);
            } catch (SocketException e) when (!_running || e.SocketErrorCode == SocketError.Interrupted) {
                _pool.Return(buffer);
                return;
            } catch (ObjectDisposedException) {
                return;
            } catch (SocketException e) {
                _pool.Return(buffer);
                Error?.Invoke($"Error al recibir: {e.SocketErrorCode}");
                continue;
            }

            if (length <= 0) {
                _pool.Return(buffer);
                continue;
            }

            var from = (IPEndPoint) any;
            // ReceiveFrom rellena el EndPoint pasado por referencia: se crea uno nuevo para el siguiente datagrama
            any = new IPEndPoint(IPAddress.Any, 0);
            lock (_queueLock) {
                if (_queue.Count >= MaxQueued) {
                    _pool.Return(buffer);
                    continue;
                }

                _queue.Enqueue(new Datagram { Buffer = buffer, Length = length, From = from });
            }
        }
    }

    public bool TryReceive(out Datagram datagram) {
        lock (_queueLock) {
            if (_queue.Count > 0) {
                datagram = _queue.Dequeue();
                return true;
            }
        }

        datagram = default;
        return false;
    }

    public void Return(byte[] buffer) => _pool.Return(buffer);

    public void Send(byte[] buffer, int offset, int length, IPEndPoint to) {
        try {
            _socket.SendTo(buffer, offset, length, SocketFlags.None, to);
        } catch (SocketException e) {
            Error?.Invoke($"Error al enviar a {to}: {e.SocketErrorCode}");
        } catch (ObjectDisposedException) {
            // Cerrando
        }
    }

    public void Dispose() {
        _running = false;
        try {
            _socket.Close();
        } catch (Exception) {
            // Ya cerrado
        }

        lock (_queueLock) {
            while (_queue.Count > 0) {
                _pool.Return(_queue.Dequeue().Buffer);
            }
        }
    }
}
