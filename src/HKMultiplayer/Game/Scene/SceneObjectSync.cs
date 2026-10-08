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
/// <para>
/// El directo (el golpe, la animación, el sonido) lo lleva un solo sistema: la copia de la cadena de
/// <see cref="FsmStateSync"/>. Aquí el aviso de "activado" no mueve la FSM mientras esa cadena va en marcha; solo se
/// comprueba al acabar y, si no llegó a activarlo, se activa. Lo demás es de aquí: lo que llega a quien entra en la
/// sala y lo que llega de objetos que aquí aún no se han encontrado o no han cargado lo guardado.
/// </para>
/// <para>
/// La línea base de cada objeto se toma cuando el juego ya puso en él lo guardado
/// (<see cref="PersistentBoolItem"/>.Start, que en objetos inactivos llega tarde): si no, un objeto ya activado
/// "se activaba" al cargarse y se avisaba de algo que no había pasado.
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

    /// <summary>Estado de la piedra al romperse (sonido, trozos, animación).</summary>
    private const string DestroyState = "Destroy";

    /// <summary>Estado tras un golpe que no la rompe: suena y elige hacia dónde tiembla.</summary>
    private const string HitFeedbackState = "Check Direction";

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

    /// <summary>Prefijo del id de un rompible con FSM propia (no tiene id de guardado: va por su ruta en la sala).</summary>
    private const string BreakableIdPrefix = "fsm:";

    /// <summary>Lo más que se espera a que acabe la cadena copiada antes de activar el objeto de todos modos.</summary>
    private const float ChainWaitSeconds = 10f;

    private sealed class Tracked {
        public string Id;
        public PersistentBoolItem Persistent;
        public PlayMakerFSM Fsm;
        public FsmBool Activated;
        public bool LastValue;
        public string ActivatedState;

        // Llegó activado de otro PC antes de que el juego le pusiera lo guardado: se pone al cargarlo
        public bool ActivateOnStart;

        // Llegó activado de otro PC con su cadena en marcha: se comprueba al acabar
        public float VerifySince;
    }

    /// <summary>Si el juego ya puso en el objeto lo guardado (privado en <see cref="PersistentBoolItem"/>).</summary>
    private static readonly System.Reflection.FieldInfo StartedField =
        typeof(PersistentBoolItem).GetField("started", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

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

        var now = Time.unscaledTime;
        for (var i = 0; i < _tracked.Count; i++) {
            var item = _tracked[i];
            if (item.Fsm == null) {
                continue;
            }

            if (item.VerifySince > 0f) {
                VerifyAfterChain(item, now);
            }

            var value = item.Activated.Value;
            if (value == item.LastValue) {
                continue;
            }

            item.LastValue = value;
            // Activado por la cadena que llegó de otro PC: allí ya se avisó (no se devuelve)
            if (value && _session.CanSend && !(FsmStateSync.CopiesChain(item.Fsm, out _, out var remote) && remote)) {
                Log.Info($"{item.Id} activado en esta sala: se avisa a los demás");
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
                SendRockHits(rock, live: true);
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
                    SendRockHits(rock, live: false);
                }
            }

            Log.Info($"Estado de {_scene} enviado a quien entra: {activatedCount} objetos activados, {_rocks.Count} piedras");
        }
    }

    /// <summary>
    /// Llegó activado de otro PC mientras su cadena se copiaba: al acabar, si quedó activado no se toca; si no (la
    /// cadena se cortó o no llegó), se activa. Si la cadena no acaba nunca, se activa igual pasado un rato.
    /// </summary>
    private static void VerifyAfterChain(Tracked item, float now) {
        if (item.Activated.Value) {
            item.VerifySince = 0f;
            return;
        }

        if (FsmStateSync.CopiesChain(item.Fsm, out var busy, out _) && busy && now - item.VerifySince < ChainWaitSeconds) {
            return;
        }

        item.VerifySince = 0f;
        Log.Info($"{item.Id}: su cadena no llegó a activarlo aquí; se activa");
        Activate(item);
    }

    private void SendRockHits(Rock rock, bool live) {
        var writer = _session.Begin(GeoRockHits);
        writer.WriteUShort(_sceneHash);
        writer.WriteString(rock.Id);
        writer.WriteInt(rock.Hits.Value);
        writer.WriteBool(live);
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
                Persistent = persistent,
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

        // Rompibles con la FSM genérica del juego sin guardable (el letrero de Cruces Olvidados): su "Activated" es
        // estar roto. No se guardan, pero quien entra después los ve como están en la sala
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            if (fsm.GetComponent<PersistentBoolItem>() != null || !FsmStateSync.IsBreakableTemplate(fsm)) {
                continue;
            }

            var id = BreakableIdPrefix + (ScenePaths.Hash(fsm.transform) ^ (uint) fsm.FsmName.GetHashCode()).ToString("x8");
            if (_byId.ContainsKey(id)) {
                continue;
            }

            var activated = fsm.FsmVariables.FindFsmBool(ActivatedVariable);
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
        var live = reader.ReadBool();
        if (sceneHash != _sceneHash) {
            return;
        }

        if (_rocksById.TryGetValue(id, out var rock)) {
            if (live) {
                Log.Info($"{from.Name} golpeó {id} (quedan {hits})");
            }

            ApplyRockHits(rock, hits, live);
        } else if (!_pendingRocks.TryGetValue(id, out var pending) || hits < pending) {
            _pendingRocks[id] = hits;
        }
    }

    /// <summary>
    /// Golpes que dio otro jugador. En directo se ve y se oye como aquí: el golpe (sonido, temblor, esquirlas) o la rotura;
    /// sin soltar geo, que es de quien golpeó. Lo que llega al entrar en la sala solo deja la piedra como está.
    /// </summary>
    private static void ApplyRockHits(Rock rock, int hits, bool live = false) {
        if (rock.Fsm == null || hits >= rock.Hits.Value) {
            return;
        }

        rock.Hits.Value = Mathf.Max(0, hits);
        rock.LastValue = rock.Hits.Value;
        var visible = live && rock.Fsm.gameObject.activeInHierarchy;
        if (visible) {
            RockFxPlayed++;
        }

        if (rock.Hits.Value <= 0) {
            if (rock.Fsm.ActiveStateName != BrokenState && rock.Fsm.ActiveStateName != DestroyState) {
                PlayRockState(rock, visible ? DestroyState : BrokenState);
            }
        } else if (visible && (rock.Fsm.ActiveStateName == "Idle" || rock.Fsm.ActiveStateName == "Gleam")) {
            PlayRockState(rock, HitFeedbackState);
        }
    }

    /// <summary>Golpes de otros a piedras vistos en directo (modo de prueba).</summary>
    public static int RockFxPlayed { get; private set; }

    private static readonly List<FsmStateAction> MutedActions = new();

    /// <summary>Pone un estado de la piedra sin las acciones que lanzan geo (en sus estados encadenados tampoco).</summary>
    private static void PlayRockState(Rock rock, string state) {
        foreach (var fsmState in rock.Fsm.FsmStates) {
            foreach (var action in fsmState.Actions) {
                if (action.Enabled && action is FlingObjectsFromGlobalPool fling && fling.gameObject?.Value != null &&
                    fling.gameObject.Value.name.StartsWith("Geo", System.StringComparison.Ordinal)) {
                    action.Enabled = false;
                    MutedActions.Add(action);
                }
            }
        }

        try {
            rock.Fsm.SetState(state);
        } finally {
            foreach (var action in MutedActions) {
                action.Enabled = true;
            }

            MutedActions.Clear();
        }
    }

    private static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        // El juego pone lo guardado en el objeto: esa es su línea base (no algo que acabe de pasar aquí)
        On.PersistentBoolItem.Start += (orig, self) => {
            orig(self);
            _active?.OnPersistentStart(self);
        };

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

    private void OnPersistentStart(PersistentBoolItem persistent) {
        foreach (var item in _tracked) {
            if (item.Persistent != persistent || item.Fsm == null) {
                continue;
            }

            if (item.ActivateOnStart) {
                // Ya activado en otro PC: queda como si lo guardado lo dijera (su FSM lo comprueba al empezar)
                item.ActivateOnStart = false;
                item.Activated.Value = true;
            } else if (item.Activated.Value && !item.LastValue) {
                Log.Info($"{item.Id}: ya estaba activado en lo guardado (no se avisa)");
            }

            item.LastValue = item.Activated.Value;
            return;
        }
    }

    private static bool HasStarted(Tracked item) =>
        item.Persistent == null || StartedField == null || (bool) StartedField.GetValue(item.Persistent);

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

        // El directo lo lleva la copia de su cadena: si va en marcha, solo se comprueba al acabar
        if (FsmStateSync.CopiesChain(item.Fsm, out var busy, out _) && busy) {
            item.VerifySince = Time.unscaledTime;
            Log.Info($"{from.Name} activó {id}: su cadena va en marcha aquí, se comprueba al acabar");
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

        if (!HasStarted(item)) {
            // Al cargarlo, el juego pisaría la variable con lo guardado: se pone justo después
            item.ActivateOnStart = true;
            return;
        }

        item.Activated.Value = true;
        item.LastValue = true;
        // Si ya está ahí (p. ej. llevado por su cadena), volver a entrar repetiría sus acciones
        if (item.ActivatedState != null && item.Fsm.ActiveStateName != item.ActivatedState) {
            FsmStateSync.ApplyingSavedState = true;
            try {
                item.Fsm.SetState(item.ActivatedState);
            } finally {
                FsmStateSync.ApplyingSavedState = false;
            }
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
        yield return $"golpes ajenos a piedras vistos={RockFxPlayed}";
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
