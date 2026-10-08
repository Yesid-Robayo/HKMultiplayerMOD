using System;
using System.Collections.Generic;
using System.Linq;
using HKMultiplayer.Game.Players;

namespace HKMultiplayer.Tests;

/// <summary>
/// Pruebas del búfer de interpolación con tiempo simulado (sin esperar de verdad): un jugador corre a velocidad
/// constante enviando su estado a 60 Hz y el otro lo dibuja a otra frecuencia de frames, con latencia, variación y
/// pérdida. Se mide si el movimiento en pantalla es continuo.
/// </summary>
public static partial class Program {
    private static void RunInterpolationTests() {
        Run("Interpolación: suave en LAN (144 fps, 2-8 ms de variación)", () => InterpolationSmooth(0.004, 0.006, 0, 144));
        Run("Interpolación: suave con 30 ms de variación y 5% de pérdida", () => InterpolationSmooth(0.04, 0.03, 0.05, 144));
        Run("Interpolación: suave a 60 fps con frames irregulares", () => InterpolationSmooth(0.01, 0.01, 0.02, 60, 0.4));
        Run("Interpolación: un salto (cambio de puerta) no se interpola", InterpolationTeleport);
        Run("Interpolación: desordenados y repetidos", InterpolationOutOfOrder);
    }

    private sealed class Arrival {
        public double At;
        public PlayerSnapshot State;
    }

    private static void InterpolationSmooth(double latency, double jitter, double loss, double fps, double frameNoise = 0.2) {
        const double speed = 8.3; // velocidad de carrera del caballero, unidades por segundo
        var random = new Random(42);
        var buffer = new SnapshotBuffer();
        var arrivals = new List<Arrival>();
        const double duration = 20;

        // El remitente: un estado por tick; cada uno llega con su latencia (desordenados si la variación es grande)
        for (uint tick = 1; tick <= duration * 60; tick++) {
            if (random.NextDouble() < loss) {
                continue;
            }

            var sentAt = tick / 60.0;
            arrivals.Add(new Arrival {
                At = sentAt + latency + random.NextDouble() * jitter,
                State = new PlayerSnapshot { Tick = tick, X = (float) (speed * sentAt), Y = 10, Visible = true, SceneHash = 1 }
            });
        }

        arrivals.Sort((a, b) => a.At.CompareTo(b.At));

        // El receptor: frames de duración irregular; en cada uno recibe lo llegado y dibuja
        var next = 0;
        var time = 0.0;
        var steps = new List<double>();
        var lags = new List<double>();
        double? lastX = null;
        var backwards = 0;
        while (time < duration) {
            var frame = 1 / fps * (1 + (random.NextDouble() * 2 - 1) * frameNoise);
            time += frame;
            while (next < arrivals.Count && arrivals[next].At <= time) {
                buffer.Add(arrivals[next].State, arrivals[next].At);
                next++;
            }

            if (!buffer.Sample(time, out var state)) {
                continue;
            }

            if (time > 2 && lastX.HasValue) {
                var dx = state.X - lastX.Value;
                if (dx < -1e-4) {
                    backwards++;
                }

                // Error del paso frente al ideal (velocidad × duración de este frame)
                steps.Add((dx - speed * frame) / (speed / fps));
                lags.Add(time - state.X / speed);
            }

            lastX = state.X;
        }

        // Error cuadrático medio del paso, relativo al paso de un frame normal
        var relative = Math.Sqrt(steps.Average(s => s * s));
        var lag = lags.Average() * 1000;
        Console.Write($"[error del paso {relative * 100:0.0}%, atraso medio {lag:0} ms, retraso {buffer.Delay:0.0} ticks] ");
        Assert(backwards == 0, $"{backwards} pasos hacia atrás");
        Assert(relative < 0.05, $"movimiento irregular: {relative * 100:0}%");
        Assert(lag < (latency + jitter) * 1000 + 70, $"demasiado atraso: {lag:0} ms");
    }

    private static void InterpolationTeleport() {
        var buffer = new SnapshotBuffer();
        for (uint tick = 1; tick <= 120; tick++) {
            var x = tick <= 60 ? 0f : 50f;
            buffer.Add(new PlayerSnapshot { Tick = tick, X = x, Y = 0 }, tick / 60.0 + 0.01);
        }

        // Al dibujar alrededor del salto nunca debe salir una posición intermedia
        for (var time = 0.9; time < 1.3; time += 0.002) {
            if (buffer.Sample(time, out var state)) {
                Assert(state.X == 0f || state.X == 50f, $"posición intermedia en el salto: {state.X}");
            }
        }
    }

    private static void InterpolationOutOfOrder() {
        var buffer = new SnapshotBuffer();
        uint[] order = { 1, 3, 2, 2, 5, 4, 4, 6 };
        foreach (var tick in order) {
            buffer.Add(new PlayerSnapshot { Tick = tick, X = tick }, 0.1 + tick / 60.0);
        }

        Assert(buffer.Count == 6, $"esperados 6 estados distintos, hay {buffer.Count}");
        double last = -1;
        for (var time = 0.1; time < 0.3; time += 0.004) {
            if (buffer.Sample(time, out var state)) {
                Assert(state.X >= last - 1e-5, "retrocedió");
                last = state.X;
            }
        }
    }
}
