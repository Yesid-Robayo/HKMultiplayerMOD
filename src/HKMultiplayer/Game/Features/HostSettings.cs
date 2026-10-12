using System;
using System.IO;
using System.Text;
using HKMultiplayer.Game.World;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HKMultiplayer.Game.Features;

/// <summary>Un ajuste de la partida: sus valores posibles (con su texto) y cuál es el de por defecto.</summary>
internal sealed class HostOption {
    public readonly int Index;
    public readonly string Key;
    public readonly float[] Values;
    public readonly int Default;

    private readonly string _label;
    private readonly string _labelEnglish;
    private readonly string[] _labels;
    private readonly string[] _labelsEnglish;

    /// <summary>
    /// Textos en español y en inglés (los de los valores en inglés pueden faltar: son los mismos, como "2 s"). Se elige
    /// al mostrarlos, con el idioma del juego.
    /// </summary>
    public HostOption(int index, string key, string label, string labelEnglish, float[] values, string[] labels,
        string[] labelsEnglish, int defaultIndex) {
        Index = index;
        Key = key;
        _label = label;
        _labelEnglish = labelEnglish;
        Values = values;
        _labels = labels;
        _labelsEnglish = labelsEnglish ?? labels;
        Default = defaultIndex;
    }

    public string Label => UI.Lang.T(_label, _labelEnglish);

    public string[] Labels => UI.Lang.Spanish ? _labels : _labelsEnglish;

    /// <summary>Posición del valor más parecido (para leer valores guardados).</summary>
    public int Nearest(float value) {
        var best = Default;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < Values.Length; i++) {
            var distance = Math.Abs(Values[i] - value);
            if (distance < bestDistance) {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }
}

/// <summary>
/// Ajustes de la partida que decide el host: dificultad, revivir, lo que se ve en pantalla y permisos. Se guardan por
/// mundo (<c>settings.json</c> en su carpeta) y se envían a todos con <see cref="HostSettingsMessage"/> al cambiar y a
/// quien entra. Los invitados los ven sin poder cambiarlos. Sin partida rigen los de por defecto.
/// </summary>
internal sealed class HostSettings {
    /// <summary>Ajustes de la partida (host → todos), con la versión de su formato delante.</summary>
    public const MessageType HostSettingsMessage = (MessageType) 73;

    /// <summary>
    /// Versión del formato del mensaje: un invitado con otra la ignora (sigue con los de por defecto). La 2 añadió las
    /// barras de enemigos y "ir con un compañero" (y cambió el orden); la 3, los compañeros en el mapa; la 4, viajar
    /// juntos en ciervo.
    /// </summary>
    private const byte FormatVersion = 4;

    private const string FileName = "settings.json";

    private static readonly string[] YesNo = { "Sí", "No" };
    private static readonly string[] YesNoEnglish = { "Yes", "No" };
    private static readonly float[] YesNoValues = { 1f, 0f };

    // ---- Dificultad ----
    public static readonly HostOption BossHealthOption = new(0, "bossHealth", "Vida de los jefes", "Boss health",
        new[] { 1f, 1.25f, 1.5f, 2f, 2.5f, 3f }, new[] { "1×", "1,25×", "1,5×", "2×", "2,5×", "3×" }, new[] { "1×", "1.25×", "1.5×", "2×", "2.5×", "3×" }, 2);

    public static readonly HostOption EnemyHealthOption = new(1, "enemyHealth", "Vida de los enemigos", "Enemy health",
        new[] { 1f, 1.25f, 1.5f, 2f }, new[] { "1×", "1,25×", "1,5×", "2×" }, new[] { "1×", "1.25×", "1.5×", "2×" }, 0);

    public static readonly HostOption PartnerInRoomOption = new(2, "onlyWithPartner", "Más vida solo con compañero en la sala", "Extra health only with a partner in the room",
        YesNoValues, YesNo, YesNoEnglish, 0);

    public static readonly HostOption DamageTakenOption = new(3, "damageTaken", "Daño recibido", "Damage taken",
        new[] { 0.5f, 1f, 1.5f, 2f }, new[] { "0,5×", "1×", "1,5×", "2×" }, new[] { "0.5×", "1×", "1.5×", "2×" }, 1);

    // ---- Revivir ----
    public static readonly HostOption ReviveOption = new(4, "revive", "Revivir", "Revive", YesNoValues, YesNo, YesNoEnglish, 0);

    public static readonly HostOption ReviveSecondsOption = new(5, "reviveSeconds", "Tiempo para revivir", "Time to revive",
        new[] { 1f, 2f, 3f, 5f }, new[] { "1 s", "2 s", "3 s", "5 s" }, null, 1);

    public static readonly HostOption ReviveSoulOption = new(6, "reviveSoul", "Alma para revivir", "Soul to revive",
        new[] { 0f, 33f, 66f, 99f }, new[] { "0", "33", "66", "99" }, null, 1);

    public static readonly HostOption ReviveMasksOption = new(7, "reviveMasks", "Máscaras al revivir", "Masks on revive",
        new[] { 1f, 2f, 3f, 0f }, new[] { "1", "2", "3", "Todas" }, new[] { "1", "2", "3", "All" }, 0);

    public static readonly HostOption ReviveInvulnerableOption = new(8, "reviveInvulnerable", "Invulnerable al revivir", "Invulnerable after revive",
        new[] { 1f, 2f, 3f, 5f }, new[] { "1 s", "2 s", "3 s", "5 s" }, null, 1);

    public static readonly HostOption ReviveAtBenchOption = new(9, "reviveAtBench", "Revivir en el banco del compañero", "Revive at a partner's bench",
        YesNoValues, YesNo, YesNoEnglish, 0);

    // ---- Pantalla ----
    public static readonly HostOption ShowNamesOption = new(10, "showNames", "Nombres", "Names", YesNoValues, YesNo, YesNoEnglish, 0);

    public static readonly HostOption ShowPartnerHealthOption = new(11, "showPartnerHealth", "Vida del compañero", "Partner health",
        YesNoValues, YesNo, YesNoEnglish, 0);

    public static readonly HostOption ShowArrowsOption = new(12, "showArrows", "Flechas hacia el compañero", "Arrows to partners",
        YesNoValues, YesNo, YesNoEnglish, 0);

    /// <summary>Cada compañero sale en el mapa abierto como una brújula de otro color (si los dos llevan el Wayward Compass equipado).</summary>
    public static readonly HostOption ShowPartnerOnMapOption = new(13, "showPartnerOnMap", "Compañeros en el mapa", "Partners on the map",
        YesNoValues, YesNo, YesNoEnglish, 0);

    public static readonly HostOption ShowNoticesOption = new(14, "showNotices", "Avisos", "Notices", YesNoValues, YesNo, YesNoEnglish, 0);

    public static readonly HostOption ShowBossBarOption = new(15, "showBossBar", "Barra del jefe", "Boss bar", YesNoValues, YesNo, YesNoEnglish, 0);

    /// <summary>Barra de vida pequeña sobre los enemigos heridos (no los jefes, que tienen la suya). Desactivada por defecto.</summary>
    public static readonly HostOption ShowEnemyBarsOption = new(16, "showEnemyBars", "Barras de enemigos", "Enemy bars", YesNoValues,
        YesNo, YesNoEnglish, 1);

    // ---- Permisos ----
    public static readonly HostOption GeoGiftsOption = new(17, "geoGifts", "Regalar geo", "Gift geo", YesNoValues, YesNo, YesNoEnglish, 0);

    /// <summary>Si cada jugador puede ir junto a un compañero desde el panel ("Ir con ...") para desatascarse.</summary>
    public static readonly HostOption PartnerTravelOption = new(18, "partnerTravel", "Permitir ir con un compañero", "Allow going to a partner",
        YesNoValues, YesNo, YesNoEnglish, 0);

    /// <summary>
    /// Cómo se salta una cinemática compartida: cuando todos los que la ven votan saltarla, o en cuanto la salta
    /// cualquiera (para todos).
    /// </summary>
    public static readonly HostOption SkipCutscenesOption = new(19, "skipCutscenes", "Saltar cinemáticas", "Skip cutscenes",
        new[] { 0f, 1f }, new[] { "Votación", "Cualquiera" }, new[] { "Vote", "Anyone" }, 0);

    /// <summary>
    /// Quien elige destino en una estación del ciervo se lleva a los compañeros que están cerca de él en esa estación (ver
    /// <see cref="StagTogether"/>).
    /// </summary>
    public static readonly HostOption StagTogetherOption = new(20, "stagTogether", "Viajar juntos en ciervo", "Ride the stag together",
        YesNoValues, YesNo, YesNoEnglish, 0);

    /// <summary>Todos, en el orden del mensaje y del panel (su posición es su <see cref="HostOption.Index"/>).</summary>
    public static readonly HostOption[] Options = {
        BossHealthOption, EnemyHealthOption, PartnerInRoomOption, DamageTakenOption,
        ReviveOption, ReviveSecondsOption, ReviveSoulOption, ReviveMasksOption, ReviveInvulnerableOption, ReviveAtBenchOption,
        ShowNamesOption, ShowPartnerHealthOption, ShowArrowsOption, ShowPartnerOnMapOption, ShowNoticesOption, ShowBossBarOption,
        ShowEnemyBarsOption, GeoGiftsOption, PartnerTravelOption, SkipCutscenesOption, StagTogetherOption
    };

    /// <summary>Posición elegida de cada ajuste.</summary>
    private readonly int[] _index = new int[Options.Length];

    /// <summary>Los que rigen ahora (los del host en su PC; los recibidos en el de un invitado).</summary>
    public static HostSettings Current { get; } = new();

    /// <summary>Cambiaron los ajustes que rigen (en el host al tocarlos; en el invitado al recibirlos).</summary>
    public static event Action Changed;

    private HostSettings() {
        _onSettings = OnSettings;
        ResetToDefaults();
    }

    public void ResetToDefaults() {
        foreach (var option in Options) {
            _index[option.Index] = option.Default;
        }
    }

    public int IndexOf(HostOption option) => _index[option.Index];

    public float Value(HostOption option) => option.Values[_index[option.Index]];

    public bool Flag(HostOption option) => Value(option) > 0.5f;

    // ---- Accesos con nombre ----
    public float BossHealth => Value(BossHealthOption);
    public float EnemyHealth => Value(EnemyHealthOption);
    public bool OnlyWithPartnerInRoom => Flag(PartnerInRoomOption);
    public float DamageTaken => Value(DamageTakenOption);
    public bool ReviveEnabled => Flag(ReviveOption);
    public float ReviveSeconds => Value(ReviveSecondsOption);
    public int ReviveSoul => (int) Value(ReviveSoulOption);

    /// <summary>Máscaras con las que vuelve el revivido (0: todas).</summary>
    public int ReviveMasks => (int) Value(ReviveMasksOption);

    public float ReviveInvulnerable => Value(ReviveInvulnerableOption);
    public bool ReviveAtBench => Flag(ReviveAtBenchOption);
    public bool ShowNames => Flag(ShowNamesOption);
    public bool ShowPartnerHealth => Flag(ShowPartnerHealthOption);
    public bool ShowArrows => Flag(ShowArrowsOption);
    public bool ShowPartnerOnMap => Flag(ShowPartnerOnMapOption);
    public bool ShowNotices => Flag(ShowNoticesOption);
    public bool ShowBossBar => Flag(ShowBossBarOption);
    public bool ShowEnemyBars => Flag(ShowEnemyBarsOption);
    public bool AllowGeoGifts => Flag(GeoGiftsOption);

    /// <summary>Si se puede ir junto a un compañero desde el panel.</summary>
    public bool AllowPartnerTravel => Flag(PartnerTravelOption);

    /// <summary>Una cinemática compartida se salta en cuanto la salta cualquiera (si no, votan todos).</summary>
    public bool AnyoneSkipsCutscenes => Flag(SkipCutscenesOption);

    /// <summary>Si quien viaja en ciervo se lleva a los compañeros que están cerca en la estación.</summary>
    public bool StagTogether => Flag(StagTogetherOption);

    // =====================================================================================================
    // Red
    // =====================================================================================================

    private GameSession _session;
    private readonly MessageHandler _onSettings;

    /// <summary>Mundo del que se cargaron los ajustes (el host los vuelve a leer al cambiar de mundo).</summary>
    private string _loadedWorld;

    static HostSettings() {
        MessageRoutes.Register(HostSettingsMessage, Route.Server, reliable: true);
    }

    /// <summary>Si este PC puede cambiarlos (es el host).</summary>
    public bool Editable => _session == null || _session.IsHost;

    public void Attach(GameSession session) {
        _session = session;
        session.On(HostSettingsMessage, _onSettings);
        session.PlayerJoined += OnPlayerJoined;
        _loadedWorld = null;
        if (session.IsHost) {
            LoadForWorld();
        } else {
            // Hasta que lleguen los del host, los de por defecto
            ResetToDefaults();
            Changed?.Invoke();
        }
    }

    public void Detach() {
        if (_session != null) {
            _session.On(HostSettingsMessage, null);
            _session.PlayerJoined -= OnPlayerJoined;
            _session = null;
        }

        _loadedWorld = null;
        ResetToDefaults();
        Changed?.Invoke();
    }

    /// <summary>Llamar cada frame (barato): el host carga los del mundo que acaba de abrir.</summary>
    public void Update() {
        if (_session == null || !_session.IsHost) {
            return;
        }

        var world = CoopSaves.ActiveWorld != null && CoopSaves.IsHostWorld ? CoopSaves.ActiveWorld.Id : string.Empty;
        if (world != _loadedWorld) {
            LoadForWorld();
        }
    }

    /// <summary>El host cambia un ajuste: se guarda en el mundo y se envía a todos.</summary>
    public void Set(HostOption option, int index) {
        if (!Editable || index < 0 || index >= option.Values.Length || _index[option.Index] == index) {
            return;
        }

        _index[option.Index] = index;
        Log.Info($"Ajuste de la partida: {option.Label} = {option.Labels[index]}");
        Save();
        Broadcast();
        Changed?.Invoke();
    }

    private void OnPlayerJoined(SessionPlayer player) {
        if (_session != null && _session.IsHost && player.Connection != null) {
            SendTo(player);
        }
    }

    private void Broadcast() {
        if (_session == null || !_session.IsHost) {
            return;
        }

        foreach (var player in _session.Players) {
            if (player.Connection != null) {
                SendTo(player);
            }
        }
    }

    private void SendTo(SessionPlayer target) {
        var writer = _session.BeginTo(target, HostSettingsMessage);
        writer.WriteByte(FormatVersion);
        writer.WriteByte((byte) Options.Length);
        foreach (var option in Options) {
            writer.WriteByte((byte) _index[option.Index]);
        }

        _session.EndTo();
    }

    private void OnSettings(SessionPlayer from, PacketReader reader) {
        if (_session == null || _session.IsHost || from.Id != GameSession.HostPlayerId) {
            return;
        }

        var version = reader.ReadByte();
        if (version != FormatVersion) {
            Log.Warn($"Ajustes del host con otro formato ({version}): se ignoran");
            return;
        }

        var count = reader.ReadByte();
        for (var i = 0; i < count; i++) {
            var index = reader.ReadByte();
            if (i < Options.Length && index < Options[i].Values.Length) {
                _index[i] = index;
            }
        }

        Log.Info("Ajustes de la partida recibidos del host");
        Changed?.Invoke();
    }

    // =====================================================================================================
    // Guardado (por mundo)
    // =====================================================================================================

    private static string PathFor(string worldId) => Path.Combine(Path.Combine(WorldStore.Root, worldId), FileName);

    private void LoadForWorld() {
        var world = CoopSaves.ActiveWorld != null && CoopSaves.IsHostWorld ? CoopSaves.ActiveWorld.Id : string.Empty;
        _loadedWorld = world;
        ResetToDefaults();
        if (world.Length > 0) {
            var path = PathFor(world);
            // Si se cerró justo entre borrar el viejo y mover el nuevo, queda el nuevo como .tmp
            if (!File.Exists(path) && File.Exists(path + ".tmp")) {
                path += ".tmp";
            }

            try {
                if (File.Exists(path)) {
                    var json = JObject.Parse(File.ReadAllText(path));
                    foreach (var option in Options) {
                        if (json[option.Key] is JValue { Type: JTokenType.Float or JTokenType.Integer } value) {
                            _index[option.Index] = option.Nearest((float) value);
                        }
                    }

                    Log.Info($"Ajustes de la partida leídos del mundo {CoopSaves.ActiveWorld.Name}");
                }
            } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
                Log.Warn($"No se pudieron leer los ajustes del mundo: {e.Message}");
            }
        }

        Broadcast();
        Changed?.Invoke();
    }

    private void Save() {
        var world = _loadedWorld;
        if (string.IsNullOrEmpty(world)) {
            return;
        }

        var json = new JObject();
        foreach (var option in Options) {
            json[option.Key] = Value(option);
        }

        try {
            // Aparte y luego en su sitio: si el juego se cerraba a mitad de escribirlo, el mundo volvía a los ajustes por
            // defecto
            var path = PathFor(world);
            File.WriteAllText(path + ".tmp", json.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) {
                File.Delete(path);
            }

            File.Move(path + ".tmp", path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Log.Warn($"No se pudieron guardar los ajustes del mundo: {e.Message}");
        }
    }

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() {
        var text = new StringBuilder();
        foreach (var option in Options) {
            text.Append($"{option.Key}={option.Labels[_index[option.Index]]} ");
        }

        return text.ToString();
    }
}
