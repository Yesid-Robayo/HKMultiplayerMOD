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
/// máscaras, vasijas, aguijón, muescas y amuletos conseguidos. Pensado para mundos que empiezan desde una partida avanzada
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

    /// <summary>Lo que se iguala tomando lo que tenga cualquiera de los dos.</summary>
    private static readonly string[] BoolFields = BuildBools();

    /// <summary>Lo que se iguala tomando el mayor.</summary>
    private static readonly string[] IntFields = {
        "fireballLevel", "quakeLevel", "screamLevel", "maxHealthBase", "heartPieces", "MPReserveMax", "vesselFragments",
        "nailSmithUpgrades", "nailDamage", "charmSlots", "charmsOwned"
    };

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
    public string Status { get; private set; } = string.Empty;

    /// <summary>Hay un resumen esperando confirmación.</summary>
    public bool WaitingConfirm => _pending != null && Time.unscaledTime < _pendingUntil;

    /// <summary>Hay una copia de antes del cambio en este mundo, que se puede devolver.</summary>
    public bool CanUndo => File.Exists(BackupPath());

    public void Attach(GameSession session) {
        _session = session;
        session.On(LevelRequest, _onRequest);
        session.On(LevelData, _onData);
        _pending = null;
        _waiting = false;
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

        writer.WriteByte((byte) IntFields.Length);
        foreach (var field in IntFields) {
            writer.WriteByte((byte) Mathf.Clamp(pd.GetInt(field), 0, 255));
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
        var ints = new byte[intCount];
        for (var i = 0; i < intCount; i++) {
            ints[i] = reader.ReadByte();
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
            if (ints[i] <= 100) {
                clean[IntFields[i]] = (int) ints[i];
            }
        }

        var summary = Summarize(clean);
        if (summary.Length == 0) {
            Status = Lang.T("Ya tienes todo lo que tiene el host", "You already have everything the host has");
            return;
        }

        _pending = clean;
        _pendingUntil = Time.unscaledTime + ConfirmSeconds;
        Status = Lang.T($"Ganarás: {summary}. Pulsa «Confirmar» para aplicarlo.", $"You'll get: {summary}. Press «Confirm» to apply it.");
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
        foreach (var field in BoolFields) {
            if (host[field] is JValue value && (bool) value && !pd.GetBool(field)) {
                if (field.StartsWith("gotCharm_", StringComparison.Ordinal)) {
                    charms++;
                } else {
                    abilities++;
                }
            }
        }

        foreach (var field in new[] { "fireballLevel", "quakeLevel", "screamLevel" }) {
            if (host[field] is JValue value && (int) value > pd.GetInt(field)) {
                abilities++;
            }
        }

        if (charms > 0) {
            Add(charms == 1 ? Lang.T("1 amuleto", "1 charm") : Lang.T($"{charms} amuletos", $"{charms} charms"));
        }

        if (abilities > 0) {
            Add(abilities == 1 ? Lang.T("1 habilidad", "1 ability") : Lang.T($"{abilities} habilidades", $"{abilities} abilities"));
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

        foreach (var field in IntFields) {
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

            foreach (var field in IntFields) {
                if (backup[field] is JValue { Type: JTokenType.Integer } value) {
                    pd.SetInt(field, (int) value);
                }
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

    private static void SaveBackup(PlayerData pd) {
        var backup = new JObject();
        foreach (var field in BoolFields) {
            backup[field] = pd.GetBool(field);
        }

        foreach (var field in IntFields) {
            backup[field] = pd.GetInt(field);
        }

        backup["maxHealth"] = pd.maxHealth;
        var path = BackupPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        SafeFile.Write(path, backup.ToString());
    }

    /// <summary>La copia de antes del cambio, por mundo (el del host en el que se juega).</summary>
    private static string BackupPath() {
        var world = World.CoopSaves.ActiveWorld?.Id;
        var name = string.IsNullOrEmpty(world) ? "mundo" : world;
        foreach (var c in Path.GetInvalidFileNameChars()) {
            name = name.Replace(c, '-');
        }

        return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "HKMultiplayer"), "Personajes"),
            $"{name}.json");
    }
}
