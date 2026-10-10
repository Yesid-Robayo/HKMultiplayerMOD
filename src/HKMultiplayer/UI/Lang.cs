using System;
using TeamCherry.Localization;

namespace HKMultiplayer.UI;

/// <summary>
/// Idioma de los textos del mod: el del juego. En español si el juego está en español; en cualquier otro idioma, en
/// inglés. Se mira cada vez que se pide un texto: los avisos, estados y el HUD cambian en cuanto se cambia el idioma en
/// las opciones; los nombres de los menús se ponen al crearlos (al abrir el juego).
/// </summary>
internal static class Lang {
    /// <summary>Si los textos van en español.</summary>
    public static bool Spanish {
        get {
            try {
                return TeamCherry.Localization.Language.CurrentLanguage() == LanguageCode.ES;
            } catch (Exception) {
                return false;
            }
        }
    }

    /// <summary>El texto en el idioma del juego.</summary>
    public static string T(string spanish, string english) => Spanish ? spanish : english;
}
