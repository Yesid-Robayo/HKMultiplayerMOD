using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// «Reportar un problema» (menú Multijugador del título y de la pausa): casi nadie adjuntaba el ModLog.txt a los issues.
/// Abre en el navegador la página para crear un issue en GitHub con la versión del mod y un resumen del registro ya
/// puestos, guarda el registro completo como <see cref="ReportFileName"/> y abre su carpeta con él seleccionado para
/// arrastrarlo a la página (GitHub solo acepta adjuntos que arrastra el propio jugador). También lo copia al portapapeles,
/// por si prefiere pegarlo.
/// <para>
/// El mod no envía nada por su cuenta: el jugador ve el texto en su navegador y decide si lo publica. Antes se quita del
/// registro lo que podría identificarlo: su carpeta personal, su nombre de usuario y el del PC, direcciones IP, ID de
/// Steam y códigos de partida.
/// </para>
/// </summary>
internal static class BugReport {
    private const string NewIssueUrl = "https://github.com/Yesid-Robayo/HKMultiplayerMOD/issues/new";
    private const string Template = "bug_report.yml";

    /// <summary>Campos de la plantilla de issues (.github/ISSUE_TEMPLATE/bug_report.yml) que se rellenan.</summary>
    private const string VersionField = "version";

    private const string LogsField = "logs";

    /// <summary>El registro limpio para adjuntar, junto al ModLog.txt (se sobrescribe en cada informe).</summary>
    private const string ReportFileName = "ModLog-report.txt";

    /// <summary>
    /// Lo que cabe del resumen en la dirección (los navegadores y GitHub cortan las muy largas; con tildes, cada carácter
    /// codificado ocupa hasta 6). El registro entero va en el portapapeles.
    /// </summary>
    private const int MaxSummaryChars = 2500;

    /// <summary>Lo que cabe del registro en el portapapeles para pegarlo en GitHub (su campo admite 65 536 caracteres).</summary>
    private const int MaxClipboardChars = 60000;

    /// <summary>Del resumen: las primeras líneas (versiones del mod, del juego y de la API), los errores y el final.</summary>
    private const int HeadLines = 12;

    private const int ProblemLines = 25;
    private const int TailLines = 40;

    private static readonly Regex Ip = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled);

    private static readonly Regex SteamId = new(@"\b7656119\d{10}\b", RegexOptions.Compiled);
    private static readonly Regex RoomCode = new(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b", RegexOptions.Compiled);

    public static void Open() {
        try {
            var log = Sanitize(ReadModLog());
            var report = Path.Combine(Application.persistentDataPath, ReportFileName);
            File.WriteAllText(report, log, new UTF8Encoding(false));
            GUIUtility.systemCopyBuffer = log.Length > MaxClipboardChars ? log.Substring(log.Length - MaxClipboardChars) : log;
            var logs = new StringBuilder()
                .AppendLine(Lang.T("Resumen del registro. Arrastra aquí ModLog-report.txt (en la carpeta que se abrió) o pega el registro completo (Ctrl+V).",
                    "Log summary. Drag ModLog-report.txt here (from the folder that opened) or paste the full log (Ctrl+V)."))
                .AppendLine("```")
                .AppendLine(Summary(log))
                .AppendLine("```")
                .ToString();
            var url = $"{NewIssueUrl}?template={Template}&{VersionField}={Uri.EscapeDataString(HKMultiplayerMod.Version)}" +
                      $"&{LogsField}={Uri.EscapeDataString(logs)}";
            Application.OpenURL(url);
            RevealFile(report);
            Log.Info($"Reportar un problema: se abre la página del issue y la carpeta de {ReportFileName} " +
                     $"(registro limpio de {log.Length} caracteres, también copiado)");
            Notice.Show(Lang.T("Se abrió GitHub: cuenta qué pasó y arrastra ModLog-report.txt a la página (o pega el registro con Ctrl+V).",
                "GitHub opened: tell us what happened and drag ModLog-report.txt onto the page (or paste the log with Ctrl+V)."), 12f);
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo preparar el informe: {e.Message}");
            Notice.Show(Lang.T("No se pudo abrir GitHub. Adjunta a mano el ModLog.txt (mira el README).",
                "Couldn't open GitHub. Attach ModLog.txt by hand (see the README)."), 10f);
        }
    }

    /// <summary>
    /// Abre la carpeta del archivo con él seleccionado (en Windows con el Explorador, en macOS con el Finder); en Linux, la
    /// carpeta. Si no se puede seleccionar, se abre la carpeta.
    /// </summary>
    private static void RevealFile(string path) {
        try {
            switch (Application.platform) {
                case RuntimePlatform.WindowsPlayer:
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                    return;
                case RuntimePlatform.OSXPlayer:
                    System.Diagnostics.Process.Start("open", $"-R \"{path}\"");
                    return;
            }
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo seleccionar el archivo en su carpeta: {e.Message}");
        }

        Application.OpenURL(new Uri(Path.GetDirectoryName(path) ?? path).AbsoluteUri);
    }

    /// <summary>El ModLog.txt de esta sesión (lo tiene abierto la Modding API para escribir: se lee compartido).</summary>
    private static string ReadModLog() {
        var path = Path.Combine(Application.persistentDataPath, "ModLog.txt");
        if (!File.Exists(path)) {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

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

    /// <summary>Lo que más ayuda en poco espacio: el principio (versiones), los avisos y errores, y el final.</summary>
    private static string Summary(string log) {
        var lines = log.Replace("\r", string.Empty).Split('\n');
        var picked = new List<string>();
        for (var i = 0; i < lines.Length && i < HeadLines; i++) {
            picked.Add(lines[i]);
        }

        var problems = new List<string>();
        foreach (var line in lines) {
            // Los avisos de coste por frame son normales y llenarían el resumen
            if ((line.Contains("[ERROR]") || line.Contains("[WARN]")) && !line.Contains("tardó")) {
                problems.Add(line);
            }
        }

        if (problems.Count > 0) {
            picked.Add("…");
            picked.AddRange(problems.GetRange(Math.Max(0, problems.Count - ProblemLines), Math.Min(ProblemLines, problems.Count)));
        }

        picked.Add("…");
        for (var i = Math.Max(HeadLines, lines.Length - TailLines); i < lines.Length; i++) {
            picked.Add(lines[i]);
        }

        var summary = string.Join("\n", picked);
        return summary.Length > MaxSummaryChars ? "…" + summary.Substring(summary.Length - MaxSummaryChars) : summary;
    }
}
