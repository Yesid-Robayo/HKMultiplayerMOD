using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Objetos rompibles de decoración (estantes, jarrones, postes, vides, glóbulos...) y cortables (telas y pasto): cuando
/// uno se rompe o se corta en un PC, también en los demás de la sala, con su animación y sus trozos. Quien entra en la sala recibe los que ya
/// están rotos. Como en el juego, vuelven a estar enteros al volver a cargar la sala.
/// <para>
/// Cada tipo del juego guarda de forma distinta si está roto; se lee con lectores compilados (sin reflexión ni memoria
/// por lectura) y se vigila cada frame. Para romperlo en los demás se usa el mismo camino que el juego: su método de
/// romper o, si solo se rompe al tocarlo un ataque, su detector de choques con un ataque de hechizo simulado.
/// </para>
/// <para>
/// El capullo de savia vital (`HealthCocoon`, de código y guardable, sin FSM: los guardables de `SceneObjectSync` necesitan
/// una) también: roto en directo, en los demás se rompe igual y suelta allí sus mariposas, así que cada jugador de la sala
/// recoge su savia, como el geo. A quien entra después se le pone roto sin soltar nada (si no, ganaría savia al entrar).
/// </para>
/// </summary>
internal sealed class BreakableSync {
    public const MessageType Broken = (MessageType) 52;

    private const int MaxIdsPerMessage = 200;

    private sealed class Kind {
        public Type Type;
        public Func<MonoBehaviour, bool> IsBroken;
        public Action<MonoBehaviour, BreakableSync> Break;

        /// <summary>El componente se destruye a sí mismo al romperse (telas y pasto cortables).</summary>
        public bool DestroyedMeansBroken;

        /// <summary>
        /// Si lo tiene, a quien entra en la sala se le pone roto así, sin lo que suelta al romperse (el capullo de
        /// savia). Si no, se rompe como en directo (los trozos de decoración no dan nada).
        /// </summary>
        public Action<MonoBehaviour> SetBroken;
    }

    private sealed class Tracked {
        public uint Id;
        public MonoBehaviour Component;
        public Kind Kind;
        public bool LastValue;
    }

    private static readonly List<Kind> Kinds = new();

    private readonly List<Tracked> _tracked = new();
    private readonly Dictionary<uint, Tracked> _byId = new();
    private readonly List<uint> _out = new();
    /// <summary>Rotos en otro PC antes de encontrarlos aquí, y si llegaron como estado para quien entra.</summary>
    private readonly Dictionary<uint, bool> _pending = new();
    private readonly MessageHandler _onBroken;
    private readonly Action<SessionPlayer> _onPlayerScene;
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;
    private bool _sendSnapshot;
    private readonly Dictionary<string, Collider2D> _fakeAttacks = new();

    static BreakableSync() {
        MessageRoutes.Register(Broken, Route.SameScene, reliable: true);
        var breakable = AddFieldKind("Breakable", "isBroken", (c, _) => ((Breakable) c).BreakSelf());
        if (breakable != null) {
            var isBroken = typeof(Breakable).GetField("isBroken", BindingFlags.NonPublic | BindingFlags.Instance);
            var setParts = typeof(Breakable).GetMethod("SetStaticPartsActivation", BindingFlags.NonPublic | BindingFlags.Instance);
            breakable.SetBroken = c => {
                // Como al cargar la sala ya rota (su OnSetSaveState): sin sonido, temblor de cámara ni trozos. Antes, al
                // entrar en una sala con N vasijas rotas, se rompían todas en directo
                isBroken?.SetValue(c, true);
                setParts?.Invoke(c, new object[] { true });
            };
        }
        AddFieldKind("BreakableObject", "activated", BreakObject);
        AddFieldKind("BreakablePoleSimple", "activated", TriggerBreak);
        AddFieldKind("BreakableInfectedVine", "activated", TriggerBreak);
        // Plataformas de enredadera que se cortan (Sendero Verde, Jardines)
        AddFieldKind("VinePlatformCut", "activated", (c, _) => ((VinePlatformCut) c).Cut());
        var cocoon = AddFieldKind("HealthCocoon", "activated", TriggerBreak);
        if (cocoon != null) {
            var activated = typeof(HealthCocoon).GetField("activated", BindingFlags.NonPublic | BindingFlags.Instance);
            var setBroken = typeof(HealthCocoon).GetMethod("SetBroken", BindingFlags.NonPublic | BindingFlags.Instance);
            cocoon.SetBroken = c => {
                // Como al cargar la sala ya abierto (su OnSetSaveState): roto, sin mariposas ni sonido
                activated?.SetValue(c, true);
                setBroken?.Invoke(c, null);
            };
        }

        AddFieldKind("GlobControl", "broken", (c, _) => {
            var method = c.GetType().GetMethod("Break", BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null) {
                c.StartCoroutine((IEnumerator) method.Invoke(c, null));
            }
        });
        // Telas y pasto cortables: al cortarse el componente se destruye; se corta con un golpe de aguijón simulado
        Kinds.Add(new Kind {
            Type = typeof(GrassCut),
            IsBroken = _ => false,
            DestroyedMeansBroken = true,
            Break = (c, sync) => {
                var method = typeof(GrassCut).GetMethod("OnTriggerEnter2D", BindingFlags.NonPublic | BindingFlags.Instance);
                var attack = sync.FakeAttack("Nail Attack", c.transform.position);
                // Sus partículas toman el color del material de su GrassBehaviour, que el juego borra al apagarse el pasto
                // (lejos de la cámara de este PC): sin él, el corte fallaba antes de quitar el pasto. Se corta sin ellas
                var cut = (GrassCut) c;
                var particles = cut.particles;
                var behaviour = c.GetComponentInParent<GrassBehaviour>();
                var noColor = particles != null && behaviour != null && behaviour.SharedMaterial == null;
                if (noColor) {
                    cut.particles = null;
                }

                // El juego pone el efecto del corte entre el centro del golpe y el del pasto: el golpe se activa solo
                // durante la llamada (sin un paso de física en medio, no toca nada más) para que su centro sea real
                attack.enabled = true;
                try {
                    method?.Invoke(c, new object[] { attack });
                } finally {
                    attack.enabled = false;
                    if (noColor) {
                        cut.particles = particles;
                        particles.SetActive(true);
                    }
                }
            }
        });

        // Pasto que se corta y se queda en su sitio (el del Sendero Verde y casi todo el de 1.5): no se seguía, así que lo
        // cortado por uno seguía entero para el otro. `Grass` recibe el golpe como un enemigo; `GrassSpriteBehaviour`
        // se corta con un aguijonazo y solo si está a la vista (fuera de la vista del otro no se cortaría)
        AddFieldKind("Grass", "isCut", (c, _) => ((Grass) c).Hit(new HitInstance {
            AttackType = AttackTypes.Nail, Direction = 0f, DamageDealt = 1, Multiplier = 1f, MagnitudeMultiplier = 1f
        }));
        var grassVisible = typeof(GrassSpriteBehaviour).GetField("visible", BindingFlags.NonPublic | BindingFlags.Instance);
        AddFieldKind("GrassSpriteBehaviour", "isCut", (c, sync) => {
            var method = typeof(GrassSpriteBehaviour).GetMethod("OnTriggerEnter2D", BindingFlags.NonPublic | BindingFlags.Instance);
            var attack = sync.FakeAttack("Nail Attack", c.transform.position);
            grassVisible?.SetValue(c, true);
            attack.enabled = true;
            try {
                method?.Invoke(c, new object[] { attack });
            } finally {
                attack.enabled = false;
            }
        });

        // El poste se queda desactivado al romperse
        Kinds.Add(new Kind {
            Type = typeof(BreakablePole),
            IsBroken = c => !c.enabled,
            Break = (c, _) => ((BreakablePole) c).Hit(new HitInstance {
                AttackType = AttackTypes.Nail, Direction = 0f, DamageDealt = 1, Multiplier = 1f, MagnitudeMultiplier = 1f
            })
        });
    }

    private static Kind AddFieldKind(string typeName, string fieldName, Action<MonoBehaviour, BreakableSync> breakAction) {
        var type = typeof(Breakable).Assembly.GetType(typeName);
        var field = type?.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (field == null || field.FieldType != typeof(bool)) {
            Util.Log.Warn($"Rompible {typeName}.{fieldName} no encontrado: no se sincroniza");
            return null;
        }

        var target = Expression.Parameter(typeof(MonoBehaviour));
        var getter = Expression.Lambda<Func<MonoBehaviour, bool>>(
            Expression.Field(Expression.Convert(target, type), field), target).Compile();
        var kind = new Kind { Type = type, IsBroken = getter, Break = breakAction };
        Kinds.Add(kind);
        return kind;
    }

    /// <summary>Rompe un objeto que solo se rompe al tocarlo un ataque: se simula el choque de un hechizo.</summary>
    private static void TriggerBreak(MonoBehaviour component, BreakableSync sync) {
        var method = component.GetType().GetMethod("OnTriggerEnter2D", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) {
            return;
        }

        var collider = sync.FakeAttack("Hero Spell", component.transform.position);
        method.Invoke(component, new object[] { collider });
    }

    /// <summary>
    /// Rompe un <see cref="BreakableObject"/> con el hechizo simulado. Con un hechizo el juego no elige dirección y, si
    /// el objeto tiene trozos que lanzar, fallaría antes de ocultarse; así que sus trozos se lanzan aparte, hacia arriba
    /// y con la fuerza de un golpe de aguijón.
    /// </summary>
    private static void BreakObject(MonoBehaviour component, BreakableSync sync) {
        var breakable = (BreakableObject) component;
        var debris = breakable.flingDebris;
        breakable.flingDebris = Array.Empty<GameObject>();
        try {
            TriggerBreak(component, sync);
        } finally {
            breakable.flingDebris = debris;
        }

        var direction = breakable.up;
        if (debris == null || direction == null) {
            return;
        }

        foreach (var piece in debris) {
            if (piece == null) {
                continue;
            }

            piece.SetActive(true);
            var speed = UnityEngine.Random.Range(direction.minFlingSpeed, direction.maxFlingSpeed) * breakable.attackMagnitude;
            var angle = UnityEngine.Random.Range(direction.minFlingAngle, direction.maxFlingAngle) * Mathf.Deg2Rad;
            var body = piece.GetComponent<Rigidbody2D>();
            if (body != null) {
                body.AddForce(new Vector2(speed * Mathf.Cos(angle), speed * Mathf.Sin(angle)), ForceMode2D.Impulse);
            }
        }
    }

    public BreakableSync() {
        _onBroken = OnBroken;
        _onPlayerScene = OnPlayerScene;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(Broken, _onBroken);
        session.PlayerSceneChanged += _onPlayerScene;
        Clear();
    }

    public void Detach() {
        if (_session != null) {
            _session.On(Broken, null);
            _session.PlayerSceneChanged -= _onPlayerScene;
        }

        _session = null;
        Clear();
    }

    private void Clear() {
        _tracked.Clear();
        _byId.Clear();
        _pending.Clear();
        _scene = string.Empty;
        _sceneHash = 0;
    }

    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            var hash = PlayerSnapshot.HashScene(scene);
            _tracked.Clear();
            _byId.Clear();
            // Una foto pedida en la sala anterior no se envía en esta
            _sendSnapshot = false;
            if (hash != _sceneHash) {
                _pending.Clear();
            }

            _scene = scene;
            _sceneHash = hash;
            if (scene.Length == 0) {
                return;
            }

            Scan();
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

        _out.Clear();
        for (var i = 0; i < _tracked.Count; i++) {
            var item = _tracked[i];
            bool broken;
            if (item.Component == null) {
                // Destruido: en los cortables significa cortado; en los demás, nada que vigilar. Durante un cambio de
                // sala es que la sala se descarga (al recargar la misma, su nombre aún no cambió)
                if (!item.Kind.DestroyedMeansBroken || EnemySync.InSceneTransition()) {
                    continue;
                }

                broken = true;
            } else {
                broken = item.Kind.IsBroken(item.Component);
            }

            if (broken == item.LastValue) {
                continue;
            }

            item.LastValue = broken;
            if (broken) {
                _out.Add(item.Id);
            }
        }

        var snapshot = false;
        if (_sendSnapshot) {
            // Alguien entró en la sala: le llegan todos los que ya están rotos aquí
            _sendSnapshot = false;
            snapshot = true;
            _out.Clear();
            foreach (var item in _tracked) {
                if (item.LastValue) {
                    _out.Add(item.Id);
                }
            }
        }

        if (_out.Count > 0 && _session.CanSend) {
            Send(_out, snapshot);
        }
    }

    private void Send(List<uint> ids, bool snapshot) {
        for (var start = 0; start < ids.Count; start += MaxIdsPerMessage) {
            var count = Math.Min(MaxIdsPerMessage, ids.Count - start);
            var writer = _session.Begin(Broken);
            writer.WriteUShort(_sceneHash);
            writer.WriteBool(snapshot);
            writer.WriteByte((byte) count);
            for (var i = start; i < start + count; i++) {
                writer.WriteUInt(ids[i]);
            }

            _session.End();
        }
    }

    private void Scan() {
        foreach (var kind in Kinds) {
            foreach (var found in UnityEngine.Object.FindObjectsByType(kind.Type, FindObjectsInactive.Include,
                         FindObjectsSortMode.None)) {
                var component = (MonoBehaviour) found;
                if (IsPoolObject(component.gameObject)) {
                    continue;
                }

                // Un componente derivado (p. ej. de Breakable) se apunta una sola vez, con el primer tipo que encaje
                var id = ScenePaths.StableHash(component.transform) ^ (uint) kind.Type.Name.GetHashCode();
                if (_byId.ContainsKey(id) || IsTrackedObject(component)) {
                    continue;
                }

                var item = new Tracked { Id = id, Component = component, Kind = kind, LastValue = kind.IsBroken(component) };
                _tracked.Add(item);
                _byId[id] = item;
                if (_pending.TryGetValue(id, out var pendingSnapshot)) {
                    _pending.Remove(id);
                    BreakRemote(item, pendingSnapshot);
                }
            }
        }
    }

    /// <summary>
    /// Del pool del juego (guardado en él o sacado de él): las gotas "Orange Glob" de los Bordes del Reino las saca cada
    /// PC por su cuenta y vuelven solas al pool (<c>AutoRecycleSelf</c>). Su ruta no es la misma en todos los PCs (ni es
    /// la misma gota de una vez a otra): si se seguían, romper una aquí rompía en el otro PC otra distinta o ninguna.
    /// Son solo decoración, así que cada PC tiene las suyas.
    /// </summary>
    private static bool IsPoolObject(GameObject gameObject) =>
        ObjectPool.IsSpawned(gameObject) || gameObject.GetComponentInParent<ObjectPool>(true) != null ||
        gameObject.GetComponent<AutoRecycleSelf>() != null;

    private bool IsTrackedObject(MonoBehaviour component) {
        foreach (var item in _tracked) {
            if (item.Component == component) {
                return true;
            }
        }

        return false;
    }

    private void OnBroken(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var snapshot = reader.ReadBool();
        var count = reader.ReadByte();
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            if (sceneHash != _sceneHash) {
                continue;
            }

            if (_byId.TryGetValue(id, out var item)) {
                BreakRemote(item, snapshot);
            } else {
                _pending[id] = snapshot;
            }
        }
    }

    private void BreakRemote(Tracked item, bool snapshot) {
        if (item.Component == null || item.Kind.IsBroken(item.Component)) {
            item.LastValue = true;
            return;
        }

        // Lo roto desde fuera no se vuelve a enviar
        item.LastValue = true;
        try {
            if (snapshot && item.Kind.SetBroken != null) {
                item.Kind.SetBroken(item.Component);
            } else {
                item.Kind.Break(item.Component, this);
            }
        } catch (Exception e) {
            Util.Log.Warn($"No se pudo romper {item.Component.name}: {e.InnerException?.Message ?? e.Message}");
        }
    }

    private void OnPlayerScene(SessionPlayer player) {
        if (!player.IsLocal && player.Scene == _scene && _scene.Length > 0 && _session.IsLocalSceneOwner) {
            _sendSnapshot = true;
        }
    }

    /// <summary>
    /// Colisionador desactivado con la etiqueta de un ataque del héroe ("Hero Spell", "Nail Attack"), para romper o cortar
    /// como lo haría ese ataque. Desactivado no choca con nada por sí mismo.
    /// </summary>
    private Collider2D FakeAttack(string tag, Vector3 near) {
        if (!_fakeAttacks.TryGetValue(tag, out var collider) || collider == null) {
            var fake = new GameObject($"HKMultiplayer golpe remoto ({tag})") { tag = tag };
            UnityEngine.Object.DontDestroyOnLoad(fake);
            var box = fake.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.enabled = false;
            collider = box;
            _fakeAttacks[tag] = collider;
        }

        collider.transform.position = near + new Vector3(-0.5f, 0f, 0f);
        return collider;
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    public IEnumerable<string> Describe() {
        foreach (var item in _tracked) {
            if (item.Component != null) {
                var p = item.Component.transform.position;
                // (los cortados ya no tienen componente: no salen en la lista)
                yield return $"{item.Kind.Type.Name} {item.Component.name} id={item.Id:x8} " +
                             $"{(item.Kind.IsBroken(item.Component) ? "roto" : "entero")} pos=({p.x:0.0},{p.y:0.0})";
            }
        }
    }

    public int BrokenCount() {
        var count = 0;
        foreach (var item in _tracked) {
            if (item.Component == null ? item.Kind.DestroyedMeansBroken : item.Kind.IsBroken(item.Component)) {
                count++;
            }
        }

        return count;
    }

    public int Count => _tracked.Count;

    /// <summary>Rompe los rompibles enteros cuyo nombre contiene el texto, como si los golpeara.</summary>
    public int TestBreakNamed(string name) {
        var broken = 0;
        foreach (var item in _tracked) {
            if (item.Component != null && item.Component.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 &&
                !item.Kind.IsBroken(item.Component)) {
                item.Kind.Break(item.Component, this);
                broken++;
            }
        }

        return broken;
    }

    /// <summary>Rompe los <paramref name="amount"/> rompibles enteros más cercanos al héroe, como si los golpeara.</summary>
    public int TestBreakNearest(int amount) {
        var hero = HeroController.instance;
        if (hero == null) {
            return 0;
        }

        var candidates = new List<Tracked>();
        foreach (var item in _tracked) {
            if (item.Component != null && item.Component.gameObject.activeInHierarchy && !item.Kind.IsBroken(item.Component)) {
                candidates.Add(item);
            }
        }

        var heroPosition = hero.transform.position;
        candidates.Sort((a, b) => Vector2.Distance(a.Component.transform.position, heroPosition)
            .CompareTo(Vector2.Distance(b.Component.transform.position, heroPosition)));
        var broken = 0;
        foreach (var item in candidates) {
            if (broken >= amount) {
                break;
            }

            item.Kind.Break(item.Component, this);
            broken++;
        }

        return broken;
    }
}
