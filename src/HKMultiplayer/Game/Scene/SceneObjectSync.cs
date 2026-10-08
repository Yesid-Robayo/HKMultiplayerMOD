using System.Collections.Generic;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Game.World;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Objetos de la sala en directo: cuando uno rompe una pared, tira de una palanca o abre una puerta, los demás que están
/// en la misma sala lo ven al momento.
/// <para>
/// Los objetos guardables del juego (<see cref="PersistentBoolItem"/>) llevan una variable "Activated" en su FSM. Se
/// vigila cada frame (sin crear memoria) y, al activarse en este PC, se avisa a la sala. Al recibirlo, el objeto se
/// pone en el estado al que el propio juego lo lleva al cargar la sala con él ya activado (se busca en su FSM la
/// comprobación de "Activated"), así queda igual que si se hubiera roto aquí.
/// </para>
/// Los objetos que recoge cada uno (<see cref="CharacterData.IsIndividualItem"/>) quedan fuera.
/// <para>
/// También en directo: piedras de geo (cada golpe baja su contador "Hits" en todos; quien golpea se lleva su geo y los
/// demás ven la piedra romperse) y estalactitas (el juego solo las suelta por el héroe local; cuando una cae en un PC,
/// cae también en los demás de la sala).
/// </para>
/// </summary>
internal sealed class SceneObjectSync {
    public const MessageType ObjectActivated = (MessageType) 42;
    public const MessageType GeoRockHits = (MessageType) 48;
    public const MessageType StalactiteFall = (MessageType) 49;

    private const string HitsVariable = "Hits";
    private const string BrokenState = "Broken";

    private sealed class Rock {
        public string Id;
        public PlayMakerFSM Fsm;
        public FsmInt Hits;
        public int LastValue;
    }

    private readonly List<Rock> _rocks = new();
    private readonly Dictionary<string, Rock> _rocksById = new();
    private readonly Dictionary<uint, StalactiteControl> _stalactites = new();
    private readonly MessageHandler _onRockHits;
    private readonly MessageHandler _onStalactite;

    private static SceneObjectSync _active;
    private static bool _hooked;
    private static bool _applyingRemoteFall;

    private static readonly System.Reflection.MethodInfo FallMethod =
        typeof(StalactiteControl).GetMethod("Fall", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static readonly System.Reflection.FieldInfo FallenField =
        typeof(StalactiteControl).GetField("fallen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static readonly System.Reflection.FieldInfo FallDelayField =
        typeof(StalactiteControl).GetField("fallDelay", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    private const string ActivatedVariable = "Activated";

    private sealed class Tracked {
        public string Id;
        public PlayMakerFSM Fsm;
        public FsmBool Activated;
        public bool LastValue;
        public string ActivatedState;
    }

    private readonly List<Tracked> _tracked = new();
    private readonly Dictionary<string, Tracked> _byId = new();
    private readonly MessageHandler _onActivated;
    private readonly System.Action<SessionPlayer> _onPlayerScene;

    // Lo que llegó de objetos que aquí aún no se han encontrado (se aplica al encontrarlos)
    private readonly HashSet<string> _pendingActivated = new();
    private readonly Dictionary<string, int> _pendingRocks = new();
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;
    private bool _sendSnapshot;

    static SceneObjectSync() {
        MessageRoutes.Register(ObjectActivated, Route.SameScene, reliable: true);
        MessageRoutes.Register(GeoRockHits, Route.SameScene, reliable: true);
        MessageRoutes.Register(StalactiteFall, Route.SameScene, reliable: true);
    }

    public SceneObjectSync() {
        _onActivated = OnActivated;
        _onRockHits = OnRockHits;
        _onStalactite = OnStalactite;
        _onPlayerScene = OnPlayerScene;
    }

    public int TrackedCount => _tracked.Count;

    public void Attach(GameSession session) {
        _session = session;
        session.On(ObjectActivated, _onActivated);
        session.On(GeoRockHits, _onRockHits);
        session.On(StalactiteFall, _onStalactite);
        session.PlayerSceneChanged += _onPlayerScene;
        _active = this;
        InstallHooks();
        Clear();
    }

    public void Detach() {
        if (_session != null) {
            _session.On(ObjectActivated, null);
            _session.On(GeoRockHits, null);
            _session.On(StalactiteFall, null);
            _session.PlayerSceneChanged -= _onPlayerScene;
        }

        _session = null;
        if (_active == this) {
            _active = null;
        }

        Clear();
    }

    private void Clear() {
        _tracked.Clear();
        _byId.Clear();
        _rocks.Clear();
        _rocksById.Clear();
        _stalactites.Clear();
        _pendingActivated.Clear();
        _pendingRocks.Clear();
        _sendSnapshot = false;
        _scene = string.Empty;
        _sceneHash = 0;
    }

    /// <summary>Llamar cada frame con la sala local (vacía si no se está jugando).</summary>
    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            Clear();
            _scene = scene;
            _sceneHash = PlayerSnapshot.HashScene(scene);
            if (scene.Length == 0) {
                return;
            }

            Scan();
            // Hay objetos que se activan un poco después de cargar la sala: segunda pasada
            _rescanAt = Time.unscaledTime + 1.5f;
            return;
        }

        if (scene.Length == 0) {
            return;
        }

        if (_rescanAt > 0f && Time.unscaledTime >= _rescanAt) {
            _rescanAt = 0f;
            Scan();
        }

        for (var i = 0; i < _tracked.Count; i++) {
            var item = _tracked[i];
            if (item.Fsm == null) {
                continue;
            }

            var value = item.Activated.Value;
            if (value == item.LastValue) {
                continue;
            }

            item.LastValue = value;
            if (value && _session.CanSend) {
                var writer = _session.Begin(ObjectActivated);
                writer.WriteUShort(_sceneHash);
                writer.WriteString(item.Id);
                _session.End();
            }
        }

        for (var i = 0; i < _rocks.Count; i++) {
            var rock = _rocks[i];
            if (rock.Fsm == null) {
                continue;
            }

            var hits = rock.Hits.Value;
            if (hits == rock.LastValue) {
                continue;
            }

            var lower = hits < rock.LastValue;
            rock.LastValue = hits;
            if (lower && _session.CanSend) {
                SendRockHits(rock);
            }
        }

        if (_sendSnapshot && _session.CanSend) {
            // Alguien entró en la sala: le llega lo que ya está activado y los golpes de cada piedra (su juego cargó la
            // sala con lo guardado, que puede ser anterior)
            _sendSnapshot = false;
            var activatedCount = 0;
            foreach (var item in _tracked) {
                if (item.Fsm != null && item.Activated.Value) {
                    var writer = _session.Begin(ObjectActivated);
                    writer.WriteUShort(_sceneHash);
                    writer.WriteString(item.Id);
                    _session.End();
                    activatedCount++;
                }
            }

            foreach (var rock in _rocks) {
                if (rock.Fsm != null) {
                    SendRockHits(rock);
                }
            }

            Log.Info($"Estado de {_scene} enviado a quien entra: {activatedCount} objetos activados, {_rocks.Count} piedras");
        }
    }

    private void SendRockHits(Rock rock) {
        var writer = _session.Begin(GeoRockHits);
        writer.WriteUShort(_sceneHash);
        writer.WriteString(rock.Id);
        writer.WriteInt(rock.Hits.Value);
        _session.End();
    }

    private void OnPlayerScene(SessionPlayer player) {
        if (!player.IsLocal && player.Scene == _scene && _scene.Length > 0 && _session.IsLocalSceneOwner) {
            _sendSnapshot = true;
        }
    }

    /// <summary>Busca los objetos guardables de la sala (solo crea memoria al entrar en una sala).</summary>
    private void Scan() {
        foreach (var persistent in Object.FindObjectsByType<PersistentBoolItem>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            var id = persistent.persistentBoolData?.id;
            if (string.IsNullOrEmpty(id)) {
                id = persistent.name;
            }

            if (_byId.ContainsKey(id) || CharacterData.IsIndividualItem(id)) {
                continue;
            }

            var fsm = FSMUtility.FindFSMWithPersistentBool(persistent.GetComponents<PlayMakerFSM>());
            var activated = fsm != null ? fsm.FsmVariables.FindFsmBool(ActivatedVariable) : null;
            if (activated == null) {
                continue;
            }

            var tracked = new Tracked {
                Id = id,
                Fsm = fsm,
                Activated = activated,
                LastValue = activated.Value,
                ActivatedState = FindActivatedState(fsm)
            };
            _tracked.Add(tracked);
            _byId[id] = tracked;
            if (_pendingActivated.Remove(id)) {
                Activate(tracked);
            }
        }

        foreach (var geoRock in Object.FindObjectsByType<GeoRock>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            var id = geoRock.geoRockData?.id;
            if (string.IsNullOrEmpty(id)) {
                id = geoRock.name;
            }

            var fsm = geoRock.GetComponent<PlayMakerFSM>();
            var hits = fsm != null ? fsm.FsmVariables.FindFsmInt(HitsVariable) : null;
            if (hits == null || _rocksById.ContainsKey(id)) {
                continue;
            }

            var rock = new Rock { Id = id, Fsm = fsm, Hits = hits, LastValue = hits.Value };
            _rocks.Add(rock);
            _rocksById[id] = rock;
            if (_pendingRocks.TryGetValue(id, out var pendingHits)) {
                _pendingRocks.Remove(id);
                ApplyRockHits(rock, pendingHits);
            }
        }

        foreach (var stalactite in Object.FindObjectsByType<StalactiteControl>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            _stalactites[ScenePaths.Hash(stalactite.transform)] = stalactite;
        }
    }

    private void OnRockHits(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadString(256);
        var hits = reader.ReadInt();
        if (sceneHash != _sceneHash) {
            return;
        }

        if (_rocksById.TryGetValue(id, out var rock)) {
            ApplyRockHits(rock, hits);
        } else if (!_pendingRocks.TryGetValue(id, out var pending) || hits < pending) {
            _pendingRocks[id] = hits;
        }
    }

    private static void ApplyRockHits(Rock rock, int hits) {
        if (rock.Fsm == null || hits >= rock.Hits.Value) {
            return;
        }

        rock.Hits.Value = Mathf.Max(0, hits);
        rock.LastValue = rock.Hits.Value;
        if (rock.Hits.Value <= 0 && rock.Fsm.ActiveStateName != BrokenState) {
            // Rota por otro: se ve rota, sin soltar geo aquí (se lo lleva quien la rompió)
            rock.Fsm.SetState(BrokenState);
        }
    }

    private static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.StalactiteControl.Fall += (orig, self, delay) => {
            var sync = _active;
            if (!_applyingRemoteFall && sync?._session != null && sync._session.CanSend && sync._scene.Length > 0) {
                var writer = sync._session.Begin(StalactiteFall);
                writer.WriteUShort(sync._sceneHash);
                writer.WriteUInt(ScenePaths.Hash(self.transform));
                sync._session.End();
            }

            return orig(self, delay);
        };
    }

    private void OnStalactite(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        if (sceneHash != _sceneHash || !_stalactites.TryGetValue(id, out var stalactite) || stalactite == null ||
            !stalactite.gameObject.activeInHierarchy || FallMethod == null ||
            FallenField != null && (bool) FallenField.GetValue(stalactite)) {
            return;
        }

        // Que el héroe local no la vuelva a soltar al pasar debajo
        var trigger = stalactite.GetComponentInChildren<TriggerEnterEvent>();
        if (trigger != null) {
            trigger.gameObject.SetActive(false);
        }

        var delay = FallDelayField != null ? (float) FallDelayField.GetValue(stalactite) : 0.25f;
        _applyingRemoteFall = true;
        try {
            stalactite.StartCoroutine((System.Collections.IEnumerator) FallMethod.Invoke(stalactite, new object[] { delay }));
        } finally {
            _applyingRemoteFall = false;
        }
    }

    /// <summary>
    /// Estado al que va la FSM cuando "Activated" es verdadero al comprobarlo (lo que hace el juego al cargar la sala
    /// con el objeto ya activado). Se prefiere la comprobación más cercana al estado inicial.
    /// </summary>
    private static string FindActivatedState(PlayMakerFSM fsm) {
        var states = fsm.FsmStates;
        var start = fsm.Fsm.StartState;
        string fallback = null;
        foreach (var state in states) {
            foreach (var action in state.Actions) {
                if (action is not BoolTest test || test.boolVariable == null ||
                    test.boolVariable.Name != ActivatedVariable || test.isTrue == null) {
                    continue;
                }

                foreach (var transition in state.Transitions) {
                    if (transition.FsmEvent == test.isTrue && !string.IsNullOrEmpty(transition.ToState)) {
                        if (state.Name == start) {
                            return transition.ToState;
                        }

                        fallback ??= transition.ToState;
                    }
                }
            }
        }

        return fallback;
    }

    private void OnActivated(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadString(256);
        if (sceneHash != _sceneHash) {
            return;
        }

        if (!_byId.TryGetValue(id, out var item)) {
            // Aún no encontrado aquí (aparece un poco después de cargar la sala): se activa al encontrarlo
            if (!CharacterData.IsIndividualItem(id) && _pendingActivated.Count < 512) {
                _pendingActivated.Add(id);
            }

            return;
        }

        if (item.Fsm == null || item.Activated.Value) {
            return;
        }

        Activate(item);
        Log.Info($"{from.Name} activó {id} en esta sala");
    }

    /// <summary>Deja el objeto como activado, igual que al cargar la sala con él activado.</summary>
    private static void Activate(Tracked item) {
        if (item.Activated.Value) {
            item.LastValue = true;
            return;
        }

        item.Activated.Value = true;
        item.LastValue = true;
        if (item.ActivatedState != null) {
            item.Fsm.SetState(item.ActivatedState);
        }
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    /// <summary>Estado de la FSM y si su objeto sigue en la sala (destruido o inactivo).</summary>
    private static string Where(PlayMakerFSM fsm) {
        if (fsm == null) {
            return "destruido";
        }

        return fsm.gameObject.activeInHierarchy ? fsm.ActiveStateName : $"inactivo, {fsm.gameObject.scene.name}";
    }

    /// <summary>Lista de objetos vigilados de la sala (id, activado, estado de activado).</summary>
    public IEnumerable<string> Describe() {
        foreach (var item in _tracked) {
            yield return $"{item.Id}={(item.Activated.Value ? "sí" : "no")}→{item.ActivatedState ?? "?"} ({Where(item.Fsm)})";
        }
    }

    /// <summary>Piedras de geo y estalactitas de la sala.</summary>
    public IEnumerable<string> DescribeExtras() {
        foreach (var rock in _rocks) {
            yield return $"piedra {rock.Id} golpes={rock.Hits.Value} ({Where(rock.Fsm)})";
        }

        foreach (var pair in _stalactites) {
            var fallen = FallenField != null && pair.Value != null && (bool) FallenField.GetValue(pair.Value);
            yield return $"estalactita {pair.Key:x8} {(fallen ? "caída" : "colgando")}";
        }
    }

    /// <summary>Un golpe a una piedra de geo (baja su contador como la FSM al golpearla).</summary>
    public bool TestHitRock(string idPrefix) {
        foreach (var rock in _rocks) {
            if (rock.Id.StartsWith(idPrefix) && rock.Hits.Value > 0) {
                // Lo mismo que hace su FSM en cada golpe: baja el contador; con el último, se rompe como en el juego
                rock.Hits.Value--;
                if (rock.Hits.Value <= 0) {
                    rock.Fsm.SetState("Pause Frame");
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Suelta la estalactita sin caer más cercana al jugador, como si pasara debajo.</summary>
    public bool TestDropStalactite() {
        var hero = HeroController.instance;
        StalactiteControl nearest = null;
        var best = float.MaxValue;
        foreach (var stalactite in _stalactites.Values) {
            if (stalactite == null || !stalactite.gameObject.activeInHierarchy || FallMethod == null ||
                FallenField != null && (bool) FallenField.GetValue(stalactite)) {
                continue;
            }

            var distance = hero != null ? Vector2.Distance(stalactite.transform.position, hero.transform.position) : 0f;
            if (distance < best) {
                best = distance;
                nearest = stalactite;
            }
        }

        if (nearest == null) {
            return false;
        }

        nearest.StartCoroutine((System.Collections.IEnumerator) FallMethod.Invoke(nearest, new object[] { 0.25f }));
        return true;
    }

    /// <summary>Activa un objeto como si lo hubiera hecho este jugador (se avisa a la sala).</summary>
    public bool TestActivate(string idPrefix) {
        foreach (var item in _tracked) {
            if (item.Id.StartsWith(idPrefix) && !item.Activated.Value) {
                item.Activated.Value = true;
                if (item.ActivatedState != null) {
                    item.Fsm.SetState(item.ActivatedState);
                }

                return true;
            }
        }

        return false;
    }
}
