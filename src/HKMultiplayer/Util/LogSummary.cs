using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HKMultiplayer.Util;

/// <summary>
/// El texto del informe de «Reportar un problema» (ver <c>BugReport</c>): el registro sin lo que identifica al jugador y
/// un resumen que cabe en la dirección del issue. Sin nada de Unity, para probarlo fuera del juego (NetTests).
/// </summary>
internal static class LogSummary {
    /// <summary>Del resumen: las primeras líneas (versiones del mod, del juego y de la API).</summary>
    private const int HeadLines = 12;

    /// <summary>Cuántos avisos, errores y cortes de conexión, como mucho (los más recientes).</summary>
    private const int EventLines = 50;

    /// <summary>Cuántas líneas del final, como mucho.</summary>
    private const int TailLines = 40;

    /// <summary>
    /// Lo que dice que la conexión se cortó o volvió (con su motivo): en un «me echan» es lo que más importa y antes no iba
    /// en el resumen.
    /// </summary>
    private static readonly string[] ConnectionMarks = {
        "Desconectado", "No se pudo conectar", "Reconectando", "Se deja de intentar", " se fue (", " se unió", "expulsado",
        "Connected as", "Connecting", "cerró la partida", "closed the game"
    };

    private static readonly Regex Ip = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled);

    private static readonly Regex SteamId = new(@"\b7656119\d{10}\b", RegexOptions.Compiled);
    private static readonly Regex RoomCode = new(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b", RegexOptions.Compiled);

    /// <summary>El registro sin lo que podría identificar al jugador o a su PC.</summary>
    public static string Sanitize(string text) {
        if (string.IsNullOrEmpty(text)) {
            return string.Empty;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && home.Length > 3) {
            text = ReplaceIgnoreCase(text, home, "~");
            text = ReplaceIgnoreCase(text, home.Replace('\\', '/'), "~");
            text = ReplaceIgnoreCase(text, home.Replace('/', '\\'), "~");
        }

        text = ReplaceWord(text, Environment.UserName, "<usuario>");
        text = ReplaceWord(text, Environment.MachineName, "<pc>");
        text = Ip.Replace(text, match => match.Value is "127.0.0.1" or "0.0.0.0" ? match.Value : "<ip>");
        text = SteamId.Replace(text, "<steamid>");
        return RoomCode.Replace(text, "<código>");
    }

    private static string ReplaceIgnoreCase(string text, string value, string replacement) =>
        Regex.Replace(text, Regex.Escape(value), replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);

    /// <summary>Un nombre (de usuario o del PC) suelto, no dentro de otra palabra; los de menos de 3 letras no se tocan.</summary>
    private static string ReplaceWord(string text, string word, string replacement) =>
        string.IsNullOrEmpty(word) || word.Length < 3
            ? text
            : Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])", replacement,
                RegexOptions.IgnoreCase);

    /// <summary>
    /// Lo que más ayuda en <paramref name="maxChars"/> caracteres, por orden de importancia: el principio (versiones), los
    /// cortes de conexión y los avisos y errores (en su orden, los más recientes si no caben), y con lo que sobre, el
    /// final. Antes, si no cabía, se cortaba por delante y se perdía justo eso: en el issue #29 («me echan a menudo») solo
    /// llegó el final.
    /// </summary>
    public static string Summary(string log, int maxChars) {
        var lines = (log ?? string.Empty).Replace("\r", string.Empty).Split('\n');
        var head = new List<string>();
        for (var i = 0; i < lines.Length && i < HeadLines; i++) {
            head.Add(lines[i]);
        }

        // Cortes de conexión y avisos o errores, en el orden del registro (sin los de coste por frame, que son normales y lo
        // llenarían)
        var events = new List<string>();
        for (var i = HeadLines; i < lines.Length; i++) {
            var line = lines[i];
            var problem = (line.Contains("[ERROR]") || line.Contains("[WARN]")) && !line.Contains("tardó");
            if (problem || Array.Exists(ConnectionMarks, mark => line.Contains(mark))) {
                events.Add(line);
            }
        }

        if (events.Count > EventLines) {
            events.RemoveRange(0, events.Count - EventLines);
        }

        var budget = maxChars - Length(head) - 4;
        while (events.Count > 0 && Length(events) > budget) {
            events.RemoveAt(0);
        }

        // Del final, lo que quepa (sin repetir lo que ya va arriba)
        budget -= Length(events) + 2;
        var tail = new List<string>();
        var shown = new HashSet<string>(events);
        for (var i = lines.Length - 1; i >= HeadLines && tail.Count < TailLines; i--) {
            if (shown.Contains(lines[i])) {
                continue;
            }

            if (lines[i].Length + 1 > budget) {
                break;
            }

            budget -= lines[i].Length + 1;
            tail.Insert(0, lines[i]);
        }

        var picked = new List<string>(head);
        if (events.Count > 0) {
            picked.Add("…");
            picked.AddRange(events);
        }

        picked.Add("…");
        picked.AddRange(tail);
        var summary = string.Join("\n", picked);
        // Solo si la cabecera sola ya no cabe (líneas larguísimas): se corta por el final
        return summary.Length > maxChars ? summary.Substring(0, maxChars) : summary;
    }

    /// <summary>
    /// El registro para pegarlo entero en el issue, recortado a <paramref name="maxChars"/> si no cabe (el campo de GitHub
    /// admite 65 536 caracteres): se quedan las primeras líneas (versiones) y el final, lo más reciente. Antes se cortaba
    /// por delante y se perdían las versiones.
    /// </summary>
    public static string Clip(string log, int maxChars) {
        log ??= string.Empty;
        if (log.Length <= maxChars) {
            return log;
        }

        var headEnd = 0;
        for (var i = 0; i < HeadLines; i++) {
            var next = log.IndexOf('\n', headEnd);
            if (next < 0) {
                break;
            }

            headEnd = next + 1;
        }

        // Una cabecera de líneas larguísimas no se come el espacio del final
        if (headEnd > maxChars / 4) {
            headEnd = 0;
        }

        const string gap = "…\n";
        var tail = maxChars - headEnd - gap.Length;
        var start = log.Length - tail;
        // El final empieza en una línea entera
        var lineStart = log.IndexOf('\n', start);
        if (lineStart >= 0 && lineStart + 1 < log.Length) {
            start = lineStart + 1;
        }

        return log.Substring(0, headEnd) + gap + log.Substring(start);
    }

    private static int Length(List<string> lines) {
        var total = 0;
        foreach (var line in lines) {
            total += line.Length + 1;
        }

        return total;
    }
}
