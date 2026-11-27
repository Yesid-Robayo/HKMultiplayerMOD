using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// «Ponerme al nivel del host»: un invitado, sentado en un banco, puede igualar su personaje al del host en habilidades,
/// máscaras, vasijas, aguijón, muescas y amuletos conseguidos, y desde la 0.45.0 también en mapas, pluma, alfileres y
/// marcadores, llaves y objetos (farol, pase del tranvía...), amuletos frágiles irrompibles, el Alma de Rey o el Corazón del
/// Vacío y la esencia. Pensado para mundos que empiezan desde una partida avanzada
/// del host, donde un invitado nuevo empieza de cero. Nadie pierde nada: de cada número se queda el mayor y de cada
/// habilidad o amuleto, el que tenga cualquiera de los dos.
/// <para>
/// Primero se pide al host lo suyo y se enseña un resumen de lo que cambia, que hay que confirmar. Antes de aplicarlo se
/// guarda en este PC una copia de esos datos del personaje, y «Deshacer» la devuelve. Lo equipado no se toca.
/// </para>
/// </summary>
internal sealed class MatchHostLevel {
    /// <summary>Un invitado pide el nivel del host (→ el host).</summary>
    public const MessageType LevelRequest = (MessageType) 125;

    /// <summary>El host responde con sus datos de nivel (→ quien los pidió), en JSON.</summary>
    public const MessageType LevelData = (MessageType) 126;

    /// <summary>Tiempo para confirmar tras ver el resumen.</summary>
    private const float ConfirmSeconds = 20f;

    /// <summary>
    /// Lo que se iguala tomando el mayor, con el máximo que se acepta de cada uno (lo que llega de la red se comprueba).
    /// </summary>
    private static readonly (string Field, int Max)[] IntFields = {
        ("fireballLevel", 2), ("quakeLevel", 2), ("screamLevel", 2), ("maxHealthBase", 9), ("heartPieces", 4),
        ("MPReserveMax", 99), ("vesselFragments", 3), ("nailSmithUpgrades", 4), ("nailDamage", 21), ("charmSlots", 11),
        ("charmsOwned", 40),
        // Desde la 0.45.0, «todo»: llaves, el Alma de Rey o el Corazón del Vacío (1-4), la esencia y los marcadores
        // comprados para el mapa
        ("simpleKeys", 4), ("royalCharmState", 4), ("dreamOrbs", 2400), ("spareMarkers_r", 6), ("spareMarkers_b", 6),
        ("spareMarkers_y", 6), ("spareMarkers_w", 6)
    };

    /// <summary>Mapas, pluma, alfileres y marcadores (en este orden en el mensaje: va por posición).</summary>
    private static readonly string[] MapFields = {
        "hasMap", "mapDirtmouth", "mapCrossroads", "mapGreenpath", "mapFogCanyon", "mapRoyalGardens", "mapFungalWastes",
        "mapCity", "mapWaterways", "mapMines", "mapDeepnest", "mapCliffs", "mapOutskirts", "mapRestingGrounds", "mapAbyss",
        "hasQuill", "hasPin", "hasPinBench", "hasPinCocoon", "hasPinDreamPlant", "hasPinGuardian", "hasPinBlackEgg",
        "hasPinShop", "hasPinSpa", "hasPinStag", "hasPinTram", "hasPinGhost", "hasPinGrub", "hasMarker", "hasMarker_r",
        "hasMarker_b", "hasMarker_y", "hasMarker_w"
    };

    /// <summary>Llaves, pases y objetos que abren el mundo o lo dejan ver (para el resumen).</summary>
    private static readonly string[] ItemFields = {
        "hasLantern", "hasTramPass", "hasCityKey", "hasWhiteKey", "hasLoveKey", "hasSpaKey", "hasWaterwaysKey",
        "hasHuntersMark", "salubraBlessing", "fragileHealth_unbreakable", "fragileGreed_unbreakable",
        "fragileStrength_unbreakable", "gotShadeCharm"
    };

    /// <summary>
    /// Lo que se iguala tomando lo que tenga cualquiera de los dos. Detrás de <see cref="MapFields"/> e
    /// <see cref="ItemFields"/>: los campos estáticos se preparan en el orden en que están escritos, y antes de ellos
    /// BuildBools los encontraba vacíos (el host no podía abrir su mundo).
    /// </summary>
    private static readonly string[] BoolFields = BuildBools();

    private static string[] BuildBools() {
        var fields = new List<string> {
            "hasDash", "canDash", "canBackDash", "hasShadowDash", "canShadowDash", "hasWalljump", "canWallJump",
            "hasDoubleJump", "hasSuperDash", "canSuperDash", "hasAcidArmour", "hasKingsBrand", "hasDreamNail",
            "dreamNailUpgraded", "hasDreamGate", "hasSpell", "hasNailArt", "hasCyclone", "hasDashSlash", "hasUpwardSlash",
            "hasAllNailArts", "hasCharm", "canOvercharm", "honedNail"
        };
        for (var i = 1; i <= 40; i++) {
            fields.Add($"gotCharm_{i}");
        }

        fields.AddRange(MapFields);
        fields.AddRange(ItemFields);
        return fields.ToArray();
    }

    private readonly MessageHandler _onRequest;
    private readonly MessageHandler _onData;
    private GameSession _session;
    private JObject _pending;
    private float _pendingUntil;
    private bool _waiting;

    static MatchHostLevel() {
        MessageRoutes.Register(LevelRequest, Route.Server, reliable: true);
        MessageRoutes.Register(LevelData, Route.Server, reliable: true);
    }

    public MatchHostLevel() {
        _onRequest = OnRequest;
        _onData = OnData;
    }

    /// <summary>Lo último que pasó, para el panel (resumen, «hecho», o por qué no se pudo).</summary>
    public string Status {
        get => _status;
        private set {
            // Cada paso queda en el registro (el panel solo lo enseña mientras está abierto)
            if (value != _status && !string.IsNullOrEmpty(value)) {
                Log.Info($"Ponerse al nivel del host: {value}");
            }

            _status = value ?? string.Empty;
        }
    }

    private string _status = string.Empty;

    /// <summary>Hay un resumen esperando confirmación.</summary>
    public bool WaitingConfirm => _pending != null && Time.unscaledTime < _pendingUntil;

    /// <summary>Hay una copia de antes del cambio en este mundo, que se puede devolver.</summary>
    public bool CanUndo => File.Exists(BackupPath());

    /// <summary>Ya se avisó en esta partida de que existe la opción (sale una vez, al entrar con un personaje nuevo).</summary>
    private bool _noticeShown;

    private float _noticeAt = -1f;

    /// <summary>Espera tras poder jugar antes del aviso (que no se pierda entre los avisos de entrada).</summary>
    private const float NoticeDelay = 4f;

    /// <summary>
    /// Cada frame: la primera vez que un invitado entra con un personaje nuevo (y el host lo permite), un aviso de que
    /// puede ponerse a su nivel desde un banco, para que no se quede sin saberlo.
    /// </summary>
    public void Update() {
        if (_noticeShown || _session == null || _session.IsHost || !World.CoopSaves.GuestIsNew ||
            World.CoopSaves.IntroPlaying || !HostSettings.Current.AllowMatchHost) {
            return;
        }

        var hero = HeroController.instance;
        if (hero == null || hero.cState.transitioning || GameManager.instance == null || !GameManager.instance.IsGameplayScene()) {
            _noticeAt = -1f;
            return;
        }

        if (_noticeAt < 0f) {
            _noticeAt = Time.unscaledTime + NoticeDelay;
            return;
        }

        if (Time.unscaledTime < _noticeAt) {
            return;
        }

        _noticeShown = true;
        Log.Info("Aviso al invitado nuevo: puede ponerse al nivel del host desde un banco");
        Notice.Show(Lang.T("Puedes ponerte al nivel del host desde un banco: Pausa › Multijugador › Compañeros",
            "You can match the host's level from a bench: Pause › Multiplayer › Partners"), 10f);
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(LevelRequest, _onRequest);
        session.On(LevelData, _onData);
        _pending = null;
        _waiting = false;
        _noticeShown = false;
        _noticeAt = -1f;
        Status = string.Empty;
    }

    public void Detach() {
        _session?.On(LevelRequest, null);
        _session?.On(LevelData, null);
        _session = null;
        _pending = null;
    }

    private static bool Sitting() {
        var pd = PlayerData.instance;
        var hero = HeroController.instance;
        return pd != null && hero != null && pd.atBench && hero.controlReqlinquished;
    }

    /// <summary>Primer paso: pide al host su nivel (solo un invitado sentado en un banco).</summary>
    public void Request() {
        if (_session == null || _session.IsHost) {
            Status = Lang.T("Solo los invitados se ponen al nivel del host", "Only guests can match the host's level");
            return;
        }

        if (!HostSettings.Current.AllowMatchHost) {
            Status = Lang.T("El host no lo permite en este mundo", "The host doesn't allow it in this world");
            return;
        }

        if (!Sitting()) {
            Status = Lang.T("Siéntate en un banco para cambiar tu personaje", "Sit on a bench to change your character");
            Log.Info($"Ponerse al nivel del host: no está sentado (atBench={PlayerData.instance?.atBench}, " +
                     $"control cedido={HeroController.instance?.controlReqlinquished})");
            return;
        }

        _waiting = true;
        _pending = null;
        Status = Lang.T("Pidiendo al host su nivel…", "Asking the host for their level…");
        _session.Begin(LevelRequest);
        _session.End();
    }

    private void OnRequest(SessionPlayer from, PacketReader reader) {
        var pd = PlayerData.instance;
        if (_session == null || !_session.IsHost || pd == null || from.IsLocal) {
            return;
        }

        // Apagado por el host: no se responde (el invitado ya no ve el botón; esto es por si llega igual)
        if (!HostSettings.Current.AllowMatchHost) {
            Log.Info($"{from.Name} pide el nivel del host, pero el host no lo permite");
            return;
        }

        // En binario y en el orden de las listas (los dos PCs tienen la misma versión): en JSON, con los nombres, pasaba
        // del tamaño máximo de un mensaje y no llegaba
        Log.Info($"{from.Name} pide el nivel del host para ponerse al día");
        var writer = _session.BeginTo(from, LevelData);
        writer.WriteByte((byte) BoolFields.Length);
        for (var i = 0; i < BoolFields.Length; i += 8) {
            var bits = 0;
            for (var b = 0; b < 8 && i + b < BoolFields.Length; b++) {
                bits |= pd.GetBool(BoolFields[i + b]) ? 1 << b : 0;
            }

            writer.WriteByte((byte) bits);
        }

        // Enteros completos: la esencia pasa de 255
        writer.WriteByte((byte) IntFields.Length);
        foreach (var (field, max) in IntFields) {
            writer.WriteInt(Mathf.Clamp(pd.GetInt(field), 0, max));
        }

        _session.EndTo();
    }

    private void OnData(SessionPlayer from, PacketReader reader) {
        var boolCount = reader.ReadByte();
        var bits = new byte[(boolCount + 7) / 8];
        for (var i = 0; i < bits.Length; i++) {
            bits[i] = reader.ReadByte();
        }

        var intCount = reader.ReadByte();
        var ints = new int[intCount];
        for (var i = 0; i < intCount; i++) {
            ints[i] = reader.ReadInt();
        }

        if (_session == null || _session.IsHost || from.Id != GameSession.HostPlayerId || !_waiting) {
            return;
        }

        _waiting = false;
        if (boolCount != BoolFields.Length || intCount != IntFields.Length) {
            Status = Lang.T("El nivel del host no llegó bien", "The host's level didn't arrive correctly");
            return;
        }

        var clean = new JObject();
        for (var i = 0; i < BoolFields.Length; i++) {
            clean[BoolFields[i]] = (bits[i / 8] & (1 << (i % 8))) != 0;
        }

        // Los números, dentro de lo que permite el juego
        for (var i = 0; i < IntFields.Length; i++) {
            if (ints[i] >= 0 && ints[i] <= IntFields[i].Max) {
                clean[IntFields[i].Field] = ints[i];
            }
        }

        var summary = Summarize(clean);
        if (summary.Length == 0) {
            Status = Lang.T("Ya tienes todo lo que tiene el host", "You already have everything the host has");
            return;
        }

        _pending = clean;
        _pendingUntil = Time.unscaledTime + ConfirmSeconds;
        Status = Lang.F("Ganarás: {0}. Pulsa «Confirmar» para aplicarlo.", "You'll get: {0}. Press «Confirm» to apply it.", summary);
    }

    /// <summary>Lo que ganaría el personaje local con los datos del host (vacío si nada).</summary>
    private static string Summarize(JObject host) {
        var pd = PlayerData.instance;
        var text = new StringBuilder();
        void Add(string part) {
            if (text.Length > 0) {
                text.Append(", ");
            }

            text.Append(part);
        }

        Gain("maxHealthBase", 1, Lang.T("máscara", "mask"), Lang.T("máscaras", "masks"));
        // La reserva va de 33 en 33: cada 33 es una vasija de alma
        Gain("MPReserveMax", 33, Lang.T("vasija de alma", "soul vessel"), Lang.T("vasijas de alma", "soul vessels"));
        Gain("nailSmithUpgrades", 1, Lang.T("mejora del aguijón", "nail upgrade"), Lang.T("mejoras del aguijón", "nail upgrades"));
        Gain("charmSlots", 1, Lang.T("muesca", "notch"), Lang.T("muescas", "notches"));
        var charms = 0;
        var abilities = 0;
        var maps = 0;
        var items = 0;
        foreach (var field in BoolFields) {
            if (host[field] is JValue value && (bool) value && !pd.GetBool(field)) {
                if (field.StartsWith("gotCharm_", StringComparison.Ordinal)) {
                    charms++;
                } else if (Array.IndexOf(MapFields, field) >= 0) {
                    maps++;
                } else if (Array.IndexOf(ItemFields, field) >= 0) {
                    items++;
                } else {
                    abilities++;
                }
            }
        }

        foreach (var field in new[] { "simpleKeys", "royalCharmState", "spareMarkers_r", "spareMarkers_b", "spareMarkers_y", "spareMarkers_w" }) {
            if (host[field] is JValue value && (int) value > pd.GetInt(field)) {
                items++;
            }
        }

        if (host["dreamOrbs"] is JValue orbs && (int) orbs > pd.GetInt("dreamOrbs")) {
            Add(Lang.F("+{0} de esencia", "+{0} essence", (int) orbs - pd.GetInt("dreamOrbs")));
        }

        foreach (var field in new[] { "fireballLevel", "quakeLevel", "screamLevel" }) {
            if (host[field] is JValue value && (int) value > pd.GetInt(field)) {
                abilities++;
            }
        }

        if (charms > 0) {
            Add(charms == 1 ? Lang.T("1 amuleto", "1 charm") : Lang.F("{0} amuletos", "{0} charms", charms));
        }

        if (abilities > 0) {
            Add(abilities == 1 ? Lang.T("1 habilidad", "1 ability") : Lang.F("{0} habilidades", "{0} abilities", abilities));
        }

        if (maps > 0) {
            Add(maps == 1 ? Lang.T("1 mapa o alfiler", "1 map or pin") : Lang.F("{0} mapas y alfileres", "{0} maps and pins", maps));
        }

        if (items > 0) {
            Add(items == 1 ? Lang.T("1 objeto", "1 item") : Lang.F("{0} objetos", "{0} items", items));
        }

        return text.ToString();

        void Gain(string field, int unit, string one, string many) {
            if (host[field] is JValue value && (int) value > pd.GetInt(field)) {
                var count = Mathf.Max(1, ((int) value - pd.GetInt(field)) / unit);
                Add($"+{count} {(count == 1 ? one : many)}");
            }
        }
    }

    /// <summary>Segundo paso: aplica lo del resumen (guardando antes una copia para deshacerlo).</summary>
    public void Confirm() {
        var pd = PlayerData.instance;
        var hero = HeroController.instance;
        if (!HostSettings.Current.AllowMatchHost) {
            _pending = null;
            Status = Lang.T("El host no lo permite en este mundo", "The host doesn't allow it in this world");
            return;
        }

        if (!WaitingConfirm || pd == null || hero == null) {
            Status = Lang.T("Vuelve a pedirlo: el resumen caducó", "Ask again: the summary expired");
            _pending = null;
            return;
        }

        if (!Sitting()) {
            Status = Lang.T("Siéntate en un banco para cambiar tu personaje", "Sit on a bench to change your character");
            return;
        }

        var host = _pending;
        _pending = null;
        SaveBackup(pd);
        foreach (var field in BoolFields) {
            if (host[field] is JValue value && (bool) value && !pd.GetBool(field)) {
                pd.SetBool(field, true);
            }
        }

        foreach (var (field, _) in IntFields) {
            if (host[field] is not JValue value || (int) value <= pd.GetInt(field)) {
                continue;
            }

            // Las máscaras y las vasijas como el juego al conseguirlas (sus marcas internas y la vida llena)
            switch (field) {
                case "maxHealthBase":
                    pd.AddToMaxHealth((int) value - pd.maxHealthBase);
                    break;
                case "MPReserveMax":
                    pd.AddToMaxMPReserve((int) value - pd.MPReserveMax);
                    break;
                default:
                    pd.SetInt(field, (int) value);
                    break;
            }
        }

        // El Corazón del Vacío (estado 4) cuesta 0 muescas, como cuando lo da el juego
        if (pd.GetInt("royalCharmState") >= 4 && pd.GetInt("charmCost_36") != 0) {
            pd.SetInt("charmCost_36", 0);
        }

        hero.MaxHealth();
        GameManager.instance.SaveGame();
        Log.Info("Personaje puesto al nivel del host");
        Status = Lang.T("Hecho: tu personaje está al nivel del host. «Deshacer» lo devuelve como estaba.",
            "Done: your character matches the host's level. «Undo» puts it back as it was.");
    }

    /// <summary>Devuelve el personaje a como estaba antes del último cambio (la copia guardada en este PC).</summary>
    public void Undo() {
        var pd = PlayerData.instance;
        var hero = HeroController.instance;
        var path = BackupPath();
        if (pd == null || hero == null || !File.Exists(path)) {
            return;
        }

        if (!Sitting()) {
            Status = Lang.T("Siéntate en un banco para cambiar tu personaje", "Sit on a bench to change your character");
            return;
        }

        try {
            var backup = JObject.Parse(File.ReadAllText(path));
            foreach (var field in BoolFields) {
                if (backup[field] is JValue { Type: JTokenType.Boolean } value) {
                    pd.SetBool(field, (bool) value);
                }
            }

            foreach (var (field, _) in IntFields) {
                if (backup[field] is JValue { Type: JTokenType.Integer } value) {
                    pd.SetInt(field, (int) value);
                }
            }

            if (backup["charmCost_36"] is JValue { Type: JTokenType.Integer } royalCost) {
                pd.SetInt("charmCost_36", (int) royalCost);
            }

            if (backup["maxHealth"] is JValue { Type: JTokenType.Integer } maxHealth) {
                pd.SetInt("maxHealth", (int) maxHealth);
            }

            File.Delete(path);
            hero.MaxHealth();
            GameManager.instance.SaveGame();
            Log.Info("Personaje devuelto a como estaba antes de ponerse al nivel del host");
            Status = Lang.T("Tu personaje vuelve a estar como antes", "Your character is back as it was");
        } catch (Exception e) when (e is IOException or Newtonsoft.Json.JsonException) {
            Log.Warn($"No se pudo deshacer el cambio de personaje: {e.Message}");
            Status = Lang.T("No se pudo deshacer", "Couldn't undo");
        }
    }

    private string FindHostName() {
        foreach (var player in _session.Players) {
            if (player.Id == GameSession.HostPlayerId) {
                return player.Name;
            }
        }

        return string.Empty;
    }

    private void SaveBackup(PlayerData pd) {
        var backup = new JObject();
        foreach (var field in BoolFields) {
            backup[field] = pd.GetBool(field);
        }

        foreach (var (field, _) in IntFields) {
            backup[field] = pd.GetInt(field);
        }

        backup["charmCost_36"] = pd.GetInt("charmCost_36");
        backup["maxHealth"] = pd.maxHealth;
        var path = BackupPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        SafeFile.Write(path, backup.ToString());
    }

    /// <summary>
    /// La copia de antes del cambio, por mundo: el invitado no sabe el identificador del mundo del host, así que se usa el
    /// nombre del host y el código de la partida, que es fijo para cada mundo.
    /// </summary>
    private string BackupPath() {
        var host = _session?.Players != null ? FindHostName() : string.Empty;
        var name = $"{host}_{_session?.RoomCode}";
        foreach (var c in Path.GetInvalidFileNameChars()) {
            name = name.Replace(c, '-');
        }

        if (name.Trim('_', '-').Length == 0) {
            name = "mundo";
        }

        return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "HKMultiplayer"), "Personajes"),
            $"{name}.json");
    }
}
