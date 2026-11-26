using System;
using System.IO;
using System.Text;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// «Reportar un problema» (menú Multijugador del título y de la pausa): casi nadie adjuntaba el ModLog.txt a los issues, y
/// abriendo GitHub directamente con el registro en el portapapeles tampoco lo pegaban (issue #29: solo llegó el resumen).
/// Ahora el botón lleva a una página del propio menú con los pasos (<see cref="GameStyledMenu"/>, «Reportar») y botones
/// para copiar el registro, abrir el issue en GitHub con la versión y un resumen ya puestos y abrir la carpeta con una copia
/// limpia del registro, para arrastrarla si el texto no cabe.
/// <para>
/// El mod no envía nada por su cuenta: el jugador ve el texto y decide si lo publica. Antes se quita del registro lo que
/// podría identificarlo (su carpeta personal, su nombre de usuario y el del PC, direcciones IP, ID de Steam y códigos de
/// partida); la copia limpia se guarda en <see cref="ReportFolder"/>, nunca se abre el original.
/// </para>
/// </summary>
internal static class BugReport {
    private const string NewIssueUrl = "https://github.com/Yesid-Robayo/HKMultiplayerMOD/issues/new";
    private const string Template = "bug_report.yml";

    /// <summary>Campos de la plantilla de issues (.github/ISSUE_TEMPLATE/bug_report.yml) que se rellenan.</summary>
    private const string VersionField = "version";

    private const string LogsField = "logs";

    /// <summary>
    /// Lo más largo que puede ser la dirección del issue ya codificada: GitHub rechaza las de más de unos 8 KB (414), y
    /// el registro entero (75-100 KB) no cabe nunca. Por eso en la dirección va un resumen y el registro se pega o adjunta.
    /// </summary>
    private const int MaxUrlLength = 7000;

    /// <summary>Desde qué tamaño de resumen se prueba (se va acortando hasta que la dirección cabe) y el mínimo.</summary>
    private const int MaxSummaryChars = 6500;

    private const int MinSummaryChars = 800;

    /// <summary>Lo que cabe del registro para pegarlo en GitHub (su campo admite 65 536 caracteres, con el resumen).</summary>
    private const int MaxClipboardChars = 55000;

    /// <summary>Carpeta (dentro de la de datos del juego) con la copia limpia del registro.</summary>
    private const string ReportFolder = "HKMultiplayer/BugReport";

    private const string LogFile = "ModLog.txt";

    /// <summary>Lo preparado al abrir la página (<see cref="Prepare"/>).</summary>
    private static string _clip;

    private static string _url;
    private static string _folder;

    /// <summary>
    /// Al abrir la página de «Reportar»: lee y limpia el registro, guarda la copia limpia, lo copia al portapapeles y arma
    /// la dirección del issue. False si no se pudo (el jugador tendrá que adjuntar el ModLog.txt a mano).
    /// </summary>
    public static bool Prepare() {
        try {
            var log = LogSummary.Sanitize(ReadModLog());
            _url = IssueUrl(log);
            _clip = LogSummary.Clip(log, MaxClipboardChars);
            _folder = Path.GetFullPath(Path.Combine(Application.persistentDataPath, ReportFolder));
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, LogFile), log, new UTF8Encoding(false));
            var copied = CopyLog();
            Log.Info($"Reportar un problema: registro limpio de {log.Length} caracteres preparado{(copied ? " y copiado" : "")}");
            return copied;
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo preparar el informe: {e.Message}");
            return false;
        }
    }

    /// <summary>Vuelve a copiar el registro (por si el jugador copió otra cosa después).</summary>
    public static bool CopyLog() {
        if (_clip == null) {
            return false;
        }

        try {
            GUIUtility.systemCopyBuffer = _clip;
            return true;
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo copiar el registro: {e.Message}");
            return false;
        }
    }

    /// <summary>Abre en el navegador el formulario del issue con la versión y el resumen.</summary>
    public static bool OpenGitHub() {
        if (_url == null) {
            return false;
        }

        try {
            Application.OpenURL(_url);
            Log.Info("Reportar un problema: se abre la página del issue");
            return true;
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo abrir GitHub: {e.Message}");
            return false;
        }
    }

    /// <summary>Abre la carpeta con la copia limpia del registro (como la de los mundos: en todos los sistemas).</summary>
    public static bool OpenFolder() {
        if (_folder == null) {
            return false;
        }

        try {
            Application.OpenURL(new Uri(_folder + Path.DirectorySeparatorChar).AbsoluteUri);
            return true;
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo abrir la carpeta del registro: {e.Message}");
            return false;
        }
    }

    /// <summary>La dirección del issue nuevo con la versión y el resumen más largo que quepa.</summary>
    private static string IssueUrl(string log) {
        var url = string.Empty;
        // Se prueba con todo y se va acortando hasta que la dirección ya codificada cabe (GitHub rechaza las muy largas)
        for (var chars = MaxSummaryChars; chars >= MinSummaryChars; chars = chars * 4 / 5) {
            url = IssueUrl(log, chars);
            if (url.Length <= MaxUrlLength) {
                break;
            }
        }

        return url;
    }

    /// <summary>La dirección del issue nuevo con la versión y un resumen de hasta <paramref name="summaryChars"/> caracteres.</summary>
    private static string IssueUrl(string log, int summaryChars) {
        var logs = new StringBuilder()
            .AppendLine(Lang.T("⚠ IMPORTANTE: esto es solo un resumen. El ModLog.txt completo ya está copiado: pégalo aquí " +
                               "debajo (Ctrl+V) antes de enviar el issue. Sin él casi nunca se puede saber qué pasó.",
                "⚠ IMPORTANT: this is only a summary. The full ModLog.txt is already copied: paste it below (Ctrl+V) " +
                "before submitting the issue. Without it we can rarely tell what happened."))
            .AppendLine("```")
            .AppendLine(LogSummary.Summary(log, summaryChars))
            .AppendLine("```")
            .ToString();
        return $"{NewIssueUrl}?template={Template}&{VersionField}={Uri.EscapeDataString(HKMultiplayerMod.Version)}" +
               $"&{LogsField}={Uri.EscapeDataString(logs)}";
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
}
