using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HKMultiplayer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HKMultiplayer.Game.World;

/// <summary>
/// Lo que es de cada jugador dentro de un mundo coop: vida y alma actuales, geo, banco y puntos de reaparición, sombra,
/// amuletos equipados, portal de sueño y estado de la interfaz. Todo lo demás de la partida (jefes, puertas,
/// habilidades, objetos, mapas, muescas...) es del mundo y lo decide el host.
/// </summary>
internal static class CharacterData {
    /// <summary>Campos de <see cref="PlayerData"/> que son del personaje.</summary>
    private static readonly string[] FieldNames = new[] {
        "playTime",
        // Vida y alma actuales (los máximos salen de objetos del mundo)
        "health", "healthBlue", "joniHealthBlue", "damagedBlue", "prevHealth",
        "MPCharge", "MPReserve", "focusMP_amount", "soulLimited",
        "geo", "geoPool",
        // Banco y reaparición
        "atBench", "respawnScene", "mapZone", "respawnMarkerName", "respawnType", "respawnFacingRight",
        "hazardRespawnLocation", "hazardRespawnFacingRight", "nextScene",
        // Sombra
        "shadeScene", "shadeMapZone", "shadePositionX", "shadePositionY", "shadeHealth", "shadeMP",
        "shadeFireballLevel", "shadeQuakeLevel", "shadeScreamLevel", "shadeSpecialType", "shadeMapPos",
        // Portal de sueño y vuelta de sueños/jefes
        "dreamGateScene", "dreamGateX", "dreamGateY", "dreamgateMapPos", "dreamReturnScene", "bossReturnEntryGate",
        // Amuletos equipados (los conseguidos son del mundo)
        "charmSlotsFilled", "equippedCharms", "overcharmed",
        // Interfaz y estado del momento
        "currentInvPane", "currentArea", "environmentType", "previousDarkness", "disablePause", "isInvincible",
        "gMap_doorX", "gMap_doorY", "gMap_doorScene", "gMap_doorMapZone", "gMap_doorOriginOffsetX",
        "gMap_doorOriginOffsetY", "gMap_doorSceneWidth", "gMap_doorSceneHeight"
    }.Concat(Enumerable.Range(1, 40).Select(i => $"equippedCharm_{i}")).ToArray();

    /// <summary>Campos que se toman del host para un personaje nuevo: empieza donde reaparece el host.</summary>
    private static readonly string[] FromHostForNew = {
        "respawnScene", "mapZone", "respawnMarkerName", "respawnType", "respawnFacingRight", "nextScene",
        "currentArea", "environmentType"
    };

    private static readonly Dictionary<string, FieldInfo> Fields = BuildFields();

    private static JsonSerializer Serializer => JsonSerializer.Create(WorldStore.SaveSettings);

    private static Dictionary<string, FieldInfo> BuildFields() {
        var fields = new Dictionary<string, FieldInfo>();
        foreach (var name in FieldNames) {
            var field = typeof(PlayerData).GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) {
                Log.Warn($"PlayerData no tiene el campo {name} (se ignora)");
                continue;
            }

            fields[name] = field;
        }

        return fields;
    }

    /// <summary>Si un campo es del personaje (no se comparte).</summary>
    public static bool IsCharacterField(string name) => Fields.ContainsKey(name);

    /// <summary>Saca los datos del personaje de una partida.</summary>
    public static JObject Extract(PlayerData playerData) {
        var data = new JObject();
        var serializer = Serializer;
        foreach (var pair in Fields) {
            data[pair.Key] = JToken.FromObject(pair.Value.GetValue(playerData) ?? JValue.CreateNull(), serializer);
        }

        return data;
    }

    /// <summary>
    /// Personaje nuevo para este mundo: valores de una partida nueva, con la vida llena y empezando donde reaparece el
    /// host.
    /// </summary>
    public static JObject CreateNew(PlayerData host) {
        var fresh = (PlayerData) Activator.CreateInstance(typeof(PlayerData), true);
        fresh.Reset();
        var data = Extract(fresh);
        var serializer = Serializer;
        foreach (var name in FromHostForNew) {
            if (Fields.TryGetValue(name, out var field)) {
                data[name] = JToken.FromObject(field.GetValue(host) ?? JValue.CreateNull(), serializer);
            }
        }

        data["health"] = host.maxHealth;
        return data;
    }

    /// <summary>
    /// Pone los datos de un personaje en el JSON de una partida (solo los campos conocidos y con el tipo correcto: lo
    /// que llega de otro PC no puede tocar nada más).
    /// </summary>
    public static void ApplyTo(JObject playerDataJson, JObject character) {
        var serializer = Serializer;
        foreach (var property in character.Properties()) {
            if (!Fields.TryGetValue(property.Name, out var field)) {
                continue;
            }

            try {
                // Se comprueba que el valor se puede leer con el tipo del campo antes de copiarlo
                property.Value.ToObject(field.FieldType, serializer);
                playerDataJson[property.Name] = property.Value.DeepClone();
            } catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidCastException) {
                Log.Warn($"Dato de personaje no válido en {property.Name}: se ignora");
            }
        }
    }
}
