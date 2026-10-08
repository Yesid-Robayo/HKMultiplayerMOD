using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HKMultiplayer.Net.Transport;

/// <summary>
/// Solo para el modo de prueba: envuelve otro transporte y estropea lo que llega como una mala conexión (retraso,
/// variación que desordena y pérdidas). Se aplica a lo recibido, así que con las dos partes activas el ida y vuelta
/// suma los dos retrasos. No crea memoria por datagrama: los retenidos van en una lista con capacidad reservada.
/// </summary>
public sealed class LaggyTransport : ITransport {
    /// <summary>Lo que se usará al abrir o unirse a la próxima partida (lo pone el modo de prueba).</summary>
    public static double NextLatency;

    public static double NextJitter;
    public static double NextLoss;

    public static bool Configured => NextLatency > 0 || NextJitter > 0 || NextLoss > 0;

    private readonly ITransport _inner;
    private readonly double _latency;
    private readonly double _jitter;
    private readonly double _loss;
    private readonly Random _random = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(Datagram Datagram, double Release)> _held = new(512);

    public LaggyTransport(ITransport inner, double latencySeconds, double jitterSeconds, double lossRate) {
        _inner = inner;
        _latency = latencySeconds;
        _jitter = jitterSeconds;
        _loss = lossRate;
    }

    public int LocalPort => _inner.LocalPort;

    public bool TryReceive(out Datagram datagram) {
        var now = _clock.Elapsed.TotalSeconds;
        while (_inner.TryReceive(out var incoming)) {
            if (_random.NextDouble() < _loss) {
                _inner.Return(incoming.Buffer);
                continue;
            }

            _held.Add((incoming, now + _latency + _random.NextDouble() * _jitter));
        }

        // El primero cuyo momento ya llegó (con variación, pueden salir en otro orden que el de llegada)
        var best = -1;
        for (var i = 0; i < _held.Count; i++) {
            if (_held[i].Release <= now && (best < 0 || _held[i].Release < _held[best].Release)) {
                best = i;
            }
        }

        if (best < 0) {
            datagram = default;
            return false;
        }

        datagram = _held[best].Datagram;
        _held.RemoveAt(best);
        return true;
    }

    public void Return(byte[] buffer) => _inner.Return(buffer);

    public void Send(byte[] buffer, int offset, int length, System.Net.IPEndPoint to) => _inner.Send(buffer, offset, length, to);

    public void Dispose() {
        foreach (var (held, _) in _held) {
            _inner.Return(held.Buffer);
        }

        _held.Clear();
        _inner.Dispose();
    }
}
