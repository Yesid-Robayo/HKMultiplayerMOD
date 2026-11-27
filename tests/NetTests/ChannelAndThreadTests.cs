using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using HKMultiplayer.Net;
using HKMultiplayer.Net.Transport;
using HKMultiplayer.Session;

namespace HKMultiplayer.Tests;

/// <summary>
/// Pruebas de los 4 canales fiables independientes (sin bloqueo de cabeza de línea) y del hilo dedicado de red
/// (supervivencia a congelamientos del hilo principal, asignaciones, cierre ordenado bajo carga y transporte por pares).
/// </summary>
public static partial class Program {
    private static void RunChannelAndThreadTests() {
        Run("4 canales: Combate no queda retenido detrás de Bulk con 15% de pérdida", ChannelsNoHeadOfLineBlocking);
        Run("4 canales: orden estricto independiente bajo 20% de pérdida y desorden", ChannelsIndependentOrderUnderLoss);
        Run("Hilo dedicado: el hilo principal congelado 5 s no corta ni dispara silencio (UDP real)", ThreadedSurvivesMainFreeze);
        Run("Hilo dedicado: asignaciones por tick en régimen estable", ThreadedSteadyStateAllocations);
        Run("Hilo dedicado: cierre ordenado (Dispose) bajo carga intensa", ThreadedCleanDisposeUnderLoad);
        Run("Hilo dedicado + pares: ráfaga de 200 mensajes en PeerTransport", ThreadedPeerBurst);
    }

    private static void ChannelsNoHeadOfLineBlocking() {
        // 15% de pérdida y 20 ms de latencia: si Combate y Bulk compartieran canal, cada trozo perdido de la
        // transferencia grande de 3 MB retendría los eventos de combate detrás de cientos de trozos en vuelo
        var pair = new Pair(loss: 0.15);
        pair.Network.Latency = 0.02;
        pair.Network.Jitter = 0.01;
        pair.ConnectOrFail();

        var bulkData = new byte[3 * 1024 * 1024];
        new Random(29).NextBytes(bulkData);
        pair.Client.Connection.SendLarge(9, bulkData, bulkData.Length);

        var sentAt = new Dictionary<uint, double>();
        var latencies = new List<double>();
        uint next = 0;
        var seen = 0;
        var watch = Stopwatch.StartNew();

        // Enviamos eventos en Combate cada 50 ms durante 3 s mientras Bulk va a tope con 15% de pérdida
        while (watch.Elapsed.TotalSeconds < 3.0) {
            var writer = pair.Client.Connection.BeginReliable(ReliableChannel.Combat);
            writer.WriteByte(20);
            writer.WriteUInt(next);
            pair.Client.Connection.EndMessage();
            sentAt[next++] = NetPeer.Now;
            pair.Pump(() => false, 0.05);

            for (; seen < pair.ServerListener.Reliable.Count; seen++) {
                var id = pair.ServerListener.Reliable[seen];
                latencies.Add(NetPeer.Now - sentAt[id]);
            }
        }

        // Esperamos a que terminen los eventos de Combate y la transferencia Bulk
        Assert(pair.Pump(() => pair.ServerListener.Reliable.Count >= (int) next, 10),
            $"faltaron mensajes de Combate ({pair.ServerListener.Reliable.Count} de {next})");
        for (; seen < pair.ServerListener.Reliable.Count; seen++) {
            var id = pair.ServerListener.Reliable[seen];
            latencies.Add(NetPeer.Now - sentAt[id]);
        }

        Assert(pair.Pump(() => pair.ServerListener.Large.Count == 1, 45), "la transferencia Bulk no llegó entera");
        using var sha = SHA256.Create();
        Assert(sha.ComputeHash(pair.ServerListener.Large[0]).SequenceEqual(sha.ComputeHash(bulkData)), "Bulk corrupto");

        latencies.Sort();
        var median = latencies[latencies.Count / 2];
        var p90 = latencies[(int) (latencies.Count * 0.9)];
        Console.Write($"[Combate mediana {median * 1000:0} ms, p90 {p90 * 1000:0} ms, Bulk OK] ");
        Assert(median < 0.15, $"Combate quedó bloqueado por Bulk (mediana {median * 1000:0} ms)");
        Assert(p90 < 0.45, $"Combate sufrió demasiada cola detrás de Bulk (p90 {p90 * 1000:0} ms)");
    }

    private sealed class ChannelListener : INetListener {
        public readonly List<uint>[] ByChannel = { new(), new(), new(), new() };
        public readonly List<NetConnection> Connected = new();
        public readonly List<DisconnectReason> Disconnects = new();

        public void OnConnected(NetConnection connection) => Connected.Add(connection);
        public void OnDisconnected(NetConnection connection, DisconnectReason reason) => Disconnects.Add(reason);

        public void OnMessage(NetConnection connection, PacketReader reader) {
            var type = reader.ReadByte();
            var channel = type - 30;
            if (channel >= 0 && channel < 4) {
                ByChannel[channel].Add(reader.ReadUInt());
            }
        }

        public void OnLargeMessage(NetConnection connection, byte kind, byte[] data) {
        }

        public void Log(string message) {
        }
    }

    private static void ChannelsIndependentOrderUnderLoss() {
        var network = new SimulatedNetwork { LossRate = 0.2, Latency = 0.02, Jitter = 0.04 };
        var serverListener = new ChannelListener();
        var clientListener = new ChannelListener();
        var serverTransport = network.Create(3100);
        using var server = new NetServer(serverTransport, serverListener, "ABCD-EFGH", 4);
        using var client = new NetClient(network.Create(3200), clientListener);
        client.Connect(serverTransport.EndPoint, "ABCD-EFGH", "Canales", "1.0");

        bool Pump(Func<bool> until, double seconds) {
            var end = NetPeer.Now + seconds;
            while (NetPeer.Now < end) {
                server.Update();
                client.Update();
                if (until()) {
                    return true;
                }

                Thread.Sleep(1);
            }

            return until();
        }

        Assert(Pump(() => client.IsConnected && serverListener.Connected.Count == 1, 10), "no se conectó");

        const int perChannel = 250;
        for (uint i = 0; i < perChannel; i++) {
            for (byte c = 0; c < 4; c++) {
                var w = client.Connection.BeginReliable((ReliableChannel) c);
                w.WriteByte((byte) (30 + c));
                w.WriteUInt(i);
                client.Connection.EndMessage();
            }

            if (i % 25 == 0) {
                Pump(() => false, 0.02);
            }
        }

        Assert(Pump(() => serverListener.ByChannel.All(list => list.Count >= perChannel), 30),
            $"llegaron [{string.Join(",", serverListener.ByChannel.Select(l => l.Count))}] de {perChannel}");
        for (var c = 0; c < 4; c++) {
            Assert(serverListener.ByChannel[c].Count == perChannel, $"duplicados en canal {c}");
            for (var i = 0; i < perChannel; i++) {
                Assert(serverListener.ByChannel[c][i] == (uint) i, $"fuera de orden en canal {c}, índice {i}");
            }
        }
    }

    private static void ThreadedSurvivesMainFreeze() {
        // UDP real en loopback con hilo dedicado en los dos extremos
        var serverListener = new Listener("servidor-hilo");
        var clientListener = new Listener("cliente-hilo");
        var serverTransport = new UdpTransport(0);
        var clientTransport = new UdpTransport(0);
        using var server = new NetServer(serverTransport, serverListener, "ABCD-EFGH", 4);
        using var client = new NetClient(clientTransport, clientListener);
        server.StartNetworkThread();
        client.StartNetworkThread();

        client.Connect(new IPEndPoint(IPAddress.Loopback, serverTransport.LocalPort), "ABCD-EFGH", "Congelado", "1.0");

        var end = NetPeer.Now + 10;
        while (NetPeer.Now < end && (!client.IsConnected || serverListener.Connected.Count == 0)) {
            server.Update();
            client.Update();
            Thread.Sleep(5);
        }

        Assert(client.IsConnected && serverListener.Connected.Count == 1, "no se conectó con hilo dedicado");
        var serverConn = serverListener.Connected[0];

        // El cliente deja de llamar a Update() durante 5 s (simula carga larga o congelamiento total de Unity).
        // Mientras tanto el servidor envía una transferencia de 2 MB y 100 mensajes fiables.
        var payload = new byte[2 * 1024 * 1024];
        new Random(41).NextBytes(payload);
        serverConn.SendLarge(7, payload, payload.Length);

        var freezeEnd = NetPeer.Now + 5.0;
        var worstSilent = 0.0;
        uint sent = 0;
        while (NetPeer.Now < freezeEnd) {
            server.Update();
            if (sent < 100) {
                var w = serverConn.BeginReliable(ReliableChannel.Combat);
                w.WriteByte(20);
                w.WriteUInt(sent++);
                serverConn.EndMessage();
            }

            worstSilent = Math.Max(worstSilent, serverConn.SilentSeconds);
            Thread.Sleep(20);
        }

        // Sin llamar a client.Update() durante 5 s, el hilo dedicado del cliente mantuvo vivo el enlace y respondió ACKs
        Assert(worstSilent < 1.0, $"el servidor vio silencio de {worstSilent:0.00} s durante el congelamiento");
        Assert(!serverListener.Logs.Any(l => l.StartsWith("No llega nada")), "el servidor registró un falso silencio");
        Assert(serverListener.Disconnects.Count == 0 && client.IsConnected, "la conexión se cayó al congelarse el hilo principal");

        // Al despertar el hilo principal del cliente, todo lo recibido se entrega íntegro y en orden
        end = NetPeer.Now + 5.0;
        while (NetPeer.Now < end && (clientListener.Reliable.Count < 100 || clientListener.Large.Count < 1)) {
            server.Update();
            client.Update();
            Thread.Sleep(5);
        }

        Assert(clientListener.Reliable.Count == 100, $"llegaron {clientListener.Reliable.Count} de 100 mensajes");
        for (var i = 0; i < 100; i++) {
            Assert(clientListener.Reliable[i] == (uint) i, $"fuera de orden en {i}");
        }

        Assert(clientListener.Large.Count == 1, "no llegó la transferencia grande durante el congelamiento");
        using var sha = SHA256.Create();
        Assert(sha.ComputeHash(clientListener.Large[0]).SequenceEqual(sha.ComputeHash(payload)), "datos distintos");
        Console.Write($"[peor silencio en 5 s congelado: {worstSilent * 1000:0} ms] ");
    }

    private static void ThreadedSteadyStateAllocations() {
        if (OnMono) {
            Console.Write("[sin medir en Mono] ");
            return;
        }

        var network = new SimulatedNetwork { Latency = 0.005, Jitter = 0 };
        var serverListener = new Listener("srv-alloc");
        var clientListener = new Listener("cli-alloc");
        var serverTransport = network.Create(3300);
        using var server = new NetServer(serverTransport, serverListener, "ABCD-EFGH", 4);
        using var client = new NetClient(network.Create(3400), clientListener);
        server.StartNetworkThread();
        client.StartNetworkThread();
        client.Connect(serverTransport.EndPoint, "ABCD-EFGH", "Memoria", "1.0");

        var end = NetPeer.Now + 10;
        while (NetPeer.Now < end && (!client.IsConnected || serverListener.Connected.Count == 0)) {
            server.Update();
            client.Update();
            Thread.Sleep(5);
        }

        Assert(client.IsConnected && serverListener.Connected.Count == 1, "no se conectó");
        var conn = client.Connection;

        void Tick(uint i) {
            var u = conn.BeginUnreliable();
            u.WriteByte(21);
            u.WriteUInt(i);
            conn.EndMessage();
            var r = conn.BeginReliable(ReliableChannel.Combat);
            r.WriteByte(20);
            r.WriteUInt(i);
            conn.EndMessage();
            server.Update();
            client.Update();
            Thread.Sleep(4);
        }

        for (uint i = 0; i < 120; i++) {
            Tick(i);
        }

        GC.Collect();
        var before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        var receivedBefore = serverListener.Reliable.Count;
        const int ticks = 250;
        for (uint i = 0; i < ticks; i++) {
            Tick(1000 + i);
        }

        var allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        var delivered = serverListener.Reliable.Count - receivedBefore;
        var perTick = allocated / (double) ticks;
        Console.Write($"[{perTick:0} bytes/tick en modo hilo dedicado, {delivered} entregados] ");
        Assert(delivered > 150, $"pocos entregados: {delivered}");
        Assert(perTick < 1024, $"demasiada memoria por tick en modo hilo: {perTick:0} bytes");
    }

    private static void ThreadedCleanDisposeUnderLoad() {
        var serverListener = new Listener("srv-dispose");
        var clientListener = new Listener("cli-dispose");
        var serverTransport = new UdpTransport(0);
        var clientTransport = new UdpTransport(0);
        var server = new NetServer(serverTransport, serverListener, "ABCD-EFGH", 4);
        var client = new NetClient(clientTransport, clientListener);
        server.StartNetworkThread();
        client.StartNetworkThread();
        client.Connect(new IPEndPoint(IPAddress.Loopback, serverTransport.LocalPort), "ABCD-EFGH", "Carga", "1.0");

        var end = NetPeer.Now + 10;
        while (NetPeer.Now < end && (!client.IsConnected || serverListener.Connected.Count == 0)) {
            server.Update();
            client.Update();
            Thread.Sleep(5);
        }

        Assert(client.IsConnected, "no se conectó");
        var data = new byte[4 * 1024 * 1024];
        client.Connection.SendLarge(1, data, data.Length);
        for (uint i = 0; i < 300; i++) {
            var w = client.Connection.BeginReliable(ReliableChannel.Combat);
            w.WriteByte(20);
            w.WriteUInt(i);
            client.Connection.EndMessage();
        }

        var watch = Stopwatch.StartNew();
        client.Dispose();
        server.Dispose();
        watch.Stop();
        Assert(watch.ElapsedMilliseconds < 1500, $"Dispose tardó demasiado: {watch.ElapsedMilliseconds} ms");
        Console.Write($"[Dispose en {watch.ElapsedMilliseconds} ms] ");
    }

    /// <summary>Red de pares segura entre hilos para probar PeerTransport con hilo dedicado.</summary>
    private sealed class ConcurrentPeerHub {
        private readonly object _lock = new();
        private readonly Dictionary<ulong, Queue<(ulong From, byte[] Data)>> _queues = new();

        public IPeerNetwork Node(ulong id) {
            lock (_lock) {
                _queues[id] = new Queue<(ulong, byte[])>();
            }

            return new NodeImpl(this, id);
        }

        private sealed class NodeImpl : IPeerNetwork {
            private readonly ConcurrentPeerHub _hub;
            private readonly ulong _id;

            public NodeImpl(ConcurrentPeerHub hub, ulong id) {
                _hub = hub;
                _id = id;
            }

            public void Send(ulong peer, byte[] buffer, int offset, int length) {
                var copy = new byte[length];
                Buffer.BlockCopy(buffer, offset, copy, 0, length);
                lock (_hub._lock) {
                    if (_hub._queues.TryGetValue(peer, out var q)) {
                        q.Enqueue((_id, copy));
                    }
                }
            }

            public int Receive(byte[] buffer, out ulong from) {
                from = 0;
                lock (_hub._lock) {
                    if (!_hub._queues.TryGetValue(_id, out var q) || q.Count == 0) {
                        return -1;
                    }

                    var (sender, data) = q.Dequeue();
                    from = sender;
                    if (data.Length > buffer.Length) {
                        return -1;
                    }

                    Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                    return data.Length;
                }
            }
        }
    }

    private static void ThreadedPeerBurst() {
        var hub = new ConcurrentPeerHub();
        const ulong hostId = 76561198000000101;
        const ulong guestId = 76561198000000102;
        var hostTransport = new PeerTransport(hub.Node(hostId));
        var guestTransport = new PeerTransport(hub.Node(guestId));

        using var host = GameSession.Host(hostTransport, "ABCD-EFGH", "HostSteam", "Town", Version, _ => { }, threaded: true);
        using var guest = GameSession.Join(guestTransport, guestTransport.EndPointOf(hostId), "ABCD-EFGH", "GuestSteam",
            Version, _ => { }, threaded: true);
        guest.SetLocalScene("Town");

        var end = NetPeer.Now + 10;
        while (NetPeer.Now < end && (!guest.IsActive || host.Players.Count < 2)) {
            host.Update();
            guest.Update();
            Thread.Sleep(5);
        }

        Assert(guest.IsActive && host.Players.Count == 2, "no se conectaron por PeerTransport con hilo dedicado");

        var received = new List<uint>();
        host.On(EveryoneType, (_, reader) => received.Add(reader.ReadUInt()));
        for (uint i = 0; i < 200; i++) {
            Send(guest, EveryoneType, i);
        }

        end = NetPeer.Now + 10;
        while (NetPeer.Now < end && received.Count < 200) {
            host.Update();
            guest.Update();
            Thread.Sleep(5);
        }

        Assert(received.Count == 200, $"llegaron {received.Count} de 200");
        for (var i = 0; i < 200; i++) {
            Assert(received[i] == (uint) i, $"fuera de orden en {i}");
        }
    }
}
