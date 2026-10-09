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
    Hit = 7
}

/// <summary>Amuletos que cambian cómo se ve un efecto (viajan en cada evento, en un byte).</summary>
internal static class EffectCharms {
    public const byte MarkOfPride = 1;
    public const byte Longnail = 2;
    public const byte Fury = 4;
    public const byte ShamanStone = 8;
    public const byte Flukenest = 16;

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

    /// <summary>Cuándo llegó (instante local), por si su avatar no llega nunca a ese tick.</summary>
    public double Arrival;

    public void Write(PacketWriter writer) {
        writer.WriteByte((byte) Kind);
        writer.WriteUInt(Tick);
        switch (Kind) {
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
        X = Y = Angle = 0f;
        switch (Kind) {
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
    /// <summary>Eventos que esperan su momento por jugador; si se llena, sale el más viejo ya.</summary>
    private const int QueueSize = 32;

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

    private readonly Dictionary<ushort, Remote> _remotes = new();
    private readonly MessageHandler _onEvent;
    private GameSession _session;
    private PlayerSync _players;
    private HeroEffectTemplates _templates;

    /// <summary>Caballero con el que no se pudieron preparar los efectos (no se reintenta cada frame).</summary>
    private HeroController _failedHero;

    private GameObject _parking;
    private EffectEvent _incoming;

    /// <summary>Carga de las artes del aguijón enviada la última vez (0 nada, 1 cargando, 2 cargada).</summary>
    private byte _chargeStage;

    public PlayerEffects() {
        _onEvent = OnEvent;
    }

    /// <summary>Eventos enviados y reproducidos (modo de prueba).</summary>
    public int Sent { get; private set; }

    public int Received { get; private set; }

    public int Played {
        get {
            var played = 0;
            foreach (var remote in _remotes.Values) {
                played += remote.Effects?.Played ?? 0;
            }

            return played;
        }
    }

    public void Attach(GameSession session, PlayerSync players) {
        _session = session;
        _players = players;
        _active = this;
        session.On(MessageType.PlayerEvent, _onEvent);
        InstallHooks();
    }

    public void Detach() {
        if (_session != null) {
            _session.On(MessageType.PlayerEvent, null);
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
            return;
        }

        var stage = hero.artChargedEffect != null && hero.artChargedEffect.activeSelf ? (byte) 2 :
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
            remote.Effects?.Play(remote.Queue[0]);
            Array.Copy(remote.Queue, 1, remote.Queue, 0, QueueSize - 1);
            remote.Count--;
        }

        remote.Queue[remote.Count++] = _incoming;
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

            remote.Effects.Play(effect);
            if (TestDriver.IsEnabled()) {
                Log.Info($"[TEST] efecto {effect.Kind} de {player.Name}");
            }

            played++;
        }

        if (played > 0) {
            Array.Copy(remote.Queue, played, remote.Queue, 0, remote.Count - played);
            remote.Count -= played;
        }

        remote.Effects.Update(Time.deltaTime);
    }

    /// <summary>El avatar de un jugador se va a destruir (cambio de sala): sus efectos se aparcan y su cola se vacía.</summary>
    public void Release(ushort playerId) {
        if (_remotes.TryGetValue(playerId, out var remote)) {
            remote.Effects?.Detach();
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
                _active?.Send(new EffectEvent { Kind = EffectKind.Hit });
            }
        };
    }
}
