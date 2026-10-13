using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Game.World;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.UI;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Revivir: al morir con un compañero en pie no se muere, se cae. El caído hace la animación de muerte y queda tendido
/// ("† Nombre"); su caballero se oculta (sin colisión, invencible) y su cámara sigue al compañero, también entre salas.
/// El compañero recibe "X cayó en &lt;zona&gt;" y una flecha hacia el cuerpo; junto a él (2 unidades o menos), mantener
/// Focus el tiempo que diga el host (2 s) lo revive gastando alma (33): ese Focus no cura, y un golpe al que revive le
/// quita el progreso. El revivido vuelve en su cuerpo con 1 máscara y un rato invulnerable (parpadeando). Sin límite de
/// tiempo caído; si un compañero se sienta en un banco, los caídos reviven a su lado con toda la vida. Si caen todos,
/// cada uno muere de forma normal (sombra y vuelta a su banco).
/// <para>Lo decide el host: <see cref="Downed"/>, <see cref="ReviveRequest"/> (lo valida) y <see cref="BenchRest"/> le
/// llegan a él; responde con <see cref="Revived"/> o <see cref="Wipe"/> al caído y reparte el estado con
/// <see cref="PartyState"/>. El progreso de quien revive va a la sala con <see cref="ReviveProgress"/> (no fiable).</para>
/// </summary>
internal sealed class Revive {
    /// <summary>El jugador cayó: sala y posición de su cuerpo (jugador → host).</summary>
    public const MessageType Downed = (MessageType) 75;

    /// <summary>Quien revive terminó de mantener Focus junto a un cuerpo (→ host, que lo valida).</summary>
    public const MessageType ReviveRequest = (MessageType) 76;

    /// <summary>Te revivieron: dónde vuelves (tu cuerpo o el banco de un compañero) y si con toda la vida (host → caído).</summary>
    public const MessageType Revived = (MessageType) 77;

    /// <summary>No queda nadie en pie: muerte normal (host → caído).</summary>
    public const MessageType Wipe = (MessageType) 78;

    /// <summary>El jugador se sentó en un banco: sala y posición (→ host).</summary>
    public const MessageType BenchRest = (MessageType) 79;

    /// <summary>Cuánto lleva quien revive y a quién (→ sala, no fiable).</summary>
    public const MessageType ReviveProgress = (MessageType) 80;

    /// <summary>Distancia máxima al cuerpo para revivirlo.</summary>
    private const float ReviveRadius = 2f;

    /// <summary>Tras caer, la cámara se queda en el cuerpo este rato (la animación de muerte) antes de seguir al compañero.</summary>
    private const float FollowDelay = 2.5f;

    /// <summary>Tiempo mínimo entre dos viajes del caído detrás de su compañero.</summary>
    private const float FollowCooldown = 1.5f;

    /// <summary>Si un viaje no termina en este tiempo (el juego lo rechazó), se reintenta.</summary>
    private const float TravelTimeout = 20f;

    private const float ProgressSendInterval = 0.1f;

    /// <summary>"X te está reviviendo" se quita si deja de llegar su progreso.</summary>
    private const float BeingRevivedTimeout = 0.6f;

    /// <summary>
    /// Un avatar a menos de esto del cuerpo (en horizontal y en vertical) tiene su nombre junto al "† Nombre": este sube
    /// por encima del suyo, con este hueco (el alto de una línea y algo más).
    /// </summary>
    private const float LabelClearX = 3f;

    private const float LabelClearY = 1.6f;
    private const float LabelGap = 0.75f;

    /// <summary>Salas a las que el caído no sigue a su compañero: cinemáticas, sueños, Panteones y menús.</summary>
    private static readonly string[] NotFollowedPrefixes = {
        "Cinematic_", "Cutscene_", "Dream_", "GG_", "Menu_", "Quit_To_Menu", "Opening_Sequence", "Intro_Cutscene",
        "Prologue_Excerpt", "Knight_Pickup", "Pre_Menu_Intro", "End_", "PermaDeath", "BetaEnd"
    };

    /// <summary>Salas con prefijo GG_ que son de Hallownest (no de Godhome).</summary>
    private static readonly HashSet<string> FollowedGodhomeRooms = new() { "GG_Waterways", "GG_Pipeway", "GG_Lurker" };

    private enum Arrival : byte {
        None,
        Revive,
        RealDeath
    }

    private static readonly MethodInfo DieMethod =
        typeof(HeroController).GetMethod("Die", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo FinishedEnteringSceneMethod = typeof(HeroController).GetMethod(
        "FinishedEnteringScene", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool), typeof(bool) },
        null);

    private static readonly MethodInfo SendHeroInPositionMethod =
        typeof(HeroController).GetMethod("SendHeroInPosition", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>Invulnerable un rato, parpadeando (como tras un golpe).</summary>
    private static readonly MethodInfo StartInvulnerableMethod =
        typeof(HeroController).GetMethod("StartInvulnerable", BindingFlags.NonPublic | BindingFlags.Instance);

    private static Revive _active;
    private static bool _hooked;

    /// <summary>La próxima muerte es de verdad (no se cae).</summary>
    private static bool _allowRealDeath;

    /// <summary>Fracción de máscara pendiente del multiplicador de daño recibido (se suma al golpe siguiente).</summary>
    private static float _damageCarry;

    /// <summary>Dentro de un golpe al caballero escalado por el mod.</summary>
    private static bool _scaledHit;

    /// <summary>El golpe en curso llegó a quitar vida (el juego llamó a TakeHealth).</summary>
    private static bool _hitLanded;

    /// <summary>El golpe en curso quedó en 0 con el multiplicador: se recibe sin quitar vida.</summary>
    private static bool _absorbHit;

    private readonly MessageHandler _onDowned;
    private readonly MessageHandler _onRequest;
    private readonly MessageHandler _onRevived;
    private readonly MessageHandler _onWipe;
    private readonly MessageHandler _onBench;
    private readonly MessageHandler _onProgress;
    private readonly Action<PartyState.Member, PlayerStatus> _onStatus;
    private readonly Action<SessionPlayer> _onLeft;
    private readonly Action<SessionPlayer> _onSceneChanged;

    private GameSession _session;
    private PartyState _party;
    private PlayerSync _players;
    private Vitals _vitals;

    // ---- El jugador local caído ----
    private bool _downed;
    private string _bodyScene = string.Empty;
    private Vector3 _bodyPosition;
    private float _downedAt;
    private Arrival _arrival;
    private bool _reviveFull;

    /// <summary>Revive junto a su compañero, donde esté al llegar, y no en su cuerpo (que quedó en un sueño).</summary>
    private bool _reviveAtPartner;
    private int _lastHazardType;
    private readonly HashSet<Renderer> _hiddenRenderers = new();
    private readonly List<Renderer> _rendererScratch = new();

    // Viaje de sala pedido por el mod (sin puerta de entrada)
    private string _travelTarget;
    private Vector3? _arrivalPosition;
    private float _travelStartedAt = -100f;

    /// <summary>Punto de la Puerta de los sueños del jugador, cambiado durante un viaje a un punto (se repone al acabar).</summary>
    private readonly DreamGatePoint _dreamGate = new();


    // Último sitio en que se vio al compañero en la sala del caído: por qué puerta se fue
    private string _partnerSeenScene = string.Empty;
    private Vector3 _partnerSeenAt;

    // Zonas de cámara emuladas mientras se sigue al compañero (el caído no tiene colisión)
    private int _cameraAreasScene = -1;
    private CameraLockArea[] _cameraAreas;
    private readonly HashSet<CameraLockArea> _insideCameraAreas = new();

    // ---- El jugador local reviviendo a otro ----
    private ushort _target;
    private float _progress;
    private int _reservedSoul;
    private float _cooldownUntil;
    private bool _notEnoughSoul;
    private float _nextProgressSend;
    private bool _progressAnnounced;

    // Quién revive a quién (lo que llega de la sala)
    private ushort _revivedBy;
    private ushort _revivingTarget;
    private float _revivingProgress;
    private float _revivingAt = -100f;

    // ---- Cuerpos dibujados en esta sala ----
    private readonly Dictionary<ushort, DownedBody> _bodies = new();
    private readonly List<ushort> _bodyScratch = new();
    private string _bodiesScene = string.Empty;
    private Font _font;

    private bool _wasAtBench;

    // ---- Pantalla ----
    private Text _status;
    private RectTransform _prompt;
    private Text _promptText;
    private Image _promptBar;
    private RectTransform _promptBarRoot;
    private int _statusKey = -1;
    private long _promptKey = -1;

    static Revive() {
        MessageRoutes.Register(Downed, Route.Server, reliable: true);
        MessageRoutes.Register(ReviveRequest, Route.Server, reliable: true);
        MessageRoutes.Register(Revived, Route.Server, reliable: true);
        MessageRoutes.Register(Wipe, Route.Server, reliable: true);
        MessageRoutes.Register(BenchRest, Route.Server, reliable: true);
        MessageRoutes.Register(ReviveProgress, Route.SameScene, reliable: false);
    }

    public Revive() {
        _onDowned = OnDowned;
        _onRequest = OnReviveRequest;
        _onRevived = OnRevived;
        _onWipe = OnWipe;
        _onBench = OnBenchRest;
        _onProgress = OnProgress;
        _onStatus = OnStatusChanged;
        _onLeft = OnPlayerLeft;
        _onSceneChanged = OnPlayerSceneChanged;
    }

    /// <summary>Si el jugador local está caído.</summary>
    public bool IsDowned => _downed;

    /// <summary>Sala y posición del cuerpo propio (solo con sentido estando caído).</summary>
    public string OwnBodyScene => _bodyScene;

    public Vector3 OwnBodyPosition => _bodyPosition;

    /// <summary>En un viaje de sala del mod (el caído detrás de su compañero, recargar la sala).</summary>
    public bool IsTraveling => _travelTarget != null;

    /// <summary>Si el jugador local está junto a un cuerpo que puede revivir (su Focus es para revivir, no cura).</summary>
    public bool NearBody => !_downed && _target != 0;

    public void Attach(GameSession session, PartyState party, PlayerSync players, Vitals vitals) {
        _session = session;
        _party = party;
        _players = players;
        _vitals = vitals;
        _active = this;
        session.On(Downed, _onDowned);
        session.On(ReviveRequest, _onRequest);
        session.On(Revived, _onRevived);
        session.On(Wipe, _onWipe);
        session.On(BenchRest, _onBench);
        session.On(ReviveProgress, _onProgress);
        session.PlayerLeft += _onLeft;
        session.PlayerSceneChanged += _onSceneChanged;
        party.StatusChanged += _onStatus;
        _wasAtBench = PlayerData.instance != null && PlayerData.instance.atBench;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(Downed, null);
            _session.On(ReviveRequest, null);
            _session.On(Revived, null);
            _session.On(Wipe, null);
            _session.On(BenchRest, null);
            _session.On(ReviveProgress, null);
            _session.PlayerLeft -= _onLeft;
            _session.PlayerSceneChanged -= _onSceneChanged;
        }

        if (_party != null) {
            _party.StatusChanged -= _onStatus;
        }

        // Sin partida nadie puede revivir: si se sigue jugando, muerte normal; si se vuelve al menú, nada
        if (_downed) {
            var leavingWorld = CoopSaves.ActiveWorld != null && !CoopSaves.IsHostWorld;
            if (leavingWorld) {
                CancelDowned();
            } else {
                Log.Info("Partida cerrada estando caído: muerte normal");
                GoToBodyThen(Arrival.RealDeath);
                MultiplayerRunner.Instance.StartCoroutine(FinishWithoutSession());
            }
        }

        ResetProgress();
        ClearBodies();
        SetUiVisible(false);
        _session = null;
        _party = null;
        if (_active == this && !_downed) {
            _active = null;
        }
    }

    /// <summary>Sin partida, el caído termina su viaje al cuerpo y muere de forma normal.</summary>
    private IEnumerator FinishWithoutSession() {
        var end = Time.unscaledTime + TravelTimeout;
        while (_downed && Time.unscaledTime < end) {
            if (!HandleArrival() && _arrival != Arrival.None && UnitySceneManager.GetActiveScene().name == _bodyScene) {
                FinishArrival();
            }

            yield return null;
        }

        if (_active == this) {
            _active = null;
        }
    }

    // =====================================================================================================
    // Muerte del caballero local
    // =====================================================================================================

    /// <summary>
    /// Se llama al cargar el mod: <c>Die()</c> es tan corto que el juego lo copia dentro de <c>TakeDamage</c> al compilarlo,
    /// y si se engancha después (al empezar la partida) la muerte no pasa por aquí.
    /// </summary>
    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        // El gancho de Die tiene que ir antes que el de TakeDamage: MonoMod compila la copia de TakeDamage que hace de
        // `orig` al poner su gancho, y esa copia solo llama a este Die si ya estaba puesto (si se cambia el orden, el
        // caballero muere de verdad en vez de caer)
        On.HeroController.Die += (orig, self) => {
            var active = _active;
            if (_allowRealDeath || active == null) {
                _allowRealDeath = false;
                return orig(self);
            }

            return active.OnHeroDie(orig, self);
        };

        On.HeroController.TakeDamage += (orig, self, source, side, amount, hazard) => {
            var active = _active;
            // Un golpe que ya llega con 0 (los Shrumeling no hacen daño al tocarlos) el juego lo ignora: no se convierte en
            // un golpe de 1 con retroceso como los que el multiplicador deja en 0
            if (active == null || active._session == null || amount <= 0) {
                orig(self, source, side, amount, hazard);
                return;
            }

            active._lastHazardType = hazard;
            var pd = PlayerData.instance;
            var before = pd != null ? pd.health + pd.healthBlue : 0;
            var scaled = ScaleDamage(amount, hazard, out var carry);

            // Un golpe que con el multiplicador queda en 0 se recibe igual (retroceso e invulnerabilidad, si no el enemigo
            // volvería a golpear en el frame siguiente), pero sin quitar vida: TakeHealth se salta
            _scaledHit = true;
            _hitLanded = false;
            _absorbHit = scaled == 0;
            try {
                orig(self, source, side, scaled == 0 ? 1 : scaled, hazard);
            } finally {
                _scaledHit = false;
                _absorbHit = false;
            }

            // Lo que sobra del redondeo solo cuenta si el golpe llegó (con invulnerabilidad el juego lo ignora)
            if (_hitLanded) {
                _damageCarry = carry;
            }

            if (_hitLanded || pd != null && pd.health + pd.healthBlue < before) {
                active.OnHurt();
            }
        };

        On.PlayerData.TakeHealth += (orig, self, amount) => {
            if (_scaledHit) {
                _hitLanded = true;
                if (_absorbHit) {
                    return;
                }
            }

            orig(self, amount);
        };

        // Junto a un cuerpo, mantener Focus es para revivir: no cura
        On.HeroController.CanFocus += (orig, self) => orig(self) && !(_active != null && _active.NearBody);

        // Salir al menú estando caído: no hay muerte ni viaje al cuerpo a mitad de la salida
        On.GameManager.ReturnToMainMenu += (orig, self, saveMode, callback) => {
            _active?.CancelDowned();
            return orig(self, saveMode, callback);
        };

        // Lo que se guarda estando caído (el personaje del invitado, el mundo del host) no lleva la invencibilidad ni la
        // vida a 0 de la caída
        On.GameManager.SaveGame_int_Action1 += (orig, self, slot, callback) => {
            var pd = self.playerData;
            var active = _active;
            if (active == null || !active._downed || pd == null) {
                orig(self, slot, callback);
                return;
            }

            var invincible = pd.isInvincible;
            var health = pd.health;
            pd.isInvincible = false;
            pd.health = Math.Max(health, 1);
            try {
                orig(self, slot, callback);
            } finally {
                pd.isInvincible = invincible;
                pd.health = health;
            }
        };
    }

    /// <summary>
    /// Daño que recibe el caballero local según el host (pinchos, ácido y pozos no cambian). Lo que no llega a una máscara
    /// se acumula para el golpe siguiente: con 0,5× los golpes de 1 quitan una máscara cada dos (antes, redondeando, todos
    /// quitaban una y el ajuste no hacía nada); con 1,5×, uno quita 1 y el siguiente 2. Puede devolver 0.
    /// </summary>
    private static int ScaleDamage(int damage, int hazardType, out float carry) {
        var multiplier = HostSettings.Current.DamageTaken;
        carry = _damageCarry;
        if (damage <= 0 || hazardType > 1 || Mathf.Approximately(multiplier, 1f)) {
            return damage;
        }

        var exact = damage * multiplier + _damageCarry;
        var whole = Mathf.FloorToInt(exact + 0.001f);
        carry = Mathf.Max(0f, exact - whole);
        return whole;
    }

    private IEnumerator OnHeroDie(On.HeroController.orig_Die orig, HeroController hero) {
        // Ya caído (algo que mata sin pasar por el daño): no se vuelve a caer
        if (_downed) {
            return EmptyRoutine();
        }

        if (!CanBeDowned(out var reason)) {
            if (reason != null) {
                Log.Info($"Muerte normal: {reason}");
            }

            // Si había otros caídos, el host los hace morir también
            if (_session != null && _session.CanSend && reason == NobodyStanding) {
                SendDowned(UnitySceneManager.GetActiveScene().name, hero.transform.position, hero.cState.facingRight);
            }

            return orig(hero);
        }

        var scene = UnitySceneManager.GetActiveScene().name;
        var position = BodyPosition(hero, _lastHazardType);
        Log.Info($"Caído en {scene} ({position.x:0.0}, {position.y:0.0})");
        CancelActions(hero);
        EnterDowned(scene, position);
        SendDowned(scene, position, hero.cState.facingRight);
        return EmptyRoutine();
    }

    /// <summary>
    /// Corta lo que estuviera haciendo (Crystal Heart, un hechizo, Cyclone Slash...) con el aviso global FSM CANCEL de sus
    /// FSM: al caer a mitad seguían (el sonido en bucle de la carga, la velocidad) y se retomaban al revivir.
    /// </summary>
    private static void CancelActions(HeroController hero) {
        foreach (var name in ActionFsms) {
            var fsm = PlayMakerFSM.FindFsmOnGameObject(hero.gameObject, name);
            if (fsm != null) {
                fsm.SendEvent("FSM CANCEL");
            }
        }
    }

    private static readonly string[] ActionFsms = { "Superdash", "Spell Control", "Nail Arts" };

    private const string NobodyStanding = "no queda nadie en pie";

    private bool CanBeDowned(out string reason) {
        reason = null;
        if (_session == null || !_session.CanSend || _party == null) {
            return false;
        }

        if (!HostSettings.Current.ReviveEnabled) {
            reason = "el host desactivó revivir";
            return false;
        }

        var gm = GameManager.instance;
        var zone = gm != null ? gm.GetCurrentMapZone() : string.Empty;
        if (zone == "GODS_GLORY") {
            reason = "en los Panteones la muerte es la del juego";
            return false;
        }

        var scene = UnitySceneManager.GetActiveScene().name;
        var standing = false;
        var standingHere = false;
        foreach (var member in _party.Members) {
            // Quien está entrando en la partida (cargando, en la introducción) no puede revivir a nadie
            if (member.Id == _session.LocalId || member.Status is PlayerStatus.Downed or PlayerStatus.Joining ||
                member.Player == null) {
                continue;
            }

            standing = true;
            standingHere |= member.Player.Scene == scene;
        }

        if (!standing) {
            reason = NobodyStanding;
            return false;
        }

        // En un sueño solo se cae si el compañero está en el mismo sueño (si no, nadie podría revivirlo dentro). También
        // por el nombre de la sala, por si alguna de sueño no lleva la zona DREAM_WORLD
        var dream = zone == "DREAM_WORLD" || scene.StartsWith("Dream_", StringComparison.Ordinal) ||
                    scene.StartsWith("Grimm_Nightmare", StringComparison.Ordinal);
        if (dream && !standingHere) {
            reason = "en un sueño sin compañero";
            return false;
        }

        return true;
    }

    private void SendDowned(string scene, Vector3 position, bool facingRight) {
        var writer = _session.Begin(Downed);
        writer.WriteString(scene);
        writer.WriteFloat(position.x);
        writer.WriteFloat(position.y);
        // Hacia dónde mira al caer: su cuerpo mira igual en todos. Antes cada PC lo tomaba de su avatar al llegar el
        // estado, cuando su caballero escondido ya iba pegado al del compañero, y podía salir al revés
        writer.WriteBool(facingRight);
        _session.End();
    }

    /// <summary>
    /// Posición del cuerpo, que tiene que quedar donde el compañero pueda quedarse de pie a su lado manteniendo Focus (en
    /// el aire, sobre pinchos o en ácido no se puede): el suelo justo debajo de donde cayó. Si murió por un peligro
    /// (pinchos, ácido, un pozo), si debajo no hay suelo (cayó sobre un pozo) o si ese suelo es un peligro, el cuerpo
    /// queda en el último sitio seguro, donde el juego hace reaparecer tras un peligro.
    /// </summary>
    private static Vector3 BodyPosition(HeroController hero, int hazardType) {
        var safe = (Vector3) PlayerData.instance.hazardRespawnLocation;
        safe.z = hero.transform.position.z;
        if (hazardType > 1) {
            return safe;
        }

        if (!GroundBelow(hero, out var ground) || InHazard(hero, ground)) {
            Log.Info("El cuerpo no tiene suelo seguro debajo: queda en el último sitio seguro");
            return safe;
        }

        return ground;
    }

    /// <summary>El suelo justo debajo del caballero (con sus pies apoyados), si lo hay.</summary>
    private static bool GroundBelow(HeroController hero, out Vector3 ground) {
        var position = hero.transform.position;
        ground = position;
        var collider = hero.GetComponent<Collider2D>();
        if (collider == null) {
            return true;
        }

        var feetOffset = position.y - collider.bounds.min.y;
        var mask = LayerMask.GetMask("Terrain");
        if (mask == 0) {
            mask = 1 << 8;
        }

        var hit = Physics2D.Raycast(new Vector2(position.x, collider.bounds.min.y + 0.1f), Vector2.down, 100f, mask);
        if (hit.collider == null) {
            return false;
        }

        ground = new Vector3(position.x, hit.point.y + feetOffset, position.z);
        return true;
    }

    /// <summary>Si el caballero, puesto en <paramref name="position"/>, tocaría un peligro (pinchos, ácido...).</summary>
    private static bool InHazard(HeroController hero, Vector3 position) {
        var collider = hero.GetComponent<Collider2D>();
        var size = collider != null ? (Vector2) collider.bounds.size : new Vector2(0.5f, 1.2f);
        var center = (Vector2) position + (collider != null
            ? (Vector2) (collider.bounds.center - hero.transform.position)
            : Vector2.zero);
        foreach (var other in Physics2D.OverlapBoxAll(center, size, 0f)) {
            var damage = other.GetComponent<DamageHero>();
            if (damage != null && damage.hazardType > 1) {
                return true;
            }
        }

        return false;
    }

    private void EnterDowned(string scene, Vector3 position) {
        _downed = true;
        _downedAt = Time.unscaledTime;
        _bodyScene = scene;
        _bodyPosition = position;
        _arrival = Arrival.None;
        _reviveFull = false;
        _arrivalPosition = null;
        ResetProgress();
        ApplyHiddenState();
    }

    private void OnHurt() {
        // Un golpe al que revive le quita el progreso
        if (_progress > 0f) {
            Log.Info("Golpeado mientras revivía: se pierde el progreso");
            ResetProgress();
            _cooldownUntil = Time.unscaledTime + 0.5f;
        }
    }

    // =====================================================================================================
    // Host
    // =====================================================================================================

    private void OnDowned(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        var facingRight = reader.ReadBool();
        if (_session == null || !_session.IsHost) {
            return;
        }

        var member = _party.Get(from.Id);
        if (member == null) {
            return;
        }

        if (member.Status == PlayerStatus.Downed) {
            return;
        }

        var partner = FindStanding(from.Id, scene);
        if (partner == null) {
            Log.Info($"{from.Name} cayó y no queda nadie en pie: muerte normal para todos");
            WipeAll(from.Id);
            return;
        }

        Log.Info($"{from.Name} cayó en {scene}; su cámara sigue a {partner.Name}");
        _party.SetStatus(from.Id, PlayerStatus.Downed, scene, x, y, partner.Id, facingRight);

        // Quienes seguían al que acaba de caer pasan a seguir al que sigue en pie
        RetargetFollowers(from.Id);
    }

    /// <summary>
    /// Jugador en pie que no es <paramref name="excluded"/>: mejor uno al que se pueda seguir (no en un sueño, una
    /// cinemática o el viaje en ciervo, salvo que sea la sala del cuerpo) y, entre esos, uno en esa sala. Antes solo se
    /// descartaban los caídos: con 3 o más, al caído le tocaba a veces alguien en un sueño y se quedaba sin seguir a nadie
    /// hasta que ese caía o se iba.
    /// </summary>
    private PartyState.Member FindStanding(ushort excluded, string scene) {
        PartyState.Member best = null;
        var bestRank = -1;
        foreach (var member in _party.Members) {
            if (member.Id == excluded || member.Status is PlayerStatus.Downed or PlayerStatus.Joining || member.Player == null) {
                continue;
            }

            var rank = (CanFollow(member.Player.Scene, scene) ? 2 : 0) + (member.Player.Scene == scene ? 1 : 0);
            if (rank > bestRank) {
                best = member;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>Si un caído con el cuerpo en <paramref name="bodyScene"/> puede seguir a alguien en esa sala.</summary>
    private static bool CanFollow(string scene, string bodyScene) =>
        !string.IsNullOrEmpty(scene) && (scene == bodyScene || IsFollowedScene(scene));

    private float _nextFollowCheck;

    /// <summary>
    /// Host, cada segundo: un caído que sigue a alguien al que no puede seguir (entró en un sueño, una cinemática o el
    /// ciervo después de asignárselo) pasa a seguir a otro en pie que sí, si lo hay. Si no, se queda con el que tenía.
    /// </summary>
    private void RetargetUnfollowable() {
        var now = Time.unscaledTime;
        if (now < _nextFollowCheck) {
            return;
        }

        _nextFollowCheck = now + 1f;
        foreach (var member in _party.Members) {
            if (member.Status != PlayerStatus.Downed) {
                continue;
            }

            var current = _party.Get(member.Partner);
            if (current?.Player != null && current.Status is not (PlayerStatus.Downed or PlayerStatus.Joining) &&
                CanFollow(current.Player.Scene, member.BodyScene)) {
                continue;
            }

            var better = FindStanding(member.Id, member.BodyScene);
            if (better != null && better.Id != member.Partner && CanFollow(better.Player.Scene, member.BodyScene)) {
                Log.Info($"{member.Name} no puede seguir a {current?.Name ?? "nadie"}: pasa a seguir a {better.Name}");
                _party.SetPartner(member.Id, better.Id);
            }
        }
    }

    private void RetargetFollowers(ushort fallen) {
        foreach (var member in _party.Members) {
            if (member.Status != PlayerStatus.Downed || member.Partner != fallen) {
                continue;
            }

            var partner = FindStanding(member.Id, member.BodyScene);
            if (partner != null) {
                _party.SetPartner(member.Id, partner.Id);
            }
        }
    }

    /// <summary>No queda nadie en pie: todos los caídos mueren de verdad.</summary>
    private void WipeAll(ushort alsoTell) {
        foreach (var member in _party.Members) {
            if (member.Status != PlayerStatus.Downed && member.Id != alsoTell) {
                continue;
            }

            if (member.Status == PlayerStatus.Downed) {
                _party.SetStatus(member.Id, PlayerStatus.Standing);
            }

            SendTo(member.Id, Wipe);
        }
    }

    private void OnReviveRequest(SessionPlayer from, PacketReader reader) {
        var targetId = reader.ReadUShort();
        if (_session == null || !_session.IsHost) {
            return;
        }

        var reviver = _party.Get(from.Id);
        var target = _party.Get(targetId);
        if (reviver == null || target == null || reviver.Status == PlayerStatus.Downed ||
            target.Status != PlayerStatus.Downed) {
            return;
        }

        if (from.Scene != target.BodyScene) {
            Log.Warn($"Petición de revivir de {from.Name} no válida: no está en la sala del cuerpo");
            return;
        }

        Log.Info($"{from.Name} revivió a {target.Name}");
        var scene = target.BodyScene;
        var x = target.BodyX;
        var y = target.BodyY;
        _party.SetStatus(target.Id, PlayerStatus.Standing);
        SendRevived(target.Id, scene, x, y, false, from.Name);
    }

    private void OnBenchRest(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        if (_session == null || !_session.IsHost || !HostSettings.Current.ReviveAtBench) {
            return;
        }

        var resting = _party.Get(from.Id);
        if (resting == null || resting.Status == PlayerStatus.Downed || !IsFollowedScene(scene)) {
            return;
        }

        foreach (var member in _party.Members) {
            if (member.Status != PlayerStatus.Downed) {
                continue;
            }

            Log.Info($"{member.Name} revive en el banco de {from.Name} ({scene})");
            _party.SetStatus(member.Id, PlayerStatus.Standing);
            SendRevived(member.Id, scene, x, y, true, from.Name);
        }
    }

    /// <summary>Un jugador se fue: si era caído, su cuerpo desaparece; si era el último en pie, mueren los caídos.</summary>
    private void OnPlayerLeft(SessionPlayer player) {
        if (_bodies.TryGetValue(player.Id, out var body)) {
            body.Destroy();
            _bodies.Remove(player.Id);
        }

        if (_session == null || !_session.IsHost) {
            return;
        }

        // El estado del que se fue ya lo quitó PartyState: se miran los demás
        var anyDowned = false;
        foreach (var member in _party.Members) {
            anyDowned |= member.Status == PlayerStatus.Downed;
        }

        if (!anyDowned) {
            return;
        }

        if (FindStanding(0, string.Empty) == null) {
            Log.Info("Se fue el último jugador en pie: muerte normal para los caídos");
            WipeAll(0);
            return;
        }

        RetargetFollowers(player.Id);
    }

    /// <summary>
    /// Host: un caído con el cuerpo en un sueño del que ya salieron los que estaban en pie (el que lo seguía ganó, cayó al
    /// vacío o despertó) revive junto a su compañero. Seguía a su compañero fuera del sueño, pero su cuerpo se quedaba en
    /// él: nadie podía revivirlo (solo un banco) y se quedaba mirando como caído.
    /// </summary>
    private void ReviveLeftInDreams() {
        foreach (var member in _party.Members) {
            if (member.Status != PlayerStatus.Downed || !member.BodyScene.StartsWith(DreamPrefix, StringComparison.Ordinal)) {
                continue;
            }

            var partner = _party.Get(member.Partner);
            var partnerScene = partner?.Player?.Scene;
            if (partner == null || partner.Status == PlayerStatus.Downed || !IsFollowedScene(partnerScene) ||
                AnyStandingIn(member.BodyScene)) {
                continue;
            }

            Log.Info($"{member.Name} tenía el cuerpo en {member.BodyScene} y ya no queda nadie allí: revive junto a " +
                     $"{partner.Name} ({partnerScene})");
            _party.SetStatus(member.Id, PlayerStatus.Standing);
            SendRevived(member.Id, partnerScene, float.NaN, float.NaN, false, partner.Name);
        }
    }

    private const string DreamPrefix = "Dream_";

    /// <summary>Si algún jugador en pie está en esa sala.</summary>
    private bool AnyStandingIn(string scene) {
        foreach (var member in _party.Members) {
            if (member.Status != PlayerStatus.Downed && member.Player != null && member.Player.Scene == scene) {
                return true;
            }
        }

        return false;
    }

    private void SendRevived(ushort id, string scene, float x, float y, bool full, string by) {
        if (id == _session.LocalId) {
            HandleRevived(scene, x, y, full, by);
            return;
        }

        var target = _party.Get(id)?.Player;
        if (target?.Connection == null) {
            return;
        }

        var writer = _session.BeginTo(target, Revived);
        writer.WriteString(scene);
        writer.WriteFloat(x);
        writer.WriteFloat(y);
        writer.WriteBool(full);
        writer.WriteString(by);
        _session.EndTo();
    }

    private void SendTo(ushort id, MessageType type) {
        if (id == _session.LocalId) {
            if (type == Wipe) {
                HandleWipe();
            }

            return;
        }

        var target = _party.Get(id)?.Player;
        if (target?.Connection == null) {
            return;
        }

        _session.BeginTo(target, type);
        _session.EndTo();
    }

    // =====================================================================================================
    // Caído: respuestas del host
    // =====================================================================================================

    private void OnRevived(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        var full = reader.ReadBool();
        var by = reader.ReadString(64);
        if (_session != null && !_session.IsHost && from.Id == GameSession.HostPlayerId) {
            HandleRevived(scene, x, y, full, by);
        }
    }

    private void OnWipe(SessionPlayer from, PacketReader reader) {
        if (_session != null && !_session.IsHost && from.Id == GameSession.HostPlayerId) {
            HandleWipe();
        }
    }

    private void HandleRevived(string scene, float x, float y, bool full, string by) {
        if (!_downed) {
            return;
        }

        Log.Info(full ? $"Revivido en el banco de {by}" : $"Revivido por {by}");
        if (full) {
            Notice.Show(Lang.T($"{by} se sentó en un banco: has revivido a su lado", $"{by} sat on a bench: you were revived next to them"), 5f);
        }

        _bodyScene = scene;
        // Sin posición (NaN): junto al compañero, donde esté al llegar (salió de un sueño con el cuerpo dentro)
        _reviveAtPartner = float.IsNaN(x);
        if (!_reviveAtPartner) {
            _bodyPosition = new Vector3(x, y, _bodyPosition.z);
        }

        _reviveFull = full;
        GoToBodyThen(Arrival.Revive);
    }

    private void HandleWipe() {
        // Un aviso que llega cuando ya no está caído (p. ej. justo después de revivir) se ignora
        if (!_downed) {
            return;
        }

        Log.Info("Todos caídos: muerte normal");
        GoToBodyThen(Arrival.RealDeath);
    }

    /// <summary>La salida al menú cancela la caída (sin muerte ni viaje): el juego guarda al caballero visible.</summary>
    private void CancelDowned() {
        _arrival = Arrival.None;
        _travelTarget = null;
        _arrivalPosition = null;
        RestoreDreamGate();
        if (!_downed) {
            return;
        }

        _downed = false;
        Log.Info("Salida al menú estando caído: se cancela la caída");
        try {
            ExitSpectatorCameraAreas();
            RestoreVisibleState();
        } catch (Exception e) {
            Log.Warn($"No se pudo restaurar al caballero al salir: {e.Message}");
            if (PlayerData.instance != null) {
                PlayerData.instance.isInvincible = false;
            }
        }
    }

    // =====================================================================================================
    // Cada frame (LateUpdate, después de dibujar a los remotos)
    // =====================================================================================================

    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        UpdateBench();
        if (_session.IsHost && _party != null) {
            ReviveLeftInDreams();
            RetargetUnfollowable();
        }

        UpdateBodies(scene);
        if (!HandleArrival()) {
            if (_downed) {
                UpdateDowned(scene);
            } else {
                UpdateReviving(scene);
            }
        }

        if (_downed && _arrival == Arrival.None) {
            ApplyHiddenState();
        }

        UpdateUi(scene);
    }

    /// <summary>Al sentarse en un banco, el host lo sabe (para revivir ahí a los caídos).</summary>
    private void UpdateBench() {
        var pd = PlayerData.instance;
        var hero = HeroController.instance;
        var atBench = pd != null && pd.atBench;
        if (atBench && !_wasAtBench && !_downed && hero != null && _session.CanSend) {
            var writer = _session.Begin(BenchRest);
            writer.WriteString(UnitySceneManager.GetActiveScene().name);
            writer.WriteFloat(hero.transform.position.x);
            writer.WriteFloat(hero.transform.position.y);
            _session.End();
        }

        _wasAtBench = atBench;
    }

    /// <summary>Cuándo vio este PC caer a cada jugador y hacia dónde miraba (para su cuerpo).</summary>
    private readonly Dictionary<ushort, (float At, bool FacingRight)> _falls = new();

    private const float FreshFallSeconds = 3f;

    private static readonly List<PartyState.Member> EmptyMembers = new();

    private (float At, bool FacingRight) NoteFall(ushort id, HeroController hero) {
        if (_falls.TryGetValue(id, out var fall)) {
            return fall;
        }

        var facingRight = id == _session.LocalId ? hero.cState.facingRight : _party?.Get(id) is { BodyFacingRight: true };
        fall = (Time.unscaledTime, facingRight);
        _falls[id] = fall;
        return fall;
    }

    /// <summary>Los cuerpos de los caídos de esta sala (también el propio).</summary>
    private void UpdateBodies(string scene) {
        // Cada caída se apunta en cuanto se sabe, esté donde esté este jugador (si no, al entrar después en la sala se
        // tomaría por recién caído); lo de los que ya no están caídos se olvida (la próxima caída es otra)
        var self = HeroController.instance;
        foreach (var member in _party?.Members ?? EmptyMembers) {
            if (member.Id == _session?.LocalId) {
                continue;
            }

            if (member.Status == PlayerStatus.Downed) {
                if (self != null && !_falls.ContainsKey(member.Id)) {
                    NoteFall(member.Id, self);
                }
            } else {
                _falls.Remove(member.Id);
            }
        }

        if (!_downed && _session != null) {
            _falls.Remove(_session.LocalId);
        } else if (_downed && _session != null && !_falls.ContainsKey(_session.LocalId) && HeroController.instance != null) {
            NoteFall(_session.LocalId, HeroController.instance);
        }

        if (scene != _bodiesScene) {
            ClearBodies();
            _bodiesScene = scene;
        }

        var hero = HeroController.instance;
        if (scene.Length == 0 || hero == null || _party == null) {
            return;
        }

        foreach (var member in _party.Members) {
            var shown = member.Status == PlayerStatus.Downed && member.BodyScene == scene;
            if (member.Id == _session.LocalId) {
                // El propio: lo que sabe este PC (llega antes que el estado del host)
                shown = _downed && _bodyScene == scene && _arrival != Arrival.Revive;
            }

            var exists = _bodies.TryGetValue(member.Id, out var body);
            if (shown && !exists) {
                var position = member.Id == _session.LocalId
                    ? _bodyPosition
                    : new Vector3(member.BodyX, member.BodyY, hero.transform.position.z);
                var fall = NoteFall(member.Id, hero);
                _bodies[member.Id] = new DownedBody(member.Name, position, hero, Font,
                    fresh: Time.unscaledTime - fall.At < FreshFallSeconds, facingRight: fall.FacingRight);
            } else if (!shown && exists) {
                body.Destroy();
                _bodies.Remove(member.Id);
            }
        }

        // Los de jugadores que ya no están
        if (_bodies.Count > 0) {
            _bodyScratch.Clear();
            foreach (var id in _bodies.Keys) {
                if (_party.Get(id) == null) {
                    _bodyScratch.Add(id);
                }
            }

            foreach (var id in _bodyScratch) {
                _bodies[id].Destroy();
                _bodies.Remove(id);
            }
        }

        // El nombre de quien está junto a un cuerpo (el que lo revive) y el "† Nombre" no se pisan: este sube
        foreach (var pair in _bodies) {
            var lift = 0f;
            var bodyPosition = pair.Value.Position;
            foreach (var member in _party.Members) {
                if (member.Id == pair.Key || member.Id == _session.LocalId ||
                    !_players.TryGetAvatarPosition(member.Id, out var avatar)) {
                    continue;
                }

                var dx = avatar.x - bodyPosition.x;
                var dy = avatar.y - bodyPosition.y;
                if (Mathf.Abs(dx) < LabelClearX && Mathf.Abs(dy) < LabelClearY) {
                    lift = Mathf.Max(lift, dy + LabelGap);
                }
            }

            pair.Value.SetLabelLift(lift);
        }
    }

    private void ClearBodies() {
        foreach (var body in _bodies.Values) {
            body.Destroy();
        }

        _bodies.Clear();
        _bodiesScene = string.Empty;
    }

    private Font Font {
        get {
            if (_font == null) {
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_font == null) {
                    _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
            }

            return _font;
        }
    }

    /// <summary>Cuerpos que se dibujan en esta sala (para las flechas del HUD y la skin de cada uno).</summary>
    public IReadOnlyDictionary<ushort, DownedBody> Bodies => _bodies;

    // ---- Caído: seguir al compañero ----

    private void UpdateDowned(string scene) {
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (gm == null || hero == null) {
            return;
        }

        // Con la pausa abierta no se cambia de sala (el menú se trabaría)
        var canTravel = !gm.isPaused && gm.gameState == GlobalEnums.GameState.PLAYING && !gm.IsInSceneTransition;
        var current = UnitySceneManager.GetActiveScene().name;
        if (_arrival != Arrival.None) {
            if (current == _bodyScene && !gm.IsInSceneTransition) {
                FinishArrival();
            } else if (canTravel) {
                // Llegó la orden durante otro viaje: ahora sí, al cuerpo
                TeleportToScene(_bodyScene, _bodyPosition);
            }

            return;
        }

        // Primero la animación de muerte, con la cámara en el cuerpo
        if (Time.unscaledTime - _downedAt < FollowDelay) {
            if (current == _bodyScene) {
                hero.transform.position = _bodyPosition;
            }

            return;
        }

        var local = _party?.Local;
        var partner = local != null ? _party.Get(local.Partner) : null;
        if (partner?.Player == null) {
            return;
        }

        var partnerScene = partner.Player.Scene;
        if (partnerScene == current && _players.TryGetAvatarPosition(partner.Id, out var position)) {
            _partnerSeenScene = current;
            _partnerSeenAt = position;
            hero.transform.position = new Vector3(position.x, position.y, hero.transform.position.z);
            UpdateSpectatorCamera(hero);
            return;
        }

        if (partnerScene.Length > 0 && partnerScene != current && canTravel && IsFollowedScene(partnerScene) &&
            Time.unscaledTime - _travelStartedAt > FollowCooldown) {
            TeleportToScene(partnerScene, null);
        }
    }

    private void OnPlayerSceneChanged(SessionPlayer player) {
        // Si el compañero al que sigue se va, el caído lo sigue enseguida (sin esperar al intervalo)
        var local = _party?.Local;
        if (_downed && local != null && local.Partner == player.Id) {
            _travelStartedAt = -100f;
        }
    }

    public static bool IsFollowedScene(string scene) {
        if (string.IsNullOrEmpty(scene)) {
            return false;
        }

        if (FollowedGodhomeRooms.Contains(scene)) {
            return true;
        }

        foreach (var prefix in NotFollowedPrefixes) {
            if (scene.StartsWith(prefix, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Ir a la sala del cuerpo (si no está ya) y al llegar ejecutar <paramref name="action"/>.</summary>
    private void GoToBodyThen(Arrival action) {
        _arrival = action;
        var gm = GameManager.instance;
        var busy = _travelTarget != null || gm == null || gm.IsInSceneTransition || gm.isPaused ||
                   gm.gameState != GlobalEnums.GameState.PLAYING;
        if (busy) {
            // Hay un viaje en curso: al terminarlo se sigue hacia el cuerpo
            return;
        }

        if (UnitySceneManager.GetActiveScene().name == _bodyScene) {
            FinishArrival();
            return;
        }

        TeleportToScene(_bodyScene, _reviveAtPartner ? null : _bodyPosition);
    }

    private void FinishArrival() {
        var hero = HeroController.instance;
        var action = _arrival;
        _arrival = Arrival.None;
        _downed = false;
        if (hero == null) {
            return;
        }

        if (_reviveAtPartner) {
            _reviveAtPartner = false;
            var partner = _party?.Local?.Partner ?? 0;
            var position = _players != null && _players.TryGetAvatarPosition(partner, out var avatar)
                ? avatar
                : hero.transform.position;
            _bodyPosition = new Vector3(position.x, position.y, hero.transform.position.z);
        }

        hero.transform.position = _bodyPosition;
        ExitSpectatorCameraAreas();
        RestoreVisibleState();
        if (action == Arrival.Revive) {
            hero.SetHazardRespawn(_bodyPosition, hero.cState.facingRight);
            var masks = HostSettings.Current.ReviveMasks;
            var pd = PlayerData.instance;
            hero.AddHealth(_reviveFull || masks <= 0 ? pd.maxHealth : Math.Min(masks, pd.maxHealth));
            _reviveFull = false;
            try {
                StartInvulnerableMethod?.Invoke(hero, new object[] { HostSettings.Current.ReviveInvulnerable });
            } catch (Exception e) {
                Log.Warn($"No se pudo poner la invulnerabilidad al revivir: {e.Message}");
            }

            Log.Info($"De pie otra vez con {pd.health} máscara(s)");
        } else if (action == Arrival.RealDeath) {
            RunRealDeath(hero);
        }
    }

    /// <summary>La muerte del juego (sombra, geo perdido, vuelta al banco) sin pasar por la caída.</summary>
    private static void RunRealDeath(HeroController hero) {
        if (DieMethod == null) {
            return;
        }

        _allowRealDeath = true;
        hero.StartCoroutine((IEnumerator) DieMethod.Invoke(hero, null));
    }

    // ---- Viajes de sala ----

    /// <summary>
    /// Cambia de sala. Con punto, se entra como con la Puerta de los sueños (el juego coloca al caballero en
    /// <c>dreamGateX/Y</c>, cambiado un momento por el punto), igual que <see cref="SceneTravel"/>. Sin punto (detrás del
    /// compañero), por la puerta por la que se fue el compañero. Sin puerta de entrada el juego ponía al caballero en la
    /// primera puerta de la sala y en algunas salía por ella a otra sala antes de colocarlo.
    /// </summary>
    private void TeleportToScene(string scene, Vector3? position) {
        var gm = GameManager.instance;
        if (gm == null || string.IsNullOrEmpty(scene)) {
            return;
        }

        var entry = string.Empty;
        var pd = PlayerData.instance;
        if (position.HasValue && pd != null) {
            _dreamGate.Use(pd, position.Value);
            entry = SceneTravel.DreamGateEntry;
        } else if (!position.HasValue) {
            entry = GateTowards(scene);
        }

        Log.Info($"Viajando a {scene} (entrada {(entry.Length > 0 ? entry : "cualquiera")})");
        _travelStartedAt = Time.unscaledTime;
        _travelTarget = scene;
        _arrivalPosition = position;
        gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = entry,
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            AlwaysUnloadUnusedAssets = false
        });
    }

    /// <summary>Termina un viaje pedido por el mod. Devuelve true mientras sigue en curso.</summary>
    private bool HandleArrival() {
        if (_travelTarget == null) {
            return false;
        }

        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (gm == null || hero == null || gm.IsInSceneTransition || UnitySceneManager.GetActiveScene().name != _travelTarget) {
            // El juego puede rechazar el cambio de sala: sin este límite se esperaría para siempre
            if (gm != null && !gm.IsInSceneTransition && Time.unscaledTime - _travelStartedAt > TravelTimeout) {
                Log.Warn($"El viaje a {_travelTarget} no terminó: se reintenta");
                _travelTarget = null;
                RestoreDreamGate();
                return false;
            }

            return true;
        }

        _travelTarget = null;
        RestoreDreamGate();
        if (_arrivalPosition.HasValue) {
            hero.transform.position = _arrivalPosition.Value;
        }

        // Sin puerta de entrada el caballero queda "entrando" (sin daño ni control): se cierra como al entrar en una sala
        try {
            FinishedEnteringSceneMethod?.Invoke(hero, new object[] { !_downed, false });
        } catch (Exception e) {
            Log.Warn($"FinishedEnteringScene falló: {e.Message}");
        }

        try {
            SendHeroInPositionMethod?.Invoke(hero, new object[] { true });
        } catch (Exception e) {
            Log.Warn($"SendHeroInPosition falló: {e.Message}");
        }

        var cameras = GameCameras.instance;
        if (cameras != null && cameras.cameraController != null) {
            cameras.cameraController.PositionToHero(true);
        }

        gm.FadeSceneIn();
        if (PlayerData.instance != null) {
            PlayerData.instance.isInvincible = false;
        }

        if (!_downed) {
            hero.RegainControl();
        }

        return false;
    }

    /// <summary>Repone el punto de la Puerta de los sueños del jugador.</summary>
    private void RestoreDreamGate() => _dreamGate.Restore();

    /// <summary>
    /// Entrada de <paramref name="scene"/> por la que llegó el compañero: la de la puerta de esta sala hacia esa sala más
    /// cercana a donde se le vio por última vez. Vacía si no hay ninguna (el juego elige una).
    /// </summary>
    private string GateTowards(string scene) {
        var current = UnitySceneManager.GetActiveScene().name;
        var seen = _partnerSeenScene == current;
        string best = string.Empty;
        var bestDistance = float.MaxValue;
        foreach (var gate in UnityEngine.Object.FindObjectsOfType<TransitionPoint>()) {
            if (gate == null || gate.targetScene != scene || string.IsNullOrEmpty(gate.entryPoint)) {
                continue;
            }

            var distance = seen ? ((Vector2) (gate.transform.position - _partnerSeenAt)).sqrMagnitude : 0f;
            if (distance < bestDistance) {
                bestDistance = distance;
                best = gate.entryPoint;
            }
        }

        return best;
    }

    /// <summary>Recarga la sala actual en el mismo punto (panel: "Recargar sala"). False si ahora no se puede.</summary>
    public bool ReloadCurrentRoom() {
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (_downed || _travelTarget != null || gm == null || hero == null || gm.IsInSceneTransition) {
            return false;
        }

        TeleportToScene(UnitySceneManager.GetActiveScene().name, hero.transform.position);
        return true;
    }

    /// <summary>Nombre de quien revive, del aviso de la sala (en los bits 16-31 de la clave del texto).</summary>
    private string Reviver(long promptKey) => _party.Get((ushort) ((promptKey >> 16) & 0xFFFF))?.Name ?? "?";

    // ---- Estado oculto del caído ----

    /// <summary>Caballero invisible, sin colisión ni gravedad, invencible y sin control (el juego lo devuelve al entrar en cada sala).</summary>
    private void ApplyHiddenState() {
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        if (!hero.controlReqlinquished) {
            hero.RelinquishControl();
        }

        // Al entrar en una sala el juego devuelve la entrada (FinishedEnteringScene → AcceptInput) aunque el control siga
        // cedido, y CanCast/CanDash/CanJump solo miran la entrada: el caído lanzaba hechizos invisible donde estaba el
        // compañero. RegainControl la devuelve al revivir
        hero.IgnoreInput();
        hero.hero_state = GlobalEnums.ActorStates.no_input;

        hero.AffectedByGravity(false);
        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.linearVelocity = Vector2.zero;
            body.bodyType = RigidbodyType2D.Kinematic;
        }

        var collider = hero.GetComponent<Collider2D>();
        if (collider != null) {
            collider.enabled = false;
        }

        // Se esconde con forceRenderingOff, una marca aparte que el juego no toca: lo que el juego encienda o apague
        // mientras tanto (`enabled`, un efecto de Focus o de amuleto que acaba) queda como lo dejó al revivir. Antes se
        // apagaba `enabled` y al revivir se encendía todo, también lo que el juego había apagado entre medias
        _rendererScratch.Clear();
        hero.GetComponentsInChildren(true, _rendererScratch);
        foreach (var renderer in _rendererScratch) {
            // La luz y la viñeta siguen: sin ellas el caído veía las salas oscuras sin oscuridad
            if (!renderer.forceRenderingOff && renderer != hero.heroLight && renderer != hero.vignette) {
                renderer.forceRenderingOff = true;
                _hiddenRenderers.Add(renderer);
            }
        }

        if (PlayerData.instance != null) {
            PlayerData.instance.isInvincible = true;
        }
    }

    private void RestoreVisibleState() {
        foreach (var renderer in _hiddenRenderers) {
            if (renderer != null) {
                renderer.forceRenderingOff = false;
            }
        }

        _hiddenRenderers.Clear();
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.bodyType = RigidbodyType2D.Dynamic;
            body.linearVelocity = Vector2.zero;
        }

        var collider = hero.GetComponent<Collider2D>();
        if (collider != null) {
            collider.enabled = true;
        }

        // Un caído que entró siguiendo al compañero por una puerta de abajo se queda en DROPPING_DOWN (solo lo acaba su
        // choque con el suelo, y su colisión estaba apagada) y CanTakeDamage pide WAITING_TO_TRANSITION: revivido en esa
        // sala, no recibía daño hasta cambiar de sala
        var gm = GameManager.instance;
        if (gm == null || !gm.IsInSceneTransition) {
            hero.transitionState = GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION;
            hero.cState.transitioning = false;
        }

        hero.AffectedByGravity(true);
        hero.StartAnimationControl();
        hero.RegainControl();
        if (PlayerData.instance != null) {
            PlayerData.instance.isInvincible = false;
        }
    }

    /// <summary>Las zonas de cámara se activan al tocarlas el caballero, pero el caído no choca: se emula.</summary>
    private void UpdateSpectatorCamera(HeroController hero) {
        var scene = UnitySceneManager.GetActiveScene().handle;
        if (scene != _cameraAreasScene) {
            _cameraAreasScene = scene;
            _insideCameraAreas.Clear();
            _cameraAreas = UnityEngine.Object.FindObjectsByType<CameraLockArea>(FindObjectsSortMode.None);
        }

        var heroCollider = hero.GetComponent<Collider2D>();
        if (heroCollider == null || _cameraAreas == null) {
            return;
        }

        Vector2 position = hero.transform.position;
        foreach (var area in _cameraAreas) {
            if (area == null || !area.isActiveAndEnabled) {
                continue;
            }

            var box = area.GetComponent<Collider2D>();
            var inside = box != null && box.OverlapPoint(position);
            var wasInside = _insideCameraAreas.Contains(area);
            if (inside && !wasInside) {
                _insideCameraAreas.Add(area);
                area.OnTriggerEnter2D(heroCollider);
            } else if (inside) {
                area.OnTriggerStay2D(heroCollider);
            } else if (wasInside) {
                _insideCameraAreas.Remove(area);
                area.OnTriggerExit2D(heroCollider);
            }
        }
    }

    private void ExitSpectatorCameraAreas() {
        var hero = HeroController.instance;
        var heroCollider = hero != null ? hero.GetComponent<Collider2D>() : null;
        if (heroCollider != null) {
            foreach (var area in _insideCameraAreas) {
                if (area != null) {
                    area.OnTriggerExit2D(heroCollider);
                }
            }
        }

        _insideCameraAreas.Clear();
        _cameraAreasScene = -1;
        _cameraAreas = null;
    }

    // =====================================================================================================
    // Reviviendo a otro
    // =====================================================================================================

    private void UpdateReviving(string scene) {
        var hero = HeroController.instance;
        if (hero == null || scene.Length == 0 || _bodies.Count == 0 || Time.unscaledTime < _cooldownUntil ||
            hero.cState.dead) {
            ResetProgress();
            return;
        }

        // El cuerpo más cercano a 2 unidades o menos (el propio no)
        ushort nearest = 0;
        var nearestDistance = float.MaxValue;
        foreach (var pair in _bodies) {
            if (pair.Key == _session.LocalId) {
                continue;
            }

            var distance = Vector2.Distance(hero.transform.position, pair.Value.Position);
            if (distance <= ReviveRadius && distance < nearestDistance) {
                nearest = pair.Key;
                nearestDistance = distance;
            }
        }

        if (nearest == 0) {
            ResetProgress();
            return;
        }

        if (_target != nearest) {
            ResetProgress();
            _target = nearest;
        }

        if (!IsFocusHeld()) {
            RefundSoul();
            _progress = 0f;
            _notEnoughSoul = false;
            SendProgress(false);
            return;
        }

        // El alma se cobra al empezar: si no, Focus la gastaría curando y revivir saldría gratis
        var cost = ReviveCost();
        if (_reservedSoul == 0 && cost > 0) {
            if (PlayerData.instance.MPCharge < cost) {
                _notEnoughSoul = true;
                return;
            }

            hero.TakeMP(cost);
            _reservedSoul = cost;
        }

        _notEnoughSoul = false;
        _progress += Time.deltaTime;
        SendProgress(false);
        if (_progress < HostSettings.Current.ReviveSeconds) {
            return;
        }

        // Completo: el alma reservada queda gastada
        _reservedSoul = 0;
        var target = _target;
        Log.Info($"Revivir a {_party.Get(target)?.Name ?? target.ToString()}: terminado, se pide al host");
        var writer = _session.Begin(ReviveRequest);
        writer.WriteUShort(target);
        _session.End();
        _cooldownUntil = Time.unscaledTime + 1f;
        ResetProgress();
    }

    /// <summary>
    /// Alma que cuesta revivir: la del host, pero nunca más de la que cabe en el orbe ahora. Con la sombra sin recuperar el
    /// orbe se queda en 66: con "99" nadie podía revivir a nadie hasta recuperarla.
    /// </summary>
    private static int ReviveCost() {
        var cost = HostSettings.Current.ReviveSoul;
        var pd = PlayerData.instance;
        return pd != null && pd.maxMP > 0 ? Math.Min(cost, pd.maxMP) : cost;
    }

    /// <summary>Envía a la sala cuánto lleva (10 veces por segundo, y una vez al soltar).</summary>
    private void SendProgress(bool force) {
        if (_session == null || !_session.CanSend || _target == 0) {
            return;
        }

        var active = _progress > 0f;
        var now = Time.unscaledTime;
        if (!force && (active ? now < _nextProgressSend : !_progressAnnounced)) {
            return;
        }

        _nextProgressSend = now + ProgressSendInterval;
        _progressAnnounced = active;
        var writer = _session.Begin(ReviveProgress);
        writer.WriteUShort(_target);
        writer.WriteByte((byte) Mathf.Clamp(Mathf.RoundToInt(_progress / HostSettings.Current.ReviveSeconds * 255f), 0, 255));
        _session.End();
    }

    private void OnProgress(SessionPlayer from, PacketReader reader) {
        var target = reader.ReadUShort();
        var progress = reader.ReadByte() / 255f;
        _revivedBy = from.Id;
        _revivingTarget = target;
        _revivingProgress = progress;
        _revivingAt = progress > 0f ? Time.unscaledTime : -100f;
    }

    private void ResetProgress() {
        if (_progressAnnounced) {
            _progress = 0f;
            SendProgress(true);
        }

        RefundSoul();
        _target = 0;
        _progress = 0f;
        _notEnoughSoul = false;
    }

    private void RefundSoul() {
        if (_reservedSoul == 0) {
            return;
        }

        var hero = HeroController.instance;
        if (hero != null) {
            hero.AddMPCharge(_reservedSoul);
        }

        _reservedSoul = 0;
    }

    /// <summary>Modo de prueba: hasta cuándo se simula Focus pulsado.</summary>
    private float _testFocusUntil;

    private bool IsFocusHeld() {
        if (Time.unscaledTime < _testFocusUntil) {
            return true;
        }

        var input = InputHandler.Instance;
        return input != null && input.inputActions != null &&
               (input.inputActions.focus.IsPressed || input.inputActions.cast.IsPressed);
    }

    // =====================================================================================================
    // Avisos y pantalla
    // =====================================================================================================

    private void OnStatusChanged(PartyState.Member member, PlayerStatus previous) {
        if (_session == null || member.Id == _session.LocalId) {
            return;
        }

        if (member.Status == PlayerStatus.Downed && previous != PlayerStatus.Downed) {
            var entry = _vitals?.Get(member.Id);
            var zone = entry != null ? Zones.Name(entry.Zone) : member.BodyScene;
            if (HostSettings.Current.ShowNotices) {
                Notice.Show(Lang.T($"{member.Name} cayó en {zone}\nVe a su cuerpo y mantén Focus para revivirlo", $"{member.Name} went down in {zone}\nGo to their body and hold Focus to revive them"), 6f);
            }
        }
    }

    private void EnsureUi() {
        if (_status != null) {
            return;
        }

        var root = HudCanvas.Root;
        _status = HudCanvas.NewText(Lang.T("Caído", "Down"), root, 40, TextAnchor.MiddleCenter, title: true);
        var statusRect = _status.rectTransform;
        statusRect.anchorMin = statusRect.anchorMax = new Vector2(0.5f, 1f);
        statusRect.anchoredPosition = new Vector2(0f, -150f);
        statusRect.sizeDelta = new Vector2(1600f, 60f);

        _prompt = HudCanvas.NewGroup("Revivir");
        _promptText = HudCanvas.NewText("Texto", _prompt, 30, TextAnchor.MiddleCenter);
        _promptText.rectTransform.anchoredPosition = new Vector2(0f, 30f);
        _promptBar = HudCanvas.NewBar("Progreso", _prompt, new Vector2(180f, 16f), new Color(0.9f, 0.9f, 1f, 0.95f),
            out _promptBarRoot);
        _promptBarRoot.anchoredPosition = Vector2.zero;
        SetUiVisible(false);
    }

    private void SetUiVisible(bool visible) {
        if (_status != null && !visible) {
            _status.gameObject.SetActive(false);
            _prompt.gameObject.SetActive(false);
            _statusKey = 0;
            _promptKey = 0;
        }
    }

    private void UpdateUi(string scene) {
        EnsureUi();
        var gm = GameManager.instance;
        var playing = scene.Length > 0 && gm != null && !gm.isPaused;

        // Arriba: el caído sabe quién puede revivirlo y si lo están reviviendo. El texto solo se rehace al cambiar
        var statusKey = 0;
        if (_downed && playing && _arrival == Arrival.None) {
            var local = _party?.Local;
            var beingRevived = _revivingTarget == _session.LocalId && Time.unscaledTime - _revivingAt < BeingRevivedTimeout;
            statusKey = beingRevived ? 0x20000 | _revivedBy : local != null && local.Partner != 0 ? 0x10000 | local.Partner : 1;
        }

        if (statusKey != _statusKey) {
            _statusKey = statusKey;
            _status.gameObject.SetActive(statusKey != 0);
            if (statusKey != 0) {
                var who = _party.Get((ushort) (statusKey & 0xFFFF))?.Name ?? Lang.T("Tu compañero", "Your partner");
                _status.text = (statusKey & 0x20000) != 0 ? Lang.T($"{who} te está reviviendo", $"{who} is reviving you")
                    : (statusKey & 0x10000) != 0 ? Lang.T($"Caído — {who} puede revivirte", $"Down — {who} can revive you")
                    : Lang.T("Caído…", "Down…");
            }
        }

        // Junto a un cuerpo: qué hacer y cuánto lleva (el propio o el que llega de quien revive)
        var camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
        long promptKey = 0;
        var fraction = 0f;
        Vector3 at = default;
        if (playing && camera != null && !_downed) {
            if (_target != 0 && _bodies.TryGetValue(_target, out var body)) {
                promptKey = (_notEnoughSoul ? 2L : 1L) << 32 | _target;
                fraction = _progress / HostSettings.Current.ReviveSeconds;
                at = body.Position;
            } else if (Time.unscaledTime - _revivingAt < BeingRevivedTimeout &&
                       _bodies.TryGetValue(_revivingTarget, out var watched)) {
                promptKey = 3L << 32 | (long) _revivedBy << 16 | _revivingTarget;
                fraction = _revivingProgress;
                at = watched.Position;
            }
        }

        var point = Vector2.zero;
        if (promptKey == 0 || !HudCanvas.WorldToCanvas(camera, at + new Vector3(0f, 2.4f, 0f), out point)) {
            if (_promptKey != 0) {
                _promptKey = 0;
                _prompt.gameObject.SetActive(false);
            }

            return;
        }

        if (promptKey != _promptKey) {
            _promptKey = promptKey;
            _prompt.gameObject.SetActive(true);
            var kind = promptKey >> 32;
            var target = _party.Get((ushort) (promptKey & 0xFFFF))?.Name ?? "?";
            _promptText.text = kind switch {
                2 => Lang.T($"Necesitas {ReviveCost()} de alma para revivir a {target}", $"You need {ReviveCost()} soul to revive {target}"),
                3 => Lang.T($"{Reviver(promptKey)} está reviviendo a {target}", $"{Reviver(promptKey)} is reviving {target}"),
                _ => Lang.T($"Mantén Focus para revivir a {target}", $"Hold Focus to revive {target}")
            };
        }

        _prompt.anchoredPosition = point;
        var showBar = fraction > 0f;
        if (_promptBarRoot.gameObject.activeSelf != showBar) {
            _promptBarRoot.gameObject.SetActive(showBar);
        }

        HudCanvas.SetFill(_promptBar, fraction);
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    /// <summary>Simula mantener Focus unos segundos.</summary>
    public void TestFocus(float seconds) => _testFocusUntil = Time.unscaledTime + seconds;

    /// <summary>Posición del cuerpo de otro jugador en esta sala (para ir a él).</summary>
    public bool TestBodyPosition(out Vector3 position) {
        foreach (var pair in _bodies) {
            if (pair.Key != _session?.LocalId) {
                position = pair.Value.Position;
                return true;
            }
        }

        position = default;
        return false;
    }

    public string Describe() =>
        $"caído={_downed} cuerpo={_bodyScene}({_bodyPosition.x:0.0},{_bodyPosition.y:0.0}) llegada={_arrival} " +
        $"viaje={_travelTarget ?? "-"} revive={_target}:{_progress:0.00} sin alma={_notEnoughSoul} cuerpos={_bodies.Count} " +
        $"estado=[{_party?.DescribeAll()}]";

    private static IEnumerator EmptyRoutine() {
        yield break;
    }
}
