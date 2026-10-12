using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.World;

/// <summary>
/// Progreso compartido en directo dentro de un mundo coop:
/// <list type="bullet">
/// <item>Campos de la partida (habilidades, objetos, jefes, puertas, diarios...): cada 0,2 s se comparan con una copia
/// mediante lectores compilados (sin reflexión ni memoria por lectura) y lo que cambió se envía.</item>
/// <item>Listas de salas (visitadas, mapeadas, larvas rescatadas...): se envían las entradas nuevas.</item>
/// <item>Objetos de las salas (paredes, palancas, piedras de geo): se envían cuando el juego los guarda. Nunca
/// retroceden: un objeto activado sigue activado, un contador solo sube y una piedra de geo solo baja. Los que el juego
/// reinicia al descansar (semipersistentes) no se comparten.</item>
/// </list>
/// Los cambios van al host, que los aplica y los reenvía a los demás. El personaje y su inventario
/// (<see cref="CharacterData"/>) y los objetos que recoge cada uno quedan fuera.
/// </summary>
internal sealed class SharedProgress {
    /// <summary>Un cambio del mundo (jugador → host → los demás).</summary>
    public const MessageType WorldChange = (MessageType) 41;

    private const float DiffInterval = 0.2f;

    private enum Kind : byte {
        Bool = 1,
        Int = 2,
        ListAdd = 3,
        SceneBool = 4,
        SceneInt = 5,
        GeoRock = 6,

        /// <summary>Suma a un contador (ver <see cref="AdditiveInts"/>).</summary>
        IntAdd = 7
    }

    /// <summary>
    /// Contadores que se envían como suma y no como valor: los cambia solo el PC donde pasa (una larva liberada no toca los
    /// datos en los demás) y, como valor, si los dos sumaban a la vez se perdía una suma (las recompensas del Padre de las
    /// Larvas). No los del diario (`kills*`, `journalEntriesCompleted`): los cambian los dos PCs con la misma muerte y,
    /// como valor, coinciden; sumados contarían doble.
    /// </summary>
    private static readonly HashSet<string> AdditiveInts = new() { "grubsCollected" };

    private struct Change {
        public Kind Kind;
        public ushort Index;
        public int Value;
        public string Scene;
        public string Id;
    }

    /// <summary>Campos de la partida que no se comparten aunque no sean del personaje (datos de la sesión).</summary>
    private static readonly HashSet<string> Excluded = new() {
        "version", "profileID", "awardAllAchievements", "playTime", "completionPercent", "completionPercentage",
        "showGeoUI", "showHealthUI", "lastJournalItem", "infiniteAirJump", "invinciTest", "environmentTypeDefault",
        "isFirstGame", "playerStory",
        // Del momento de cada jugador: viajando en ciervo o tranvía, ante el aviso del mapa, llegando en un ascensor grande
        // (`Lift Move` lo pone justo antes de cambiar de sala: compartido, el otro PC que cargara esa sala en ese momento
        // hacía la llegada y sujetaba a su caballero a la cabina)
        "travelling", "atMapPrompt", "liftArrival",
        // Preferencias de interfaz y avisos de tutorial de cada uno: lo que elegía o veía uno cambiaba al otro
        "mapKeyPref", "promptFocus", "seenJournalMsg", "seenHunterMsg", "bossDoorEntranceTextSeen", "firstGeo"
    };

    private static readonly FieldInfo[] BoolFields;
    private static readonly Func<PlayerData, bool>[] BoolGetters;
    private static readonly Action<PlayerData, bool>[] BoolSetters;
    private static readonly FieldInfo[] IntFields;
    private static readonly Func<PlayerData, int>[] IntGetters;
    private static readonly Action<PlayerData, int>[] IntSetters;
    private static readonly FieldInfo[] ListFields;

    private GameSession _session;
    private readonly MessageHandler _onChange;
    private bool _active;
    private PlayerData _snapshotOwner;
    private bool[] _bools = Array.Empty<bool>();
    private int[] _ints = Array.Empty<int>();
    private int[] _listCounts = Array.Empty<int>();
    private float _nextDiff;
    private bool _applying;

    // Lo que ya se sabe de los objetos de las salas (para enviar solo lo nuevo)
    private readonly HashSet<string> _knownActivated = new();
    private readonly Dictionary<string, int> _knownInts = new();
    private readonly Dictionary<string, int> _knownRocks = new();

    /// <summary>Cambios que llegaron mientras aún se cargaba el mundo (se aplican al entrar).</summary>
    private readonly List<Change> _pending = new();

    private static SharedProgress _hooked;

    /// <summary>
    /// Se puso un dato del mundo (sí/no o número) con lo que llegó de otro PC, ya en la partida, con el valor que tenía
    /// antes (los sí/no, 0 o 1) y si es un sí/no: los personajes de misión de la sala que lo miran vuelven a comprobarlo
    /// (<see cref="QuestNpcSync"/>).
    /// </summary>
    public Action<string, int, bool> RemoteFieldChanged;

    static SharedProgress() {
        MessageRoutes.Register(WorldChange, Route.Server, reliable: true);
        var fields = typeof(PlayerData).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !Excluded.Contains(f.Name) && !CharacterData.IsCharacterField(f.Name))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();
        BoolFields = fields.Where(f => f.FieldType == typeof(bool)).ToArray();
        IntFields = fields.Where(f => f.FieldType == typeof(int)).ToArray();
        ListFields = fields.Where(f => f.FieldType == typeof(List<string>)).ToArray();
        BoolGetters = BoolFields.Select(Getter<bool>).ToArray();
        BoolSetters = BoolFields.Select(Setter<bool>).ToArray();
        IntGetters = IntFields.Select(Getter<int>).ToArray();
        IntSetters = IntFields.Select(Setter<int>).ToArray();
    }

    public SharedProgress() {
        _onChange = OnChange;
    }

    private static Func<PlayerData, T> Getter<T>(FieldInfo field) {
        var target = Expression.Parameter(typeof(PlayerData));
        return Expression.Lambda<Func<PlayerData, T>>(Expression.Field(target, field), target).Compile();
    }

    private static Action<PlayerData, T> Setter<T>(FieldInfo field) {
        var target = Expression.Parameter(typeof(PlayerData));
        var value = Expression.Parameter(typeof(T));
        return Expression.Lambda<Action<PlayerData, T>>(Expression.Assign(Expression.Field(target, field), value),
            target, value).Compile();
    }

    /// <summary>Número de campos compartidos (para el registro).</summary>
    public static string Describe() =>
        $"{BoolFields.Length} sí/no, {IntFields.Length} números y {ListFields.Length} listas compartidos";

    public void Attach(GameSession session) {
        _session = session;
        session.On(WorldChange, _onChange);
        _active = false;
        _snapshotOwner = null;
        _pending.Clear();
        _hooked = this;
        InstallHooks();
    }

    public void Detach() {
        _session?.On(WorldChange, null);
        _session = null;
        _active = false;
        _snapshotOwner = null;
        _pending.Clear();
        if (_hooked == this) {
            _hooked = null;
        }
    }

    // =====================================================================================================
    // Detección
    // =====================================================================================================

    /// <summary>Llamar cada frame.</summary>
    public void Update() {
        _active = IsActive();
        if (!_active) {
            return;
        }

        var playerData = PlayerData.instance;
        if (playerData != _snapshotOwner) {
            // Partida recién cargada: lo pendiente se aplica y lo actual pasa a ser la referencia (no se envía)
            _snapshotOwner = playerData;
            foreach (var change in _pending) {
                Apply(change);
            }

            _pending.Clear();
            TakeSnapshot(playerData);
            Log.Info($"Progreso compartido activo ({Describe()})");
            return;
        }

        if (Time.unscaledTime < _nextDiff) {
            return;
        }

        _nextDiff = Time.unscaledTime + DiffInterval;
        Diff(playerData);
    }

    private bool IsActive() {
        var session = _session;
        if (session == null || CoopSaves.ActiveWorld == null || HeroController.instance == null ||
            PlayerData.instance == null) {
            return false;
        }

        return session.IsHost ? CoopSaves.IsHostWorld : session.IsActive && !CoopSaves.IsHostWorld;
    }

    private void TakeSnapshot(PlayerData playerData) {
        if (_bools.Length != BoolGetters.Length) {
            _bools = new bool[BoolGetters.Length];
            _ints = new int[IntGetters.Length];
            _listCounts = new int[ListFields.Length];
        }

        for (var i = 0; i < BoolGetters.Length; i++) {
            _bools[i] = BoolGetters[i](playerData);
        }

        for (var i = 0; i < IntGetters.Length; i++) {
            _ints[i] = IntGetters[i](playerData);
        }

        for (var i = 0; i < ListFields.Length; i++) {
            _listCounts[i] = (ListFields[i].GetValue(playerData) as List<string>)?.Count ?? 0;
        }

        _knownActivated.Clear();
        _knownInts.Clear();
        _knownRocks.Clear();
        var scenes = SceneData.instance;
        foreach (var item in scenes.persistentBoolItems) {
            if (item.activated) {
                _knownActivated.Add(Key(item.sceneName, item.id));
            }
        }

        foreach (var item in scenes.persistentIntItems) {
            _knownInts[Key(item.sceneName, item.id)] = item.value;
        }

        foreach (var rock in scenes.geoRocks) {
            _knownRocks[Key(rock.sceneName, rock.id)] = rock.hitsLeft;
        }
    }

    private void Diff(PlayerData playerData) {
        for (var i = 0; i < BoolGetters.Length; i++) {
            var value = BoolGetters[i](playerData);
            if (value != _bools[i]) {
                _bools[i] = value;
                Send(new Change { Kind = Kind.Bool, Index = (ushort) i, Value = value ? 1 : 0 });
            }
        }

        for (var i = 0; i < IntGetters.Length; i++) {
            var value = IntGetters[i](playerData);
            if (value != _ints[i]) {
                var delta = value - _ints[i];
                _ints[i] = value;
                Send(AdditiveInts.Contains(IntFields[i].Name)
                    ? new Change { Kind = Kind.IntAdd, Index = (ushort) i, Value = delta }
                    : new Change { Kind = Kind.Int, Index = (ushort) i, Value = value });
            }
        }

        for (var i = 0; i < ListFields.Length; i++) {
            if (ListFields[i].GetValue(playerData) is not List<string> list) {
                continue;
            }

            if (list.Count < _listCounts[i]) {
                _listCounts[i] = list.Count;
                continue;
            }

            for (var j = _listCounts[i]; j < list.Count; j++) {
                Send(new Change { Kind = Kind.ListAdd, Index = (ushort) i, Id = list[j] ?? string.Empty });
            }

            _listCounts[i] = list.Count;
        }
    }

    private static void InstallHooks() {
        if (_hookInstalled) {
            return;
        }

        _hookInstalled = true;
        On.SceneData.SaveMyState_PersistentBoolData += (orig, self, data) => {
            _hooked?.KeepActivated(data);
            orig(self, data);
            _hooked?.OnSceneBool(data);
        };
        On.SceneData.SaveMyState_PersistentIntData += (orig, self, data) => {
            orig(self, data);
            _hooked?.OnSceneInt(data);
        };
        On.SceneData.SaveMyState_GeoRockData += (orig, self, data) => {
            _hooked?.KeepRockHits(data);
            orig(self, data);
            _hooked?.OnGeoRock(data);
        };
    }

    /// <summary>
    /// El objeto se guarda tal como está en esta sala, que puede ser anterior a lo que hizo otro jugador (entró antes de
    /// que llegara): lo ya activado en el mundo no vuelve atrás.
    /// </summary>
    private void KeepActivated(PersistentBoolData data) {
        if (!_applying && _active && data != null && !data.activated && !data.semiPersistent &&
            !CharacterData.IsIndividualItem(data.id) && _knownActivated.Contains(Key(data.sceneName, data.id))) {
            data.activated = true;
        }
    }

    /// <summary>Igual con las piedras de geo: no recuperan golpes que ya recibieron en otro PC.</summary>
    private void KeepRockHits(GeoRockData data) {
        if (!_applying && _active && data != null && _knownRocks.TryGetValue(Key(data.sceneName, data.id), out var known) &&
            data.hitsLeft > known) {
            data.hitsLeft = known;
        }
    }

    private static bool _hookInstalled;

    private void OnSceneBool(PersistentBoolData data) {
        // Lo que recoge cada uno (objetos, fragmentos, cofres...) no se comparte
        if (_applying || !_active || data.semiPersistent || !data.activated || CharacterData.IsIndividualItem(data.id)) {
            return;
        }

        if (_knownActivated.Add(Key(data.sceneName, data.id))) {
            Send(new Change { Kind = Kind.SceneBool, Scene = data.sceneName, Id = data.id, Value = 1 });
        }
    }

    private void OnSceneInt(PersistentIntData data) {
        if (_applying || !_active || data.semiPersistent) {
            return;
        }

        var key = Key(data.sceneName, data.id);
        if (!_knownInts.TryGetValue(key, out var known) || Replaces(data.id, data.value, known)) {
            _knownInts[key] = data.value;
            Send(new Change { Kind = Kind.SceneInt, Scene = data.sceneName, Id = data.id, Value = data.value });
        }
    }

    /// <summary>
    /// Si un número guardado de la sala sustituye al conocido. Solo hay dos tipos (`PersistentIntItem`): tótems de alma,
    /// cuyas cargas solo bajan (gana el menor), y ascensores de la Ciudad (`Ruins Lift`), cuyo número es el piso y sube y
    /// baja (gana el último cambio). Antes ganaba siempre el mayor: los ascensores no guardaban una bajada y quien entraba
    /// después los encontraba en otro piso.
    /// </summary>
    private static bool Replaces(string id, int value, int known) =>
        id != null && id.IndexOf("Totem", StringComparison.OrdinalIgnoreCase) >= 0 ? value < known : value != known;

    private void OnGeoRock(GeoRockData data) {
        if (_applying || !_active) {
            return;
        }

        var key = Key(data.sceneName, data.id);
        if (!_knownRocks.TryGetValue(key, out var known) || data.hitsLeft < known) {
            _knownRocks[key] = data.hitsLeft;
            Send(new Change { Kind = Kind.GeoRock, Scene = data.sceneName, Id = data.id, Value = data.hitsLeft });
        }
    }

    private static string Key(string scene, string id) => (scene ?? string.Empty) + "|" + id;

    // =====================================================================================================
    // Red
    // =====================================================================================================

    /// <summary>Un cambio local: al host (o, si somos el host, a todos).</summary>
    private void Send(in Change change) {
        if (_session == null) {
            return;
        }

        if (_session.IsHost) {
            Broadcast(change, null);
            return;
        }

        var writer = _session.Begin(WorldChange);
        Write(writer, change);
        _session.End();
    }

    /// <summary>El host envía un cambio a todos los jugadores remotos menos al que lo originó.</summary>
    private void Broadcast(in Change change, SessionPlayer except) {
        foreach (var player in _session.Players) {
            if (player.IsLocal || player == except) {
                continue;
            }

            var writer = _session.BeginTo(player, WorldChange);
            Write(writer, change);
            _session.EndTo();
        }
    }

    private static void Write(PacketWriter writer, in Change change) {
        writer.WriteByte((byte) change.Kind);
        switch (change.Kind) {
            case Kind.Bool:
            case Kind.Int:
            case Kind.IntAdd:
                writer.WriteUShort(change.Index);
                writer.WriteInt(change.Value);
                break;
            case Kind.ListAdd:
                writer.WriteUShort(change.Index);
                writer.WriteString(change.Id);
                break;
            default:
                writer.WriteString(change.Scene ?? string.Empty);
                writer.WriteString(change.Id ?? string.Empty);
                writer.WriteInt(change.Value);
                break;
        }
    }

    private static bool TryRead(PacketReader reader, out Change change) {
        change = new Change { Kind = (Kind) reader.ReadByte() };
        switch (change.Kind) {
            case Kind.Bool:
            case Kind.Int:
            case Kind.IntAdd:
                change.Index = reader.ReadUShort();
                change.Value = reader.ReadInt();
                return change.Kind == Kind.Bool ? change.Index < BoolFields.Length : change.Index < IntFields.Length;
            case Kind.ListAdd:
                change.Index = reader.ReadUShort();
                change.Id = reader.ReadString(256);
                return change.Index < ListFields.Length;
            case Kind.SceneBool:
            case Kind.SceneInt:
            case Kind.GeoRock:
                change.Scene = reader.ReadString(128);
                change.Id = reader.ReadString(256);
                change.Value = reader.ReadInt();
                return true;
            default:
                return false;
        }
    }

    private void OnChange(SessionPlayer from, PacketReader reader) {
        if (!TryRead(reader, out var change)) {
            Log.Warn($"Cambio del mundo no válido de {from.Name}");
            return;
        }

        if (_session.IsHost) {
            // Solo se acepta si el host está en su mundo; luego se reenvía a los demás
            if (!CoopSaves.IsHostWorld || CoopSaves.ActiveWorld == null) {
                return;
            }

            Broadcast(change, from);
        }

        if (_active && PlayerData.instance == _snapshotOwner) {
            Apply(change);
        } else {
            _pending.Add(change);
        }
    }

    /// <summary>Aplica un cambio recibido y lo apunta como ya conocido (para no reenviarlo).</summary>
    private void Apply(in Change change) {
        var playerData = PlayerData.instance;
        _applying = true;
        try {
            switch (change.Kind) {
                case Kind.Bool:
                    var flag = change.Value != 0;
                    var wasSet = BoolGetters[change.Index](playerData);
                    BoolSetters[change.Index](playerData, flag);
                    if (_bools.Length > change.Index) {
                        _bools[change.Index] = flag;
                    }

                    if (wasSet != flag) {
                        RemoteFieldChanged?.Invoke(BoolFields[change.Index].Name, wasSet ? 1 : 0, true);
                    }

                    break;
                case Kind.Int:
                    var previous = IntGetters[change.Index](playerData);
                    IntSetters[change.Index](playerData, change.Value);
                    if (_ints.Length > change.Index) {
                        _ints[change.Index] = change.Value;
                    }

                    if (previous != change.Value) {
                        RemoteFieldChanged?.Invoke(IntFields[change.Index].Name, previous, false);
                    }

                    break;
                case Kind.IntAdd: {
                    var before = IntGetters[change.Index](playerData);
                    var after = before + change.Value;
                    IntSetters[change.Index](playerData, after);
                    // Lo local que aún no se comparó sigue pendiente: se apunta solo la suma recibida
                    if (_ints.Length > change.Index) {
                        _ints[change.Index] += change.Value;
                    }

                    RemoteFieldChanged?.Invoke(IntFields[change.Index].Name, before, false);
                    break;
                }
                case Kind.ListAdd:
                    if (ListFields[change.Index].GetValue(playerData) is List<string> list && !list.Contains(change.Id)) {
                        var before = list.Count;
                        list.Add(change.Id);
                        // Solo si no había nada local sin enviar: si este PC añadió algo hace menos de 0,2 s (aún sin comparar),
                        // la cuenta se queda y la comparación lo envía (quien lo recibe quita los repetidos). Antes la cuenta
                        // saltaba al final y eso local no se enviaba nunca
                        if (_listCounts.Length > change.Index && _listCounts[change.Index] == before) {
                            _listCounts[change.Index] = list.Count;
                        }
                    }

                    break;
                case Kind.SceneBool:
                    if (CharacterData.IsIndividualItem(change.Id)) {
                        break;
                    }

                    _knownActivated.Add(Key(change.Scene, change.Id));
                    SceneData.instance.SaveMyState(new PersistentBoolData {
                        sceneName = change.Scene, id = change.Id, activated = true, semiPersistent = false
                    });
                    break;
                case Kind.SceneInt: {
                    var key = Key(change.Scene, change.Id);
                    if (!_knownInts.TryGetValue(key, out var known) || Replaces(change.Id, change.Value, known)) {
                        _knownInts[key] = change.Value;
                        SceneData.instance.SaveMyState(new PersistentIntData {
                            sceneName = change.Scene, id = change.Id, value = change.Value, semiPersistent = false
                        });
                    }

                    break;
                }
                case Kind.GeoRock: {
                    var key = Key(change.Scene, change.Id);
                    if (!_knownRocks.TryGetValue(key, out var known) || change.Value < known) {
                        _knownRocks[key] = change.Value;
                        SceneData.instance.SaveMyState(new GeoRockData {
                            sceneName = change.Scene, id = change.Id, hitsLeft = change.Value
                        });
                    }

                    break;
                }
            }
        } finally {
            _applying = false;
        }
    }

    /// <summary>Nombre de un campo compartido (pruebas y registro).</summary>
    public static int IndexOfBool(string name) => Array.FindIndex(BoolFields, f => f.Name == name);
}
