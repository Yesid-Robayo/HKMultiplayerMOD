using System;
using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Testing;
using HKMultiplayer.Util;
using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HKMultiplayer.Game.Players;

/// <summary>Qué hizo el jugador (primer byte de <see cref="MessageType.PlayerEvent"/>).</summary>
internal enum EffectKind : byte {
    /// <summary>Una FSM de acciones del caballero entró en un estado con efectos (hechizos, artes, Crystal Heart...).</summary>
    FsmState = 1,

    /// <summary>Tajo del aguijón.</summary>
    Slash = 2,

    Dash = 3,
    DoubleJump = 4,
    WallJump = 5,

    /// <summary>Carga de las artes del aguijón (empieza, se completa o termina).</summary>
    NailCharge = 6,

    /// <summary>Recibió un golpe.</summary>
    Hit = 7,

    /// <summary>Rayo de Grubberfly's Elegy (cuál, giro y escala).</summary>
    Beam = 8,

    /// <summary>Nube al curarse con Spore Shroom o Defender's Crest (cuál, dónde y tamaño).</summary>
    Cloud = 9,

    /// <summary>Una mosca de Flukenest (cuál, dónde, velocidad, giro y tamaño).</summary>
    Fluke = 10,

    /// <summary>Un efecto suelto del caballero sacado del pool (dash sombra, aterrizaje fuerte...): cuál, dónde, giro y escala.</summary>
    Puff = 11,

    /// <summary>Polvo al correr: empieza (1) o para (0).</summary>
    Run = 12
}

/// <summary>Amuletos que cambian cómo se ve un efecto (viajan en cada evento, en un byte).</summary>
internal static class EffectCharms {
    public const byte MarkOfPride = 1;
    public const byte Longnail = 2;
    public const byte Fury = 4;
    public const byte ShamanStone = 8;
    public const byte Flukenest = 16;

    /// <summary>
    /// El último golpe se llevó una máscara azul (<c>PlayerData.damagedBlue</c>): su efecto de daño salta de "Gen" a
    /// "Blue Effects" antes de la grieta, las partículas y los tajos oscuros, que entonces no salen.
    /// </summary>
    public const byte DamagedBlue = 32;

    /// <summary>Los del jugador local ahora.</summary>
    public static byte Local() {
        var data = PlayerData.instance;
        if (data == null) {
            return 0;
        }

        var charms = 0;
        charms |= data.equippedCharm_13 ? MarkOfPride : 0;
        charms |= data.equippedCharm_18 ? Longnail : 0;
        charms |= data.equippedCharm_6 && data.health == 1 ? Fury : 0;
        charms |= data.equippedCharm_19 ? ShamanStone : 0;
        charms |= data.equippedCharm_11 ? Flukenest : 0;
        charms |= data.damagedBlue ? DamagedBlue : 0;
        return (byte) charms;
    }
}

/// <summary>Un efecto de un jugador, tal como viaja (y espera en la cola hasta que su avatar llega a ese momento).</summary>
internal struct EffectEvent {
    public const byte DashShadow = 1;
    public const byte DashSharp = 2;
    public const byte DashDown = 4;

    public EffectKind Kind;

    /// <summary>Tick del remitente en que pasó (el mismo reloj que su estado).</summary>
    public uint Tick;

    /// <summary>FSM, tajo, opciones del dash o fase de la carga.</summary>
    public byte A;

    /// <summary>Estado de la FSM.</summary>
    public ushort B;

    public byte Charms;

    /// <summary>Dash: dónde y con qué giro puso el caballero su ráfaga.</summary>
    public float X;

    public float Y;
    public float Angle;

    /// <summary>Velocidad con la que sale una mosca de Flukenest.</summary>
    public float VX;

    public float VY;

    /// <summary>Escala del objeto (con signo si va volteado) y giro por segundo (moscas de Defender's Crest).</summary>
    public float Scale;

    public float Spin;

    /// <summary>Cuándo llegó (instante local), por si su avatar no llega nunca a ese tick.</summary>
    public double Arrival;

    public void Write(PacketWriter writer) {
        writer.WriteByte((byte) Kind);
        writer.WriteUInt(Tick);
        switch (Kind) {
            case EffectKind.Beam:
                writer.WriteByte(A);
                writer.WriteFloat(Angle);
                writer.WriteFloat(Scale);
                break;
            case EffectKind.Cloud:
                writer.WriteByte(A);
                writer.WriteFloat(X);
                writer.WriteFloat(Y);
                writer.WriteFloat(Scale);
                break;
            case EffectKind.Puff:
                writer.WriteByte(A);
                writer.WriteFloat(X);
                writer.WriteFloat(Y);
                writer.WriteFloat(Angle);
                writer.WriteFloat(Scale);
                break;
            case EffectKind.Fluke:
                writer.WriteByte(A);
                writer.WriteFloat(X);
                writer.WriteFloat(Y);
                writer.WriteFloat(VX);
                writer.WriteFloat(VY);
                writer.WriteFloat(Angle);
                writer.WriteFloat(Scale);
                writer.WriteFloat(Spin);
                break;
            case EffectKind.FsmState:
                writer.WriteByte(A);
                writer.WriteUShort(B);
                writer.WriteByte(Charms);
                break;
            case EffectKind.Slash:
                writer.WriteByte(A);
                writer.WriteByte(Charms);
                break;
            case EffectKind.Dash:
                writer.WriteByte(A);
                writer.WriteFloat(X);
                writer.WriteFloat(Y);
                writer.WriteFloat(Angle);
                break;
            case EffectKind.NailCharge:
            case EffectKind.Run:
            case EffectKind.Hit:
                writer.WriteByte(A);
                break;
        }
    }

    public void Read(PacketReader reader) {
        Kind = (EffectKind) reader.ReadByte();
        Tick = reader.ReadUInt();
        A = 0;
        B = 0;
        Charms = 0;
        X = Y = Angle = VX = VY = Spin = 0f;
        Scale = 1f;
        switch (Kind) {
            case EffectKind.Beam:
                A = reader.ReadByte();
                Angle = reader.ReadFloat();
                Scale = reader.ReadFloat();
                break;
            case EffectKind.Cloud:
                A = reader.ReadByte();
                X = reader.ReadFloat();
                Y = reader.ReadFloat();
                Scale = reader.ReadFloat();
                break;
            case EffectKind.Puff:
                A = reader.ReadByte();
                X = reader.ReadFloat();
                Y = reader.ReadFloat();
                Angle = reader.ReadFloat();
                Scale = reader.ReadFloat();
                break;
            case EffectKind.Fluke:
                A = reader.ReadByte();
                X = reader.ReadFloat();
                Y = reader.ReadFloat();
                VX = reader.ReadFloat();
                VY = reader.ReadFloat();
                Angle = reader.ReadFloat();
                Scale = reader.ReadFloat();
                Spin = reader.ReadFloat();
                break;
            case EffectKind.FsmState:
                A = reader.ReadByte();
                B = reader.ReadUShort();
                Charms = reader.ReadByte();
                break;
            case EffectKind.Slash:
                A = reader.ReadByte();
                Charms = reader.ReadByte();
                break;
            case EffectKind.Dash:
                A = reader.ReadByte();
                X = reader.ReadFloat();
                Y = reader.ReadFloat();
                Angle = reader.ReadFloat();
                break;
            case EffectKind.NailCharge:
            case EffectKind.Run:
            case EffectKind.Hit:
                A = reader.ReadByte();
                break;
        }
    }
}

/// <summary>
/// Efectos del otro jugador: cuando el caballero local hace algo que se ve (tajo, hechizo, dash, curación...), se envía
/// un evento pequeño a los de la sala, y cada uno lo reproduce sobre el avatar de quien lo hizo con los efectos de su
/// propio caballero (<see cref="RemoteEffects"/>). Solo visual: el daño ya va por los golpes directos, así que los
/// efectos reproducidos no dañan, no rompen nada, no gastan alma ni tocan al caballero local.
/// <para>
/// Cada evento lleva el tick en que pasó y espera a que el avatar (que se dibuja un poco en el pasado) llegue a ese
/// momento: así el tajo sale cuando el avatar hace el tajo, no antes. Sin memoria nueva por evento: colas fijas.
/// </para>
/// </summary>
internal sealed class PlayerEffects {
    /// <summary>Eventos que esperan su momento por jugador; si se llena, sale el más viejo ya (una bola de Flukenest son 9-16).</summary>
    private const int QueueSize = 64;

    /// <summary>Objetos de amuletos creados en este frame, que se envían al final (ya con su velocidad, giro y escala).</summary>
    private const int PendingSize = 32;

    /// <summary>Lo más que espera un evento a que su avatar llegue a su tick.</summary>
    private const double MaxWait = 0.5;

    private sealed class Remote {
        public SessionPlayer Player;
        public RemoteEffects Effects;
        public readonly EffectEvent[] Queue = new EffectEvent[QueueSize];
        public int Count;
    }

    private static PlayerEffects _active;
    private static bool _hooked;

    /// <summary>Acompañantes de los jugadores (no fiable: solo vale el último).</summary>
    public const MessageType PlayerCompanions = MessageType.PlayerCompanions;

    private readonly Dictionary<ushort, Remote> _remotes = new();
    private readonly MessageHandler _onEvent;
    private readonly MessageHandler _onCompanions;
    private readonly LocalCompanions _localCompanions = new();
    private GameSession _session;
    private PlayerSync _players;
    private HeroEffectTemplates _templates;

    /// <summary>Caballero con el que no se pudieron preparar los efectos (no se reintenta cada frame).</summary>
    private HeroController _failedHero;

    private GameObject _parking;
    private EffectEvent _incoming;

    /// <summary>Carga de las artes del aguijón enviada la última vez (0 nada, 1 cargando, 2 cargada).</summary>
    private byte _chargeStage;

    /// <summary>El polvo al correr del caballero local mientras lo lleva pegado (al soltarlo, paró de correr).</summary>
    private GameObject _runEffect;

    private readonly GameObject[] _pendingObjects = new GameObject[PendingSize];
    private readonly CharmTemplates.SpawnKind[] _pendingKinds = new CharmTemplates.SpawnKind[PendingSize];
    private readonly byte[] _pendingIndexes = new byte[PendingSize];
    private int _pendingCount;

    public PlayerEffects() {
        _onEvent = OnEvent;
        _onCompanions = OnCompanions;
    }

    /// <summary>Eventos enviados y reproducidos (modo de prueba).</summary>
    public int Sent { get; private set; }

    public int Received { get; private set; }

    /// <summary>
    /// Eventos reproducidos sobre el avatar de su dueño. Se cuentan aquí y no en su copia de efectos: la copia se rehace
    /// (otra partida, una pieza perdida) y su cuenta se perdería con ella.
    /// </summary>
    public int Played { get; private set; }

    /// <summary>Eventos sin reproducir porque su dueño ya no se dibujaba en la sala (cambio de sala, caído).</summary>
    public int Discarded { get; private set; }

    /// <summary>Eventos que esperan ahora en las colas a que su avatar llegue a su tick.</summary>
    public int Waiting {
        get {
            var waiting = 0;
            foreach (var remote in _remotes.Values) {
                waiting += remote.Count;
            }

            return waiting;
        }
    }

    /// <summary>Pone a cero los contadores (modo de prueba: contar una acción sola).</summary>
    public void ResetCounters() {
        Sent = Received = Played = Discarded = 0;
    }

    /// <summary>Lo encendido de los amuletos de cada compañero (modo de prueba).</summary>
    public string DescribeCharms() {
        var text = string.Empty;
        foreach (var remote in _remotes.Values) {
            text += $"{remote.Player.Name}:{remote.Effects?.DescribeCharms() ?? " sin copia"} ";
        }

        return text;
    }

    public void Attach(GameSession session, PlayerSync players) {
        _session = session;
        _players = players;
        _active = this;
        session.On(MessageType.PlayerEvent, _onEvent);
        session.On(PlayerCompanions, _onCompanions);
        InstallHooks();
    }

    public void Detach() {
        if (_session != null) {
            _session.On(MessageType.PlayerEvent, null);
            _session.On(PlayerCompanions, null);
            _session = null;
        }

        if (_active == this) {
            _active = null;
        }

        foreach (var remote in _remotes.Values) {
            remote.Effects?.Destroy();
        }

        _remotes.Clear();
        _templates?.Destroy();
        _templates = null;
        _localCompanions.SetTemplates(null);
        if (_parking != null) {
            Object.Destroy(_parking);
            _parking = null;
        }

        _chargeStage = 0;
    }

    /// <summary>Plantillas del caballero local; se rehacen si el caballero es otro (se cargó otra partida).</summary>
    private HeroEffectTemplates Templates() {
        var hero = HeroController.instance;
        if (hero == null) {
            return null;
        }

        if (_templates is { IsValid: true } && _templates.Hero == hero) {
            return _templates;
        }

        if (hero == _failedHero) {
            return null;
        }

        // Las copias de los compañeros eran del caballero anterior
        foreach (var remote in _remotes.Values) {
            remote.Effects?.Destroy();
            remote.Effects = null;
        }

        _templates?.Destroy();
        try {
            _templates = new HeroEffectTemplates(hero);
        } catch (Exception e) {
            Log.Error($"Efectos: no se pudieron preparar los del caballero: {e}");
            _templates = null;
            _failedHero = hero;
        }

        _localCompanions.SetTemplates(_templates?.Companions);

        return _templates;
    }

    // =====================================================================================================
    // Jugador local → red
    // =====================================================================================================

    /// <summary>
    /// Lo que se mira cada frame (la carga de las artes del aguijón). Las plantillas se preparan en cuanto hay
    /// caballero, al cargar la partida: así se copian sus efectos en reposo (no a mitad de un hechizo). Llamar en
    /// LateUpdate, solo jugando en una sala.
    /// </summary>
    public void UpdateLocal() {
        var hero = HeroController.instance;
        if (hero == null || _session == null || Templates() == null) {
            _pendingCount = 0;
            return;
        }

        SendPendingSpawns();
        if (CanSend()) {
            _localCompanions.Send(_session, _players.LocalSceneHash);
        }

        // Paró de correr: el caballero suelta su polvo (y su FSM deja de emitir)
        if (_runEffect != null && (!_runEffect.activeInHierarchy || _runEffect.transform.parent != hero.transform)) {
            _runEffect = null;
            Send(new EffectEvent { Kind = EffectKind.Run, A = 0 });
        }

        var stage =hero.artChargedEffect != null && hero.artChargedEffect.activeSelf ? (byte) 2 :
            hero.artChargeEffect != null && hero.artChargeEffect.activeSelf ? (byte) 1 : (byte) 0;
        if (stage == _chargeStage) {
            return;
        }

        _chargeStage = stage;
        Send(new EffectEvent { Kind = EffectKind.NailCharge, A = stage });
    }

    private bool CanSend() => _session != null && _session.CanSend && _players.AnyRemoteInScene();

    private void Send(EffectEvent effect) {
        if (!CanSend()) {
            return;
        }

        effect.Tick = (uint) (Time.unscaledTimeAsDouble * NetConstants.TickRate);
        var writer = _session.Begin(MessageType.PlayerEvent);
        effect.Write(writer);
        _session.End();
        Sent++;
        if (TestDriver.IsEnabled()) {
            Log.Info($"[TEST] efecto enviado {effect.Kind} {DescribeEvent(effect)}");
        }
    }

    /// <summary>
    /// Una FSM cualquiera cambió de estado: si es una de acciones del caballero y el estado tiene efectos, se envía. Las
    /// plantillas existen desde que se dibuja algún compañero (sin compañero en la sala no hay a quién enviar).
    /// </summary>
    private void OnFsmState(Fsm fsm, FsmState state) {
        var templates = _templates;
        if (templates == null || templates.Hero == null) {
            return;
        }

        var fsms = templates.HeroFsms;
        for (var i = 0; i < fsms.Length; i++) {
            if (fsms[i] == null || fsms[i].Fsm != fsm) {
                continue;
            }

            if (!CanSend() || templates.Hero != HeroController.instance) {
                return;
            }

            var index = Array.IndexOf(fsm.States, state);
            if (index >= 0 && index < templates.EffectStates[i].Length && templates.EffectStates[i][index]) {
                Send(new EffectEvent {
                    Kind = EffectKind.FsmState, A = (byte) i, B = (ushort) index, Charms = EffectCharms.Local()
                });
            }

            return;
        }
    }

    private void OnSlash(HeroController hero, NailSlash slash) {
        byte index;
        if (slash == hero.normalSlash) {
            index = 0;
        } else if (slash == hero.alternateSlash) {
            index = 1;
        } else if (slash == hero.upSlash) {
            index = 2;
        } else if (slash == hero.downSlash) {
            index = 3;
        } else if (slash == hero.wallSlash) {
            index = 4;
        } else {
            return;
        }

        Send(new EffectEvent { Kind = EffectKind.Slash, A = index, Charms = EffectCharms.Local() });
    }

    private void OnDash(HeroController hero) {
        var flags = 0;
        flags |= hero.cState.shadowDashing ? EffectEvent.DashShadow : 0;
        flags |= PlayerData.instance != null && PlayerData.instance.equippedCharm_16 ? EffectEvent.DashSharp : 0;
        flags |= hero.dashingDown ? EffectEvent.DashDown : 0;
        var burst = hero.dashBurst != null ? hero.dashBurst.transform : null;
        Send(new EffectEvent {
            Kind = EffectKind.Dash,
            A = (byte) flags,
            X = burst != null ? burst.localPosition.x : 0f,
            Y = burst != null ? burst.localPosition.y : 0f,
            Angle = burst != null ? burst.localEulerAngles.z : 0f
        });
    }

    /// <summary>
    /// Algo salió del pool del juego: si es de un amuleto del caballero (rayo, nube, mosca), se apunta para enviarlo al
    /// final del frame. Antes no: quien lo crea le pone después su velocidad, giro y escala.
    /// </summary>
    private void OnPoolSpawn(GameObject prefab, GameObject spawned) {
        _localCompanions.OnPoolSpawn(prefab, spawned);
        var charms = _templates?.Charms;
        if (charms != null && spawned != null && prefab == charms.RunEffectsPrefab) {
            // Empieza a correr: lo lleva pegado hasta que para
            _runEffect = spawned;
            Send(new EffectEvent { Kind = EffectKind.Run, A = 1 });
            return;
        }
        if (charms == null || spawned == null || _pendingCount == PendingSize ||
            !charms.TryGetSpawn(prefab, out var kind, out var index) || !CanSend()) {
            return;
        }

        _pendingObjects[_pendingCount] = spawned;
        _pendingKinds[_pendingCount] = kind;
        _pendingIndexes[_pendingCount] = (byte) index;
        _pendingCount++;
    }

    private void SendPendingSpawns() {
        for (var i = 0; i < _pendingCount; i++) {
            var spawned = _pendingObjects[i];
            _pendingObjects[i] = null;
            if (spawned == null) {
                continue;
            }

            var transform = spawned.transform;
            var position = transform.position;
            var effect = new EffectEvent { A = _pendingIndexes[i], Scale = 1f };
            switch (_pendingKinds[i]) {
                case CharmTemplates.SpawnKind.Beam:
                    effect.Kind = EffectKind.Beam;
                    effect.Angle = transform.localEulerAngles.z;
                    effect.Scale = transform.localScale.y;
                    break;
                case CharmTemplates.SpawnKind.Cloud:
                    effect.Kind = EffectKind.Cloud;
                    effect.X = position.x;
                    effect.Y = position.y;
                    effect.Scale = transform.localScale.x;
                    break;
                case CharmTemplates.SpawnKind.Puff:
                    effect.Kind = EffectKind.Puff;
                    effect.X = position.x;
                    effect.Y = position.y;
                    effect.Angle = transform.eulerAngles.z;
                    effect.Scale = transform.localScale.x;
                    break;
                case CharmTemplates.SpawnKind.Fluke:
                    effect.Kind = EffectKind.Fluke;
                    effect.X = position.x;
                    effect.Y = position.y;
                    effect.Angle = transform.eulerAngles.z;
                    effect.Scale = transform.localScale.x;
                    var body = spawned.GetComponent<Rigidbody2D>();
                    if (body != null) {
                        var velocity = body.linearVelocity;
                        effect.VX = velocity.x;
                        effect.VY = velocity.y;
                        effect.Spin = body.angularVelocity;
                    }

                    break;
                default:
                    continue;
            }

            Send(effect);
        }

        _pendingCount = 0;
    }

    // =====================================================================================================
    // Red → compañeros
    // =====================================================================================================

    private void OnEvent(SessionPlayer from, PacketReader reader) {
        _incoming.Read(reader);
        Received++;
        if (!_remotes.TryGetValue(from.Id, out var remote)) {
            remote = _remotes[from.Id] = new Remote { Player = from };
        }

        _incoming.Arrival = Time.unscaledTimeAsDouble;
        if (remote.Count == QueueSize) {
            // Cola llena (no debería pasar): el más viejo sale ya
            PlayNow(remote, remote.Queue[0], "cola llena");
            Array.Copy(remote.Queue, 1, remote.Queue, 0, QueueSize - 1);
            remote.Count--;
        }

        remote.Queue[remote.Count++] = _incoming;
    }

    /// <summary>Reproduce un evento sobre el avatar de su dueño y lo cuenta (reproducido o descartado).</summary>
    private void PlayNow(Remote remote, in EffectEvent effect, string why) {
        if (remote.Effects != null && remote.Effects.Play(effect)) {
            Played++;
            if (TestDriver.IsEnabled()) {
                Log.Info($"[TEST] efecto {effect.Kind} de {remote.Player.Name} {DescribeEvent(effect)}{why}");
            }

            return;
        }

        Discarded++;
        if (TestDriver.IsEnabled()) {
            Log.Info($"[TEST] efecto {effect.Kind} de {remote.Player.Name} descartado: sin avatar en la sala{why}");
        }
    }

    private void OnCompanions(SessionPlayer from, PacketReader reader) {
        var tick = reader.ReadUInt();
        var sceneHash = reader.ReadUShort();
        if (sceneHash != _players.LocalSceneHash || sceneHash == 0 || !_remotes.TryGetValue(from.Id, out var remote)) {
            return;
        }

        remote.Effects?.Companions?.Read(reader, tick);
    }

    /// <summary>
    /// El avatar de un jugador se acaba de dibujar en el tick <paramref name="renderTick"/> de su reloj: se pegan sus
    /// efectos y salen los eventos que ya tocan. Llamar después de <see cref="RemoteAvatar.Apply"/>.
    /// </summary>
    public void UpdateRemote(SessionPlayer player, RemoteAvatar avatar, double renderTick) {
        if (!_remotes.TryGetValue(player.Id, out var remote)) {
            remote = _remotes[player.Id] = new Remote { Player = player };
        }

        if (remote.Effects is { Broken: true }) {
            remote.Effects.Destroy();
            remote.Effects = null;
        }

        if (remote.Effects == null) {
            var templates = Templates();
            if (templates == null) {
                return;
            }

            if (_parking == null) {
                _parking = new GameObject("HKMultiplayer efectos de compañeros");
                Object.DontDestroyOnLoad(_parking);
            }

            remote.Effects = new RemoteEffects(templates, player.Name, _parking.transform);
        }

        remote.Effects.AttachTo(avatar);

        // En orden de llegada (el canal es fiable y ordenado): sale cada uno al llegar el avatar a su tick
        var now = Time.unscaledTimeAsDouble;
        var played = 0;
        while (played < remote.Count) {
            ref var effect = ref remote.Queue[played];
            if (effect.Tick > renderTick && now - effect.Arrival < MaxWait) {
                break;
            }

            PlayNow(remote, effect, effect.Tick > renderTick ? " (tarde)" : string.Empty);
            played++;
        }

        if (played > 0) {
            Array.Copy(remote.Queue, played, remote.Queue, 0, remote.Count - played);
            remote.Count -= played;
        }

        remote.Effects.Update(Time.deltaTime);
        remote.Effects.Companions?.Update(renderTick);
    }

    /// <summary>Detalle de un evento para el registro del modo de prueba (FSM y estado, o qué objeto).</summary>
    private string DescribeEvent(in EffectEvent effect) {
        if (effect.Kind == EffectKind.FsmState && _templates != null && effect.A < _templates.HeroFsms.Length &&
            _templates.HeroFsms[effect.A] is { } fsm && effect.B < fsm.FsmStates.Length) {
            return $"{fsm.FsmName}/{fsm.FsmStates[effect.B].Name}";
        }

        return effect.Kind is EffectKind.Beam or EffectKind.Cloud or EffectKind.Fluke or EffectKind.Puff
            ? $"#{effect.A} ({effect.X:0.0},{effect.Y:0.0}) v=({effect.VX:0.0},{effect.VY:0.0}) escala={effect.Scale:0.00}"
            : string.Empty;
    }

    /// <summary>El avatar de un jugador se va a destruir (cambio de sala): sus efectos se aparcan y su cola se vacía.</summary>
    public void Release(ushort playerId) {
        if (_remotes.TryGetValue(playerId, out var remote)) {
            remote.Effects?.Detach();
            if (remote.Count > 0) {
                Discarded += remote.Count;
                if (TestDriver.IsEnabled()) {
                    Log.Info($"[TEST] {remote.Count} efectos de {remote.Player.Name} descartados: su avatar dejó la sala");
                }
            }

            remote.Count = 0;
        }
    }

    public void Forget(ushort playerId) {
        if (_remotes.TryGetValue(playerId, out var remote)) {
            remote.Effects?.Destroy();
            _remotes.Remove(playerId);
        }
    }

    // =====================================================================================================
    // Ganchos al caballero
    // =====================================================================================================

    private static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;

        // Estados de las FSM de acciones del caballero (hechizos, artes, Crystal Heart, curación, aguijón onírico)
        On.HutongGames.PlayMaker.Fsm.SwitchState += (orig, self, toState) => {
            orig(self, toState);
            if (toState != null) {
                _active?.OnFsmState(self, toState);
            }
        };

        // Lo que sacan los amuletos del pool del juego (rayos de Grubberfly, nubes al curarse, moscas de Flukenest)
        On.ObjectPool.Spawn_GameObject_Transform_Vector3_Quaternion += (orig, prefab, parent, position, rotation) => {
            var spawned = orig(prefab, parent, position, rotation);
            _active?.OnPoolSpawn(prefab, spawned);
            return spawned;
        };

        On.NailSlash.StartSlash += (orig, self) => {
            orig(self);
            var hero = HeroController.instance;
            if (_active != null && hero != null) {
                _active.OnSlash(hero, self);
            }
        };

        On.HeroController.HeroDash += (orig, self) => {
            orig(self);
            if (_active != null && self == HeroController.instance) {
                _active.OnDash(self);
            }
        };

        On.HeroController.DoDoubleJump += (orig, self) => {
            orig(self);
            if (self == HeroController.instance) {
                _active?.Send(new EffectEvent { Kind = EffectKind.DoubleJump });
            }
        };

        On.HeroController.DoWallJump += (orig, self) => {
            orig(self);
            if (self == HeroController.instance) {
                _active?.Send(new EffectEvent { Kind = EffectKind.WallJump });
            }
        };

        On.HeroController.TakeDamage += (orig, self, source, side, amount, hazard) => {
            var data = PlayerData.instance;
            var before = data != null ? data.health + data.healthBlue : 0;
            orig(self, source, side, amount, hazard);
            var after = data != null ? data.health + data.healthBlue : 0;
            if (after < before && self == HeroController.instance) {
                // Con A = 1 se llevó una máscara azul: sin las partículas del golpe normal
                _active?.Send(new EffectEvent { Kind = EffectKind.Hit, A = (byte) (data.damagedBlue ? 1 : 0) });
            }
        };
    }
}
