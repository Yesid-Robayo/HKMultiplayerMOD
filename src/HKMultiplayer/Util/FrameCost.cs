using System;
using System.Diagnostics;
using System.Text;

namespace HKMultiplayer.Util;

/// <summary>
/// Cuánto tarda el mod en cada frame, por partes. Sirve para saber si un tirón es del mod o del PC: si una parte del mod
/// pasa de <see cref="SlowFrameMs"/> en un frame, queda en el registro con el desglose (como mucho cada 10 s). Medir
/// cuesta unas decenas de nanosegundos por parte.
/// </summary>
internal static class FrameCost {
    public enum Part {
        /// <summary>Lo del Update del mod (mundo, progreso compartido, menús, reconexión).</summary>
        Update,
        Room,
        LocalState,
        Network,
        PlayersEnemies,
        Revive,
        Hud,
        Features,
        Count
    }

    private static readonly string[] Names = {
        "mundo y menús", "objetos de la sala", "estado propio", "red", "jugadores y enemigos", "revivir y sombras", "HUD",
        "funciones"
    };

    private const int Parts = (int) Part.Count;

    /// <summary>Frame del mod que ya cuenta como tirón.</summary>
    private const double SlowFrameMs = 50.0;

    private const float SlowWarnInterval = 10f;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly long[] Frame = new long[Parts];
    private static readonly long[] Total = new long[Parts];
    private static readonly long[] Worst = new long[Parts];
    private static long _mark;
    private static long _worstFrame;
    private static int _frames;
    private static int _slowFrames;
    private static float _nextSlowWarn;

    /// <summary>Empieza a contar (al principio del Update o del LateUpdate del mod).</summary>
    public static void Start() => _mark = Stopwatch.GetTimestamp();

    /// <summary>Lo que pasó desde la marca anterior es de esa parte.</summary>
    public static void Lap(Part part) {
        var now = Stopwatch.GetTimestamp();
        Frame[(int) part] += now - _mark;
        _mark = now;
    }

    /// <summary>Al final del frame del mod: suma lo de este frame y avisa si fue un tirón.</summary>
    public static void EndFrame() {
        long sum = 0;
        for (var i = 0; i < Parts; i++) {
            sum += Frame[i];
        }

        if (sum * MsPerTick > SlowFrameMs) {
            _slowFrames++;
            if (UnityEngine.Time.unscaledTime >= _nextSlowWarn) {
                _nextSlowWarn = UnityEngine.Time.unscaledTime + SlowWarnInterval;
                Log.Warn($"El mod tardó {sum * MsPerTick:0} ms en un frame: {Breakdown(Frame, 1)}");
            }
        }

        _worstFrame = Math.Max(_worstFrame, sum);
        for (var i = 0; i < Parts; i++) {
            Total[i] += Frame[i];
            Worst[i] = Math.Max(Worst[i], Frame[i]);
            Frame[i] = 0;
        }

        _frames++;
    }

    private static string Breakdown(long[] ticks, int frames) {
        var text = new StringBuilder();
        for (var i = 0; i < Parts; i++) {
            var ms = ticks[i] * MsPerTick / Math.Max(1, frames);
            if (ms >= 0.05) {
                text.Append(text.Length > 0 ? ", " : string.Empty).Append($"{Names[i]} {ms:0.0}");
            }
        }

        return text.Length > 0 ? text.ToString() : "nada que destaque";
    }

    /// <summary>Media y peor frame de cada parte desde la última vez, y vuelve a empezar (para el diagnóstico).</summary>
    public static string DescribeAndReset() {
        long total = 0;
        foreach (var ticks in Total) {
            total += ticks;
        }

        var text = new StringBuilder();
        text.Append($"frames={_frames} medio={total * MsPerTick / Math.Max(1, _frames):0.00} ms peor={_worstFrame * MsPerTick:0.0} ms " +
                    $"tirones del mod(>{SlowFrameMs:0} ms)={_slowFrames} |");
        for (var i = 0; i < Parts; i++) {
            text.Append($" {Names[i]}={Total[i] * MsPerTick / Math.Max(1, _frames):0.00}/{Worst[i] * MsPerTick:0.0}");
        }

        Array.Clear(Total, 0, Parts);
        Array.Clear(Worst, 0, Parts);
        _worstFrame = 0;
        _frames = 0;
        _slowFrames = 0;
        return text.ToString();
    }
}
