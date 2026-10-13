using System.Collections.Generic;
using System.Linq;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Presentaciones de jefe en la sala: los dos caballeros quietos y recuperan el control a la vez. La lógica del jefe
/// (o la de su arena) corre en un solo PC y es la que le quita el control a su caballero al presentarse y se lo devuelve
/// al acabar; en los demás PCs el jefe es una marioneta y esas llamadas al caballero no se hacen, así que el compañero
/// podía moverse (y pegar) durante la presentación. Cuando una FSM de la sala, con un jefe vivo, le quita el control al
/// caballero local, se avisa a la sala (<see cref="PresentationLock"/>) y los demás caballeros de la sala se quedan
/// quietos hasta que se lo devuelve. Si en un PC su propia lógica también lo había quitado, se espera a las dos.
/// </summary>
internal sealed class BossPresentation {
    /// <summary>Un caballero perdió (o recuperó) el control por la presentación de un jefe (→ sala).</summary>
    public const MessageType PresentationLock = (MessageType) 92;

    /// <summary>Lo más que se deja quieto a un caballero por la presentación de otro (si el aviso de soltarlo no llega).</summary>
    private const float MaxLockSeconds = 30f;

    private static BossPresentation _active;

    /// <summary>El caballero local está quieto por la presentación de jefe de otro PC (no está hablando con nadie).</summary>
    public static bool PartnerLocked => _active != null && _active._lockers.Count != 0;
    private static bool _hooked;

    /// <summary>Se está quitando o devolviendo el control desde aquí (no es la lógica del juego).</summary>
    private static bool _applying;

    private readonly MessageHandler _onLock;
    private GameSession _session;
    private Revive _revive;

    /// <summary>Este PC avisó de que su caballero está quieto por una presentación.</summary>
    private bool _sentLock;

    /// <summary>
    /// Quiénes tienen quieto al caballero local y desde cuándo. Varios a la vez: con 3 o más jugadores puede haber dos
    /// presentaciones a la vez (en PCs distintos); antes se recordaba uno solo y al soltar el primero el caballero
    /// recuperaba el control con la otra aún en marcha.
    /// </summary>
    private readonly Dictionary<ushort, float> _lockers = new();

    /// <summary>La lógica del juego en este PC también quitó el control (y aún no lo devolvió).</summary>
    private bool _localWantsLock;

    static BossPresentation() {
        MessageRoutes.Register(PresentationLock, Route.SameScene, reliable: true);
    }

    public BossPresentation() {
        _onLock = OnLock;
        _onLeft = player => {
            if (_lockers.Remove(player.Id)) {
                ReleaseIfFree($"{player.Name} se fue de la partida");
            }
        };
    }

    private readonly System.Action<SessionPlayer> _onLeft;

    public void Attach(GameSession session, Revive revive) {
        _session = session;
        _revive = revive;
        _active = this;
        _sentLock = false;
        _lockers.Clear();
        _localWantsLock = false;
        session.On(PresentationLock, _onLock);
        session.PlayerLeft += _onLeft;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(PresentationLock, null);
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        Release("sin partida");
        _revive = null;
        if (_active == this) {
            _active = null;
        }
    }

    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.HeroController.RelinquishControl += (orig, self) => {
            var active = _active;
            if (active != null && !_applying) {
                active.OnLocalRelinquish(self);
            }

            orig(self);
        };

        On.HeroController.RegainControl += (orig, self) => {
            var active = _active;
            if (active != null && !_applying && !active.OnLocalRegain()) {
                return;
            }

            orig(self);
        };
    }

    // =====================================================================================================
    // Este PC
    // =====================================================================================================

    private void OnLocalRelinquish(HeroController hero) {
        _localWantsLock = true;
        if (_sentLock || _session == null || !_session.CanSend || _revive != null && _revive.IsDowned) {
            return;
        }

        // Solo la lógica del jefe (un enemigo o algo que cuelga de él) o un controlador de su combate compartido entre
        // los PCs, con un jefe vivo y alguien más en la sala. Lo que quita el control a un solo caballero (caer al vacío
        // en un sueño, reaparecer, un banco, hablar con alguien, la llegada a un sueño) no inmoviliza a los demás
        var fsm = FsmExecutionStack.ExecutingFsm;
        var owner = fsm != null ? fsm.GameObject : null;
        if (owner == null || owner.transform.IsChildOf(hero.transform) ||
            !Scene.EnemySync.IsEnemyObject(owner.transform) && !Scene.FsmStateSync.IsSharedController(fsm) ||
            !Scene.EnemySync.BossInRoom || !AnyoneElseHere()) {
            return;
        }

        _sentLock = true;
        Log.Info($"Presentación de jefe ({owner.name}/{fsm.Name}): los demás caballeros de la sala se quedan quietos");
        Send(true);
    }

    /// <summary>La lógica de este PC devuelve el control. False si no debe devolverse aún (otro sigue en la presentación).</summary>
    private bool OnLocalRegain() {
        _localWantsLock = false;
        if (_sentLock) {
            _sentLock = false;
            Log.Info("Fin de la presentación de jefe: los demás recuperan el control");
            Send(false);
        }

        return _lockers.Count == 0;
    }

    private void Send(bool locked) {
        if (_session == null || !_session.CanSend) {
            return;
        }

        var writer = _session.Begin(PresentationLock);
        writer.WriteBool(locked);
        _session.End();
    }

    private bool AnyoneElseHere() {
        var local = _session.LocalPlayer;
        if (local == null || local.Scene.Length == 0) {
            return false;
        }

        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == local.Scene && !PartyState.IsDown(player.Id)) {
                return true;
            }
        }

        return false;
    }

    // =====================================================================================================
    // Avisos de los demás
    // =====================================================================================================

    private void OnLock(SessionPlayer from, PacketReader reader) {
        var locked = reader.ReadBool();
        var hero = HeroController.instance;
        var local = _session?.LocalPlayer;
        if (hero == null || local == null) {
            return;
        }

        if (!locked) {
            if (_lockers.Remove(from.Id)) {
                ReleaseIfFree($"{from.Name} terminó la presentación");
            }

            return;
        }

        var gm = GameManager.instance;
        if (_lockers.ContainsKey(from.Id) || from.Scene != local.Scene || _revive != null && _revive.IsDowned ||
            gm == null || gm.IsInSceneTransition || hero.cState.dead) {
            return;
        }

        var first = _lockers.Count == 0;
        _lockers[from.Id] = Time.unscaledTime;
        Log.Info($"Presentación de jefe en el PC de {from.Name}: caballero quieto");
        if (first && !hero.controlReqlinquished) {
            _applying = true;
            try {
                hero.RelinquishControl();
            } finally {
                _applying = false;
            }
        }
    }

    /// <summary>Suelta al caballero local de todas las presentaciones de otros (al dejar la partida).</summary>
    private void Release(string reason) {
        if (_lockers.Count == 0) {
            return;
        }

        _lockers.Clear();
        ReleaseIfFree(reason);
    }

    /// <summary>
    /// Suelta al caballero local si ya nadie lo tiene quieto (y su propia lógica no lo quiere quieto).
    /// </summary>
    private void ReleaseIfFree(string reason) {
        if (_lockers.Count != 0) {
            Log.Info($"{reason}; sigue quieto por otra presentación ({string.Join(", ", _lockers.Keys)})");
            return;
        }

        var hero = HeroController.instance;
        Log.Info($"Caballero suelto: {reason}");
        // No en medio de una muerte, una reaparición o un cambio de sala: el juego devuelve el control cuando acaba
        var gm = GameManager.instance;
        if (hero == null || _localWantsLock || _revive != null && _revive.IsDowned || hero.cState.dead ||
            hero.cState.hazardRespawning || gm != null && gm.IsInSceneTransition) {
            return;
        }

        _applying = true;
        try {
            hero.RegainControl();
        } finally {
            _applying = false;
        }
    }

    /// <summary>Cada frame: quien lo tenía quieto se fue de la sala o pasó demasiado tiempo.</summary>
    public void Update() {
        if (_session == null) {
            return;
        }

        // El juego a veces devuelve el control sin RegainControl (al reaparecer tras morir o tras los pinchos): las marcas
        // se quedaban puestas. Con `_localWantsLock` el siguiente bloqueo de otro no devolvía el control; con `_sentLock`,
        // quien murió a mitad de la presentación no volvía a quietar a los demás en el siguiente intento
        var self = HeroController.instance;
        if ((_localWantsLock || _sentLock) && self != null && !self.controlReqlinquished && self.acceptingInput) {
            _localWantsLock = false;
            if (_sentLock) {
                _sentLock = false;
                Send(false);
            }
        }

        if (_lockers.Count == 0) {
            return;
        }

        var local = _session.LocalPlayer;
        var now = Time.unscaledTime;
        foreach (var pair in _lockers.ToList()) {
            SessionPlayer locker = null;
            foreach (var player in _session.Players) {
                if (player.Id == pair.Key) {
                    locker = player;
                }
            }

            if (locker == null || local == null || locker.Scene != local.Scene) {
                _lockers.Remove(pair.Key);
                ReleaseIfFree("quien presentaba al jefe ya no está en la sala");
            } else if (now - pair.Value > MaxLockSeconds) {
                _lockers.Remove(pair.Key);
                ReleaseIfFree("la presentación duró demasiado");
            }
        }
    }

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() => $"avisó={_sentLock} quieto por={(_lockers.Count == 0 ? "0" : string.Join(",", _lockers.Keys))} lógica propia={_localWantsLock}";
}
