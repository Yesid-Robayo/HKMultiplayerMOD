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

    /// <summary>
    /// Código de la partida de este mundo: se crea la primera vez que se abre y es siempre el mismo, para que los
    /// compañeros no tengan que volver a escribirlo (el menú lo recuerda en «Partidas recientes»). Solo está en este PC.
    /// </summary>
    public string RoomCode { get; set; } = string.Empty;

    /// <summary>Modo del mundo: <see cref="CoopWorldMode.Classic"/>, Alma de Acero o Buscador de Dioses.</summary>
    public CoopWorldMode Mode { get; set; }

    /// <summary>
    /// Mundo de Alma de Acero en el que cayeron todos a la vez: se perdió, como la partida de ese modo jugando solo. No se
    /// puede volver a hostear; solo borrar.
    /// </summary>
    public bool Lost { get; set; }

    /// <summary>Si aún no se ha guardado nunca (mundo recién creado: se empieza partida nueva).</summary>
    [JsonIgnore]
    public bool IsNew { get; set; }
}

/// <summary>Modo de juego de un mundo coop, como los de una partida nueva del juego.</summary>
public enum CoopWorldMode {
    Classic = 0,

    /// <summary>Alma de Acero: si caen todos a la vez, el mundo se pierde.</summary>
    SteelSoul = 1,

    /// <summary>Buscador de Dioses: solo Dios Hogar, con todo el equipo desde el principio.</summary>
    Godseeker = 2
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

    /// <summary>Mundos cuyo problema ya se escribió en el registro: la lista se vuelve a leer cada vez que se abre el menú.</summary>
    private static readonly HashSet<string> Reported = new();

    public static CoopWorldInfo ReadInfo(string id) {
        var dir = Path.Combine(Root, id);
        var path = Path.Combine(dir, InfoFile);
        foreach (var candidate in SafeFile.Candidates(path)) {
            try {
                if (!File.Exists(candidate)) {
                    continue;
                }

                // Un archivo vacío da null: tras un apagón puede quedar así
                var info = JsonConvert.DeserializeObject<CoopWorldInfo>(File.ReadAllText(candidate));
                if (info == null) {
                    ReportOnce(id, $"La ficha del mundo {id} ({Path.GetFileName(candidate)}) está vacía");
                    continue;
                }

                if (candidate != path) {
                    ReportOnce(id, $"La ficha del mundo {id} estaba dañada: se usa {Path.GetFileName(candidate)}");
                }

                info.Id = id;
                info.IsNew = !HasSave(dir);
                return info;
            } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
                ReportOnce(id, $"No se pudo leer la ficha del mundo {id} ({Path.GetFileName(candidate)}): {e.Message}");
            }
        }

        return RebuildInfo(id);
    }

    /// <summary>
    /// Rehace la ficha de un mundo que tiene partida pero cuya ficha falta o no se puede leer, para que no desaparezca de
    /// la lista (issue #19: un corte de luz dejó <c>info.json</c> inservible y el mundo dejó de salir). El código de la
    /// partida no se puede recuperar: se crea uno nuevo al abrirlo.
    /// </summary>
    private static CoopWorldInfo RebuildInfo(string id) {
        var dir = Path.Combine(Root, id);
        if (!HasSave(dir)) {
            return null;
        }

        var data = Load(id);
        if (data == null) {
            ReportOnce(id, $"El mundo {id} no tiene ficha ni partida legible: no se muestra");
            return null;
        }

        var lastSaved = SafeFile.Candidates(Path.Combine(dir, WorldFile)).Where(File.Exists)
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.UtcNow).Max();
        var info = new CoopWorldInfo {
            Id = id,
            Name = id,
            PlayTime = data.playerData.playTime,
            Area = data.playerData.mapZone.ToString(),
            LastSaved = lastSaved
        };

        try {
            WriteInfo(info);
            Log.Warn($"Ficha del mundo {id} rehecha a partir de su partida");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            ReportOnce(id, $"Ficha del mundo {id} rehecha, pero no se pudo guardar: {e.Message}");
        }

        return info;
    }

    /// <summary>Si el mundo ya se guardó alguna vez (el último guardado o su copia).</summary>
    private static bool HasSave(string dir) => SafeFile.Candidates(Path.Combine(dir, WorldFile)).Any(File.Exists);

    private static void ReportOnce(string id, string message) {
        if (Reported.Add(id + "|" + message)) {
            Log.Warn(message);
        }
    }

    /// <summary>Borra un mundo y sus copias de este PC.</summary>
    /// <summary>
    /// Lee una de las 4 ranuras normales del juego (sin tocarla) como lo hace el juego al cargarla: cifrada o no, y luego
    /// JSON. Llama a <paramref name="done"/> en el hilo del juego con la partida, o null si la ranura está vacía o no se lee.
    /// </summary>
    public static void ReadSlot(int slot, Action<SaveGameData> done) {
        if (Platform.Current == null || !Platform.IsSaveSlotIndexValid(slot)) {
            done(null);
            return;
        }

        Platform.Current.ReadSaveSlot(slot, bytes => {
            SaveGameData data = null;
            try {
                if (bytes != null && bytes.Length > 0) {
                    string text;
                    try {
                        var formatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                        text = StringEncrypt.DecryptData((string) formatter.Deserialize(new MemoryStream(bytes)));
                    } catch (Exception e) when (e is not OutOfMemoryException) {
                        // Sin cifrar (el juego también guarda así según la plataforma)
                        text = Encoding.UTF8.GetString(bytes);
                    }

                    data = JsonConvert.DeserializeObject<SaveGameData>(text, SaveSettings);
                }
            } catch (Exception e) when (e is JsonException or FormatException or IOException or ArgumentException) {
                Log.Warn($"No se pudo leer la partida de la ranura {slot}: {e.Message}");
                data = null;
            }

            CoreLoop.InvokeNext(() => done(data));
        });
    }

    /// <summary>
    /// Crea un mundo como copia de una partida del juego: su progreso y el personaje del host. La partida original no se
    /// toca (lo jugado en coop no vuelve a ella). El modo sale de la partida (Alma de Acero, Buscador de Dioses). Null si
    /// ya hay el máximo de mundos.
    /// </summary>
    public static CoopWorldInfo CreateFromSave(string name, SaveGameData data) {
        var pd = data.playerData;
        var mode = pd.bossRushMode ? CoopWorldMode.Godseeker
            : pd.permadeathMode == 1 ? CoopWorldMode.SteelSoul
            : CoopWorldMode.Classic;
        var info = Create(name, mode);
        if (info == null) {
            return null;
        }

        Save(info, data);
        Log.Info($"Mundo coop {info.Name} creado desde una partida ({mode}, {pd.playTime / 3600f:0.0} h)");
        return info;
    }

    /// <summary>Carpeta de las copias de seguridad dentro de la del mundo, y cuántas se guardan.</summary>
    public const string BackupFolder = "Copias";

    private const int MaxBackups = 5;

    /// <summary>
    /// Copia de seguridad del mundo al abrirlo (cada sesión): sus archivos (mundo, datos, ajustes y personajes) en
    /// <c>Copias/&lt;fecha y hora&gt;/</c> dentro de su carpeta. Se guardan las últimas <see cref="MaxBackups"/>. Para volver
    /// a una, se copian sus archivos encima de los del mundo con el juego cerrado. Un fallo no impide abrir el mundo.
    /// </summary>
    public static void Backup(string id) {
        try {
            var dir = Path.Combine(Root, id);
            if (!HasSave(dir)) {
                return;
            }

            var backups = Path.Combine(dir, BackupFolder);
            var target = Path.Combine(backups, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(dir, "*.json")) {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }

            var players = Path.Combine(dir, "players");
            if (Directory.Exists(players)) {
                var playersTarget = Path.Combine(target, "players");
                Directory.CreateDirectory(playersTarget);
                foreach (var file in Directory.GetFiles(players, "*.json")) {
                    File.Copy(file, Path.Combine(playersTarget, Path.GetFileName(file)), true);
                }
            }

            // Solo las últimas: los nombres son la fecha, así que el orden alfabético es el del tiempo
            var old = Directory.GetDirectories(backups).OrderBy(d => d, StringComparer.Ordinal).ToList();
            for (var i = 0; i < old.Count - MaxBackups; i++) {
                Directory.Delete(old[i], true);
            }

            Log.Info($"Copia de seguridad del mundo en {BackupFolder}/{Path.GetFileName(target)}");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Log.Warn($"No se pudo hacer la copia de seguridad del mundo: {e.Message}");
        }
    }

    public static void Delete(string id) {
        var dir = Path.Combine(Root, id);
        if (Directory.Exists(dir)) {
            Directory.Delete(dir, true);
            Log.Info($"Mundo coop borrado: {id}");
        }
    }

    /// <summary>Crea un mundo vacío (se empezará con partida nueva). Null si ya hay el máximo.</summary>
    public static CoopWorldInfo Create(string name, CoopWorldMode mode = CoopWorldMode.Classic) {
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
        var info = new CoopWorldInfo { Id = id, Name = name, LastSaved = DateTime.UtcNow, IsNew = true, Mode = mode };
        WriteInfo(info);
        return info;
    }

    /// <summary>Lee la partida de un mundo (null si es nuevo o no se pudo leer).</summary>
    public static SaveGameData Load(string id) {
        var path = Path.Combine(Path.Combine(Root, id), WorldFile);
        foreach (var candidate in SafeFile.Candidates(path)) {
            if (!File.Exists(candidate)) {
                continue;
            }

            try {
                var data = JsonConvert.DeserializeObject<SaveGameData>(File.ReadAllText(candidate), SaveSettings);
                if (data?.playerData != null && data.sceneData != null) {
                    if (candidate != path) {
                        Log.Warn($"El mundo {id} estaba dañado: se cargó {Path.GetFileName(candidate)}");
                    }

                    return data;
                }

                Log.Warn($"El guardado {Path.GetFileName(candidate)} del mundo {id} está vacío");
            } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
                Log.Warn($"No se pudo leer {Path.GetFileName(candidate)} del mundo {id}: {e.Message}");
            }
        }

        return null;
    }

    /// <summary>Guarda la partida de un mundo (el guardado anterior queda como copia).</summary>
    public static void Save(CoopWorldInfo info, SaveGameData data) {
        var dir = Path.Combine(Root, info.Id);
        Directory.CreateDirectory(dir);
        SafeFile.Write(Path.Combine(dir, WorldFile), JsonConvert.SerializeObject(data, Formatting.None, SaveSettings));

        info.PlayTime = data.playerData.playTime;
        info.Area = data.playerData.mapZone.ToString();
        info.LastSaved = DateTime.UtcNow;
        info.IsNew = false;
        // Alma de Acero: el juego guarda la muerte de verdad con permadeathMode = 2 antes de llevar a su pantalla final
        if (data.playerData.permadeathMode == 2 && !info.Lost) {
            info.Lost = true;
            Log.Info($"Mundo de Alma de Acero perdido: {info.Name}");
        }

        WriteInfo(info);
    }

    /// <summary>Personaje guardado de un jugador en este mundo (null si es la primera vez).</summary>
    public static Newtonsoft.Json.Linq.JObject LoadCharacter(string worldId, string playerKey) {
        var path = CharacterPath(worldId, playerKey);
        // El anterior (.bak) si el último no está o no se puede leer: el juego pudo cerrarse justo al guardarlo
        foreach (var candidate in SafeFile.Candidates(path)) {
            try {
                if (File.Exists(candidate)) {
                    var character = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(candidate));
                    if (candidate != path) {
                        Log.Warn($"El personaje {playerKey} estaba dañado o faltaba: se cargó {Path.GetFileName(candidate)}");
                    }

                    return character;
                }
            } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
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
        SafeFile.Write(path, character.ToString(Formatting.None));
    }

    /// <summary>La clave ya viene validada (solo hexadecimal): no puede salirse de la carpeta del mundo.</summary>
    private static string CharacterPath(string worldId, string playerKey) =>
        Path.Combine(Path.Combine(Path.Combine(Root, worldId), "players"), playerKey + ".json");

    /// <summary>Guarda los datos de la lista del mundo (p. ej. su código nuevo) sin tocar la partida.</summary>
    public static void UpdateInfo(CoopWorldInfo info) => WriteInfo(info);

    /// <summary>Como la partida, con copia: antes se escribía encima y un apagón la dejaba vacía (issue #19).</summary>
    private static void WriteInfo(CoopWorldInfo info) {
        SafeFile.Write(Path.Combine(Path.Combine(Root, info.Id), InfoFile),
            JsonConvert.SerializeObject(info, Formatting.Indented));
    }
}
