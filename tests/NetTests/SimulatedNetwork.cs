using System;
using System.Collections.Generic;
using System.Net;
using HKMultiplayer.Net;
using HKMultiplayer.Net.Transport;

namespace HKMultiplayer.Tests;

/// <summary>
/// Red simulada en memoria, sin hilos: los datagramas llegan tras un retraso (con variación, lo que también los
/// desordena) y una parte se pierde. Reutiliza búferes para poder medir la memoria que crea el transporte.
/// </summary>
public sealed class SimulatedNetwork {
    private readonly object _lock = new();
    private readonly Random _random = new(1234);
    private readonly Dictionary<int, SimulatedTransport> _endpoints = new();

    /// <summary>Probabilidad de perder cada datagrama (0..1).</summary>
    public double LossRate;

    /// <summary>Retraso base y variación máxima, en segundos.</summary>
    public double Latency = 0.02;
    public double Jitter = 0.01;

    /// <summary>
    /// Ancho de banda de cada sentido, en bytes por segundo (0 = sin límite). Simula un enlace estrecho, como una VPN que
    /// pasa por un relé: los datagramas esperan en una cola de <see cref="QueueBytes"/> y, si no caben, se pierden.
    /// </summary>
    public double Bandwidth;

    /// <summary>Bytes que caben en la cola de cada sentido cuando hay límite de ancho de banda.</summary>
    public int QueueBytes = 64 * 1024;

    /// <summary>Datagramas perdidos por cola llena.</summary>
    public int QueueDrops;

    // Momento en que cada sentido (origen → destino) termina de enviar lo que tiene en cola
    private readonly Dictionary<(int From, int To), double> _busyUntil = new();

    /// <summary>Si se guardan los datagramas enviados (para la prueba de repetición).</summary>
    public bool Capture;

    public readonly List<(byte[] Data, int To)> Captured = new();

    internal readonly BufferPool Pool = new(NetConstants.MaxDatagramSize);

    public SimulatedTransport Create(int port) {
        var transport = new SimulatedTransport(this, port);
        lock (_lock) {
            _endpoints[port] = transport;
        }

        return transport;
    }

    internal void Deliver(byte[] buffer, int offset, int length, IPEndPoint from, IPEndPoint to) {
        SimulatedTransport target;
        double at;
        lock (_lock) {
            if (Capture) {
                var copy = new byte[length];
                System.Buffer.BlockCopy(buffer, offset, copy, 0, length);
                Captured.Add((copy, to.Port));
            }

            if (_random.NextDouble() < LossRate || !_endpoints.TryGetValue(to.Port, out target)) {
                return;
            }

            var now = NetPeer.Now;
            var departure = now;
            if (Bandwidth > 0) {
                var link = (from.Port, to.Port);
                var busy = _busyUntil.TryGetValue(link, out var until) ? Math.Max(until, now) : now;
                if ((busy - now) * Bandwidth + length > QueueBytes) {
                    QueueDrops++;
                    return;
                }

                departure = busy + length / Bandwidth;
                _busyUntil[link] = departure;
            }

            at = departure + Latency + _random.NextDouble() * Jitter;
        }

        var data = Pool.Rent();
        System.Buffer.BlockCopy(buffer, offset, data, 0, length);
        lock (target.Inbox) {
            target.Inbox.Add(new Pending { At = at, Data = data, Length = length, From = from });
        }
    }

    /// <summary>Inyecta un datagrama tal cual (repetición de uno capturado).</summary>
    public void Inject(byte[] data, int toPort, IPEndPoint from) {
        SimulatedTransport target;
        lock (_lock) {
            if (!_endpoints.TryGetValue(toPort, out target)) {
                return;
            }
        }

        var buffer = Pool.Rent();
        System.Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
        lock (target.Inbox) {
            target.Inbox.Add(new Pending { At = NetPeer.Now, Data = buffer, Length = data.Length, From = from });
        }
    }

    internal struct Pending {
        public double At;
        public byte[] Data;
        public int Length;
        public IPEndPoint From;
    }
}

public sealed class SimulatedTransport : ITransport {
    private readonly SimulatedNetwork _network;
    internal readonly List<SimulatedNetwork.Pending> Inbox = new();

    public IPEndPoint EndPoint { get; }
    public int LocalPort { get; }

    internal SimulatedTransport(SimulatedNetwork network, int port) {
        _network = network;
        LocalPort = port;
        EndPoint = new IPEndPoint(IPAddress.Loopback, port);
    }

    public bool TryReceive(out Datagram datagram) {
        var now = NetPeer.Now;
        lock (Inbox) {
            for (var i = 0; i < Inbox.Count; i++) {
                if (Inbox[i].At <= now) {
                    var pending = Inbox[i];
                    Inbox.RemoveAt(i);
                    datagram = new Datagram { Buffer = pending.Data, Length = pending.Length, From = pending.From };
                    return true;
                }
            }
        }

        datagram = default;
        return false;
    }

    public void Return(byte[] buffer) => _network.Pool.Return(buffer);

    public void Send(byte[] buffer, int offset, int length, IPEndPoint to) {
        _network.Deliver(buffer, offset, length, EndPoint, to);
    }

    public void Dispose() {
    }
}
