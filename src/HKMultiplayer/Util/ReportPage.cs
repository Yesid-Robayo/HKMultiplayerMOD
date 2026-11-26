using System.Net;
using System.Text;

namespace HKMultiplayer.Util;

/// <summary>
/// La página local de «Reportar un problema» (ver <c>BugReport</c>): los pasos, el registro ya limpio con un botón para
/// copiarlo, la carpeta con esa copia limpia (para adjuntar el archivo en vez de pegarlo) y el botón que abre el issue en
/// GitHub. Antes se abría GitHub directamente con el registro en el portapapeles y casi nadie lo pegaba (issue #29: solo
/// llegó el resumen). Todo va dentro del archivo, sin nada de internet (ni letras ni scripts). Sin nada de Unity, para
/// probarlo fuera del juego (NetTests).
/// </summary>
internal static class ReportPage {
    /// <summary>Los textos de la página, ya en el idioma del juego.</summary>
    internal sealed class Texts {
        public string Language = "en";
        public string Title;
        public string Intro;
        public string[] Steps;
        public string Copy;
        public string Copied;
        public string CopyFailed;
        public string OpenGitHub;
        public string OpenFolder;
        public string CopyPath;
        public string PathCopied;
        public string LogLabel;
        public string Privacy;
    }

    /// <param name="texts">Textos en el idioma del juego.</param>
    /// <param name="log">El registro limpio que se copia y se ve en la página.</param>
    /// <param name="issueUrl">El formulario del issue con la versión y el resumen ya puestos.</param>
    /// <param name="version">Versión del mod.</param>
    /// <param name="folderUrl">La carpeta con la copia limpia del registro (file:///), para abrirla o arrastrar el archivo.</param>
    /// <param name="logPath">Ruta de esa copia, para pegarla en el cuadro de «adjuntar archivo» de GitHub.</param>
    public static string Build(Texts texts, string log, string issueUrl, string version, string folderUrl, string logPath) {
        var html = new StringBuilder((log?.Length ?? 0) + 8192);
        html.Append("<!doctype html>\n<html lang=\"").Append(Escape(texts.Language)).Append("\">\n<head>\n")
            .Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Escape(texts.Title)).Append("</title>\n<style>\n")
            .Append(":root{color-scheme:dark}")
            .Append("body{margin:0;background:#0b0c10;color:#e8e6e3;font:17px/1.5 Georgia,'Times New Roman',serif}")
            .Append("main{max-width:900px;margin:0 auto;padding:32px 16px 48px}")
            .Append("h1{font-weight:normal;letter-spacing:.04em;margin:0 0 4px}")
            .Append(".version{color:#9a9a9a;margin:0 0 20px}")
            .Append("ol{padding-left:24px}li{margin:8px 0}")
            .Append(".buttons{display:flex;flex-wrap:wrap;gap:12px;margin:20px 0}")
            .Append("button,a.button{font:inherit;cursor:pointer;border:1px solid #c9c4b8;background:#1b1d24;color:#fff;")
            .Append("padding:10px 22px;border-radius:4px;text-decoration:none}")
            .Append("button:hover,a.button:hover{background:#2a2d38}")
            .Append(".primary{border-color:#f0d38a;color:#f0d38a}")
            .Append("#status{min-height:1.5em;color:#9fe0a5}#status.bad{color:#ff8a80}")
            .Append("label{display:block;margin:24px 0 6px;color:#bdbab3}")
            .Append("textarea{box-sizing:border-box;width:100%;height:340px;background:#05060a;color:#cfcac0;")
            .Append("border:1px solid #3a3d48;border-radius:4px;padding:10px;font:13px/1.4 Consolas,Menlo,monospace}")
            .Append(".privacy{color:#8c8a85;font-size:15px}")
            .Append("</style>\n</head>\n<body>\n<main>\n")
            .Append("<h1>").Append(Escape(texts.Title)).Append("</h1>\n")
            .Append("<p class=\"version\">").Append(Escape(version)).Append("</p>\n")
            .Append("<p>").Append(Escape(texts.Intro)).Append("</p>\n<ol>\n");
        foreach (var step in texts.Steps ?? new string[0]) {
            html.Append("<li>").Append(Escape(step)).Append("</li>\n");
        }

        html.Append("</ol>\n<div class=\"buttons\">\n")
            .Append("<button id=\"copy\" class=\"primary\" type=\"button\">").Append(Escape(texts.Copy)).Append("</button>\n")
            .Append("<a class=\"button primary\" href=\"").Append(Escape(issueUrl))
            .Append("\" target=\"_blank\" rel=\"noopener noreferrer\">").Append(Escape(texts.OpenGitHub)).Append("</a>\n")
            .Append("<a class=\"button\" href=\"").Append(Escape(folderUrl)).Append("\" target=\"_blank\">")
            .Append(Escape(texts.OpenFolder)).Append("</a>\n")
            .Append("<button id=\"path\" type=\"button\" data-path=\"").Append(Escape(logPath)).Append("\">")
            .Append(Escape(texts.CopyPath)).Append("</button>\n")
            .Append("</div>\n<p id=\"status\" role=\"status\"></p>\n")
            .Append("<label for=\"log\">").Append(Escape(texts.LogLabel)).Append("</label>\n")
            .Append("<textarea id=\"log\" readonly spellcheck=\"false\">").Append(Escape(log)).Append("</textarea>\n")
            .Append("<p class=\"privacy\">").Append(Escape(texts.Privacy)).Append("</p>\n")
            .Append("</main>\n<script>\n")
            .Append("var copied=").Append(Js(texts.Copied)).Append(",failed=").Append(Js(texts.CopyFailed))
            .Append(",pathCopied=").Append(Js(texts.PathCopied)).Append(";\n")
            .Append("var log=document.getElementById('log'),status=document.getElementById('status');\n")
            .Append("function show(ok,text){status.textContent=ok?text:failed;status.className=ok?'':'bad';}\n")
            // Sin el portapapeles moderno (o si lo niega), el de siempre: seleccionar un texto y copiar
            .Append("function viaSelection(text,done){var area=document.createElement('textarea');area.value=text;")
            .Append("document.body.appendChild(area);area.select();var ok=false;try{ok=document.execCommand('copy');}catch(e){}")
            .Append("document.body.removeChild(area);show(ok,done);}\n")
            .Append("function copy(text,done){if(navigator.clipboard&&navigator.clipboard.writeText){")
            .Append("navigator.clipboard.writeText(text).then(function(){show(true,done);},function(){viaSelection(text,done);});")
            .Append("}else{viaSelection(text,done);}}\n")
            .Append("document.getElementById('copy').addEventListener('click',function(){copy(log.value,copied);});\n")
            .Append("var path=document.getElementById('path');\n")
            .Append("path.addEventListener('click',function(){copy(path.getAttribute('data-path'),pathCopied);});\n")
            .Append("</script>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Texto seguro dentro del HTML (también en atributos entre comillas dobles).</summary>
    private static string Escape(string text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>Cadena de JavaScript entre comillas, sin nada que cierre el script.</summary>
    private static string Js(string text) {
        var js = new StringBuilder("\"");
        foreach (var c in text ?? string.Empty) {
            switch (c) {
                case '\\': js.Append("\\\\"); break;
                case '"': js.Append("\\\""); break;
                case '<': js.Append("\\u003c"); break;
                case '>': js.Append("\\u003e"); break;
                case '&': js.Append("\\u0026"); break;
                case '\n': js.Append("\\n"); break;
                case '\r': break;
                default: js.Append(c); break;
            }
        }

        return js.Append('"').ToString();
    }
}
