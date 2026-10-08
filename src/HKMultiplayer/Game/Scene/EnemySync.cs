using System;
using System.Collections.Generic;
using System.Text;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Enemigos compartidos. Manda el dueño de la sala (el primero que entró): sus enemigos funcionan normal y envía su
/// estado (posición, orientación, animación) 30 veces por segundo y cada cambio de vida o muerte. En los demás PCs son
/// marionetas: su lógica (FSM, movimiento, física) se detiene y se dibujan con lo recibido, interpolado; siguen haciendo
/// daño al tocarlos.
/// <list type="bullet">
/// <item>Los golpes a una marioneta van al dueño, que los aplica al enemigo real. El que golpea ve los efectos y gana
/// su alma como siempre.</item>
/// <item>Al entrar alguien en la sala, el dueño envía qué enemigos están muertos y su vida: los muertos no
/// reaparecen y los heridos siguen heridos.</item>
/// <item>Si el dueño sale, el siguiente pasa a mover los enemigos desde donde estaban y con la vida que tenían: no se
/// curan.</item>
/// </list>
/// Los enemigos se identifican por su ruta en la jerarquía de la sala (igual en todos los PCs).
/// </summary>
internal sealed class EnemySync {
    public const MessageType EnemyStates = (MessageType) 43;
    public const MessageType EnemyHit = (MessageType) 44;
    public const MessageType EnemyHealth = (MessageType) 45;

    /// <summary>Un enemigo real recibió un golpe (dueño → sala): los demás ven el impacto, el destello y oyen el golpe.</summary>
    public const MessageType EnemyHitFx = (MessageType) 61;

    /// <summary>La lógica de un enemigo real entró en un estado con sonido (dueño → sala): las marionetas lo suenan.</summary>
    public const MessageType EnemySound = (MessageType) 62;

    /// <summary>Enemigos muertos en una sala (dueño → host, que los recuerda hasta que alguien descansa).</summary>
    public const MessageType EnemyKilled = (MessageType) 46;

    /// <summary>Un jugador descansó en un banco (→ host): los enemigos vuelven a aparecer.</summary>
    public const MessageType Rested = (MessageType) 47;

    private const int StateTickDivider = 2; // 60 / 2 = 30 envíos por segundo
    private const int KeepAliveTicks = 30;
    private const int MaxEntriesPerMessage = 50;

    // Banderas extra del estado de un enemigo (los 4 bits altos: desde qué dirección es invencible)
    private const byte ShownFlag = 1;
    private const byte SolidFlag = 2;
    private const byte InvincibleFlag = 4;
    private const byte FlippedYFlag = 8;

    private enum HealthKind : byte {
        Update = 0,
        Died = 1,
        AlreadyDead = 2
    }

    /// <summary>Componentes que siguen activos en una marioneta (dibujo, daño al tocar, vida y efectos).</summary>
    private static readonly string[] KeepOnPuppet = {
        "HealthManager", "tk2d", "DamageHero", "SpriteFlash", "EnemyDeathEffects", "EnemyHitEffects",
        "InfectedEnemyEffects", "ExtraDamageable", "EnemyDreamnailReaction", "NonBouncer"
    };

    private sealed class Enemy {
        public uint Id;
        public HealthManager Health;
        public GameObject Root;
        public Transform Transform;
        public tk2dSpriteAnimator Animator;
        public Rigidbody2D Body;
        public Renderer Renderer;
        public Collider2D Collider;

        /// <summary>FSM del enemigo y sus hijos, en el mismo orden en todos los PCs (para los sonidos).</summary>
        public PlayMakerFSM[] Fsms;
        public bool Puppet;
        public readonly List<Behaviour> Disabled = new();
        public RigidbodyType2D BodyType;
        public readonly SnapshotBuffer Buffer = new();

        // Lo último enviado (dueño)
        public int LastHp;
        public bool ReportedDead;
        public PlayerSnapshot LastSent;
        public uint LastSentTick;
        public tk2dSpriteAnimationClip LastClip;
        public ushort LastClipIndex;
    }

    /// <summary>FSM de enemigos que tienen estados con sonido: su enemigo, su posición y qué estados suenan.</summary>
    private sealed class SoundFsm {
        public Enemy Enemy;
        public byte Index;
        public bool[] SoundStates;
    }

    private readonly Dictionary<HutongGames.PlayMaker.Fsm, SoundFsm> _soundFsms = new();

    private readonly List<Enemy> _enemies = new();
    private readonly Dictionary<uint, Enemy> _byId = new();
    private readonly Dictionary<HealthManager, Enemy> _byHealth = new();
    private readonly Dictionary<uint, (HealthKind Kind, int Hp)> _pendingHealth = new();
    private readonly List<(uint Id, HealthKind Kind, int Hp)> _healthOut = new();
    private readonly List<uint> _killedOut = new();

    /// <summary>
    /// Solo en el host: enemigos muertos de cada sala. Siguen muertos aunque la sala se quede vacía, hasta que
    /// cualquier jugador descansa en un banco.
    /// </summary>
    private readonly Dictionary<string, HashSet<uint>> _deadByScene = new();

    private readonly MessageHandler _onKilled;
    private readonly MessageHandler _onRested;
    private float _lastRest = -10f;
    private readonly MessageHandler _onStates;
    private readonly MessageHandler _onHit;
    private readonly MessageHandler _onHealth;
    private readonly MessageHandler _onHitFx;
    private readonly MessageHandler _onSound;
    private readonly Action<SessionPlayer> _onPlayerScene;

    private GameSession _session;
    private PlayerSync _players;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private float _rescanAt;
    private uint _lastTick;
    private bool _sendSnapshot;
    private PlayerSnapshot _incoming;

    private static EnemySync _active;
    private static bool _hooked;
    private static bool _applyingRemoteHit;

    /// <summary>Quién dio el golpe que se está aplicando desde otro PC (para que él no repita sus propios efectos).</summary>
    private static ushort _remoteHitter;

    /// <summary>Se reproducen en una marioneta los efectos de un golpe que dio otro: ni se reenvía ni quita vida.</summary>
    private static bool _playingFx;

    static EnemySync() {
        MessageRoutes.Register(EnemyStates, Route.SameScene, reliable: false);
        MessageRoutes.Register(EnemyHit, Route.SceneOwner, reliable: true);
        MessageRoutes.Register(EnemyHealth, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyHitFx, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemySound, Route.SameScene, reliable: false);
        MessageRoutes.Register(EnemyKilled, Route.Server, reliable: true);
        MessageRoutes.Register(Rested, Route.Server, reliable: true);
    }

    public EnemySync() {
        _onStates = OnStates;
        _onHit = OnHit;
        _onHealth = OnHealth;
        _onHitFx = OnHitFx;
        _onSound = OnSound;
        _onPlayerScene = OnPlayerScene;
        _onKilled = OnKilled;
        _onRested = (from, _) => {
            Log.Info($"{from.Name} descansó en un banco: los enemigos vuelven a aparecer");
            ClearDead();
        };
    }

    public void Attach(GameSession session, PlayerSync players) {
        _session = session;
        _players = players;
        session.On(EnemyStates, _onStates);
        session.On(EnemyHit, _onHit);
        session.On(EnemyHealth, _onHealth);
        session.On(EnemyHitFx, _onHitFx);
        session.On(EnemySound, _onSound);
        session.On(EnemyKilled, _onKilled);
        session.On(Rested, _onRested);
        session.PlayerSceneChanged += _onPlayerScene;
        _deadByScene.Clear();
        _active = this;
        InstallHooks();
        Clear();
    }

    public void Detach() {
        if (_session != null) {
            _session.On(EnemyStates, null);
            _session.On(EnemyHit, null);
            _session.On(EnemyHealth, null);
            _session.On(EnemyHitFx, null);
            _session.On(EnemySound, null);
            _session.On(EnemyKilled, null);
            _session.On(Rested, null);
            _session.PlayerSceneChanged -= _onPlayerScene;
        }

        // Las marionetas vuelven a funcionar solas
        foreach (var enemy in _enemies) {
            SetPuppet(enemy, false);
        }

        _session = null;
        if (_active == this) {
            _active = null;
        }

        Clear();
    }

    private void Clear() {
        _enemies.Clear();
        _byId.Clear();
        _byHealth.Clear();
        _soundFsms.Clear();
        _pendingHealth.Clear();
        _scene = string.Empty;
        _sceneHash = 0;
    }

    // =====================================================================================================
    // Cada frame
    // =====================================================================================================

    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            var hash = PlayerSnapshot.HashScene(scene);
            _enemies.Clear();
            _byId.Clear();
            _byHealth.Clear();
            _soundFsms.Clear();
            if (hash != _sceneHash) {
                _pendingHealth.Clear();
            }

            _scene = scene;
            _sceneHash = hash;
            if (scene.Length == 0) {
                return;
            }

            // El host sabe qué murió aquí antes (aunque la sala estuviera vacía)
            if (_session.IsHost && _deadByScene.TryGetValue(scene, out var dead)) {
                foreach (var id in dead) {
                    _pendingHealth[id] = (HealthKind.AlreadyDead, 0);
                }
            }

            Scan();
            _rescanAt = Time.unscaledTime + 1.5f;
        }

        if (scene.Length == 0) {
            return;
        }

        if (_rescanAt > 0f && Time.unscaledTime >= _rescanAt) {
            _rescanAt = 0f;
            Scan();
        }

        var owner = _session.OwnerOf(scene);
        var isOwner = owner == _session.LocalId;
        if (owner != 0) {
            for (var i = 0; i < _enemies.Count; i++) {
                // Los muertos se quedan como están (su muerte sigue su curso)
                if (_enemies[i].Puppet == isOwner && _enemies[i].Health != null && !_enemies[i].Health.isDead) {
                    SetPuppet(_enemies[i], !isOwner);
                }
            }
        }

        if (isOwner) {
            UpdateOwner();
        } else {
            UpdatePuppets();
        }
    }

    private bool OthersInScene() {
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene) {
                return true;
            }
        }

        return false;
    }

    private void UpdateOwner() {
        if (!_session.CanSend) {
            return;
        }

        var others = OthersInScene();

        // Vida y muertes (fiable). Las muertes se apuntan en el host aunque no haya nadie más en la sala
        _healthOut.Clear();
        _killedOut.Clear();
        for (var i = 0; i < _enemies.Count; i++) {
            var enemy = _enemies[i];
            // Los muertos se recuerdan aunque su objeto ya no exista (el juego lo destruye al morir); dentro de la
            // sala, un enemigo destruido es un enemigo muerto
            if (enemy.ReportedDead) {
                if (_sendSnapshot) {
                    _healthOut.Add((enemy.Id, HealthKind.AlreadyDead, 0));
                }

                continue;
            }

            var dead = enemy.Health == null || enemy.Health.isDead;
            if (dead) {
                enemy.ReportedDead = true;
                _healthOut.Add((enemy.Id, HealthKind.Died, 0));
                _killedOut.Add(enemy.Id);
            } else if (enemy.Health.hp != enemy.LastHp || _sendSnapshot) {
                enemy.LastHp = enemy.Health.hp;
                _healthOut.Add((enemy.Id, HealthKind.Update, enemy.LastHp));
            }
        }

        _sendSnapshot = false;
        ReportKilled();
        if (!others) {
            return;
        }

        SendHealth();

        // Estado continuo (no fiable), 30 veces por segundo
        var tick = (uint) (Time.unscaledTimeAsDouble * NetConstants.TickRate);
        if (tick / StateTickDivider == _lastTick / StateTickDivider) {
            return;
        }

        _lastTick = tick;
        PacketWriter writer = null;
        var countPosition = 0;
        var count = 0;
        for (var i = 0; i < _enemies.Count; i++) {
            var enemy = _enemies[i];
            if (enemy.Root == null || enemy.ReportedDead) {
                continue;
            }

            var state = Capture(enemy, tick);
            if (state.SameLook(enemy.LastSent) && state.Rotation == enemy.LastSent.Rotation &&
                state.Extra == enemy.LastSent.Extra && tick - enemy.LastSentTick < KeepAliveTicks) {
                continue;
            }

            enemy.LastSent = state;
            enemy.LastSentTick = tick;
            if (writer == null || count == MaxEntriesPerMessage) {
                if (writer != null) {
                    FinishStates(writer, countPosition, count);
                }

                writer = _session.Begin(EnemyStates);
                writer.WriteUShort(_sceneHash);
                writer.WriteUInt(tick);
                countPosition = writer.Position;
                writer.WriteByte(0);
                count = 0;
            }

            writer.WriteUInt(enemy.Id);
            writer.WriteFloat(state.X);
            writer.WriteFloat(state.Y);
            writer.WriteByte((byte) ((state.FacingRight ? 1 : 0) | (state.Visible ? 2 : 0)));
            writer.WriteUShort(state.SpriteId);
            writer.WriteByte(state.Collection);
            writer.WriteUShort(state.Rotation);
            writer.WriteByte(state.Extra);
            count++;
        }

        if (writer != null) {
            FinishStates(writer, countPosition, count);
        }
    }

    private void FinishStates(PacketWriter writer, int countPosition, int count) {
        var end = writer.Position;
        writer.Seek(countPosition);
        writer.WriteByte((byte) count);
        writer.Seek(end);
        _session.End();
    }

    /// <summary>
    /// Estado del enemigo: posición, orientación y giro, si está activo, si se dibuja y si choca (los que se entierran o
    /// se vuelven intangibles), si es invencible y desde dónde, y animación (clip en SpriteId, fotograma en Collection).
    /// </summary>
    private static PlayerSnapshot Capture(Enemy enemy, uint tick) {
        var transform = enemy.Transform;
        var position = transform.position;
        var scale = transform.localScale;
        var health = enemy.Health;
        var extra = (health.InvincibleFromDirection & 0x0F) << 4;
        if (enemy.Renderer == null || enemy.Renderer.enabled) {
            extra |= ShownFlag;
        }

        if (enemy.Collider == null || enemy.Collider.enabled) {
            extra |= SolidFlag;
        }

        if (health.IsInvincible) {
            extra |= InvincibleFlag;
        }

        if (scale.y < 0f) {
            extra |= FlippedYFlag;
        }

        var state = new PlayerSnapshot {
            Tick = tick,
            X = position.x,
            Y = position.y,
            FacingRight = scale.x < 0f,
            Visible = enemy.Root.activeInHierarchy,
            Rotation = (ushort) Mathf.RoundToInt(Mathf.Repeat(transform.eulerAngles.z, 360f) * 100f),
            Extra = (byte) extra
        };

        var animator = enemy.Animator;
        var clip = animator != null ? animator.CurrentClip : null;
        if (clip != null) {
            if (clip != enemy.LastClip) {
                enemy.LastClip = clip;
                var index = Array.IndexOf(animator.Library.clips, clip);
                enemy.LastClipIndex = index < 0 ? ushort.MaxValue : (ushort) index;
            }

            state.SpriteId = enemy.LastClipIndex;
            state.Collection = (byte) Mathf.Clamp(animator.CurrentFrame, 0, 255);
        } else {
            state.SpriteId = ushort.MaxValue;
        }

        return state;
    }

    private void SendHealth() {
        if (_healthOut.Count == 0) {
            return;
        }

        for (var start = 0; start < _healthOut.Count; start += MaxEntriesPerMessage) {
            var count = Math.Min(MaxEntriesPerMessage, _healthOut.Count - start);
            var writer = _session.Begin(EnemyHealth);
            writer.WriteUShort(_sceneHash);
            writer.WriteByte((byte) count);
            for (var i = start; i < start + count; i++) {
                var (id, kind, hp) = _healthOut[i];
                writer.WriteUInt(id);
                writer.WriteByte((byte) kind);
                writer.WriteInt(hp);
            }

            _session.End();
        }
    }

    private void ReportKilled() {
        if (_killedOut.Count == 0) {
            return;
        }

        if (_session.IsHost) {
            RememberDead(_scene, _killedOut);
            return;
        }

        for (var start = 0; start < _killedOut.Count; start += MaxEntriesPerMessage) {
            var count = Math.Min(MaxEntriesPerMessage, _killedOut.Count - start);
            var writer = _session.Begin(EnemyKilled);
            writer.WriteString(_scene);
            writer.WriteByte((byte) count);
            for (var i = start; i < start + count; i++) {
                writer.WriteUInt(_killedOut[i]);
            }

            _session.End();
        }
    }

    private void RememberDead(string scene, List<uint> ids) {
        if (!_deadByScene.TryGetValue(scene, out var dead)) {
            dead = new HashSet<uint>();
            _deadByScene[scene] = dead;
        }

        foreach (var id in ids) {
            dead.Add(id);
        }
    }

    private void OnKilled(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var count = reader.ReadByte();
        _killedOut.Clear();
        for (var i = 0; i < count; i++) {
            _killedOut.Add(reader.ReadUInt());
        }

        // Solo se acepta de quien está en esa sala
        if (_session.IsHost && from.Scene == scene) {
            RememberDead(scene, _killedOut);
        }

        _killedOut.Clear();
    }

    /// <summary>Alguien descansó: el host olvida los muertos (vuelven al entrar de nuevo en cada sala).</summary>
    private void ClearDead() {
        if (_session != null && _session.IsHost) {
            _deadByScene.Clear();
        }
    }

    /// <summary>Este jugador descansó en un banco.</summary>
    private void OnLocalRest() {
        if (_session == null || Time.unscaledTime - _lastRest < 3f) {
            return;
        }

        _lastRest = Time.unscaledTime;
        if (_session.IsHost) {
            Log.Info("Descansaste en un banco: los enemigos vuelven a aparecer");
            ClearDead();
        } else if (_session.CanSend) {
            _session.Begin(Rested);
            _session.End();
        }
    }

    private void UpdatePuppets() {
        var time = Time.unscaledTimeAsDouble;
        for (var i = 0; i < _enemies.Count; i++) {
            var enemy = _enemies[i];
            if (!enemy.Puppet || enemy.Root == null || enemy.Health.isDead || !enemy.Buffer.Sample(time, out var state)) {
                continue;
            }

            if (enemy.Root.activeSelf != state.Visible) {
                enemy.Root.SetActive(state.Visible);
            }

            var transform = enemy.Transform;
            var position = transform.position;
            transform.position = new Vector3(state.X, state.Y, position.z);
            var scale = transform.localScale;
            var flippedY = (state.Extra & FlippedYFlag) != 0;
            if ((scale.x < 0f) != state.FacingRight || (scale.y < 0f) != flippedY) {
                scale.x = state.FacingRight ? -Mathf.Abs(scale.x) : Mathf.Abs(scale.x);
                scale.y = flippedY ? -Mathf.Abs(scale.y) : Mathf.Abs(scale.y);
                transform.localScale = scale;
            }

            var rotation = state.Rotation / 100f;
            if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.z, rotation)) > 0.5f) {
                transform.rotation = Quaternion.Euler(0f, 0f, rotation);
            }

            // Lo que decide la lógica del dueño: si se dibuja, si choca y si los golpes le entran
            var shown = (state.Extra & ShownFlag) != 0;
            if (enemy.Renderer != null && enemy.Renderer.enabled != shown) {
                enemy.Renderer.enabled = shown;
            }

            var solid = (state.Extra & SolidFlag) != 0;
            if (enemy.Collider != null && enemy.Collider.enabled != solid) {
                enemy.Collider.enabled = solid;
            }

            enemy.Health.IsInvincible = (state.Extra & InvincibleFlag) != 0;
            enemy.Health.InvincibleFromDirection = state.Extra >> 4;

            var animator = enemy.Animator;
            if (animator != null && state.SpriteId != ushort.MaxValue && animator.Library != null &&
                state.SpriteId < animator.Library.clips.Length) {
                var clip = animator.Library.clips[state.SpriteId];
                if (clip != null && (animator.CurrentClip != clip || Math.Abs(animator.CurrentFrame - state.Collection) > 1)) {
                    animator.PlayFromFrame(clip, Mathf.Min(state.Collection, Math.Max(0, clip.frames.Length - 1)));
                }
            }
        }
    }

    // =====================================================================================================
    // Enemigos de la sala
    // =====================================================================================================

    /// <summary>Busca los enemigos de la sala, también los aún inactivos (solo crea memoria al entrar en una sala).</summary>
    private void Scan() {
        for (var s = 0; s < UnitySceneManager.sceneCount; s++) {
            var unityScene = UnitySceneManager.GetSceneAt(s);
            if (!unityScene.isLoaded) {
                continue;
            }

            foreach (var root in unityScene.GetRootGameObjects()) {
                foreach (var health in root.GetComponentsInChildren<HealthManager>(true)) {
                    if (_byHealth.ContainsKey(health)) {
                        continue;
                    }

                    var id = ScenePaths.Hash(health.transform);
                    if (_byId.ContainsKey(id)) {
                        continue;
                    }

                    var enemy = new Enemy {
                        Id = id,
                        Health = health,
                        Root = health.gameObject,
                        Transform = health.transform,
                        Animator = health.GetComponent<tk2dSpriteAnimator>(),
                        Body = health.GetComponent<Rigidbody2D>(),
                        Renderer = health.GetComponent<Renderer>(),
                        Collider = health.GetComponent<Collider2D>(),
                        LastHp = health.hp
                    };
                    _enemies.Add(enemy);
                    _byId[id] = enemy;
                    _byHealth[health] = enemy;
                    RegisterSounds(enemy);
                    if (_pendingHealth.TryGetValue(id, out var pending)) {
                        ApplyHealth(enemy, pending.Kind, pending.Hp);
                    }
                }
            }
        }
    }

    /// <summary>Apunta las FSM del enemigo con estados que suenan (solo al entrar en una sala).</summary>
    private void RegisterSounds(Enemy enemy) {
        enemy.Fsms = enemy.Root.GetComponentsInChildren<PlayMakerFSM>(true);
        for (var i = 0; i < enemy.Fsms.Length && i < byte.MaxValue; i++) {
            var states = enemy.Fsms[i].FsmStates;
            bool[] soundStates = null;
            for (var s = 0; s < states.Length; s++) {
                foreach (var action in states[s].Actions) {
                    if (IsSoundAction(action)) {
                        soundStates ??= new bool[states.Length];
                        soundStates[s] = true;
                        break;
                    }
                }
            }

            if (soundStates != null) {
                _soundFsms[enemy.Fsms[i].Fsm] = new SoundFsm { Enemy = enemy, Index = (byte) i, SoundStates = soundStates };
            }
        }
    }

    /// <summary>Acciones de sonido o música de una FSM (las únicas que una marioneta hace de su lógica).</summary>
    private static bool IsSoundAction(HutongGames.PlayMaker.FsmStateAction action) {
        var name = action.GetType().Name;
        return name.IndexOf("Audio", StringComparison.Ordinal) >= 0 || name.IndexOf("Sound", StringComparison.Ordinal) >= 0 ||
               name.IndexOf("Music", StringComparison.Ordinal) >= 0;
    }

    /// <summary>Una FSM entró en un estado: si es de un enemigo real de este PC y suena, los demás lo oyen.</summary>
    private void OnFsmState(HutongGames.PlayMaker.Fsm fsm, HutongGames.PlayMaker.FsmState state) {
        if (!_soundFsms.TryGetValue(fsm, out var sound) || sound.Enemy.Puppet || !_session.CanSend) {
            return;
        }

        var index = Array.IndexOf(fsm.States, state);
        if (index < 0 || index >= sound.SoundStates.Length || !sound.SoundStates[index] || !OthersInScene()) {
            return;
        }

        var writer = _session.Begin(EnemySound);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(sound.Enemy.Id);
        writer.WriteByte(sound.Index);
        writer.WriteUShort((ushort) index);
        _session.End();
    }

    private void OnSound(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var fsmIndex = reader.ReadByte();
        var stateIndex = reader.ReadUShort();
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var enemy) || !enemy.Puppet || enemy.Fsms == null ||
            fsmIndex >= enemy.Fsms.Length || enemy.Fsms[fsmIndex] == null) {
            return;
        }

        var states = enemy.Fsms[fsmIndex].FsmStates;
        if (stateIndex >= states.Length) {
            return;
        }

        // Solo las acciones de sonido del estado; la lógica de la marioneta sigue parada
        var state = states[stateIndex];
        foreach (var action in state.Actions) {
            if (!action.Enabled || !IsSoundAction(action)) {
                continue;
            }

            try {
                SoundsPlayed++;
                action.Init(state);
                action.OnEnter();
            } catch (Exception e) {
                Log.Warn($"Sonido de {enemy.Root.name} ({state.Name}) no reproducido: {e.Message}");
            }
        }
    }

    /// <summary>Convierte un enemigo en marioneta (o lo devuelve a la normalidad).</summary>
    private static void SetPuppet(Enemy enemy, bool puppet) {
        if (enemy.Puppet == puppet || enemy.Root == null) {
            return;
        }

        enemy.Puppet = puppet;
        if (puppet) {
            enemy.Disabled.Clear();
            foreach (var behaviour in enemy.Root.GetComponents<Behaviour>()) {
                if (behaviour.enabled && behaviour is MonoBehaviour && !Keep(behaviour)) {
                    behaviour.enabled = false;
                    enemy.Disabled.Add(behaviour);
                }
            }

            foreach (var fsm in enemy.Root.GetComponentsInChildren<PlayMakerFSM>(true)) {
                if (fsm.enabled && fsm.gameObject != enemy.Root) {
                    fsm.enabled = false;
                    enemy.Disabled.Add(fsm);
                }
            }

            if (enemy.Body != null) {
                enemy.BodyType = enemy.Body.bodyType;
                enemy.Body.velocity = Vector2.zero;
                enemy.Body.bodyType = RigidbodyType2D.Kinematic;
            }

            enemy.Buffer.Clear();
        } else {
            foreach (var behaviour in enemy.Disabled) {
                if (behaviour != null) {
                    behaviour.enabled = true;
                }
            }

            enemy.Disabled.Clear();
            if (enemy.Body != null) {
                enemy.Body.bodyType = enemy.BodyType;
            }

            enemy.LastHp = enemy.Health.hp;
            enemy.ReportedDead = enemy.Health.isDead;
        }
    }

    private static bool Keep(Behaviour behaviour) {
        var name = behaviour.GetType().Name;
        foreach (var prefix in KeepOnPuppet) {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    // =====================================================================================================
    // Red
    // =====================================================================================================

    private void OnStates(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var tick = reader.ReadUInt();
        var count = reader.ReadByte();
        var time = Time.unscaledTimeAsDouble;
        var reliableTiming = Time.unscaledDeltaTime < 0.1f;
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            _incoming.Tick = tick;
            _incoming.SceneHash = sceneHash;
            _incoming.X = reader.ReadFloat();
            _incoming.Y = reader.ReadFloat();
            var flags = reader.ReadByte();
            _incoming.FacingRight = (flags & 1) != 0;
            _incoming.Visible = (flags & 2) != 0;
            _incoming.SpriteId = reader.ReadUShort();
            _incoming.Collection = reader.ReadByte();
            _incoming.Rotation = reader.ReadUShort();
            _incoming.Extra = reader.ReadByte();
            if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var enemy) && enemy.Puppet) {
                enemy.Buffer.Add(_incoming, time, reliableTiming);
            }
        }
    }

    private void OnHealth(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var count = reader.ReadByte();
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            var kind = (HealthKind) reader.ReadByte();
            var hp = reader.ReadInt();
            if (sceneHash != _sceneHash) {
                continue;
            }

            if (_byId.TryGetValue(id, out var enemy)) {
                ApplyHealth(enemy, kind, hp);
            } else {
                // Aún no se ha encontrado en la sala (llegó antes de terminar de cargarla)
                _pendingHealth[id] = (kind, hp);
            }
        }
    }

    private void ApplyHealth(Enemy enemy, HealthKind kind, int hp) {
        var health = enemy.Health;
        if (health == null || health.isDead) {
            enemy.ReportedDead |= kind != HealthKind.Update;
            return;
        }

        switch (kind) {
            case HealthKind.Update:
                // Nunca a 0 por aquí: la muerte llega como tal (si no, el siguiente golpe local lo mataría aquí)
                health.hp = Math.Max(1, hp);
                enemy.LastHp = health.hp;
                break;
            case HealthKind.Died:
                // Las FSM vuelven para que la muerte siga su curso (cadáver, efectos, botín de este jugador)
                SetPuppet(enemy, false);
                health.Die(null, AttackTypes.Nail, true);
                enemy.ReportedDead = true;
                break;
            case HealthKind.AlreadyDead:
                // Murió antes de entrar: no aparece
                health.SetIsDead(true);
                enemy.ReportedDead = true;
                enemy.Root.SetActive(false);
                break;
        }
    }

    private void OnPlayerScene(SessionPlayer player) {
        if (player.IsLocal) {
            return;
        }

        // Alguien entra en la sala del dueño: le llega qué está muerto y la vida de cada uno
        if (player.Scene == _scene && _scene.Length > 0 && _session.IsLocalSceneOwner) {
            _sendSnapshot = true;
        }

        // El host le dice qué murió en esa sala antes (aunque no haya nadie en ella)
        if (_session.IsHost && player.Scene.Length > 0 && _deadByScene.TryGetValue(player.Scene, out var dead) &&
            dead.Count > 0) {
            SendDeadTo(player, dead);
        }
    }

    private void SendDeadTo(SessionPlayer player, HashSet<uint> dead) {
        var sceneHash = PlayerSnapshot.HashScene(player.Scene);
        PacketWriter writer = null;
        var count = 0;
        var countPosition = 0;
        foreach (var id in dead) {
            if (writer == null || count == MaxEntriesPerMessage) {
                if (writer != null) {
                    FinishTo(writer, countPosition, count);
                }

                writer = _session.BeginTo(player, EnemyHealth);
                writer.WriteUShort(sceneHash);
                countPosition = writer.Position;
                writer.WriteByte(0);
                count = 0;
            }

            writer.WriteUInt(id);
            writer.WriteByte((byte) HealthKind.AlreadyDead);
            writer.WriteInt(0);
            count++;
        }

        if (writer != null) {
            FinishTo(writer, countPosition, count);
        }
    }

    private void FinishTo(PacketWriter writer, int countPosition, int count) {
        var end = writer.Position;
        writer.Seek(countPosition);
        writer.WriteByte((byte) count);
        writer.Seek(end);
        _session.EndTo();
    }

    private void OnHit(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var hit = new HitInstance {
            DamageDealt = reader.ReadInt(),
            AttackType = (AttackTypes) reader.ReadByte(),
            Direction = reader.ReadFloat(),
            IgnoreInvulnerable = reader.ReadBool(),
            MagnitudeMultiplier = reader.ReadFloat(),
            Multiplier = reader.ReadFloat(),
            SpecialType = (SpecialTypes) reader.ReadByte()
        };

        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var enemy) || enemy.Puppet ||
            enemy.Health == null || enemy.Health.isDead) {
            return;
        }

        hit.DamageDealt = Mathf.Clamp(hit.DamageDealt, 0, 1000);
        hit.Multiplier = Mathf.Clamp(hit.Multiplier, 0f, 10f);
        hit.Source = _players.TryGetAvatarObject(from.Id, out var avatar) ? avatar : enemy.Root;
        _applyingRemoteHit = true;
        _remoteHitter = from.Id;
        try {
            enemy.Health.Hit(hit);
        } finally {
            _applyingRemoteHit = false;
            _remoteHitter = 0;
        }
    }

    /// <summary>El enemigo real de este PC recibió daño: los demás de la sala ven el golpe (salvo quien lo dio).</summary>
    private void SendHitFx(Enemy enemy, HitInstance hit) {
        if (!_session.CanSend || !OthersInScene()) {
            return;
        }

        var writer = _session.Begin(EnemyHitFx);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(enemy.Id);
        writer.WriteUShort(_applyingRemoteHit ? _remoteHitter : _session.LocalId);
        writer.WriteByte((byte) hit.AttackType);
        writer.WriteFloat(hit.GetActualDirection(enemy.Transform));
        _session.End();
    }

    private void OnHitFx(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var hitter = reader.ReadUShort();
        var attackType = (AttackTypes) reader.ReadByte();
        var direction = reader.ReadFloat();
        if (hitter == _session.LocalId || sceneHash != _sceneHash || !_byId.TryGetValue(id, out var enemy) ||
            !enemy.Puppet || enemy.Health == null || enemy.Health.isDead || enemy.Root == null ||
            !enemy.Root.activeInHierarchy) {
            return;
        }

        // Los efectos salen entre el enemigo y quien golpeó, como en su PC
        var source = _players.TryGetAvatarObject(hitter, out var avatar) ? avatar : enemy.Root;
        var health = enemy.Health;
        var invincible = health.IsInvincible;
        health.IsInvincible = false;
        _playingFx = true;
        _applyingRemoteHit = true;
        try {
            HitFxPlayed++;
            HitWithoutDamage(health, new HitInstance {
                Source = source, AttackType = attackType, DamageDealt = 1, Multiplier = 0f, MagnitudeMultiplier = 0f,
                Direction = direction, IgnoreInvulnerable = true
            }, null);
        } finally {
            _playingFx = false;
            _applyingRemoteHit = false;
            health.IsInvincible = invincible;
        }
    }

    /// <summary>
    /// Golpe solo de efectos (impacto, destello, sonido, alma de quien golpea) en una marioneta: su vida no cambia aquí.
    /// Algunos enemigos restan siempre 1 por golpe (<c>damageOverride</c>), así que la vida se guarda y se repone, y
    /// nunca baja a 0 en medio (moriría solo en este PC).
    /// </summary>
    private static void HitWithoutDamage(HealthManager health, HitInstance hit, On.HealthManager.orig_Hit orig) {
        var hp = health.hp;
        health.hp = Math.Max(hp, 2);
        hit.Multiplier = 0f;
        try {
            if (orig != null) {
                orig(health, hit);
            } else {
                health.Hit(hit);
            }
        } finally {
            health.hp = hp;
        }
    }

    // =====================================================================================================
    // Golpes locales a marionetas
    // =====================================================================================================

    private static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.HealthManager.Hit += (orig, self, hit) => {
            var sync = _active;
            if (_playingFx || sync == null || !sync._byHealth.TryGetValue(self, out var enemy) || !enemy.Puppet) {
                orig(self, hit);
                return;
            }

            sync.SendHit(enemy, hit);
            if (self.hp > 0) {
                // Efectos del golpe y alma para quien golpea, sin quitar vida aquí (la quita el dueño)
                HitWithoutDamage(self, hit, orig);
            }
        };

        // Estados de la lógica de los enemigos reales: los que suenan, también en las marionetas
        On.HutongGames.PlayMaker.Fsm.SwitchState += (orig, self, toState) => {
            orig(self, toState);
            var sync = _active;
            if (sync?._session != null && toState != null && sync._soundFsms.Count > 0) {
                sync.OnFsmState(self, toState);
            }
        };

        // Daño recibido por un enemigo real: los demás de la sala lo ven
        On.HealthManager.TakeDamage += (orig, self, hit) => {
            orig(self, hit);
            var sync = _active;
            if (!_playingFx && sync?._session != null && sync._byHealth.TryGetValue(self, out var enemy) && !enemy.Puppet) {
                sync.SendHitFx(enemy, hit);
            }
        };

        // Descansar en un banco (el juego fija ahí el punto de reaparición)
        On.PlayerData.SetBenchRespawn_RespawnMarker_string_int += (orig, self, marker, scene, type) => {
            orig(self, marker, scene, type);
            _active?.OnLocalRest();
        };
        On.PlayerData.SetBenchRespawn_string_string_bool += (orig, self, marker, scene, facingRight) => {
            orig(self, marker, scene, facingRight);
            _active?.OnLocalRest();
        };
        On.PlayerData.SetBenchRespawn_string_string_int_bool += (orig, self, marker, scene, type, facingRight) => {
            orig(self, marker, scene, type, facingRight);
            _active?.OnLocalRest();
        };

        // Los golpes de otros aplicados en este PC no dan alma a este jugador
        On.HeroController.SoulGain += (orig, self) => {
            if (!_applyingRemoteHit) {
                orig(self);
            }
        };
    }

    private void SendHit(Enemy enemy, HitInstance hit) {
        if (!_session.CanSend || hit.DamageDealt <= 0 || enemy.Health.isDead) {
            return;
        }

        var writer = _session.Begin(EnemyHit);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(enemy.Id);
        writer.WriteInt(hit.DamageDealt);
        writer.WriteByte((byte) hit.AttackType);
        writer.WriteFloat(hit.GetActualDirection(enemy.Transform));
        writer.WriteBool(hit.IgnoreInvulnerable);
        writer.WriteFloat(hit.MagnitudeMultiplier);
        writer.WriteFloat(hit.Multiplier);
        writer.WriteByte((byte) hit.SpecialType);
        _session.End();
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    /// <summary>Golpes y sonidos de otros reproducidos en marionetas (modo de prueba).</summary>
    public int HitFxPlayed { get; private set; }

    public int SoundsPlayed { get; private set; }

    public IEnumerable<string> Describe() {
        yield return $"efectos de golpes ajenos={HitFxPlayed} sonidos de marionetas={SoundsPlayed} " +
                     $"fsm con sonido={_soundFsms.Count}";
        foreach (var enemy in _enemies) {
            if (enemy.Root == null) {
                yield return $"id={enemy.Id:x8} MUERTO (ya no existe)";
                continue;
            }

            var p = enemy.Transform.position;
            yield return $"{enemy.Root.name} id={enemy.Id:x8} {(enemy.Puppet ? "marioneta" : "real")} hp={enemy.Health.hp}" +
                         $"{(enemy.Health.isDead ? " MUERTO" : "")}{(enemy.Health.IsInvincible ? " invencible" : "")}" +
                         $"{(enemy.Root.activeInHierarchy ? "" : " (inactivo)")} " +
                         $"pos=({p.x:0.00},{p.y:0.00})";
        }
    }

    /// <summary>Golpea al enemigo vivo y activo con ese nombre, como un golpe de aguijón del jugador.</summary>
    public bool TestHit(string namePrefix, int damage) {
        var hero = HeroController.instance;
        foreach (var enemy in _enemies) {
            if (enemy.Root != null && enemy.Root.activeInHierarchy && !enemy.Health.isDead &&
                string.Equals(enemy.Root.name, namePrefix, StringComparison.OrdinalIgnoreCase)) {
                enemy.Health.Hit(new HitInstance {
                    Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = damage, Multiplier = 1f,
                    MagnitudeMultiplier = 1f, Direction = 0f
                });
                return true;
            }
        }

        return false;
    }

    /// <summary>Lleva a este jugador junto al primer enemigo con ese nombre.</summary>
    public bool TryGetPosition(string namePrefix, out Vector3 position) {
        foreach (var enemy in _enemies) {
            if (enemy.Root != null && enemy.Root.name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) {
                position = enemy.Transform.position;
                return true;
            }
        }

        position = default;
        return false;
    }
}
