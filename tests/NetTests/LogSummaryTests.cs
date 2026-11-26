using System.Text;
using HKMultiplayer.Util;

namespace HKMultiplayer.Tests;

/// <summary>El resumen del registro de «Reportar un problema» (issue #29: solo llegaba el final del registro).</summary>
public static partial class Program {
    private static void RunLogSummaryTests() {
        Run("Resumen del registro: versiones y cortes de conexión aunque el registro sea largo", SummaryKeepsHeadAndCuts);
        Run("Resumen del registro: sin IP, ID de Steam ni código de partida", SanitizeRemovesIdentifiers);
        Run("Registro para pegar: si no cabe, versiones y final", ClipKeepsHeadAndTail);
    }

    private static void ClipKeepsHeadAndTail() {
        var log = new StringBuilder();
        log.AppendLine("Iniciando HKMultiplayer 9.9.9");
        for (var i = 0; i < 5000; i++) {
            log.AppendLine($"línea número {i} del registro con algo de texto");
        }

        Assert(LogSummary.Clip("corto", 100) == "corto", "recortó un registro que cabía");
        var clip = LogSummary.Clip(log.ToString(), 20000);
        Assert(clip.Length <= 20000, $"ocupa {clip.Length}");
        Assert(clip.StartsWith("Iniciando HKMultiplayer 9.9.9"), "sin la versión del mod");
        Assert(clip.Contains("línea número 4999 "), "sin el final");
        Assert(!clip.Contains("línea número 2000 "), "con el medio");
        var tailStart = clip.IndexOf("…\n", System.StringComparison.Ordinal) + 2;
        Assert(clip.Substring(tailStart).StartsWith("línea número"), "el final no empieza en una línea entera");
    }

    private static void SummaryKeepsHeadAndCuts() {
        var log = new StringBuilder();
        log.AppendLine("[INFO]:[HKMultiplayer] 10:00:00.000 Iniciando HKMultiplayer 9.9.9");
        for (var i = 0; i < 11; i++) {
            log.AppendLine($"[INFO]:[API] - línea de cabecera {i}");
        }

        for (var i = 0; i < 2000; i++) {
            log.AppendLine($"[INFO]:[HKMultiplayer] 10:{i / 60:00}:{i % 60:00}.000 algo normal de la partida número {i}");
            if (i == 900) {
                log.AppendLine("[INFO]:[HKMultiplayer] 10:15:00.000 Desconectado del host (TimedOut)");
                log.AppendLine("[INFO]:[HKMultiplayer] 10:15:05.000 Reconectando con el host (intento 1 de 12)");
            }

            if (i == 1500) {
                log.AppendLine("[WARN]:[HKMultiplayer] 10:25:00.000 No llega nada de host: 3 s sin paquetes");
                log.AppendLine("[WARN]:[HKMultiplayer] 10:25:00.000 El mod tardó 80 ms en un frame: red 80");
            }
        }

        foreach (var max in new[] { 6500, 2500, 1200 }) {
            var summary = LogSummary.Summary(log.ToString(), max);
            Assert(summary.Length <= max, $"el resumen de {max} ocupa {summary.Length}");
            Assert(summary.Contains("Iniciando HKMultiplayer 9.9.9"), $"con {max}, sin la versión del mod");
            Assert(summary.Contains("Desconectado del host (TimedOut)"), $"con {max}, sin el corte y su motivo");
            Assert(summary.Contains("No llega nada de host"), $"con {max}, sin el aviso de silencio");
            Assert(!summary.Contains("tardó 80 ms"), $"con {max}, con un aviso de coste por frame");
        }

        var big = LogSummary.Summary(log.ToString(), 6500);
        Assert(big.Contains("número 1999"), "con espacio de sobra, sin el final del registro");
    }

    private static void SanitizeRemovesIdentifiers() {
        var clean = LogSummary.Sanitize("Conectando a 192.168.1.20:27300 con el código ABCD-1234, Steam 76561198000000001; " +
                                        "local 127.0.0.1");
        Assert(!clean.Contains("192.168.1.20"), "quedó la IP");
        Assert(!clean.Contains("76561198000000001"), "quedó el ID de Steam");
        Assert(!clean.Contains("ABCD-1234"), "quedó el código de partida");
        Assert(clean.Contains("127.0.0.1"), "quitó la IP local, que no identifica a nadie");
    }
}
