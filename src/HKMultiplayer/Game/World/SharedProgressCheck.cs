using System;
using System.Collections.Generic;
using System.Text;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.World;

/// <summary>
/// Comprobación del mundo compartido: el host envía cada <see cref="DigestInterval"/> un resumen (una suma por trozo de
/// los datos compartidos de la partida y de las salas). Quien ve un trozo distinto dos veces seguidas (una sola puede ser
/// un cambio aún de camino) pide ese trozo entero y lo corrige: en la partida manda el host; en las salas, lo que tenga
/// cualquiera de los dos (lo abierto, roto o activado no vuelve atrás) y lo que solo tiene este PC se envía al host.
/// Cada corrección queda en el registro con el dato y los dos valores, y el jugador ve un aviso corto.
/// </summary>
internal sealed partial class SharedProgress {
    /// <summary>Resumen del mundo (host → todos).</summary>
    public const MessageType WorldDigest = (MessageType) 128;

    /// <summary>Trozos del resumen que no coinciden (jugador → host).</summary>
    public const MessageType WorldDetailRequest = (MessageType) 129;

    /// <summary>Un trozo entero (host → quien lo pidió).</summary>
    public const MessageType WorldDetail = (MessageType) 130;

    private const float DigestInterval = 15f;
    private const int BoolChunk = 64;
    private const int IntChunk = 32;
    private const int SceneBuckets = 16;

    /// <summary>Trozos que se piden de una vez como mucho.</summary>
    private const int MaxRequested = 8;

    /// <summary>Espacio que se deja libre al llenar un mensaje de un trozo.</summary>
    private const int DetailReserve = 600;

    private const float NoticeInterval = 300f;

    private enum Section : byte {
        Bools = 0,
        Ints = 1,
        List = 2,
        Scenes = 3
    }

    private enum SceneEntry : byte {
        Bool = 0,
        Int = 1,
        Rock = 2
    }

    private float _nextDigest;
    private float _nextNotice;

    /// <summary>Trozos que ya no coincidían en el resumen anterior (al segundo se piden).</summary>
    private readonly HashSet<int> _suspect = new();

    private readonly HashSet<int> _mismatched = new();

    /// <summary>Lo que va llegando de un trozo de lista o de salas hasta su último mensaje.</summary>
    private readonly Dictionary<int, List<(SceneEntry Kind, string Scene, string Id, int Value)>> _incomingDetail = new();

    /// <summary>Datos corregidos desde que se abrió el juego (para el diagnóstico).</summary>
    public static int Corrections { get; private set; }

    private static int SectionKey(Section section, int index) => (int) section << 16 | index;

    private void AttachCheck(GameSession session) {
        session.On(WorldDigest, OnDigest);
        session.On(WorldDetailRequest, OnDetailRequest);
        session.On(WorldDetail, OnDetail);
        _suspect.Clear();
        _incomingDetail.Clear();
        _nextDigest = Time.unscaledTime + DigestInterval;
    }

    private void DetachCheck() {
        _session?.On(WorldDigest, null);
        _session?.On(WorldDetailRequest, null);
        _session?.On(WorldDetail, null);
    }

    private static void RegisterCheckRoutes() {
        MessageRoutes.Register(WorldDigest, Route.Everyone, reliable: true);
        MessageRoutes.Register(WorldDetailRequest, Route.Server, reliable: true);
        MessageRoutes.Register(WorldDetail, Route.Server, reliable: true);
    }

    // =====================================================================================================
    // Sumas
    // =====================================================================================================

    private static uint Mix(uint hash, int value) {
        unchecked {
            for (var i = 0; i < 4; i++) {
                hash = (hash ^ (byte) (value >> (8 * i))) * 16777619u;
            }
        }

        return hash;
    }

    private static uint HashText(string text) {
        var hash = 2166136261u;
        foreach (var c in text ?? string.Empty) {
            hash = Mix(hash, c);
        }

        return hash;
    }

    private static uint HashBools(PlayerData playerData, int chunk) {
        var hash = 2166136261u;
        for (var i = chunk * BoolChunk; i < Math.Min(BoolGetters.Length, (chunk + 1) * BoolChunk); i++) {
            hash = Mix(hash, BoolGetters[i](playerData) ? 1 : 0);
        }

        return hash;
    }

    private static uint HashInts(PlayerData playerData, int chunk) {
        var hash = 2166136261u;
        for (var i = chunk * IntChunk; i < Math.Min(IntGetters.Length, (chunk + 1) * IntChunk); i++) {
            hash = Mix(hash, IntGetters[i](playerData));
        }

        return hash;
    }

    /// <summary>Si la lista cuenta en la comparación (las del mapa, solo con el mapa compartido).</summary>
    private static bool ListShared(int index) => !IsMapList(index) || Features.HostSettings.Current.SharedMap;

    /// <summary>Suma de una lista sin importar el orden ni los repetidos (cada PC la llena en su orden).</summary>
    private static uint HashList(PlayerData playerData, int index) {
        uint sum = 0;
        if (ListFields[index].GetValue(playerData) is List<string> list) {
            foreach (var item in new HashSet<string>(list)) {
                unchecked {
                    sum += HashText(item);
                }
            }
        }

        return sum;
    }

    private static int BucketOf(string scene) => (int) (HashText(scene) % SceneBuckets);

    /// <summary>Lo compartido de las salas de un grupo (el de <see cref="BucketOf"/>), como en <see cref="SharedProgress"/>.</summary>
    private static IEnumerable<(SceneEntry Kind, string Scene, string Id, int Value)> SceneEntries(int bucket) {
        var scenes = SceneData.instance;
        if (scenes == null) {
            yield break;
        }

        foreach (var item in scenes.persistentBoolItems) {
            if (item.activated && !item.semiPersistent && !CharacterData.IsIndividualItem(item.id) && BucketOf(item.sceneName) == bucket) {
                yield return (SceneEntry.Bool, item.sceneName, item.id, 1);
            }
        }

        foreach (var item in scenes.persistentIntItems) {
            if (!item.semiPersistent && BucketOf(item.sceneName) == bucket) {
                yield return (SceneEntry.Int, item.sceneName, item.id, item.value);
            }
        }

        foreach (var rock in scenes.geoRocks) {
            if (BucketOf(rock.sceneName) == bucket) {
                yield return (SceneEntry.Rock, rock.sceneName, rock.id, rock.hitsLeft);
            }
        }
    }

    private static uint HashScenes(int bucket) {
        uint sum = 0;
        foreach (var (kind, scene, id, value) in SceneEntries(bucket)) {
            unchecked {
                sum += Mix(HashText((byte) kind + "|" + scene + "|" + id), value);
            }
        }

        return sum;
    }

    private static IEnumerable<(Section Section, int Index)> AllSections() {
        for (var i = 0; i * BoolChunk < BoolGetters.Length; i++) {
            yield return (Section.Bools, i);
        }

        for (var i = 0; i * IntChunk < IntGetters.Length; i++) {
            yield return (Section.Ints, i);
        }

        for (var i = 0; i < ListFields.Length; i++) {
            if (ListShared(i)) {
                yield return (Section.List, i);
            }
        }

        for (var i = 0; i < SceneBuckets; i++) {
            yield return (Section.Scenes, i);
        }
    }

    private static uint HashOf(PlayerData playerData, Section section, int index) => section switch {
        Section.Bools => HashBools(playerData, index),
        Section.Ints => HashInts(playerData, index),
        Section.List => HashList(playerData, index),
        _ => HashScenes(index)
    };

    // =====================================================================================================
    // Host: resumen y trozos
    // =====================================================================================================

    /// <summary>Llamar cada frame con el progreso activo.</summary>
    private void UpdateCheck(PlayerData playerData) {
        if (!_session.IsHost || Time.unscaledTime < _nextDigest || !_session.CanSend) {
            return;
        }

        _nextDigest = Time.unscaledTime + DigestInterval;
        var others = false;
        foreach (var player in _session.Players) {
            others |= !player.IsLocal;
        }

        if (!others) {
            return;
        }

        // Lo que el host acaba de cambiar sale antes que el resumen que ya lo cuenta
        Diff(playerData);
        var sections = new List<(Section, int)>(AllSections());
        var writer = _session.Begin(WorldDigest);
        writer.WriteUShort((ushort) sections.Count);
        foreach (var (section, index) in sections) {
            writer.WriteByte((byte) section);
            writer.WriteUShort((ushort) index);
            writer.WriteUInt(HashOf(playerData, section, index));
        }

        _session.End();
    }

    private void OnDetailRequest(SessionPlayer from, PacketReader reader) {
        var playerData = PlayerData.instance;
        if (!_session.IsHost || !_active || playerData != _snapshotOwner) {
            return;
        }

        var count = Math.Min((int) reader.ReadByte(), MaxRequested);
        for (var n = 0; n < count; n++) {
            var section = (Section) reader.ReadByte();
            var index = reader.ReadUShort();
            SendDetail(from, playerData, section, index);
        }
    }

    private void SendDetail(SessionPlayer to, PlayerData playerData, Section section, int index) {
        switch (section) {
            case Section.Bools when index * BoolChunk < BoolGetters.Length: {
                ulong bits = 0;
                for (var i = 0; i < BoolChunk && index * BoolChunk + i < BoolGetters.Length; i++) {
                    if (BoolGetters[index * BoolChunk + i](playerData)) {
                        bits |= 1UL << i;
                    }
                }

                var writer = _session.BeginTo(to, WorldDetail);
                writer.WriteByte((byte) section);
                writer.WriteUShort((ushort) index);
                writer.WriteULong(bits);
                _session.EndTo();
                return;
            }
            case Section.Ints when index * IntChunk < IntGetters.Length: {
                var start = index * IntChunk;
                var count = Math.Min(IntChunk, IntGetters.Length - start);
                var writer = _session.BeginTo(to, WorldDetail);
                writer.WriteByte((byte) section);
                writer.WriteUShort((ushort) index);
                writer.WriteByte((byte) count);
                for (var i = 0; i < count; i++) {
                    writer.WriteInt(IntGetters[start + i](playerData));
                }

                _session.EndTo();
                return;
            }
            case Section.List when index < ListFields.Length && ListShared(index): {
                var items = new List<(SceneEntry, string, string, int)>();
                if (ListFields[index].GetValue(playerData) is List<string> list) {
                    foreach (var item in new HashSet<string>(list)) {
                        items.Add((SceneEntry.Bool, string.Empty, item ?? string.Empty, 0));
                    }
                }

                SendEntries(to, section, index, items);
                return;
            }
            case Section.Scenes when index < SceneBuckets:
                SendEntries(to, section, index, new List<(SceneEntry, string, string, int)>(SceneEntries(index)));
                return;
        }
    }

    /// <summary>Un trozo largo, en varios mensajes; el último lo marca para que quien lo recibe lo compare entero.</summary>
    private void SendEntries(SessionPlayer to, Section section, int index, List<(SceneEntry Kind, string Scene, string Id, int Value)> entries) {
        var next = 0;
        do {
            var writer = _session.BeginTo(to, WorldDetail);
            writer.WriteByte((byte) section);
            writer.WriteUShort((ushort) index);
            var countAt = writer.Position;
            writer.WriteByte(0);
            writer.WriteBool(false);
            var count = 0;
            while (next < entries.Count && count < byte.MaxValue && (count == 0 || writer.Remaining > DetailReserve)) {
                var (kind, scene, id, value) = entries[next++];
                writer.WriteByte((byte) kind);
                writer.WriteString(scene);
                writer.WriteString(id);
                writer.WriteInt(value);
                count++;
            }

            var end = writer.Position;
            writer.Seek(countAt);
            writer.WriteByte((byte) count);
            writer.WriteBool(next >= entries.Count);
            writer.Seek(end);
            _session.EndTo();
        } while (next < entries.Count);
    }

    // =====================================================================================================
    // Jugador: comparar y corregir
    // =====================================================================================================

    private void OnDigest(SessionPlayer from, PacketReader reader) {
        var playerData = PlayerData.instance;
        if (_session.IsHost || !_active || playerData != _snapshotOwner || _pending.Count > 0) {
            return;
        }

        // Lo que este PC cambió y aún no envió no cuenta como diferencia: sale ya
        Diff(playerData);
        _mismatched.Clear();
        var count = reader.ReadUShort();
        for (var n = 0; n < count; n++) {
            var section = (Section) reader.ReadByte();
            var index = reader.ReadUShort();
            var hash = reader.ReadUInt();
            var valid = section switch {
                Section.Bools => index * BoolChunk < BoolGetters.Length,
                Section.Ints => index * IntChunk < IntGetters.Length,
                Section.List => index < ListFields.Length && ListShared(index),
                Section.Scenes => index < SceneBuckets,
                _ => false
            };
            if (valid && HashOf(playerData, section, index) != hash) {
                _mismatched.Add(SectionKey(section, index));
            }
        }

        var requested = new List<int>();
        foreach (var key in _mismatched) {
            if (_suspect.Contains(key) && requested.Count < MaxRequested) {
                requested.Add(key);
            }
        }

        _suspect.Clear();
        _suspect.UnionWith(_mismatched);
        if (requested.Count == 0 || !_session.CanSend) {
            return;
        }

        var writer = _session.Begin(WorldDetailRequest);
        writer.WriteByte((byte) requested.Count);
        foreach (var key in requested) {
            writer.WriteByte((byte) (key >> 16));
            writer.WriteUShort((ushort) (key & 0xffff));
            _suspect.Remove(key);
        }

        _session.End();
    }

    private void OnDetail(SessionPlayer from, PacketReader reader) {
        var playerData = PlayerData.instance;
        if (_session.IsHost || from.Id != GameSession.HostPlayerId || !_active || playerData != _snapshotOwner) {
            return;
        }

        var section = (Section) reader.ReadByte();
        var index = reader.ReadUShort();
        var fixes = new StringBuilder();
        var fixedCount = 0;
        switch (section) {
            case Section.Bools when index * BoolChunk < BoolGetters.Length: {
                var bits = reader.ReadULong();
                for (var i = 0; i < BoolChunk && index * BoolChunk + i < BoolGetters.Length; i++) {
                    var field = index * BoolChunk + i;
                    var host = (bits & (1UL << i)) != 0;
                    var here = BoolGetters[field](playerData);
                    if (host != here) {
                        Note(fixes, ref fixedCount, $"{BoolFields[field].Name} host={host} aquí={here}");
                        Apply(new Change { Kind = Kind.Bool, Index = (ushort) field, Value = host ? 1 : 0, Origin = from.Id });
                    }
                }

                break;
            }
            case Section.Ints when index * IntChunk < IntGetters.Length: {
                var count = reader.ReadByte();
                for (var i = 0; i < count && index * IntChunk + i < IntGetters.Length; i++) {
                    var field = index * IntChunk + i;
                    var host = reader.ReadInt();
                    var here = IntGetters[field](playerData);
                    if (host != here) {
                        Note(fixes, ref fixedCount, $"{IntFields[field].Name} host={host} aquí={here}");
                        Apply(new Change { Kind = Kind.Int, Index = (ushort) field, Value = host, Origin = from.Id });
                    }
                }

                break;
            }
            case Section.List when index < ListFields.Length:
            case Section.Scenes when index < SceneBuckets: {
                var key = SectionKey(section, index);
                if (!_incomingDetail.TryGetValue(key, out var entries)) {
                    entries = new List<(SceneEntry, string, string, int)>();
                    _incomingDetail[key] = entries;
                }

                var count = reader.ReadByte();
                var last = reader.ReadBool();
                for (var i = 0; i < count; i++) {
                    var kind = (SceneEntry) reader.ReadByte();
                    var scene = reader.ReadString(128);
                    var id = reader.ReadString(256);
                    var value = reader.ReadInt();
                    if (entries.Count < 20000) {
                        entries.Add((kind, scene, id, value));
                    }
                }

                if (!last) {
                    return;
                }

                _incomingDetail.Remove(key);
                if (section == Section.List) {
                    FixList(playerData, index, entries, from.Id, fixes, ref fixedCount);
                } else {
                    FixScenes(index, entries, from.Id, fixes, ref fixedCount);
                }

                break;
            }
            default:
                return;
        }

        if (fixedCount == 0) {
            return;
        }

        Corrections += fixedCount;
        Log.Warn($"Desincronizado con el host: {fixedCount} dato(s) corregidos: {fixes}");
        if (Time.unscaledTime >= _nextNotice) {
            _nextNotice = Time.unscaledTime + NoticeInterval;
            Notice.Show(Lang.T("Tu partida no coincidía con la del host: corregida", "Your game didn't match the host's: fixed"), 5f);
        }
    }

    /// <summary>
    /// Cambia un dato compartido solo en este PC, sin enviarlo (como un aviso perdido), para ver que el resumen lo
    /// encuentra y lo corrige. Devuelve si existía.
    /// </summary>
    public bool TestDesync(string field, int value) {
        var playerData = PlayerData.instance;
        // "sala|id": un objeto de sala activado solo aquí
        var cut = field.IndexOf('|');
        if (cut > 0 && SceneData.instance != null) {
            _applying = true;
            try {
                SceneData.instance.SaveMyState(new PersistentBoolData {
                    sceneName = field.Substring(0, cut), id = field.Substring(cut + 1), activated = value != 0, semiPersistent = false
                });
            } finally {
                _applying = false;
            }

            return true;
        }

        var index = Array.FindIndex(BoolFields, f => f.Name == field);
        if (playerData != null && index >= 0) {
            BoolSetters[index](playerData, value != 0);
            if (index < _bools.Length) {
                _bools[index] = value != 0;
            }

            return true;
        }

        index = Array.FindIndex(IntFields, f => f.Name == field);
        if (playerData != null && index >= 0) {
            IntSetters[index](playerData, value);
            if (index < _ints.Length) {
                _ints[index] = value;
            }

            return true;
        }

        return false;
    }

    private static void Note(StringBuilder fixes, ref int count, string text) {
        count++;
        if (count <= 12) {
            fixes.Append(count > 1 ? "; " : string.Empty).Append(text);
        } else if (count == 13) {
            fixes.Append("; ...");
        }
    }

    private void FixList(PlayerData playerData, int index, List<(SceneEntry Kind, string Scene, string Id, int Value)> entries,
        ushort host, StringBuilder fixes, ref int count) {
        if (ListFields[index].GetValue(playerData) is not List<string> list) {
            return;
        }

        var name = ListFields[index].Name;
        var hostItems = new HashSet<string>();
        foreach (var entry in entries) {
            hostItems.Add(entry.Id);
        }

        var here = new HashSet<string>(list);
        foreach (var item in hostItems) {
            if (!here.Contains(item)) {
                Note(fixes, ref count, $"{name} sin {item}");
                Apply(new Change { Kind = Kind.ListAdd, Index = (ushort) index, Id = item, Origin = host });
            }
        }

        // Lo que solo tiene este PC: la lista es la unión de lo de todos, así que va al host
        foreach (var item in here) {
            if (!hostItems.Contains(item) && item != null) {
                Note(fixes, ref count, $"{name} con {item} que el host no tenía");
                Send(new Change { Kind = Kind.ListAdd, Index = (ushort) index, Id = item });
            }
        }
    }

    private void FixScenes(int bucket, List<(SceneEntry Kind, string Scene, string Id, int Value)> entries, ushort host,
        StringBuilder fixes, ref int count) {
        var hostEntries = new Dictionary<string, (SceneEntry Kind, string Scene, string Id, int Value)>();
        foreach (var entry in entries) {
            hostEntries[(byte) entry.Kind + "|" + entry.Scene + "|" + entry.Id] = entry;
        }

        var here = new Dictionary<string, (SceneEntry Kind, string Scene, string Id, int Value)>();
        foreach (var entry in SceneEntries(bucket)) {
            here[(byte) entry.Kind + "|" + entry.Scene + "|" + entry.Id] = entry;
        }

        foreach (var pair in hostEntries) {
            var (kind, scene, id, value) = pair.Value;
            var known = here.TryGetValue(pair.Key, out var mine);
            switch (kind) {
                case SceneEntry.Bool when !known:
                    Note(fixes, ref count, $"{scene}/{id} sin activar aquí");
                    Apply(new Change { Kind = Kind.SceneBool, Scene = scene, Id = id, Value = 1, Origin = host });
                    break;
                case SceneEntry.Int when !known || mine.Value != value:
                    Note(fixes, ref count, $"{scene}/{id} host={value} aquí={(known ? mine.Value.ToString() : "-")}");
                    _knownInts.Remove(Key(scene, id));
                    Apply(new Change { Kind = Kind.SceneInt, Scene = scene, Id = id, Value = value, Origin = host });
                    break;
                case SceneEntry.Rock when !known || mine.Value > value:
                    Note(fixes, ref count, $"{scene}/{id} golpes host={value} aquí={(known ? mine.Value.ToString() : "-")}");
                    _knownRocks.Remove(Key(scene, id));
                    Apply(new Change { Kind = Kind.GeoRock, Scene = scene, Id = id, Value = value, Origin = host });
                    break;
            }
        }

        // Lo que solo está aquí (activado, o una piedra con menos golpes): se envía al host
        foreach (var pair in here) {
            var (kind, scene, id, value) = pair.Value;
            var hostHas = hostEntries.TryGetValue(pair.Key, out var theirs);
            if (kind == SceneEntry.Bool && !hostHas) {
                Note(fixes, ref count, $"{scene}/{id} activado aquí y no en el host");
                Send(new Change { Kind = Kind.SceneBool, Scene = scene, Id = id, Value = 1 });
            } else if (kind == SceneEntry.Rock && (!hostHas || theirs.Value > value)) {
                Note(fixes, ref count, $"{scene}/{id} golpes aquí={value} host={(hostHas ? theirs.Value.ToString() : "-")}");
                Send(new Change { Kind = Kind.GeoRock, Scene = scene, Id = id, Value = value });
            }
        }
    }
}
