using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Acompañantes que atacan solos (crías de Glowing Womb, tejedoras de Weaversong, Grimmchild y sus bolas de fuego):
/// qué prefabs son, qué partes con sprite tienen y la numeración de sus colecciones de sprites, igual en todos los PC
/// (se recorren los mismos prefabs en el mismo orden). En el PC del dueño son los del juego, con su lógica y su daño;
/// en los demás, copias solo visuales que copian lo que se ve de cada uno (<see cref="LocalCompanions"/>,
/// <see cref="RemoteCompanions"/>).
/// </summary>
internal sealed class CompanionTemplates {
    /// <summary>Partes con sprite de un acompañante que se envían (la raíz y sus hijos con sprite, en su orden).</summary>
    public const int MaxParts = 6;

    internal sealed class Kind {
        public GameObject Prefab;

        /// <summary>Copia solo visual (sin lógica, colisiones, sonidos ni partículas, y sin animar: el sprite llega).</summary>
        public GameObject Template;

        /// <summary>Ruta de cada parte con sprite desde la raíz ("" la raíz).</summary>
        public string[] PartPaths;
    }

    public readonly List<Kind> Kinds = new();
    private readonly Dictionary<GameObject, int> _kindByPrefab = new();
    private readonly List<tk2dSpriteCollectionData> _collections = new();
    private readonly Dictionary<tk2dSpriteCollectionData, byte> _collectionIndex = new();
    private readonly Dictionary<string, byte> _collectionByName = new();

    /// <summary>FSM de los amuletos del caballero que sacan a sus acompañantes del pool.</summary>
    private static readonly string[] SpawnerFsms = {
        "Hatchling Spawn", "Weaverling Control", "Spawn Grimmchild",
        // Dreamshield: el escudo que gira alrededor del caballero (su sprite y su giro llegan como los de los demás)
        OrbitShieldSpawner
    };

    public CompanionTemplates(HeroController hero, Transform holder) {
        var charmEffects = hero.transform.Find("Charm Effects");
        if (charmEffects != null) {
            foreach (var fsmName in SpawnerFsms) {
                AddSpawnsOf(FSMUtility.LocateFSM(charmEffects.gameObject, fsmName), holder, depth: 0);
            }
        }

        Log.Info($"Acompañantes del compañero: {Kinds.Count} tipos, {_collections.Count} colecciones de sprites");
    }

    /// <summary>Lo que saca una FSM del pool (y lo que sacan esos, como las bolas de fuego de Grimmchild).</summary>
    private void AddSpawnsOf(PlayMakerFSM fsm, Transform holder, int depth) {
        if (fsm == null) {
            return;
        }

        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                var prefab = action is SpawnObjectFromGlobalPool spawn ? spawn.gameObject?.Value : null;
                if (prefab == null || _kindByPrefab.ContainsKey(prefab) || prefab.GetComponentInChildren<tk2dSprite>(true) == null) {
                    continue;
                }

                // El escudo de Dreamshield conserva su brillo (partículas que deja al girar); en los demás saldría el humo
                // de Defender's Crest también sin él
                AddKind(prefab, holder, keepParticles: depth == 0 && fsm.FsmName == OrbitShieldSpawner);
                if (depth == 0) {
                    foreach (var child in prefab.GetComponents<PlayMakerFSM>()) {
                        AddSpawnsOf(child, holder, depth + 1);
                    }
                }
            }
        }
    }

    private const string OrbitShieldSpawner = "Spawn Orbit Shield";

    private void AddKind(GameObject prefab, Transform holder, bool keepParticles) {
        var sprites = prefab.GetComponentsInChildren<tk2dSprite>(true);
        var count = Mathf.Min(sprites.Length, MaxParts);
        var paths = new string[count];
        for (var i = 0; i < count; i++) {
            paths[i] = PathOf(sprites[i].transform, prefab.transform);
            AddCollection(sprites[i].Collection);
            var animator = sprites[i].GetComponent<tk2dSpriteAnimator>();
            if (animator != null && animator.Library != null) {
                foreach (var clip in animator.Library.clips) {
                    if (clip?.frames == null) {
                        continue;
                    }

                    foreach (var frame in clip.frames) {
                        AddCollection(frame?.spriteCollection);
                    }
                }
            }
        }

        var template = Object.Instantiate(prefab, holder, false);
        template.name = prefab.name;
        HeroEffectTemplates.Strip(template, keepFsms: false);
        // Solo sus sprites: sin animarse solo (el sprite llega), sin sonidos ni partículas (el humo de Defender's
        // Crest saldría también sin él). Primero lo que depende del animador.
        foreach (var behaviour in template.GetComponentsInChildren<MonoBehaviour>(true)) {
            if (behaviour is not (tk2dBaseSprite or tk2dSpriteAnimator)) {
                Object.DestroyImmediate(behaviour);
            }
        }

        foreach (var component in template.GetComponentsInChildren<Component>(true)) {
            if (component is tk2dSpriteAnimator or AudioSource or Animator or SpriteRenderer ||
                component is ParticleSystemRenderer && !keepParticles) {
                Object.DestroyImmediate(component);
            }
        }

        if (!keepParticles) {
            foreach (var particles in template.GetComponentsInChildren<ParticleSystem>(true)) {
                Object.DestroyImmediate(particles);
            }
        }

        _kindByPrefab[prefab] = Kinds.Count;
        Kinds.Add(new Kind { Prefab = prefab, Template = template, PartPaths = paths });
    }

    private void AddCollection(tk2dSpriteCollectionData collection) {
        if (collection == null || _collectionIndex.ContainsKey(collection) || _collections.Count >= byte.MaxValue) {
            return;
        }

        var index = (byte) _collections.Count;
        _collections.Add(collection);
        _collectionIndex[collection] = index;
        _collectionByName[collection.spriteCollectionName ?? string.Empty] = index;
    }

    private static string PathOf(Transform node, Transform root) {
        var path = string.Empty;
        for (var t = node; t != null && t != root; t = t.parent) {
            path = path.Length == 0 ? t.name : t.name + "/" + path;
        }

        return path;
    }

    public bool TryGetKind(GameObject prefab, out int kind) => _kindByPrefab.TryGetValue(prefab, out kind);

    /// <summary>Número de una colección (255 si no es de los acompañantes).</summary>
    public byte IndexOf(tk2dSpriteCollectionData collection) {
        if (collection == null) {
            return byte.MaxValue;
        }

        if (_collectionIndex.TryGetValue(collection, out var index)) {
            return index;
        }

        // Otra instancia de la misma colección (se apunta para no buscar por nombre otra vez)
        if (!_collectionByName.TryGetValue(collection.spriteCollectionName ?? string.Empty, out index)) {
            index = byte.MaxValue;
        }

        _collectionIndex[collection] = index;
        return index;
    }

    public tk2dSpriteCollectionData Collection(byte index) => index < _collections.Count ? _collections[index] : null;
}

/// <summary>Lo que se ve de un acompañante en un momento: dónde está y qué sprite muestra cada parte.</summary>
internal struct CompanionSample {
    public byte Slot;

    /// <summary>Cambia cuando el hueco pasa a otro acompañante (no se interpola desde el anterior).</summary>
    public byte Generation;

    public byte Kind;
    public Vector3 Position;
    public float Rotation;
    public Vector2 Scale;

    /// <summary>Qué partes se ven (bit por parte).</summary>
    public byte Visible;

    public byte Collection0, Collection1, Collection2, Collection3, Collection4, Collection5;
    public ushort Sprite0, Sprite1, Sprite2, Sprite3, Sprite4, Sprite5;

    public void SetPart(int part, byte collection, ushort sprite) {
        switch (part) {
            case 0: Collection0 = collection; Sprite0 = sprite; break;
            case 1: Collection1 = collection; Sprite1 = sprite; break;
            case 2: Collection2 = collection; Sprite2 = sprite; break;
            case 3: Collection3 = collection; Sprite3 = sprite; break;
            case 4: Collection4 = collection; Sprite4 = sprite; break;
            case 5: Collection5 = collection; Sprite5 = sprite; break;
        }
    }

    public void GetPart(int part, out byte collection, out ushort sprite) {
        switch (part) {
            case 0: collection = Collection0; sprite = Sprite0; break;
            case 1: collection = Collection1; sprite = Sprite1; break;
            case 2: collection = Collection2; sprite = Sprite2; break;
            case 3: collection = Collection3; sprite = Sprite3; break;
            case 4: collection = Collection4; sprite = Sprite4; break;
            default: collection = Collection5; sprite = Sprite5; break;
        }
    }

    public void Write(PacketWriter writer) {
        writer.WriteByte(Slot);
        writer.WriteByte(Generation);
        writer.WriteByte(Kind);
        writer.WriteFloat(Position.x);
        writer.WriteFloat(Position.y);
        writer.WriteFloat(Position.z);
        writer.WriteShort((short) Mathf.RoundToInt(Mathf.Repeat(Rotation, 360f) * 100f - 18000f));
        writer.WriteShort((short) Mathf.Clamp(Mathf.RoundToInt(Scale.x * 1000f), short.MinValue, short.MaxValue));
        writer.WriteShort((short) Mathf.Clamp(Mathf.RoundToInt(Scale.y * 1000f), short.MinValue, short.MaxValue));
        writer.WriteByte(Visible);
        for (var part = 0; part < CompanionTemplates.MaxParts; part++) {
            if ((Visible & (1 << part)) == 0) {
                continue;
            }

            GetPart(part, out var collection, out var sprite);
            writer.WriteByte(collection);
            writer.WriteUShort(sprite);
        }
    }

    public void Read(PacketReader reader) {
        Slot = reader.ReadByte();
        Generation = reader.ReadByte();
        Kind = reader.ReadByte();
        Position = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
        Rotation = (reader.ReadShort() + 18000f) / 100f;
        Scale = new Vector2(reader.ReadShort() / 1000f, reader.ReadShort() / 1000f);
        Visible = reader.ReadByte();
        for (var part = 0; part < CompanionTemplates.MaxParts; part++) {
            if ((Visible & (1 << part)) != 0) {
                SetPart(part, reader.ReadByte(), reader.ReadUShort());
            }
        }
    }
}

/// <summary>
/// Acompañantes del caballero local: se apuntan al salir del pool del juego y se envían a la sala unas 20 veces por
/// segundo (no fiable, solo el último vale). Sin memoria nueva por envío.
/// </summary>
internal sealed class LocalCompanions {
    public const int MaxSlots = 12;

    /// <summary>Ticks entre envíos (a 60 Hz: 20 por segundo).</summary>
    private const int SendEveryTicks = 3;

    /// <summary>Envíos vacíos tras quedarse sin acompañantes (por si se pierde alguno: el otro los apaga).</summary>
    private const int EmptyRepeats = 3;

    private readonly GameObject[] _objects = new GameObject[MaxSlots];
    private readonly byte[] _kinds = new byte[MaxSlots];
    private readonly byte[] _generations = new byte[MaxSlots];
    private readonly tk2dSprite[][] _parts = new tk2dSprite[MaxSlots][];
    private readonly MeshRenderer[][] _renderers = new MeshRenderer[MaxSlots][];
    private readonly List<GameObject> _found = new();
    private CompanionTemplates _templates;
    private uint _lastSendTick;
    private int _emptySent;

    public void SetTemplates(CompanionTemplates templates) {
        if (templates == _templates) {
            return;
        }

        _templates = templates;
        for (var i = 0; i < MaxSlots; i++) {
            _objects[i] = null;
        }

        if (templates == null) {
            return;
        }

        // Los que ya salieron antes de preparar las plantillas (Grimmchild sale al cargar la sala)
        for (var kind = 0; kind < templates.Kinds.Count; kind++) {
            ObjectPool.GetSpawned(templates.Kinds[kind].Prefab, _found, false);
            foreach (var spawned in _found) {
                Track(kind, spawned);
            }
        }

        _found.Clear();
    }

    /// <summary>Algo salió del pool: si es un acompañante, se apunta.</summary>
    public void OnPoolSpawn(GameObject prefab, GameObject spawned) {
        if (_templates != null && spawned != null && _templates.TryGetKind(prefab, out var kind)) {
            Track(kind, spawned);
        }
    }

    private void Track(int kind, GameObject spawned) {
        var free = -1;
        for (var i = 0; i < MaxSlots; i++) {
            if (_objects[i] == spawned) {
                return;
            }

            if (free < 0 && (_objects[i] == null || !ObjectPool.IsSpawned(_objects[i]))) {
                free = i;
            }
        }

        if (free < 0) {
            return;
        }

        var paths = _templates.Kinds[kind].PartPaths;
        var parts = _parts[free];
        if (parts == null || parts.Length != paths.Length) {
            parts = _parts[free] = new tk2dSprite[paths.Length];
            _renderers[free] = new MeshRenderer[paths.Length];
        }

        for (var p = 0; p < paths.Length; p++) {
            var node = paths[p].Length == 0 ? spawned.transform : spawned.transform.Find(paths[p]);
            parts[p] = node != null ? node.GetComponent<tk2dSprite>() : null;
            _renderers[free][p] = node != null ? node.GetComponent<MeshRenderer>() : null;
        }

        _objects[free] = spawned;
        _kinds[free] = (byte) kind;
        _generations[free]++;
    }

    /// <summary>Envía lo que se ve de cada acompañante, si toca. Llamar en LateUpdate.</summary>
    public void Send(GameSession session, ushort sceneHash) {
        var tick = (uint) (Time.unscaledTimeAsDouble * NetConstants.TickRate);
        if (_templates == null || tick - _lastSendTick < SendEveryTicks) {
            return;
        }

        _lastSendTick = tick;
        var count = 0;
        for (var i = 0; i < MaxSlots; i++) {
            if (IsShown(i)) {
                count++;
            }
        }

        if (count == 0) {
            if (_emptySent >= EmptyRepeats) {
                return;
            }

            _emptySent++;
        } else {
            _emptySent = 0;
        }

        var writer = session.Begin(PlayerEffects.PlayerCompanions);
        writer.WriteUInt(tick);
        writer.WriteUShort(sceneHash);
        writer.WriteByte((byte) count);
        var sample = new CompanionSample();
        for (var i = 0; i < MaxSlots; i++) {
            if (!IsShown(i)) {
                continue;
            }

            var transform = _objects[i].transform;
            var scale = transform.lossyScale;
            sample.Slot = (byte) i;
            sample.Generation = _generations[i];
            sample.Kind = _kinds[i];
            sample.Position = transform.position;
            sample.Rotation = transform.eulerAngles.z;
            sample.Scale = new Vector2(scale.x, scale.y);
            sample.Visible = 0;
            var parts = _parts[i];
            for (var p = 0; p < parts.Length; p++) {
                var sprite = parts[p];
                var renderer = _renderers[i][p];
                if (sprite == null || renderer == null || !renderer.enabled || !sprite.gameObject.activeInHierarchy) {
                    continue;
                }

                var collection = _templates.IndexOf(sprite.Collection);
                if (collection == byte.MaxValue) {
                    continue;
                }

                sample.Visible |= (byte) (1 << p);
                sample.SetPart(p, collection, (ushort) sprite.spriteId);
            }

            sample.Write(writer);
        }

        session.End();
    }

    private bool IsShown(int slot) {
        var spawned = _objects[slot];
        if (spawned == null) {
            return false;
        }

        if (!ObjectPool.IsSpawned(spawned)) {
            _objects[slot] = null;
            return false;
        }

        return spawned.activeInHierarchy;
    }
}

/// <summary>
/// Acompañantes de un compañero en este PC: copias solo visuales que siguen lo que llega, interpoladas al mismo
/// momento que su avatar (su tick). No dañan, no chocan y no atacan: el daño ya lo hizo el PC del dueño.
/// </summary>
internal sealed class RemoteCompanions {
    /// <summary>Sin noticias en este tiempo, se apagan (el dueño se fue o se perdieron los envíos).</summary>
    private const double Timeout = 0.6;

    private sealed class Slot {
        public CompanionSample Previous;
        public uint PreviousTick;
        public CompanionSample Latest;
        public uint LatestTick;
        public bool HasPrevious;
        public bool Present;
        public GameObject[] Copies;
        public Transform[][] Parts;
        public tk2dSprite[][] Sprites;
        public MeshRenderer[][] Renderers;
        public int Shown = -1;
    }

    private readonly CompanionTemplates _templates;
    private readonly Transform _container;
    private readonly Slot[] _slots = new Slot[LocalCompanions.MaxSlots];
    private double _lastMessage;
    private CompanionSample _incoming;

    public RemoteCompanions(CompanionTemplates templates, Transform container) {
        _templates = templates;
        _container = container;
        for (var i = 0; i < _slots.Length; i++) {
            _slots[i] = new Slot { Copies = new GameObject[templates.Kinds.Count] };
            _slots[i].Parts = new Transform[templates.Kinds.Count][];
            _slots[i].Sprites = new tk2dSprite[templates.Kinds.Count][];
            _slots[i].Renderers = new MeshRenderer[templates.Kinds.Count][];
        }
    }

    /// <summary>Lo que llegó del dueño (ya leídos el tick y la sala).</summary>
    public void Read(PacketReader reader, uint tick) {
        _lastMessage = Time.unscaledTimeAsDouble;
        foreach (var slot in _slots) {
            slot.Present = false;
        }

        var count = reader.ReadByte();
        for (var i = 0; i < count; i++) {
            _incoming.Read(reader);
            if (_incoming.Slot >= _slots.Length || _incoming.Kind >= _templates.Kinds.Count) {
                continue;
            }

            var slot = _slots[_incoming.Slot];
            slot.HasPrevious = slot.LatestTick != 0 && slot.Latest.Generation == _incoming.Generation &&
                               slot.Latest.Kind == _incoming.Kind;
            slot.Previous = slot.Latest;
            slot.PreviousTick = slot.LatestTick;
            slot.Latest = _incoming;
            slot.LatestTick = tick;
            slot.Present = true;
        }
    }

    /// <summary>Coloca las copias en el tick en que se dibuja el avatar de su dueño.</summary>
    public void Update(double renderTick) {
        var stale = Time.unscaledTimeAsDouble - _lastMessage > Timeout;
        foreach (var slot in _slots) {
            if (!slot.Present || stale) {
                Show(slot, -1);
                continue;
            }

            var latest = slot.Latest;
            Show(slot, latest.Kind);
            var copy = slot.Copies[latest.Kind];
            if (copy == null) {
                continue;
            }

            // Entre los dos últimos, al tick del avatar (sin pasar del último)
            var position = latest.Position;
            var rotation = latest.Rotation;
            var scale = latest.Scale;
            var sample = latest;
            if (slot.HasPrevious && slot.LatestTick > slot.PreviousTick && renderTick < slot.LatestTick) {
                var t = (float) ((renderTick - slot.PreviousTick) / (slot.LatestTick - slot.PreviousTick));
                t = Mathf.Clamp01(t);
                position = Vector3.Lerp(slot.Previous.Position, latest.Position, t);
                rotation = Mathf.LerpAngle(slot.Previous.Rotation, latest.Rotation, t);
                scale = Vector2.Lerp(slot.Previous.Scale, latest.Scale, t);
                if (t < 0.5f) {
                    sample = slot.Previous;
                }
            }

            var transform = copy.transform;
            transform.position = position;
            transform.eulerAngles = new Vector3(0f, 0f, rotation);
            SetLossyScale(transform, scale);
            var sprites = slot.Sprites[latest.Kind];
            var renderers = slot.Renderers[latest.Kind];
            for (var p = 0; p < sprites.Length; p++) {
                var visible = (sample.Visible & (1 << p)) != 0;
                if (renderers[p] != null && renderers[p].enabled != visible) {
                    renderers[p].enabled = visible;
                }

                if (!visible || sprites[p] == null) {
                    continue;
                }

                sample.GetPart(p, out var collectionIndex, out var spriteId);
                var collection = _templates.Collection(collectionIndex);
                if (collection != null && spriteId < collection.spriteDefinitions.Length &&
                    (sprites[p].Collection != collection || sprites[p].spriteId != spriteId)) {
                    sprites[p].SetSprite(collection, spriteId);
                }
            }
        }
    }

    private static void SetLossyScale(Transform transform, Vector2 scale) {
        var parent = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
        transform.localScale = new Vector3(scale.x / NonZero(parent.x), scale.y / NonZero(parent.y), 1f);
    }

    private static float NonZero(float value) => Mathf.Abs(value) < 0.0001f ? 1f : value;

    /// <summary>Enciende la copia del tipo indicado en ese hueco (-1: ninguna).</summary>
    private void Show(Slot slot, int kind) {
        if (slot.Shown == kind) {
            return;
        }

        if (slot.Shown >= 0 && slot.Copies[slot.Shown] != null) {
            slot.Copies[slot.Shown].SetActive(false);
        }

        slot.Shown = kind;
        if (kind < 0) {
            return;
        }

        var copy = slot.Copies[kind];
        if (copy == null) {
            var template = _templates.Kinds[kind];
            copy = slot.Copies[kind] = Object.Instantiate(template.Template, _container, false);
            copy.name = template.Template.name;
            var paths = template.PartPaths;
            slot.Parts[kind] = new Transform[paths.Length];
            slot.Sprites[kind] = new tk2dSprite[paths.Length];
            slot.Renderers[kind] = new MeshRenderer[paths.Length];
            for (var p = 0; p < paths.Length; p++) {
                var node = paths[p].Length == 0 ? copy.transform : copy.transform.Find(paths[p]);
                slot.Parts[kind][p] = node;
                slot.Sprites[kind][p] = node != null ? node.GetComponent<tk2dSprite>() : null;
                slot.Renderers[kind][p] = node != null ? node.GetComponent<MeshRenderer>() : null;
            }
        }

        copy.SetActive(true);
    }

    /// <summary>Las apaga (el avatar se fue de la sala).</summary>
    public void Hide() {
        foreach (var slot in _slots) {
            slot.Present = false;
            slot.LatestTick = 0;
            Show(slot, -1);
        }
    }

    public void Destroy() {
        foreach (var slot in _slots) {
            foreach (var copy in slot.Copies) {
                if (copy != null) {
                    Object.Destroy(copy);
                }
            }
        }
    }

    /// <summary>Cuántos se ven ahora (modo de prueba).</summary>
    public int ShownCount {
        get {
            var shown = 0;
            foreach (var slot in _slots) {
                shown += slot.Shown >= 0 ? 1 : 0;
            }

            return shown;
        }
    }
}
