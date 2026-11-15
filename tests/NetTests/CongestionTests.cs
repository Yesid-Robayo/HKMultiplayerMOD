using System;
using System.Collections.Generic;
using HKMultiplayer.Net;

namespace HKMultiplayer.Tests;

/// <summary>
/// Pruebas con un enlace estrecho (VPN por relé): una ráfaga de mensajes fiables, como la de un cambio de sala, no debe
/// cortar la conexión ni dejar al otro sin noticias durante mucho tiempo.
/// </summary>
public static partial class Program {
    private static void RunCongestionTests() {
        Run("Enlace estrecho (1 Mbit/s): una ráfaga fiable de 400 KB no corta ni congela", NarrowLinkBurst);
        Run("Silencio de la red: queda en el registro al empezar y al volver", SilenceLogged);
    }

    private static void SilenceLogged() {
        var pair = new Pair();
        pair.ConnectOrFail();
        // El cliente deja de enviar 4 s (su juego parado): el servidor lo avisa a los 3 y no corta
        var end = NetPeer.Now + 4;
        while (NetPeer.Now < end) {
            pair.Server.Update();
            System.Threading.Thread.Sleep(5);
        }

        Assert(pair.ServerListener.Logs.Exists(l => l.StartsWith("No llega nada de el jugador") && l.Contains("latencia")),
            "no se avisó del silencio");
        Assert(pair.ServerListener.Disconnects.Count == 0, "se cortó antes de tiempo");
        pair.Pump(() => pair.ServerListener.Logs.Exists(l => l.StartsWith("Vuelve a llegar la red")), 2);
        Assert(pair.ServerListener.Logs.Exists(l => l.StartsWith("Vuelve a llegar la red de el jugador")),
            "no se avisó de que volvió a llegar");
        Assert(!pair.ClientListener.Logs.Exists(l => l.StartsWith("No llega nada")),
            "el cliente avisó de un silencio que fue suyo");
    }

    /// <summary>Resultado de una ráfaga por un enlace estrecho.</summary>
    private sealed class BurstResult {
        public bool Delivered;
        public double DeliverSeconds;
        public double WorstGap;
        public double WorstEvent;
        public int Disconnects;
        public int QueueDrops;
    }

    /// <summary>
    /// El servidor envía de golpe <paramref name="messages"/> mensajes fiables de <paramref name="size"/> bytes mientras
    /// los dos lados mandan su estado (no fiable) en cada tick y el servidor un evento cada 100 ms. Mide el mayor hueco
    /// sin estado recibido en el cliente y lo más que tardó un evento.
    /// </summary>
    private static BurstResult MeasureBurst(double bandwidth, int queueBytes, double latency, int messages, int size) {
        var pair = new Pair();
        pair.Network.Latency = latency;
        pair.Network.Jitter = 0.005;
        pair.ConnectOrFail();
        pair.Network.Bandwidth = bandwidth;
        pair.Network.QueueBytes = queueBytes;
        var server = pair.ServerListener.Connected[0];
        var client = pair.Client.Connection;

        // Unos segundos de estado normal antes de la ráfaga
        uint state = 0;
        void SendState() {
            foreach (var connection in new[] { server, client }) {
                var writer = connection.BeginUnreliable();
                writer.WriteByte(21);
                writer.WriteUInt(state);
                writer.WriteBytes(new byte[120], 0, 120);
                connection.EndMessage();
            }

            state++;
        }

        var end = NetPeer.Now + 2;
        while (NetPeer.Now < end) {
            SendState();
            pair.Pump(() => false, 1.0 / 60);
        }

        // La ráfaga: id 1.000.000 en adelante para no mezclarla con los eventos
        var padding = new byte[size];
        for (var i = 0; i < messages; i++) {
            var writer = server.BeginReliable();
            writer.WriteByte(20);
            writer.WriteUInt((uint) (1_000_000 + i));
            writer.WriteBytes(padding, 0, size);
            server.EndMessage();
        }

        var result = new BurstResult();
        var start = NetPeer.Now;
        var lastState = start;
        var seenState = pair.ClientListener.Unreliable.Count;
        var sentAt = new Dictionary<uint, double>();
        uint nextEvent = 0;
        var nextEventAt = start;
        var seenReliable = pair.ClientListener.Reliable.Count;
        var burstDone = 0;
        end = start + 20;
        while (NetPeer.Now < end && client.State == ConnectionState.Connected) {
            if (NetPeer.Now >= nextEventAt) {
                var writer = server.BeginReliable();
                writer.WriteByte(20);
                writer.WriteUInt(nextEvent);
                server.EndMessage();
                sentAt[nextEvent++] = NetPeer.Now;
                nextEventAt += 0.1;
            }

            SendState();
            pair.Pump(() => false, 1.0 / 60);
            var now = NetPeer.Now;
            if (pair.ClientListener.Unreliable.Count > seenState) {
                seenState = pair.ClientListener.Unreliable.Count;
                lastState = now;
            }

            result.WorstGap = Math.Max(result.WorstGap, now - lastState);
            for (; seenReliable < pair.ClientListener.Reliable.Count; seenReliable++) {
                var id = pair.ClientListener.Reliable[seenReliable];
                if (id >= 1_000_000) {
                    if (++burstDone == messages) {
                        result.Delivered = true;
                        result.DeliverSeconds = now - start;
                    }
                } else {
                    result.WorstEvent = Math.Max(result.WorstEvent, now - sentAt[id]);
                }
            }

            if (result.Delivered && now - start > result.DeliverSeconds + 2) {
                break;
            }
        }

        result.Disconnects = pair.ClientListener.Disconnects.Count + pair.ServerListener.Disconnects.Count;
        result.QueueDrops = pair.Network.QueueDrops;
        return result;
    }

    private static void NarrowLinkBurst() {
        // 1 Mbit/s, 64 KB de cola, 40 ms: una VPN por relé modesta. 400 mensajes de 1 KB, unos 400 KB
        var result = MeasureBurst(125_000, 64 * 1024, 0.04, 400, 950);
        Console.Write($"[entregada {(result.Delivered ? $"en {result.DeliverSeconds:0.0} s" : "NO")}, hueco de estado " +
                      $"{result.WorstGap * 1000:0} ms, evento más lento {result.WorstEvent * 1000:0} ms, " +
                      $"{result.QueueDrops} perdidos por cola, {result.Disconnects} cortes] ");
        Assert(result.Disconnects == 0, "se cortó la conexión");
        Assert(result.Delivered, "la ráfaga no llegó entera");
        Assert(result.WorstGap < 2, $"el cliente pasó {result.WorstGap:0.0} s sin estado del servidor");
    }
}
