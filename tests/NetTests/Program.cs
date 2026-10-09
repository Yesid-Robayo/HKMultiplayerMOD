using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using HKMultiplayer.Net;
using HKMultiplayer.Net.Crypto;

namespace HKMultiplayer.Tests;

/// <summary>
/// Pruebas del transporte fuera del juego. Cada prueba levanta un servidor y un cliente sobre la red simulada.
/// Uso: dotnet run --project tests/NetTests [-- filtro]
/// </summary>
public static partial class Program {
    private static int _failures;

    /// <summary>Si se indica, solo se ejecutan las pruebas cuyo nombre lo contiene.</summary>
    private static string _filter;

    public static int Main(string[] args) {
        _filter = args.Length > 0 ? args[0] : null;
        AppDomain.MonitoringIsEnabled = true;

        Run("ChaCha20-Poly1305: vector del RFC 8439", AeadRfcVector);
        Run("ChaCha20-Poly1305: igual que BouncyCastle en 500 casos al azar", AeadMatchesBouncyCastle);
        Run("ChaCha20-Poly1305: rechaza datos o etiqueta alterados", AeadRejectsTampering);
        Run("Conexión con el código correcto", ConnectsWithRightCode);
        Run("Código incorrecto: rechazado", RejectsWrongCode);
        Run("Fiable: todo llega, una vez y en orden (20% de pérdida y desorden)", ReliableInOrderUnderLoss);
        Run("No fiable: llega lo que no se pierde, sin duplicados", UnreliableArrives);
        Run("Datos grandes: 13 MB íntegros con 5% de pérdida", LargeTransfer);
        Run("Datos grandes de baja prioridad: a su ritmo y sin retrasar los mensajes", LowPriorityLargeTransfer);
        Run("Paquetes repetidos (reenvío malicioso): se ignoran", ReplayIgnored);
        Run("Desconexión avisada", DisconnectNotified);
        Run("Memoria creada por paquete en régimen estable", SteadyStateAllocations);
        Run("Memoria del cifrado por paquete", CipherAllocations);
        RunSessionTests();
        RunInterpolationTests();

        Console.WriteLine(_failures == 0 ? "\nTODAS LAS PRUEBAS PASARON" : $"\n{_failures} PRUEBA(S) FALLARON");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test) {
        if (_filter != null && name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) {
            return;
        }

        Console.Write($"- {name}... ");
        var watch = Stopwatch.StartNew();
        try {
            test();
            Console.WriteLine($"OK ({watch.ElapsedMilliseconds} ms)");
        } catch (Exception e) {
            _failures++;
            Console.WriteLine($"FALLO: {e.Message}");
        }
    }

    private static void Assert(bool condition, string message) {
        if (!condition) {
            throw new Exception(message);
        }
    }

    /// <summary>Servidor y cliente sobre una red simulada, con un registro de lo recibido.</summary>
    private sealed class Pair {
        public readonly SimulatedNetwork Network = new();
        public readonly Listener ServerListener = new("servidor");
        public readonly Listener ClientListener = new("cliente");
        public readonly NetServer Server;
        public readonly NetClient Client;
        public readonly SimulatedTransport ServerTransport;

        public Pair(string serverCode = "ABCD-EFGH", double loss = 0) {
            Network.LossRate = loss;
            ServerTransport = Network.Create(1000);
            Server = new NetServer(ServerTransport, ServerListener, serverCode, 4);
            Client = new NetClient(Network.Create(2000), ClientListener);
        }

        public void Connect(string code = "ABCD-EFGH") {
            Client.Connect(ServerTransport.EndPoint, code, "Probador", "1.0");
        }

        /// <summary>Actualiza los dos lados hasta que se cumpla la condición o pase el tiempo.</summary>
        public bool Pump(Func<bool> until, double seconds) {
            var end = NetPeer.Now + seconds;
            while (NetPeer.Now < end) {
                Server.Update();
                Client.Update();
                if (until()) {
                    return true;
                }

                Thread.Sleep(1);
            }

            return until();
        }

        public void ConnectOrFail(string code = "ABCD-EFGH") {
            Connect(code);
            Assert(Pump(() => Client.IsConnected && ServerListener.Connected.Count == 1, 10), "no se conectó");
        }
    }

    private sealed class Listener : INetListener {
        private readonly string _name;
        public readonly List<NetConnection> Connected = new();
        public readonly List<DisconnectReason> Disconnects = new();
        public readonly List<uint> Reliable = new();
        public readonly List<uint> Unreliable = new();
        public readonly List<byte[]> Large = new();

        public Listener(string name) {
            _name = name;
        }

        public void OnConnected(NetConnection connection) => Connected.Add(connection);

        public void OnDisconnected(NetConnection connection, DisconnectReason reason) => Disconnects.Add(reason);

        public void OnMessage(NetConnection connection, PacketReader reader) {
            var type = reader.ReadByte();
            var value = reader.ReadUInt();
            if (type == 20) {
                Reliable.Add(value);
            } else if (type == 21) {
                Unreliable.Add(value);
            }
        }

        public void OnLargeMessage(NetConnection connection, byte kind, byte[] data) => Large.Add(data);

        public void Log(string message) {
        }
    }

    // =====================================================================================================

    private static void ConnectsWithRightCode() {
        var pair = new Pair();
        pair.ConnectOrFail("abcd efgh");
        Assert(pair.Client.Connection.Id == pair.ServerListener.Connected[0].Id, "ids distintos");
        Assert(pair.ServerListener.Connected[0].Name == "Probador", "nombre no llegó");
    }

    private static void RejectsWrongCode() {
        var pair = new Pair();
        pair.Connect("WXYZ-2345");
        pair.Pump(() => pair.ClientListener.Disconnects.Count > 0, 5);
        Assert(pair.ClientListener.Disconnects.Contains(DisconnectReason.WrongCode),
            $"se esperaba WrongCode, llegó: {string.Join(",", pair.ClientListener.Disconnects)}");
        Assert(pair.ServerListener.Connected.Count == 0, "el servidor aceptó un código incorrecto");
    }

    private static void ReliableInOrderUnderLoss() {
        var pair = new Pair(loss: 0.2);
        pair.Network.Jitter = 0.05;
        pair.ConnectOrFail();
        const int count = 2000;
        for (uint i = 0; i < count; i++) {
            var writer = pair.Client.Connection.BeginReliable();
            writer.WriteByte(20);
            writer.WriteUInt(i);
            pair.Client.Connection.EndMessage();
            if (i % 50 == 0) {
                pair.Pump(() => false, 0.02);
            }
        }

        Assert(pair.Pump(() => pair.ServerListener.Reliable.Count >= count, 30),
            $"llegaron {pair.ServerListener.Reliable.Count} de {count}");
        for (var i = 0; i < count; i++) {
            Assert(pair.ServerListener.Reliable[i] == i, $"fuera de orden en {i}");
        }

        pair.Pump(() => false, 0.5);
        Assert(pair.ServerListener.Reliable.Count == count, "llegaron duplicados");
    }

    private static void UnreliableArrives() {
        var pair = new Pair(loss: 0.1);
        pair.ConnectOrFail();
        for (uint i = 0; i < 300; i++) {
            var writer = pair.Client.Connection.BeginUnreliable();
            writer.WriteByte(21);
            writer.WriteUInt(i);
            pair.Client.Connection.EndMessage();
            pair.Pump(() => false, 1.0 / 60);
        }

        pair.Pump(() => false, 0.3);
        var received = pair.ServerListener.Unreliable;
        Assert(received.Count > 200, $"solo llegaron {received.Count} de 300");
        Assert(received.Distinct().Count() == received.Count, "llegaron duplicados");
    }

    private static void LargeTransfer() {
        var pair = new Pair(loss: 0.05);
        pair.ConnectOrFail();
        var data = new byte[13 * 1024 * 1024];
        new Random(7).NextBytes(data);
        var watch = Stopwatch.StartNew();
        pair.Client.Connection.SendLarge(9, data, data.Length);
        Assert(pair.Pump(() => pair.ServerListener.Large.Count == 1, 60), "no llegó entero");
        using var sha = SHA256.Create();
        Assert(sha.ComputeHash(pair.ServerListener.Large[0]).SequenceEqual(sha.ComputeHash(data)), "datos distintos");
        Console.Write($"[{watch.Elapsed.TotalSeconds:0.0} s, {data.Length / 1048576.0 / watch.Elapsed.TotalSeconds:0.0} MB/s] ");
    }

    private static void LowPriorityLargeTransfer() {
        var pair = new Pair();
        pair.Network.Latency = 0.03;
        pair.ConnectOrFail();
        var data = new byte[2 * 1024 * 1024];
        new Random(11).NextBytes(data);
        var watch = Stopwatch.StartNew();
        pair.Client.Connection.SendLarge(9, data, data.Length, true);
        // Mientras va la skin, un evento cada 50 ms: ninguno debe tardar mucho más que la latencia de la red
        var sentAt = new Dictionary<uint, double>();
        var worst = 0.0;
        uint next = 0;
        var seen = 0;
        while (pair.ServerListener.Large.Count == 0 && watch.Elapsed.TotalSeconds < 30) {
            var writer = pair.Client.Connection.BeginReliable();
            writer.WriteByte(20);
            writer.WriteUInt(next);
            pair.Client.Connection.EndMessage();
            sentAt[next++] = NetPeer.Now;
            pair.Pump(() => false, 0.05);
            for (; seen < pair.ServerListener.Reliable.Count; seen++) {
                worst = Math.Max(worst, NetPeer.Now - sentAt[pair.ServerListener.Reliable[seen]]);
            }
        }

        Assert(pair.ServerListener.Large.Count == 1, "no llegó entera");
        using var sha = SHA256.Create();
        Assert(sha.ComputeHash(pair.ServerListener.Large[0]).SequenceEqual(sha.ComputeHash(data)), "datos distintos");
        var rate = data.Length / watch.Elapsed.TotalSeconds;
        Assert(rate < NetConnection.LowPriorityBytesPerSecond * 1.3, $"demasiado rápida: {rate / 1048576:0.00} MB/s");
        Assert(worst < 0.25, $"un mensaje tardó {worst * 1000:0} ms");
        Console.Write($"[{watch.Elapsed.TotalSeconds:0.0} s, {rate / 1048576.0:0.00} MB/s, mensaje más lento {worst * 1000:0} ms] ");
    }

    private static void ReplayIgnored() {
        var pair = new Pair();
        pair.ConnectOrFail();
        pair.Network.Capture = true;
        var writer = pair.Client.Connection.BeginReliable();
        writer.WriteByte(20);
        writer.WriteUInt(42);
        pair.Client.Connection.EndMessage();
        pair.Pump(() => pair.ServerListener.Reliable.Count == 1, 2);
        pair.Network.Capture = false;

        // Se repiten todos los datagramas que el cliente envió al servidor
        foreach (var (data, to) in pair.Network.Captured.Where(c => c.To == 1000).ToList()) {
            pair.Network.Inject(data, to, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2000));
        }

        pair.Pump(() => false, 0.3);
        Assert(pair.ServerListener.Reliable.Count == 1, $"el mensaje se entregó {pair.ServerListener.Reliable.Count} veces");
    }

    private static void DisconnectNotified() {
        var pair = new Pair();
        pair.ConnectOrFail();
        pair.Client.Connection.Disconnect();
        Assert(pair.Pump(() => pair.ServerListener.Disconnects.Count == 1, 3), "el servidor no se enteró");
        Assert(pair.ServerListener.Disconnects[0] == DisconnectReason.Closed, "motivo incorrecto");
    }

    private static byte[] Hex(string hex) {
        hex = hex.Replace(" ", "");
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++) {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    /// <summary>Vector de prueba de la sección 2.8.2 del RFC 8439.</summary>
    private static void AeadRfcVector() {
        var key = Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        var nonce = Hex("070000004041424344454647");
        var aad = Hex("50515253c0c1c2c3c4c5c6c7");
        var plain = System.Text.Encoding.ASCII.GetBytes(
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var expectedStart = Hex("d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6");
        var expectedTag = Hex("1ae10b594f09e26a7e902ecbd0600691");

        var aead = new ChaCha20Poly1305Aead(key);
        var output = new byte[plain.Length + 16];
        var n = aead.Seal(nonce, aad, 0, aad.Length, plain, 0, plain.Length, output, 0);
        Assert(n == plain.Length + 16, "longitud");
        Assert(output.Take(expectedStart.Length).SequenceEqual(expectedStart), "texto cifrado distinto del RFC");
        Assert(output.Skip(plain.Length).SequenceEqual(expectedTag), "etiqueta distinta del RFC");

        var back = new byte[plain.Length];
        Assert(aead.Open(nonce, aad, 0, aad.Length, output, 0, n, back, 0) == plain.Length, "no descifra");
        Assert(back.SequenceEqual(plain), "texto descifrado distinto");
    }

    private static void AeadMatchesBouncyCastle() {
        var random = new Random(99);
        for (var i = 0; i < 500; i++) {
            var key = new byte[32];
            var nonce = new byte[12];
            var aad = new byte[random.Next(0, 40)];
            var plain = new byte[random.Next(0, 1300)];
            random.NextBytes(key);
            random.NextBytes(nonce);
            random.NextBytes(aad);
            random.NextBytes(plain);

            var ours = new byte[plain.Length + 16];
            new ChaCha20Poly1305Aead(key).Seal(nonce, aad, 0, aad.Length, plain, 0, plain.Length, ours, 0);

            var bc = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            bc.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
                new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), 128, nonce, aad));
            var theirs = new byte[plain.Length + 16];
            var w = bc.ProcessBytes(plain, 0, plain.Length, theirs, 0);
            bc.DoFinal(theirs, w);

            Assert(ours.SequenceEqual(theirs), $"distinto de BouncyCastle (caso {i}, {plain.Length} bytes, aad {aad.Length})");
        }
    }

    private static void AeadRejectsTampering() {
        var key = new byte[32];
        var nonce = new byte[12];
        var aad = new byte[9];
        var plain = new byte[100];
        new Random(5).NextBytes(plain);
        var aead = new ChaCha20Poly1305Aead(key);
        var sealedData = new byte[116];
        aead.Seal(nonce, aad, 0, 9, plain, 0, 100, sealedData, 0);
        var output = new byte[100];

        var tampered = (byte[]) sealedData.Clone();
        tampered[10] ^= 1;
        Assert(aead.Open(nonce, aad, 0, 9, tampered, 0, 116, output, 0) == -1, "aceptó datos alterados");
        tampered = (byte[]) sealedData.Clone();
        tampered[115] ^= 1;
        Assert(aead.Open(nonce, aad, 0, 9, tampered, 0, 116, output, 0) == -1, "aceptó etiqueta alterada");
        var otherAad = (byte[]) aad.Clone();
        otherAad[0] = 7;
        Assert(aead.Open(nonce, otherAad, 0, 9, sealedData, 0, 116, output, 0) == -1, "aceptó cabecera alterada");
        Assert(aead.Open(nonce, aad, 0, 9, sealedData, 0, 116, output, 0) == 100, "rechazó el original");
    }

    private static void CipherAllocations() {
        var key = new byte[32];
        var cipher = new PacketCipher(key);
        var header = new byte[9];
        var plain = new byte[200];
        var output = new byte[300];
        var back = new byte[300];
        var decipher = new PacketCipher(key);
        for (ulong i = 1; i < 100; i++) {
            var n = cipher.Encrypt(i, header, 0, 9, plain, 0, plain.Length, output, 0);
            decipher.Decrypt(i, header, 0, 9, output, 0, n, back, 0);
        }

        GC.Collect();
        var before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        for (ulong i = 100; i < 1100; i++) {
            var n = cipher.Encrypt(i, header, 0, 9, plain, 0, plain.Length, output, 0);
            decipher.Decrypt(i, header, 0, 9, output, 0, n, back, 0);
        }

        var perPacket = (AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before) / 1000.0;
        Console.Write($"[{perPacket:0} bytes por paquete cifrado y descifrado] ");
        Assert(perPacket < 64, $"el cifrado crea {perPacket:0} bytes por paquete");
    }

    private static void SteadyStateAllocations() {
        var pair = new Pair();
        pair.ConnectOrFail();
        var connection = pair.Client.Connection;

        void Tick(uint i) {
            var writer = connection.BeginUnreliable();
            writer.WriteByte(21);
            writer.WriteUInt(i);
            connection.EndMessage();
            var reliable = connection.BeginReliable();
            reliable.WriteByte(20);
            reliable.WriteUInt(i);
            connection.EndMessage();
            pair.Pump(() => false, 1.0 / 60);
        }

        // Calentamiento: que se llenen las reservas de búferes
        for (uint i = 0; i < 300; i++) {
            Tick(i);
        }

        GC.Collect();
        var before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        var receivedBefore = pair.ServerListener.Reliable.Count;
        const int ticks = 600;
        for (uint i = 0; i < ticks; i++) {
            Tick(1000 + i);
        }

        var allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
        var delivered = pair.ServerListener.Reliable.Count - receivedBefore;
        // Lo que crea la propia prueba (listas de lo recibido) también cuenta: se descuenta aproximadamente
        var perTick = allocated / (double) ticks;
        Console.Write($"[{perTick:0} bytes por tick con un mensaje fiable y uno no fiable en cada sentido, {delivered} entregados] ");
        Assert(perTick < 1024, $"demasiada memoria por tick: {perTick:0} bytes");
    }
}
