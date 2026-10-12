using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HKMultiplayer.Net;
using HKMultiplayer.Session;

namespace HKMultiplayer.Tests;

/// <summary>Pruebas de la sesión: jugadores, salas, quién manda en cada sala y reenvío de mensajes según su ruta.</summary>
public static partial class Program {
    private const string Version = "1.0";

    /// <summary>Tipo de prueba que va al dueño de la sala de quien lo envía.</summary>
    private const MessageType ToOwnerType = (MessageType) 41;

    /// <summary>Tipo de prueba que va a todos.</summary>
    private const MessageType EveryoneType = (MessageType) 42;

    private static void RunSessionTests() {
        MessageRoutes.Register(ToOwnerType, Route.SceneOwner, reliable: true);
        MessageRoutes.Register(EveryoneType, Route.Everyone, reliable: true);

        Run("Sesión: bienvenida, jugadores y salas", SessionJoinAndScenes);
        Run("Sesión: el estado solo llega a quien está en la misma sala", SessionSameSceneRouting);
        Run("Sesión: dueño de la sala y traspaso al salir", SessionSceneOwner);
        Run("Sesión: mensajes al dueño y a todos", SessionOwnerAndEveryoneRouting);
        Run("Sesión: salida de un jugador avisada a todos", SessionPlayerLeft);
        Run("Sesión: versión distinta rechazada", SessionVersionMismatch);
        Run("Sesión: memoria del reenvío en régimen estable", SessionRelayAllocations);
        Run("Sesión: un mensaje del invitado del tamaño máximo se reenvía y la conexión sigue", SessionRelayMaxSize);
        Run("Sesión: nombre de 24 caracteres chinos", SessionLongUnicodeName);
    }

    /// <summary>Un host y varios clientes sobre la red simulada, con lo que recibe cada uno.</summary>
    private sealed class World {
        public readonly SimulatedNetwork Network = new();
        public readonly GameSession Host;
        public readonly List<GameSession> Clients = new();
        public readonly Dictionary<GameSession, Received> Inbox = new();
        private readonly SimulatedTransport _hostTransport;

        public World(string hostScene = "Town") {
            _hostTransport = Network.Create(1000);
            Host = GameSession.Host(_hostTransport, "ABCD-EFGH", "Anfitrión", hostScene, Version, _ => { });
            Watch(Host);
        }

        public GameSession Join(string name, string scene, string version = Version) {
            var client = GameSession.Join(Network.Create(2000 + Clients.Count), _hostTransport.EndPoint, "ABCD-EFGH",
                name, version, _ => { });
            client.SetLocalScene(scene);
            Clients.Add(client);
            Watch(client);
            return client;
        }

        public GameSession JoinOrFail(string name, string scene) {
            var client = Join(name, scene);
            Assert(Pump(() => client.IsActive && client.LocalPlayer.Scene == scene &&
                              Host.Players.Any(p => p.Id == client.LocalId && p.Scene == scene), 10),
                $"{name} no se unió");
            return client;
        }

        private void Watch(GameSession session) {
            var received = new Received();
            Inbox[session] = received;
            session.On(MessageType.PlayerState, (from, reader) => received.State.Add((from.Id, reader.ReadUInt())));
            session.On(ToOwnerType, (from, reader) => received.ToOwner.Add((from.Id, reader.ReadUInt())));
            session.On(EveryoneType, (from, reader) => received.Everyone.Add((from.Id, reader.ReadUInt())));
            session.PlayerLeft += player => received.Left.Add(player.Id);
            session.Disconnected += reason => received.Disconnects.Add(reason);
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

        public void Settle() => Pump(() => false, 0.3);
    }

    private sealed class Received {
        public readonly List<(ushort From, uint Value)> State = new();
        public readonly List<(ushort From, uint Value)> ToOwner = new();
        public readonly List<(ushort From, uint Value)> Everyone = new();
        public readonly List<ushort> Left = new();
        public readonly List<DisconnectReason> Disconnects = new();
    }

    private static void SessionRelayMaxSize() {
        var world = new World();
        var a = world.JoinOrFail("Ana", "Town");
        var b = world.JoinOrFail("Beto", "Town");

        // Lo más grande que deja escribir el invitado: al reenviarlo el host añade el remitente y tiene que caber igual.
        // Antes uno de 998-999 bytes no cabía, el envío fallaba a medias y nada más llegaba a ese jugador
        var writer = a.Begin(EveryoneType);
        writer.WriteUInt(5);
        var filler = new byte[writer.Remaining];
        writer.WriteBytes(filler, 0, filler.Length);
        a.End();
        Assert(world.Pump(() => world.Inbox[b].Everyone.Any(m => m.From == a.LocalId && m.Value == 5), 5),
            "el mensaje grande no llegó a Beto");

        // Y lo que va después también llega
        Send(a, EveryoneType, 6);
        Send(world.Host, EveryoneType, 7);
        Assert(world.Pump(() => world.Inbox[b].Everyone.Any(m => m.Value == 6) && world.Inbox[b].Everyone.Any(m => m.Value == 7), 5),
            "después del mensaje grande la conexión dejó de entregar");
    }

    private static void SessionLongUnicodeName() {
        var world = new World();
        // 24 caracteres de 3 bytes en UTF-8 (72 bytes): antes el saludo se leía con 64 y fallaba sin explicación
        var name = new string('漢', 24);
        var client = world.JoinOrFail(name, "Town");
        Assert(world.Host.Players.Any(p => p.Id == client.LocalId && p.Name == name), "el host no ve el nombre entero");
    }

    private static void Send(GameSession session, MessageType type, uint value) {
        var writer = session.Begin(type);
        writer.WriteUInt(value);
        session.End();
    }

    // =====================================================================================================

    private static void SessionJoinAndScenes() {
        var world = new World();
        var a = world.JoinOrFail("Ana", "Town");
        var b = world.JoinOrFail("Beto", "Crossroads_01");
        world.Settle();

        Assert(a.LocalId == 2 && b.LocalId == 3, $"ids inesperados: {a.LocalId}, {b.LocalId}");
        foreach (var session in new[] { world.Host, a, b }) {
            Assert(session.Players.Count() == 3, "no todos ven a los tres jugadores");
            var host = session.Players.Single(p => p.Id == GameSession.HostPlayerId);
            Assert(host.Name == "Anfitrión" && host.Scene == "Town", "el host no se ve bien");
            Assert(session.Players.Single(p => p.Id == b.LocalId).Scene == "Crossroads_01", "la sala de Beto no se ve");
        }

        b.SetLocalScene("Town");
        Assert(world.Pump(() => a.Players.Single(p => p.Id == b.LocalId).Scene == "Town", 3),
            "el cambio de sala no llegó");

        // Salir al menú (sala vacía) también se ve, y deja de contar para el dueño de la sala
        b.SetLocalScene(string.Empty);
        Assert(world.Pump(() => a.Players.Single(p => p.Id == b.LocalId).Scene.Length == 0, 3),
            "la salida al menú no llegó");
    }

    private static void SessionSameSceneRouting() {
        var world = new World();
        var a = world.JoinOrFail("Ana", "Town");
        var b = world.JoinOrFail("Beto", "Crossroads_01");

        // Estado de Ana: solo el host (misma sala) lo recibe
        for (uint i = 0; i < 10; i++) {
            Send(a, MessageType.PlayerState, i);
            world.Pump(() => false, 1.0 / 60);
        }

        world.Settle();
        Assert(world.Inbox[world.Host].State.Count > 5 && world.Inbox[world.Host].State.All(s => s.From == a.LocalId),
            "el host no recibió el estado de Ana");
        Assert(world.Inbox[b].State.Count == 0, "a Beto le llegó el estado de otra sala");

        // Estado del host: le llega a Ana, no a Beto
        Send(world.Host, MessageType.PlayerState, 77);
        world.Settle();
        Assert(world.Inbox[a].State.Any(s => s.From == GameSession.HostPlayerId && s.Value == 77),
            "a Ana no le llegó el estado del host");
        Assert(world.Inbox[b].State.Count == 0, "a Beto le llegó el estado del host");

        // Beto entra en la sala: ahora sí
        b.SetLocalScene("Town");
        world.Settle();
        Send(a, MessageType.PlayerState, 99);
        Assert(world.Pump(() => world.Inbox[b].State.Any(s => s.From == a.LocalId && s.Value == 99), 3),
            "a Beto no le llegó el estado tras entrar en la sala");
    }

    private static void SessionSceneOwner() {
        var world = new World();
        var a = world.JoinOrFail("Ana", "Town");
        world.Settle();

        Assert(world.Host.IsLocalSceneOwner, "el host, primero en la sala, no manda en ella");
        Assert(!a.IsLocalSceneOwner && a.OwnerOf("Town") == GameSession.HostPlayerId, "Ana no sabe que manda el host");

        world.Host.SetLocalScene("Crossroads_01");
        Assert(world.Pump(() => a.IsLocalSceneOwner, 3), "al salir el host, Ana no pasó a mandar");
        Assert(world.Host.OwnerOf("Town") == a.LocalId && world.Host.IsLocalSceneOwner,
            "el host no ve el traspaso o no manda en su nueva sala");

        a.SetLocalScene("Crossroads_01");
        Assert(world.Pump(() => a.OwnerOf("Town") == 0 && a.OwnerOf("Crossroads_01") == GameSession.HostPlayerId, 3),
            "la sala vacía sigue con dueño o la nueva tiene otro dueño");
    }

    private static void SessionOwnerAndEveryoneRouting() {
        var world = new World();
        var a = world.JoinOrFail("Ana", "Town");
        var b = world.JoinOrFail("Beto", "Town");
        var c = world.JoinOrFail("Caro", "Crossroads_01");

        // Al dueño de Town (el host): solo él lo recibe
        Send(b, ToOwnerType, 5);
        Assert(world.Pump(() => world.Inbox[world.Host].ToOwner.Count == 1, 3), "al dueño no le llegó");
        world.Settle();
        Assert(world.Inbox[a].ToOwner.Count == 0 && world.Inbox[c].ToOwner.Count == 0, "le llegó a quien no manda");

        // Caro manda en su sala: su mensaje al dueño no se le devuelve a ella misma
        Send(c, ToOwnerType, 6);
        world.Settle();
        Assert(world.Inbox[c].ToOwner.Count == 0, "el remitente recibió su propio mensaje");

        // A todos, desde un cliente
        Send(c, EveryoneType, 7);
        Assert(world.Pump(() => new[] { world.Host, a, b }.All(s => world.Inbox[s].Everyone.Any(e => e.From == c.LocalId)), 3),
            "no le llegó a todos");
        Assert(world.Inbox[c].Everyone.Count == 0, "el remitente recibió su propio mensaje");
    }

    private static void SessionPlayerLeft() {
        var world = new World();
        var a = world.JoinOrFail("Ana", "Town");
        var b = world.JoinOrFail("Beto", "Town");
        world.Settle();

        b.Dispose();
        world.Clients.Remove(b);
        Assert(world.Pump(() => world.Inbox[world.Host].Left.Contains(3) && world.Inbox[a].Left.Contains(3), 5),
            "la salida no se avisó");
        Assert(world.Host.Players.Count() == 2 && a.Players.Count() == 2, "Beto sigue en la lista");

        world.Host.Dispose();
        Assert(world.Pump(() => world.Inbox[a].Disconnects.Count == 1, 5), "Ana no se enteró de que el host cerró");
        Assert(!a.IsActive && world.Inbox[a].Left.Contains(GameSession.HostPlayerId), "Ana sigue activa o ve al host");
    }

    private static void SessionVersionMismatch() {
        var world = new World();
        var a = world.Join("Ana", "Town", version: "0.9");
        Assert(world.Pump(() => world.Inbox[a].Disconnects.Count == 1, 10), "no se rechazó");
        Assert(world.Inbox[a].Disconnects[0] == DisconnectReason.VersionMismatch,
            $"motivo inesperado: {world.Inbox[a].Disconnects[0]}");
        Assert(world.Host.Players.Count() == 1, "el host lo añadió igualmente");
    }

    private static void SessionRelayAllocations() {
        var world = new World();
        var a = world.JoinOrFail("Ana", "Town");
        var b = world.JoinOrFail("Beto", "Town");
        var counts = new int[3];
        world.Host.On(MessageType.PlayerState, (_, r) => {
            r.ReadUInt();
            counts[0]++;
        });
        a.On(MessageType.PlayerState, (_, r) => {
            r.ReadUInt();
            counts[1]++;
        });
        b.On(MessageType.PlayerState, (_, r) => {
            r.ReadUInt();
            counts[2]++;
        });

        void Tick(uint i) {
            Send(world.Host, MessageType.PlayerState, i);
            Send(a, MessageType.PlayerState, i);
            Send(b, MessageType.PlayerState, i);
            world.Pump(() => false, 1.0 / 60);
        }

        for (uint i = 0; i < 300; i++) {
            Tick(i);
        }

        GC.Collect();
        var before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        var receivedBefore = counts.Sum();
        const int ticks = 600;
        for (uint i = 0; i < ticks; i++) {
            Tick(1000 + i);
        }

        var allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        var delivered = counts.Sum() - receivedBefore;
        var perTick = allocated / (double) ticks;
        Console.Write($"[{perTick:0} bytes por tick con 3 jugadores en la misma sala, {delivered} estados entregados] ");
        Assert(delivered > ticks * 6 * 0.9, $"se perdieron estados: {delivered}");
        Assert(perTick < 2048, $"demasiada memoria por tick: {perTick:0} bytes");
    }
}
