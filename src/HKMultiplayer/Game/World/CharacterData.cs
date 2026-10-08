using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HKMultiplayer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HKMultiplayer.Game.World;

/// <summary>
/// Lo que es de cada jugador dentro de un mundo coop: su estado (vida, alma, banco, sombra) y su <b>inventario</b>
/// (máscaras y vasijas, aguijón y artes, amuletos, llaves, reliquias, mapas, esencia, compras), además de los objetos
/// de las salas que recoge cada uno por su cuenta (objetos brillantes, fragmentos, cofres, orbes de sueño...).
/// Lo demás es del mundo y se comparte: habilidades de movimiento y hechizos, jefes, puertas, palancas, paredes,
/// personajes y salas visitadas.
/// </summary>
internal static class CharacterData {
    /// <summary>Propiedad del JSON del personaje con sus objetos de sala individuales.</summary>
    private const string SceneItemsProperty = "_sceneItems";

    /// <summary>Máximo de objetos de sala individuales aceptados de otro PC.</summary>
    private const int MaxSceneItems = 4000;

    private static IEnumerable<string> Numbered(string prefix, int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(i => prefix + i);

    /// <summary>Campos de <see cref="PlayerData"/> que son del personaje.</summary>
    private static readonly string[] FieldNames = new[] {
            "playTime",
            // Estado del momento: vida y alma actuales, geo
            "health", "maxHealth", "healthBlue", "joniHealthBlue", "damagedBlue", "prevHealth",
            "MPCharge", "MPReserve", "focusMP_amount", "soulLimited", "geo", "geoPool",
            // Banco y reaparición
            "atBench", "respawnScene", "mapZone", "respawnMarkerName", "respawnType", "respawnFacingRight",
            "hazardRespawnLocation", "hazardRespawnFacingRight", "nextScene",
            // Sombra
            "shadeScene", "shadeMapZone", "shadePositionX", "shadePositionY", "shadeHealth", "shadeMP",
            "shadeFireballLevel", "shadeQuakeLevel", "shadeScreamLevel", "shadeSpecialType", "shadeMapPos",
            // Portal de sueño y vuelta de sueños/jefes
            "dreamGateScene", "dreamGateX", "dreamGateY", "dreamgateMapPos", "dreamReturnScene", "bossReturnEntryGate",
            // Interfaz
            "currentInvPane", "currentArea", "environmentType", "previousDarkness", "disablePause", "isInvincible",
            "gMap_doorX", "gMap_doorY", "gMap_doorScene", "gMap_doorMapZone", "gMap_doorOriginOffsetX",
            "gMap_doorOriginOffsetY", "gMap_doorSceneWidth", "gMap_doorSceneHeight",

            // ---- Inventario ----
            // Máscaras y vasijas
            "maxHealthBase", "heartPieces", "heartPieceCollected", "heartPieceMax", "MPReserveMax",
            "vesselFragments", "vesselFragmentCollected", "vesselFragmentMax", "vesselFragStagNest",
            // Aguijón y artes del aguijón
            "nailDamage", "nailSmithUpgrades", "honedNail", "nailsmithCliff",
            "hasNailArt", "hasCyclone", "hasDashSlash", "hasUpwardSlash", "hasAllNailArts",
            // Esencia y recompensas del sueño
            "dreamOrbs", "dreamOrbsSpent", "dreamReward5b", "completedRGDreamPlant", "mothDeparted",
            "scenesEncounteredDreamPlantC", "falseKnightOrbsCollected", "mageLordOrbsCollected",
            "infectedKnightOrbsCollected", "whiteDefenderOrbsCollected", "greyPrinceOrbsCollected",
            // Llaves y objetos
            "hasLantern", "hasTramPass", "hasQuill", "hasCityKey", "hasSlykey", "gaveSlykey", "hasWhiteKey",
            "usedWhiteKey", "hasWaterwaysKey", "hasSpaKey", "hasLoveKey", "hasXunFlower", "xunRewardGiven",
            "simpleKeys", "rancidEggs", "ore", "gotLurkerKey", "gotQueenFragment", "gotKingFragment",
            "hasHuntersMark", "hunterRewardOffered", "huntersMarkOffered",
            // Reliquias y ventas
            "foundGhostCoin", "jinnEggsSold", "tukEggPrice", "tukDungEgg", "paidLegEater",
            // Compras y muescas
            "slySimpleKey", "slyRancidEgg", "gotSlyCharm", "salubraBlessing", "bankerAccountPurchased",
            "bankerBalance", "notchShroomOgres", "notchFogCanyon", "gotGrimmNotch", "fountainGeo",
            "fountainVesselSummoned", "metGrimm",
            // Amuletos
            "charmSlots", "charmSlotsFilled", "hasCharm", "charmBenchMsg", "charmsOwned", "canOvercharm", "overcharmed",
            "equippedCharms", "fragileHealth_unbreakable", "fragileGreed_unbreakable", "fragileStrength_unbreakable",
            "royalCharmState", "gotShadeCharm", "gaveFragileHeart", "gaveFragileGreed", "gaveFragileStrength",
            "pooedFragileHeart", "pooedFragileGreed", "pooedFragileStrength",
            // Recompensas de larvas y coliseo
            "grubRewards", "finalGrubRewardCollected", "seenColosseumTitle", "colosseumBronzeOpened",
            "colosseumBronzeCompleted", "colosseumSilverOpened", "colosseumSilverCompleted", "colosseumGoldOpened",
            "colosseumGoldCompleted",
            // Mapas, alfileres y marcadores
            "hasMap", "mapDirtmouth", "mapCrossroads", "mapGreenpath", "mapFogCanyon", "mapRoyalGardens",
            "mapFungalWastes", "mapCity", "mapWaterways", "mapMines", "mapDeepnest", "mapCliffs", "mapOutskirts",
            "mapRestingGrounds", "mapAbyss", "hasPin", "hasPinBench", "hasPinCocoon", "hasPinDreamPlant",
            "hasPinGuardian", "hasPinBlackEgg", "hasPinShop", "hasPinSpa", "hasPinStag", "hasPinTram", "hasPinGhost",
            "hasPinGrub", "hasMarker", "hasMarker_r", "hasMarker_b", "hasMarker_y", "hasMarker_w", "spareMarkers_r",
            "spareMarkers_b", "spareMarkers_y", "spareMarkers_w", "placedMarkers_r", "placedMarkers_b",
            "placedMarkers_y", "placedMarkers_w"
        }
        .Concat(Numbered("equippedCharm_", 1, 40))
        .Concat(Numbered("gotCharm_", 1, 40))
        .Concat(Numbered("newCharm_", 1, 40))
        .Concat(Numbered("dreamReward", 1, 9))
        .Concat(Numbered("trinket", 1, 4)).Concat(Numbered("foundTrinket", 1, 4))
        .Concat(Numbered("noTrinket", 1, 4)).Concat(Numbered("soldTrinket", 1, 4))
        .Concat(Numbered("slyShellFrag", 1, 4)).Concat(Numbered("slyVesselFrag", 1, 2))
        .Concat(Numbered("salubraNotch", 1, 4))
        .ToArray();

    /// <summary>
    /// Objetos de sala que recoge cada jugador por su cuenta (por el principio de su nombre en el juego): lo que
    /// recoge uno no desaparece para el otro. También los avisos de tutorial, que cada uno ve una vez.
    /// </summary>
    private static readonly string[] IndividualItemPrefixes = {
        "Shiny", "Heart Piece", "Vessel Fragment", "Chest", "Mantis Chest", "Grimm Chest", "Dream Plant", "Egg Sac",
        "Reward", "Reminder", "Interact Reminder", "Death Respawn Trigger", "Initial Fall Impact", "Force Hard Landing",
        "Secret Sound Region", "Poo "
    };

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

    /// <summary>Si un objeto de sala lo recoge cada jugador por su cuenta (no se comparte).</summary>
    public static bool IsIndividualItem(string id) {
        if (string.IsNullOrEmpty(id)) {
            return false;
        }

        foreach (var prefix in IndividualItemPrefixes) {
            if (id.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Saca los datos del personaje de una partida (campos e objetos de sala individuales).</summary>
    public static JObject Extract(PlayerData playerData, SceneData sceneData) {
        var data = new JObject();
        var serializer = Serializer;
        foreach (var pair in Fields) {
            data[pair.Key] = JToken.FromObject(pair.Value.GetValue(playerData) ?? JValue.CreateNull(), serializer);
        }

        var items = new JArray();
        if (sceneData != null) {
            foreach (var item in sceneData.persistentBoolItems) {
                if (IsIndividualItem(item.id) && !item.semiPersistent) {
                    items.Add(ItemJson(item.sceneName, item.id, item.activated));
                }
            }
        }

        data[SceneItemsProperty] = items;
        return data;
    }

    /// <summary>
    /// Personaje nuevo para este mundo: inventario y estado de una partida nueva, con la vida llena y empezando donde
    /// reaparece el host.
    /// </summary>
    public static JObject CreateNew(PlayerData host) {
        var fresh = (PlayerData) Activator.CreateInstance(typeof(PlayerData), true);
        fresh.Reset();
        var data = Extract(fresh, null);
        var serializer = Serializer;
        foreach (var name in FromHostForNew) {
            if (Fields.TryGetValue(name, out var field)) {
                data[name] = JToken.FromObject(field.GetValue(host) ?? JValue.CreateNull(), serializer);
            }
        }

        data["health"] = fresh.maxHealthBase;
        data["maxHealth"] = fresh.maxHealthBase;
        return data;
    }

    /// <summary>
    /// Deja solo lo válido de un personaje recibido de otro PC: campos conocidos con su tipo y objetos de sala
    /// individuales (con límites). Lo demás se descarta.
    /// </summary>
    public static JObject Sanitize(JObject character) {
        var clean = new JObject();
        ApplyFields(clean, character);
        var items = new JArray();
        if (character[SceneItemsProperty] is JArray received) {
            foreach (var token in received.Take(MaxSceneItems)) {
                if (token is not JObject item) {
                    continue;
                }

                var scene = (string) item["sceneName"];
                var id = (string) item["id"];
                if (scene is { Length: > 0 and <= 128 } && id is { Length: <= 256 } && IsIndividualItem(id)) {
                    items.Add(ItemJson(scene, id, item["activated"]?.Type == JTokenType.Boolean && (bool) item["activated"]));
                }
            }
        }

        clean[SceneItemsProperty] = items;
        return clean;
    }

    /// <summary>
    /// Pone un personaje en el JSON de una partida completa (<c>playerData</c> y <c>sceneData</c>): sus campos y sus
    /// objetos de sala individuales en lugar de los de quien guardó la partida. Lo que falte se toma de
    /// <paramref name="defaults"/> (p. ej. personajes guardados con una versión anterior).
    /// </summary>
    public static void ApplyToWorld(JObject worldJson, JObject character, JObject defaults) {
        var playerJson = (JObject) worldJson["playerData"];
        ApplyFields(playerJson, defaults);
        ApplyFields(playerJson, character);

        if (worldJson["sceneData"]?["persistentBoolItems"] is not JArray sceneItems) {
            return;
        }

        for (var i = sceneItems.Count - 1; i >= 0; i--) {
            if (IsIndividualItem((string) sceneItems[i]["id"])) {
                sceneItems.RemoveAt(i);
            }
        }

        if (character[SceneItemsProperty] is JArray items) {
            foreach (var item in items) {
                var entry = (JObject) item.DeepClone();
                entry["semiPersistent"] = false;
                sceneItems.Add(entry);
            }
        }
    }

    private static void ApplyFields(JObject target, JObject source) {
        var serializer = Serializer;
        foreach (var property in source.Properties()) {
            if (!Fields.TryGetValue(property.Name, out var field)) {
                continue;
            }

            try {
                // Se comprueba que el valor se puede leer con el tipo del campo antes de copiarlo
                property.Value.ToObject(field.FieldType, serializer);
                target[property.Name] = property.Value.DeepClone();
            } catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidCastException) {
                Log.Warn($"Dato de personaje no válido en {property.Name}: se ignora");
            }
        }
    }

    private static JObject ItemJson(string scene, string id, bool activated) =>
        new() { ["sceneName"] = scene, ["id"] = id, ["activated"] = activated };
}
