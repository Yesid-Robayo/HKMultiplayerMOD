using System;
using System.IO;
using System.Text;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// «Reportar un problema» (menú Multijugador del título y de la pausa): casi nadie adjuntaba el ModLog.txt a los issues, y
/// cuando se abría GitHub directamente con el registro en el portapapeles tampoco lo pegaban (issue #29: solo llegó el
/// resumen). Ahora abre en el navegador una página local (<see cref="ReportPage"/>) con los pasos, el registro y botones
/// para copiarlo, abrir su carpeta, copiar la ruta del archivo (para adjuntarlo) y abrir el issue en GitHub con la versión
/// y un resumen ya puestos. El registro además queda copiado al portapapeles.
/// <para>
/// El mod no envía nada por su cuenta: el jugador ve el texto y decide si lo publica. Antes se quita del registro lo que
/// podría identificarlo (su carpeta personal, su nombre de usuario y el del PC, direcciones IP, ID de Steam y códigos de
/// partida); la página y la copia limpia del registro se guardan en <see cref="ReportFolder"/>, nunca el original.
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

    /// <summary>Carpeta (dentro de la de datos del juego) con la página y la copia limpia del registro.</summary>
    private const string ReportFolder = "HKMultiplayer/BugReport";

    private const string PageFile = "bug-report.html";
    private const string LogFile = "ModLog.txt";

    public static void Open() {
        string log;
        string url;
        try {
            log = LogSummary.Sanitize(ReadModLog());
            url = IssueUrl(log);
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo preparar el informe: {e.Message}");
            Notice.Show(Lang.T("No se pudo abrir GitHub. Adjunta a mano el ModLog.txt (mira el README).",
                "Couldn't open GitHub. Attach ModLog.txt by hand (see the README)."), 10f);
            return;
        }

        var clip = LogSummary.Clip(log, MaxClipboardChars);
        try {
            GUIUtility.systemCopyBuffer = clip;
        } catch (Exception e) {
            Log.Warn($"Reportar un problema: no se pudo copiar el registro: {e.Message}");
        }

        try {
            var folder = Path.Combine(Application.persistentDataPath, ReportFolder);
            Directory.CreateDirectory(folder);
            var logPath = Path.GetFullPath(Path.Combine(folder, LogFile));
            File.WriteAllText(logPath, log, new UTF8Encoding(false));
            var pagePath = Path.Combine(folder, PageFile);
            var folderUrl = new Uri(Path.GetFullPath(folder) + Path.DirectorySeparatorChar).AbsoluteUri;
            File.WriteAllText(pagePath, ReportPage.Build(PageTexts(), clip, url, $"HKMultiplayer {HKMultiplayerMod.Version}",
                folderUrl, logPath), new UTF8Encoding(false));
            Application.OpenURL(new Uri(Path.GetFullPath(pagePath)).AbsoluteUri);
            Log.Info($"Reportar un problema: se abre la página con los pasos (registro limpio de {log.Length} caracteres)");
            Notice.Show(Lang.T("Se abrió una página con los pasos para reportar el problema.",
                "A page opened with the steps to report the problem."), 10f);
        } catch (Exception e) {
            // Sin la página, como antes: GitHub directamente con el registro ya copiado
            Log.Warn($"Reportar un problema: no se pudo crear la página ({e.Message}); se abre GitHub");
            Application.OpenURL(url);
            Notice.Show(Lang.T("Se abrió GitHub: cuenta qué pasó y pega el registro (Ctrl+V), que ya está copiado.",
                "GitHub opened: tell us what happened and paste the log (Ctrl+V), it's already copied."), 10f);
        }
    }

    private static ReportPage.Texts PageTexts() => new() {
        Language = Lang.Current.ToString().ToLowerInvariant().Replace('_', '-'),
        Title = Lang.T("Reportar un problema", "Report a problem"),
        Intro = Lang.T("Gracias por avisar. Con el registro se puede ver qué pasó; sin él casi nunca. Sigue estos pasos:",
            "Thanks for letting us know. With the log we can see what happened; without it we almost never can. " +
            "Follow these steps:"),
        Steps = new[] {
            Lang.T("Pulsa «Copiar registro» (ya se copió al abrir esta página, pero por si copiaste otra cosa después).",
                "Press \"Copy log\" (it was already copied when this page opened, but just in case you copied something else)."),
            Lang.T("Pulsa «Abrir GitHub». Se abre el formulario con la versión y un resumen ya puestos (hace falta una cuenta " +
                   "de GitHub, que es gratis).",
                "Press \"Open GitHub\". The form opens with the version and a summary already filled in (you need a GitHub " +
                "account, which is free)."),
            Lang.T("Rellena si eras el host o el invitado, cómo te conectaste, qué pasó y cómo repetirlo.",
                "Fill in whether you were the host or the guest, how you connected, what happened and how to reproduce it."),
            Lang.T("En «ModLog.txt y capturas», debajo del resumen, pega el registro (Ctrl+V).",
                "In \"ModLog.txt and screenshots\", below the summary, paste the log (Ctrl+V)."),
            Lang.T("Si GitHub dice que el texto es demasiado largo, adjunta el archivo: pulsa «Copiar ruta del archivo», haz " +
                   "clic en la barra de abajo del campo («Paste, drop, or click to add files»), pega la ruta en la ventana que " +
                   "se abre y pulsa Abrir. También puedes arrastrarlo desde «Abrir carpeta del registro».",
                "If GitHub says the text is too long, attach the file instead: press \"Copy file path\", click the bar at " +
                "the bottom of the field (\"Paste, drop, or click to add files\"), paste the path in the window that opens " +
                "and press Open. You can also drag it from \"Open log folder\"."),
            Lang.T("Añade capturas o un vídeo si ayudan y pulsa «Create».",
                "Add screenshots or a video if they help and press \"Create\".")
        },
        Copy = Lang.T("Copiar registro", "Copy log"),
        Copied = Lang.T("¡Copiado! Ahora pégalo en GitHub (Ctrl+V).", "Copied! Now paste it on GitHub (Ctrl+V)."),
        CopyFailed = Lang.T("No se pudo copiar: haz clic en el registro de abajo, pulsa Ctrl+A y luego Ctrl+C.",
            "Couldn't copy: click the log below, press Ctrl+A and then Ctrl+C."),
        OpenGitHub = Lang.T("Abrir GitHub", "Open GitHub"),
        OpenFolder = Lang.T("Abrir carpeta del registro", "Open log folder"),
        CopyPath = Lang.T("Copiar ruta del archivo", "Copy file path"),
        PathCopied = Lang.T("Ruta copiada: pégala en la ventana para adjuntar archivos de GitHub.",
            "Path copied: paste it in GitHub's attach file window."),
        LogLabel = Lang.T("Tu registro (ModLog.txt, sin datos personales)", "Your log (ModLog.txt, without personal data)"),
        Privacy = Lang.T("Este registro ya no lleva tu carpeta personal, tu nombre de usuario ni el del PC, direcciones IP, " +
                         "ID de Steam ni códigos de partida. Nada se envía solo: tú decides qué publicas en GitHub.",
            "This log no longer has your user folder, your user or PC name, IP addresses, Steam IDs or room codes. " +
            "Nothing is sent on its own: you decide what you post on GitHub.")
    };

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
