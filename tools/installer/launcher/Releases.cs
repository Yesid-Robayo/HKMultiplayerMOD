using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HKMultiplayer.Setup;

/// <summary>Una versión publicada en GitHub.</summary>
internal sealed class Release {
    internal string Tag;
    internal Version Version;
    internal string Body;
    internal string Url;
}

/// <summary>
/// Versiones publicadas del mod. Todas se publican como pre-release, así que no sirve "latest": se pide la lista. El
/// cartel de versión nueva y el botón "Novedades" de la ventana salen de aquí.
/// </summary>
internal static class Releases {
    internal const string ApiUrl = "https://api.github.com/repos/Yesid-Robayo/HKMultiplayerMOD/releases?per_page=20";
    internal const string PageUrl = "https://github.com/Yesid-Robayo/HKMultiplayerMOD/releases";

    internal static WebClient NewClient(string packageVersion) {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var client = new WebClient { Encoding = Encoding.UTF8 };
        client.Headers.Add("User-Agent", $"HKMultiplayer-Setup/{packageVersion}");
        client.Headers.Add("Accept", "application/vnd.github+json");
        return client;
    }

    /// <summary>Los números del principio de la etiqueta: "v0.26.0-hotfix" cuenta como 0.26.0.</summary>
    internal static Version ParseVersion(string tag) {
        var match = Regex.Match(tag ?? string.Empty, @"^[vV]?(\d+(\.\d+){1,3})");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    /// <summary>Las versiones publicadas (sin borradores ni etiquetas sin número) de la respuesta de GitHub.</summary>
    internal static List<Release> Parse(string json) {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var list = new List<Release>();
        if (serializer.DeserializeObject(json) is not object[] items) {
            return list;
        }

        foreach (var item in items.OfType<Dictionary<string, object>>()) {
            if (item.TryGetValue("draft", out var draft) && draft is true) {
                continue;
            }

            var tag = item.TryGetValue("tag_name", out var tagValue) ? tagValue as string : null;
            var version = ParseVersion(tag);
            if (version == null) {
                continue;
            }

            list.Add(new Release {
                Tag = tag,
                Version = version,
                Body = item.TryGetValue("body", out var body) ? body as string ?? string.Empty : string.Empty,
                Url = item.TryGetValue("html_url", out var url) ? url as string ?? PageUrl : PageUrl
            });
        }

        return list;
    }

    /// <summary>El apartado "Novedades de la X" / "What's new in X" de las notas, en el idioma de Windows, de Markdown a texto.</summary>
    internal static string Notes(Release release) {
        var body = release.Body.Replace("\r", string.Empty);
        var marker = Lang.Spanish ? "### Novedades" : "### What's new";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0) {
            var rest = body.Substring(start);
            var next = rest.IndexOf("\n### ", 1, StringComparison.Ordinal);
            body = next > 0 ? rest.Substring(0, next) : rest;
        }

        // Sin el título del apartado (la ventana ya lo tiene)
        body = Regex.Replace(body, @"^###[^\n]*\n", string.Empty);
        body = Regex.Replace(body, @"(?m)^#+\s*", string.Empty);
        body = Regex.Replace(body, @"\[([^\]]+)\]\([^)]+\)", "$1");
        body = body.Replace("**", string.Empty).Replace("`", string.Empty);
        body = Regex.Replace(body, @"(?m)^\s*[-*]\s+", "•  ");
        body = Regex.Replace(body, @"(?<![\w*])\*([^*\n]+)\*(?![\w*])", "$1");
        return body.Trim();
    }
}
