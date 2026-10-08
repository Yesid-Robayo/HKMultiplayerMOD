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

    /// <summary>Enemigos muertos en una sala (dueño → host, que los recuerda hasta que alguien descansa).</summary>
    public const MessageType EnemyKilled = (MessageType) 46;

    /// <summary>Un jugador descansó en un banco (→ host): los enemigos vuelven a aparecer.</summary>
    public const MessageType Rested = (MessageType) 47;

    private const int StateTickDivider = 2; // 60 / 2 = 30 envíos por segundo
    private const int KeepAliveTicks = 30;
    private const int MaxEntriesPerMessage = 50;

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

    static EnemySync() {
        MessageRoutes.Register(EnemyStates, Route.SameScene, reliable: false);
        MessageRoutes.Register(EnemyHit, Route.SceneOwner, reliable: true);
        MessageRoutes.Register(EnemyHealth, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyKilled, Route.Server, reliable: true);
        MessageRoutes.Register(Rested, Route.Server, reliable: true);
    }

    public EnemySync() {
        _onStates = OnStates;
        _onHit = OnHit;
        _onHealth = OnHealth;
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
            if (state.SameLook(enemy.LastSent) && tick - enemy.LastSentTick < KeepAliveTicks) {
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

    /// <summary>Estado del enemigo: posición, orientación, si se ve, animación (clip en SpriteId, fotograma en Collection).</summary>
    private static PlayerSnapshot Capture(Enemy enemy, uint tick) {
        var position = enemy.Transform.position;
        var state = new PlayerSnapshot {
            Tick = tick,
            X = position.x,
            Y = position.y,
            FacingRight = enemy.Transform.localScale.x < 0f,
            Visible = enemy.Root.activeInHierarchy
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

            var position = enemy.Transform.position;
            enemy.Transform.position = new Vector3(state.X, state.Y, position.z);
            var scale = enemy.Transform.localScale;
            var facingRight = scale.x < 0f;
            if (facingRight != state.FacingRight) {
                scale.x = -scale.x;
                enemy.Transform.localScale = scale;
            }

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
                        LastHp = health.hp
                    };
                    _enemies.Add(enemy);
                    _byId[id] = enemy;
                    _byHealth[health] = enemy;
                    if (_pendingHealth.TryGetValue(id, out var pending)) {
                        ApplyHealth(enemy, pending.Kind, pending.Hp);
                    }
                }
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
        try {
            enemy.Health.Hit(hit);
        } finally {
            _applyingRemoteHit = false;
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
            if (sync == null || !sync._byHealth.TryGetValue(self, out var enemy) || !enemy.Puppet) {
                orig(self, hit);
                return;
            }

            sync.SendHit(enemy, hit);
            if (self.hp > 0) {
                // Efectos del golpe y alma para quien golpea, sin quitar vida aquí (la quita el dueño)
                hit.Multiplier = 0f;
                orig(self, hit);
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

    public IEnumerable<string> Describe() {
        foreach (var enemy in _enemies) {
            if (enemy.Root == null) {
                yield return $"id={enemy.Id:x8} MUERTO (ya no existe)";
                continue;
            }

            var p = enemy.Transform.position;
            yield return $"{enemy.Root.name} id={enemy.Id:x8} {(enemy.Puppet ? "marioneta" : "real")} hp={enemy.Health.hp}" +
                         $"{(enemy.Health.isDead ? " MUERTO" : "")}{(enemy.Root.activeInHierarchy ? "" : " (inactivo)")} " +
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
