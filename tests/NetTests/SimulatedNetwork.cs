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
    private readonly Random _random = new(1234);
    private readonly Dictionary<int, SimulatedTransport> _endpoints = new();

    /// <summary>Probabilidad de perder cada datagrama (0..1).</summary>
    public double LossRate;

    /// <summary>Retraso base y variación máxima, en segundos.</summary>
    public double Latency = 0.02;
    public double Jitter = 0.01;

    /// <summary>Si se guardan los datagramas enviados (para la prueba de repetición).</summary>
    public bool Capture;

    public readonly List<(byte[] Data, int To)> Captured = new();

    internal readonly BufferPool Pool = new(NetConstants.MaxDatagramSize);

    public SimulatedTransport Create(int port) {
        var transport = new SimulatedTransport(this, port);
        _endpoints[port] = transport;
        return transport;
    }

    internal void Deliver(byte[] buffer, int offset, int length, IPEndPoint from, IPEndPoint to) {
        if (Capture) {
            var copy = new byte[length];
            System.Buffer.BlockCopy(buffer, offset, copy, 0, length);
            Captured.Add((copy, to.Port));
        }

        if (_random.NextDouble() < LossRate || !_endpoints.TryGetValue(to.Port, out var target)) {
            return;
        }

        var data = Pool.Rent();
        System.Buffer.BlockCopy(buffer, offset, data, 0, length);
        var at = NetPeer.Now + Latency + _random.NextDouble() * Jitter;
        target.Inbox.Add(new Pending { At = at, Data = data, Length = length, From = from });
    }

    /// <summary>Inyecta un datagrama tal cual (repetición de uno capturado).</summary>
    public void Inject(byte[] data, int toPort, IPEndPoint from) {
        if (_endpoints.TryGetValue(toPort, out var target)) {
            var buffer = Pool.Rent();
            System.Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
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
        for (var i = 0; i < Inbox.Count; i++) {
            if (Inbox[i].At <= now) {
                var pending = Inbox[i];
                Inbox.RemoveAt(i);
                datagram = new Datagram { Buffer = pending.Data, Length = pending.Length, From = pending.From };
                return true;
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
