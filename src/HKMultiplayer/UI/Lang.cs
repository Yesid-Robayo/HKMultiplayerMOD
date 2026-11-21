using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HKMultiplayer.Util;
using Newtonsoft.Json;
using TeamCherry.Localization;

namespace HKMultiplayer.UI;

/// <summary>
/// Idioma de los textos del mod: el del juego. Cada texto se escribe en español y en inglés en el código; los demás
/// idiomas del juego (francés, alemán, italiano, portugués, ruso, chino, chino tradicional, japonés y coreano) salen de
/// archivos de traducción embebidos en el mod (<c>Lang/&lt;código&gt;.json</c>), indexados por el texto en inglés. Lo que
/// falte en una traducción sale en inglés.
/// <para>
/// Se mira el idioma cada vez que se pide un texto: los avisos, estados y el HUD cambian en cuanto se cambia el idioma en
/// las opciones; los nombres de los menús se ponen al crearlos (al abrir el juego).
/// </para>
/// </summary>
internal static class Lang {
    private static readonly Dictionary<LanguageCode, Dictionary<string, string>> Tables = new();

    /// <summary>
    /// Textos fijos ya entregados (en el idioma de entonces) con su pareja español/inglés: así los menús, que se crean una
    /// vez, se vuelven a rotular al cambiar de idioma (ver <see cref="Relabel"/>). Con límite: solo textos sin datos.
    /// </summary>
    private static readonly Dictionary<string, (string Spanish, string English)> Given = new();

    private const int MaxGiven = 4000;

    /// <summary>
    /// Pareja de cada texto de menú ya rotulado y lo que se le puso: al cambiar otra vez de idioma se usa esta pareja y no
    /// se busca por la traducción, porque dos textos distintos pueden traducirse igual (en japonés «Nombres» y «Tu
    /// nombre» son los dos 名前) y se confundían.
    /// </summary>
    private static readonly Dictionary<long, ((string Spanish, string English) Pair, string Shown)> Labelled = new();

    /// <summary>Lo apuntado de un texto (para el diagnóstico).</summary>
    public static string DescribeGiven(string text) =>
        $"apuntados={Given.Count} rotulados={Labelled.Count} " +
        (Given.TryGetValue(text, out var pair) ? $"pareja=({pair.Spanish} / {pair.English})" : "sin pareja");

    private static bool TryPair(long key, string current, out (string Spanish, string English) pair) {
        // El texto pudo cambiar desde entonces (los que se refrescan solos): solo vale si sigue siendo el que se puso
        if (Labelled.TryGetValue(key, out var known) && known.Shown == current) {
            pair = known.Pair;
            return true;
        }

        return Given.TryGetValue(current, out pair);
    }

    private static string Relabelled(long key, (string Spanish, string English) pair) {
        var text = T(pair.Spanish, pair.English);
        Labelled[key] = (pair, text);
        return text;
    }

    /// <summary>Idioma del juego ahora (inglés si no se puede leer).</summary>
    public static LanguageCode Current {
        get {
            try {
                return TeamCherry.Localization.Language.CurrentLanguage();
            } catch (Exception) {
                return LanguageCode.EN;
            }
        }
    }

    /// <summary>Si los textos van en español.</summary>
    public static bool Spanish => Current == LanguageCode.ES;

    /// <summary>El texto en el idioma del juego.</summary>
    public static string T(string spanish, string english) {
        var text = Pick(spanish, english);
        Remember(text, spanish, english);
        return text;
    }

    private static string Pick(string spanish, string english) {
        var language = Current;
        return language switch {
            LanguageCode.ES => spanish,
            LanguageCode.EN => english,
            _ => Lookup(language, english) ?? english
        };
    }

    private static void Remember(string text, string spanish, string english) {
        if (!string.IsNullOrEmpty(text) && Given.Count < MaxGiven && !Given.ContainsKey(text)) {
            Given[text] = (spanish, english);
        }
    }

    /// <summary>
    /// Vuelve a rotular un menú ya creado en el idioma de ahora: cada texto y cada valor de selector que salió de
    /// <see cref="T"/> o <see cref="Labels"/> se cambia por su traducción. Lo que lleva datos ya se refresca solo.
    /// </summary>
    public static void Relabel(UnityEngine.Component root) {
        if (root == null) {
            return;
        }

        foreach (var text in root.GetComponentsInChildren<UnityEngine.UI.Text>(true)) {
            // Los botones llevan un salto de línea al final (para centrarse): se compara sin él y se conserva
            var current = text.text;
            if (string.IsNullOrEmpty(current)) {
                continue;
            }

            var core = current.TrimEnd();
            long key = text.GetInstanceID();
            if (TryPair(key, core, out var pair)) {
                text.text = Relabelled(key, pair) + current.Substring(core.Length);
            }
        }

        foreach (var option in root.GetComponentsInChildren<UnityEngine.UI.MenuOptionHorizontal>(true)) {
            if (option.optionList == null) {
                continue;
            }

            // Lista propia: varias opciones comparten la de Sí/No, y cambiarla aquí cambiaba también el texto de origen
            option.optionList = (string[]) option.optionList.Clone();
            for (var i = 0; i < option.optionList.Length; i++) {
                var key = ((long) option.GetInstanceID() << 8) + i;
                if (option.optionList[i] != null && TryPair(key, option.optionList[i], out var pair)) {
                    option.optionList[i] = Relabelled(key, pair);
                }
            }

            if (option.optionText != null && option.selectedOptionIndex >= 0 && option.selectedOptionIndex < option.optionList.Length) {
                option.optionText.text = option.optionList[option.selectedOptionIndex];
            }
        }
    }

    /// <summary>
    /// Como <see cref="T"/>, para un texto con datos: plantillas con <c>{0}</c>, <c>{1}</c>... (como <c>string.Format</c>) y
    /// los valores aparte, para que la traducción se busque por la plantilla en inglés.
    /// </summary>
    public static string F(string spanish, string english, params object[] values) {
        var template = T(spanish, english);
        try {
            return string.Format(template, values);
        } catch (FormatException) {
            // Una traducción con los huecos mal escritos: mejor en inglés que nada
            return string.Format(english, values);
        }
    }

    /// <summary>Etiquetas de un selector (Sí/No...), traducidas una a una desde las inglesas.</summary>
    public static string[] Labels(string[] spanish, string[] english) {
        var language = Current;
        if (language == LanguageCode.ES) {
            Remember(spanish, spanish, english);
            return spanish;
        }

        if (language == LanguageCode.EN) {
            Remember(english, spanish, english);
            return english;
        }

        var labels = new string[english.Length];
        for (var i = 0; i < english.Length; i++) {
            labels[i] = Lookup(language, english[i]) ?? english[i];
        }

        Remember(labels, spanish, english);
        return labels;
    }

    private static void Remember(string[] labels, string[] spanish, string[] english) {
        for (var i = 0; i < labels.Length && i < spanish.Length && i < english.Length; i++) {
            Remember(labels[i], spanish[i], english[i]);
        }
    }

    private static string Lookup(LanguageCode language, string english) {
        if (!Tables.TryGetValue(language, out var table)) {
            table = Load(language);
            Tables[language] = table;
        }

        return table != null && table.TryGetValue(english, out var text) && !string.IsNullOrEmpty(text) ? text : null;
    }

    /// <summary>La tabla de un idioma (embebida en el mod); null si no hay.</summary>
    private static Dictionary<string, string> Load(LanguageCode language) {
        var name = $"HKMultiplayer.Lang.{language}.json";
        try {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) {
                return null;
            }

            using var reader = new StreamReader(stream);
            var table = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
            Log.Info($"Traducción del mod cargada: {language} ({table?.Count ?? 0} textos)");
            return table;
        } catch (Exception e) when (e is JsonException or IOException) {
            Log.Warn($"No se pudo leer la traducción {language}: {e.Message}");
            return null;
        }
    }
}
