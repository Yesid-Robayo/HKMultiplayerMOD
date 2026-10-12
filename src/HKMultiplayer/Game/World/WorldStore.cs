using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HKMultiplayer.Util;
using Modding;
using Modding.Patches;
using Newtonsoft.Json;
using UnityEngine;
using HKMultiplayer.UI;

namespace HKMultiplayer.Game.World;

/// <summary>Datos de un mundo coop para listarlo sin cargarlo.</summary>
public sealed class CoopWorldInfo {
    /// <summary>Nombre de la carpeta (único, sin caracteres raros).</summary>
    [JsonIgnore]
    public string Id { get; set; }

    /// <summary>Nombre que eligió el host.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tiempo jugado del host, en segundos.</summary>
    public float PlayTime { get; set; }

    /// <summary>Zona del último guardado (nombre interno del mapa).</summary>
    public string Area { get; set; } = string.Empty;

    /// <summary>Cuándo se guardó por última vez (UTC).</summary>
    public DateTime LastSaved { get; set; }

    /// <summary>Si aún no se ha guardado nunca (mundo recién creado: se empieza partida nueva).</summary>
    [JsonIgnore]
    public bool IsNew { get; set; }
}

/// <summary>
/// Mundos coop guardados en el PC del host, aparte de las 4 partidas normales del juego (que nunca se tocan):
/// <c>&lt;datos del juego&gt;/HKMultiplayer/Mundos/&lt;id&gt;/</c> con <c>info.json</c> (para la lista) y
/// <c>world.json</c> (la partida completa: datos del jugador y de las salas, en el mismo formato que el juego). Cada
/// guardado deja el anterior como <c>world.json.bak</c>.
/// </summary>
public static class WorldStore {
    /// <summary>Máximo de mundos coop (como las ranuras del juego).</summary>
    public const int MaxWorlds = 4;

    private const string InfoFile = "info.json";
    private const string WorldFile = "world.json";

    public static string Root => Path.Combine(Path.Combine(Application.persistentDataPath, "HKMultiplayer"), "Mundos");

    /// <summary>Ajustes de JSON iguales a los del guardado del juego con la Modding API.</summary>
    internal static readonly JsonSerializerSettings SaveSettings = new() {
        ContractResolver = ShouldSerializeContractResolver.Instance,
        TypeNameHandling = TypeNameHandling.Auto,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Converters = JsonConverterTypes.ConverterTypes
    };

    /// <summary>
    /// Para lo que llega de otro PC (el mundo del host, el personaje de un invitado): sin nombres de tipo, que con
    /// <c>TypeNameHandling.Auto</c> dejarían a quien envía elegir qué clase se crea al leerlo. <c>PlayerData</c> y
    /// <c>SceneData</c> no tienen miembros polimórficos, así que se leen igual.
    /// </summary>
    internal static readonly JsonSerializerSettings NetworkSettings = new() {
        ContractResolver = ShouldSerializeContractResolver.Instance,
        TypeNameHandling = TypeNameHandling.None,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        Converters = JsonConverterTypes.ConverterTypes
    };

    public static List<CoopWorldInfo> List() {
        var worlds = new List<CoopWorldInfo>();
        if (!Directory.Exists(Root)) {
            return worlds;
        }

        foreach (var dir in Directory.GetDirectories(Root)) {
            var info = ReadInfo(Path.GetFileName(dir));
            if (info != null) {
                worlds.Add(info);
            }
        }

        return worlds.OrderByDescending(w => w.LastSaved).ToList();
    }

    public static CoopWorldInfo ReadInfo(string id) {
        var dir = Path.Combine(Root, id);
        try {
            var info = JsonConvert.DeserializeObject<CoopWorldInfo>(File.ReadAllText(Path.Combine(dir, InfoFile)));
            if (info == null) {
                return null;
            }

            info.Id = id;
            info.IsNew = !File.Exists(Path.Combine(dir, WorldFile));
            return info;
        } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Borra un mundo y sus copias de este PC.</summary>
    public static void Delete(string id) {
        var dir = Path.Combine(Root, id);
        if (Directory.Exists(dir)) {
            Directory.Delete(dir, true);
            Log.Info($"Mundo coop borrado: {id}");
        }
    }

    /// <summary>Crea un mundo vacío (se empezará con partida nueva). Null si ya hay el máximo.</summary>
    public static CoopWorldInfo Create(string name) {
        if (List().Count >= MaxWorlds) {
            Log.Warn($"No se crea el mundo: ya hay {MaxWorlds}");
            return null;
        }

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) {
            name = Lang.T("Mundo coop", "Coop world");
        }

        if (name.Length > 40) {
            name = name.Substring(0, 40);
        }

        var baseId = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (baseId.Length == 0) {
            baseId = "mundo";
        }

        var id = baseId;
        for (var i = 2; Directory.Exists(Path.Combine(Root, id)); i++) {
            id = $"{baseId}-{i}";
        }

        Directory.CreateDirectory(Path.Combine(Root, id));
        var info = new CoopWorldInfo { Id = id, Name = name, LastSaved = DateTime.UtcNow, IsNew = true };
        WriteInfo(info);
        return info;
    }

    /// <summary>Lee la partida de un mundo (null si es nuevo o no se pudo leer).</summary>
    public static SaveGameData Load(string id) {
        var path = Path.Combine(Path.Combine(Root, id), WorldFile);
        foreach (var candidate in new[] { path, path + ".bak" }) {
            if (!File.Exists(candidate)) {
                continue;
            }

            try {
                var data = JsonConvert.DeserializeObject<SaveGameData>(File.ReadAllText(candidate), SaveSettings);
                if (data?.playerData != null && data.sceneData != null) {
                    if (candidate != path) {
                        Log.Warn($"El mundo {id} estaba dañado: se cargó la copia anterior");
                    }

                    return data;
                }
            } catch (Exception e) when (e is IOException or JsonException) {
                Log.Warn($"No se pudo leer {candidate}: {e.Message}");
            }
        }

        return null;
    }

    /// <summary>Guarda la partida de un mundo (el guardado anterior queda como copia).</summary>
    public static void Save(CoopWorldInfo info, SaveGameData data) {
        var dir = Path.Combine(Root, info.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, WorldFile);
        var json = JsonConvert.SerializeObject(data, Formatting.None, SaveSettings);

        // Se escribe aparte y luego se cambia: si el juego se cierra a medias, queda el guardado anterior entero
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        if (File.Exists(path)) {
            if (File.Exists(path + ".bak")) {
                File.Delete(path + ".bak");
            }

            File.Move(path, path + ".bak");
        }

        File.Move(temp, path);

        info.PlayTime = data.playerData.playTime;
        info.Area = data.playerData.mapZone.ToString();
        info.LastSaved = DateTime.UtcNow;
        info.IsNew = false;
        WriteInfo(info);
    }

    /// <summary>Personaje guardado de un jugador en este mundo (null si es la primera vez).</summary>
    public static Newtonsoft.Json.Linq.JObject LoadCharacter(string worldId, string playerKey) {
        var path = CharacterPath(worldId, playerKey);
        // El anterior (.bak) si el último no está o no se puede leer: el juego pudo cerrarse justo al guardarlo
        foreach (var candidate in new[] { path, path + ".bak" }) {
            try {
                if (File.Exists(candidate)) {
                    var character = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(candidate));
                    if (candidate != path) {
                        Log.Warn($"El personaje {playerKey} estaba dañado o faltaba: se cargó la copia anterior");
                    }

                    return character;
                }
            } catch (Exception e) when (e is IOException or JsonException) {
                Log.Warn($"No se pudo leer el personaje {playerKey} ({Path.GetFileName(candidate)}): {e.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Como el mundo: se escribe aparte y el anterior queda como <c>.bak</c>. Antes se borraba el anterior y luego se
    /// movía el nuevo: si el juego se cerraba entre medias, el invitado se quedaba sin personaje y empezaba uno nuevo.
    /// </summary>
    public static void SaveCharacter(string worldId, string playerKey, Newtonsoft.Json.Linq.JObject character) {
        var path = CharacterPath(worldId, playerKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, character.ToString(Formatting.None), new UTF8Encoding(false));
        if (File.Exists(path)) {
            if (File.Exists(path + ".bak")) {
                File.Delete(path + ".bak");
            }

            File.Move(path, path + ".bak");
        }

        File.Move(temp, path);
    }

    /// <summary>La clave ya viene validada (solo hexadecimal): no puede salirse de la carpeta del mundo.</summary>
    private static string CharacterPath(string worldId, string playerKey) =>
        Path.Combine(Path.Combine(Path.Combine(Root, worldId), "players"), playerKey + ".json");

    private static void WriteInfo(CoopWorldInfo info) {
        File.WriteAllText(Path.Combine(Path.Combine(Root, info.Id), InfoFile),
            JsonConvert.SerializeObject(info, Formatting.Indented), new UTF8Encoding(false));
    }
}
