using System.Collections.Generic;
using GameText = TeamCherry.Localization.Language;

namespace HKMultiplayer.Game.Features;

/// <summary>Nombre de cada zona del mapa en el idioma del juego (el de las ranuras de partida), guardado al pedirlo.</summary>
internal static class Zones {
    private const string Sheet = "Map Zones";

    private static readonly Dictionary<byte, string> Names = new();

    public static string Name(byte zone) {
        if (Names.TryGetValue(zone, out var name)) {
            return name;
        }

        var key = ((GlobalEnums.MapZone) zone).ToString();
        name = GameText.Has(key, Sheet) ? GameText.Get(key, Sheet).Replace("<br>", " ").Trim() : string.Empty;
        if (name.Length == 0 || name.Contains("#!#")) {
            // Sin traducción: el nombre interno legible ("FOG_CANYON" → "Fog canyon")
            var words = key.Replace('_', ' ').ToLowerInvariant();
            name = words.Length > 0 ? char.ToUpperInvariant(words[0]) + words.Substring(1) : key;
        }

        Names[zone] = name;
        return name;
    }

    /// <summary>Al cambiar el idioma del juego los nombres se vuelven a pedir.</summary>
    public static void Clear() => Names.Clear();
}
