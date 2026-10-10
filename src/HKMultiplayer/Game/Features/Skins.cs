using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Skins del caballero, compatibles con las de CustomKnight (como la 0.7.3): cada skin es una carpeta con Knight.png y,
/// si quiere, Sprint.png, Unn.png, los hechizos (VS.png, Wraiths.png, VoidSpells.png), Fluke.png, Hatchling.png, Hud.png,
/// Geo.png y OrbFull.png; lo que falte se ve normal. Se buscan en <c>Mods/HKMultiplayer/Skins</c> y en las carpetas de
/// skins de CustomKnight. Cada PC elige la suya en Pausa › Multijugador › Skin, y los demás la ven aunque no la tengan.
/// <para>
/// Red: cada jugador anuncia su skin al host (<see cref="SkinInfo"/>: nombre y huella de sus archivos) y el host la
/// reparte. Los archivos van por la red del propio mod (cifrada, sin abrir otro puerto) como transferencia grande de
/// baja prioridad (<see cref="NetConnection.LowPriorityBytesPerSecond"/>, unos 0,5 MB/s, con pocos trozos por delante de
/// los mensajes normales): una skin no retrasa golpes ni eventos. Quien no tiene una skin la pide al host
/// (<see cref="SkinRequest"/>); el host se la envía si la tiene o se la pide a su dueño y la reenvía al llegar. Máximo
/// 32 MB por skin; solo PNG de hasta 4096x4096; se comprueba la huella. Se guardan solo en memoria mientras dura la
/// partida (nada se escribe en disco).
/// </para>
/// <para>
/// Dibujo (como la 0.7.3): el cuerpo de cada jugador lleva su skin con un bloque de propiedades en su renderer (no en
/// los materiales compartidos), así cada uno puede llevar una distinta; los hechizos, amuletos y HUD propios cambian el
/// material compartido de su colección (como CustomKnight), y a los efectos de los compañeros (sus copias bajo el avatar)
/// se les pone la skin de su dueño o la textura original. Cada PC puede no ver las skins de los demás (ajuste propio).
/// </para>
/// </summary>
internal sealed class Skins {
    /// <summary>Skin de un jugador: nombre y huella (jugador → host; host → todos, con el jugador delante).</summary>
    public const MessageType SkinInfo = (MessageType) 96;

    /// <summary>Pedir los archivos de una skin por su huella (jugador → host; host → dueño, para que la suba).</summary>
    public const MessageType SkinRequest = (MessageType) 97;

    /// <summary>Tipo de transferencia grande con los archivos de una skin.</summary>
    private const byte LargeSkin = 3;

    /// <summary>Tamaño máximo de una skin completa.</summary>
    public const int MaxSkinBytes = 32 * 1024 * 1024;

    /// <summary>Ancho y alto máximos de cada imagen (las hojas del caballero miden como mucho 4096).</summary>
    private const int MaxImageSide = 4096;

    /// <summary>Cada cuánto se vuelve a leer la lista de carpetas como mucho.</summary>
    private const float ListRefreshSeconds = 5f;

    private static readonly byte[] PackageMagic = Encoding.ASCII.GetBytes("HKSK");
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Archivos que ven los demás (cuerpo, hechizos y efectos) y que viajan por la red, en este orden. El HUD, el geo y
    /// el orbe solo los ve su dueño.
    /// </summary>
    private static readonly string[] SentFiles = {
        "Knight.png", "Sprint.png", "Unn.png", "VS.png", "Wraiths.png", "VoidSpells.png", "Fluke.png", "Hatchling.png"
    };

    /// <summary>
    /// Colección de sprites del juego → archivo de la skin que la reemplaza (comprobado en la 0.7.3 comparando la forma
    /// de cada archivo de una skin de CustomKnight con las colecciones cargadas).
    /// </summary>
    private static readonly Dictionary<string, string> CollectionFiles = new() {
        { "Knight", "Knight.png" },
        { "Knight Dream Gate Cln", "Sprint.png" },
        { "Knight Slug Cln", "Unn.png" },
        { "Spell Effects", "VS.png" },
        { "Spell Effects 2", "Wraiths.png" },
        { "Spell Effects Neutral", "VoidSpells.png" },
        { "Fluke Spell Cln", "Fluke.png" },
        { "Knight Hatchling Cln", "Hatchling.png" }
    };

    /// <summary>Colecciones cuyo material compartido lleva la skin propia (hechizos, amuletos y HUD).</summary>
    private static readonly Dictionary<string, string> SharedCollectionFiles = new() {
        { "Spell Effects", "VS.png" },
        { "Spell Effects 2", "Wraiths.png" },
        { "Spell Effects Neutral", "VoidSpells.png" },
        { "Fluke Spell Cln", "Fluke.png" },
        { "Knight Hatchling Cln", "Hatchling.png" },
        { "HUD Cln", "Hud.png" },
        { "Geo", "Geo.png" }
    };

    /// <summary>Texturas del orbe de alma lleno del HUD que reemplaza OrbFull.png (imágenes sueltas, no hojas).</summary>
    private static readonly HashSet<string> OrbFullTextures = new() { "soul_orb_full_v020000", "soul_orb_glow0000" };

    private static readonly int MainTex = Shader.PropertyToID("_MainTex");

    /// <summary>Texturas originales de cada colección (para volver a ellas y para quien no lleva skin).</summary>
    private static readonly Dictionary<string, Texture> VanillaTextures = new();

    // ---- Una skin cargada en memoria ----
    private sealed class Skin {
        public string Name;
        public string Hash;

        /// <summary>Archivos (nombre → contenido PNG).</summary>
        public Dictionary<string, byte[]> Files;

        /// <summary>Paquete para enviarlo (en el host y en el dueño).</summary>
        public byte[] Package;

        public readonly Dictionary<string, Texture2D> Textures = new();

        /// <summary>Archivos cuya textura no se pudo crear (no se reintenta).</summary>
        public readonly HashSet<string> Failed = new();

        /// <summary>Avisos al leerla en otro hilo (se registran en el principal).</summary>
        public readonly List<string> Warnings = new();
    }

    private static Skins _active;

    private readonly MessageHandler _onInfo;
    private readonly MessageHandler _onRequest;
    private readonly Action<SessionPlayer, byte, byte[]> _onLarge;
    private readonly Action<SessionPlayer> _onJoined;
    private readonly Action<SessionPlayer> _onLeft;
    private GameSession _session;
    private PlayerSync _players;

    /// <summary>Skins conocidas por huella (la propia, las recibidas y, en el host, las de todos).</summary>
    private readonly Dictionary<string, Skin> _skins = new();

    /// <summary>Skin que lleva cada jugador (nombre y huella; vacía = normal).</summary>
    private readonly Dictionary<ushort, (string Name, string Hash)> _worn = new();

    /// <summary>Huellas ya pedidas al host (para no pedirlas otra vez).</summary>
    private readonly HashSet<string> _requested = new();

    /// <summary>Host: quién espera cada skin que aún no tiene.</summary>
    private readonly Dictionary<string, List<ushort>> _waiting = new();

    // ---- Skin propia ----
    private string _localFor;
    private Skin _local;
    private Task<Skin> _loading;
    private string _loadingFor;
    private string _announced;

    // ---- Dibujo ----
    private readonly MaterialPropertyBlock _block = new();
    private readonly List<tk2dBaseSprite> _effectSprites = new();
    private string _sharedAppliedHash;
    private int _sharedAppliedScene = -1;

    /// <summary>Si faltan texturas de la skin propia, cuándo se vuelve a intentar (buscar las colecciones crea memoria).</summary>
    private float _sharedRetryAt;
    private readonly Dictionary<SpriteRenderer, Sprite> _orbOriginals = new();
    private readonly Dictionary<Sprite, Sprite> _orbSkinned = new();
    private string _orbHash;

    /// <summary>Texturas creadas este frame (crearlas cuesta: como mucho una por frame).</summary>
    private int _texturesThisFrame;

    /// <summary>Desde cuándo espera alguna textura a un momento sin juego para crearse (-1 si ninguna).</summary>
    private float _waitingTextureSince = -1f;

    /// <summary>
    /// Lo más que espera una textura a un momento sin juego; pasado esto se crea igualmente, salvo con un jefe en la
    /// sala.
    /// </summary>
    private const float TextureWaitSeconds = 60f;

    private static List<string> _available = new();
    private static Dictionary<string, string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private static float _listReadAt = -999f;

    static Skins() {
        MessageRoutes.Register(SkinInfo, Route.Server, reliable: true);
        MessageRoutes.Register(SkinRequest, Route.Server, reliable: true);
    }

    public Skins() {
        _onInfo = OnInfo;
        _onRequest = OnRequest;
        _onLarge = OnLarge;
        _onJoined = OnPlayerJoined;
        _onLeft = OnPlayerLeft;
    }

    /// <summary>Las skins del PC actual (null sin partida).</summary>
    public static Skins Current => _active;

    public void Attach(GameSession session, PlayerSync players) {
        _session = session;
        _players = players;
        _active = this;
        _worn.Clear();
        _requested.Clear();
        _waiting.Clear();
        _announced = null;
        session.On(SkinInfo, _onInfo);
        session.On(SkinRequest, _onRequest);
        session.LargeMessage += _onLarge;
        session.PlayerJoined += _onJoined;
        session.PlayerLeft += _onLeft;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(SkinInfo, null);
            _session.On(SkinRequest, null);
            _session.LargeMessage -= _onLarge;
            _session.PlayerJoined -= _onJoined;
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        _players = null;
        _worn.Clear();
        _requested.Clear();
        _waiting.Clear();
        _unpacking.Clear();
        _announced = null;
        // Las de los demás se olvidan; la propia sigue (también se ve jugando solo)
        foreach (var hash in _skins.Keys.ToList()) {
            if (_local == null || hash != _local.Hash) {
                Forget(hash);
            }
        }

        if (_active == this) {
            _active = null;
        }
    }

    // =====================================================================================================
    // Lista de skins y la propia
    // =====================================================================================================

    /// <summary>Carpeta de skins del mod (junto a su DLL).</summary>
    public static string SkinsFolder =>
        Path.Combine(Path.GetDirectoryName(typeof(Skins).Assembly.Location) ?? ".", "Skins");

    /// <summary>Nombres de las skins instaladas (se relee la carpeta como mucho cada pocos segundos).</summary>
    public static IReadOnlyList<string> Available {
        get {
            if (Time.unscaledTime - _listReadAt >= ListRefreshSeconds) {
                RefreshList();
            }

            return _available;
        }
    }

    public static void RefreshList() {
        _listReadAt = Time.unscaledTime;
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SkinRoots()) {
            try {
                if (!Directory.Exists(root)) {
                    continue;
                }

                foreach (var dir in Directory.GetDirectories(root)) {
                    var name = Path.GetFileName(dir);
                    // Una skin tiene al menos Knight.png (si el nombre se repite gana la carpeta del mod)
                    if (!folders.ContainsKey(name) && File.Exists(Path.Combine(dir, "Knight.png"))) {
                        folders[name] = dir;
                    }
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // Carpeta sin permisos o que desapareció: se ignora
            }
        }

        _folders = folders;
        _available = folders.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Carpetas donde se buscan skins: la del mod y las de CustomKnight.</summary>
    private static IEnumerable<string> SkinRoots() {
        yield return SkinsFolder;
        var mods = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Skins).Assembly.Location) ?? ".") ?? ".";
        yield return Path.Combine(Path.Combine(mods, "CustomKnight"), "Skins");
        yield return Path.Combine(Path.Combine(Path.Combine(mods, "Disabled"), "CustomKnight"), "Skins");
    }

    /// <summary>Skin propia elegida (vacía = normal).</summary>
    public static string LocalSkinName => HKMultiplayerMod.Settings.SkinName ?? string.Empty;

    /// <summary>Elige la skin propia (vacía = normal) y la guarda; los demás la reciben solos.</summary>
    public static void SelectLocalSkin(string name) {
        HKMultiplayerMod.Settings.SkinName = name ?? string.Empty;
        HKMultiplayerMod.SaveSettings();
        Log.Info($"Skin propia: {(string.IsNullOrEmpty(name) ? "normal" : name)}");
    }

    /// <summary>Ver o no las skins de los demás en este PC (se guarda).</summary>
    public static void SetShowOthers(bool show) {
        HKMultiplayerMod.Settings.ShowOthersSkins = show;
        HKMultiplayerMod.SaveSettings();
        Log.Info($"Skins de los demás: {(show ? "se ven" : "no se ven")}");
    }

    public static bool ShowOthers => HKMultiplayerMod.Settings.ShowOthersSkins;

    /// <summary>Abre la carpeta de skins en el explorador de Windows.</summary>
    public static void OpenFolder() {
        Directory.CreateDirectory(SkinsFolder);
        Application.OpenURL(new Uri(SkinsFolder).AbsoluteUri);
    }

    /// <summary>Carga la skin propia cuando cambia la elegida (leer y comprobar los archivos va en otro hilo).</summary>
    private void UpdateLocal() {
        var name = LocalSkinName;
        if (_loading != null) {
            if (!_loading.IsCompleted) {
                return;
            }

            var loaded = _loading.IsFaulted ? null : _loading.Result;
            if (_loading.IsFaulted) {
                Log.Warn($"No se pudo leer la skin {_loadingFor}: {_loading.Exception?.GetBaseException().Message}");
            }

            _loading = null;
            if (_loadingFor == name) {
                SetLocal(loaded);
                return;
            }
        }

        if (name == _localFor) {
            return;
        }

        _localFor = name;
        if (name.Length == 0) {
            SetLocal(null);
            return;
        }

        RefreshList();
        if (!_folders.TryGetValue(name, out var folder)) {
            Log.Warn($"La skin elegida ({name}) no está en la carpeta de skins: se usa la normal");
            SetLocal(null);
            return;
        }

        _loadingFor = name;
        _loading = Task.Run(() => ReadFolder(name, folder));
    }

    private void SetLocal(Skin skin) {
        var previous = _local;
        if (skin != null && _skins.TryGetValue(skin.Hash, out var known)) {
            known.Name = skin.Name;
            known.Package ??= skin.Package;
            skin = known;
        }

        _local = skin;
        if (skin != null) {
            foreach (var warning in skin.Warnings) {
                Log.Warn(warning);
            }

            _skins[skin.Hash] = skin;
            Log.Info($"Skin propia lista: {skin.Name} ({skin.Hash}, {skin.Package.Length / 1048576f:0.0} MB)");
        }

        if (previous != null && previous != skin && !InUse(previous.Hash)) {
            Forget(previous.Hash);
        }
    }

    /// <summary>Lee una carpeta de skin, la comprueba y la empaqueta (en otro hilo).</summary>
    private static Skin ReadFolder(string name, string folder) {
        var files = new Dictionary<string, byte[]>();
        var warnings = new List<string>();
        var total = 0;
        foreach (var file in SentFiles.Concat(new[] { "Hud.png", "Geo.png", "OrbFull.png" })) {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path)) {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (!IsAcceptablePng(bytes)) {
                warnings.Add($"{path} no es un PNG válido o es demasiado grande (máximo {MaxImageSide}x{MaxImageSide})");
                continue;
            }

            files[file] = bytes;
            if (Array.IndexOf(SentFiles, file) >= 0) {
                total += bytes.Length;
            }
        }

        if (!files.ContainsKey("Knight.png")) {
            throw new IOException("sin Knight.png válido");
        }

        if (total > MaxSkinBytes) {
            throw new IOException($"pesa {total / 1048576f:0.0} MB (máximo {MaxSkinBytes / 1048576} MB)");
        }

        var skin = new Skin { Name = name, Hash = HashOf(files), Files = files, Package = Pack(name, files) };
        skin.Warnings.AddRange(warnings);
        return skin;
    }

    // =====================================================================================================
    // Paquete, huella y comprobaciones
    // =====================================================================================================

    /// <summary>Huella de los archivos que ven los demás (los mismos en todos los PCs).</summary>
    private static string HashOf(Dictionary<string, byte[]> files) {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
            foreach (var file in SentFiles) {
                writer.Write(file);
                if (files.TryGetValue(file, out var content)) {
                    writer.Write(content.Length);
                    writer.Write(content);
                } else {
                    writer.Write(-1);
                }
            }
        }

        stream.Position = 0;
        return BitConverter.ToString(sha.ComputeHash(stream), 0, 16).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>Paquete con el nombre y los archivos que ven los demás.</summary>
    private static byte[] Pack(string name, Dictionary<string, byte[]> files) {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
            writer.Write(PackageMagic);
            writer.Write(name.Length > 64 ? name.Substring(0, 64) : name);
            foreach (var file in SentFiles) {
                if (files.TryGetValue(file, out var content)) {
                    writer.Write(content.Length);
                    writer.Write(content);
                } else {
                    writer.Write(-1);
                }
            }
        }

        return stream.ToArray();
    }

    /// <summary>Lee un paquete recibido; null si no es válido.</summary>
    private static Skin Unpack(byte[] package) {
        if (package.Length > MaxSkinBytes + 4096) {
            return null;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(package), Encoding.UTF8);
            var magic = reader.ReadBytes(PackageMagic.Length);
            if (!magic.SequenceEqual(PackageMagic)) {
                return null;
            }

            var name = reader.ReadString();
            var files = new Dictionary<string, byte[]>();
            var total = 0;
            foreach (var file in SentFiles) {
                var length = reader.ReadInt32();
                if (length < -1 || length > MaxSkinBytes - total) {
                    return null;
                }

                if (length < 0) {
                    continue;
                }

                total += length;
                var content = reader.ReadBytes(length);
                if (content.Length != length || !IsAcceptablePng(content)) {
                    return null;
                }

                files[file] = content;
            }

            if (!files.ContainsKey("Knight.png")) {
                return null;
            }

            return new Skin { Name = name.Length > 64 ? name.Substring(0, 64) : name, Hash = HashOf(files), Files = files };
        } catch (Exception e) when (e is IOException or ArgumentException) {
            return null;
        }
    }

    /// <summary>
    /// PNG con cabecera válida y de tamaño razonable. Se mira antes de que el juego la decodifique, para que una imagen
    /// que diga medir 50000x50000 no pueda agotar la memoria.
    /// </summary>
    private static bool IsAcceptablePng(byte[] data) {
        if (data.Length < 24 || data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R') {
            return false;
        }

        for (var i = 0; i < PngSignature.Length; i++) {
            if (data[i] != PngSignature[i]) {
                return false;
            }
        }

        var width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        var height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
        return width is > 0 and <= MaxImageSide && height is > 0 and <= MaxImageSide;
    }

    private static bool IsValidHash(string hash) {
        if (hash == null || hash.Length != 32) {
            return false;
        }

        foreach (var c in hash) {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) {
                return false;
            }
        }

        return true;
    }

    // =====================================================================================================
    // Red
    // =====================================================================================================

    /// <summary>Cada frame: la skin propia y su anuncio.</summary>
    public void Update() {
        UpdateLocal();
        FinishUnpacking();
        if (_session == null || !_session.CanSend) {
            return;
        }

        var hash = _local?.Hash ?? string.Empty;
        if (_loading == null && _announced != hash) {
            _announced = hash;
            var writer = _session.Begin(SkinInfo);
            writer.WriteString(hash.Length > 0 ? _local.Name : string.Empty);
            writer.WriteString(hash);
            _session.End();
        }
    }

    private void OnInfo(SessionPlayer from, PacketReader reader) {
        if (_session == null) {
            return;
        }

        if (_session.IsHost) {
            // De un jugador: se apunta y se reparte a los demás
            var name = reader.ReadString(64);
            var hash = reader.ReadString(64);
            if (hash.Length > 0 && !IsValidHash(hash)) {
                return;
            }

            SetWorn(from, name, hash);
            foreach (var player in _session.Players) {
                if (player.Id != from.Id && player.Connection != null) {
                    SendInfoTo(player, from.Id, name, hash);
                }
            }

            // Si el host aún no la tiene, se la pide a su dueño (para verla y para dársela a los demás)
            if (hash.Length > 0 && !_skins.ContainsKey(hash) && from.Connection != null && _requested.Add(hash)) {
                var writer = _session.BeginTo(from, SkinRequest);
                writer.WriteString(hash);
                _session.EndTo();
            }

            return;
        }

        // Del host: la skin de un jugador
        var id = reader.ReadUShort();
        var playerName = reader.ReadString(64);
        var playerHash = reader.ReadString(64);
        if (from.Id != GameSession.HostPlayerId || playerHash.Length > 0 && !IsValidHash(playerHash)) {
            return;
        }

        foreach (var player in _session.Players) {
            if (player.Id == id && !player.IsLocal) {
                SetWorn(player, playerName, playerHash);
            }
        }
    }

    private void SetWorn(SessionPlayer player, string name, string hash) {
        _worn.TryGetValue(player.Id, out var previous);
        _worn[player.Id] = (name, hash);
        if (player.IsLocal || previous.Hash == hash) {
            return;
        }

        Log.Info($"Skin de {player.Name}: {(hash.Length > 0 ? $"{name} ({hash})" : "normal")}");
        if (previous.Hash is { Length: > 0 } old && !InUse(old)) {
            Forget(old);
        }

        if (hash.Length > 0 && !_skins.ContainsKey(hash) && !_session.IsHost && ShowOthers && _requested.Add(hash)) {
            // Se pide al host (llega en segundo plano)
            if (HostSettings.Current.ShowNotices) {
                Notice.Show(Lang.T($"Recibiendo la skin \"{name}\" de {player.Name}...", $"Receiving the skin '{name}' from {player.Name}..."), 4f);
            }

            var writer = _session.Begin(SkinRequest);
            writer.WriteString(hash);
            _session.End();
        }
    }

    private void SendInfoTo(SessionPlayer target, ushort id, string name, string hash) {
        var writer = _session.BeginTo(target, SkinInfo);
        writer.WriteUShort(id);
        writer.WriteString(name);
        writer.WriteString(hash);
        _session.EndTo();
    }

    private void OnPlayerJoined(SessionPlayer player) {
        // Quien entra recibe la skin de cada uno
        if (_session == null || !_session.IsHost || player.Connection == null) {
            return;
        }

        foreach (var other in _session.Players) {
            if (other.Id != player.Id && _worn.TryGetValue(other.Id, out var worn)) {
                SendInfoTo(player, other.Id, worn.Name, worn.Hash);
            }
        }
    }

    private void OnPlayerLeft(SessionPlayer player) {
        if (_worn.TryGetValue(player.Id, out var worn)) {
            _worn.Remove(player.Id);
            if (worn.Hash is { Length: > 0 } hash && !InUse(hash)) {
                Forget(hash);
            }
        }
    }

    private void OnRequest(SessionPlayer from, PacketReader reader) {
        var hash = reader.ReadString(64);
        if (_session == null || !IsValidHash(hash)) {
            return;
        }

        if (!_session.IsHost) {
            // El host pide la skin propia para repartirla
            if (from.Id == GameSession.HostPlayerId && _local?.Hash == hash && _local.Package != null) {
                Log.Info($"Enviando la skin propia al host ({_local.Package.Length / 1048576f:0.0} MB, en segundo plano)");
                _session.SendLargeToHost(LargeSkin, _local.Package, lowPriority: true);
            }

            return;
        }

        if (from.Connection == null) {
            return;
        }

        if (_skins.TryGetValue(hash, out var skin) && skin.Package != null) {
            _session.SendLargeTo(from, LargeSkin, skin.Package, lowPriority: true);
            return;
        }

        if (!_waiting.TryGetValue(hash, out var waiting)) {
            waiting = new List<ushort>();
            _waiting[hash] = waiting;
        }

        if (!waiting.Contains(from.Id)) {
            waiting.Add(from.Id);
        }
    }

    /// <summary>Skins recibidas que se están comprobando en otro hilo (la huella de 20 MB cuesta).</summary>
    private readonly List<(Task<Skin> Task, byte[] Data)> _unpacking = new();

    private void OnLarge(SessionPlayer from, byte kind, byte[] data) {
        if (kind != LargeSkin || _session == null) {
            return;
        }

        _unpacking.Add((Task.Run(() => Unpack(data)), data));
    }

    /// <summary>Termina las skins recibidas ya comprobadas.</summary>
    private void FinishUnpacking() {
        for (var i = _unpacking.Count - 1; i >= 0; i--) {
            var (task, data) = _unpacking[i];
            if (!task.IsCompleted) {
                continue;
            }

            _unpacking.RemoveAt(i);
            if (_session != null) {
                Received(task.IsFaulted ? null : task.Result, data);
            }
        }
    }

    private void Received(Skin skin, byte[] data) {
        if (skin == null) {
            Log.Warn("Llegó una skin que no es válida: se ignora");
            return;
        }

        // Solo se acepta una skin que alguien lleva (no cualquier dato)
        var expected = false;
        foreach (var worn in _worn.Values) {
            expected |= worn.Hash == skin.Hash;
        }

        if (!expected) {
            Log.Warn($"Llegó la skin {skin.Hash}, que no lleva nadie: se ignora");
            return;
        }

        if (!_skins.ContainsKey(skin.Hash)) {
            skin.Package = _session.IsHost ? data : null;
            _skins[skin.Hash] = skin;
            Log.Info($"Skin recibida: {skin.Name} ({skin.Hash}, {data.Length / 1048576f:0.0} MB)");
        }

        if (!_session.IsHost || !_waiting.TryGetValue(skin.Hash, out var waiting)) {
            return;
        }

        _waiting.Remove(skin.Hash);
        foreach (var player in _session.Players) {
            if (waiting.Contains(player.Id) && player.Connection != null) {
                _session.SendLargeTo(player, LargeSkin, data, lowPriority: true);
            }
        }
    }

    /// <summary>Si alguien (o este PC) lleva todavía esa skin.</summary>
    private bool InUse(string hash) {
        if (_local != null && _local.Hash == hash || _loading != null) {
            return true;
        }

        foreach (var worn in _worn.Values) {
            if (worn.Hash == hash) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Olvida una skin y libera sus texturas (antes, lo que la llevaba vuelve a lo normal).</summary>
    private void Forget(string hash) {
        if (!_skins.TryGetValue(hash, out var skin)) {
            return;
        }

        RestoreShared(skin);
        foreach (var texture in skin.Textures.Values) {
            if (texture != null) {
                UnityEngine.Object.Destroy(texture);
            }
        }

        _skins.Remove(hash);
        _requested.Remove(hash);
        Log.Info($"Skin {skin.Name} ({hash}) olvidada: ya no la lleva nadie");
    }

    // =====================================================================================================
    // Dibujo
    // =====================================================================================================

    /// <summary>Cada frame, después de dibujar a los demás: el cuerpo, los efectos y el HUD con su skin.</summary>
    public void LateUpdate(string scene) {
        _texturesThisFrame = 0;
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        ApplyShared();
        KeepOrbFull();
        ApplyTo(hero.GetComponent<MeshRenderer>(), hero.GetComponent<tk2dSprite>(), _local);
        if (_session == null || _players == null || scene.Length == 0) {
            return;
        }

        foreach (var player in _session.Players) {
            if (player.IsLocal || player.Scene != scene) {
                continue;
            }

            var avatar = _players.AvatarOf(player.Id);
            if (avatar == null) {
                continue;
            }

            var skin = ShowOthers && _worn.TryGetValue(player.Id, out var worn) && worn.Hash.Length > 0 &&
                       _skins.TryGetValue(worn.Hash, out var known)
                ? known
                : null;
            ApplyTo(avatar.Renderer, avatar.Sprite, skin);
            ApplyToEffects(avatar, skin);
        }
    }

    /// <summary>
    /// Pone en el renderer la textura de la skin para la colección que muestra ahora (o la normal). Cada frame: el
    /// sprite cambia de colección según la animación. Se conserva lo demás del bloque (el destello al recibir daño).
    /// </summary>
    private void ApplyTo(MeshRenderer renderer, tk2dBaseSprite sprite, Skin skin) {
        if (renderer == null || sprite == null || renderer.sharedMaterial == null) {
            return;
        }

        var collection = sprite.Collection;
        Texture texture = null;
        if (skin != null && collection != null && CollectionFiles.TryGetValue(collection.spriteCollectionName, out var file)) {
            texture = TextureOf(skin, file, null);
        }

        // Sin skin para esta colección: la original (las compartidas pueden llevar ya la skin propia)
        if (texture == null && collection != null) {
            VanillaTextures.TryGetValue(collection.spriteCollectionName, out texture);
        }

        texture ??= renderer.sharedMaterial.mainTexture;
        if (texture == null) {
            return;
        }

        renderer.GetPropertyBlock(_block);
        _block.SetTexture(MainTex, texture);
        renderer.SetPropertyBlock(_block);
    }

    /// <summary>
    /// Los efectos del compañero son copias bajo su avatar que comparten material con los propios: se les pone la skin
    /// de su dueño, o la textura original si no lleva (los materiales compartidos pueden llevar la skin propia).
    /// </summary>
    private void ApplyToEffects(RemoteAvatar avatar, Skin skin) {
        avatar.SpriteTransform.GetComponentsInChildren(_effectSprites);
        ApplyToEffectSprites(avatar, skin);

        // Lo que vuela por su cuenta (bolas de fuego, rayos, moscas...)
        if (avatar.LooseEffects != null) {
            avatar.LooseEffects.GetComponentsInChildren(_effectSprites);
            ApplyToEffectSprites(avatar, skin);
        }
    }

    private void ApplyToEffectSprites(RemoteAvatar avatar, Skin skin) {
        foreach (var sprite in _effectSprites) {
            if (sprite.gameObject == avatar.Sprite.gameObject) {
                continue;
            }

            var renderer = sprite.GetComponent<Renderer>();
            var collection = sprite.Collection;
            if (renderer == null || collection == null || !renderer.enabled) {
                continue;
            }

            Texture texture = null;
            if (skin != null && CollectionFiles.TryGetValue(collection.spriteCollectionName, out var file)) {
                texture = TextureOf(skin, file, null);
            }

            if (texture == null) {
                if (!VanillaTextures.TryGetValue(collection.spriteCollectionName, out texture)) {
                    continue;
                }
            }

            renderer.GetPropertyBlock(_block);
            _block.SetTexture(MainTex, texture);
            renderer.SetPropertyBlock(_block);
        }
    }

    /// <summary>
    /// La skin propia en los materiales compartidos de hechizos, amuletos y HUD (al cambiar de skin o de sala, porque
    /// algunas colecciones se cargan con la sala). Sin skin, o sin ese archivo, vuelve la textura original.
    /// </summary>
    private void ApplyShared() {
        var hash = _local?.Hash ?? string.Empty;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (hash == _sharedAppliedHash && scene == _sharedAppliedScene || Time.unscaledTime < _sharedRetryAt) {
            return;
        }

        // Las texturas de la skin se crean de una en una: hasta tenerlas todas se vuelve a intentar
        var complete = true;
        foreach (var collection in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()) {
            if (collection == null || collection.materials == null || collection.materials.Length == 0 ||
                collection.materials[0] == null) {
                continue;
            }

            var name = collection.spriteCollectionName;
            if (!VanillaTextures.ContainsKey(name) && (CollectionFiles.ContainsKey(name) || SharedCollectionFiles.ContainsKey(name))) {
                VanillaTextures[name] = collection.materials[0].mainTexture;
            }

            if (!SharedCollectionFiles.TryGetValue(name, out var file)) {
                continue;
            }

            var vanilla = VanillaTextures[name];
            // Hojas que cambiaron en esta versión del juego: se recolocan los sprites que la skin tiene en su sitio antiguo
            var repair = name is "HUD Cln" or "Geo" ? collection : null;
            Texture texture = null;
            if (_local != null && _local.Files.ContainsKey(file)) {
                texture = TextureOf(_local, file, repair);
                complete &= texture != null || _local.Failed.Contains(file);
            }

            texture ??= vanilla;
            foreach (var material in collection.materials.Concat(collection.materialInsts ?? Array.Empty<Material>())) {
                if (material != null && (material.mainTexture == vanilla || IsSkinTexture(material.mainTexture))) {
                    material.mainTexture = texture;
                }
            }
        }

        ApplyOrbFull();
        if (complete) {
            _sharedAppliedHash = hash;
            _sharedAppliedScene = scene;
        } else {
            _sharedRetryAt = Time.unscaledTime + 0.2f;
        }
    }

    /// <summary>Antes de olvidar una skin, lo que la llevaba en los materiales compartidos vuelve a lo normal.</summary>
    private void RestoreShared(Skin skin) {
        if (skin.Textures.Count == 0) {
            return;
        }

        foreach (var collection in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()) {
            if (collection?.materials == null || !VanillaTextures.TryGetValue(collection.spriteCollectionName, out var vanilla)) {
                continue;
            }

            foreach (var material in collection.materials.Concat(collection.materialInsts ?? Array.Empty<Material>())) {
                if (material != null && material.mainTexture is Texture2D texture && skin.Textures.ContainsValue(texture)) {
                    material.mainTexture = vanilla;
                }
            }
        }

        if (_orbHash == skin.Hash) {
            ApplyOrbFull(true);
        }

        _sharedAppliedScene = -1;
    }

    private bool IsSkinTexture(Texture texture) {
        if (texture is not Texture2D texture2D) {
            return false;
        }

        foreach (var skin in _skins.Values) {
            if (skin.Textures.ContainsValue(texture2D)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Orbe de alma lleno del HUD (OrbFull.png): es un sprite suelto, así que se cambia el sprite de sus SpriteRenderer por
    /// uno hecho con la imagen de la skin (mismo pivote y escala). Solo el propio.
    /// </summary>
    private void ApplyOrbFull(bool reset = false) {
        var orbTexture = !reset && _local != null && _local.Files.ContainsKey("OrbFull.png")
            ? TextureOf(_local, "OrbFull.png", null)
            : null;
        var orbKey = orbTexture != null ? _local.Hash : string.Empty;
        if (orbKey != _orbHash) {
            foreach (var sprite in _orbSkinned.Values) {
                UnityEngine.Object.Destroy(sprite);
            }

            _orbSkinned.Clear();
            _orbHash = orbKey;
        }

        foreach (var renderer in Resources.FindObjectsOfTypeAll<SpriteRenderer>()) {
            if (renderer == null || !renderer.gameObject.scene.IsValid()) {
                continue;
            }

            if (!_orbOriginals.TryGetValue(renderer, out var original)) {
                if (renderer.sprite == null || !OrbFullTextures.Contains(renderer.sprite.texture.name)) {
                    continue;
                }

                original = renderer.sprite;
                _orbOriginals[renderer] = original;
            }

            if (orbTexture == null) {
                renderer.sprite = original;
                continue;
            }

            if (!_orbSkinned.TryGetValue(original, out var skinned)) {
                var pivot = new Vector2(original.pivot.x / original.rect.width, original.pivot.y / original.rect.height);
                // Misma escala en pantalla: los píxeles por unidad se ajustan al tamaño de la imagen de la skin
                var pixelsPerUnit = original.pixelsPerUnit * orbTexture.width / original.rect.width;
                skinned = Sprite.Create(orbTexture, new Rect(0, 0, orbTexture.width, orbTexture.height), pivot, pixelsPerUnit);
                skinned.name = $"{original.name} (skin)";
                _orbSkinned[original] = skinned;
            }

            renderer.sprite = skinned;
        }
    }

    /// <summary>El juego vuelve a poner el sprite del orbe lleno (su brillo): se vuelve a poner el de la skin cada frame.</summary>
    private void KeepOrbFull() {
        if (_orbSkinned.Count == 0) {
            return;
        }

        foreach (var pair in _orbOriginals) {
            var renderer = pair.Key;
            if (renderer == null || renderer.sprite == null || !_orbSkinned.TryGetValue(pair.Value, out var skinned) ||
                renderer.sprite == skinned) {
                continue;
            }

            if (_orbSkinned.TryGetValue(renderer.sprite, out var current)) {
                renderer.sprite = current;
            } else if (OrbFullTextures.Contains(renderer.sprite.texture.name)) {
                renderer.sprite = skinned;
            }
        }
    }

    /// <summary>
    /// Textura de un archivo de la skin, creada una sola vez (y como mucho una por frame: crearla cuesta). Null si aún
    /// no se creó o no se puede.
    /// </summary>
    private Texture2D TextureOf(Skin skin, string file, tk2dSpriteCollectionData repairWith) {
        if (skin.Textures.TryGetValue(file, out var texture)) {
            return texture;
        }

        if (skin.Failed.Contains(file) || skin.Files == null || !skin.Files.TryGetValue(file, out var bytes) ||
            _texturesThisFrame > 0 || !CanCreateTextureNow()) {
            return null;
        }

        _texturesThisFrame++;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) {
            name = $"{skin.Name}/{file}", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
        };
        var vanilla = repairWith != null && VanillaTextures.TryGetValue(repairWith.spriteCollectionName, out var v) ? v : null;
        // Sin copia legible en memoria (solo hace falta en la tarjeta gráfica), salvo para repararla
        if (!texture.LoadImage(bytes, vanilla == null)) {
            UnityEngine.Object.Destroy(texture);
            skin.Failed.Add(file);
            Log.Warn($"No se pudo crear la textura {skin.Name}/{file}");
            return null;
        }

        if (vanilla != null) {
            AtlasRepair.Repair(texture, vanilla, repairWith);
            texture.Apply(false, true);
        }

        skin.Textures[file] = texture;
        Log.Info($"Textura {skin.Name}/{file} creada ({texture.width}x{texture.height}, {watch.ElapsedMilliseconds} ms)");
        return texture;
    }

    /// <summary>Para el panel: la skin propia y la de cada compañero (y si ya llegó).</summary>
    public string DescribeForPanel(GameSession session) {
        var text = new StringBuilder();
        text.Append(_local != null ? Lang.T($"Llevas «{_local.Name}»", $"You're wearing '{_local.Name}'") : LocalSkinName.Length > 0 ? Lang.T($"Cargando «{LocalSkinName}»...", $"Loading '{LocalSkinName}'...") : Lang.T("Llevas el caballero normal", "You're wearing the default knight"));
        foreach (var player in session.Players) {
            if (player.IsLocal) {
                continue;
            }

            text.Append('\n').Append(player.Name).Append(": ");
            if (!_worn.TryGetValue(player.Id, out var worn) || worn.Hash.Length == 0) {
                text.Append(Lang.T("normal", "default"));
            } else if (!ShowOthers) {
                text.Append(Lang.T($"«{worn.Name}» (no la ves: desactivado)", $"'{worn.Name}' (hidden: turned off)"));
            } else {
                text.Append(_skins.ContainsKey(worn.Hash) ? Lang.T($"«{worn.Name}»", $"'{worn.Name}'") : Lang.T($"«{worn.Name}» (recibiéndola...)", $"'{worn.Name}' (receiving...)"));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Crear la textura de una hoja de 4096x4096 lleva unos 0,15-0,25 s (el juego descomprime el PNG en el hilo principal):
    /// se hace cuando no se está jugando (cargando una sala, en pausa o sin caballero), así no da tirones. Mientras, ese
    /// caballero se ve normal. Si en un rato no hay ese momento, se crea igualmente fuera de un combate de jefe.
    /// </summary>
    private bool CanCreateTextureNow() {
        var gm = GameManager.instance;
        if (gm == null || HeroController.instance == null || gm.IsInSceneTransition || gm.isPaused ||
            gm.gameState != GlobalEnums.GameState.PLAYING) {
            _waitingTextureSince = -1f;
            return true;
        }

        var now = Time.unscaledTime;
        if (_waitingTextureSince < 0f) {
            _waitingTextureSince = now;
        }

        if (now - _waitingTextureSince >= TextureWaitSeconds && !Scene.EnemySync.BossInRoom) {
            _waitingTextureSince = -1f;
            return true;
        }

        return false;
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    public string Describe() {
        var text = new StringBuilder();
        text.Append($"propia={(_local != null ? $"{_local.Name}/{_local.Hash}" : LocalSkinName.Length > 0 ? $"{LocalSkinName} (cargando)" : Lang.T("normal", "default"))} " +
                    $"ver las de los demás={ShowOthers} disponibles=[{string.Join(", ", Available)}] cargadas=[");
        foreach (var skin in _skins.Values) {
            text.Append($"{skin.Name}/{skin.Hash} texturas={skin.Textures.Count}; ");
        }

        text.Append("] llevan=[");
        foreach (var pair in _worn) {
            text.Append($"{pair.Key}:{(pair.Value.Hash.Length > 0 ? pair.Value.Name : Lang.T("normal", "default"))}" +
                        $"{(pair.Value.Hash.Length > 0 && !_skins.ContainsKey(pair.Value.Hash) ? " (sin recibir)" : string.Empty)}; ");
        }

        text.Append(']');
        if (_session != null && _players != null) {
            foreach (var player in _session.Players) {
                var avatar = player.IsLocal ? null : _players.AvatarOf(player.Id);
                var renderer = player.IsLocal ? HeroController.instance?.GetComponent<MeshRenderer>() : avatar?.Renderer;
                if (renderer == null) {
                    continue;
                }

                renderer.GetPropertyBlock(_block);
                var texture = _block.GetTexture(MainTex);
                text.Append($" {player.Name} dibuja={(texture != null ? $"{texture.name} {texture.width}x{texture.height}" : "sin bloque")}");
            }
        }

        return text.ToString();
    }
}
