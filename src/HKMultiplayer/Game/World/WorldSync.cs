using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HKMultiplayer.Game.World;

/// <summary>
/// El mundo coop entre el host y los demás:
/// <list type="bullet">
/// <item>Al unirse, cada jugador dice su clave de personaje; el host le envía el mundo (su partida, comprimida) con el
/// personaje de ese jugador dentro, o uno nuevo si es la primera vez, y el jugador lo carga.</item>
/// <item>Los demás no guardan nada en su PC: en los bancos, al salir y cada minuto envían su personaje al host, que lo
/// guarda en el mundo (<c>players/&lt;clave&gt;.json</c>).</item>
/// <item>El host guarda el mundo solo cada 2 minutos (y cuando alguien entra o sale) en un momento tranquilo.</item>
/// </list>
/// </summary>
internal sealed class WorldSync {
    /// <summary>Clave de personaje del jugador (cliente → host).</summary>
    public const MessageType Identify = (MessageType) 40;

    private const byte LargeWorld = 1;
    private const byte LargeCharacter = 2;

    /// <summary>Tamaño máximo aceptado de un personaje recibido (comprimido).</summary>
    private const int MaxCharacterBytes = 256 * 1024;

    private const float HostAutosaveSeconds = 120f;
    private const float GuestAutosaveSeconds = 60f;

    private GameSession _session;
    private readonly Dictionary<ushort, string> _keys = new();
    private float _nextAutosave;
    private bool _saveSoon;

    /// <summary>Estado para el menú de unirse.</summary>
    public string JoinStatus { get; private set; } = string.Empty;

    static WorldSync() {
        MessageRoutes.Register(Identify, Route.Server, reliable: true);
    }

    public static string NewKey() {
        var bytes = new byte[16];
        using (var random = RandomNumberGenerator.Create()) {
            random.GetBytes(bytes);
        }

        var text = new StringBuilder(32);
        foreach (var b in bytes) {
            text.Append(b.ToString("x2"));
        }

        return text.ToString();
    }

    /// <summary>Solo 32 caracteres hexadecimales: se usa en nombres de archivo, nada más puede entrar.</summary>
    public static bool IsValidKey(string key) {
        if (key == null || key.Length != 32) {
            return false;
        }

        foreach (var c in key) {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) {
                return false;
            }
        }

        return true;
    }

    public void Attach(GameSession session) {
        _session = session;
        _keys.Clear();
        session.On(Identify, OnIdentify);
        session.LargeMessage += OnLarge;
        session.Connected += OnConnected;
        session.PlayerLeft += OnPlayerLeft;
        CoopSaves.GuestSaving += OnGuestSaving;
        _nextAutosave = Time.unscaledTime + (session.IsHost ? HostAutosaveSeconds : GuestAutosaveSeconds);
        JoinStatus = string.Empty;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(Identify, null);
            _session.LargeMessage -= OnLarge;
            _session.Connected -= OnConnected;
            _session.PlayerLeft -= OnPlayerLeft;
        }

        CoopSaves.GuestSaving -= OnGuestSaving;
        _session = null;
        _keys.Clear();
    }

    // =====================================================================================================
    // Cliente
    // =====================================================================================================

    private void OnConnected() {
        var writer = _session.Begin(Identify);
        writer.WriteString(HKMultiplayerMod.Settings.PlayerKey);
        _session.End();
        JoinStatus = "Conectado. Recibiendo el mundo del host...";
    }

    private void ReceiveWorld(byte[] data) {
        if (!CoopSaves.CanStart) {
            Log.Warn("Llegó el mundo del host pero no estamos en el menú principal: se ignora");
            return;
        }

        SaveGameData world;
        try {
            world = JsonConvert.DeserializeObject<SaveGameData>(Decompress(data, MaxWorldChars), WorldStore.SaveSettings);
        } catch (Exception e) when (e is JsonException or InvalidDataException) {
            Log.Error($"El mundo recibido no es válido: {e.Message}");
            JoinStatus = "El mundo recibido no es válido";
            return;
        }

        if (world?.playerData == null || world.sceneData == null) {
            JoinStatus = "El mundo recibido no es válido";
            return;
        }

        Log.Info($"Mundo del host recibido ({data.Length / 1024} KB)");
        JoinStatus = "Entrando en el mundo...";
        CoopSaves.StartGuest(world);
    }

    /// <summary>El juego del cliente guardó (banco, salir, automático): se envía el personaje al host.</summary>
    private void OnGuestSaving(PlayerData playerData) {
        if (_session == null || _session.IsHost || !_session.IsActive) {
            return;
        }

        var json = CharacterData.Extract(playerData).ToString(Formatting.None);
        _session.SendLargeToHost(LargeCharacter, Compress(json));
        Log.Info("Personaje enviado al host");
    }

    // =====================================================================================================
    // Host
    // =====================================================================================================

    private void OnIdentify(SessionPlayer from, PacketReader reader) {
        var key = reader.ReadString(64);
        if (!IsValidKey(key)) {
            Log.Warn($"{from.Name} envió una clave de personaje no válida");
            return;
        }

        _keys[from.Id] = key;
        var world = CoopSaves.ActiveWorld;
        if (world == null || !CoopSaves.IsHostWorld) {
            Log.Info($"{from.Name} se unió, pero no hay un mundo coop abierto");
            return;
        }

        SendWorld(from, world, key);
        _saveSoon = true;
    }

    private void SendWorld(SessionPlayer target, CoopWorldInfo world, string key) {
        var gm = GameManager.instance;
        // Lo que hay en la sala actual pasa a los datos de las salas antes de copiarlos
        gm.SaveLevelState();
        var json = JObject.Parse(JsonConvert.SerializeObject(new SaveGameData(gm.playerData, gm.sceneData),
            Formatting.None, WorldStore.SaveSettings));
        var playerJson = (JObject) json["playerData"];
        var character = WorldStore.LoadCharacter(world.Id, key);
        CharacterData.ApplyTo(playerJson, character ?? CharacterData.CreateNew(gm.playerData));
        var data = Compress(json.ToString(Formatting.None));
        _session.SendLargeTo(target, LargeWorld, data);
        Log.Info($"Mundo enviado a {target.Name} ({data.Length / 1024} KB, personaje {(character != null ? "guardado" : "nuevo")})");
    }

    private void ReceiveCharacter(SessionPlayer from, byte[] data) {
        var world = CoopSaves.ActiveWorld;
        if (from == null || world == null || !CoopSaves.IsHostWorld || !_keys.TryGetValue(from.Id, out var key)) {
            return;
        }

        if (data.Length > MaxCharacterBytes) {
            Log.Warn($"Personaje de {from.Name} demasiado grande: se ignora");
            return;
        }

        try {
            var character = JObject.Parse(Decompress(data, MaxCharacterChars));
            // Solo se guardan los campos de personaje conocidos y válidos
            var clean = new JObject();
            CharacterData.ApplyTo(clean, character);
            WorldStore.SaveCharacter(world.Id, key, clean);
            Log.Info($"Personaje de {from.Name} guardado en el mundo");
        } catch (Exception e) when (e is JsonException or InvalidDataException or IOException) {
            Log.Warn($"No se pudo guardar el personaje de {from.Name}: {e.Message}");
        }
    }

    private void OnPlayerLeft(SessionPlayer player) {
        _keys.Remove(player.Id);
        if (_session != null && _session.IsHost) {
            _saveSoon = true;
        }
    }

    // =====================================================================================================
    // Común
    // =====================================================================================================

    private void OnLarge(SessionPlayer from, byte kind, byte[] data) {
        if (_session == null) {
            return;
        }

        if (!_session.IsHost && kind == LargeWorld) {
            ReceiveWorld(data);
        } else if (_session.IsHost && kind == LargeCharacter) {
            ReceiveCharacter(from, data);
        }
    }

    /// <summary>Guardado automático en un momento tranquilo (jugando y sin cambiar de sala).</summary>
    public void Update() {
        if (_session == null || CoopSaves.ActiveWorld == null || !IsQuietMoment()) {
            return;
        }

        var now = Time.unscaledTime;
        if (now < _nextAutosave && !_saveSoon) {
            return;
        }

        _saveSoon = false;
        _nextAutosave = now + (_session.IsHost ? HostAutosaveSeconds : GuestAutosaveSeconds);
        if (_session.IsHost || _session.IsActive) {
            Log.Info("Guardado automático");
            GameManager.instance.SaveGame();
        }
    }

    private static bool IsQuietMoment() {
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        return gm != null && hero != null && gm.gameState == GlobalEnums.GameState.PLAYING &&
               !gm.IsInSceneTransition && hero.acceptingInput && !hero.cState.dead;
    }

    private static byte[] Compress(string text) {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress)) {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>Límites al descomprimir: unos pocos KB comprimidos no pueden convertirse en gigas.</summary>
    private const int MaxWorldChars = 64 * 1024 * 1024;

    private const int MaxCharacterChars = 1024 * 1024;

    private static string Decompress(byte[] data, int maxChars) {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) {
            if (text.Length + read > maxChars) {
                throw new InvalidDataException("Datos demasiado grandes al descomprimir");
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }
}
