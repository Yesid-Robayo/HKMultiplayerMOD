using System;
using System.Collections.Generic;
using System.Text;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;
using HKMultiplayer.UI;

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

    /// <summary>
    /// Daño a una marioneta que no pasa por <c>HealthManager.Hit</c> (marioneta → sala; lo aplica quien la mueve): nubes de
    /// Spore Shroom y Defender's Crest (<c>ApplyExtraDamage</c>), moscas de Flukenest (<c>SpellFluke.DoDamage</c>) y muertes
    /// directas (<c>KillOnContact</c>, plataformas de liana que caen encima).
    /// </summary>
    public const MessageType EnemyDamage = (MessageType) 108;

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
    /// Un aviso de fuera del enemigo llegó a su marioneta (parada aquí): el ataque de un jugador ("TAKE DAMAGE"), la zona
    /// de la arena en la que entró el caballero de este PC, la escena de batalla... Se reenvía al PC que lo mueve: con esos
    /// avisos despiertan o reaccionan muchos enemigos (Mamá Gruz dormida, la Vasija Rota al empezar la batalla).
    /// </summary>
    public const MessageType EnemyEvent = (MessageType) 67;

    /// <summary>Números de la FSM que viajan con sus efectos (los de una ráfaga: velocidad, ángulos...).</summary>
    private const int MaxEffectVars = 48;

    private const int MaxEffectVectors = 16;

    /// <summary>Modo de prueba: cada efecto reproducido en una marioneta se registra.</summary>
    private static readonly bool TestMode = HKMultiplayer.Testing.TestDriver.IsEnabled();

    /// <summary>Lo más a menudo que se reenvía el mismo aviso a un mismo enemigo (hay zonas que avisan cada frame).</summary>
    private const float EventForwardInterval = 0.25f;

    /// <summary>Aviso con el que el HealthManager dice a la lógica de un enemigo que su vida llegó a cero.</summary>
    private const string ZeroHpEvent = "ZERO HP";

    /// <summary>Lo que se espera tras ZERO HP para ver si la vida vuelve a subir (cambio de fase).</summary>
    private const float ZeroHpPhaseSeconds = 0.5f;

    /// <summary>Estados de muerte de la lógica de un jefe (la del Falso Caballero: Death Land, Death Open..., Cough).</summary>
    private static bool IsDeathState(string name) =>
        name.StartsWith("Death", StringComparison.Ordinal) || name == "Cough";

    /// <summary>Piezas que se mueven aparte (hijos con animación o que dañan) (dueño → sala, no fiable).</summary>
    public const MessageType EnemyParts = (MessageType) 68;

    /// <summary>Objetos de la arena que la lógica del enemigo enciende y apaga (al cambiar, y todos a quien entra).</summary>
    public const MessageType EnemyLinked = (MessageType) 69;

    /// <summary>El bicho que soltó un tarro original del Coleccionista: los demás crean una copia que lo sigue.</summary>
    public const MessageType EnemyJar = (MessageType) 71;

    /// <summary>
    /// La lógica de un enemigo real sacó de su jaula un enemigo de la sala que vuelve a ella al morir (las crías de la
    /// Colmena que suelta el Zombie Hive): los demás sacan el mismo de su jaula como copia que lo sigue.
    /// </summary>
    public const MessageType EnemyRelease = (MessageType) 98;

    /// <summary>Aviso con el que un enemigo de jaula sale de ella (la plantilla de las crías de los Hatcher).</summary>
    private const string CageReleaseEvent = "SPAWN";

    /// <summary>Variable de la FSM de un enemigo de jaula con la jaula a la que vuelve al morir.</summary>
    private const string CageVariable = "Cage";

    private const int MaxLinked = 64;

    /// <summary>Tras encontrar un enemigo, cuándo buscar los objetos que enciende (su lógica ya los tiene apuntados).</summary>
    private const float LinkedResolveDelay = 1f;

    private const int MaxParts = 24;

    /// <summary>Tras morir, lo que se sigue copiando su cadáver (lo que tarda en caer y, si explota, en explotar).</summary>
    private const float CorpseFollowSeconds = 5f;

    /// <summary>A partir de aquí, los estados de los enemigos siguientes van en otro mensaje.</summary>
    private const int StatesMessageLimit = 900;

    /// <summary>Piezas de fuera del enemigo (su rayo, su impacto...) que como mucho se siguen.</summary>
    private const int MaxExternalParts = 32;

    /// <summary>Trampas de la sala que como mucho van con un enemigo.</summary>
    private const int MaxSatellites = 8;

    /// <summary>En el mensaje de piezas, en lugar del número de pieza: va una de fuera, identificada por su clave.</summary>
    private const byte ExternalPartIndex = byte.MaxValue;

    /// <summary>A partir de aquí, las piezas siguientes van en otro mensaje (cada pieza ocupa 28 bytes, 32 las de fuera).</summary>
    private const int PartsMessageLimit = 900;

    /// <summary>El otro jugador tiene que estar al menos esto más cerca para que el enemigo pase a su PC.</summary>
    private const float SwitchMargin = 4f;

    /// <summary>El margen cuando los dos están dentro de su zona de alerta (ver AssignControllers).</summary>
    private const float ZoneSwitchMargin = 8f;

    /// <summary>Tiempo mínimo entre cambios de quién mueve un enemigo (más en los jefes: cada cambio corta su ataque).</summary>
    private const float SwitchInterval = 5f;

    private const float BossSwitchInterval = 8f;

    /// <summary>
    /// Velocidad (unidades/s, medida entre dos repartos) a partir de la cual no cambia de PC: va embistiendo o lanzándose.
    /// El Moss Charger embiste a 15 y cruzaba de un jugador al otro: pasaba al PC del otro a mitad de la embestida, su
    /// lógica seguía allí con ese caballero y daba la vuelta (se veía derrapar al revés).
    /// </summary>
    private const float FastSpeed = 9f;

    /// <summary>Vida a partir de la cual un enemigo se trata como jefe.</summary>
    private const int BossHp = 150;

    /// <summary>Distancia al jefe a la que tienen que estar todos para que despierte.</summary>
    private const float WakeRange = 25f;

    /// <summary>Batalla de arena a la que pertenece un enemigo (privado en <see cref="HealthManager"/>).</summary>
    private static readonly System.Reflection.FieldInfo BattleSceneField = typeof(HealthManager).GetField("battleScene",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Cambio de estado pendiente de una FSM (privado en PlayMaker): se anula al retener el despertar.</summary>
    private static readonly System.Reflection.FieldInfo SwitchToStateField = typeof(HutongGames.PlayMaker.Fsm).GetField(
        "switchToState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Tras encontrar un enemigo, se le deja iniciar su lógica antes de pararla (si no, la retoma sin iniciar).</summary>
    private const float PuppetDelay = 0.5f;

    private const int MaxHandoverVars = 48;

    /// <summary>Largo máximo de cada texto del traspaso (nombres de eventos y de estados).</summary>
    private const int MaxHandoverString = 64;

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
        "InfectedEnemyEffects", "ExtraDamageable", "EnemyDreamnailReaction", "NonBouncer", "DisableWatch"
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

        /// <summary>Daño al tocarlo: su lógica lo cambia (p. ej. 0 durante la presentación de un jefe).</summary>
        public DamageHero Damage;

        /// <summary>FSM del enemigo y sus hijos, en el mismo orden en todos los PCs (para los sonidos).</summary>
        public PlayMakerFSM[] Fsms;

        /// <summary>Piezas que se mueven aparte (null si no tiene).</summary>
        public Part[] Parts;

        /// <summary>Objetos de la arena que su lógica enciende y apaga (se buscan tras prepararse).</summary>
        public Linked[] Linked;

        public bool LinkedResolved;

        /// <summary>Este PC acaba de retomarlo: se envían todas sus piezas y objetos (el anterior los tenía como él los dejó).</summary>
        public bool SendFull;

        /// <summary>Lo que llegó antes de tenerlos localizados (quien acaba de entrar): se aplica al localizarlos.</summary>
        public Dictionary<uint, bool> PendingLinked;
        public bool Puppet;

        /// <summary>Creado durante el combate (no es de la sala); si vino del pool, vuelve a él al desaparecer.</summary>
        public bool Spawned;

        public bool Pooled;

        /// <summary>Enemigo de jaula fuera de ella (null si no lo es): al volver a su jaula desaparece como lo creado.</summary>
        public CagedEnemy Caged;

        /// <summary>Ya se le vio fuera de su jaula (antes de eso, estar en su estado de reposo no es haber vuelto).</summary>
        public bool LeftCage;

        /// <summary>
        /// Creado por un objeto de la sala, al cargarla o durante la partida (las jaulas del Coliseo): no queda muerto
        /// para siempre.
        /// </summary>
        public bool Created;

        /// <summary>
        /// Si el juego lo deja muerto hasta el banco (lleva <c>PersistentBoolItem</c>). Los que no lo llevan (los jefes de
        /// arena, como el primer Señor Mantis) vuelven cada vez que se carga la sala; si el host los recordaba muertos,
        /// al volver a intentarlo el combate empezaba sin ellos.
        /// </summary>
        public bool StaysDead;

        /// <summary>Quién lo mueve (0: el dueño de la sala) y quién lo movía antes del último cambio.</summary>
        public ushort Controller;

        public ushort PreviousController;
        public float LastSwitch;

        /// <summary>Su zona de alerta (ver AlertZone) y si ya se buscó.</summary>
        public Collider2D AlertZone;

        public bool AlertZoneSearched;

        /// <summary>Dónde estaba y cuándo en el reparto anterior (para saber si va rápido).</summary>
        public Vector2 AssignPosition;

        public float AssignAt;

        /// <summary>
        /// Enemigo del que cuelga este (la cabeza del Falso Caballero): lo mueve siempre el mismo PC que a él (ver
        /// <see cref="AssignControllers"/>). Null si no cuelga de ninguno.
        /// </summary>
        public Enemy Parent;

        /// <summary>Veces que se buscó (el padre puede apuntarse después que el hijo al entrar en la sala).</summary>
        public int ParentChecks;
        public float ScannedAt;

        // Jefe dormido: su lógica principal, el estado en el que espera y si ya despertó. Si quiere despertar sin estar
        // todos cerca, el cambio se retiene (invulnerable mientras) hasta que lleguen
        public int WakeFsm = -1;
        public string DormantState;
        public bool Awake;
        public HutongGames.PlayMaker.FsmState HeldWake;
        public bool WaitingWake;

        /// <summary>El tipo de ataque con el que murió aquí (va en el aviso de su muerte).</summary>
        public AttackTypes KilledBy;

        /// <summary>
        /// Murió en otro PC y aquí su lógica hace su muerte: lo que saque del pool no se envía (el otro ya lo hizo; antes
        /// los efectos de muerte salían dobles).
        /// </summary>
        public bool DiedElsewhere;

        /// <summary>Desde cuándo espera a que estén todos cerca para despertar.</summary>
        public float WaitingWakeSince;

        /// <summary>Si su arena espera a todos (<see cref="ArenaWaits"/>): 0 sin mirar aún, 1 sí, -1 no.</summary>
        public sbyte ArenaWaits;
        public bool WasInvincible;
        public float NextWaitNotice;

        /// <summary>Cómo iba, recibido del que lo movía antes (al pasar a moverlo este PC).</summary>
        public HandoverFsm[] Handover;

        public bool HandoverReady;

        /// <summary>Avisos reenviados a este PC que llegaron cuando aún no lo movía (se atienden al tomarlo).</summary>
        public List<(byte Fsm, string Event, float At)> PendingEvents;

        /// <summary>Golpes de otros que llegaron mientras este PC esperaba el traspaso para tomarlo.</summary>
        public List<(HitInstance Hit, ushort From, float At)> PendingHits;

        public readonly List<Behaviour> Disabled = new();

        /// <summary>Desde qué FSM de la lista son de las trampas de la sala que dispara (0: ninguna).</summary>
        public int FirstSatellite;

        public bool SatellitesReleased;

        /// <summary>FSM paradas en la marioneta y si antes reiniciaban al reactivarse (se pausan, no se reinician).</summary>
        public readonly List<(PlayMakerFSM Fsm, bool Restart)> PausedFsms = new();

        /// <summary>FSM pausadas a las que se les paró el andar (`WalkLeftRight`): al retomarlo, vuelve a andar.</summary>
        public readonly List<PlayMakerFSM> StoppedWalks = new();

        /// <summary>
        /// FSM de piezas que se soltaron de la marioneta al morir (el cadáver del Hollow Knight): ya funcionan solas en este
        /// PC y no siguen los efectos del original. Null si ninguna.
        /// </summary>
        public List<PlayMakerFSM> Detached;

        /// <summary>
        /// Su lógica recibió y atendió ZERO HP en este PC (su muerte va por ella, no por HealthManager.Die). Se quita si la
        /// vida vuelve a subir de 0: era un cambio de fase (el Falso Caballero en cada armadura rota, Sly, Oro y Mato con
        /// `SetHP`). Antes no se quitaba nunca y desde el primer cambio de fase el jefe quedaba "muriendo": no cambiaba de
        /// PC aunque su jugador cayera y la marioneta repetía sus avisos a todos como si ya hubiera muerto.
        /// </summary>
        public bool ZeroHp;

        public float ZeroHpAt;

        /// <summary>
        /// Su lógica entró en un estado de muerte (`Death...`, `Cough`): la del Falso Caballero va por su cabeza y su cuerpo
        /// nunca queda muerto en su HealthManager. No se quita.
        /// </summary>
        public bool DeathState;

        /// <summary>El PC que lo mueve avisó de que allí ya murió (sus efectos llegan con su muerte en marcha).</summary>
        public bool DeadThere;

        /// <summary>
        /// Su muerte está en marcha aunque su vida no lo diga: la del Falso Caballero va por su cabeza (otro enemigo) y su
        /// cuerpo nunca queda muerto en su HealthManager.
        /// </summary>
        public bool Dying => ZeroHp || DeadThere || DeathState;
        public RigidbodyType2D BodyType;
        public readonly SnapshotBuffer Buffer = new();

        // Lo último enviado (dueño)
        public int LastHp;
        public bool ReportedDead;

        /// <summary>Cuándo murió (o llegó su muerte): su cadáver se sigue un rato más.</summary>
        public float DiedAt = -1f;

        /// <summary>Murió en otro PC: su cadáver sigue al de allí (no al que lanza la física de este PC).</summary>
        public bool CorpseFollows;
        public PlayerSnapshot LastSent;
        public uint LastSentTick;
        public tk2dSpriteAnimationClip LastClip;
        public ushort LastClipIndex;
    }

    /// <summary>
    /// Enemigo de la sala que espera en una jaula fuera de la vista y vuelve a ella al morir (las crías de la Colmena: su
    /// FSM tiene la variable "Cage" y sale con "SPAWN"). No es un enemigo de la sala más: lo saca la lógica de otro
    /// enemigo, que solo funciona en el PC que lo mueve, así que se sigue como lo creado en combate.
    /// </summary>
    private sealed class CagedEnemy {
        public HealthManager Health;
        public PlayMakerFSM Fsm;

        /// <summary>Su jaula al cargar la sala (null si no tenía padre).</summary>
        public Transform Cage;

        /// <summary>El estado en el que espera en la jaula (el que sale con "SPAWN").</summary>
        public string RestState;

        /// <summary>Su ruta en la sala al cargarla, dentro de la jaula: igual en todos los PCs.</summary>
        public uint Path;

        /// <summary>Su vida en la jaula al cargar la sala (la que tiene al volver a salir).</summary>
        public int FullHp;
    }

    /// <summary>Pieza de un enemigo y lo último enviado de ella.</summary>
    private sealed class Linked {
        public GameObject Object;
        public uint Path;
        public bool Active;
    }

    private sealed class Part {
        /// <summary>Su cadáver (escondido hasta que muere): se sigue unos segundos tras la muerte.</summary>
        public bool IsCorpse;

        /// <summary>Solo las de fuera del enemigo: qué variable de qué FSM la apunta (igual en todos los PCs).</summary>
        public uint Key;

        public Transform Transform;
        public tk2dSpriteAnimator Animator;
        public Collider2D Collider;
        public Renderer Renderer;
        public DamageHero Damage;
        public byte DamageSent;
        public float ScaleX;
        public float ScaleY;
        public tk2dSpriteAnimationClip LastClipRef;
        public ushort ClipIndex;
        public byte Flags;
        public float X;
        public float Y;
        public float Z;
        public ushort Rotation;
        public ushort Clip;
        public byte Frame;
    }

    private sealed class HandoverFsm {
        public ushort State;

        /// <summary>El jefe ya había despertado en el PC que lo soltó.</summary>
        public bool Awake;

        /// <summary>Cómo iba su `Walker` en el PC que lo soltó (null si no tiene).</summary>
        public WalkerData? Walker;
        public int[] Ints;
        public float[] Floats;
        public bool[] Bools;

        /// <summary>
        /// Sus textos: algunos ataques eligen en uno el lado y lo usan en otro (los Señores Mantis de la segunda fase
        /// guardan en `Shot Event` hacia dónde va su guadaña).
        /// </summary>
        public string[] Strings;

        public ObjectRef[] Objects;
        public bool HasBody;
        public Vector2 Velocity;
        public float Gravity;
        public RigidbodyType2D BodyType;

        /// <summary>Lo que llevaba su estado esperando (Wait o WaitRandom; 0 si no espera).</summary>
        public float Waited;

        /// <summary>Si estaba encendido en el PC que lo movía.</summary>
        public bool Active;

        /// <summary>Si se dibujaba, si chocaba y si era invencible (y desde dónde), como en su estado continuo.</summary>
        public byte Look;

        /// <summary>Cuánto dañaba al tocarlo.</summary>
        public byte Damage;

        /// <summary>Si su lógica ya lo había soltado de su padre en la sala (los Mage del Coliseo al despertar).</summary>
        public bool Detached;
    }

    /// <summary>A qué apunta una variable de objeto, de forma que otro PC encuentre su equivalente.</summary>
    private enum ObjectKind : byte {
        None = 0,

        /// <summary>El caballero del que lo movía: en quien lo recibe, su propio caballero.</summary>
        Hero = 1,

        /// <summary>El enemigo o una pieza suya, por su ruta dentro de él.</summary>
        Own = 2,

        /// <summary>Un objeto de la sala, por su ruta en la sala.</summary>
        Scene = 3
    }

    private struct ObjectRef {
        public ObjectKind Kind;
        public uint Path;
    }

    /// <summary>FSM de enemigos que tienen estados con sonido: su enemigo, su posición y qué estados suenan.</summary>
    private sealed class SoundFsm {
        public Enemy Enemy;
        public byte Index;
        public bool[] SoundStates;

        /// <summary>
        /// Por estado, sus efectos que siguen cada frame mientras dura (null si no tiene): partículas con
        /// <c>everyFrame</c>, salpicaduras continuas, y las cuentas con sus números que esos efectos usan (el humo del
        /// Falso Caballero al morir sube 5 cada frame).
        /// </summary>
        public HutongGames.PlayMaker.FsmStateAction[][] Continuous;

        /// <summary>En el PC que lo mueve: el último estado enviado tenía efectos continuos (al salir hay que avisar).</summary>
        public bool Running;
    }

    /// <summary>En una marioneta: los efectos continuos del estado en el que está su FSM en el PC que la mueve.</summary>
    private struct RunningEffect {
        public Enemy Enemy;
        public byte Fsm;
        public HutongGames.PlayMaker.FsmStateAction[] Actions;
    }

    private readonly List<RunningEffect> _runningEffects = new();

    private readonly Dictionary<HutongGames.PlayMaker.Fsm, SoundFsm> _soundFsms = new();

    /// <summary>Todas las FSM de los enemigos: a qué enemigo pertenecen y en qué posición (para lo que crean).</summary>
    private readonly Dictionary<HutongGames.PlayMaker.Fsm, (Enemy Enemy, byte Index)> _enemyFsms = new();

    private uint _spawnCounter;

    /// <summary>Lo que crearon los enemigos de este PC y aún no se envió (se envía con su tamaño y giro finales).</summary>
    private const int MaxPendingSpawns = 32;

    private struct PendingSpawn {
        public GameObject Object;
        public uint CreatorId;
        public byte FsmIndex;
        public ushort StateIndex;
        public byte ActionIndex;
        public uint Id;

        /// <summary>Nombre del prefab (el otro PC lo busca si su acción lo toma de una variable que allí está vacía).</summary>
        public string Prefab;
    }

    private readonly PendingSpawn[] _pendingSpawns = new PendingSpawn[MaxPendingSpawns];
    private int _pendingSpawnCount;

    /// <summary>FSM de lo creado cuyos números viajan con ello (las primeras).</summary>
    private const int MaxSpawnFsms = 4;

    private readonly List<PlayMakerFSM> _spawnFsms = new();

    /// <summary>Se está creando aquí la copia de algo que creó un enemigo en otro PC (no se vuelve a avisar).</summary>
    private static bool _spawningCopy;

    /// <summary>La marioneta está haciendo los efectos que le llegaron, o recibe un golpe solo de efectos: lo que eso le
    /// avise a ella misma no se reenvía (ya pasó en el PC que la mueve).</summary>
    private static bool _replaying;

    private readonly Dictionary<(uint Id, byte Fsm, string Event), float> _lastForward = new();

    /// <summary>Este PC está retomando un enemigo: lo que cree al ponerse en su estado no se replica.</summary>
    private static bool _takingOver;

    /// <summary>Tiempo tras encontrar un enemigo en que lo que crea es de su preparación (no se replica).</summary>
    private const float PreparationSeconds = 1.5f;

    private readonly List<Enemy> _enemies = new();

    /// <summary>Enemigos de jaula de la sala (estén dentro o fuera), por su HealthManager y por su ruta al cargarla.</summary>
    private readonly Dictionary<HealthManager, CagedEnemy> _caged = new();

    private readonly Dictionary<uint, CagedEnemy> _cagedByPath = new();
    private readonly Dictionary<uint, Enemy> _byId = new();
    private readonly Dictionary<HealthManager, Enemy> _byHealth = new();
    private readonly Dictionary<uint, (HealthKind Kind, int Hp)> _pendingHealth = new();

    /// <summary>
    /// Quién mueve enemigos que aquí aún no existen: los que crea una jaula del Coliseo se crean en cada PC cuando su
    /// jaula llega a ese estado, y el aviso del dueño de la sala puede llegar antes. Perderlo dejaba el enemigo como
    /// marioneta en los dos PCs (quieto e imposible de matar: la oleada no acababa).
    /// </summary>
    private readonly Dictionary<uint, ushort> _pendingControl = new();
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
    private readonly MessageHandler _onDamage;
    private readonly MessageHandler _onHealth;
    private readonly MessageHandler _onHitFx;
    private readonly MessageHandler _onSound;
    private readonly MessageHandler _onSpawn;
    private readonly MessageHandler _onDespawn;
    private readonly MessageHandler _onControl;
    private readonly MessageHandler _onHandover;
    private readonly MessageHandler _onEvent;
    private readonly MessageHandler _onParts;
    private readonly MessageHandler _onLinked;
    private readonly MessageHandler _onJar;
    private readonly MessageHandler _onRelease;

    /// <summary>Alguien entró en la sala: le llegan los objetos encendidos y apagados de los enemigos que mueve este PC.</summary>
    private bool _sendLinkedSnapshot;
    private uint _lastPartsKeepAlive;
    private uint _lastLinkedRefresh;
    private bool _linkedRefreshDue;

    /// <summary>Cada cuánto se repasan todos los objetos de la arena aunque no hayan cambiado (2 s).</summary>
    private const uint LinkedRefreshTicks = 120;
    private readonly List<(uint Id, ushort Controller)> _controlOut = new();
    private float _nextAssign;

    /// <summary>
    /// Cuándo repite el dueño de la sala quién mueve cada enemigo (a todos). Los avisos de cambio solo llegan al cambiar:
    /// quien recargó la sala (revivir, desatascarse, entrar otra vez) o perdió uno por un cambio de dueño se quedaba con
    /// otro reparto y cada PC creía que el enemigo lo movía el otro (quieto en los dos).
    /// </summary>
    private float _nextControlResync;

    private const float ControlResyncInterval = 2f;
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
        MessageRoutes.Register(EnemyDamage, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyHitFx, Route.SameScene, reliable: true);
        // Fiable: activar y desactivar objetos de la arena tiene que llegar, y en orden
        MessageRoutes.Register(EnemySound, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemySpawn, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyDespawn, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyControl, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyHandover, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyEvent, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyParts, Route.SameScene, reliable: false);
        MessageRoutes.Register(EnemyLinked, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyJar, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyRelease, Route.SameScene, reliable: true);
        MessageRoutes.Register(EnemyKilled, Route.Server, reliable: true);
        MessageRoutes.Register(Rested, Route.Server, reliable: true);
    }

    public EnemySync() {
        _onStates = OnStates;
        _onHit = OnHit;
        _onDamage = OnDamage;
        _onHealth = OnHealth;
        _onHitFx = OnHitFx;
        _onSound = OnSound;
        _onSpawn = OnSpawn;
        _onDespawn = OnDespawn;
        _onControl = OnControl;
        _onHandover = OnHandover;
        _onEvent = OnEnemyEvent;
        _onParts = OnParts;
        _onLinked = OnLinked;
        _onJar = OnJar;
        _onRelease = OnRelease;
        _onPlayerScene = OnPlayerScene;
        _onKilled = OnKilled;
        _onRested = (from, _) => {
            Log.Info($"{from.Name} descansó en un banco o murió: los enemigos vuelven a aparecer");
            ClearDead();
        };
    }

    public void Attach(GameSession session, PlayerSync players) {
        _session = session;
        _players = players;
        session.On(EnemyStates, _onStates);
        session.On(EnemyHit, _onHit);
        session.On(EnemyDamage, _onDamage);
        session.On(EnemyHealth, _onHealth);
        session.On(EnemyHitFx, _onHitFx);
        session.On(EnemySound, _onSound);
        session.On(EnemySpawn, _onSpawn);
        session.On(EnemyDespawn, _onDespawn);
        session.On(EnemyControl, _onControl);
        session.On(EnemyHandover, _onHandover);
        session.On(EnemyEvent, _onEvent);
        session.On(EnemyParts, _onParts);
        session.On(EnemyLinked, _onLinked);
        session.On(EnemyJar, _onJar);
        session.On(EnemyRelease, _onRelease);
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
            _session.On(EnemyDamage, null);
            _session.On(EnemyHealth, null);
            _session.On(EnemyHitFx, null);
            _session.On(EnemySound, null);
            _session.On(EnemySpawn, null);
            _session.On(EnemyDespawn, null);
            _session.On(EnemyControl, null);
            _session.On(EnemyHandover, null);
            _session.On(EnemyEvent, null);
            _session.On(EnemyParts, null);
            _session.On(EnemyLinked, null);
            _session.On(EnemyJar, null);
            _session.On(EnemyRelease, null);
            _session.On(EnemyKilled, null);
            _session.On(Rested, null);
            _session.PlayerSceneChanged -= _onPlayerScene;
        }

        // Lo creado en otro PC se quita antes: sin marioneta, RemoveSpawnedCopies ya no lo reconocía y los disparos, crías y
        // bichos de los tarros copiados seguían como objetos de verdad al cerrar la sesión
        RemoveSpawnedCopies();

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

    private void ClearPendingSpawns() {
        for (var i = 0; i < _pendingSpawnCount; i++) {
            _pendingSpawns[i].Object = null;
        }

        _pendingSpawnCount = 0;
    }

    private void Clear() {
        ClearPendingSpawns();
        RemoveSpawnedCopies();
        _enemies.Clear();
        _byId.Clear();
        _byHealth.Clear();
        _caged.Clear();
        _cagedByPath.Clear();
        _soundFsms.Clear();
        _runningEffects.Clear();
        _enemyFsms.Clear();
        _pendingHealth.Clear();
        _pendingControl.Clear();
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
            ClearPendingSpawns();
            RemoveSpawnedCopies();
            _enemies.Clear();
            _byId.Clear();
            _byHealth.Clear();
            _caged.Clear();
            _cagedByPath.Clear();
            _soundFsms.Clear();
            _runningEffects.Clear();
            _enemyFsms.Clear();
            _pendingControl.Clear();
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

        FlushSpawns();
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
                if (now >= _nextControlResync) {
                    _nextControlResync = now + ControlResyncInterval;
                    ResendControllers();
                }
            }

            for (var i = _enemies.Count - 1; i >= 0; i--) {
                var enemy = _enemies[i];
                if (!enemy.LinkedResolved && now - enemy.ScannedAt >= LinkedResolveDelay) {
                    ResolveLinked(enemy);
                    AddSatellites(enemy);
                    CollectExternalParts(enemy);
                }

                // Un ZERO HP tras el que la vida volvió a subir era un cambio de fase, no la muerte
                if (enemy.ZeroHp && !enemy.Puppet && enemy.Health != null && !enemy.Health.isDead && enemy.Health.hp > 0 &&
                    now - enemy.ZeroHpAt > ZeroHpPhaseSeconds) {
                    enemy.ZeroHp = false;
                    Log.Info($"{enemy.Root.name}: ZERO HP era un cambio de fase (vuelve a tener {enemy.Health.hp} de vida)");
                }

                if (enemy.Spawned) {
                    // Copia de algo creado por quien ya no está en la sala: aquí ya nadie la mueve
                    if (enemy.Puppet && !InScene(enemy.Controller)) {
                        RemoveSpawned(enemy);
                        continue;
                    }

                    // Los proyectiles siguen siempre a quien los creó; los bichos con vida pueden pasar a otro (ver
                    // AssignControllers) y siguen como los demás enemigos
                    if (enemy.Health == null) {
                        continue;
                    }
                }

                // Los muertos se quedan como están (su muerte sigue su curso)
                if (enemy.Health == null || enemy.Health.isDead) {
                    ReleaseSatellites(enemy);
                    continue;
                }

                var local = ControllerOf(enemy, owner) == _session.LocalId;
                if (local && enemy.Puppet) {
                    // Pasa a moverlo este PC: desde como lo dejó el anterior (si sigue aquí, espera a saberlo)
                    if (enemy.HandoverReady || enemy.PreviousController == _session.LocalId ||
                        !InScene(enemy.PreviousController) || now - enemy.LastSwitch > HandoverTimeout) {
                        TakeOver(enemy);
                    }
                } else if (local && enemy.HandoverReady) {
                    // Recién llegado a la sala (sus enemigos aún no son marionetas) y ya se lo dieron: sigue desde como lo
                    // dejó el anterior. Antes el traspaso se descartaba y seguía con lo recién cargado (un jefe a mitad del
                    // combate empezaba de nuevo y la marioneta del otro saltaba al sitio del principio)
                    TakeOver(enemy);
                } else if (!local && !enemy.Puppet && now - enemy.ScannedAt >= PuppetDelay) {
                    SetPuppet(enemy, true);
                }
            }
        }

        UpdateReal(isOwner);
        UpdatePuppets();
        UpdateRunningEffects();
        UpdateWakeGates();
    }

    /// <summary>El juego está cambiando de sala (desde que empieza hasta que la nueva está lista).</summary>
    internal static bool InSceneTransition() => GameManager.instance != null && GameManager.instance.IsInSceneTransition;

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
            // Lo creado en combate que ya no está (vuelto al pool o destruido): desaparece también en los demás. Si era
            // una copia, solo aquí: el original lo quita quien lo mueve (una copia puede acabar sola, como el tarro del
            // Coleccionista al llegar abajo, y borraría el original antes de que soltara su bicho)
            if (enemy.Spawned && (enemy.Root == null || !enemy.Root.activeInHierarchy || BackInCage(enemy))) {
                // Salvo un disparo: si su copia chocó (con el caballero de este PC o con una pared), en los demás también
                // se acaba ahí, en vez de seguir y atravesar a este jugador
                if (!enemy.Puppet || enemy.Root != null && enemy.Root.GetComponent<EnemyBullet>() != null) {
                    SendDespawn(enemy.Id);
                }

                // Una copia que volvió sola al pool (una bala, `EnemyBullet` se recicla aunque esté apagado) vuelve a estar
                // como el juego la dejó: si no, la siguiente vez que este PC la sacara para un enemigo suyo salía apagada,
                // sin gravedad y sin reciclarse
                if (enemy.Puppet && enemy.Root != null) {
                    SetPuppet(enemy, false);
                }

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

            // Destruido durante un cambio de sala no es muerto: la sala se está descargando. Al volver a cargar la misma
            // sala ("Recargar sala", "Ir con" a la sala en la que ya se está) su nombre no cambia hasta que la nueva está
            // lista, y antes todos sus enemigos se daban por muertos hasta el banco (el Falso Caballero no salía)
            if (enemy.Health == null && InSceneTransition()) {
                continue;
            }

            var dead = enemy.Health == null || enemy.Health.isDead;
            if (dead) {
                enemy.ReportedDead = true;
                enemy.DiedAt = Time.unscaledTime;
                // Con el tipo de ataque que lo mató (en el número, que en una muerte no se usaba): las muertes por
                // salpicadura o por el agua de la Ciudad no sueltan geo y las de ácido, agua o hechizo dejan otro cadáver
                _healthOut.Add((enemy.Id, HealthKind.Died, (int) enemy.KilledBy));
                if (enemy.StaysDead && !enemy.Spawned && !enemy.Created && !RepeatsEveryVisit(_scene)) {
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
        if (_lastForward.Count > 256) {
            _lastForward.Clear();
        }

        SendParts(tick);
        SendLinked(_sendLinkedSnapshot || _linkedRefreshDue);
        _sendLinkedSnapshot = false;
        _linkedRefreshDue = false;
        foreach (var enemy in _enemies) {
            enemy.SendFull = false;
        }
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
                state.Extra == enemy.LastSent.Extra && state.Damage == enemy.LastSent.Damage && state.Z == enemy.LastSent.Z &&
                tick - enemy.LastSentTick < KeepAliveTicks) {
                continue;
            }

            enemy.LastSent = state;
            enemy.LastSentTick = tick;
            // Cada enemigo ocupa 24 bytes: en salas con muchos (las jaulas del Coliseo) van en varios mensajes
            if (writer == null || count == MaxEntriesPerMessage || writer.Position > StatesMessageLimit) {
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
            writer.WriteFloat(state.Z);
            writer.WriteByte((byte) ((state.FacingRight ? 1 : 0) | (state.Visible ? 2 : 0)));
            writer.WriteUShort(state.SpriteId);
            writer.WriteByte(state.Collection);
            writer.WriteUShort(state.Rotation);
            writer.WriteByte(state.Extra);
            writer.WriteByte(state.Damage);
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
            Z = position.z,
            FacingRight = scale.x < 0f,
            Visible = enemy.Root.activeInHierarchy,
            Rotation = (ushort) Mathf.RoundToInt(Mathf.Repeat(transform.eulerAngles.z, 360f) * 100f),
            Extra = (byte) extra,
            Damage = enemy.Damage != null ? (byte) Mathf.Clamp(enemy.Damage.damageDealt, 0, 255) : (byte) 0
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

    /// <summary>
    /// Salas cuyo combate empieza de cero cada vez que se entra (las pruebas del Coliseo): sus enemigos no quedan muertos
    /// hasta el banco. Si no, al repetir una prueba sin descansar, los Mage de las oleadas (son de la sala, no de una
    /// jaula) no aparecían y la oleada 46 de Oro esperaba para siempre a uno que el host daba por muerto.
    /// </summary>
    private static bool RepeatsEveryVisit(string scene) => scene.StartsWith("Room_Colosseum_", StringComparison.Ordinal);

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

    /// <summary>Este jugador descansó en un banco o murió.</summary>
    private void OnLocalRest() {
        if (_session == null || Time.unscaledTime - _lastRest < 3f) {
            return;
        }

        _lastRest = Time.unscaledTime;
        if (_session.IsHost) {
            Log.Info("Banco o muerte: los enemigos vuelven a aparecer");
            ClearDead();
        } else if (_session.CanSend) {
            Log.Info("Banco o muerte: se avisa al host para que los enemigos vuelvan a aparecer");
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

            // También la profundidad: un jefe dormido al fondo (Mawlek) pasa al primer plano al despertar, y lo que crea
            // sale desde ahí (al fondo quedaría tapado por el decorado)
            var transform = enemy.Transform;
            transform.position = new Vector3(state.X, state.Y, state.Z);
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

            // Cuánto daña al tocarlo lo decide su lógica (parada aquí): sin esto, un jefe que empieza sin dañar no
            // dañaría nunca al jugador de este PC
            if (enemy.Damage != null) {
                enemy.Damage.damageDealt = state.Damage;
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
        _creatorIds = null;
        ScanScenes();
        _creatorIds = null;
    }

    private void ScanScenes() {
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

                    // Un objeto con dos HealthManager (la segunda fase del Tirano de Almas del sueño lleva el suyo
                    // repetido, el segundo invulnerable) es un solo enemigo: el primero. Si no, salían dos enemigos con
                    // el mismo objeto y en la marioneta cada uno lo ponía en su sitio (saltaba entre dos posiciones)
                    if (health.GetComponent<HealthManager>() != health) {
                        continue;
                    }

                    // Los de jaula (las crías de la Colmena) no son de la sala: los saca la lógica de otro enemigo
                    if (TryCage(health)) {
                        continue;
                    }

                    // Los clonados en la raíz de la sala (los de las jaulas del Coliseo) se llaman igual y Unity les da a
                    // todos el mismo puesto entre hermanos: se distinguen por dónde están al cargarla, igual en todos
                    var cloned = health.transform.parent == null && health.name.EndsWith("(Clone)", StringComparison.Ordinal);
                    var id = cloned ? CreatorId(health.gameObject) ?? PlacedId(health.transform) : ScenePaths.StableHash(health.transform);
                    if (_byId.ContainsKey(id)) {
                        id = PlacedId(health.transform) ^ 0x5bd1e995u;
                        if (_byId.ContainsKey(id)) {
                            continue;
                        }
                    }

                    // Los crea la sala al cargarse (las jaulas del Coliseo preparan así los de cada oleada): como los que
                    // crea durante la partida, no quedan muertos para siempre. Si no, al repetir una prueba sin pasar por
                    // un banco la oleada esperaba a enemigos que el host daba por muertos y el Coliseo no avanzaba
                    AddEnemy(health, id).Created = cloned;
                }
            }
        }
    }

    /// <summary>Durante un repaso de la sala: clonados en la raíz y su identificador por quién los apunta.</summary>
    private Dictionary<GameObject, uint> _creatorIds;

    /// <summary>
    /// Clonados en la raíz que apunta una FSM de la sala que no es un enemigo (la jaula del Coliseo que lo creó guarda
    /// su enemigo en una variable): su identificador sale de esa FSM y esa variable. Por su sitio no basta: en Oro hay
    /// jaulas de oleadas distintas en el mismo punto, sus enemigos tenían la misma huella y cada PC le daba la de
    /// repuesto a uno distinto (el enemigo de una oleada era, en el otro PC, el de otra: no salía nunca).
    /// </summary>
    private uint? CreatorId(GameObject clone) {
        if (_creatorIds == null) {
            _creatorIds = new Dictionary<GameObject, uint>();
            foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None)) {
                if (fsm.gameObject.scene.buildIndex < 0 || IsEnemyObject(fsm.transform)) {
                    continue;
                }

                uint path = 0;
                foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
                    var value = variable.Value;
                    if (value == null || value.transform.parent != null || value.GetComponent<HealthManager>() == null) {
                        continue;
                    }

                    if (path == 0) {
                        path = ScenePaths.StableHash(fsm.transform) ^ ScenePaths.Fnv(fsm.FsmName);
                    }

                    var id = (path ^ ScenePaths.Fnv(variable.Name) * 16777619u) & ~SpawnedIdFlag;
                    // Si lo apuntan varias, la menor: igual en todos los PCs, sea cual sea el orden en que se recorren
                    if (!_creatorIds.TryGetValue(value, out var known) || id < known) {
                        _creatorIds[value] = id;
                    }
                }
            }
        }

        return _creatorIds.TryGetValue(clone, out var found) ? found : null;
    }

    /// <summary>Número compartido del enemigo (igual en todos los PCs), o 0 si no es uno de los compartidos.</summary>
    internal static uint SyncIdOf(HealthManager health) =>
        health != null && _active != null && _active._byHealth.TryGetValue(health, out var enemy) ? enemy.Id : 0u;

    /// <summary>Huella de un objeto por su sala, su nombre y su posición (redondeada a una décima).</summary>
    internal static uint PlacedId(Transform transform) {
        var hash = 2166136261u;
        foreach (var c in transform.gameObject.scene.name) {
            hash = (hash ^ c) * 16777619u;
        }

        foreach (var c in transform.name) {
            hash = (hash ^ c) * 16777619u;
        }

        var position = transform.position;
        hash = (hash ^ (uint) Mathf.RoundToInt(position.x * 10f)) * 16777619u;
        hash = (hash ^ (uint) Mathf.RoundToInt(position.y * 10f)) * 16777619u;
        return hash & ~SpawnedIdFlag;
    }

    private Enemy AddEnemy(HealthManager health, uint id) {
        var enemy = new Enemy {
            Id = id,
            Health = health,
            Root = health.gameObject,
            Transform = health.transform,
            Animator = health.GetComponent<tk2dSpriteAnimator>(),
            Body = health.GetComponent<Rigidbody2D>(),
            Renderer = health.GetComponent<Renderer>(),
            Collider = health.GetComponent<Collider2D>(),
            Damage = health.GetComponent<DamageHero>(),
            LastHp = health.hp,
            StaysDead = health.GetComponent<PersistentBoolItem>() != null
        };
        enemy.ScannedAt = Time.unscaledTime;
        _enemies.Add(enemy);
        _byId[id] = enemy;
        _byHealth[health] = enemy;
        WatchDisable(enemy);
        RegisterFsms(enemy);
        if (_pendingHealth.TryGetValue(id, out var pending)) {
            ApplyHealth(enemy, pending.Kind, pending.Hp);
        }

        if (_pendingControl.TryGetValue(id, out var controller)) {
            _pendingControl.Remove(id);
            ApplyControl(enemy, controller);
        }

        return enemy;
    }

    /// <summary>
    /// Un enemigo se acaba de encender: si es el bicho que suelta un tarro del Coleccionista al romperse (lo hace el código
    /// del tarro, no una FSM), está justo donde el tarro y es del tipo que lleva. Solo lo crea el tarro original: este PC
    /// lo sigue como algo creado en combate y los demás crean una copia que lo sigue (sus copias del tarro van vacías y
    /// se rompen sin soltar nada). Así hay siempre el mismo bicho en todos, aunque el jefe cambie de PC a mitad.
    /// </summary>
    private void NoteEnabled(HealthManager health) {
        if (_session == null || _scene.Length == 0 || _byHealth.ContainsKey(health) || _spawningCopy) {
            return;
        }

        var position = health.transform.position;
        foreach (var enemy in _enemies) {
            if (!enemy.Spawned || enemy.Puppet || enemy.Root == null || !enemy.Root.activeInHierarchy ||
                (enemy.Transform.position - position).sqrMagnitude > JarReach * JarReach) {
                continue;
            }

            var jar = enemy.Root.GetComponent<SpawnJarControl>();
            var prefab = jar != null ? JarEnemyField?.GetValue(jar) as GameObject : null;
            if (prefab == null || !health.name.StartsWith(prefab.name, StringComparison.Ordinal)) {
                continue;
            }

            var id = NextSpawnedId();
            Track(health.gameObject, id, pooled: true, puppet: false, _session.LocalId);
            if (_session.CanSend && OthersInScene()) {
                var writer = _session.Begin(EnemyJar);
                writer.WriteUShort(_sceneHash);
                writer.WriteUInt(id);
                writer.WriteString(prefab.name);
                writer.WriteInt(health.hp);
                writer.WriteFloat(position.x);
                writer.WriteFloat(position.y);
                _session.End();
            }

            return;
        }
    }

    /// <summary>Otro PC soltó el bicho de un tarro original: aquí se crea una copia que lo sigue.</summary>
    private void OnJar(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var prefabName = reader.ReadName(128);
        var hp = reader.ReadInt();
        var position = new Vector3(reader.ReadFloat(), reader.ReadFloat(), 0f);
        if (sceneHash != _sceneHash || (id & SpawnedIdFlag) == 0 || _byId.ContainsKey(id)) {
            return;
        }

        var prefab = FindPrefab(prefabName);
        if (prefab == null) {
            return;
        }

        GameObject copy;
        _spawningCopy = true;
        try {
            copy = prefab.Spawn(position, Quaternion.identity);
        } finally {
            _spawningCopy = false;
        }

        if (copy == null) {
            return;
        }

        var health = copy.GetComponent<HealthManager>();
        if (health != null) {
            health.hp = hp;
        }

        Track(copy, id, pooled: true, puppet: true, from.Id);
    }

    /// <summary>El prefab con ese nombre que tiene apuntado la lógica de algún enemigo (el jefe que llena los tarros).</summary>
    private GameObject FindPrefab(string name) {
        foreach (var enemy in _enemies) {
            if (enemy.Fsms == null) {
                continue;
            }

            foreach (var fsm in enemy.Fsms) {
                if (fsm == null) {
                    continue;
                }

                foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
                    if (variable.Value != null && variable.Value.name == name) {
                        return variable.Value;
                    }
                }
            }
        }

        return null;
    }

    // =====================================================================================================
    // Enemigos de jaula (las crías de la Colmena)
    // =====================================================================================================

    /// <summary>
    /// Si es un enemigo de jaula (su FSM tiene la variable "Cage" y un estado que sale con "SPAWN"), se apunta y no se
    /// sigue como enemigo de la sala. Al morir no muere: vuelve a su jaula con la vida repuesta, y el que los suelta (el
    /// Zombie Hive) saca uno cualquiera. Como enemigos de la sala, cada PC tenía vivos unos distintos.
    /// </summary>
    private bool TryCage(HealthManager health) {
        if (_caged.ContainsKey(health)) {
            return true;
        }

        foreach (var fsm in health.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmVariables.FindFsmGameObject(CageVariable) == null) {
                continue;
            }

            string rest = null;
            foreach (var state in fsm.FsmStates) {
                foreach (var transition in state.Transitions) {
                    if (transition.EventName == CageReleaseEvent) {
                        rest = state.Name;
                    }
                }
            }

            if (rest == null) {
                continue;
            }

            var caged = new CagedEnemy {
                Health = health, Fsm = fsm, Cage = health.transform.parent, RestState = rest,
                Path = ScenePaths.StableHash(health.transform), FullHp = health.hp
            };
            _caged[health] = caged;
            if (!_cagedByPath.ContainsKey(caged.Path)) {
                _cagedByPath[caged.Path] = caged;
            }

            return true;
        }

        return false;
    }

    /// <summary>El enemigo de jaula que este aviso saca de ella, si lo manda un enemigo real de este PC (si no, null).</summary>
    private CagedEnemy ReleaseTarget(HutongGames.PlayMaker.Actions.SendEventByName send) {
        if (_session == null || _caged.Count == 0 || _replaying || _playingFx || _spawningCopy ||
            send.sendEvent?.Value != CageReleaseEvent || !_enemyFsms.TryGetValue(send.Fsm, out var sender) ||
            sender.Enemy.Puppet) {
            return null;
        }

        var target = send.eventTarget;
        if (target == null || target.target != HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObject &&
            target.target != HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObjectFSM) {
            return null;
        }

        var receiver = send.Fsm.GetOwnerDefaultTarget(target.gameObject);
        var health = receiver != null ? receiver.GetComponent<HealthManager>() : null;
        return health != null && _caged.TryGetValue(health, out var caged) && !_byHealth.ContainsKey(health) ? caged : null;
    }

    /// <summary>
    /// Un enemigo real de este PC sacó uno de su jaula: este PC lo mueve hasta que vuelve a ella (muere) y los demás sacan
    /// el mismo de la suya como copia que lo sigue.
    /// </summary>
    private void OnReleased(CagedEnemy caged) {
        if (caged.Health == null || _byHealth.ContainsKey(caged.Health)) {
            return;
        }

        var id = NextSpawnedId();
        var enemy = Track(caged.Health.gameObject, id, pooled: false, puppet: false, _session.LocalId);
        enemy.Caged = caged;
        if (TestMode) {
            Log.Info($"[TEST] sale de su jaula {caged.Health.name} id={id:x8}");
        }

        if (!_session.CanSend || !OthersInScene()) {
            return;
        }

        var writer = _session.Begin(EnemyRelease);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(id);
        writer.WriteUInt(caged.Path);
        writer.WriteInt(caged.Health.hp);
        _session.End();
    }

    /// <summary>Otro PC sacó un enemigo de su jaula: aquí sale el mismo, como copia parada que sigue al original.</summary>
    private void OnRelease(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var path = reader.ReadUInt();
        var hp = reader.ReadInt();
        if (sceneHash != _sceneHash || (id & SpawnedIdFlag) == 0 || _byId.ContainsKey(id) ||
            !_cagedByPath.TryGetValue(path, out var caged) || caged.Health == null) {
            return;
        }

        if (_byHealth.TryGetValue(caged.Health, out var previous)) {
            // Lo movía este PC (los dos lo sacaron a la vez): se queda el de aquí
            if (!previous.Puppet || !previous.Spawned) {
                return;
            }

            // Una copia anterior cuya vuelta a la jaula aún no había llegado
            RemoveSpawned(previous);
        }

        // Sale como en el PC de origen (deja la jaula y su lógica pasa a perseguir) y enseguida se para como marioneta
        _spawningCopy = true;
        try {
            if (caged.Fsm != null) {
                caged.Fsm.SendEvent(CageReleaseEvent);
            }
        } finally {
            _spawningCopy = false;
        }

        caged.Health.transform.SetParent(null, true);
        caged.Health.hp = hp;
        Track(caged.Health.gameObject, id, pooled: false, puppet: true, from.Id).Caged = caged;
    }

    /// <summary>Un enemigo de jaula que mueve este PC volvió a ella (murió y su lógica lo devolvió).</summary>
    private static bool BackInCage(Enemy enemy) {
        var caged = enemy.Caged;
        if (caged == null || enemy.Puppet || enemy.Root == null) {
            return false;
        }

        var resting = caged.Fsm != null && caged.Fsm.ActiveStateName == caged.RestState;
        var inCage = caged.Cage != null ? enemy.Transform.parent == caged.Cage && resting : resting;
        if (!inCage) {
            enemy.LeftCage = true;
            return false;
        }

        return enemy.LeftCage;
    }

    /// <summary>La copia de un enemigo de jaula vuelve a ella en reposo (su original ya volvió o ya no hay quien lo mueva).</summary>
    private static void ReturnToCage(CagedEnemy caged) {
        if (caged.Health == null) {
            return;
        }

        var transform = caged.Health.transform;
        if (caged.Cage != null && transform.parent != caged.Cage) {
            transform.SetParent(caged.Cage, true);
            transform.localPosition = new Vector3(0f, 0f, transform.localPosition.z);
        }

        caged.Health.SetIsDead(false);
        // Vuelve con la vida de la jaula: si después este PC la soltaba como original, enviaba a todos su vida vieja
        if (caged.FullHp > 0) {
            caged.Health.hp = caged.FullHp;
        }

        if (caged.Health.GetComponent<Rigidbody2D>() is { } body) {
            body.velocity = Vector2.zero;
        }

        if (caged.Fsm != null && caged.Fsm.ActiveStateName != caged.RestState) {
            caged.Fsm.SetState(caged.RestState);
        }
    }

    /// <summary>Distancia del tarro a la que aparece su bicho.</summary>
    private const float JarReach = 4f;

    /// <summary>
    /// Enemigo creado durante la partida por un objeto de la sala que no es enemigo (cada jaula del Coliseo crea el suyo
    /// cuando se activa su oleada, después de repasar la sala). Cada PC lo crea igual, así que se sigue como uno de la
    /// sala; su identificador sale de quién lo creó (su sitio en la sala, la FSM, el estado y la acción), que es el mismo
    /// en todos los PCs aunque el orden de los objetos de la sala no lo sea. No se apunta como muerto para siempre.
    /// </summary>
    private void OnSceneCreated(HutongGames.PlayMaker.FsmStateAction action, GameObject created) {
        if (_session == null || _scene.Length == 0 || created == null || _spawningCopy || _enemyFsms.ContainsKey(action.Fsm)) {
            return;
        }

        var health = created.GetComponent<HealthManager>();
        var owner = action.Fsm.GameObject;
        if (health == null || _byHealth.ContainsKey(health) || owner == null || owner.scene.buildIndex < 0 ||
            IsEnemyObject(owner.transform)) {
            return;
        }

        var stateIndex = Array.IndexOf(action.Fsm.States, action.State);
        var actionIndex = action.State != null ? Array.IndexOf(action.State.Actions, action) : -1;
        var id = ScenePaths.StableHash(owner.transform);
        id = (id ^ ScenePaths.Fnv(action.Fsm.Name)) * 16777619u;
        id = (id ^ (uint) (stateIndex << 8 | actionIndex & 0xFF)) * 16777619u;
        id &= ~SpawnedIdFlag;
        if (_byId.ContainsKey(id)) {
            return;
        }

        AddEnemy(health, id).Created = true;
    }

    /// <summary>Apunta las FSM del enemigo: de quién son y qué estados suenan (solo al entrar o al crearse).</summary>
    private void RegisterFsms(Enemy enemy) {
        CollectParts(enemy);
        enemy.Fsms = enemy.Root.GetComponentsInChildren<PlayMakerFSM>(true);
        for (var i = 0; i < enemy.Fsms.Length && i < byte.MaxValue; i++) {
            RegisterFsm(enemy, i);
        }
    }

    private void RegisterFsm(Enemy enemy, int i) {
        _enemyFsms[enemy.Fsms[i].Fsm] = (enemy, (byte) i);

        // Las FSM de daño solo avisan de un golpe al objeto tocado (no se ve nada): sus avisos se enviaban en cada
        // contacto (los barriles del Falso Caballero, cientos por minuto) y la marioneta no hacía nada con ellos
        if (DamageFsms.Contains(enemy.Fsms[i].FsmName)) {
            return;
        }

        var states = enemy.Fsms[i].FsmStates;
        bool[] soundStates = null;
        HutongGames.PlayMaker.FsmStateAction[][] continuous = null;
        for (var s = 0; s < states.Length; s++) {
            foreach (var action in states[s].Actions) {
                if (IsEffectAction(action)) {
                    soundStates ??= new bool[states.Length];
                    soundStates[s] = true;
                    break;
                }
            }

            if (soundStates != null && soundStates[s] && ContinuousActions(states[s]) is { } running) {
                continuous ??= new HutongGames.PlayMaker.FsmStateAction[states.Length][];
                continuous[s] = running;
            }
        }

        if (soundStates != null) {
            _soundFsms[enemy.Fsms[i].Fsm] = new SoundFsm {
                Enemy = enemy, Index = (byte) i, SoundStates = soundStates, Continuous = continuous
            };
        }
    }

    /// <summary>FSM que solo pasan un golpe al objeto que tocan: sin efectos que copiar en la marioneta.</summary>
    private static readonly HashSet<string> DamageFsms = new() { "damages_enemy", "damages_hero" };

    /// <summary>Efectos que siguen cada frame mientras dura el estado (su <c>OnUpdate</c> solo hace lo que se ve).</summary>
    private static readonly HashSet<string> ContinuousEffectActions = new() {
        "SetParticleEmission", "SetParticleEmissionRate", "SetParticleEmissionSpeed", "ActivateGameObject",
        // Salpicaduras continuas: no hacen nada al entrar, todo en OnUpdate (la muerte del Falso Caballero y del
        // Campeón Fallido)
        "SpawnBloodTime", "SpawnRandomObjectsOverTime"
    };

    /// <summary>
    /// Cuentas con los números de su FSM que un efecto continuo puede usar (el ritmo del humo sube cada frame). En la
    /// marioneta su lógica está parada, así que tocar sus números no decide nada; al llegar el siguiente estado se
    /// ponen otra vez los del PC que la mueve.
    /// </summary>
    private static readonly HashSet<string> ContinuousVariableActions = new() {
        "FloatAdd", "FloatSubtract", "FloatMultiply", "FloatDivide", "FloatClamp", "FloatOperator", "IntAdd",
        "IntOperator", "IntClamp", "SetFloatValue", "SetIntValue",
        // Con tiempo: empieza al entrar (el humo baja a 0 en medio segundo antes de acabar). Su aviso al acabar no se manda
        // en la marioneta (movería su lógica parada): ver WithoutFinishEvent
        "EaseFloat"
    };

    private static readonly Dictionary<Type, System.Reflection.FieldInfo> EveryFrameFields = new();

    /// <summary>Si la acción sigue cada frame (su campo <c>everyFrame</c>, o las que solo trabajan en OnUpdate).</summary>
    private static bool RunsEveryFrame(HutongGames.PlayMaker.FsmStateAction action) {
        var type = action.GetType();
        if (type.Name is "SpawnBloodTime" or "SpawnRandomObjectsOverTime") {
            return true;
        }

        // Con aviso al acabar también: en la marioneta se hace sin él (ver WithoutFinishEvent)
        if (action is HutongGames.PlayMaker.Actions.EaseFsmAction) {
            return true;
        }

        if (!EveryFrameFields.TryGetValue(type, out var field)) {
            field = type.GetField("everyFrame");
            EveryFrameFields[type] = field != null && field.FieldType == typeof(bool) ? field : null;
            field = EveryFrameFields[type];
        }

        return field != null && (bool) field.GetValue(action);
    }

    /// <summary>
    /// Lo que la marioneta repite cada frame en este estado: sus efectos continuos y las cuentas que hacen cada frame (si
    /// hay alguno). Antes solo se hacía al entrar: el humo del vapor del Falso Caballero al morir salía con el ritmo del
    /// principio (más flojo) y la salpicadura continua no salía. Se calcula al apuntar el enemigo.
    /// </summary>
    private static HutongGames.PlayMaker.FsmStateAction[] ContinuousActions(HutongGames.PlayMaker.FsmState state) {
        List<HutongGames.PlayMaker.FsmStateAction> found = null;
        var effects = false;
        foreach (var action in state.Actions) {
            var name = action.GetType().Name;
            var effect = ContinuousEffectActions.Contains(name);
            if (!action.Enabled || !effect && !ContinuousVariableActions.Contains(name) || !RunsEveryFrame(action)) {
                continue;
            }

            effects |= effect;
            (found ??= new List<HutongGames.PlayMaker.FsmStateAction>()).Add(action);
        }

        return effects ? found.ToArray() : null;
    }

    /// <summary>Acciones visuales que la marioneta también hace (además del sonido): no deciden nada de su lógica.</summary>
    private static readonly HashSet<string> EffectActions = new() {
        "ActivateGameObject", "PlayParticleEmitter", "StopParticleEmitter", "SetParticleEmission", "SetParticleEmissionRate",
        "SetParticleEmissionSpeed", "SpawnRandomObjects", "FlingObjectsFromGlobalPool", "FlingObjectsFromGlobalPoolVel",
        "SendEventByName", "CreateObject",
        // Los escombros que caen del techo al empezar el combate del Falso Caballero: piedras de su propio pool, que su
        // lógica enciende y lanza (solo se ven)
        "ActivateAllChildren", "SpawnFromPool",
        // Las salpicaduras al romperse (los barriles que caen en ese combate)
        "SpawnBlood",
        // Salpicaduras continuas (solo en OnUpdate: van con los efectos continuos del estado)
        "SpawnBloodTime", "SpawnRandomObjectsOverTime"
    };

    /// <summary>
    /// Acciones de un estado que la marioneta hace cuando el enemigo real entra en él: sonido y música, y lo que se ve
    /// de la arena (objetos que se activan, partículas, ráfagas, efectos, avisos a otros objetos).
    /// </summary>
    private static bool IsEffectAction(HutongGames.PlayMaker.FsmStateAction action) {
        var name = action.GetType().Name;
        return name.IndexOf("Audio", StringComparison.Ordinal) >= 0 || name.IndexOf("Sound", StringComparison.Ordinal) >= 0 ||
               name.IndexOf("Music", StringComparison.Ordinal) >= 0 || EffectActions.Contains(name) || IsAreaTitleAction(action);
    }

    private const string AreaTitleVariable = "Area Title";

    /// <summary>
    /// El nombre del jefe al despertar: su lógica busca el título de zona, lo enciende y le dice qué nombre poner. En la
    /// marioneta se hace todo (antes solo se encendía, vacío, y en ese PC no salía el nombre).
    /// </summary>
    private static bool IsAreaTitleAction(HutongGames.PlayMaker.FsmStateAction action) {
        return action switch {
            HutongGames.PlayMaker.Actions.SetGameObject set => set.variable?.Name == AreaTitleVariable,
            HutongGames.PlayMaker.Actions.SetFsmBool flag => TargetsAreaTitle(flag.gameObject),
            HutongGames.PlayMaker.Actions.SetFsmString text => TargetsAreaTitle(text.gameObject),
            _ => false
        };
    }

    private static bool TargetsAreaTitle(HutongGames.PlayMaker.FsmOwnerDefault target) =>
        target != null && target.OwnerOption == HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject &&
        target.GameObject?.Name == AreaTitleVariable;

    /// <summary>
    /// Si la marioneta hace esta acción. No: lo que suelta geo; lo que crea con vida o daño (llega como copia); los avisos
    /// a todos, a sí mismo (son su lógica) o al caballero (el rugido lo bloquearía en este PC sin garantía de soltarlo).
    /// </summary>
    private static bool ShouldRunEffect(Enemy enemy, HutongGames.PlayMaker.FsmStateAction action) {
        switch (action) {
            // La reaparición tras los pinchos es para el caballero de cada PC (la Radiancia la sube con la pelea en
            // `Climb Plats1`): sin ella, el compañero que caía al abismo reaparecía en el punto viejo
            case HutongGames.PlayMaker.Actions.CallMethodProper { methodName.Value: "SetHazardRespawn" } respawn:
                var respawnHero = HeroController.instance;
                return respawnHero != null && action.Fsm.GetOwnerDefaultTarget(respawn.gameObject) == respawnHero.gameObject;
            case HutongGames.PlayMaker.Actions.SpawnRandomObjects random:
                return random.gameObject.Value != null && !IsGeo(random.gameObject.Value);
            case HutongGames.PlayMaker.Actions.SpawnRandomObjectsOverTime overTime:
                return overTime.gameObject.Value != null && !IsGeo(overTime.gameObject.Value) &&
                       !IsGameplay(overTime.gameObject.Value);
            // Lo lanzado que hace daño llega como copia del original (ver _flinging): si la marioneta lo lanzaba también,
            // cada PC tenía sus propios proyectiles con otras trayectorias al azar, que dañaban aparte
            case HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPool fling:
                return fling.gameObject.Value != null && !IsGeo(fling.gameObject.Value) && !IsGameplay(fling.gameObject.Value);
            case HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPoolVel flingVel:
                return flingVel.gameObject.Value != null && !IsGeo(flingVel.gameObject.Value) &&
                       !IsGameplay(flingVel.gameObject.Value);
            case HutongGames.PlayMaker.Actions.CreateObject create:
                return create.gameObject.Value != null && !IsGameplay(create.gameObject.Value);
            case HutongGames.PlayMaker.Actions.SendEventByName send:
                var target = send.eventTarget;
                GameObject receiver;
                switch (target.target) {
                    case HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObject:
                    case HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObjectFSM:
                        receiver = action.Fsm.GetOwnerDefaultTarget(target.gameObject);
                        break;
                    case HutongGames.PlayMaker.FsmEventTarget.EventTarget.FSMComponent:
                        receiver = target.fsmComponent != null ? target.fsmComponent.gameObject : null;
                        break;
                    default:
                        return false;
                }

                // Sacar de su jaula a un enemigo de jaula: lo hace solo el PC que mueve al que lo saca (llega como copia)
                if (receiver != null && _active != null && receiver.GetComponent<HealthManager>() is { } released &&
                    _active._caged.ContainsKey(released)) {
                    return false;
                }

                // Lo que va con el enemigo (el invocador de barriles) solo funciona en el PC que lo mueve; lo que crea
                // llega como copia
                if (IsSatellite(enemy, receiver)) {
                    return false;
                }

                var hero = HeroController.instance;
                return receiver != null && receiver != enemy.Root &&
                       (hero == null || receiver.transform != hero.transform && !receiver.transform.IsChildOf(hero.transform));
            default:
                return true;
        }
    }

    private static bool IsGeo(GameObject prefab) => prefab.name.StartsWith("Geo", StringComparison.Ordinal);

    /// <summary>
    /// La lógica del enemigo suelta una pieza suya (la deja sin padre): el Hollow Knight al morir suelta su cadáver, que
    /// sigue solo en la sala (cae, humea y es donde el caballero absorbe el vacío para el final). En la marioneta su
    /// lógica estaba parada y el cadáver se apagaba con el enemigo: en ese PC no aparecía y nadie podía acabar. Aquí se
    /// suelta igual y sus FSM dejan de ser de la marioneta (funcionan solas, como en el PC que lo movía).
    /// </summary>
    private void DetachPart(Enemy enemy, HutongGames.PlayMaker.Actions.SetParent action) {
        var part = action.Fsm.GetOwnerDefaultTarget(action.gameObject);

        // La variable con la pieza la llena su preparación, que en la marioneta no se hizo; y en el original ya está
        // suelta cuando se envía el efecto, así que no llega como suya. Lo que se suelta al morir es su cadáver: se busca
        // entre sus hijos y se apunta en la variable (la acción siguiente lo enciende)
        var variable = action.gameObject?.GameObject;
        if (part == null && variable != null && !string.IsNullOrEmpty(variable.Name)) {
            foreach (var child in enemy.Transform.GetComponentsInChildren<Transform>(true)) {
                if (child != enemy.Transform && child.name.IndexOf("Corpse", StringComparison.Ordinal) >= 0 &&
                    child.GetComponent<PlayMakerFSM>() != null) {
                    part = child.gameObject;
                    variable.Value = part;
                    break;
                }
            }
        }

        if (part == null || action.parent.Value != null || part == enemy.Root || !part.transform.IsChildOf(enemy.Transform)) {
            if (TestMode) {
                Log.Info($"[TEST] {enemy.Root.name}: no se suelta {(part != null ? part.name : "(nada)")} " +
                         $"padre nuevo={(action.parent.Value != null ? action.parent.Value.name : "-")}");
            }

            return;
        }

        part.transform.SetParent(null, true);
        foreach (var fsm in part.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (!enemy.Disabled.Remove(fsm)) {
                continue;
            }

            var paused = enemy.PausedFsms.FindIndex(entry => entry.Fsm == fsm);
            if (paused >= 0) {
                fsm.Fsm.RestartOnEnable = enemy.PausedFsms[paused].Restart;
                enemy.PausedFsms.RemoveAt(paused);
            }

            _enemyFsms.Remove(fsm.Fsm);
            _soundFsms.Remove(fsm.Fsm);
            (enemy.Detached ??= new List<PlayMakerFSM>()).Add(fsm);
            fsm.enabled = true;
        }

        Log.Info($"{enemy.Root.name}: suelta {part.name} como en el PC que lo movía");
    }

    /// <summary>
    /// Aviso a todos que manda un jefe ya muerto en el PC que lo mueve: ya no es su lógica de combate sino lo que sigue en la
    /// sala tras vencerlo (Grimm manda GRIMM DEFEATED para que aparezca el Grimm que se inclina y habla). En la marioneta
    /// su lógica está parada y ese aviso no salía, así que en este PC no pasaba nada tras el combate.
    /// </summary>
    private static bool IsDeathBroadcast(HutongGames.PlayMaker.FsmStateAction action, out string eventName) {
        eventName = null;
        if (action is not HutongGames.PlayMaker.Actions.SendEventByName send ||
            send.eventTarget.target != HutongGames.PlayMaker.FsmEventTarget.EventTarget.BroadcastAll) {
            return false;
        }

        eventName = send.sendEvent?.Value;
        return !string.IsNullOrEmpty(eventName);
    }

    /// <summary>
    /// El aviso del jefe muerto a las FSM activas de la sala, como el aviso a todos del juego, salvo a las del propio
    /// enemigo (su lógica está parada en la marioneta) y a las del caballero.
    /// </summary>
    private static void BroadcastFromDead(Enemy enemy, string eventName) {
        var hero = HeroController.instance;
        var receivers = new List<PlayMakerFSM>();
        foreach (var fsm in PlayMakerFSM.FsmList) {
            if (fsm == null || !fsm.enabled || !fsm.gameObject.activeInHierarchy ||
                enemy.Transform != null && fsm.transform.IsChildOf(enemy.Transform) ||
                hero != null && fsm.transform.IsChildOf(hero.transform)) {
                continue;
            }

            receivers.Add(fsm);
        }

        Log.Info($"{enemy.Root.name} (muerto en el PC que lo movía) avisa a la sala: {eventName}");
        foreach (var fsm in receivers) {
            fsm.SendEvent(eventName);
        }
    }

    /// <summary>
    /// Una FSM entró en un estado: si es de un enemigo real de este PC y tiene efectos (sonido, arena), los demás los ven
    /// y oyen. No lo que pasa al prepararse ni al retomarlo (ya lo hizo cada PC, o no toca repetirlo).
    /// </summary>
    private void OnFsmState(HutongGames.PlayMaker.Fsm fsm, HutongGames.PlayMaker.FsmState state) {
        // Lo creado en combate no se prepara en cada PC (su copia está parada desde que sale): todo lo suyo cuenta. Los
        // barriles del Falso Caballero se rompen antes de 1,5 s y en el otro PC desaparecían sin romperse
        if (_takingOver || !_soundFsms.TryGetValue(fsm, out var sound) || sound.Enemy.Puppet || !_session.CanSend ||
            !sound.Enemy.Spawned && Time.unscaledTime - sound.Enemy.ScannedAt < PreparationSeconds) {
            return;
        }

        var index = Array.IndexOf(fsm.States, state);
        if (index < 0 || index >= sound.SoundStates.Length) {
            return;
        }

        // Al salir de un estado con efectos continuos también se avisa (aunque el nuevo no tenga efectos): si no, la
        // marioneta los seguiría haciendo
        var continuous = sound.Continuous != null && sound.Continuous[index] != null;
        var stopping = sound.Running && !continuous;
        sound.Running = continuous;
        if (!sound.SoundStates[index] && !stopping || !OthersInScene()) {
            return;
        }

        // Lo creado antes llega antes que los efectos de este estado (que pueden actuar sobre ello), como en el original
        FlushSpawns();
        var writer = _session.Begin(EnemySound);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(sound.Enemy.Id);
        writer.WriteByte(sound.Index);
        writer.WriteUShort((ushort) index);

        // Sus números tal como los usan sus acciones ahora (velocidad y ángulo de una ráfaga, posiciones...): en la
        // marioneta son de cuando se pausó
        var variables = fsm.Variables;
        var floats = variables.FloatVariables;
        var floatCount = Math.Min(floats.Length, MaxEffectVars);
        writer.WriteByte((byte) floatCount);
        for (var i = 0; i < floatCount; i++) {
            writer.WriteFloat(floats[i].Value);
        }

        var ints = variables.IntVariables;
        var intCount = Math.Min(ints.Length, MaxEffectVars);
        writer.WriteByte((byte) intCount);
        for (var i = 0; i < intCount; i++) {
            writer.WriteInt(ints[i].Value);
        }

        var vectors = variables.Vector3Variables;
        var vectorCount = Math.Min(vectors.Length, MaxEffectVectors);
        writer.WriteByte((byte) vectorCount);
        for (var i = 0; i < vectorCount; i++) {
            var vector = vectors[i].Value;
            writer.WriteFloat(vector.x);
            writer.WriteFloat(vector.y);
            writer.WriteFloat(vector.z);
        }

        // Y a qué objetos apuntan (él mismo, el caballero...): una marioneta que se activó ya parada (las de las jaulas
        // del Coliseo) nunca preparó su lógica y los tiene vacíos, y sus sonidos fallarían
        var objects = variables.GameObjectVariables;
        var objectCount = Math.Min(objects.Length, MaxEffectVectors);
        writer.WriteByte((byte) objectCount);
        for (var i = 0; i < objectCount; i++) {
            var reference = Reference(sound.Enemy, objects[i].Value);
            // Los de la sala no: buscarlos recorre la sala, y esto pasa en cada efecto
            var kind = reference.Kind == ObjectKind.Scene ? ObjectKind.None : reference.Kind;
            writer.WriteByte((byte) kind);
            if (kind == ObjectKind.Own) {
                writer.WriteUInt(reference.Path);
            }
        }

        // Si ya murió aquí: sus avisos a todos son entonces para la sala (en la marioneta no se sabe). Solo muerto de
        // verdad (o con su muerte en marcha por ZERO HP): el Hollow Knight pasa por vida negativa entre fases y sus avisos
        // de entonces son de su combate
        var health = sound.Enemy.Health;
        writer.WriteBool(sound.Enemy.ZeroHp || sound.Enemy.DeathState || health != null && health.isDead);
        _session.End();
    }

    private void OnSound(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var fsmIndex = reader.ReadByte();
        var stateIndex = reader.ReadUShort();
        _byId.TryGetValue(id, out var enemy);
        var valid = sceneHash == _sceneHash && enemy != null && enemy.Puppet && enemy.Fsms != null &&
                    fsmIndex < enemy.Fsms.Length && enemy.Fsms[fsmIndex] != null &&
                    stateIndex < enemy.Fsms[fsmIndex].FsmStates.Length;
        var variables = valid ? enemy.Fsms[fsmIndex].FsmVariables : null;

        // Los números con los que lo hizo el original, antes de hacer sus efectos aquí
        var floatCount = reader.ReadByte();
        for (var i = 0; i < floatCount; i++) {
            var value = reader.ReadFloat();
            if (variables != null && i < variables.FloatVariables.Length) {
                variables.FloatVariables[i].Value = value;
            }
        }

        var intCount = reader.ReadByte();
        for (var i = 0; i < intCount; i++) {
            var value = reader.ReadInt();
            if (variables != null && i < variables.IntVariables.Length) {
                variables.IntVariables[i].Value = value;
            }
        }

        var vectorCount = reader.ReadByte();
        for (var i = 0; i < vectorCount; i++) {
            var value = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
            if (variables != null && i < variables.Vector3Variables.Length) {
                variables.Vector3Variables[i].Value = value;
            }
        }

        var objectCount = reader.ReadByte();
        for (var i = 0; i < objectCount; i++) {
            var reference = new ObjectRef { Kind = (ObjectKind) reader.ReadByte() };
            if (reference.Kind == ObjectKind.Own) {
                reference.Path = reader.ReadUInt();
            }

            if (variables != null && i < variables.GameObjectVariables.Length && reference.Kind != ObjectKind.None &&
                variables.GameObjectVariables[i].Value == null) {
                variables.GameObjectVariables[i].Value = Resolve(enemy, reference);
            }
        }

        var deadThere = reader.ReadBool();
        if (!valid) {
            return;
        }

        enemy.DeadThere |= deadThere;

        if (enemy.Detached != null && enemy.Detached.Contains(enemy.Fsms[fsmIndex])) {
            return;
        }

        var states = enemy.Fsms[fsmIndex].FsmStates;

        // Su FSM cambió de estado allí: los efectos continuos del anterior se acaban
        StopRunningEffects(enemy, fsmIndex);

        // Solo sus efectos (sonido y arena); la lógica de la marioneta sigue parada
        var state = states[stateIndex];
        if (TestMode) {
            Log.Info($"[TEST] efecto de enemigo {enemy.Fsms[fsmIndex].gameObject.name}/{enemy.Fsms[fsmIndex].FsmName}/{state.Name}");
        }

        foreach (var action in state.Actions) {
            var detach = action as HutongGames.PlayMaker.Actions.SetParent;
            if (!action.Enabled || !IsEffectAction(action) && detach == null) {
                continue;
            }

            try {
                action.Init(state);
                if (detach != null) {
                    DetachPart(enemy, detach);
                    continue;
                }

                if (deadThere && IsDeathBroadcast(action, out var deathEvent)) {
                    BroadcastFromDead(enemy, deathEvent);
                    continue;
                }

                if (!ShouldRunEffect(enemy, action)) {
                    continue;
                }

                EffectsPlayed++;
                _replaying = true;
                try {
                    action.OnEnter();
                } finally {
                    _replaying = false;
                }
            } catch (Exception e) {
                Log.Warn($"Efecto de {enemy.Root.name} ({state.Name}, {action.GetType().Name}) no reproducido: {e.Message}");
            }
        }

        // Y los que siguen cada frame mientras su FSM esté en ese estado allí (hasta que llegue el siguiente)
        if (_soundFsms.TryGetValue(enemy.Fsms[fsmIndex].Fsm, out var sound) && sound.Continuous != null &&
            stateIndex < sound.Continuous.Length && sound.Continuous[stateIndex] is { } continuous) {
            foreach (var action in continuous) {
                action.Init(state);
                action.Active = true;
                action.Finished = false;
                // Las que van con tiempo empiezan al entrar (con los números del original al entrar, que ya llegaron)
                if (action is HutongGames.PlayMaker.Actions.EaseFsmAction) {
                    try {
                        WithoutFinishEvent(action, action.OnEnter);
                    } catch (Exception e) {
                        Log.Warn($"Efecto continuo de {enemy.Root.name} ({state.Name}) no empezado: {e.Message}");
                    }
                }
            }

            _runningEffects.Add(new RunningEffect {
                Enemy = enemy, Fsm = fsmIndex, Actions = continuous
            });
        }
    }

    /// <summary>
    /// Hace una acción de la marioneta sin el aviso que manda al acabar (<c>EaseFloat</c>): ese aviso es de su lógica, que
    /// está parada, y el PC que la mueve ya lo da. Así la marioneta también baja el vapor de la muerte del Falso Caballero
    /// a 0 (antes se saltaba y el vapor se quedaba echando partículas quietas).
    /// </summary>
    private static void WithoutFinishEvent(HutongGames.PlayMaker.FsmStateAction action, Action run) {
        if (action is not HutongGames.PlayMaker.Actions.EaseFsmAction ease || ease.finishEvent == null) {
            run();
            return;
        }

        var finish = ease.finishEvent;
        ease.finishEvent = null;
        try {
            run();
        } finally {
            ease.finishEvent = finish;
        }
    }

    private void StopRunningEffects(Enemy enemy, byte fsmIndex) {
        for (var i = _runningEffects.Count - 1; i >= 0; i--) {
            if (_runningEffects[i].Enemy == enemy && _runningEffects[i].Fsm == fsmIndex) {
                _runningEffects.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Los efectos continuos de las marionetas, cada frame como en el PC que las mueve (su <c>OnUpdate</c>): el ritmo
    /// del humo, las salpicaduras continuas. Se acaban al llegar otro estado de esa FSM, al pasar a moverla este PC o al
    /// dejar de seguirla (o si su FSM queda suelta).
    /// </summary>
    private void UpdateRunningEffects() {
        if (_runningEffects.Count == 0) {
            return;
        }

        for (var i = _runningEffects.Count - 1; i >= 0; i--) {
            var running = _runningEffects[i];
            var enemy = running.Enemy;
            // Su FSM pudo quedar suelta (funciona sola en este PC: ya hace sus efectos). Muerto por la vía normal o
            // apagado ya no llegan más estados que lo acaben: el efecto se quedaba pegado (salvo una muerte que va por su
            // lógica, como el vapor del Falso Caballero, que sí sigue enviando sus estados)
            if (!enemy.Puppet || enemy.Root == null || !_byId.TryGetValue(enemy.Id, out var tracked) || tracked != enemy ||
                enemy.Detached != null && enemy.Detached.Contains(enemy.Fsms[running.Fsm]) ||
                !enemy.Root.activeInHierarchy || enemy.Health != null && enemy.Health.isDead && !enemy.Dying) {
                _runningEffects.RemoveAt(i);
                continue;
            }

            _replaying = true;
            try {
                foreach (var action in running.Actions) {
                    if (action.Finished ||
                        action is HutongGames.PlayMaker.Actions.SpawnRandomObjectsOverTime && !ShouldRunEffect(enemy, action)) {
                        continue;
                    }

                    WithoutFinishEvent(action, action.OnUpdate);
                }
            } catch (Exception e) {
                Log.Warn($"Efecto continuo de {enemy.Root.name} no reproducido: {e.Message}");
                _runningEffects.RemoveAt(i);
            } finally {
                _replaying = false;
            }
        }
    }

    // =====================================================================================================
    // Quién mueve cada enemigo: el jugador más cercano
    // =====================================================================================================

    /// <summary>Quién mueve el enemigo: su controlador si sigue en la sala; si no, el dueño de la sala.</summary>
    private ushort ControllerOf(Enemy enemy, ushort roomOwner) =>
        enemy.Controller != 0 && InScene(enemy.Controller) ? enemy.Controller : roomOwner;

    private bool InScene(ushort playerId) =>
        playerId != 0 && _session.TryGetPlayer(playerId, out var player) && player.Scene == _scene;

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
        _childScratch.Clear();
        foreach (var enemy in _enemies) {
            if (enemy.Root == null || enemy.Health == null || enemy.Health.isDead) {
                continue;
            }

            // Lo creado en combate sigue con quien lo creó, salvo los bichos con vida (crías, los de los tarros) si ese
            // está caído o en pausa: antes se quedaban quietos o persiguiendo al caído y en los demás PCs no hacían nada
            if (enemy.Spawned && !Features.PartyState.IsInactive(ControllerOf(enemy, roomOwner))) {
                continue;
            }

            if (ParentOf(enemy) != null) {
                _childScratch.Add(enemy);
                continue;
            }

            // Un caído (o quien tiene el juego en pausa: sus enemigos se quedarían congelados para los demás) no se queda
            // con enemigos: pasan enseguida a quien sigue jugando. También los apagados: en una marioneta, apagado es lo
            // último que dijo quien la movía, y si ese ya no juega nadie lo cambiaría (cada PC creía que lo movía el otro)
            var current = ControllerOf(enemy, roomOwner);
            var currentDown = Features.PartyState.IsInactive(current);
            if (!enemy.Root.activeInHierarchy && !currentDown) {
                continue;
            }

            // Con su muerte en marcha se queda en el PC que la empezó mientras siga en la sala (aunque esté caído o en
            // pausa: allí sigue cuando vuelve). Si pasaba a otro, este seguía desde a mitad ("Cough" del Falso Caballero)
            // y la muerte no acababa: el cuerpo seguía dañando y las rejas no se abrían
            if (enemy.Dying && InScene(current)) {
                continue;
            }
            var position = enemy.Transform.position;
            var moving = MovingFast(enemy, position, now);
            var interval = enemy.Health.hp >= BossHp ? BossSwitchInterval : SwitchInterval;
            if (now - enemy.LastSwitch < interval && !currentDown || moving && !currentDown) {
                continue;
            }

            // Los que están dentro de su zona de alerta van primero: su lógica solo ataca si su caballero está dentro.
            // El Rey Vengamosca se iba a esperar a su sitio ("Lurk") siguiendo al más cercano, fuera de la zona, mientras
            // el otro peleaba dentro, y se quedaba mucho rato sin atacar
            var zone = AlertZone(enemy);
            var currentInZone = false;
            var currentDistance = float.MaxValue;
            if (!currentDown && TryGetPlayerPosition(current, out var currentPosition)) {
                currentDistance = Vector2.Distance(position, currentPosition);
                currentInZone = InZone(zone, currentPosition);
            }

            var nearest = current;
            var nearestDistance = currentDistance;
            var nearestInZone = currentInZone;
            foreach (var player in _session.Players) {
                if (player.Scene != _scene || player.Id == current || Features.PartyState.IsInactive(player.Id) ||
                    !TryGetPlayerPosition(player.Id, out var playerPosition)) {
                    continue;
                }

                var distance = Vector2.Distance(position, playerPosition);
                var inZone = InZone(zone, playerPosition);
                if (inZone && !nearestInZone || inZone == nearestInZone && distance < nearestDistance) {
                    nearest = player.Id;
                    nearestDistance = distance;
                    nearestInZone = inZone;
                }
            }

            // Dentro de su zona, el otro tiene que estar bastante más cerca para quitárselo: el Rey Vengamosca vuela a 13
            // de su caballero y quedaba más cerca del otro, pasaba a él, volaba hacia ese y volvía a quedar más cerca del
            // primero; cada cambio lo hacía esperar otra vez para atacar y se quedaba en el aire
            var margin = zone != null && currentInZone && nearestInZone ? ZoneSwitchMargin : SwitchMargin;
            if (nearest != current && (currentDown || nearestInZone && !currentInZone ||
                                       currentDistance - nearestDistance > margin)) {
                _controlOut.Add((enemy.Id, nearest));
                ApplyControl(enemy, nearest);
            }
        }

        // Los que cuelgan de otro enemigo van con él: su lógica (parada en la marioneta) los mueve y los enciende. La
        // cabeza del Falso Caballero, con vida propia, se repartía por distancia aparcada lejos del combate: el PC que
        // movía al jefe la ponía en su sitio al aturdirlo, pero en ese PC era marioneta y no se le podía golpear
        foreach (var child in _childScratch) {
            var leader = ControllerOf(child.Parent, roomOwner);
            if (leader != ControllerOf(child, roomOwner)) {
                _controlOut.Add((child.Id, leader));
                ApplyControl(child, leader);
            }
        }

        SendControlOut();
    }

    private readonly List<Enemy> _childScratch = new();

    /// <summary>
    /// Si un caballero en esa posición toca la zona: el juego mira si su caja la toca, no su centro, así que la zona se
    /// amplía con el tamaño de esa caja (sin zona, cuenta como dentro).
    /// </summary>
    private static bool InZone(Collider2D zone, Vector3 position) {
        if (zone == null) {
            return true;
        }

        if (!zone.isActiveAndEnabled) {
            return false;
        }

        var bounds = zone.bounds;
        bounds.Expand(new Vector3(2f, 4f, 0f));
        return bounds.Contains(new Vector3(position.x, position.y, bounds.center.z));
    }

    /// <summary>
    /// Zona de alerta del enemigo (el hijo "Alert Range..." con su caja: su lógica mira si el caballero está dentro). Se
    /// busca una vez; null si no tiene.
    /// </summary>
    private static Collider2D AlertZone(Enemy enemy) {
        if (enemy.AlertZoneSearched) {
            return enemy.AlertZone;
        }

        enemy.AlertZoneSearched = true;
        // La que apunta su lógica (la del Rey Vengamosca es un objeto de la sala, "Buzzer Range")
        foreach (var fsm in enemy.Fsms ?? Array.Empty<PlayMakerFSM>()) {
            var variable = fsm != null ? fsm.FsmVariables.FindFsmGameObject("Alert Range") : null;
            if (variable?.Value != null && variable.Value.GetComponent<Collider2D>() is { } zone) {
                enemy.AlertZone = zone;
                return zone;
            }
        }

        foreach (var child in enemy.Root.GetComponentsInChildren<Transform>(true)) {
            if (child != enemy.Transform && child.name.StartsWith("Alert Range", StringComparison.Ordinal) &&
                child.GetComponent<Collider2D>() is { } box) {
                enemy.AlertZone = box;
                break;
            }
        }

        return enemy.AlertZone;
    }

    /// <summary>Si desde el reparto anterior se movió más rápido que <see cref="FastSpeed"/> (y apunta dónde está ahora).</summary>
    private static bool MovingFast(Enemy enemy, Vector2 position, float now) {
        var elapsed = now - enemy.AssignAt;
        var fast = enemy.AssignAt > 0f && elapsed > 0.05f && elapsed < 2f &&
                   Vector2.Distance(position, enemy.AssignPosition) / elapsed > FastSpeed;
        enemy.AssignPosition = position;
        enemy.AssignAt = now;
        return fast;
    }

    private const int MaxParentChecks = 20;

    /// <summary>Enemigo vivo del que cuelga este en la jerarquía (se busca hasta encontrarlo, unas pocas veces).</summary>
    private Enemy ParentOf(Enemy enemy) {
        if (enemy.Parent == null && enemy.ParentChecks < MaxParentChecks) {
            enemy.ParentChecks++;
            for (var t = enemy.Root.transform.parent; t != null; t = t.parent) {
                var health = t.GetComponent<HealthManager>();
                if (health != null && _byHealth.TryGetValue(health, out var parent) && parent != enemy) {
                    enemy.Parent = parent;
                    break;
                }
            }
        }

        var p = enemy.Parent;
        return p != null && p.Root != null && p.Health != null && !p.Health.isDead ? p : null;
    }

    /// <summary>
    /// El reparto entero del dueño de la sala, a todos (cada 2 s si hay alguien más). Quien lo tiene igual no hace nada;
    /// quien no, pasa a moverlo o lo suelta como con un cambio normal.
    /// </summary>
    private void ResendControllers() {
        if (!OthersInScene()) {
            return;
        }

        var roomOwner = _session.LocalId;
        _controlOut.Clear();
        foreach (var enemy in _enemies) {
            if (enemy.Spawned || enemy.Root == null || enemy.Health == null || enemy.Health.isDead) {
                continue;
            }

            _controlOut.Add((enemy.Id, ControllerOf(enemy, roomOwner)));
        }

        SendControlOut();
    }

    private void SendControlOut() {
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
            if (sceneHash != _sceneHash || from.Id != _session.OwnerOf(_scene)) {
                continue;
            }

            if (!_byId.TryGetValue(id, out var enemy)) {
                // Aún no creado aquí: se aplica al crearse
                if (_pendingControl.Count < 256) {
                    _pendingControl[id] = controller;
                }
            } else if (!enemy.Spawned) {
                ApplyControl(enemy, controller);
            }
        }
    }

    /// <summary>
    /// Cambia quién mueve el enemigo. Si lo movía este PC, envía al nuevo cómo iba (estado y números de cada FSM) y pasa
    /// a ser marioneta; el nuevo lo sigue desde ahí en cuanto lo recibe.
    /// </summary>
    private void ApplyControl(Enemy enemy, ushort controller) {
        // Un traspaso guardado que no es para este PC se tira también si el control no cambia según este PC (el que lo
        // envió ya se fue y aquí ya se daba por movido por el dueño): con 3 o más jugadores, uno viejo se usaba minutos
        // después en otro cambio
        if (controller != _session.LocalId) {
            enemy.Handover = null;
            enemy.HandoverReady = false;
        }

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

        ReleaseRoarLock(enemy);

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
            writer.WriteFloat(fsm != null ? WaitedInState(fsm.Fsm.ActiveState) : 0f);
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

            // Sus textos: el Señor Mantis de la segunda fase elige pared y guarda hacia dónde lanzar (`Shot Event`) antes
            // de llegar a ella; retomado entre medias, lanzaba con el texto de su lanzamiento anterior, a veces hacia
            // fuera de la arena desde la otra pared
            var strings = variables?.StringVariables;
            var stringCount = strings == null ? 0 : Math.Min(strings.Length, MaxHandoverVars);
            var objectTotal = variables?.GameObjectVariables == null ? 0
                : Math.Min(variables.GameObjectVariables.Length, MaxHandoverVars);
            // Lo que va detrás de los textos: objetos (hasta 5 bytes cada uno), física, aspecto, si estaba despierto y su
            // Walker. Un jefe con muchos textos pasaba del tamaño del mensaje y el envío fallaba dentro del cambio de sala
            var reserve = 1 + objectTotal * 5 + 1 + 13 + 6 + 14 + 8;
            writer.WriteByte((byte) stringCount);
            for (var v = 0; v < stringCount; v++) {
                // Uno demasiado largo no va (el otro PC deja el suyo): no son los que eligen lados ni ataques
                var value = strings[v].Value ?? string.Empty;
                var size = System.Text.Encoding.UTF8.GetByteCount(value);
                var fits = size <= MaxHandoverString && writer.Remaining - (size + 3) > reserve;
                writer.WriteBool(fits);
                if (fits) {
                    writer.WriteString(value);
                }
            }

            // Sus variables de objeto (el caballero al que sigue, él mismo...): las que guarda al despertar no las tiene
            // la marioneta, que no pasó por ahí
            var objects = variables?.GameObjectVariables;
            var objectCount = objects == null ? 0 : Math.Min(objects.Length, MaxHandoverVars);
            writer.WriteByte((byte) objectCount);
            for (var v = 0; v < objectCount; v++) {
                var reference = Reference(enemy, objects[v].Value);
                writer.WriteByte((byte) reference.Kind);
                if (reference.Kind is ObjectKind.Own or ObjectKind.Scene) {
                    writer.WriteUInt(reference.Path);
                }
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

            // Si está encendido, si se dibuja, si choca, si es invencible y cuánto daña (la marioneta del que lo recibe
            // los copia con retraso), y si su lógica ya lo soltó de su padre (ver MatchPrevious)
            writer.WriteBool(enemy.Root != null && enemy.Root.activeSelf);
            var now = enemy.Root != null ? Capture(enemy, 0) : default;
            var look = now.Extra;
            if (enemy.WaitingWake) {
                // La invulnerabilidad de la espera para despertar es de este PC: si iba, el que lo toma lo dejaba
                // invulnerable para siempre (allí no espera y nunca la quita)
                look = (byte) (enemy.WasInvincible ? look | InvincibleFlag : look & ~InvincibleFlag);
            }

            writer.WriteByte(look);
            writer.WriteByte(now.Damage);
            writer.WriteBool(enemy.Root != null && enemy.Transform.parent == null);
            // Si ya despertó: el que lo toma a mitad del combate no apunta su estado de ahora como el de dormido (si no, su
            // siguiente cambio de estado esperaba a todos y el jefe se paraba invulnerable)
            writer.WriteBool(enemy.Awake);
            WriteWalker(writer, enemy);
            _session.End();
        }
    }

    private void OnHandover(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var index = reader.ReadByte();
        var count = reader.ReadByte();
        var state = reader.ReadUShort();
        var waited = reader.ReadFloat();
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

        var strings = new string[reader.ReadByte()];
        for (var v = 0; v < strings.Length; v++) {
            strings[v] = reader.ReadBool() ? reader.ReadString(MaxHandoverString) : null;
        }

        var objects = new ObjectRef[reader.ReadByte()];
        for (var v = 0; v < objects.Length; v++) {
            objects[v].Kind = (ObjectKind) reader.ReadByte();
            if (objects[v].Kind is ObjectKind.Own or ObjectKind.Scene) {
                objects[v].Path = reader.ReadUInt();
            }
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

        var active = reader.ReadBool();
        var look = reader.ReadByte();
        var damage = reader.ReadByte();
        var detached = reader.ReadBool();
        var awake = reader.ReadBool();
        var walker = ReadWalker(reader);
        // También uno que aún no es marioneta porque este PC acaba de entrar en la sala (se toma igual, ver Update)
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var enemy) || index >= count ||
            !enemy.Puppet && Time.unscaledTime - enemy.ScannedAt > PuppetDelay + 1f) {
            return;
        }

        enemy.Handover ??= new HandoverFsm[count];
        if (enemy.Handover.Length != count) {
            enemy.Handover = new HandoverFsm[count];
        }

        enemy.Handover[index] = new HandoverFsm {
            State = state, Ints = ints, Floats = floats, Bools = bools, Strings = strings, Objects = objects, HasBody = hasBody, Velocity = velocity, Gravity = gravity,
            BodyType = bodyType, Active = active, Look = look, Damage = damage, Detached = detached, Waited = waited,
            Awake = awake, Walker = walker
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

        ApplyPendingEvents(enemy);
        ApplyPendingHits(enemy);
    }

    /// <summary>Los avisos reenviados que llegaron mientras este PC esperaba a tomarlo: ahora los atiende su lógica.</summary>
    private static void ApplyPendingEvents(Enemy enemy) {
        if (enemy.PendingEvents == null || enemy.Puppet) {
            return;
        }

        var pending = enemy.PendingEvents;
        enemy.PendingEvents = null;
        var now = Time.unscaledTime;
        foreach (var (fsmIndex, eventName, at) in pending) {
            if (now - at > PendingEventSeconds || enemy.Fsms == null || fsmIndex >= enemy.Fsms.Length ||
                enemy.Fsms[fsmIndex] == null) {
                continue;
            }

            Log.Info($"{enemy.Root.name}/{enemy.Fsms[fsmIndex].FsmName}: {eventName} llegó mientras se tomaba, se atiende ahora");
            enemy.Fsms[fsmIndex].SendEvent(eventName);
        }
    }

    private void TakeOverUnguarded(Enemy enemy) {
        var handover = enemy.Handover;
        enemy.Handover = null;
        enemy.HandoverReady = false;
        if (handover != null && Array.Exists(handover, h => h is { Awake: true })) {
            enemy.Awake = true;
        }

        if (handover is { Length: > 0 } && handover[0]?.Walker is { } walkerData) {
            ApplyWalker(enemy, walkerData);
        }

        MatchPrevious(enemy, handover);
        SetPuppet(enemy, false);
        enemy.SendFull = true;
        if (enemy.Body != null) {
            enemy.Body.velocity = Vector2.zero;
        }

        if (handover == null || enemy.Fsms == null) {
            // El `Climber` solo pone su velocidad al echar a andar y al girar: con la velocidad a 0 se quedaba quieto
            RestartWalkers(enemy);
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

            for (var v = 0; v < data.Strings.Length && v < variables.StringVariables.Length; v++) {
                var variable = variables.StringVariables[v];
                if (data.Strings[v] == null) {
                    continue;
                }

                if (TestMode && variable.Value != data.Strings[v]) {
                    Log.Info($"[TEST] {enemy.Root.name}/{fsm.FsmName}: al retomarlo {variable.Name} pasa de " +
                             $"'{variable.Value}' a '{data.Strings[v]}'");
                }

                variable.Value = data.Strings[v];
            }

            // Solo las vacías aquí: las que ya apuntan a algo las preparó su propia lógica en este PC
            for (var v = 0; v < data.Objects.Length && v < variables.GameObjectVariables.Length; v++) {
                var variable = variables.GameObjectVariables[v];
                if (variable.Value == null && data.Objects[v].Kind != ObjectKind.None) {
                    variable.Value = Resolve(enemy, data.Objects[v]);
                    // Lo que su lógica creó allí (no está en esta sala): se crea aquí como lo crea ella
                    if (variable.Value == null && data.Objects[v].Kind == ObjectKind.Scene) {
                        variable.Value = RecreateStored(fsm, variable.Name);
                    }
                }
            }

            var states = fsm.Fsm.States;
            if (data.State < states.Length && fsm.Fsm.ActiveState != states[data.State]) {
                // Lo que crea ese estado al entrar ya lo creó el PC que lo movía (sus disparos siguen en el aire): si se
                // repetía, el enemigo atacaba dos veces cada vez que cambiaba de PC a mitad de un ataque
                MuteSpawns(states[data.State]);
                try {
                    fsm.Fsm.SetState(states[data.State].Name);
                } catch (Exception e) {
                    // A medias en ese estado podría quedarse parado: vuelve al principio de su lógica
                    Log.Warn($"No se pudo seguir {enemy.Root.name} desde {states[data.State].Name} ({e.Message}): " +
                             "empieza de nuevo");
                    try {
                        fsm.Fsm.SetState(fsm.Fsm.StartState);
                    } catch (Exception again) {
                        Log.Warn($"{enemy.Root.name} tampoco pudo empezar de nuevo: {again.Message}");
                    }
                } finally {
                    UnmuteSpawns();
                }

                // La espera de ese estado sigue por donde iba allí (si no, cada cambio de PC la empezaba de cero)
                KeepWaited(fsm.Fsm.ActiveState, data.Waited);
            } else if (data.State < states.Length) {
                // Ya estaba en ese estado (parado desde que pasó a marioneta): su espera también va como allí. Antes se
                // quedaba la de cuando se paró y el arreglo del Rey Vengamosca no valía si los dos estaban en el mismo
                KeepWaited(fsm.Fsm.ActiveState, data.Waited);
            }
        }

        var last = handover[handover.Length - 1];
        if (enemy.Body != null && last != null && last.HasBody) {
            enemy.Body.bodyType = last.BodyType;
            enemy.Body.gravityScale = last.Gravity;
            enemy.Body.velocity = last.Velocity;
        }

        RestartWalkers(enemy);

        RefreshZoneFlags(enemy);
        var first = handover.Length > 0 && enemy.Fsms.Length > 0 && enemy.Fsms[0] != null ? enemy.Fsms[0].ActiveStateName : "-";
        Log.Info($"{enemy.Root.name}: lo mueve este PC, sigue desde {first}");
    }

    /// <summary>
    /// Al retomarlo, como lo tenía el PC que lo movía: encendido o apagado, si se dibuja, si choca, si es invencible,
    /// cuánto daña y si cuelga de su padre (abajo). La marioneta copia todo eso (salvo el padre) de lo que llega, con el
    /// retraso de dibujo, y la lógica del enemigo solo lo cambia al pasar por ciertos estados. El enemigo de una jaula
    /// del Coliseo lo enciende en cada PC su jaula al abrirse y justo después la marioneta lo volvía a apagar con un
    /// estado de antes de salir; si este PC lo retomaba en ese momento (el caballero de aquí estaba junto a la jaula), se
    /// quedaba apagado como enemigo real: nadie lo volvía a encender, el otro PC copiaba "apagado" y la oleada esperaba
    /// para siempre a un enemigo vivo e invisible. Igual con su caja: el Electric Mage solo la enciende al despertar, y
    /// retomado justo entonces se quedaba sin ella (imposible de golpear).
    /// Se hace antes de devolverle su lógica (al encenderlo con ella parada sigue donde estaba, no empieza de nuevo).
    /// </summary>
    private static void MatchPrevious(Enemy enemy, HandoverFsm[] handover) {
        if (enemy.Root == null) {
            return;
        }

        bool active;
        byte look, damage;
        var data = handover != null ? Array.Find(handover, entry => entry != null) : null;
        if (data != null) {
            active = data.Active;
            look = data.Look;
            damage = data.Damage;
        } else if (enemy.Buffer.TryGetNewest(out var newest)) {
            // Sin cómo iba (el anterior ya no está o tardó): lo último que se supo de él
            active = newest.Visible;
            look = newest.Extra;
            damage = newest.Damage;
        } else {
            return;
        }

        if (enemy.Root.activeSelf != active) {
            Log.Info($"{enemy.Root.name}: al retomarlo se {(active ? "enciende" : "apaga")} como lo tenía el otro PC");
            enemy.Root.SetActive(active);
        }

        var shown = (look & ShownFlag) != 0;
        if (enemy.Renderer != null && enemy.Renderer.enabled != shown) {
            enemy.Renderer.enabled = shown;
        }

        var solid = (look & SolidFlag) != 0;
        if (enemy.Collider != null && enemy.Collider.enabled != solid) {
            Log.Info($"{enemy.Root.name}: al retomarlo {(solid ? "recupera" : "pierde")} su caja como la tenía el otro PC");
            enemy.Collider.enabled = solid;
        }

        if (enemy.Health != null) {
            enemy.Health.IsInvincible = (look & InvincibleFlag) != 0;
            enemy.Health.InvincibleFromDirection = look >> 4;
        }

        if (enemy.Damage != null) {
            enemy.Damage.damageDealt = damage;
        }

        // Los Mage del Coliseo cuelgan de su oleada hasta despertar, que los suelta; luego se teletransportan con
        // posiciones locales. Retomado tras despertar en otro PC, aquí seguía colgado y se teletransportaba lejos de la
        // arena (detrás de la pared, donde nadie lo alcanzaba: la oleada 46 de Oro esperaba para siempre)
        if (data != null && data.Detached && enemy.Transform.parent != null) {
            Log.Info($"{enemy.Root.name}: al retomarlo se suelta de {enemy.Transform.parent.name} como en el otro PC");
            enemy.Transform.SetParent(null, true);
        }
    }

    /// <summary>
    /// Zonas del enemigo que apuntan si el caballero está dentro (una FSM hija que al entrar y salir pone una variable
    /// de otra, como el "Hero Saver" de los Mage Blob del Coliseo: no salen mientras el caballero esté donde
    /// aparecerían). Lo que llega del anterior es su caballero; aquí se pone según si el de este PC está dentro. Si no,
    /// el Mage Blob heredaba "dentro" del otro PC, aquí nunca llegaba la salida y no salía nunca (la oleada esperaba).
    /// </summary>
    private static void RefreshZoneFlags(Enemy enemy) {
        var hero = HeroController.instance;
        var heroCollider = hero != null ? hero.GetComponent<Collider2D>() : null;
        if (heroCollider == null || enemy.Fsms == null) {
            return;
        }

        foreach (var zone in enemy.Fsms) {
            var collider = zone != null ? zone.GetComponent<Collider2D>() : null;
            if (collider == null) {
                continue;
            }

            string enterEvent = null, exitEvent = null;
            foreach (var state in zone.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is not HutongGames.PlayMaker.Actions.Trigger2dEvent trigger) {
                        continue;
                    }

                    var name = trigger.sendEvent?.Name;
                    if (trigger.trigger == PlayMakerUnity2d.Trigger2DType.OnTriggerExit2D) {
                        exitEvent ??= name;
                    } else {
                        enterEvent ??= name;
                    }
                }
            }

            if (enterEvent == null || exitEvent == null) {
                continue;
            }

            var inside = collider.enabled && collider.gameObject.activeInHierarchy && collider.IsTouching(heroCollider);
            var wanted = inside ? enterEvent : exitEvent;
            foreach (var state in zone.FsmStates) {
                foreach (var transition in state.Transitions) {
                    if (transition.EventName != wanted) {
                        continue;
                    }

                    var target = Array.Find(zone.FsmStates, s => s.Name == transition.ToState);
                    if (target == null) {
                        continue;
                    }

                    foreach (var action in target.Actions) {
                        if (action is HutongGames.PlayMaker.Actions.SetFsmBool set) {
                            var owner = zone.Fsm.GetOwnerDefaultTarget(set.gameObject);
                            var fsm = owner != null ? FSMUtility.LocateFSM(owner, set.fsmName.Value) : null;
                            var variable = fsm != null ? fsm.FsmVariables.FindFsmBool(set.variableName.Value) : null;
                            if (variable != null) {
                                variable.Value = set.setValue.Value;
                            }
                        }
                    }
                }
            }
        }
    }

    // =====================================================================================================
    // Jefes que despiertan al ver al caballero: esperan a que estén todos cerca
    // =====================================================================================================

    /// <summary>
    /// Si el enemigo pertenece a una batalla de arena (el Coliseo, las arenas con Battle Scene): lo despierta la arena,
    /// que ya espera a que entren todos, no verlo. Oblobble entra volando desde muy arriba y nadie llegaba a estar cerca.
    /// </summary>
    private static bool InBattleScene(Enemy enemy) {
        if (enemy.ArenaWaits == 0) {
            var arena = enemy.Health != null ? BattleSceneField?.GetValue(enemy.Health) as GameObject : null;
            enemy.ArenaWaits = (sbyte) (arena != null && ArenaWaits(arena) ? 1 : -1);
        }

        return enemy.ArenaWaits > 0;
    }

    /// <summary>
    /// Si la arena del enemigo es la que empieza el combate esperando a todos: es una zona que pisa el caballero (su
    /// espera es la de <see cref="FsmStateSync"/>) o la lleva un controlador compartido (el Coliseo). La de Mawlek no:
    /// no tiene zona propia (solo las de la cámara, que no empiezan nada), la empieza el propio Mawlek al ver al
    /// caballero, y con ella Mawlek despertaba sin esperar al otro.
    /// </summary>
    private static bool ArenaWaits(GameObject arena) {
        var zone = arena.GetComponent<Collider2D>();
        if (zone != null && zone.isTrigger && arena.GetComponent<PlayMakerFSM>() != null) {
            return true;
        }

        foreach (var fsm in arena.GetComponentsInParent<PlayMakerFSM>(true)) {
            if (FsmStateSync.IsSharedController(fsm.Fsm)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Apunta en qué estado duerme cada jefe (el de su lógica principal, ya preparada): su primer cambio desde ahí es
    /// despertar.
    /// </summary>
    private void NoteDormant(Enemy enemy) {
        // Los que crea una jaula de la arena (jefes del Coliseo) los saca la propia arena, con todos ya dentro: no esperan
        if (enemy.Created || enemy.Awake || enemy.DormantState != null || enemy.Spawned || enemy.Puppet || enemy.Fsms == null ||
            enemy.Health == null || enemy.Health.isDead || enemy.Health.hp < BossHp ||
            Time.unscaledTime - enemy.ScannedAt < PreparationSeconds) {
            return;
        }

        var best = -1;
        var last = enemy.FirstSatellite > 0 ? enemy.FirstSatellite : enemy.Fsms.Length;
        for (var i = 0; i < last; i++) {
            var fsm = enemy.Fsms[i];
            if (fsm != null && fsm.enabled && (best < 0 || fsm.FsmStates.Length > enemy.Fsms[best].FsmStates.Length)) {
                best = i;
            }
        }

        if (best >= 0 && enemy.Fsms[best].Fsm.ActiveState != null) {
            enemy.WakeFsm = best;
            enemy.DormantState = enemy.Fsms[best].Fsm.ActiveState.Name;
        }
    }

    /// <summary>
    /// La lógica de un jefe quiere dejar su estado dormido. Si algún jugador de la sala no está cerca, no despierta: se
    /// retiene (y es invulnerable, para que nadie le pegue mientras) hasta que lleguen todos.
    /// </summary>
    private bool HoldWake(HutongGames.PlayMaker.Fsm fsm, HutongGames.PlayMaker.FsmState to) {
        if (_takingOver || _session == null || !_enemyFsms.TryGetValue(fsm, out var owner)) {
            return false;
        }

        var enemy = owner.Enemy;
        if (enemy.Awake || enemy.Puppet || enemy.DormantState == null || owner.Index != enemy.WakeFsm) {
            return false;
        }

        if (fsm.ActiveState == null || fsm.ActiveState.Name != enemy.DormantState || !OthersInScene() || EveryoneNear(enemy) ||
            InBattleScene(enemy) || !LocalNear(enemy)) {
            WakeUp(enemy);
            return false;
        }

        enemy.HeldWake = to;
        if (!enemy.WaitingWake) {
            enemy.WaitingWake = true;
            enemy.WaitingWakeSince = Time.unscaledTime;
            enemy.WasInvincible = enemy.Health.IsInvincible;
            enemy.Health.IsInvincible = true;
            Log.Info($"{enemy.Root.name}: espera a que estén todos cerca para despertar");
        }

        return true;
    }

    /// <summary>Lo que un jefe espera como mucho a que estén todos cerca para despertar.</summary>
    private const float WakeHoldSeconds = 60f;

    private void WakeUp(Enemy enemy) {
        enemy.Awake = true;
        if (enemy.WaitingWake) {
            enemy.WaitingWake = false;
            if (enemy.Health != null) {
                enemy.Health.IsInvincible = enemy.WasInvincible;
            }
        }
    }

    /// <summary>Jefes retenidos: despiertan cuando están todos cerca (o si ya no los mueve este PC, lo decide el otro).</summary>
    private void UpdateWakeGates() {
        var now = Time.unscaledTime;
        foreach (var enemy in _enemies) {
            NoteDormant(enemy);
            if (!enemy.WaitingWake) {
                continue;
            }

            if (enemy.Puppet || enemy.Root == null || enemy.Health == null || enemy.Health.isDead) {
                enemy.WaitingWake = false;
                if (enemy.Health != null) {
                    enemy.Health.IsInvincible = enemy.WasInvincible;
                }

                continue;
            }

            // Como mucho un minuto: si falta el avatar de alguien o está lejos en pausa, el jefe esperaba invulnerable sin fin
            if (OthersInScene() && !EveryoneNear(enemy) && !InBattleScene(enemy) &&
                now - enemy.WaitingWakeSince < WakeHoldSeconds) {
                if (now >= enemy.NextWaitNotice) {
                    enemy.NextWaitNotice = now + 4f;
                    HKMultiplayer.UI.Notice.Show(Lang.T($"Esperando a {MissingNear(enemy)} para empezar el combate", $"Waiting for {MissingNear(enemy)} to start the fight"), 3.5f);
                }

                continue;
            }

            var fsm = enemy.Fsms[enemy.WakeFsm].Fsm;
            var to = enemy.HeldWake;
            WakeUp(enemy);
            if (to != null && fsm.ActiveState != null && fsm.ActiveState.Name == enemy.DormantState) {
                HutongGames.PlayMaker.FsmExecutionStack.PushFsm(fsm);
                try {
                    fsm.SetState(to.Name);
                    fsm.UpdateStateChanges();
                } finally {
                    HutongGames.PlayMaker.FsmExecutionStack.PopFsm();
                }
            }
        }
    }

    /// <summary>
    /// Si el caballero de este PC está cerca del jefe. Si no lo está, el jefe no despierta por verlo (lo despierta otra
    /// cosa: la presentación de la Radiancia la empieza su cartel y ella espera muy arriba de la arena): no se retiene,
    /// porque nadie llegaría nunca a estar cerca.
    /// </summary>
    private static bool LocalNear(Enemy enemy) {
        var hero = HeroController.instance;
        return hero != null && ((Vector2) (hero.transform.position - enemy.Transform.position)).sqrMagnitude <= WakeRange * WakeRange;
    }

    private bool EveryoneNear(Enemy enemy) {
        var position = enemy.Transform.position;
        var hero = HeroController.instance;
        if (hero == null || ((Vector2) (hero.transform.position - position)).sqrMagnitude > WakeRange * WakeRange) {
            return false;
        }

        foreach (var player in _session.Players) {
            // Un caído no bloquea la espera
            if (player.IsLocal || player.Scene != _scene || Features.PartyState.IsDown(player.Id)) {
                continue;
            }

            if (_players == null || !_players.TryGetAvatarObject(player.Id, out var avatar) ||
                ((Vector2) (avatar.transform.position - position)).sqrMagnitude > WakeRange * WakeRange) {
                return false;
            }
        }

        return true;
    }

    private string MissingNear(Enemy enemy) {
        var position = enemy.Transform.position;
        var names = new List<string>();
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene && !Features.PartyState.IsDown(player.Id) &&
                (_players == null || !_players.TryGetAvatarObject(player.Id, out var avatar) ||
                 ((Vector2) (avatar.transform.position - position)).sqrMagnitude > WakeRange * WakeRange)) {
                names.Add(player.Name);
            }
        }

        var hero = HeroController.instance;
        if (hero != null && ((Vector2) (hero.transform.position - position)).sqrMagnitude > WakeRange * WakeRange) {
            names.Add(Lang.T("ti", "you"));
        }

        return names.Count > 0 ? string.Join(", ", names) : Lang.T("los demás", "the others");
    }

    /// <summary>Si la FSM es de un enemigo que mueve este PC (su lógica funciona aquí, no es una marioneta).</summary>
    public static bool IsRealEnemyFsm(HutongGames.PlayMaker.Fsm fsm) =>
        _active != null && fsm != null && _active._enemyFsms.TryGetValue(fsm, out var owner) && !owner.Enemy.Puppet;

    /// <summary>Si en la sala hay un jefe vivo (con mucha vida): sus combates esperan a que entren todos.</summary>
    public static bool BossInRoom {
        get {
            var sync = _active;
            if (sync == null) {
                return false;
            }

            foreach (var enemy in sync._enemies) {
                if (!enemy.Spawned && enemy.Health != null && !enemy.Health.isDead && enemy.Health.hp >= BossHp) {
                    return true;
                }
            }

            return false;
        }
    }

    private static ObjectRef Reference(Enemy enemy, GameObject target) {
        if (target == null) {
            return default;
        }

        var hero = HeroController.instance;
        if (hero != null && target == hero.gameObject) {
            return new ObjectRef { Kind = ObjectKind.Hero };
        }

        if (enemy.Root != null && target.transform.IsChildOf(enemy.Transform)) {
            return new ObjectRef { Kind = ObjectKind.Own, Path = OwnPath(enemy.Transform, target.transform) };
        }

        return target.scene.buildIndex >= 0
            ? new ObjectRef { Kind = ObjectKind.Scene, Path = ScenePaths.StableHash(target.transform) }
            : default;
    }

    /// <summary>
    /// Al tomar un enemigo, un objeto solo visual que su lógica creó en el PC que lo movía y guardó en una variable (el
    /// vapor de la muerte del Falso Caballero, <c>Death Smoke</c>, con <c>CreateObject</c>): en este PC no existe, y su
    /// lógica, que sigue desde más adelante, ya no lo crea. Se crea con la misma acción. Antes la variable quedaba vacía:
    /// el vapor no salía en este PC y en el otro se quedaba a medias, congelado.
    /// </summary>
    private static GameObject RecreateStored(PlayMakerFSM fsm, string variableName) {
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                HutongGames.PlayMaker.FsmGameObject store;
                GameObject prefab;
                switch (action) {
                    case HutongGames.PlayMaker.Actions.CreateObject create:
                        store = create.storeObject;
                        prefab = create.gameObject.Value;
                        break;
                    case HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool spawn:
                        store = spawn.storeObject;
                        prefab = spawn.gameObject.Value;
                        break;
                    default:
                        continue;
                }

                if (store == null || store.IsNone || store.Name != variableName || prefab == null || IsGameplay(prefab)) {
                    continue;
                }

                _spawningCopy = true;
                try {
                    action.Init(state);
                    action.OnEnter();
                } catch (Exception e) {
                    Log.Warn($"{fsm.gameObject.name}/{fsm.FsmName}: no se pudo crear {variableName}: {e.Message}");
                } finally {
                    _spawningCopy = false;
                }

                if (store.Value != null) {
                    Log.Info($"{fsm.gameObject.name}/{fsm.FsmName}: {variableName} lo creó su lógica en el otro PC; " +
                             "se crea aquí al tomarlo");
                }

                return store.Value;
            }
        }

        return null;
    }

    private static GameObject Resolve(Enemy enemy, ObjectRef reference) {
        switch (reference.Kind) {
            case ObjectKind.Hero:
                return HeroController.instance != null ? HeroController.instance.gameObject : null;
            case ObjectKind.Own:
                foreach (var child in enemy.Root.GetComponentsInChildren<Transform>(true)) {
                    if (OwnPath(enemy.Transform, child) == reference.Path) {
                        return child.gameObject;
                    }
                }

                return null;
            case ObjectKind.Scene:
                // Raro (solo variables vacías al retomarlo) y caro: se recorre la sala
                foreach (var candidate in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                             FindObjectsSortMode.None)) {
                    if (candidate.gameObject.scene.buildIndex >= 0 && ScenePaths.StableHash(candidate) == reference.Path) {
                        return candidate.gameObject;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Huella de la ruta de una pieza dentro del enemigo (0: el propio enemigo).</summary>
    private static uint OwnPath(Transform root, Transform target) {
        var hash = 2166136261u;
        for (var t = target; t != null && t != root; t = t.parent) {
            foreach (var c in t.name) {
                hash = (hash ^ c) * 16777619u;
            }

            hash = (hash ^ (uint) t.GetSiblingIndex()) * 16777619u;
            hash = (hash ^ '/') * 16777619u;
        }

        return target == root ? 0u : hash;
    }

    // =====================================================================================================
    // Lo que crean los enemigos en combate
    // =====================================================================================================

    /// <summary>
    /// La lógica de un enemigo real de este PC creó algo. Los demás de la sala crean lo mismo con la misma acción de su
    /// marioneta. Si es de juego (hace daño o tiene vida: proyectiles, crías, invocaciones) se sigue como un enemigo más
    /// hasta que desaparece; si es solo visual (polvo, destellos), cada PC lo deja correr solo.
    /// </summary>
    /// <summary>
    /// Identificador de algo creado en combate en este PC: lleva el jugador que lo creó, porque cada PC cuenta por su
    /// cuenta y dos podrían dar el mismo número (se pisaban: quedaba una copia huérfana y quieta).
    /// </summary>
    private uint NextSpawnedId() =>
        SpawnedIdFlag | (uint) (_session.LocalId & 0x7F) << 24 | (++_spawnCounter & 0xFFFFFF);

    private static readonly System.Reflection.FieldInfo WaitTimerField =
        typeof(HutongGames.PlayMaker.Actions.Wait).GetField("timer", AnyInstance);

    private static readonly System.Reflection.FieldInfo WaitRandomTimerField =
        typeof(HutongGames.PlayMaker.Actions.WaitRandom).GetField("timer", AnyInstance);

    /// <summary>Lo que lleva esperando la espera del estado (la primera Wait o WaitRandom), o 0.</summary>
    private static float WaitedInState(HutongGames.PlayMaker.FsmState state) {
        if (state == null) {
            return 0f;
        }

        foreach (var action in state.Actions) {
            switch (action) {
                case HutongGames.PlayMaker.Actions.Wait { Enabled: true, realTime: false } wait:
                    return WaitTimerField?.GetValue(wait) is float t ? t : 0f;
                case HutongGames.PlayMaker.Actions.WaitRandom { Enabled: true, realTime: false } random:
                    return WaitRandomTimerField?.GetValue(random) is float r ? r : 0f;
            }
        }

        return 0f;
    }

    private static void KeepWaited(HutongGames.PlayMaker.FsmState state, float waited) {
        if (state == null || waited <= 0f) {
            return;
        }

        foreach (var action in state.Actions) {
            switch (action) {
                case HutongGames.PlayMaker.Actions.Wait { Enabled: true, realTime: false } wait:
                    WaitTimerField?.SetValue(wait, waited);
                    return;
                case HutongGames.PlayMaker.Actions.WaitRandom { Enabled: true, realTime: false } random:
                    WaitRandomTimerField?.SetValue(random, waited);
                    return;
            }
        }
    }

    private static readonly List<HutongGames.PlayMaker.FsmStateAction> MutedSpawns = new();

    /// <summary>Apaga un momento las acciones del estado que crean o lanzan objetos.</summary>
    private static void MuteSpawns(HutongGames.PlayMaker.FsmState state) {
        foreach (var action in state.Actions) {
            // También la variante Vel (lanza en OnEnter: el vómito de los Ogros Hongo, el escupitajo de los zombis, Nosk):
            // retomado a mitad, se lanzaba otra vez y la segunda ráfaga salía como copias en los demás
            if (action.Enabled && action is HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool or
                    HutongGames.PlayMaker.Actions.CreateObject or HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPool or
                    HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPoolVel) {
                action.Enabled = false;
                MutedSpawns.Add(action);
            }
        }
    }

    private static void UnmuteSpawns() {
        foreach (var action in MutedSpawns) {
            action.Enabled = true;
        }

        MutedSpawns.Clear();
    }

    /// <summary>El prefab que usa una acción de crear (o null).</summary>
    private static GameObject SpawnPrefab(HutongGames.PlayMaker.FsmStateAction action) => action switch {
        HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool spawn => spawn.gameObject?.Value,
        HutongGames.PlayMaker.Actions.CreateObject create => create.gameObject?.Value,
        _ => null
    };

    /// <summary>
    /// Un objeto que conoce la lógica del enemigo, por su nombre: en sus variables o en lo que ponen sus acciones
    /// `SetGameObject` (los prefabs que elige antes de crear).
    /// </summary>
    private static GameObject FindKnownObject(Enemy enemy, string name) {
        if (enemy.Fsms == null) {
            return null;
        }

        foreach (var fsm in enemy.Fsms) {
            if (fsm == null) {
                continue;
            }

            foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
                if (variable.Value != null && variable.Value.name == name) {
                    return variable.Value;
                }
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is HutongGames.PlayMaker.Actions.SetGameObject set && set.gameObject?.Value != null &&
                        set.gameObject.Value.name == name) {
                        return set.gameObject.Value;
                    }

                    // El enemigo que saca un invocador que crea (las mosquitas del Rey Vengamosca)
                    if (action is HutongGames.PlayMaker.Actions.CreateObject create && create.gameObject?.Value != null &&
                        create.gameObject.Value.GetComponent<EnemySpawner>() is { enemyPrefab: { } released } &&
                        released.name == name) {
                        return released;
                    }
                }
            }
        }

        return null;
    }

    private void OnSpawned(HutongGames.PlayMaker.FsmStateAction action, GameObject spawned, bool pooled,
        string prefabName = null) {
        if (_spawningCopy || _takingOver || spawned == null || _session == null || !_session.CanSend ||
            !_enemyFsms.TryGetValue(action.Fsm, out var creator) || creator.Enemy.Puppet || creator.Enemy.DiedElsewhere ||
            !OthersInScene()) {
            return;
        }

        // Lo que crea al prepararse (cadáveres, piezas propias) ya lo crea cada PC por su cuenta; y lo solo visual hecho
        // con CreateObject suele quedarse como pieza escondida del enemigo, no es un efecto que se acabe solo
        var gameplay = IsGameplay(spawned);
        if (!creator.Enemy.Spawned && Time.unscaledTime - creator.Enemy.ScannedAt < PreparationSeconds || !pooled && !gameplay) {
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
            // El pool lo recicló y lo volvió a sacar en el mismo frame, antes de que UpdateReal viera que se fue: la entrada
            // vieja se acaba (si no, dos entradas con el mismo objeto y dos copias en el otro PC)
            for (var i = _enemies.Count - 1; i >= 0; i--) {
                if (_enemies[i].Spawned && _enemies[i].Root == spawned) {
                    SendDespawn(_enemies[i].Id);
                    Forget(_enemies[i]);
                }
            }

            id = NextSpawnedId();
            Track(spawned, id, pooled, puppet: false, _session.LocalId);
        }

        // Se envía al acabar el estado (o el frame): las acciones siguientes de su estado le ponen tamaño, giro o
        // dirección (la onda del Falso Caballero sale a 2,5; su golpe grande, a 2)
        if (_pendingSpawnCount == MaxPendingSpawns) {
            FlushSpawns();
        }

        _pendingSpawns[_pendingSpawnCount++] = new PendingSpawn {
            Object = spawned, CreatorId = creator.Enemy.Id, FsmIndex = creator.Index, StateIndex = (ushort) stateIndex,
            ActionIndex = (byte) actionIndex, Id = id, Prefab = prefabName ?? SpawnPrefab(action)?.name ?? string.Empty
        };

        // Un invocador: lo que saque (si su tirada sale) se envía como creado por la misma acción, con el prefab del
        // enemigo para que el otro PC cree ese y no otro invocador
        if (gameplay && spawned.GetComponent<EnemySpawner>() is { } spawner && spawner.enemyPrefab != null) {
            var enemyName = spawner.enemyPrefab.name;
            spawner.OnEnemySpawned += released => _active?.OnSpawned(action, released, pooled: false, enemyName);
        }
    }

    /// <summary>
    /// Envía lo creado que espera, como está ahora: posición, giro entero (el polvo y los chorros del juego son partículas
    /// giradas en X para salir hacia arriba; con solo el giro en Z salían hacia la cámara, quietos y estirados) y tamaño.
    /// </summary>
    private void FlushSpawns() {
        var count = _pendingSpawnCount;
        _pendingSpawnCount = 0;
        for (var i = 0; i < count; i++) {
            ref var pending = ref _pendingSpawns[i];
            var spawned = pending.Object;
            pending.Object = null;
            if (_session == null || !_session.CanSend) {
                continue;
            }

            // Ya se acabó (un efecto de un frame) y no es de juego: no hace falta copiarlo
            if (spawned == null || pending.Id == 0 && !spawned.activeInHierarchy) {
                continue;
            }

            var transform = spawned.transform;
            var position = transform.position;
            var angles = transform.eulerAngles;
            var scale = transform.localScale;
            var writer = _session.Begin(EnemySpawn);
            writer.WriteUShort(_sceneHash);
            writer.WriteUInt(pending.CreatorId);
            writer.WriteByte(pending.FsmIndex);
            writer.WriteUShort(pending.StateIndex);
            writer.WriteByte(pending.ActionIndex);
            writer.WriteString(pending.Prefab);
            writer.WriteUInt(pending.Id);
            writer.WriteFloat(position.x);
            writer.WriteFloat(position.y);
            writer.WriteFloat(angles.x);
            writer.WriteFloat(angles.y);
            writer.WriteFloat(angles.z);
            writer.WriteFloat(scale.x);
            writer.WriteFloat(scale.y);
            WriteSpawnVariables(writer, spawned);
            _session.End();
            if (TestMode) {
                Log.Info($"[TEST] creado {spawned.name} en ({position.x:0.0},{position.y:0.0}) giro={angles} " +
                         $"escala=({scale.x:0.00},{scale.y:0.00}) {DescribeSpawnFsms(spawned)}");
            }
        }
    }

    /// <summary>
    /// Los números de las FSM de lo creado, como se los dejó quien lo creó (la onda del Falso Caballero recibe su
    /// velocidad y su dirección con SetFsmFloat y SetFsmBool justo después de salir del pool). Lo solo visual corre solo
    /// en cada PC con su lógica: sin ellos, la copia se quedaba quieta echando chorros que dañaban en otro sitio y no se
    /// acababa nunca.
    /// </summary>
    private void WriteSpawnVariables(PacketWriter writer, GameObject spawned) {
        spawned.GetComponents(_spawnFsms);
        var fsmCount = Math.Min(_spawnFsms.Count, MaxSpawnFsms);
        writer.WriteByte((byte) fsmCount);
        for (var f = 0; f < fsmCount; f++) {
            var variables = _spawnFsms[f].FsmVariables;
            var floats = variables.FloatVariables;
            var floatCount = Math.Min(floats.Length, MaxEffectVars);
            writer.WriteByte((byte) floatCount);
            for (var i = 0; i < floatCount; i++) {
                writer.WriteFloat(floats[i].Value);
            }

            var ints = variables.IntVariables;
            var intCount = Math.Min(ints.Length, MaxEffectVars);
            writer.WriteByte((byte) intCount);
            for (var i = 0; i < intCount; i++) {
                writer.WriteInt(ints[i].Value);
            }

            var bools = variables.BoolVariables;
            var boolCount = Math.Min(bools.Length, MaxEffectVars);
            writer.WriteByte((byte) boolCount);
            for (var i = 0; i < boolCount; i++) {
                writer.WriteBool(bools[i].Value);
            }
        }

        _spawnFsms.Clear();
    }

    /// <summary>Lo mismo en la copia, antes de que su lógica empiece (empieza en el frame siguiente, como en el original).</summary>
    private void ReadSpawnVariables(PacketReader reader, GameObject copy) {
        copy.GetComponents(_spawnFsms);
        var fsmCount = reader.ReadByte();
        for (var f = 0; f < fsmCount; f++) {
            var variables = f < _spawnFsms.Count ? _spawnFsms[f].FsmVariables : null;
            var floatCount = reader.ReadByte();
            for (var i = 0; i < floatCount; i++) {
                var value = reader.ReadFloat();
                if (variables != null && i < variables.FloatVariables.Length) {
                    variables.FloatVariables[i].Value = value;
                }
            }

            var intCount = reader.ReadByte();
            for (var i = 0; i < intCount; i++) {
                var value = reader.ReadInt();
                if (variables != null && i < variables.IntVariables.Length) {
                    variables.IntVariables[i].Value = value;
                }
            }

            var boolCount = reader.ReadByte();
            for (var i = 0; i < boolCount; i++) {
                var value = reader.ReadBool();
                if (variables != null && i < variables.BoolVariables.Length) {
                    variables.BoolVariables[i].Value = value;
                }
            }
        }

        _spawnFsms.Clear();
    }

    /// <summary>Estado y algunos números de las FSM de lo creado (modo de prueba).</summary>
    private string DescribeSpawnFsms(GameObject spawned) {
        var text = string.Empty;
        foreach (var fsm in spawned.GetComponents<PlayMakerFSM>()) {
            var speed = fsm.FsmVariables.FindFsmFloat("Speed");
            var bools = string.Concat(System.Linq.Enumerable.Select(fsm.FsmVariables.BoolVariables, b => $" {b.Name}={b.Value}"));
            text += $"[{fsm.FsmName}:{fsm.ActiveStateName}{(speed != null ? $" Speed={speed.Value:0.##}" : string.Empty)}{bools}]";
        }

        return text;
    }

    /// <summary>Si lo creado cuenta para el juego: hace daño al tocarlo o se le puede golpear.</summary>
    /// <summary>
    /// Lo que crea un enemigo y cuenta para el juego: tiene vida, daña, o es un invocador de enemigos (`EnemySpawner`, el
    /// `Buzzer Summon` del Rey Vengamosca). El invocador lo creaba cada PC como efecto de la marioneta, cada uno tiraba su
    /// 75 % y sacaba sus propias mosquitas, que no se compartían.
    /// </summary>
    private static bool IsGameplay(GameObject spawned) =>
        spawned.GetComponent<HealthManager>() != null || spawned.GetComponentInChildren<DamageHero>(true) != null ||
        spawned.GetComponent<EnemySpawner>() != null;

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
            Damage = root.GetComponent<DamageHero>(),
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
            // Sin vida propia, un aguijonazo no pasa por HealthManager.Hit: su caja avisa aquí (el escupitajo del Gulka)
            if (health == null && root.GetComponent<Collider2D>() != null && root.GetComponent<NailCatcher>() == null) {
                root.AddComponent<NailCatcher>();
            }
        }

        return enemy;
    }

    /// <summary>
    /// Un aguijonazo de este jugador tocó una copia sin vida propia (lo que crean los enemigos): si su lógica, parada
    /// aquí, esperaba ese golpe (devolver el escupitajo del Gulka), el aviso va al PC que la mueve. Antes no llegaba.
    /// </summary>
    internal static void OnNailTouch(GameObject root) {
        var sync = _active;
        if (sync?._session == null) {
            return;
        }

        foreach (var enemy in sync._enemies) {
            if (enemy.Root == root) {
                if (TestMode) {
                    Log.Info($"[TEST] aguijonazo a {root.name} (marioneta={enemy.Puppet})");
                }

                if (enemy.Puppet) {
                    sync.ForwardNailTriggers(enemy, root);
                }

                return;
            }
        }
    }

    private void OnSpawn(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var creatorId = reader.ReadUInt();
        var fsmIndex = reader.ReadByte();
        var stateIndex = reader.ReadUShort();
        var actionIndex = reader.ReadByte();
        var prefabName = reader.ReadName(128);
        var id = reader.ReadUInt();
        var position = new Vector3(reader.ReadFloat(), reader.ReadFloat(), 0f);
        var rotation = Quaternion.Euler(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());
        var scaleX = reader.ReadFloat();
        var scaleY = reader.ReadFloat();
        // Lo solo visual se crea aunque este PC ya mueva al creador: el aviso pudo llegar justo después de tomarlo (con
        // retraso, la nube de la muerte del Falso Caballero no salía) y su lógica, que siguió desde más adelante, ya no
        // lo crea. Lo de juego no: este PC ya mueve su lógica y lo que cree será el original
        // (un creador ya destruido aquí, p. ej. al morir, sigue apuntado: sin él no hay a qué altura crearlo)
        if (sceneHash != _sceneHash || !_byId.TryGetValue(creatorId, out var creator) || creator.Root == null ||
            !creator.Puppet && id != 0 || creator.Fsms == null || fsmIndex >= creator.Fsms.Length ||
            creator.Fsms[fsmIndex] == null || id != 0 && (id & SpawnedIdFlag) == 0) {
            return;
        }

        var states = creator.Fsms[fsmIndex].FsmStates;
        if (stateIndex >= states.Length || actionIndex >= states[stateIndex].Actions.Length) {
            return;
        }

        // El mismo prefab que usa esa acción en la marioneta (es el mismo objeto del juego en todos los PCs)
        GameObject prefab;
        bool pooled;
        HutongGames.PlayMaker.FsmGameObject store;
        switch (states[stateIndex].Actions[actionIndex]) {
            case HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool spawn:
                prefab = spawn.gameObject.Value;
                pooled = true;
                store = spawn.storeObject;
                break;
            case HutongGames.PlayMaker.Actions.CreateObject create:
                prefab = create.gameObject.Value;
                pooled = false;
                store = create.storeObject;
                break;
            case HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPool fling:
                prefab = fling.gameObject.Value;
                pooled = true;
                store = null;
                break;
            case HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPoolVel flingVel:
                prefab = flingVel.gameObject.Value;
                pooled = true;
                store = null;
                break;
            case HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPoolTime flingTime:
                prefab = flingTime.gameObject.Value;
                pooled = true;
                store = null;
                break;
            case HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPoolOverTime overTime:
                prefab = overTime.gameObject.Value;
                pooled = true;
                store = null;
                break;
            case HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPoolOverTimeV2 overTimeV2:
                prefab = overTimeV2.gameObject.Value;
                pooled = true;
                store = null;
                break;
            default:
                return;
        }

        // El prefab puede venir de una variable que ponen estados anteriores (el Elder Baldur elige en "Goop" o "Roller"
        // qué escupe y "Fire" saca del pool {Projectile}): en la marioneta esos estados no pasan, la variable seguía
        // vacía y no se creaba nada (el otro no veía sus disparos ni sus crías). Se busca por su nombre
        if (prefabName.Length > 0 && (prefab == null || prefab.name != prefabName)) {
            prefab = FindKnownObject(creator, prefabName) ?? prefab;
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
            copy = pooled ? prefab.Spawn(position, rotation) : UnityEngine.Object.Instantiate(prefab, position, rotation);
        } finally {
            _spawningCopy = false;
        }

        if (copy == null) {
            return;
        }

        // La lógica de la marioneta lo apunta donde lo apunta la del original: sus efectos siguientes actúan sobre la
        // copia (p. ej. el Coleccionista mete el bicho en el tarro que acaba de lanzar)
        // (solo en la marioneta: si este PC ya mueve al creador, su variable es de su propia lógica)
        if (store != null && !store.IsNone && creator.Puppet) {
            store.Value = copy;
        }

        // El tamaño del original: el pool reutiliza objetos con el tamaño que les puso su último uso, y quien lo crea a
        // veces lo cambia después (la onda del Falso Caballero, a 2,5)
        copy.transform.localScale = new Vector3(scaleX, scaleY, copy.transform.localScale.z);
        ReadSpawnVariables(reader, copy);
        if (TestMode) {
            Log.Info($"[TEST] copia de {prefab.name} de {creator.Root.name} en ({position.x:0.0},{position.y:0.0}) " +
                     $"giro={copy.transform.eulerAngles} escala=({scaleX:0.00},{scaleY:0.00}) {DescribeSpawnFsms(copy)}");
        }

        // La copia de un tarro del Coleccionista va vacía: su bicho lo suelta el original y llega como copia
        var jar = copy.GetComponent<SpawnJarControl>();
        if (jar != null) {
            JarEnemyField?.SetValue(jar, null);
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
        // La copia de un disparo chocó en otro PC (con su caballero o una pared): el original también se acaba
        if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var original) && original.Spawned && !original.Puppet &&
            original.Root != null && original.Root.activeInHierarchy && original.Root.GetComponent<EnemyBullet>() != null) {
            if (original.Pooled) {
                original.Root.Recycle();
            } else {
                UnityEngine.Object.Destroy(original.Root);
            }

            return;
        }

        if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var enemy) && enemy.Spawned && enemy.Puppet) {
            // Un tarro del Coleccionista se rompe solo al llegar abajo (y suelta aquí su bicho, que se sigue junto al
            // de allí); su copia va justo detrás del original, así que se deja acabar
            if (enemy.Root != null && enemy.Root.activeInHierarchy && enemy.Root.GetComponent<SpawnJarControl>() != null) {
                return;
            }

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
        if (enemy.Caged != null) {
            ReturnToCage(enemy.Caged);
        } else if (enemy.Pooled) {
            enemy.Root.Recycle();
        } else {
            UnityEngine.Object.Destroy(enemy.Root);
        }

        if (TestMode) {
            Log.Info($"[TEST] copia quitada {enemy.Root.name} {enemy.Id:x8} activa={enemy.Root.activeSelf}");
        }
    }

    /// <summary>Deja de seguir un objeto creado en combate.</summary>
    private void Forget(Enemy enemy) {
        if (TestMode && enemy.Spawned) {
            Log.Info($"[TEST] deja de seguir {(enemy.Root != null ? enemy.Root.name : "-")} {enemy.Id:x8} " +
                     $"marioneta={enemy.Puppet} activa={(enemy.Root != null && enemy.Root.activeInHierarchy)}");
        }

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

    private void ForwardEvent(Enemy enemy, byte fsmIndex, string eventName) {
        if (!_session.CanSend) {
            return;
        }

        var key = (enemy.Id, fsmIndex, eventName);
        var now = Time.unscaledTime;
        if (_lastForward.TryGetValue(key, out var last) && now - last < EventForwardInterval) {
            return;
        }

        _lastForward[key] = now;
        SendEventTo(enemy, fsmIndex, eventName, ControllerOf(enemy, _session.OwnerOf(_scene)), 0);
    }

    /// <summary>
    /// Envía el aviso al PC que mueve el enemigo (según este PC). Si es este mismo (lo acaba de recibir y aún no lo ha
    /// tomado), se guarda para cuando lo tome: un mensaje a sí mismo no llega.
    /// </summary>
    private void SendEventTo(Enemy enemy, byte fsmIndex, string eventName, ushort target, byte hops) {
        if (target == _session.LocalId) {
            KeepPendingEvent(enemy, fsmIndex, eventName);
            return;
        }

        var writer = _session.Begin(EnemyEvent);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(enemy.Id);
        writer.WriteByte(fsmIndex);
        writer.WriteString(eventName);
        writer.WriteUShort(target);
        writer.WriteByte(hops);
        _session.End();
    }

    private static void KeepPendingEvent(Enemy enemy, byte fsmIndex, string eventName) {
        enemy.PendingEvents ??= new List<(byte, string, float)>();
        // Los viejos se quitan antes de mirar el límite: un PC que nunca lo toma llenaba la lista y después se perdían
        // los nuevos aunque sí lo tomara
        var now = Time.unscaledTime;
        enemy.PendingEvents.RemoveAll(e => now - e.At > PendingEventSeconds);
        if (enemy.PendingEvents.Count < MaxPendingEvents) {
            enemy.PendingEvents.Add((fsmIndex, eventName, Time.unscaledTime));
        }
    }

    /// <summary>Avisos guardados como mucho para cuando se toma un enemigo, y cuánto valen.</summary>
    private const int MaxPendingEvents = 8;

    private const float PendingEventSeconds = 3f;

    /// <summary>Veces que un aviso se pasa al siguiente PC si el que lo recibe ya no mueve el enemigo.</summary>
    private const byte MaxEventHops = 3;

    /// <summary>
    /// Un objeto de la sala de este PC mandó un aviso a todos. La lógica de las marionetas está parada y no lo oye: si
    /// alguna lo esperaba en su estado de ahora, se reenvía al PC que mueve ese enemigo, como los avisos directos. Así
    /// la zona de abajo del Tirano de Almas (PHASE 2) empieza su segunda fase aunque quien la mueve siga arriba; antes la
    /// fase solo empezaba si caía en ella el caballero del PC que la movía.
    /// </summary>
    private void ForwardBroadcast(HutongGames.PlayMaker.Actions.SendEventByName send) {
        if (_session == null || !_session.CanSend || _replaying || _playingFx || _spawningCopy ||
            FsmStateSync.IsApplyingRemote || send.eventTarget?.target != HutongGames.PlayMaker.FsmEventTarget.EventTarget.BroadcastAll) {
            return;
        }

        // Los de un enemigo son de su lógica y los de un controlador compartido pasan en todos los PCs
        var sender = send.Fsm;
        var eventName = send.sendEvent?.Value;
        if (sender == null || string.IsNullOrEmpty(eventName) || _enemyFsms.ContainsKey(sender) ||
            FsmStateSync.IsSharedController(sender)) {
            return;
        }

        foreach (var enemy in _enemies) {
            if (!enemy.Puppet || enemy.Fsms == null || enemy.Health != null && enemy.Health.isDead) {
                continue;
            }

            for (var i = 0; i < enemy.Fsms.Length && i <= byte.MaxValue; i++) {
                var fsm = enemy.Fsms[i];
                if (fsm != null && !fsm.enabled && Handles(fsm.Fsm, eventName)) {
                    Log.Info($"{enemy.Root.name}/{fsm.FsmName}: {eventName} de {sender.GameObjectName}, al PC que lo mueve");
                    ForwardEvent(enemy, (byte) i, eventName);
                }
            }
        }
    }

    /// <summary>Si el evento cambia el estado de la FSM desde donde está (transición del estado o global).</summary>
    private static bool Handles(HutongGames.PlayMaker.Fsm fsm, string eventName) {
        foreach (var transition in fsm.GlobalTransitions) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        var state = fsm.ActiveState;
        if (state == null) {
            return false;
        }

        foreach (var transition in state.Transitions) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        return false;
    }

    private void OnEnemyEvent(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var fsmIndex = reader.ReadByte();
        var eventName = reader.ReadName(128);
        var target = reader.ReadUShort();
        var hops = reader.ReadByte();
        // Solo lo atiende el PC al que iba (si no, al cambiar quién lo mueve podrían atenderlo dos o ninguno)
        if (sceneHash != _sceneHash || target != _session.LocalId || !_byId.TryGetValue(id, out var enemy) ||
            enemy.Fsms == null || fsmIndex >= enemy.Fsms.Length || enemy.Fsms[fsmIndex] == null) {
            return;
        }

        if (!enemy.Puppet) {
            PrepareRemoteNail(enemy, enemy.Fsms[fsmIndex], eventName, from);
            enemy.Fsms[fsmIndex].SendEvent(eventName);
            return;
        }

        // Ya no lo mueve este PC (cambió justo al enviarlo): se pasa al que lo mueve ahora, o se guarda si este PC
        // está a punto de tomarlo. Antes se perdía: el BATTLE START de la arena del Campeón Fallido no llegaba a nadie
        // y el jefe se quedaba dormido para siempre
        var controller = ControllerOf(enemy, _session.OwnerOf(_scene));
        // Si ya volvió una vez al que lo envió (cada uno cree que lo mueve el otro: el cambio aún no ha llegado a los
        // dos), se guarda aquí por si este PC lo toma, en vez de ir y venir sin que nadie lo atienda
        if (controller == _session.LocalId || controller == from.Id && hops > 0) {
            KeepPendingEvent(enemy, fsmIndex, eventName);
        } else if (hops < MaxEventHops && _session.CanSend) {
            Log.Info($"{enemy.Root.name}/{enemy.Fsms[fsmIndex].FsmName}: {eventName} llegó y ya no lo mueve este PC: " +
                     $"pasa al {controller}");
            SendEventTo(enemy, fsmIndex, eventName, controller, (byte) (hops + 1));
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

            // También su cadáver aunque no tenga animación propia: tras morir se sigue un rato (ver CorpseFollowSeconds)
            var animator = child.GetComponent<tk2dSpriteAnimator>();
            if (animator == null && child.GetComponent<DamageHero>() == null &&
                !child.name.StartsWith("Corpse", StringComparison.Ordinal)) {
                continue;
            }

            parts ??= new List<Part>();
            parts.Add(new Part {
                Transform = child, Animator = animator, Collider = child.GetComponent<Collider2D>(),
                Renderer = child.GetComponent<Renderer>(), Damage = child.GetComponent<DamageHero>(),
                IsCorpse = child.name.StartsWith("Corpse", StringComparison.Ordinal)
            });
            if (parts.Count == MaxParts) {
                break;
            }
        }

        enemy.Parts = parts?.ToArray();
    }

    /// <summary>
    /// Objetos de la sala que la lógica del enemigo mueve y enseña pero que no cuelgan de él: los apunta una variable
    /// de sus FSM, y a veces los suelta al prepararse (el rayo del Guardián de Cristal, su impacto y su bola de luz).
    /// Se siguen como sus piezas, identificados por esa variable porque su sitio en la sala puede no coincidir.
    /// </summary>
    private static void CollectExternalParts(Enemy enemy) {
        // Ya destruido (lo creado en combate, o la sala que se descarga): leer su escena lanzaba NullReferenceException
        // en GameObject.get_scene. Sus FSM sueltas (piezas que su lógica sacó de él, satélites) pueden seguir vivas
        if (enemy.Fsms == null || enemy.Root == null) {
            return;
        }

        List<Part> external = null;
        var hero = HeroController.instance;
        var enemyScene = enemy.Root.scene;
        for (var f = 0; f < enemy.Fsms.Length && f < 256; f++) {
            var fsm = enemy.Fsms[f];
            if (fsm == null) {
                continue;
            }

            if (enemy.FirstSatellite > 0 && f >= enemy.FirstSatellite) {
                external ??= new List<Part>();
                if (!external.Exists(part => part.Transform == fsm.transform)) {
                    external.Add(new Part { Key = (uint) (f << 16 | 0xFFFF) + 1, Transform = fsm.transform });
                }
            }

            var variables = fsm.FsmVariables.GameObjectVariables;
            for (var v = 0; v < variables.Length && v < 0xFFFF; v++) {
                var target = variables[v].Value;
                if (target == null || !SameRoom(target.scene, enemyScene) || target.transform.IsChildOf(enemy.Transform) ||
                    IsEnemyObject(target.transform) ||
                    hero != null && target.transform.IsChildOf(hero.transform)) {
                    continue;
                }

                var animator = target.GetComponent<tk2dSpriteAnimator>();
                var renderer = target.GetComponent<Renderer>();
                var damage = target.GetComponent<DamageHero>();
                if (animator == null && damage == null && renderer is not MeshRenderer) {
                    continue;
                }

                external ??= new List<Part>();
                if (external.Exists(part => part.Transform == target.transform) ||
                    enemy.Parts != null && Array.Exists(enemy.Parts, part => part.Transform == target.transform)) {
                    continue;
                }

                external.Add(new Part {
                    Key = (uint) (f << 16 | v) + 1, Transform = target.transform, Animator = animator,
                    Collider = target.GetComponent<Collider2D>(), Renderer = renderer, Damage = damage
                });
                if (external.Count == MaxExternalParts) {
                    break;
                }
            }
        }

        if (external == null) {
            return;
        }

        if (enemy.Parts != null) {
            external.InsertRange(0, enemy.Parts);
        }

        enemy.Parts = external.ToArray();
    }

    /// <summary>
    /// Trampas de la sala que el enemigo dispara con un aviso a todos (las torretas del techo del Guardián de Cristal con
    /// LASER SHOOT): cada una apunta y espera al azar, así que solo deben funcionar en el PC que mueve al enemigo. Sus FSM
    /// pasan a ser del enemigo: en la marioneta se paran, sus efectos llegan como los suyos y su giro, rayos y
    /// animaciones se copian como piezas. Solo las que pueden hacer daño (las puertas de la arena van aparte).
    /// </summary>
    private void AddSatellites(Enemy enemy) {
        if (enemy.Fsms == null || enemy.Root == null) {
            return;
        }

        HashSet<string> events = null;
        HashSet<GameObject> direct = null;
        foreach (var fsm in enemy.Fsms) {
            if (fsm == null) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is not HutongGames.PlayMaker.Actions.SendEventByName send ||
                        string.IsNullOrEmpty(send.sendEvent?.Value)) {
                        continue;
                    }

                    switch (send.eventTarget.target) {
                        case HutongGames.PlayMaker.FsmEventTarget.EventTarget.BroadcastAll:
                            (events ??= new HashSet<string>()).Add(send.sendEvent.Value);
                            break;
                        case HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObject:
                        case HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObjectFSM:
                            // Un objeto de la sala al que avisa directamente (el que hace caer los barriles del Falso
                            // Caballero: SUMMON)
                            var receiver = fsm.Fsm.GetOwnerDefaultTarget(send.eventTarget.gameObject);
                            if (receiver != null && receiver != enemy.Root) {
                                (direct ??= new HashSet<GameObject>()).Add(receiver);
                            }

                            break;
                    }
                }
            }
        }

        if (events == null && direct == null) {
            return;
        }

        var hero = HeroController.instance;
        List<(uint Path, PlayMakerFSM Fsm)> found = null;
        var enemyScene = enemy.Root.scene;
        foreach (var fsm in PlayMakerFSM.FsmList) {
            var owner = fsm != null ? fsm.gameObject : null;
            if (owner == null || !SameRoom(owner.scene, enemyScene) || _enemyFsms.ContainsKey(fsm.Fsm) ||
                owner.transform.IsChildOf(enemy.Transform) || IsEnemyObject(owner.transform) ||
                hero != null && owner.transform.IsChildOf(hero.transform) ||
                // Las trampas que dispara con un aviso a todos; o lo que, avisado por él, crea cosas que dañan (cada PC
                // las crearía al azar en otro sitio: los barriles caían distintos en cada uno)
                // También lo que escucha un aviso a todos y crea cosas que dañan sin dañar él mismo: `Barb Region` de Hornet en
                // los Bordes del Reino (SPAWN3) crea las púas; sin esto, en el PC que la mueve no se seguían y en los demás no
                // había (el aviso de un jefe vivo no se repite)
                !(events != null && Listens(fsm, events) &&
                  (owner.GetComponentInChildren<DamageHero>(true) != null || SpawnsGameplay(fsm)) ||
                  direct != null && direct.Contains(owner) && SpawnsGameplay(fsm)) ||
                // No los controladores de combate (el del Coliseo contiene enemigos; ya los lleva un PC para todos)
                owner.GetComponentsInChildren<HealthManager>(true).Length > 0 || FsmStateSync.IsSharedController(fsm.Fsm)) {
                continue;
            }

            (found ??= new List<(uint, PlayMakerFSM)>()).Add((ScenePaths.StableHash(owner.transform), fsm));
        }

        if (found == null) {
            return;
        }

        // En el mismo orden en todos los PCs: los mensajes las identifican por su posición en la lista del enemigo
        found.Sort((a, b) => a.Path != b.Path ? a.Path.CompareTo(b.Path) : string.CompareOrdinal(a.Fsm.FsmName, b.Fsm.FsmName));
        var first = enemy.Fsms.Length;
        var count = Math.Min(found.Count, Math.Min(MaxSatellites, byte.MaxValue - first));
        if (count <= 0) {
            return;
        }

        var fsms = new PlayMakerFSM[first + count];
        Array.Copy(enemy.Fsms, fsms, first);
        for (var i = 0; i < count; i++) {
            fsms[first + i] = found[i].Fsm;
        }

        enemy.Fsms = fsms;
        enemy.FirstSatellite = first;
        for (var i = first; i < fsms.Length; i++) {
            RegisterFsm(enemy, i);
            if (enemy.Puppet && fsms[i].enabled) {
                Pause(enemy, fsms[i]);
                fsms[i].enabled = false;
                enemy.Disabled.Add(fsms[i]);
            }
        }

        Log.Info($"{enemy.Root.name}: {count} trampas de la sala van con él");
    }

    /// <summary>Si la FSM crea algo que daña o tiene vida (con el pool del juego o creándolo).</summary>
    private static bool SpawnsGameplay(PlayMakerFSM fsm) {
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                var prefab = action switch {
                    HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool spawn => spawn.gameObject?.Value,
                    HutongGames.PlayMaker.Actions.CreateObject create => create.gameObject?.Value,
                    _ => null
                };
                if (prefab != null && IsGameplay(prefab)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Si el objeto es una trampa o un invocador que va con el enemigo (su lógica solo corre en el PC que lo mueve).</summary>
    private static bool IsSatellite(Enemy enemy, GameObject target) {
        if (enemy.FirstSatellite <= 0 || enemy.SatellitesReleased || target == null) {
            return false;
        }

        for (var i = enemy.FirstSatellite; i < enemy.Fsms.Length; i++) {
            if (enemy.Fsms[i] != null && enemy.Fsms[i].gameObject == target) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Muerto el enemigo, sus trampas vuelven a ser de la sala en cada PC y lo que estaban enseñando se apaga: el que lo
    /// movía deja de enviar sus piezas, y en las marionetas se quedaría un rayo a medias.
    /// </summary>
    private static void ReleaseSatellites(Enemy enemy) {
        if (enemy.FirstSatellite <= 0 || enemy.SatellitesReleased) {
            return;
        }

        enemy.SatellitesReleased = true;
        if (!enemy.Puppet) {
            return;
        }

        for (var i = enemy.FirstSatellite; i < enemy.Fsms.Length; i++) {
            var satellite = enemy.Fsms[i];
            if (satellite == null || !enemy.Disabled.Remove(satellite)) {
                continue;
            }

            var paused = enemy.PausedFsms.FindIndex(entry => entry.Fsm == satellite);
            if (paused >= 0) {
                satellite.Fsm.RestartOnEnable = enemy.PausedFsms[paused].Restart;
                enemy.PausedFsms.RemoveAt(paused);
            }

            satellite.enabled = true;
        }

        foreach (var part in enemy.Parts ?? Array.Empty<Part>()) {
            if (part.Key != 0 && part.Renderer != null) {
                part.Renderer.enabled = false;
            }
        }
    }

    /// <summary>
    /// Si las dos escenas son de la misma sala: la misma, o la sala y su escena adicional (los jefes suelen estar en
    /// una "_boss" aparte, como Mines_18 y Mines_18_boss). No la sala que se está descargando al cambiar de sala.
    /// </summary>
    private static bool SameRoom(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.Scene enemyScene) {
        if (scene == enemyScene) {
            return true;
        }

        if (!scene.isLoaded || scene.buildIndex < 0) {
            return false;
        }

        return enemyScene.name.StartsWith(scene.name + "_", StringComparison.Ordinal) ||
               scene.name.StartsWith(enemyScene.name + "_", StringComparison.Ordinal);
    }

    /// <summary>
    /// Si el objeto es un enemigo o cuelga de uno. También desactivado: GetComponentInParent no mira los objetos
    /// inactivos (los Señores Mantis están apagados hasta que empieza su combate).
    /// </summary>
    internal static bool IsEnemyObject(Transform transform) {
        for (var t = transform; t != null; t = t.parent) {
            if (t.GetComponent<HealthManager>() != null) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Si la FSM cambia de estado con alguno de esos avisos.</summary>
    private static bool Listens(PlayMakerFSM fsm, HashSet<string> events) {
        foreach (var transition in fsm.FsmGlobalTransitions) {
            if (events.Contains(transition.EventName)) {
                return true;
            }
        }

        foreach (var state in fsm.FsmStates) {
            foreach (var transition in state.Transitions) {
                if (events.Contains(transition.EventName)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Estado de las piezas de los enemigos que mueve este PC (solo las que cambiaron, o todas de vez en cuando).</summary>
    private void SendParts(uint tick) {
        PacketWriter writer = null;
        var enemyCountPosition = 0;
        var enemyCount = 0;
        var keepAlive = tick - _lastPartsKeepAlive >= KeepAliveTicks;
        // De vez en cuando, también todos los objetos de la arena (por si se perdió algún cambio)
        if (tick - _lastLinkedRefresh >= LinkedRefreshTicks) {
            _lastLinkedRefresh = tick;
            _linkedRefreshDue = true;
        }

        if (keepAlive) {
            _lastPartsKeepAlive = tick;
        }

        foreach (var enemy in _enemies) {
            // Muerto, solo su cadáver y solo un rato: sale despedido con física, y algunos explotan (el Gruzzer
            // volátil); así cae y explota en el mismo sitio en todos los PCs
            var corpseOnly = enemy.ReportedDead;
            if (corpseOnly && enemy.CorpseFollows) {
                // Murió en otro PC: su cadáver lo envía aquel
                continue;
            }

            // (muerto, el juego suele destruir el enemigo; su cadáver ya se soltó y sigue)
            if (enemy.Parts == null || enemy.Puppet ||
                (corpseOnly
                    ? Time.unscaledTime - enemy.DiedAt > CorpseFollowSeconds
                    : enemy.Root == null || !enemy.Root.activeInHierarchy)) {
                continue;
            }

            var partCountPosition = -1;
            var partCount = 0;
            for (var i = 0; i < enemy.Parts.Length; i++) {
                var part = enemy.Parts[i];
                if (part.Transform == null || corpseOnly && !part.IsCorpse) {
                    continue;
                }

                var transform = part.Transform;
                var local = transform.localPosition;
                // Si está activa, si su caja de daño está encendida y si se dibuja (lo decide su lógica, parada en la copia;
                // p. ej. el rayo del Guardián de Cristal: se coloca, se estira hasta donde llega y se enciende al disparar)
                var flags = (byte) ((transform.gameObject.activeSelf ? 1 : 0) |
                                    (part.Collider != null && part.Collider.enabled ? 4 : 0) |
                                    (part.Renderer != null && part.Renderer.enabled ? 8 : 0) |
                                    (transform.parent == null ? 16 : 0));
                var scale = transform.localScale;
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

                var damage = part.Damage != null ? (byte) Mathf.Clamp(part.Damage.damageDealt, 0, 255) : (byte) 0;
                if (!keepAlive && !enemy.SendFull && flags == part.Flags && local.x == part.X && local.y == part.Y && local.z == part.Z &&
                    scale.x == part.ScaleX && scale.y == part.ScaleY &&
                    rotation == part.Rotation &&
                    clip == part.Clip && frame == part.Frame && damage == part.DamageSent) {
                    continue;
                }

                part.DamageSent = damage;

                part.Flags = flags;
                part.X = local.x;
                part.Y = local.y;
                part.Z = local.z;
                part.ScaleX = scale.x;
                part.ScaleY = scale.y;
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

                if (part.Key != 0 || i >= MaxParts) {
                    writer.WriteByte(ExternalPartIndex);
                    writer.WriteUInt(part.Key);
                } else {
                    writer.WriteByte((byte) i);
                }

                writer.WriteByte(flags);
                writer.WriteFloat(local.x);
                writer.WriteFloat(local.y);
                writer.WriteFloat(local.z);
                writer.WriteFloat(scale.x);
                writer.WriteFloat(scale.y);
                writer.WriteUShort(rotation);
                writer.WriteUShort(clip);
                writer.WriteByte(frame);
                writer.WriteByte(damage);
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
            var alive = enemy != null && enemy.Puppet && enemy.Health != null && !enemy.Health.isDead;
            var corpse = enemy != null && enemy.CorpseFollows && Time.unscaledTime - enemy.DiedAt <= CorpseFollowSeconds;
            var apply = sceneHash == _sceneHash && enemy?.Parts != null && (alive || corpse);
            for (var p = 0; p < partCount; p++) {
                int index = reader.ReadByte();
                var key = index == ExternalPartIndex ? reader.ReadUInt() : 0u;
                var flags = reader.ReadByte();
                var x = reader.ReadFloat();
                var y = reader.ReadFloat();
                var z = reader.ReadFloat();
                var scaleX = reader.ReadFloat();
                var scaleY = reader.ReadFloat();
                var rotation = reader.ReadUShort();
                var clip = reader.ReadUShort();
                var frame = reader.ReadByte();
                var damage = reader.ReadByte();
                if (!apply) {
                    continue;
                }

                if (index == ExternalPartIndex) {
                    index = Array.FindIndex(enemy.Parts, part => part.Key == key && part.Transform != null);
                }

                if (index < 0 || index >= enemy.Parts.Length || enemy.Parts[index].Transform == null ||
                    !alive && !enemy.Parts[index].IsCorpse) {
                    continue;
                }

                // El cadáver lo mueve la física de allí: aquí se le quita el impulso que le dio la de este PC
                if (!alive) {
                    var body = enemy.Parts[index].Transform.GetComponent<Rigidbody2D>();
                    if (body != null) {
                        body.velocity = Vector2.zero;
                    }
                }

                ApplyPart(enemy.Parts[index], flags, x, y, z, scaleX, scaleY, rotation, clip, frame);
                if (enemy.Parts[index].Damage != null) {
                    enemy.Parts[index].Damage.damageDealt = damage;
                }
            }
        }
    }

    private static void ApplyPart(Part part, byte flags, float x, float y, float z, float scaleX, float scaleY, ushort rotation,
        ushort clip, byte frame) {
        var transform = part.Transform;
        var active = (flags & 1) != 0;
        if (part.Collider != null) {
            part.Collider.enabled = (flags & 4) != 0;
        }

        if (transform.gameObject.activeSelf != active) {
            transform.gameObject.SetActive(active);
        }

        // Hay piezas que su lógica suelta del enemigo al prepararse (el impacto y la bola del rayo del Guardián de
        // Cristal): entonces su posición es la de la sala, y aquí puede que la copia no llegara a soltarlas
        if ((flags & 16) != 0 && transform.parent != null) {
            transform.SetParent(null, true);
        }

        transform.localPosition = new Vector3(x, y, z);
        var angle = rotation / 100f;
        if (Mathf.Abs(Mathf.DeltaAngle(transform.localEulerAngles.z, angle)) > 0.5f) {
            transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        var scale = transform.localScale;
        if (scale.x != scaleX || scale.y != scaleY) {
            transform.localScale = new Vector3(scaleX, scaleY, scale.z);
        }

        if (part.Renderer != null) {
            part.Renderer.enabled = (flags & 8) != 0;
        }

        var animator = part.Animator;
        if (animator != null && clip != ushort.MaxValue && animator.Library != null && clip < animator.Library.clips.Length) {
            var animation = animator.Library.clips[clip];
            if (animation != null && (animator.CurrentClip != animation || Math.Abs(animator.CurrentFrame - frame) > 1)) {
                animator.PlayFromFrame(animation, Mathf.Min(frame, Math.Max(0, animation.frames.Length - 1)));
            }
        }
    }

    // =====================================================================================================
    // Objetos de la arena que la lógica del enemigo enciende y apaga
    // =====================================================================================================

    /// <summary>
    /// Objetos que las FSM del enemigo activan o desactivan (cuerpo tumbado, destellos, piezas de la arena...), sin contar
    /// otros enemigos ni lo que no es de la sala. Se buscan cuando su lógica ya se preparó (sus variables apuntan a ellos)
    /// y se identifican por su ruta en la sala, igual en todos los PCs.
    /// </summary>
    private static void ResolveLinked(Enemy enemy) {
        enemy.LinkedResolved = true;
        if (enemy.Fsms == null || enemy.Root == null) {
            return;
        }

        List<Linked> linked = null;
        var hero = HeroController.instance;
        foreach (var fsm in enemy.Fsms) {
            if (fsm == null) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is not HutongGames.PlayMaker.Actions.ActivateGameObject activate) {
                        continue;
                    }

                    var target = fsm.Fsm.GetOwnerDefaultTarget(activate.gameObject);
                    if (target == null || target == enemy.Root || target.scene != enemy.Root.scene ||
                        target.GetComponent<HealthManager>() != null ||
                        hero != null && target.transform.IsChildOf(hero.transform)) {
                        continue;
                    }

                    linked ??= new List<Linked>();
                    if (linked.Exists(l => l.Object == target)) {
                        continue;
                    }

                    linked.Add(new Linked { Object = target, Path = ScenePaths.StableHash(target.transform), Active = target.activeSelf });
                    if (linked.Count == MaxLinked) {
                        break;
                    }
                }
            }
        }

        enemy.Linked = linked?.ToArray();
        if (enemy.PendingLinked == null || enemy.Linked == null) {
            return;
        }

        foreach (var link in enemy.Linked) {
            if (enemy.PendingLinked.TryGetValue(link.Path, out var active) && link.Object.activeSelf != active) {
                link.Object.SetActive(active);
            }
        }

        enemy.PendingLinked = null;
    }

    /// <summary>Los que cambiaron (o todos, para quien acaba de entrar) de los enemigos que mueve este PC.</summary>
    private void SendLinked(bool all) {
        foreach (var enemy in _enemies) {
            if (enemy.Linked == null || enemy.Puppet || enemy.Root == null) {
                continue;
            }

            var full = all || enemy.SendFull;

            PacketWriter writer = null;
            var countPosition = 0;
            var count = 0;
            foreach (var link in enemy.Linked) {
                if (link.Object == null) {
                    continue;
                }

                var active = link.Object.activeSelf;
                if (!full && active == link.Active) {
                    continue;
                }

                link.Active = active;
                if (writer == null) {
                    writer = _session.Begin(EnemyLinked);
                    writer.WriteUShort(_sceneHash);
                    writer.WriteUInt(enemy.Id);
                    countPosition = writer.Position;
                    writer.WriteByte(0);
                }

                writer.WriteUInt(link.Path);
                writer.WriteBool(active);
                count++;
            }

            if (writer != null) {
                PatchByte(writer, countPosition, count);
                _session.End();
            }
        }
    }

    private void OnLinked(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var count = reader.ReadByte();
        _byId.TryGetValue(id, out var enemy);
        var apply = sceneHash == _sceneHash && enemy != null && enemy.Puppet;
        for (var i = 0; i < count; i++) {
            var path = reader.ReadUInt();
            var active = reader.ReadBool();
            if (!apply) {
                continue;
            }

            if (!enemy.LinkedResolved) {
                enemy.PendingLinked ??= new Dictionary<uint, bool>();
                enemy.PendingLinked[path] = active;
                continue;
            }

            if (enemy.Linked == null) {
                continue;
            }

            foreach (var link in enemy.Linked) {
                if (link.Path == path && link.Object != null) {
                    link.Active = active;
                    if (link.Object.activeSelf != active) {
                        link.Object.SetActive(active);
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// Mientras se apagan o encienden las FSM de un enemigo al pasar a marioneta (o volver). PlayMaker les manda solo
    /// <c>DISABLE</c> al apagarlas: no es un aviso de fuera y no se reenvía. Antes llegaba al PC que acababa de tomar el
    /// enemigo y su lógica viva lo atendía: el Falso Caballero se quedaba quieto tras cada cambio de quién lo movía.
    /// </summary>
    private static bool _settingPuppet;

    /// <summary>Aviso que PlayMaker manda a una FSM al apagarse su componente.</summary>
    private const string PlayMakerDisableEvent = "DISABLE";

    /// <summary>Convierte un enemigo en marioneta (o lo devuelve a la normalidad).</summary>
    private static void SetPuppet(Enemy enemy, bool puppet) {
        if (enemy.Puppet == puppet || enemy.Root == null) {
            return;
        }

        _settingPuppet = true;
        try {
            SetPuppetUnguarded(enemy, puppet);
        } finally {
            _settingPuppet = false;
        }
    }

    private static void SetPuppetUnguarded(Enemy enemy, bool puppet) {

        enemy.Puppet = puppet;
        if (puppet) {
            enemy.Disabled.Clear();
            enemy.PausedFsms.Clear();
            foreach (var behaviour in enemy.Root.GetComponents<Behaviour>()) {
                if (behaviour.enabled && behaviour is MonoBehaviour && !Keep(behaviour)) {
                    Pause(enemy, behaviour as PlayMakerFSM);
                    behaviour.enabled = false;
                    enemy.Disabled.Add(behaviour);
                    StopWalker(behaviour);
                }
            }

            foreach (var fsm in enemy.Root.GetComponentsInChildren<PlayMakerFSM>(true)) {
                if (fsm.enabled && fsm.gameObject != enemy.Root && !IsVisualOnly(fsm)) {
                    Pause(enemy, fsm);
                    fsm.enabled = false;
                    enemy.Disabled.Add(fsm);
                }
            }

            for (var i = enemy.FirstSatellite; enemy.FirstSatellite > 0 && i < enemy.Fsms.Length; i++) {
                var satellite = enemy.Fsms[i];
                if (satellite != null && satellite.enabled) {
                    Pause(enemy, satellite);
                    satellite.enabled = false;
                    enemy.Disabled.Add(satellite);
                }
            }

            StopFsmWalks(enemy);
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

            // (al retomarlo, después de poner su velocidad: ver TakeOverUnguarded)
            if (!_takingOver) {
                RestartWalkers(enemy);
            }

            RestartFsmWalks(enemy);

            if (enemy.Health != null) {
                enemy.LastHp = enemy.Health.hp;
                enemy.ReportedDead = enemy.Health.isDead;
            }
        }
    }

    // ---- Walker ----

    /// <summary>
    /// Lo que guarda el componente `Walker` (lo usan los enemigos que andan con las acciones StartWalker, StopWalker y
    /// SetWalkerFacing: centinelas de la Ciudad, Husk...). Va en el traspaso: apagado en la marioneta, al retomarlo seguía
    /// con lo que tenía antes de serlo (andaba de espaldas, giraba de golpe o andaba cuando su FSM lo había parado), y su
    /// `Update` pone la velocidad según `currentFacing` cada frame.
    /// </summary>
    private struct WalkerData {
        public byte State;
        public byte StopReason;
        public sbyte Facing;
        public sbyte TurningFacing;
        public float WalkTime;
        public float PauseTime;
    }

    private static readonly System.Reflection.FieldInfo WalkerStateField = WalkerField("state");
    private static readonly System.Reflection.FieldInfo WalkerStopField = WalkerField("stopReason");
    private static readonly System.Reflection.FieldInfo WalkerFacingField = WalkerField("currentFacing");
    private static readonly System.Reflection.FieldInfo WalkerTurningField = WalkerField("turningFacing");
    private static readonly System.Reflection.FieldInfo WalkerWalkTimeField = WalkerField("walkTimeRemaining");
    private static readonly System.Reflection.FieldInfo WalkerPauseTimeField = WalkerField("pauseTimeRemaining");

    private static System.Reflection.FieldInfo WalkerField(string name) =>
        typeof(Walker).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void WriteWalker(PacketWriter writer, Enemy enemy) {
        var walker = enemy.Root != null ? enemy.Root.GetComponent<Walker>() : null;
        var known = walker != null && WalkerStateField != null && WalkerStopField != null && WalkerFacingField != null &&
                    WalkerTurningField != null && WalkerWalkTimeField != null && WalkerPauseTimeField != null;
        writer.WriteBool(known);
        if (!known) {
            return;
        }

        writer.WriteByte((byte) Convert.ToInt32(WalkerStateField.GetValue(walker)));
        writer.WriteByte((byte) Convert.ToInt32(WalkerStopField.GetValue(walker)));
        writer.WriteByte((byte) (sbyte) Mathf.Clamp((int) WalkerFacingField.GetValue(walker), -1, 1));
        writer.WriteByte((byte) (sbyte) Mathf.Clamp((int) WalkerTurningField.GetValue(walker), -1, 1));
        writer.WriteFloat((float) WalkerWalkTimeField.GetValue(walker));
        writer.WriteFloat((float) WalkerPauseTimeField.GetValue(walker));
    }

    private static WalkerData? ReadWalker(PacketReader reader) {
        if (!reader.ReadBool()) {
            return null;
        }

        return new WalkerData {
            State = reader.ReadByte(), StopReason = reader.ReadByte(), Facing = (sbyte) reader.ReadByte(),
            TurningFacing = (sbyte) reader.ReadByte(), WalkTime = reader.ReadFloat(), PauseTime = reader.ReadFloat()
        };
    }

    private static void ApplyWalker(Enemy enemy, WalkerData data) {
        var walker = enemy.Root != null ? enemy.Root.GetComponent<Walker>() : null;
        if (walker == null || WalkerStateField == null || data.State > 4 || data.StopReason > 1) {
            return;
        }

        try {
            WalkerStateField.SetValue(walker, Enum.ToObject(WalkerStateField.FieldType, (int) data.State));
            WalkerStopField.SetValue(walker, Enum.ToObject(WalkerStopField.FieldType, (int) data.StopReason));
            WalkerFacingField.SetValue(walker, (int) data.Facing);
            WalkerTurningField.SetValue(walker, (int) data.TurningFacing);
            WalkerWalkTimeField.SetValue(walker, data.WalkTime);
            WalkerPauseTimeField.SetValue(walker, data.PauseTime);
        } catch (Exception e) {
            Log.Warn($"{enemy.Root.name}: no se pudo poner su Walker como iba ({e.Message})");
        }
    }

    /// <summary>
    /// Los que andan por el suelo, las paredes y el techo con su propio código (`Climber`, los Tiktik; `Crawler`) lo
    /// hacen en una corrutina que empieza en su `Start`, y apagar el componente no la para: en la marioneta seguía
    /// girándola, volteándola y moviéndola, y el `Climber` la devuelve a donde estaba si se mueve más de 0,1 en un frame
    /// (todo lo que llega del otro PC). Así iban a saltos o quietos. Se para al hacerse marioneta.
    /// </summary>
    private static void StopWalker(Behaviour behaviour) {
        if (behaviour is Climber or Crawler && WalkerStarted(behaviour)) {
            ((MonoBehaviour) behaviour).StopAllCoroutines();
        }
    }

    /// <summary>
    /// Al volver a moverlo este PC, su `Start` otra vez (elige la dirección por su giro y su escala de ahora, que son las
    /// del PC que lo movía, y vuelve a echar a andar). Antes se quitan sus avisos del retroceso, que `Start` vuelve a
    /// apuntar. Uno que aún no hizo su `Start` (se apagó antes de empezar) lo hará solo al encenderse.
    /// </summary>
    private static void RestartWalkers(Enemy enemy) {
        foreach (var behaviour in enemy.Root.GetComponents<MonoBehaviour>()) {
            if (behaviour is not (Climber or Crawler) || !behaviour.enabled || !WalkerStarted(behaviour)) {
                continue;
            }

            behaviour.StopAllCoroutines();
            var recoil = behaviour.GetComponent<Recoil>();
            if (recoil != null) {
                RemoveHandlersOf(recoil, RecoilFreezeField, behaviour);
                RemoveHandlersOf(recoil, RecoilCancelField, behaviour);
            }

            try {
                (behaviour is Climber ? ClimberStart : CrawlerStart)?.Invoke(behaviour, null);
            } catch (Exception e) {
                Log.Warn($"{enemy.Root.name}: no se pudo volver a echar a andar ({e.InnerException?.Message ?? e.Message})");
            }
        }
    }

    /// <summary>
    /// Lo mismo con la acción `WalkLeftRight` (los Reptantes de Cruces): su corrutina corre en el componente de la FSM y
    /// sigue con la FSM apagada, poniendo su velocidad y dándole la vuelta. Se paran las de las FSM que están en un
    /// estado con esa acción (es la única acción del juego que mueve con una corrutina).
    /// </summary>
    private static void StopFsmWalks(Enemy enemy) {
        enemy.StoppedWalks.Clear();
        foreach (var (fsm, _) in enemy.PausedFsms) {
            if (fsm != null && HasWalk(fsm.Fsm.ActiveState)) {
                fsm.StopAllCoroutines();
                enemy.StoppedWalks.Add(fsm);
            }
        }
    }

    /// <summary>Al retomarlo, su acción de andar empieza otra vez en su estado (sigue hacia donde mira).</summary>
    private static void RestartFsmWalks(Enemy enemy) {
        foreach (var fsm in enemy.StoppedWalks) {
            if (fsm == null || !fsm.enabled || fsm.Fsm.ActiveState == null) {
                continue;
            }

            foreach (var action in fsm.Fsm.ActiveState.Actions) {
                if (action is HutongGames.PlayMaker.Actions.WalkLeftRight walk && action.Enabled) {
                    walk.OnExit();
                    walk.OnEnter();
                }
            }
        }

        enemy.StoppedWalks.Clear();
    }

    private static bool HasWalk(HutongGames.PlayMaker.FsmState state) {
        if (state == null) {
            return false;
        }

        foreach (var action in state.Actions) {
            if (action is HutongGames.PlayMaker.Actions.WalkLeftRight && action.Enabled) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Si ya pasó por su `Start` (lo que este fija y antes vale cero).</summary>
    private static bool WalkerStarted(Behaviour behaviour) {
        var field = behaviour is Climber ? ClimberPreviousPos : CrawlerVelocity;
        return field != null && field.GetValue(behaviour) is Vector2 value && value != Vector2.zero;
    }

    private static void RemoveHandlersOf(Recoil recoil, System.Reflection.FieldInfo field, object target) {
        if (field?.GetValue(recoil) is not Delegate handlers) {
            return;
        }

        foreach (var handler in handlers.GetInvocationList()) {
            if (handler.Target == target) {
                handlers = Delegate.Remove(handlers, handler);
            }
        }

        field.SetValue(recoil, handlers);
    }

    private const System.Reflection.BindingFlags AnyInstance =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

    private static readonly System.Reflection.MethodInfo ClimberStart = typeof(Climber).GetMethod("Start", AnyInstance);
    private static readonly System.Reflection.MethodInfo CrawlerStart = typeof(Crawler).GetMethod("Start", AnyInstance);
    private static readonly System.Reflection.FieldInfo ClimberPreviousPos = typeof(Climber).GetField("previousPos", AnyInstance);
    private static readonly System.Reflection.FieldInfo CrawlerVelocity = typeof(Crawler).GetField("velocity", AnyInstance);
    private static readonly System.Reflection.FieldInfo RecoilFreezeField = typeof(Recoil).GetField("OnHandleFreeze", AnyInstance);
    private static readonly System.Reflection.FieldInfo RecoilCancelField = typeof(Recoil).GetField("OnCancelRecoil", AnyInstance);

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

    /// <summary>
    /// Acciones (por el principio de su tipo) que solo cambian cómo se ve o se oye su propio objeto: esperas, animación,
    /// partículas, sonido, encenderse o apagarse, moverse o girarse, y sus cuentas y comparaciones.
    /// </summary>
    private static readonly string[] VisualActionPrefixes = {
        "Wait", "Tk2d", "SetMeshRenderer", "SetSpriteRenderer", "SetParticle", "PlayParticle", "StopParticle", "Audio",
        "GetOwner", "BoolTest", "BoolFlip", "NextFrameEvent", "SetPosition", "GetPosition", "SetRotation", "GetRotation",
        "SetScale", "GetScale", "SetParent", "DestroySelf", "iTween", "Fade", "SetColor", "SetFloatValue", "FloatAdd",
        "FloatSubtract", "FloatMultiply", "FloatCompare", "FloatOperator", "IntCompare", "SetIntValue", "IntAdd",
        "SetBoolValue", "RandomFloat", "RandomInt", "SetVector", "Vector", "SetZ", "SendRandomEvent", "FinishFSM"
    };

    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, bool> VisualOnlyCache = new();

    /// <summary>
    /// FSM de un hijo que solo anima su propio objeto (el efecto de escupir del Gulka o del Elder Baldur: aparece, se
    /// anima y se apaga). En la marioneta sigue funcionando: si se paraba con las demás, el efecto que encendía la
    /// marioneta no se apagaba nunca y se quedaba pegado. Las que deciden algo (zonas de alcance, avisos a otros objetos,
    /// colisiones, daño, crear cosas) se paran.
    /// </summary>
    private static bool IsVisualOnly(PlayMakerFSM fsm) {
        if (VisualOnlyCache.TryGetValue(fsm.Fsm, out var cached)) {
            return cached;
        }

        var visual = fsm.GetComponent<HealthManager>() == null && fsm.GetComponent<DamageHero>() == null &&
                     fsm.GetComponent<Collider2D>() == null;
        foreach (var state in visual ? fsm.FsmStates : Array.Empty<HutongGames.PlayMaker.FsmState>()) {
            foreach (var action in state.Actions) {
                if (!IsVisualAction(fsm, action)) {
                    visual = false;
                    break;
                }
            }

            if (!visual) {
                break;
            }
        }

        if (VisualOnlyCache.Count > 512) {
            VisualOnlyCache.Clear();
        }

        VisualOnlyCache[fsm.Fsm] = visual;
        return visual;
    }

    private static bool IsVisualAction(PlayMakerFSM fsm, HutongGames.PlayMaker.FsmStateAction action) {
        switch (action) {
            case null:
                return true;
            // Encender o apagar: solo a sí mismo o a algo suyo
            case HutongGames.PlayMaker.Actions.ActivateGameObject activate:
                var target = fsm.Fsm.GetOwnerDefaultTarget(activate.gameObject);
                return target == null || target.transform.IsChildOf(fsm.transform);
            case HutongGames.PlayMaker.Actions.SendEvent send:
                return send.eventTarget == null ||
                       send.eventTarget.target == HutongGames.PlayMaker.FsmEventTarget.EventTarget.Self;
        }

        var name = action.GetType().Name;
        foreach (var prefix in VisualActionPrefixes) {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
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
            _incoming.Z = reader.ReadFloat();
            var flags = reader.ReadByte();
            _incoming.FacingRight = (flags & 1) != 0;
            _incoming.Visible = (flags & 2) != 0;
            _incoming.SpriteId = reader.ReadUShort();
            _incoming.Collection = reader.ReadByte();
            _incoming.Rotation = reader.ReadUShort();
            _incoming.Extra = reader.ReadByte();
            _incoming.Damage = reader.ReadByte();
            // Solo del PC que lo mueve: quien acaba de entrar en la sala envía en sus primeros 0,5 s el estado de todos sus
            // enemigos (aún no son marionetas allí), recién cargados, y un tercer jugador los veía saltar al principio
            if (sceneHash == _sceneHash && _byId.TryGetValue(id, out var enemy) && enemy.Puppet &&
                ControllerOf(enemy, _session.OwnerOf(_scene)) == from.Id) {
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
                // La vida de uno que mueve este PC es la suya. Uno recién encontrado aún no es marioneta (lo será en
                // PuppetDelay) aunque lo mueva otro: la foto de quien llega trae su vida y no se descarta (si no, quien
                // entraba tarde veía con la vida entera a los enemigos ya heridos)
                if (kind == HealthKind.Update && !enemy.Puppet && ControllerOf(enemy, _session.OwnerOf(_scene)) == _session.LocalId) {
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
                enemy.DiedElsewhere = true;
                SetPuppet(enemy, false);
                health.Die(null, hp is >= 0 and <= (int) AttackTypes.NailBeam ? (AttackTypes) hp : AttackTypes.Nail, true);
                enemy.ReportedDead = true;
                enemy.DiedAt = Time.unscaledTime;
                enemy.CorpseFollows = true;
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
            // Y el reparto en cuanto haya repasado la sala
            _nextControlResync = Math.Min(_nextControlResync, Time.unscaledTime + 1.5f);
        }

        // Y de cada enemigo que mueve este PC, qué objetos de la arena tiene encendidos
        if (player.Scene == _scene && _scene.Length > 0) {
            _sendLinkedSnapshot = true;
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

    private static readonly System.Reflection.FieldInfo PreventInvincibleEffectField =
        typeof(HealthManager).GetField("preventInvincibleEffect", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

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

        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var enemy) || enemy.Health == null || enemy.Health.isDead) {
            return;
        }

        hit.DamageDealt = Mathf.Clamp(hit.DamageDealt, 0, 1000);
        hit.Multiplier = Mathf.Clamp(hit.Multiplier, 0f, 10f);
        if (enemy.Puppet) {
            // Este PC lo va a tomar (ya se lo dieron y espera el traspaso): el golpe se aplica al tomarlo. Antes se perdía
            // en cada cambio de PC (el que lo soltó ya es marioneta y este aún no lo movía) aunque quien golpeó ya había
            // cobrado el alma y visto el impacto
            if (enemy.Controller == _session.LocalId) {
                enemy.PendingHits ??= new List<(HitInstance, ushort, float)>();
                if (enemy.PendingHits.Count < MaxPendingHits) {
                    enemy.PendingHits.Add((hit, from.Id, Time.unscaledTime));
                }
            }

            return;
        }

        ApplyRemoteHit(enemy, hit, from.Id);
    }

    private const int MaxPendingHits = 16;

    /// <summary>
    /// Este PC suelta a un jefe que bloquea al caballero con su rugido (`ROAR ENTER` a `Roar Lock`): si estaba a mitad, el
    /// `ROAR EXIT` lo mandará el PC que lo toma a su propio caballero y este se quedaba congelado. Se suelta aquí.
    /// </summary>
    private static void ReleaseRoarLock(Enemy enemy) {
        var hero = HeroController.instance;
        var roar = hero != null ? PlayMakerFSM.FindFsmOnGameObject(hero.gameObject, "Roar Lock") : null;
        if (roar == null || !roar.ActiveStateName.StartsWith("Lock", StringComparison.Ordinal) || !SendsRoar(enemy)) {
            return;
        }

        Log.Info($"{enemy.Root.name}: se suelta a mitad de su rugido; el caballero de este PC deja de estar bloqueado");
        roar.SendEvent("ROAR EXIT");
    }

    private static bool SendsRoar(Enemy enemy) {
        foreach (var fsm in enemy.Fsms) {
            if (fsm == null) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is HutongGames.PlayMaker.Actions.SendEventByName { sendEvent: { Value: "ROAR ENTER" } }) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Los golpes de otros que llegaron mientras este PC esperaba a tomarlo (como mucho de hace 3 s).</summary>
    private void ApplyPendingHits(Enemy enemy) {
        var pending = enemy.PendingHits;
        if (pending == null || enemy.Puppet) {
            return;
        }

        enemy.PendingHits = null;
        var now = Time.unscaledTime;
        foreach (var (hit, from, at) in pending) {
            if (now - at <= PendingEventSeconds && enemy.Health != null && !enemy.Health.isDead) {
                ApplyRemoteHit(enemy, hit, from);
            }
        }
    }

    private static readonly System.Reflection.FieldInfo EvasionField =
        typeof(HealthManager).GetField("evasionByHitRemaining", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private void ApplyRemoteHit(Enemy enemy, HitInstance hit, ushort from) {
        // La ventana sin daño de 0,2 s tras un golpe es para un solo atacante: con dos jugadores, el golpe del compañero
        // que caía en ella se perdía aquí aunque en su PC ya le había dado alma y efectos
        EvasionField?.SetValue(enemy.Health, 0f);
        hit.Source = _players.TryGetAvatarObject(from, out var avatar) ? avatar : enemy.Root;
        _applyingRemoteHit = true;
        _remoteHitter = from;
        // Un golpe de otro a un enemigo que lo bloquea (escudo de Baldur, caballero de musgo, jefe invulnerable): el
        // retroceso, la congelación y el temblor de cámara son de quien golpeó (ya los tuvo en su PC, la marioneta copia la
        // invulnerabilidad). Antes los sufría el caballero de este PC con cada golpe del compañero
        var preventedBefore = PreventInvincibleEffectField != null && (bool) PreventInvincibleEffectField.GetValue(enemy.Health);
        if (PreventInvincibleEffectField != null && !preventedBefore) {
            enemy.Health.SetPreventInvincibleEffect(true);
        }

        try {
            enemy.Health.Hit(hit);
        } finally {
            _applyingRemoteHit = false;
            _remoteHitter = 0;
            if (PreventInvincibleEffectField != null && !preventedBefore && enemy.Health != null) {
                enemy.Health.SetPreventInvincibleEffect(false);
            }
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
        _replaying = true;
        try {
            if (orig != null) {
                orig(health, hit);
            } else {
                health.Hit(hit);
            }
        } finally {
            health.hp = hp;
            _replaying = false;
        }
    }

    // =====================================================================================================
    // Golpes locales a marionetas
    // =====================================================================================================

    private static readonly System.Reflection.FieldInfo JarEnemyField = typeof(SpawnJarControl).GetField("enemyToSpawn",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    /// <summary>Distancia (en x y en y) a otro jugador a la que el Moss Charger ya no sale de la tierra.</summary>
    private const float EmergeClearance = 4f;

    /// <summary>
    /// El Moss Charger sale de la tierra a 14 del caballero de su PC (`Appear X`, en "Emerge Left/Right") y ya al salir
    /// tiene su caja de daño (en "Emerge", durante la animación de salir). En el juego nunca sale encima del caballero,
    /// pero ese punto se elige con el caballero del PC que lo mueve y podía caer justo debajo del otro jugador: le pegaba
    /// mientras aún salía. Si es así, sale por el otro lado de su caballero (girado y embistiendo hacia el otro lado,
    /// como en el otro estado), si ese lado está dentro de su zona y libre.
    /// </summary>
    /// <returns>El estado al que pasa.</returns>
    private HutongGames.PlayMaker.FsmState AvoidEmergingOnPartner(HutongGames.PlayMaker.Fsm fsm,
        HutongGames.PlayMaker.FsmState toState) {
        if (toState.Name != "Emerge" || fsm.Name != "Mossy Control" || _session == null ||
            !_enemyFsms.TryGetValue(fsm, out var owner) || owner.Enemy.Puppet || owner.Enemy.Root == null) {
            return toState;
        }

        var variables = fsm.Variables;
        var appear = variables.FindFsmFloat("Appear X");
        var heroX = variables.FindFsmFloat("Hero X");
        var startY = variables.FindFsmFloat("Start Y");
        var speed = variables.FindFsmFloat("Current Charge Speed");
        var minX = variables.FindFsmFloat("X Min");
        var maxX = variables.FindFsmFloat("X Max");
        if (appear == null || heroX == null || startY == null || speed == null || minX == null || maxX == null ||
            !NearOtherPlayer(appear.Value, startY.Value)) {
            return toState;
        }

        // (si ese lado no vale, sale como en el juego: esperar escondido lo hacía intentarlo otra vez cada frame)
        var mirrored = 2f * heroX.Value - appear.Value;
        if (mirrored < minX.Value || mirrored > maxX.Value || NearOtherPlayer(mirrored, startY.Value)) {
            return toState;
        }

        appear.Value = mirrored;
        speed.Value = -speed.Value;
        var transform = owner.Enemy.Transform;
        var scale = transform.localScale;
        scale.x = -scale.x;
        transform.localScale = scale;
        Log.Info($"{owner.Enemy.Root.name}: iba a salir encima de otro jugador, sale por el otro lado");
        return toState;
    }

    private bool NearOtherPlayer(float x, float y) {
        foreach (var player in _session.Players) {
            if (player.IsLocal || player.Scene != _scene || !TryGetPlayerPosition(player.Id, out var position)) {
                continue;
            }

            if (Mathf.Abs(position.x - x) < EmergeClearance && Mathf.Abs(position.y - y) < EmergeClearance) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Acción de lanzar objetos que se está ejecutando (para seguir lo que saca del pool).</summary>
    private static HutongGames.PlayMaker.FsmStateAction _flinging;

    /// <summary>Llamadas a ObjectPool.Spawn anidadas (al crear un pool nuevo se llama a sí mismo).</summary>
    private static int _poolDepth;

    private static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        // Lo que lanzan las acciones Fling (el vómito de los Ogros Hongo): no lo guardan en ninguna variable, así que se
        // sigue lo que sacan del pool mientras se ejecutan. Lo que hace daño va a los demás como copia del original
        On.HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPool.OnEnter += (orig, self) => {
            _flinging = self;
            try {
                orig(self);
            } finally {
                _flinging = null;
            }
        };
        On.HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPoolVel.OnEnter += (orig, self) => {
            _flinging = self;
            try {
                orig(self);
            } finally {
                _flinging = null;
            }
        };
        // Las que crean "con el tiempo" lo hacen en OnUpdate, no al entrar (los disparos del Hollow Knight, la lluvia de
        // Nosk, el gas de los Fungus Flyer, el rastro de los Mushroom Roller, el de fuego de Grimm de la Pesadilla): solo
        // existían en el PC que mueve al enemigo y el compañero ni los veía ni le dañaban
        On.HutongGames.PlayMaker.Actions.FlingObjectsFromGlobalPoolTime.OnUpdate += (orig, self) => {
            _flinging = self;
            try {
                orig(self);
            } finally {
                _flinging = null;
            }
        };
        On.HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPoolOverTime.OnUpdate += (orig, self) => {
            _flinging = self;
            try {
                orig(self);
            } finally {
                _flinging = null;
            }
        };
        On.HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPoolOverTimeV2.OnUpdate += (orig, self) => {
            _flinging = self;
            try {
                orig(self);
            } finally {
                _flinging = null;
            }
        };
        On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion += (orig, prefab, parent, position, rotation) => {
            // Si el pool aún no tiene copias de ese prefab, Spawn crea el pool y se llama a sí mismo: solo cuenta la llamada
            // de fuera (si no, el mismo objeto se apuntaba dos veces y el otro PC recibía una copia huérfana)
            _poolDepth++;
            GameObject spawned;
            try {
                spawned = orig(prefab, parent, position, rotation);
            } finally {
                _poolDepth--;
            }

            if (_poolDepth == 0 && _flinging != null && spawned != null && IsGameplay(spawned)) {
                _active?.OnSpawned(_flinging, spawned, pooled: true);
            }

            return spawned;
        };
        On.HealthManager.Hit += (orig, self, hit) => {
            var sync = _active;
            if (_playingFx || sync == null || !sync._byHealth.TryGetValue(self, out var enemy) || !enemy.Puppet) {
                orig(self, hit);
                return;
            }

            // El ácido, los pinchos y las prensas también dañan al enemigo real en el PC que lo mueve: reenviado, contaba
            // dos veces
            if (IsWorldHazard(hit)) {
                return;
            }

            sync.SendHit(enemy, hit);
            if (hit.AttackType == AttackTypes.Nail) {
                sync.ForwardNailTriggers(enemy, self.gameObject);
            }

            if (self.hp > 0) {
                // Efectos del golpe y alma para quien golpea, sin quitar vida aquí (la quita el dueño)
                HitWithoutDamage(self, hit, orig);
            }
        };

        // Una marioneta no retrocede por su cuenta: su posición llega del PC que la mueve, retroceso incluido. Los golpes que
        // se reproducen en ella (también el aguijón onírico) pasaban por RecoilByDirection: con `freezeInPlace` disparaba
        // OnHandleFreeze, que en los Tiktik (`Climber.Stun`) volvía a arrancar su corrutina de andar aunque estuviera
        // apagado, y además mandaba RECOIL HORIZONTAL / HIT LEFT... a sus FSM, que se reenviaban al PC que la mueve
        On.Recoil.RecoilByDirection += (orig, self, direction, magnitude) => {
            var sync = _active;
            var health = sync != null && self != null ? self.GetComponent<HealthManager>() : null;
            if (health != null && sync._byHealth.TryGetValue(health, out var enemy) && enemy.Puppet) {
                return;
            }

            orig(self, direction, magnitude);
        };

        // Daño que no pasa por Hit: nubes de Spore Shroom y Defender's Crest. Se mide lo que quitó (con la vida subida para
        // que no muera dentro) y se aplica aquí con la vida extra de los jefes o, en una marioneta, se envía a quien la mueve
        On.HealthManager.ApplyExtraDamage += (orig, self, damage) =>
            MeasuredDamage(self, DamageKind.Extra, () => orig(self, damage));
        // Moscas de Flukenest: restan la vida directamente y llaman a Die
        On.SpellFluke.DoDamage += (orig, self, target, recursion, burst) => {
            var health = target != null ? target.GetComponent<HealthManager>() : null;
            if (health == null) {
                orig(self, target, recursion, burst);
                return;
            }

            MeasuredDamage(health, DamageKind.Subtract, () => orig(self, target, recursion, burst));
        };
        // Una marioneta no muere aquí por su cuenta (KillOnContact, liana que cae encima...): lo decide quien la mueve, que
        // avisa de la muerte. Antes moría solo en este PC (geo y diario aquí) y en el otro seguía vivo
        On.HealthManager.Die += (orig, self, direction, attackType, ignoreEvasion) => {
            var sync = _active;
            if (sync != null && !self.isDead && sync._byHealth.TryGetValue(self, out var enemy)) {
                if (enemy.Puppet) {
                    sync.SendDamage(enemy, DamageKind.Kill, 0, attackType);
                    return;
                }

                enemy.KilledBy = attackType;
            }

            orig(self, direction, attackType, ignoreEvasion);
        };

        // Estados de la lógica de los enemigos reales: los que suenan, también en las marionetas
        On.HutongGames.PlayMaker.Fsm.SwitchState += (orig, self, toState) => {
            if (toState != null && _active != null && _active.HoldWake(self, toState)) {
                SwitchToStateField?.SetValue(self, null);
                return;
            }

            if (toState != null && _active != null) {
                toState = _active.AvoidEmergingOnPartner(self, toState);
            }

            orig(self, toState);
            var sync = _active;
            if (sync?._session != null && toState != null && IsDeathState(toState.Name) &&
                sync._enemyFsms.TryGetValue(self, out var dying) && !dying.Enemy.Puppet) {
                dying.Enemy.DeathState = true;
            }

            if (sync?._session != null && toState != null && sync._soundFsms.Count > 0) {
                sync.OnFsmState(self, toState);
                // Entrar en ese estado provocó otro cambio dentro (un aviso de otra FSM): el de dentro ya se avisó antes
                // que este, y el último que recibían los demás era el viejo (con él, sus efectos continuos)
                if (self.ActiveState != null && self.ActiveState != toState) {
                    sync.OnFsmState(self, self.ActiveState);
                }
            }
        };

        // Lo que crea la lógica de los enemigos reales (proyectiles, crías, efectos)
        On.HutongGames.PlayMaker.Actions.SpawnObjectFromGlobalPool.OnEnter += (orig, self) => {
            orig(self);
            _active?.OnSpawned(self, self.storeObject?.Value, pooled: true);
        };
        On.HealthManager.OnEnable += (orig, self) => {
            orig(self);
            _active?.NoteEnabled(self);
        };

        On.HutongGames.PlayMaker.Actions.CreateObject.OnEnter += (orig, self) => {
            orig(self);
            _active?.OnSpawned(self, self.storeObject?.Value, pooled: false);
            _active?.OnSceneCreated(self, self.storeObject?.Value);
        };

        // Los avisos de fuera a la lógica de una marioneta (parada aquí) van al PC que la mueve
        On.HutongGames.PlayMaker.Fsm.ProcessEvent += (orig, self, fsmEvent, eventData) => {
            // La vida de un jefe real llegó a cero y su lógica lo atiende (la muerte de Grimm va por ZERO HP, sin
            // HealthManager.Die): desde ahí sus avisos a todos son los de después de vencerlo
            if (fsmEvent?.Name == ZeroHpEvent && _active != null && _active._enemyFsms.TryGetValue(self, out var dying) &&
                !dying.Enemy.Puppet && Handles(self, ZeroHpEvent)) {
                dying.Enemy.ZeroHp = true;
                dying.Enemy.ZeroHpAt = Time.unscaledTime;
            }

            // El remitente antes de procesarlo: `Fsm.EventData` es estático y los avisos que se mandan dentro lo cambian.
            // Con `eventData` se usa ese (si su remitente es null, es que no lo tiene: no se toma el estático)
            var sentBy = eventData != null ? eventData.SentByFsm : HutongGames.PlayMaker.Fsm.EventData?.SentByFsm;
            orig(self, fsmEvent, eventData);
            var sync = _active;
            if (sync?._session == null || fsmEvent == null || _replaying || _playingFx || _spawningCopy || _settingPuppet ||
                FsmStateSync.IsApplyingRemote || fsmEvent.Name == "FINISHED" ||
                !sync._enemyFsms.TryGetValue(self, out var target) || !target.Enemy.Puppet) {
                return;
            }

            // Los de otro enemigo (o de sí mismo) son de su lógica: ya pasan en el PC que los mueve
            var sender = sentBy;
            if (sender != null && (sync._enemyFsms.ContainsKey(sender) || FsmStateSync.IsSharedController(sender))) {
                return;
            }

            // DISABLE sin remitente es el que manda PlayMaker al apagarse la FSM en este PC (ver _settingPuppet)
            if (sender == null && fsmEvent.Name == PlayMakerDisableEvent) {
                return;
            }

            sync.ForwardEvent(target.Enemy, target.Index, fsmEvent.Name);
        };

        // Los avisos a todos de la sala no llegan a la lógica de una marioneta (parada, fuera de la lista de FSM activas)
        On.HutongGames.PlayMaker.Actions.SendEventByName.OnEnter += (orig, self) => {
            // A quién saca de su jaula, antes de que salga (al salir deja la jaula)
            var released = _active?.ReleaseTarget(self);
            orig(self);
            _active?.ForwardBroadcast(self);
            if (released != null) {
                _active?.OnReleased(released);
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

        // Lo que hace el juego para que los enemigos vuelvan (sus datos "semipersistentes"): al descansar en un banco y al
        // morir. El banco no llama a SetBenchRespawn (pone el punto de reaparición dato a dato): sentarse no hacía nada y
        // los enemigos muertos seguían muertos; morir tampoco los devolvía
        On.GameManager.ResetSemiPersistentItems += (orig, self) => {
            orig(self);
            _active?.OnLocalRest();
        };

        // SetBenchRespawn no es descansar: lo llaman los puntos de reaparición al cruzarlos (`RespawnTrigger`), y estaba
        // enganchado: cruzar uno devolvía antes de tiempo los enemigos muertos de la sala

        // Los golpes de otros aplicados en este PC no dan alma a este jugador
        On.HeroController.SoulGain += (orig, self) => {
            if (!_applyingRemoteHit) {
                orig(self);
            }
        };
    }

    /// <summary>
    /// Un aguijonazo de este jugador a una marioneta: si su lógica (parada aquí) esperaba en su estado de ahora un
    /// aguijonazo en su caja (una acción Trigger2dEvent con la etiqueta "Nail Attack"), su aviso va al PC que la mueve.
    /// El golpe final de la Radiancia (FINAL HIT) es así: sin esto solo podía darlo quien la movía.
    /// </summary>
    private void ForwardNailTriggers(Enemy enemy, GameObject struck) {
        if (enemy.Fsms == null) {
            return;
        }

        for (var i = 0; i < enemy.Fsms.Length && i <= byte.MaxValue; i++) {
            var fsm = enemy.Fsms[i];
            if (fsm == null || fsm.enabled || fsm.gameObject != struck) {
                continue;
            }

            // La copia de algo creado en combate nace parada en su estado inicial, no en el del original (el escupitajo
            // del Gulka espera el golpe en "Idle"): se mira en todos sus estados. El PC que la mueve solo lo atiende si
            // en su estado de ahora lo espera
            var states = enemy.Spawned ? fsm.FsmStates : new[] { fsm.Fsm.ActiveState };
            var sent = false;
            foreach (var state in states) {
                if (state == null || sent) {
                    continue;
                }

                foreach (var action in state.Actions) {
                    // (también la variante por capa: el escupitajo del Gulka se devuelve de un aguijonazo con ella)
                    if (NailTrigger(action, out var eventName, out _)) {
                        Log.Info($"{enemy.Root.name}/{fsm.FsmName}: {eventName} por un aguijonazo de este PC, al PC que lo mueve");
                        ForwardEvent(enemy, (byte) i, eventName);
                        sent = true;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Si la acción espera un aguijonazo en su caja (Trigger2dEvent o Trigger2dEventLayer con la etiqueta "Nail Attack"):
    /// su aviso y la variable donde guarda el objeto que la golpeó.
    /// </summary>
    private static bool NailTrigger(HutongGames.PlayMaker.FsmStateAction action, out string eventName,
        out HutongGames.PlayMaker.FsmGameObject store) {
        eventName = null;
        store = null;
        switch (action) {
            case HutongGames.PlayMaker.Actions.Trigger2dEvent trigger
                when trigger.trigger != PlayMakerUnity2d.Trigger2DType.OnTriggerExit2D &&
                     trigger.collideTag?.Value == "Nail Attack":
                eventName = trigger.sendEvent?.Name;
                store = trigger.storeCollider;
                break;
            case HutongGames.PlayMaker.Actions.Trigger2dEventLayer layer
                when layer.trigger != PlayMakerUnity2d.Trigger2DType.OnTriggerExit2D &&
                     layer.collideTag?.Value == "Nail Attack":
                eventName = layer.sendEvent?.Name;
                store = layer.storeCollider;
                break;
        }

        return action.Enabled && !string.IsNullOrEmpty(eventName);
    }

    private static GameObject _remoteNail;
    private static HutongGames.PlayMaker.FsmFloat _remoteNailDirection;

    /// <summary>
    /// Antes de atender aquí un aguijonazo que dio otro jugador en su PC: la lógica guarda el objeto que la golpeó y lee
    /// de su FSM `damages_enemy` hacia dónde iba el golpe (el Gulka devuelve así su escupitajo). Ese golpe no existe en
    /// este PC: se le da un aguijón de referencia (apagado, sin caja) con la dirección del golpe, deducida de dónde
    /// estaba ese caballero (a la derecha, izquierda, arriba o abajo del enemigo). Si no, salía siempre hacia la
    /// derecha, al revés si se golpeaba desde la derecha.
    /// </summary>
    private void PrepareRemoteNail(Enemy enemy, PlayMakerFSM fsm, string eventName, SessionPlayer from) {
        var state = fsm.Fsm.ActiveState;
        if (state == null || !TryGetPlayerPosition(from.Id, out var hitter)) {
            return;
        }

        foreach (var action in state.Actions) {
            if (!NailTrigger(action, out var name, out var store) || name != eventName || store == null || store.IsNone) {
                continue;
            }

            if (_remoteNail == null || _remoteNailDirection == null) {
                if (_remoteNail != null) {
                    UnityEngine.Object.Destroy(_remoteNail);
                }

                var nail = new GameObject("HKMultiplayer aguijón remoto");
                nail.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(nail);
                var nailFsm = nail.AddComponent<PlayMakerFSM>();
                // Apagado no pasa por su Awake: su Fsm se crea aquí (como en el editor)
                nailFsm.Reset();
                nailFsm.FsmName = "damages_enemy";
                var direction = new HutongGames.PlayMaker.FsmFloat("direction");
                nailFsm.FsmVariables.FloatVariables = new[] { direction };
                _remoteNail = nail;
                _remoteNailDirection = direction;
            }

            var target = enemy.Transform.position;
            var dx = target.x - hitter.x;
            var dy = target.y - hitter.y;
            _remoteNailDirection.Value = Mathf.Abs(dy) > Mathf.Abs(dx) ? (dy > 0f ? 90f : 270f) : (dx > 0f ? 0f : 180f);
            _remoteNail.transform.position = hitter;
            store.Value = _remoteNail;
        }
    }

    private enum DamageKind : byte {
        /// <summary><c>ApplyExtraDamage</c>: nubes de Spore Shroom, Defender's Crest y estiércol.</summary>
        Extra,

        /// <summary>Resta directa (<c>SpellFluke.DoDamage</c>).</summary>
        Subtract,

        /// <summary>Muerte directa (<c>Die</c> sin pasar por la vida).</summary>
        Kill
    }

    /// <summary>
    /// Golpes del mundo, no de un jugador: ácido, salpicadura y agua de la Ciudad, y los genéricos de 50 o más (pinchos,
    /// prensas de los ascensores, pinchos del Coliseo: <c>damages_enemy</c> con 50 o 9999). Ningún ataque del caballero ni
    /// de sus acompañantes llega a 50.
    /// </summary>
    private static bool IsWorldHazard(HitInstance hit) =>
        hit.AttackType is AttackTypes.Acid or AttackTypes.Splatter or AttackTypes.RuinsWater ||
        hit.AttackType == AttackTypes.Generic && hit.DamageDealt >= 50;

    /// <summary>
    /// Ejecuta algo que resta vida sin pasar por Hit y lo mide: con la vida subida antes, no puede morir dentro. En una
    /// marioneta la vida queda como estaba y el daño va a quien la mueve; en un enemigo real se aplica con la vida extra
    /// del host (<see cref="Features.Difficulty"/>, que solo actuaba en TakeDamage) y muere si llega a 0.
    /// </summary>
    private static void MeasuredDamage(HealthManager health, DamageKind kind, Action run) {
        var sync = _active;
        if (sync == null || health == null || health.isDead) {
            run();
            return;
        }

        const int Reserve = 100000;
        var before = health.hp;
        health.hp = before + Reserve;
        try {
            run();
        } finally {
            var dealt = before + Reserve - health.hp;
            health.hp = before;
            if (dealt > 0) {
                if (sync._byHealth.TryGetValue(health, out var enemy) && enemy.Puppet) {
                    sync.SendDamage(enemy, kind, dealt, AttackTypes.Generic);
                } else {
                    ApplyDamage(health, kind, dealt, AttackTypes.Generic);
                }
            }
        }
    }

    /// <summary>Lo que hace el juego con ese daño, con la vida extra de los jefes.</summary>
    private static void ApplyDamage(HealthManager health, DamageKind kind, int damage, AttackTypes attackType) {
        if (health == null || health.isDead) {
            return;
        }

        if (kind == DamageKind.Kill) {
            health.Die(null, attackType, true);
            return;
        }

        health.hp = Math.Max(health.hp - Features.Difficulty.ScaleDamage(health, damage), -50);
        if (health.hp <= 0) {
            health.Die(null, attackType, kind == DamageKind.Extra);
        }
    }

    private void SendDamage(Enemy enemy, DamageKind kind, int damage, AttackTypes attackType) {
        if (_session == null || !_session.CanSend || enemy.Health == null || enemy.Health.isDead) {
            return;
        }

        var writer = _session.Begin(EnemyDamage);
        writer.WriteUShort(_sceneHash);
        writer.WriteUInt(enemy.Id);
        writer.WriteByte((byte) kind);
        writer.WriteInt(damage);
        writer.WriteByte((byte) attackType);
        _session.End();
    }

    private void OnDamage(SessionPlayer from, PacketReader reader) {
        var sceneHash = reader.ReadUShort();
        var id = reader.ReadUInt();
        var kind = (DamageKind) reader.ReadByte();
        var damage = Mathf.Clamp(reader.ReadInt(), 0, 1000);
        var attackType = (AttackTypes) reader.ReadByte();
        if (sceneHash != _sceneHash || !_byId.TryGetValue(id, out var enemy) || enemy.Puppet || enemy.Health == null ||
            enemy.Health.isDead || kind > DamageKind.Kill) {
            return;
        }

        if (kind == DamageKind.Kill) {
            Log.Info($"{enemy.Root.name}: muere en el PC de {from.Name} (sin golpe)");
        }

        ApplyDamage(enemy.Health, kind, damage, attackType);
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

    public int EffectsPlayed { get; private set; }

    /// <summary>Jugadores caídos o en pausa (no mueven enemigos), para el modo de prueba.</summary>
    private string DescribeInactive() {
        var text = new StringBuilder();
        if (_session == null) {
            return string.Empty;
        }

        foreach (var player in _session.Players) {
            if (Features.PartyState.IsInactive(player.Id)) {
                text.Append(text.Length > 0 ? "," : string.Empty).Append(player.Id);
            }
        }

        return text.ToString();
    }

    public IEnumerable<string> Describe() {
        yield return $"efectos de golpes ajenos={HitFxPlayed} efectos de marionetas={EffectsPlayed} " +
                     $"fsm con sonido={_soundFsms.Count} sala={_scene} dueño={_session?.OwnerOf(_scene)} " +
                     $"inactivos={DescribeInactive()}";
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
                         $" hp={enemy.Health.hp}{(enemy.Damage != null ? $" daño={enemy.Damage.damageDealt}" : "")}" +
                         $"{(enemy.Health.isDead ? " MUERTO" : "")}{(enemy.Health.IsInvincible ? " invencible" : "")}" +
                         $"{(enemy.Root.activeInHierarchy ? "" : " (inactivo)")}" +
                         $"{(enemy.Root.GetComponent<Renderer>() is { enabled: false } ? " oculto" : "")}" +
                         $"{(enemy.Root.GetComponent<Collider2D>() is { enabled: false } ? " sin-caja" : "")} " +
                         $"pos=({p.x:0.00},{p.y:0.00}) giro={enemy.Transform.eulerAngles.z:0}" +
                         $"{(enemy.Body != null ? $" vel=({enemy.Body.velocity.x:0.00},{enemy.Body.velocity.y:0.00})" : "")}";
        }
    }

    /// <summary>
    /// Jefes de la sala (vida de jefe): por cada FSM, su estado y los eventos que lo hacen avanzar, y las transiciones
    /// globales (modo de prueba: para saber cómo despertarlos).
    /// </summary>
    public IEnumerable<string> DescribeBosses() {
        foreach (var enemy in _enemies) {
            if (enemy.Root == null || enemy.Health == null || enemy.Health.hp < BossHp || enemy.Fsms == null) {
                continue;
            }

            var p = enemy.Transform.position;
            var arena = BattleSceneField?.GetValue(enemy.Health) as GameObject;
            yield return $"jefe {enemy.Root.name} hp={enemy.Health.hp} pos=({p.x:0.0},{p.y:0.0}) {(enemy.Puppet ? "marioneta" : "real")}" +
                         $" arena={(arena != null ? arena.name : "-")} dormido={enemy.DormantState ?? "-"} despierto={enemy.Awake}" +
                         $" esperando={enemy.WaitingWake}";
            foreach (var fsm in enemy.Fsms) {
                if (fsm == null) {
                    continue;
                }

                var state = fsm.Fsm.ActiveState;
                var exits = state == null
                    ? "-"
                    : string.Join(", ", Array.ConvertAll(state.Transitions, t => $"{t.EventName}->{t.ToState}"));
                var globals = string.Join(", ", Array.ConvertAll(fsm.FsmGlobalTransitions, t => $"{t.EventName}->{t.ToState}"));
                yield return $"  {fsm.gameObject.name}/{fsm.FsmName} estado={fsm.ActiveStateName} sale=[{exits}] globales=[{globals}]";
            }
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
                var drawn = part.Renderer == null ? "-" : part.Renderer.enabled ? "sí" : "no";
                var angle = part.Transform.localEulerAngles.z;
                yield return $"{enemy.Root.name}/{part.Transform.name}{(part.Key != 0 ? " (fuera)" : "")} " +
                             $"{(part.Transform.gameObject.activeSelf ? "visible" : "oculta")} dibujada={drawn} " +
                             $"pos=({local.x:0.00},{local.y:0.00}) giro={angle:0} escala={part.Transform.localScale.x:0.00} anim={clip}";
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

    /// <summary>Golpea a todos los enemigos vivos y activos de la sala (modo de prueba); devuelve cuántos.</summary>
    public int TestHitAll(int damage) {
        var hero = HeroController.instance;
        var count = 0;
        foreach (var enemy in _enemies.ToArray()) {
            if (enemy.Root != null && enemy.Root.activeInHierarchy && enemy.Health != null && !enemy.Health.isDead) {
                enemy.Health.Hit(new HitInstance {
                    Source = hero.gameObject, AttackType = AttackTypes.Nail, DamageDealt = damage, Multiplier = 1f,
                    MagnitudeMultiplier = 1f, Direction = 0f, IgnoreInvulnerable = true
                });
                count++;
            }
        }

        return count;
    }

    /// <summary>Modo de prueba: avisa en el registro si alguien apaga el enemigo estando vivo, y desde dónde.</summary>
    private static void WatchDisable(Enemy enemy) {
        if (!TestMode || enemy.Root == null) {
            return;
        }

        // Puede que ya lo vigilara con otro identificador (lo creó su jaula antes de repasar la sala)
        var watch = enemy.Root.GetComponent<DisableWatch>() ?? enemy.Root.AddComponent<DisableWatch>();
        watch.Health = enemy.Health;
        watch.Id = enemy.Id;
    }

    /// <summary>Registra quién apaga un enemigo vivo (modo de prueba).</summary>
    internal sealed class DisableWatch : MonoBehaviour {
        public HealthManager Health;
        public uint Id;

        private void OnDisable() {
            // Si sigue activo es que solo se apagó este componente; al descargar la sala se apaga todo
            if (Health == null || Health.isDead || gameObject.activeSelf || !gameObject.scene.isLoaded) {
                return;
            }

            var sync = _active;
            var enemy = sync != null && sync._byId.TryGetValue(Id, out var found) ? found : null;
            var caller = string.Join(" < ", Array.ConvertAll(new System.Diagnostics.StackTrace().GetFrames() ??
                                                             Array.Empty<System.Diagnostics.StackFrame>(),
                frame => $"{frame.GetMethod()?.DeclaringType?.Name}.{frame.GetMethod()?.Name}"));
            Log.Info($"[TEST] apagado vivo {name} id={Id:x8} hp={Health.hp} " +
                     $"{(enemy == null ? "sin seguir" : enemy.Puppet ? "marioneta" : "real")} " +
                     $"pos=({transform.position.x:0.00},{transform.position.y:0.00}) fsm={DescribeExecuting()} por {caller}");
        }

        private static string DescribeExecuting() {
            var fsm = HutongGames.PlayMaker.FsmExecutionStack.ExecutingFsm;
            return fsm == null ? "-" : $"{fsm.GameObjectName}/{fsm.Name}/{fsm.ActiveStateName}";
        }
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

/// <summary>Avisa de los aguijonazos de este jugador que tocan una copia sin vida propia (ver EnemySync.OnNailTouch).</summary>
internal sealed class NailCatcher : MonoBehaviour {
    private void OnTriggerEnter2D(Collider2D other) {
        if (other.CompareTag("Nail Attack")) {
            EnemySync.OnNailTouch(gameObject);
        }
    }
}
