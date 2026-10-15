using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HKMultiplayer.Net;
using HKMultiplayer.Net.Transport;
using HKMultiplayer.Session;

namespace HKMultiplayer.Tests;

/// <summary>
/// Pruebas de la conexión por Steam sin Steam: una red de pares en memoria hace de SteamNetworkingMessages (cada par es
/// un número de 64 bits, como un SteamID). Comprueba el transporte por pares y el host que escucha por IP y por Steam a
/// la vez.
/// </summary>
public static partial class Program {
    private const string PeerCode = "ABCD-EFGH";

    private static void RunPeerTransportTests() {
        Run("Steam: dirección inventada por par, estable y sin el SteamID", PeerEndPointsStable);
        Run("Steam: un invitado por Steam y otro por IP en la misma partida", PeerAndIpGuestsTogether);
        Run("Steam: código incorrecto rechazado", PeerWrongCode);
        Run("Steam: fiable con 20% de pérdida", PeerReliableUnderLoss);
    }

    /// <summary>Red de pares en memoria: lo que se envía a un par llega a su cola (o se pierde, si se pide).</summary>
    private sealed class PeerHub {
        private readonly Random _random = new(99);
        private readonly Dictionary<ulong, Queue<(ulong From, byte[] Data)>> _queues = new();

        public double LossRate;

        public PeerNode Node(ulong id) {
            _queues[id] = new Queue<(ulong, byte[])>();
            return new PeerNode(this, id);
        }

        internal void Deliver(ulong from, ulong to, byte[] buffer, int offset, int length) {
            if (_random.NextDouble() < LossRate || !_queues.TryGetValue(to, out var queue)) {
                return;
            }

            var copy = new byte[length];
            Buffer.BlockCopy(buffer, offset, copy, 0, length);
            queue.Enqueue((from, copy));
        }

        internal int Take(ulong id, byte[] buffer, out ulong from) {
            from = 0;
            if (!_queues.TryGetValue(id, out var queue) || queue.Count == 0) {
                return -1;
            }

            var (sender, data) = queue.Dequeue();
            from = sender;
            if (data.Length > buffer.Length) {
                return -1;
            }

            Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
            return data.Length;
        }
    }

    private sealed class PeerNode : IPeerNetwork {
        private readonly PeerHub _hub;
        public readonly ulong Id;

        public PeerNode(PeerHub hub, ulong id) {
            _hub = hub;
            Id = id;
        }

        public void Send(ulong peer, byte[] buffer, int offset, int length) => _hub.Deliver(Id, peer, buffer, offset, length);

        public int Receive(byte[] buffer, out ulong from) => _hub.Take(Id, buffer, out from);
    }

    private const ulong HostSteamId = 76561198000000001;

    private static void PeerEndPointsStable() {
        var transport = new PeerTransport(new PeerHub().Node(HostSteamId));
        var a = transport.EndPointOf(76561198012345678);
        var b = transport.EndPointOf(76561198087654321);
        Assert(a.Equals(transport.EndPointOf(76561198012345678)), "el mismo par no da la misma dirección");
        Assert(!a.Equals(b), "dos pares con la misma dirección");
        Assert(transport.TryGetPeer(b, out var peer) && peer == 76561198087654321, "la dirección no lleva de vuelta al par");
        Assert(transport.Owns(a) && !transport.Owns(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1)),
            "Owns se equivoca");
        var text = a.ToString();
        Assert(!text.Contains("76561198012345678") && !text.Contains(76561198012345678UL.ToString("x")),
            $"la dirección lleva el SteamID: {text}");
    }

    /// <summary>Host con IP y Steam a la vez; un invitado por IP (red simulada) y otro por Steam (red de pares).</summary>
    private sealed class MixedWorld {
        public readonly SimulatedNetwork Network = new();
        public readonly PeerHub Hub = new();
        public readonly GameSession Host;
        public readonly List<GameSession> Clients = new();
        private readonly SimulatedTransport _hostUdp;

        public MixedWorld() {
            _hostUdp = Network.Create(1000);
            var hostPeers = new PeerTransport(Hub.Node(HostSteamId), Network.Pool);
            Host = GameSession.Host(new CompositeTransport(_hostUdp, hostPeers), PeerCode, "Anfitrión", "Town", Version, _ => { });
        }

        public GameSession JoinByIp(string name) {
            var client = GameSession.Join(Network.Create(2000 + Clients.Count), _hostUdp.EndPoint, PeerCode, name, Version, _ => { });
            client.SetLocalScene("Town");
            Clients.Add(client);
            return client;
        }

        public GameSession JoinBySteam(string name, ulong steamId, string code = PeerCode) {
            var transport = new PeerTransport(Hub.Node(steamId));
            var client = GameSession.Join(transport, transport.EndPointOf(HostSteamId), code, name, Version, _ => { });
            client.SetLocalScene("Town");
            Clients.Add(client);
            return client;
        }

        public bool Pump(Func<bool> until, double seconds) {
            var end = NetPeer.Now + seconds;
            while (NetPeer.Now < end) {
                Host.Update();
                foreach (var client in Clients) {
                    client.Update();
                }

                if (until()) {
                    return true;
                }

                Thread.Sleep(1);
            }

            return until();
        }
    }

    private static void PeerAndIpGuestsTogether() {
        var world = new MixedWorld();
        var byIp = world.JoinByIp("PorIP");
        var bySteam = world.JoinBySteam("PorSteam", 76561198000000002);
        Assert(world.Pump(() => byIp.IsActive && bySteam.IsActive && world.Host.Players.Count == 3, 15),
            $"no se unieron los dos (IP {byIp.IsActive}, Steam {bySteam.IsActive}, jugadores {world.Host.Players.Count})");

        // Un mensaje del de Steam llega al de IP pasando por el host, y al revés
        var seenByIp = new List<uint>();
        var seenBySteam = new List<uint>();
        byIp.On(EveryoneType, (_, reader) => seenByIp.Add(reader.ReadUInt()));
        bySteam.On(EveryoneType, (_, reader) => seenBySteam.Add(reader.ReadUInt()));
        Send(bySteam, EveryoneType, 7);
        Send(byIp, EveryoneType, 8);
        Assert(world.Pump(() => seenByIp.Contains(7) && seenBySteam.Contains(8), 10),
            $"los mensajes no cruzaron de una vía a la otra (IP vio [{string.Join(",", seenByIp)}], Steam vio [{string.Join(",", seenBySteam)}])");
        Assert(byIp.Players.Count == 3 && bySteam.Players.Count == 3, "algún invitado no ve a los tres jugadores");
    }

    private static void PeerWrongCode() {
        var world = new MixedWorld();
        var reasons = new List<DisconnectReason>();
        var client = world.JoinBySteam("Intruso", 76561198000000003, "ZZZZ-ZZZZ");
        client.Disconnected += reason => reasons.Add(reason);
        Assert(world.Pump(() => reasons.Count > 0, 15), "no se rechazó");
        Assert(reasons[0] == DisconnectReason.WrongCode, $"motivo {reasons[0]}");
        Assert(world.Host.Players.Count == 1, "entró en la partida");
    }

    private static void PeerReliableUnderLoss() {
        var world = new MixedWorld();
        var client = world.JoinBySteam("PorSteam", 76561198000000004);
        Assert(world.Pump(() => client.IsActive, 15), "no se unió");
        world.Hub.LossRate = 0.2;
        var received = new List<uint>();
        world.Host.On(EveryoneType, (_, reader) => received.Add(reader.ReadUInt()));
        for (uint i = 0; i < 300; i++) {
            Send(client, EveryoneType, i);
        }

        Assert(world.Pump(() => received.Count >= 300, 30), $"llegaron {received.Count} de 300");
        Assert(received.SequenceEqual(Enumerable.Range(0, 300).Select(i => (uint) i)), "no llegaron en orden o se repitieron");
    }
}
