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
/// Enemigos compartidos. Cada enemigo lo mueve el PC del jugador más cercano (lo reparte el dueño de la sala, el primero
/// que entró), porque su lógica solo ataca al caballero de su PC. Ese PC lo hace funcionar normal y envía su estado
/// (posición, orientación, animación, piezas) 30 veces por segundo y cada cambio de vida o muerte. En los demás PCs es una
/// marioneta: su lógica (FSM, movimiento, física) se pausa y se dibuja con lo recibido, interpolado; sigue haciendo daño
/// al tocarla.
/// <list type="bullet">
/// <item>Los golpes a una marioneta van al PC que la mueve, que los aplica al enemigo real. El que golpea ve los efectos
/// y gana su alma como siempre; los demás ven el golpe.</item>
/// <item>Al cambiar quién lo mueve, el anterior envía cómo iba y el nuevo lo sigue desde ahí.</item>
/// <item>Lo que crean en combate (proyectiles, crías) aparece también en los demás PCs.</item>
/// <item>Al entrar alguien en la sala, el dueño envía qué enemigos están muertos y su vida: los muertos no
/// reaparecen y los heridos siguen heridos.</item>
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

    /// <summary>La lógica de un enemigo real creó algo (proyectil, cría, efecto) (dueño → sala).</summary>
    public const MessageType EnemySpawn = (MessageType) 63;

    /// <summary>Algo creado por un enemigo desapareció en el PC del dueño (dueño → sala).</summary>
    public const MessageType EnemyDespawn = (MessageType) 64;

    /// <summary>Quién mueve cada enemigo (dueño de la sala → sala): el jugador más cercano.</summary>
    public const MessageType EnemyControl = (MessageType) 65;

    /// <summary>Cómo iba un enemigo (estado y números de una FSM) para quien pasa a moverlo.</summary>
    public const MessageType EnemyHandover = (MessageType) 66;

    /// <summary>
    /// El ataque de un jugador avisó a la marioneta de que recibe daño ("TAKE DAMAGE", aparte del golpe): se avisa al PC
    /// que la mueve (con ese aviso despiertan o reaccionan muchos enemigos, como Mamá Gruz dormida).
    /// </summary>
    public const MessageType EnemyEvent = (MessageType) 67;

    private const string TakeDamageEvent = "TAKE DAMAGE";

    /// <summary>Piezas que se mueven aparte (hijos con animación o que dañan) (dueño → sala, no fiable).</summary>
    public const MessageType EnemyParts = (MessageType) 68;

    private const int MaxParts = 24;

    /// <summary>A partir de aquí, las piezas siguientes van en otro mensaje (cada pieza ocupa 15 bytes).</summary>
    private const int PartsMessageLimit = 900;

    /// <summary>El otro jugador tiene que estar al menos esto más cerca para que el enemigo pase a su PC.</summary>
    private const float SwitchMargin = 4f;

    /// <summary>Tiempo mínimo entre cambios de quién mueve un enemigo (más en los jefes: cada cambio corta su ataque).</summary>
    private const float SwitchInterval = 3f;

    private const float BossSwitchInterval = 8f;

    /// <summary>Vida a partir de la cual un enemigo se trata como jefe.</summary>
    private const int BossHp = 150;

    /// <summary>Tras encontrar un enemigo, se le deja iniciar su lógica antes de pararla (si no, la retoma sin iniciar).</summary>
    private const float PuppetDelay = 0.5f;

    private const int MaxHandoverVars = 48;

    /// <summary>Si no llega cómo iba, el nuevo lo mueve igualmente pasado este tiempo (desde el principio de su lógica).</summary>
    private const float HandoverTimeout = 1.5f;

    /// <summary>Los objetos creados durante el combate llevan ids con este bit (los de la sala, su ruta).</summary>
    private const uint SpawnedIdFlag = 0x80000000u;

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

        /// <summary>Piezas que se mueven aparte (null si no tiene).</summary>
        public Part[] Parts;
        public bool Puppet;

        /// <summary>Creado durante el combate (no es de la sala); si vino del pool, vuelve a él al desaparecer.</summary>
        public bool Spawned;

        public bool Pooled;

        /// <summary>Quién lo mueve (0: el dueño de la sala) y quién lo movía antes del último cambio.</summary>
        public ushort Controller;

        public ushort PreviousController;
        public float LastSwitch;
        public float ScannedAt;

        /// <summary>Cómo iba, recibido del que lo movía antes (al pasar a moverlo este PC).</summary>
        public HandoverFsm[] Handover;

        public bool HandoverReady;
        public readonly List<Behaviour> Disabled = new();

        /// <summary>FSM paradas en la marioneta y si antes reiniciaban al reactivarse (se pausan, no se reinician).</summary>
        public readonly List<(PlayMakerFSM Fsm, bool Restart)> PausedFsms = new();
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

    /// <summary>Pieza de un enemigo y lo último enviado de ella.</summary>
    private sealed class Part {
        public Transform Transform;
        public tk2dSpriteAnimator Animator;
        public tk2dSpriteAnimationClip LastClipRef;
        public ushort ClipIndex;
        public byte Flags;
        public float X;
        public float Y;
        public ushort Rotation;
        public ushort Clip;
        public byte Frame;
    }

    private sealed class HandoverFsm {
        public ushort State;
        public int[] Ints;
        public float[] Floats;
        public bool[] Bools;
        public bool HasBody;
        public Vector2 Velocity;
        public float Gravity;
        public RigidbodyType2D BodyType;
    }

    /// <summary>FSM de enemigos que tienen estados con sonido: su enemigo, su posición y qué estados suenan.</summary>
    private sealed class SoundFsm {
        public Enemy Enemy;
        public byte Index;
        public bool[] SoundStates;
    }

    private readonly Dictionary<HutongGames.PlayMaker.Fsm, SoundFsm> _soundFsms = new();

    /// <summary>Todas las FSM de los enemigos: a qué enemigo pertenecen y en qué posición (para lo que crean).</summary>
    private readonly Dictionary<HutongGames.PlayMaker.Fsm, (Enemy Enemy, byte Index)> _enemyFsms = new();

    private uint _spawnCounter;

    /// <summary>Se está creando aquí la copia de algo que creó un enemigo en otro PC (no se vuelve a avisar).</summary>
    private static bool _spawningCopy;

    /// <summary>Este PC está retomando un enemigo: lo que cree al ponerse en su estado no se replica.</summary>
    private static bool _takingOver;

    /// <summary>Tiempo tras encontrar un enemigo en que lo que crea es de su preparación (no se replica).</summary>
    private const float PreparationSeconds = 1.5f;

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
    private readonly MessageHandler _onSpawn;
    private readonly MessageHandler _onDespawn;
    private readonly MessageHandler _onControl;
    private readonly MessageHandler _onHandover;
    private readonly MessageHandler _onEvent;
    private readonly MessageHandler _onParts;
    private uint _lastPartsKeepAlive;
    private readonly List<(uint Id, ushort Controller)> _controlOut = new();
    private float _nextAssign;
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
        // A la sala: lo aplica el PC que mueve ese enemigo (los demás lo tienen como marioneta)
        MessageRoutes.Register(EnemyHit, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyHealth, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyHitFx, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemySound, Route.SameScene, reliable: false);
        MessageRoutes.Register(EnemySpawn, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyDespawn, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyControl, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyHandover, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyEvent, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyParts, Route.SameScene, reliable: false);
        MessageRoutes.Register(EnemyKilled, Route.Server, reliable: true);
        MessageRoutes.Register(Rested, Route.Server, reliable: true);
    }

    public EnemySync() {
        _onStates = OnStates;
        _onHit = OnHit;
        _onHealth = OnHealth;
        _onHitFx = OnHitFx;
        _onSound = OnSound;
        _onSpawn = OnSpawn;
        _onDespawn = OnDespawn;
        _onControl = OnControl;
        _onHandover = OnHandover;
        _onEvent = OnEnemyEvent;
        _onParts = OnParts;
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
        session.On(EnemySpawn, _onSpawn);
        session.On(EnemyDespawn, _onDespawn);
        session.On(EnemyControl, _onControl);
        session.On(EnemyHandover, _onHandover);
        session.On(EnemyEvent, _onEvent);
        session.On(EnemyParts, _onParts);
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
            _session.On(EnemySpawn, null);
            _session.On(EnemyDespawn, null);
            _session.On(EnemyControl, null);
            _session.On(EnemyHandover, null);
            _session.On(EnemyEvent, null);
            _session.On(EnemyParts, null);
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
        RemoveSpawnedCopies();
        _enemies.Clear();
        _byId.Clear();
        _byHealth.Clear();
        _soundFsms.Clear();
        _enemyFsms.Clear();
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
            RemoveSpawnedCopies();
            _enemies.Clear();
            _byId.Clear();
            _byHealth.Clear();
            _soundFsms.Clear();
            _enemyFsms.Clear();
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
        var now = Time.unscaledTime;
        if (owner != 0) {
            if (isOwner && now >= _nextAssign && _session.CanSend) {
                _nextAssign = now + 0.5f;
                AssignControllers();
            }

            for (var i = _enemies.Count - 1; i >= 0; i--) {
                var enemy = _enemies[i];
                if (enemy.Spawned) {
                    // Copia de algo creado por quien ya no está en la sala: aquí ya nadie la mueve
                    if (enemy.Puppet && !InScene(enemy.Controller)) {
                        RemoveSpawned(enemy);
                    }

                    continue;
                }

                // Los muertos se quedan como están (su muerte sigue su curso)
                if (enemy.Health == null || enemy.Health.isDead) {
                    continue;
                }

                var local = ControllerOf(enemy, owner) == _session.LocalId;
                if (local && enemy.Puppet) {
                    // Pasa a moverlo este PC: desde como lo dejó el anterior (si sigue aquí, espera a saberlo)
                    if (enemy.HandoverReady || enemy.PreviousController == _session.LocalId ||
                        !InScene(enemy.PreviousController) || now - enemy.LastSwitch > HandoverTimeout) {
                        TakeOver(enemy);
                    }
                } else if (!local && !enemy.Puppet && now - enemy.ScannedAt >= PuppetDelay) {
                    SetPuppet(enemy, true);
                }
            }
        }

        UpdateReal(isOwner);
        UpdatePuppets();
    }

    private bool OthersInScene() {
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Los enemigos que mueve este PC: su vida, muerte y estado a los demás (el dueño de la sala, también la foto).</summary>
    private void UpdateReal(bool roomOwner) {
        if (!_session.CanSend) {
            return;
        }

        var others = OthersInScene();
        var snapshot = _sendSnapshot && roomOwner;

        // Vida y muertes (fiable). Las muertes se apuntan en el host aunque no haya nadie más en la sala
        _healthOut.Clear();
        _killedOut.Clear();
        for (var i = _enemies.Count - 1; i >= 0; i--) {
            var enemy = _enemies[i];
            // Lo creado en combate que ya no está (vuelto al pool o destruido): desaparece también en los demás
            if (enemy.Spawned && (enemy.Root == null || !enemy.Root.activeInHierarchy)) {
                SendDespawn(enemy.Id);
                Forget(enemy);
                continue;
            }

            if (enemy.Spawned && enemy.Health == null) {
                continue;
            }

            // Los que mueve otro PC: solo entran en la foto para quien llega
            if (enemy.Puppet) {
                if (snapshot && enemy.Health != null) {
                    _healthOut.Add(enemy.ReportedDead || enemy.Health.isDead
                        ? (enemy.Id, HealthKind.AlreadyDead, 0)
                        : (enemy.Id, HealthKind.Update, enemy.Health.hp));
                }

                continue;
            }

            // Los muertos se recuerdan aunque su objeto ya no exista (el juego lo destruye al morir); dentro de la
            // sala, un enemigo destruido es un enemigo muerto
            if (enemy.ReportedDead) {
                if (snapshot) {
                    _healthOut.Add((enemy.Id, HealthKind.AlreadyDead, 0));
                }

                continue;
            }

            var dead = enemy.Health == null || enemy.Health.isDead;
            if (dead) {
                enemy.ReportedDead = true;
                _healthOut.Add((enemy.Id, HealthKind.Died, 0));
                if (!enemy.Spawned) {
                    _killedOut.Add(enemy.Id);
                }
            } else if (enemy.Health.hp != enemy.LastHp || snapshot) {
                enemy.LastHp = enemy.Health.hp;
                _healthOut.Add((enemy.Id, HealthKind.Update, enemy.LastHp));
            }
        }

        if (roomOwner) {
            _sendSnapshot = false;
        }

        ReportKilled();
        if (!others) {
            return;
        }

        SendHealth();

        // Estado continuo (no fiable), 30 veces por segundo; las piezas, a la vez
        var tick = (uint) (Time.unscaledTimeAsDouble * NetConstants.TickRate);
        if (tick / StateTickDivider == _lastTick / StateTickDivider) {
            return;
        }

        _lastTick = tick;
        SendParts(tick);
        PacketWriter writer = null;
        var countPosition = 0;
        var count = 0;
        for (var i = 0; i < _enemies.Count; i++) {
            var enemy = _enemies[i];
            if (enemy.Root == null || enemy.ReportedDead || enemy.Puppet) {
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
        var extra = health != null ? (health.InvincibleFromDirection & 0x0F) << 4 : 0;
        if (enemy.Renderer == null || enemy.Renderer.enabled) {
            extra |= ShownFlag;
        }

        if (enemy.Collider == null || enemy.Collider.enabled) {
            extra |= SolidFlag;
        }

        if (health != null && health.IsInvincible) {
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
            if (!enemy.Puppet || enemy.Root == null || enemy.Health != null && enemy.Health.isDead ||
                !enemy.Buffer.Sample(time, out var state)) {
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

            if (enemy.Health != null) {
                enemy.Health.IsInvincible = (state.Extra & InvincibleFlag) != 0;
                enemy.Health.InvincibleFromDirection = state.Extra >> 4;
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
                    // La sombra es de cada jugador (su geo perdido): no es un enemigo compartido
                    if (_byHealth.ContainsKey(health) ||
                        health.gameObject.name.StartsWith("Hollow Shade", StringComparison.Ordinal)) {
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
                    enemy.ScannedAt = Time.unscaledTime;
                    _enemies.Add(enemy);
                    _byId[id] = enemy;
                    _byHealth[health] = enemy;
                    RegisterFsms(enemy);
                    if (_pendingHealth.TryGetValue(id, out var pending)) {
                        ApplyHealth(enemy, pending.Kind, pending.Hp);
                    }
                }
            }
        }
    }

    /// <summary>Apunta las FSM del enemigo: de quién son y qué estados suenan (solo al entrar o al crearse).</summary>
    private void RegisterFsms(Enemy enemy) {
        CollectParts(enemy);
        enemy.Fsms = enemy.Root.GetComponentsInChildren<PlayMakerFSM>(true);
        for (var i = 0; i < enemy.Fsms.Length && i < byte.MaxValue; i++) {
            _enemyFsms[enemy.Fsms[i].Fsm] = (enemy, (byte) i);
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

    // =====================================================================================================
    // Quién mueve cada enemigo: el jugador más cercano
    // =====================================================================================================

    /// <summary>Quién mueve el enemigo: su controlador si sigue en la sala; si no, el dueño de la sala.</summary>
    private ushort ControllerOf(Enemy enemy, ushort roomOwner) =>
        enemy.Controller != 0 && InScene(enemy.Controller) ? enemy.Controller : roomOwner;

    private bool InScene(ushort playerId) {
        if (playerId == 0) {
            return false;
        }

        foreach (var player in _session.Players) {
            if (player.Id == playerId) {
                return player.Scene == _scene;
            }
        }

        return false;
    }

    private bool TryGetPlayerPosition(ushort playerId, out Vector3 position) {
        if (playerId == _session.LocalId) {
            var hero = HeroController.instance;
            position = hero != null ? hero.transform.position : default;
            return hero != null;
        }

        return _players.TryGetAvatarPosition(playerId, out position);
    }

    /// <summary>
    /// El dueño de la sala reparte los enemigos: cada uno lo mueve el PC del jugador más cercano, porque su lógica solo
    /// ataca al caballero de ese PC. Se cambia solo si el otro está claramente más cerca y hace un rato del último cambio.
    /// </summary>
    private void AssignControllers() {
        var now = Time.unscaledTime;
        var roomOwner = _session.LocalId;
        _controlOut.Clear();
        foreach (var enemy in _enemies) {
            if (enemy.Spawned || enemy.Root == null || !enemy.Root.activeInHierarchy || enemy.Health == null ||
                enemy.Health.isDead) {
                continue;
            }

            var interval = enemy.Health.hp >= BossHp ? BossSwitchInterval : SwitchInterval;
            if (now - enemy.LastSwitch < interval) {
                continue;
            }

            var position = enemy.Transform.position;
            var current = ControllerOf(enemy, roomOwner);
            var currentDistance = TryGetPlayerPosition(current, out var currentPosition)
                ? Vector2.Distance(position, currentPosition)
                : float.MaxValue;
            var nearest = current;
            var nearestDistance = currentDistance;
            foreach (var player in _session.Players) {
                if (player.Scene != _scene || player.Id == current || !TryGetPlayerPosition(player.Id, out var playerPosition)) {
                    continue;
                }

                var distance = Vector2.Distance(position, playerPosition);
                if (distance < nearestDistance) {
                    nearest = player.Id;
                    nearestDistance = distance;
                }
            }

            if (nearest != current && currentDistance - nearestDistance > SwitchMargin) {
                _controlOut.Add((enemy.Id, nearest));
                ApplyControl(enemy, nearest);
            }
        }

        for (var start = 0; start < _controlOut.Count; start += MaxEntriesPerMessage) {
            var count = Math.Min(MaxEntriesPerMessage, _controlOut.Count - start);
            var writer = _session.Begin(EnemyControl);
            writer.WriteUShort(_sceneHash);
            writer.WriteByte((byte) count);
            for (var i = start; i < start + count; i++) {
                writer.WriteUInt(_controlOut[i].Id);
                writer.WriteUShort(_controlOut[i].Controller);
            }

            _session.End();
        }
    }

    private void OnControl(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var count = reader.ReadByte();
        for (var i = 0; i < count; i++) {
            var id = reader.ReadUInt();
            var controller = reader.ReadUShort();
            // Solo lo decide el dueño de la sala
            if (sceneHash == _sceneHash && from.Id == _session.OwnerOf(_scene) && _byId.TryGetValue(id, out var enemy) &&
                !enemy.Spawned) {
                ApplyControl(enemy, controller);
            }
        }
    }

    /// <summary>
    /// Cambia quién mueve el enemigo. Si lo movía este PC, envía al nuevo cómo iba (estado y números de cada FSM) y pasa
    /// a ser marioneta; el nuevo lo sigue desde ahí en cuanto lo recibe.
    /// </summary>
    private void ApplyControl(Enemy enemy, ushort controller) {
        var previous = ControllerOf(enemy, _session.OwnerOf(_scene));
        if (previous == controller) {
            return;
        }

        enemy.Controller = controller;
        enemy.PreviousController = previous;
        enemy.LastSwitch = Time.unscaledTime;
        // Cómo iba puede haber llegado ya (del anterior, antes que el aviso del dueño): se guarda si es para este PC
        if (controller != _session.LocalId) {
            enemy.Handover = null;
            enemy.HandoverReady = false;
        }
        if (previous == _session.LocalId && !enemy.Puppet && enemy.Health != null && !enemy.Health.isDead) {
            SendHandover(enemy);
            SetPuppet(enemy, true);
        }
    }

    /// <summary>
    /// Este jugador sale de la sala (cambia de sala, muere y reaparece en otra): aún dentro, deja dicho cómo iba cada
    /// enemigo que movía. Quien lo pase a mover lo sigue desde ahí en vez de empezar su lógica de nuevo (un jefe no
    /// repite su presentación).
    /// </summary>
    private void ReleaseAll() {
        if (_session == null || !_session.CanSend || _scene.Length == 0 || !OthersInScene()) {
            return;
        }

        foreach (var enemy in _enemies) {
            if (!enemy.Puppet && !enemy.Spawned && enemy.Root != null && enemy.Health != null && !enemy.Health.isDead) {
                SendHandover(enemy);
            }
        }
    }

    /// <summary>Cómo va el enemigo (estado y números de cada FSM) para el PC que pasa a moverlo; un mensaje por FSM.</summary>
    private void SendHandover(Enemy enemy) {
        if (!_session.CanSend || enemy.Fsms == null) {
            return;
        }

        var fsmCount = Math.Min(enemy.Fsms.Length, byte.MaxValue);
        for (var i = 0; i < fsmCount; i++) {
            var fsm = enemy.Fsms[i];
            var writer = _session.Begin(EnemyHandover);
            writer.WriteUShort(_sceneHash);
            writer.WriteUInt(enemy.Id);
            writer.WriteByte((byte) i);
            writer.WriteByte((byte) fsmCount);
            var stateIndex = fsm != null && fsm.Fsm.ActiveState != null ? Array.IndexOf(fsm.Fsm.States, fsm.Fsm.ActiveState) : -1;
            writer.WriteUShort(stateIndex < 0 ? ushort.MaxValue : (ushort) stateIndex);
            var variables = fsm != null ? fsm.FsmVariables : null;
            var ints = variables?.IntVariables;
            var intCount = ints == null ? 0 : Math.Min(ints.Length, MaxHandoverVars);
            writer.WriteByte((byte) intCount);
            for (var v = 0; v < intCount; v++) {
                writer.WriteInt(ints[v].Value);
            }

            var floats = variables?.FloatVariables;
            var floatCount = floats == null ? 0 : Math.Min(floats.Length, MaxHandoverVars);
            writer.WriteByte((byte) floatCount);
            for (var v = 0; v < floatCount; v++) {
                writer.WriteFloat(floats[v].Value);
            }

            var bools = variables?.BoolVariables;
            var boolCount = bools == null ? 0 : Math.Min(bools.Length, MaxHandoverVars);
            writer.WriteByte((byte) boolCount);
            for (var v = 0; v < boolCount; v++) {
                writer.WriteBool(bools[v].Value);
            }

            // La física con la que iba (a mitad de un salto, su gravedad la pone el estado que ya pasó)
            var body = enemy.Body;
            writer.WriteBool(body != null);
            if (body != null) {
                writer.WriteFloat(body.velocity.x);
                writer.WriteFloat(body.velocity.y);
                writer.WriteFloat(body.gravityScale);
                // Tipo de cuerpo con el que iba (dormido, un jefe suele ser cinemático; despierto, no)
                writer.WriteByte((byte) (body.bodyType == RigidbodyType2D.Kinematic && enemy.Puppet
                    ? enemy.BodyType
                    : body.bodyType));
            }

            _session.End();
        }
    }

    private void OnHandover(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var index = reader.ReadByte();
        var count = reader.ReadByte();
        var state = reader.ReadUShort();
        var ints = new int[reader.ReadByte()];
        for (var v = 0; v < ints.Length; v++) {
            ints[v] = reader.ReadInt();
        }

        var floats = new float[reader.ReadByte()];
        for (var v = 0; v < floats.Length; v++) {
            floats[v] = reader.ReadFloat();
        }

        var bools = new bool[reader.ReadByte()];
        for (var v = 0; v < bools.Length; v++) {
            bools[v] = reader.ReadBool();
        }

        var hasBody = reader.ReadBool();
        var velocity = Vector2.zero;
        var gravity = 0f;
        var bodyType = RigidbodyType2D.Dynamic;
        if (hasBody) {
            velocity = new Vector2(reader.ReadFloat(), reader.ReadFloat());
            gravity = reader.ReadFloat();
            bodyType = (RigidbodyType2D) reader.ReadByte();
        }

        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var enemy) || !enemy.Puppet || index >= count) {
            return;
        }

        enemy.Handover ??= new HandoverFsm[count];
        if (enemy.Handover.Length != count) {
            enemy.Handover = new HandoverFsm[count];
        }

        enemy.Handover[index] = new HandoverFsm {
            State = state, Ints = ints, Floats = floats, Bools = bools, HasBody = hasBody, Velocity = velocity, Gravity = gravity,
            BodyType = bodyType
        };
        if (index == count - 1) {
            enemy.HandoverReady = true;
        }
    }

    /// <summary>
    /// Este PC pasa a mover el enemigo: su lógica vuelve a funcionar (empieza de nuevo, con este caballero como objetivo)
    /// y se pone donde la dejó el anterior: el mismo estado y los mismos números en cada FSM.
    /// </summary>
    private void TakeOver(Enemy enemy) {
        _takingOver = true;
        try {
            TakeOverUnguarded(enemy);
        } finally {
            _takingOver = false;
        }
    }

    private void TakeOverUnguarded(Enemy enemy) {
        SetPuppet(enemy, false);
        var handover = enemy.Handover;
        enemy.Handover = null;
        enemy.HandoverReady = false;
        if (enemy.Body != null) {
            enemy.Body.velocity = Vector2.zero;
        }

        if (handover == null || enemy.Fsms == null) {
            return;
        }

        for (var i = 0; i < handover.Length && i < enemy.Fsms.Length; i++) {
            var data = handover[i];
            var fsm = enemy.Fsms[i];
            if (data == null || fsm == null) {
                continue;
            }

            var variables = fsm.FsmVariables;
            for (var v = 0; v < data.Ints.Length && v < variables.IntVariables.Length; v++) {
                variables.IntVariables[v].Value = data.Ints[v];
            }

            for (var v = 0; v < data.Floats.Length && v < variables.FloatVariables.Length; v++) {
                variables.FloatVariables[v].Value = data.Floats[v];
            }

            for (var v = 0; v < data.Bools.Length && v < variables.BoolVariables.Length; v++) {
                variables.BoolVariables[v].Value = data.Bools[v];
            }

            var states = fsm.Fsm.States;
            if (data.State < states.Length && fsm.Fsm.ActiveState != states[data.State]) {
                try {
                    fsm.Fsm.SetState(states[data.State].Name);
                } catch (Exception e) {
                    Log.Warn($"No se pudo seguir {enemy.Root.name} desde {states[data.State].Name}: {e.Message}");
                }
            }
        }

        var last = handover[handover.Length - 1];
        if (enemy.Body != null && last != null && last.HasBody) {
            enemy.Body.bodyType = last.BodyType;
            enemy.Body.gravityScale = last.Gravity;
            enemy.Body.velocity = last.Velocity;
        }

        var first = handover.Length > 0 && enemy.Fsms.Length > 0 && enemy.Fsms[0] != null ? enemy.Fsms[0].ActiveStateName : "-";
        Log.Info($"{enemy.Root.name}: lo mueve este PC, sigue desde {first}");
    }

    // =====================================================================================================
    // Lo que crean los enemigos en combate
    // =====================================================================================================

    /// <summary>
    /// La lógica de un enemigo real de este PC creó algo. Los demás de la sala crean lo mismo con la misma acción de su
    /// marioneta. Si es de juego (hace daño o tiene vida: proyectiles, crías, invocaciones) se sigue como un enemigo más
    /// hasta que desaparece; si es solo visual (polvo, destellos), cada PC lo deja correr solo.
    /// </summary>
    private void OnSpawned(HutongGames.PlayMaker.FsmStateAction action, GameObject spawned, bool pooled) {
        if (_spawningCopy || _takingOver || spawned == null || _session == null || !_session.CanSend ||
            !_enemyFsms.TryGetValue(action.Fsm, out var creator) || creator.Enemy.Puppet || !OthersInScene()) {
            return;
        }

        // Lo que crea al prepararse (cadáveres, piezas propias) ya lo crea cada PC por su cuenta; y lo solo visual hecho
        // con CreateObject suele quedarse como pieza escondida del enemigo, no es un efecto que se acabe solo
        var gameplay = IsGameplay(spawned);
        if (Time.unscaledTime - creator.Enemy.ScannedAt < PreparationSeconds || !pooled && !gameplay) {
            return;
        }

        var fsm = action.Fsm;
        var stateIndex = Array.IndexOf(fsm.States, action.State);
        var actionIndex = action.State != null ? Array.IndexOf(action.State.Actions, action) : -1;
        if (stateIndex < 0 || actionIndex < 0 || actionIndex > byte.MaxValue) {
            return;
        }

        uint id = 0;
        if (gameplay) {
            id = SpawnedIdFlag | ++_spawnCounter;
            Track(spawned, id, pooled, puppet: false, _session.LocalId);
        }

        var transform = spawned.transform;
        var position = transform.position;
        var writer = _session.Begin(EnemySpawn);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(creator.Enemy.Id);
        writer.WriteByte(creator.Index);
        writer.WriteUShort((ushort) stateIndex);
        writer.WriteByte((byte) actionIndex);
        writer.WriteUInt(id);
        writer.WriteFloat(position.x);
        writer.WriteFloat(position.y);
        writer.WriteFloat(transform.eulerAngles.z);
        writer.WriteBool(transform.localScale.x < 0f);
        _session.End();
    }

    /// <summary>Si lo creado cuenta para el juego: hace daño al tocarlo o se le puede golpear.</summary>
    private static bool IsGameplay(GameObject spawned) =>
        spawned.GetComponent<HealthManager>() != null || spawned.GetComponentInChildren<DamageHero>(true) != null;

    private Enemy Track(GameObject root, uint id, bool pooled, bool puppet, ushort controller) {
        var health = root.GetComponent<HealthManager>();
        var enemy = new Enemy {
            Id = id,
            Health = health,
            Root = root,
            Transform = root.transform,
            Animator = root.GetComponent<tk2dSpriteAnimator>(),
            Body = root.GetComponent<Rigidbody2D>(),
            Renderer = root.GetComponent<Renderer>(),
            Collider = root.GetComponent<Collider2D>(),
            LastHp = health != null ? health.hp : 0,
            Spawned = true,
            Pooled = pooled,
            Controller = controller,
            ScannedAt = Time.unscaledTime
        };
        _enemies.Add(enemy);
        _byId[id] = enemy;
        if (health != null) {
            _byHealth[health] = enemy;
        }

        RegisterFsms(enemy);
        if (puppet) {
            SetPuppet(enemy, true);
        }

        return enemy;
    }

    private void OnSpawn(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var creatorId = reader.ReadUInt();
        var fsmIndex = reader.ReadByte();
        var stateIndex = reader.ReadUShort();
        var actionIndex = reader.ReadByte();
        var id = reader.ReadUInt();
        var position = new Vector3(reader.ReadFloat(), reader.ReadFloat(), 0f);
        var rotation = reader.ReadFloat();
        var facingRight = reader.ReadBool();
        if (sceneHash != _sceneHash || !_byId.TryGetValue(creatorId, out var creator) || !creator.Puppet ||
            creator.Fsms == null || fsmIndex >= creator.Fsms.Length || creator.Fsms[fsmIndex] == null ||
            id != 0 && (id & SpawnedIdFlag) == 0) {
            return;
        }

        var states = creator.Fsms[fsmIndex].FsmStates;
        if (stateIndex >= states.Length || actionIndex >= states[stateIndex].Actions.Length) {
            return;
        }

        // El mismo prefab que usa esa acción en la marioneta (es el mismo objeto del juego en todos los PCs)
        GameObject prefab;
        bool pooled;
        switch (states[stateIndex].Actions[actionIndex]) {
            case HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool spawn:
                prefab = spawn.gameObject.Value;
                pooled = true;
                break;
            case HutongGames.PlayMaker.Actions.CreateObject create:
                prefab = create.gameObject.Value;
                pooled = false;
                break;
            default:
                return;
        }

        if (prefab == null) {
            return;
        }

        if (id != 0 && _byId.TryGetValue(id, out var previous)) {
            RemoveSpawned(previous);
        }

        // Se crea a la altura de la marioneta (la z del original puede no ser la de este PC)
        position.z = creator.Transform.position.z;
        GameObject copy;
        _spawningCopy = true;
        try {
            copy = pooled
                ? prefab.Spawn(position, Quaternion.Euler(0f, 0f, rotation))
                : UnityEngine.Object.Instantiate(prefab, position, Quaternion.Euler(0f, 0f, rotation));
        } finally {
            _spawningCopy = false;
        }

        if (copy == null) {
            return;
        }

        var scale = copy.transform.localScale;
        if ((scale.x < 0f) != facingRight) {
            scale.x = -scale.x;
            copy.transform.localScale = scale;
        }

        // Solo visual: corre solo. De juego: copia que sigue al original (daña al tocarla; los golpes van al dueño)
        if (id != 0) {
            Track(copy, id, pooled, puppet: true, from.Id);
        }
    }

    private void SendDespawn(uint id) {
        if (!_session.CanSend || !OthersInScene()) {
            return;
        }

        var writer = _session.Begin(EnemyDespawn);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(id);
        _session.End();
    }

    private void OnDespawn(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var enemy) && enemy.Spawned && enemy.Puppet) {
            RemoveSpawned(enemy);
        }
    }

    /// <summary>Quita la copia de algo creado en otro PC: vuelve a funcionar normal y al pool (o se destruye).</summary>
    private void RemoveSpawned(Enemy enemy) {
        Forget(enemy);
        if (enemy.Root == null) {
            return;
        }

        SetPuppet(enemy, false);
        if (enemy.Pooled) {
            enemy.Root.Recycle();
        } else {
            UnityEngine.Object.Destroy(enemy.Root);
        }
    }

    /// <summary>Deja de seguir un objeto creado en combate.</summary>
    private void Forget(Enemy enemy) {
        _enemies.Remove(enemy);
        _byId.Remove(enemy.Id);
        if (enemy.Health != null) {
            _byHealth.Remove(enemy.Health);
        }

        if (enemy.Fsms != null) {
            foreach (var fsm in enemy.Fsms) {
                if (fsm != null) {
                    _enemyFsms.Remove(fsm.Fsm);
                    _soundFsms.Remove(fsm.Fsm);
                }
            }
        }
    }

    /// <summary>Al salir de la sala, las copias de lo creado en otro PC se quitan (las del pool no deben quedar paradas).</summary>
    private void RemoveSpawnedCopies() {
        for (var i = _enemies.Count - 1; i >= 0; i--) {
            var enemy = _enemies[i];
            if (enemy.Spawned && enemy.Puppet) {
                RemoveSpawned(enemy);
            }
        }
    }

    private void SendEnemyEvent(Enemy enemy, byte fsmIndex) {
        if (!_session.CanSend) {
            return;
        }

        var writer = _session.Begin(EnemyEvent);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(enemy.Id);
        writer.WriteByte(fsmIndex);
        _session.End();
    }

    private void OnEnemyEvent(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var fsmIndex = reader.ReadByte();
        if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var enemy) && !enemy.Puppet && enemy.Fsms != null &&
            fsmIndex < enemy.Fsms.Length && enemy.Fsms[fsmIndex] != null) {
            enemy.Fsms[fsmIndex].SendEvent(TakeDamageEvent);
        }
    }

    // =====================================================================================================
    // Piezas que se mueven aparte (aguja de Hornet, mazas, brazos...)
    // =====================================================================================================

    /// <summary>
    /// Hijos del enemigo con animación propia o que hacen daño (y sin vida propia: esos son otro enemigo). Su lógica es la
    /// del enemigo, parada en las marionetas, así que su posición, giro, si se ven y su animación vienen del que lo mueve.
    /// </summary>
    private static void CollectParts(Enemy enemy) {
        List<Part> parts = null;
        foreach (var child in enemy.Root.GetComponentsInChildren<Transform>(true)) {
            if (child == enemy.Transform || child.GetComponent<HealthManager>() != null) {
                continue;
            }

            var animator = child.GetComponent<tk2dSpriteAnimator>();
            if (animator == null && child.GetComponent<DamageHero>() == null) {
                continue;
            }

            parts ??= new List<Part>();
            parts.Add(new Part { Transform = child, Animator = animator });
            if (parts.Count == MaxParts) {
                break;
            }
        }

        enemy.Parts = parts?.ToArray();
    }

    /// <summary>Estado de las piezas de los enemigos que mueve este PC (solo las que cambiaron, o todas de vez en cuando).</summary>
    private void SendParts(uint tick) {
        PacketWriter writer = null;
        var enemyCountPosition = 0;
        var enemyCount = 0;
        var keepAlive = tick - _lastPartsKeepAlive >= KeepAliveTicks;
        if (keepAlive) {
            _lastPartsKeepAlive = tick;
        }

        foreach (var enemy in _enemies) {
            if (enemy.Parts == null || enemy.Puppet || enemy.Root == null || enemy.ReportedDead || !enemy.Root.activeInHierarchy) {
                continue;
            }

            var partCountPosition = -1;
            var partCount = 0;
            for (var i = 0; i < enemy.Parts.Length; i++) {
                var part = enemy.Parts[i];
                if (part.Transform == null) {
                    continue;
                }

                var transform = part.Transform;
                var local = transform.localPosition;
                var flags = (byte) ((transform.gameObject.activeSelf ? 1 : 0) | (transform.localScale.x < 0f ? 2 : 0));
                var rotation = (ushort) Mathf.RoundToInt(Mathf.Repeat(transform.localEulerAngles.z, 360f) * 100f);
                ushort clip = ushort.MaxValue;
                byte frame = 0;
                var animator = part.Animator;
                if (animator != null && animator.CurrentClip != null) {
                    if (animator.CurrentClip != part.LastClipRef) {
                        part.LastClipRef = animator.CurrentClip;
                        var index = Array.IndexOf(animator.Library.clips, animator.CurrentClip);
                        part.ClipIndex = index < 0 ? ushort.MaxValue : (ushort) index;
                    }

                    clip = part.ClipIndex;
                    frame = (byte) Mathf.Clamp(animator.CurrentFrame, 0, 255);
                }

                if (!keepAlive && flags == part.Flags && local.x == part.X && local.y == part.Y && rotation == part.Rotation &&
                    clip == part.Clip && frame == part.Frame) {
                    continue;
                }

                part.Flags = flags;
                part.X = local.x;
                part.Y = local.y;
                part.Rotation = rotation;
                part.Clip = clip;
                part.Frame = frame;
                if (writer == null || writer.Position > PartsMessageLimit) {
                    if (writer != null) {
                        FinishParts(writer, enemyCountPosition, enemyCount, partCountPosition, partCount);
                        partCountPosition = -1;
                        partCount = 0;
                    }

                    writer = _session.Begin(EnemyParts);
                    writer.WriteUShort(_sceneHash);
                    enemyCountPosition = writer.Position;
                    writer.WriteByte(0);
                    enemyCount = 0;
                }

                if (partCountPosition < 0) {
                    writer.WriteUInt(enemy.Id);
                    partCountPosition = writer.Position;
                    writer.WriteByte(0);
                    enemyCount++;
                }

                writer.WriteByte((byte) i);
                writer.WriteByte(flags);
                writer.WriteFloat(local.x);
                writer.WriteFloat(local.y);
                writer.WriteUShort(rotation);
                writer.WriteUShort(clip);
                writer.WriteByte(frame);
                partCount++;
            }

            if (partCountPosition >= 0) {
                PatchByte(writer, partCountPosition, partCount);
            }
        }

        if (writer != null) {
            FinishParts(writer, enemyCountPosition, enemyCount, -1, 0);
        }
    }

    private void FinishParts(PacketWriter writer, int enemyCountPosition, int enemyCount, int partCountPosition, int partCount) {
        if (partCountPosition >= 0) {
            PatchByte(writer, partCountPosition, partCount);
        }

        PatchByte(writer, enemyCountPosition, enemyCount);
        _session.End();
    }

    private static void PatchByte(PacketWriter writer, int position, int value) {
        var end = writer.Position;
        writer.Seek(position);
        writer.WriteByte((byte) value);
        writer.Seek(end);
    }

    private void OnParts(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var enemyCount = reader.ReadByte();
        for (var e = 0; e < enemyCount; e++) {
            var id = reader.ReadUInt();
            var partCount = reader.ReadByte();
            _byId.TryGetValue(id, out var enemy);
            var apply = sceneHash == _sceneHash && enemy != null && enemy.Puppet && enemy.Parts != null &&
                        enemy.Health != null && !enemy.Health.isDead;
            for (var p = 0; p < partCount; p++) {
                var index = reader.ReadByte();
                var flags = reader.ReadByte();
                var x = reader.ReadFloat();
                var y = reader.ReadFloat();
                var rotation = reader.ReadUShort();
                var clip = reader.ReadUShort();
                var frame = reader.ReadByte();
                if (!apply || index >= enemy.Parts.Length || enemy.Parts[index].Transform == null) {
                    continue;
                }

                ApplyPart(enemy.Parts[index], flags, x, y, rotation, clip, frame);
            }
        }
    }

    private static void ApplyPart(Part part, byte flags, float x, float y, ushort rotation, ushort clip, byte frame) {
        var transform = part.Transform;
        var active = (flags & 1) != 0;
        if (transform.gameObject.activeSelf != active) {
            transform.gameObject.SetActive(active);
        }

        var local = transform.localPosition;
        transform.localPosition = new Vector3(x, y, local.z);
        var angle = rotation / 100f;
        if (Mathf.Abs(Mathf.DeltaAngle(transform.localEulerAngles.z, angle)) > 0.5f) {
            transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        var scale = transform.localScale;
        var flipped = (flags & 2) != 0;
        if ((scale.x < 0f) != flipped) {
            scale.x = -scale.x;
            transform.localScale = scale;
        }

        var animator = part.Animator;
        if (animator != null && clip != ushort.MaxValue && animator.Library != null && clip < animator.Library.clips.Length) {
            var animation = animator.Library.clips[clip];
            if (animation != null && (animator.CurrentClip != animation || Math.Abs(animator.CurrentFrame - frame) > 1)) {
                animator.PlayFromFrame(animation, Mathf.Min(frame, Math.Max(0, animation.frames.Length - 1)));
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
            enemy.PausedFsms.Clear();
            foreach (var behaviour in enemy.Root.GetComponents<Behaviour>()) {
                if (behaviour.enabled && behaviour is MonoBehaviour && !Keep(behaviour)) {
                    Pause(enemy, behaviour as PlayMakerFSM);
                    behaviour.enabled = false;
                    enemy.Disabled.Add(behaviour);
                }
            }

            foreach (var fsm in enemy.Root.GetComponentsInChildren<PlayMakerFSM>(true)) {
                if (fsm.enabled && fsm.gameObject != enemy.Root) {
                    Pause(enemy, fsm);
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
            foreach (var (fsm, restart) in enemy.PausedFsms) {
                if (fsm != null) {
                    fsm.Fsm.RestartOnEnable = restart;
                }
            }

            enemy.PausedFsms.Clear();
            if (enemy.Body != null) {
                enemy.Body.bodyType = enemy.BodyType;
            }

            if (enemy.Health != null) {
                enemy.LastHp = enemy.Health.hp;
                enemy.ReportedDead = enemy.Health.isDead;
            }
        }
    }

    /// <summary>
    /// La lógica de la marioneta se pausa, no se reinicia: al volver a moverla este PC sigue desde donde estaba (sin repetir
    /// su preparación, que crea objetos como el cadáver de Hornet y los duplicaría).
    /// </summary>
    private static void Pause(Enemy enemy, PlayMakerFSM fsm) {
        if (fsm == null) {
            return;
        }

        enemy.PausedFsms.Add((fsm, fsm.Fsm.RestartOnEnable));
        fsm.Fsm.RestartOnEnable = false;
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
                if (kind == HealthKind.Update && !enemy.Puppet) {
                    continue;
                }

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

        // Lo que crea la lógica de los enemigos reales (proyectiles, crías, efectos)
        On.HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool.OnEnter += (orig, self) => {
            orig(self);
            _active?.OnSpawned(self, self.storeObject?.Value, pooled: true);
        };
        On.HutongGames.PlayMaker.Actions.CreateObject.OnEnter += (orig, self) => {
            orig(self);
            _active?.OnSpawned(self, self.storeObject?.Value, pooled: false);
        };

        // El aviso de daño que manda el ataque a la lógica de una marioneta (parada aquí) va al PC que la mueve
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += (orig, self, fsmEvent, eventData) => {
            orig(self, fsmEvent, eventData);
            var sync = _active;
            if (sync?._session != null && fsmEvent != null && fsmEvent.Name == TakeDamageEvent &&
                sync._enemyFsms.TryGetValue(self, out var target) && target.Enemy.Puppet) {
                sync.SendEnemyEvent(target.Enemy, target.Index);
            }
        };

        // Al empezar a cambiar de sala (aún dentro de la anterior): los enemigos que movía este PC, a los demás
        On.GameManager.BeginSceneTransition += (orig, self, info) => {
            _active?.ReleaseAll();
            orig(self, info);
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
            if (enemy.Health == null) {
                yield return $"{enemy.Root.name} id={enemy.Id:x8} {(enemy.Puppet ? "copia" : "creado")} pos=({p.x:0.00},{p.y:0.00})";
                continue;
            }

            yield return $"{enemy.Root.name} id={enemy.Id:x8} {(enemy.Puppet ? "marioneta" : "real")}{(enemy.Spawned ? " creado" : "")}" +
                         $" mueve={ControllerOf(enemy, _session.OwnerOf(_scene))}" +
                         $" hp={enemy.Health.hp}" +
                         $"{(enemy.Health.isDead ? " MUERTO" : "")}{(enemy.Health.IsInvincible ? " invencible" : "")}" +
                         $"{(enemy.Root.activeInHierarchy ? "" : " (inactivo)")} " +
                         $"pos=({p.x:0.00},{p.y:0.00})";
        }
    }

    /// <summary>Piezas del primer enemigo con ese nombre: si se ven, posición local y animación (modo de prueba).</summary>
    public IEnumerable<string> DescribeParts(string name) {
        foreach (var enemy in _enemies) {
            if (enemy.Root == null || enemy.Parts == null || !enemy.Root.name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            foreach (var part in enemy.Parts) {
                if (part.Transform == null) {
                    continue;
                }

                var local = part.Transform.localPosition;
                var clip = part.Animator != null && part.Animator.CurrentClip != null ? part.Animator.CurrentClip.name : "-";
                yield return $"{enemy.Root.name}/{part.Transform.name} {(part.Transform.gameObject.activeSelf ? "visible" : "oculta")} " +
                             $"pos=({local.x:0.00},{local.y:0.00}) anim={clip}";
            }

            yield break;
        }
    }

    /// <summary>Golpea al enemigo vivo y activo con ese nombre, como un golpe de aguijón del jugador.</summary>
    public bool TestHit(string namePrefix, int damage) {
        var hero = HeroController.instance;
        foreach (var enemy in _enemies) {
            if (enemy.Root != null && enemy.Root.activeInHierarchy && enemy.Health != null && !enemy.Health.isDead &&
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
