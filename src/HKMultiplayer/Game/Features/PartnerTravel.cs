using System;
using System.Collections.Generic;
using HKMultiplayer.Game.World;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// "Ir con ..." del panel (página Compañeros): al quitar la pausa, el caballero aparece junto a un compañero para
/// desatascarse. Se pregunta al compañero, a través del host, dónde está: responde con su sala y el último suelo que
/// pisó en ella (si está saltando, no se aparece en el aire; si aún no pisó suelo en la sala, el punto de reaparición
/// que le dio el juego al entrar). En la misma sala se coloca al lado; en otra, se viaja a esa sala sin puerta de
/// entrada (<see cref="SceneTravel"/>) y se coloca allí. El punto de reaparición tras pinchos pasa a ser ese sitio.
/// <para>El host decide si se puede (ajuste "Permitir ir con un compañero") y lo comprueba él: no se va con un compañero
/// caído, en una cinemática, en un sueño, cambiando de sala o en otro mundo, ni estando caído uno mismo.</para>
/// </summary>
internal sealed class PartnerTravel {
    /// <summary>Quiero ir con este compañero (jugador → host).</summary>
    public const MessageType GoToRequest = (MessageType) 99;

    /// <summary>¿Dónde estás? Para este jugador (host → compañero).</summary>
    public const MessageType GoToQuery = (MessageType) 100;

    /// <summary>Dónde estoy (o por qué no se puede venir), para este jugador (compañero → host).</summary>
    public const MessageType GoToAnswer = (MessageType) 101;

    /// <summary>Adónde ir (o por qué no se puede) (host → quien lo pidió).</summary>
    public const MessageType GoToResult = (MessageType) 102;

    /// <summary>Si no llega respuesta en este tiempo, se abandona.</summary>
    private const float ReplyTimeout = 6f;

    /// <summary>Distancia a un lado del compañero a la que se aparece (si cabe y hay suelo a la misma altura).</summary>
    private const float SideStep = 0.9f;

    /// <summary>Por qué no se puede ir con el compañero.</summary>
    private enum Refusal : byte {
        None = 0,
        Disabled = 1,
        Gone = 2,
        Downed = 3,
        Cutscene = 4,
        Dream = 5,
        Changing = 6,
        OtherWorld = 7,
        Unreachable = 8,
        RequesterDowned = 9
    }

    private readonly MessageHandler _onRequest;
    private readonly MessageHandler _onQuery;
    private readonly MessageHandler _onAnswer;
    private readonly MessageHandler _onResult;
    private readonly Func<Vector3, Vector3> _onArrival;
    private readonly SceneTravel _travel = new();

    private GameSession _session;
    private Players.PlayerSync _players;
    private PartyState _party;
    private Revive _revive;
    private DreamSync _dreams;
    private SharedCutscenes _cutscenes;

    // ---- Lo pedido por el jugador local ----

    /// <summary>Compañero con el que se quiere ir (0: nada pedido).</summary>
    private ushort _wanted;

    private string _wantedName = string.Empty;

    /// <summary>Cuándo se envió la petición (negativo: aún no, se envía al quitar la pausa).</summary>
    private float _sentAt = -1f;

    /// <summary>Respuesta recibida, pendiente de hacerse en cuanto se juegue.</summary>
    private bool _hasDestination;

    private string _destinationScene = string.Empty;

    /// <summary>El compañero con el que se va (su sitio es el centro, no cuenta como ocupado).</summary>
    private ushort _destinationId;
    private Vector3 _destination;

    // ---- Último suelo pisado por el caballero local (lo que se responde a quien quiere venir) ----
    private string _groundScene = string.Empty;
    private Vector3 _groundPosition;

    static PartnerTravel() {
        MessageRoutes.Register(GoToRequest, Route.Server, reliable: true);
        MessageRoutes.Register(GoToQuery, Route.Server, reliable: true);
        MessageRoutes.Register(GoToAnswer, Route.Server, reliable: true);
        MessageRoutes.Register(GoToResult, Route.Server, reliable: true);
    }

    public PartnerTravel() {
        _onRequest = OnRequest;
        _onQuery = OnQuery;
        _onAnswer = OnAnswer;
        _onResult = OnResult;
        _onArrival = OnArrival;
    }

    public void Attach(GameSession session, PartyState party, Revive revive, DreamSync dreams, SharedCutscenes cutscenes,
        Players.PlayerSync players) {
        _session = session;
        _players = players;
        _party = party;
        _revive = revive;
        _dreams = dreams;
        _cutscenes = cutscenes;
        Clear();
        session.On(GoToRequest, _onRequest);
        session.On(GoToQuery, _onQuery);
        session.On(GoToAnswer, _onAnswer);
        session.On(GoToResult, _onResult);
    }

    public void Detach() {
        if (_session != null) {
            _session.On(GoToRequest, null);
            _session.On(GoToQuery, null);
            _session.On(GoToAnswer, null);
            _session.On(GoToResult, null);
            _session = null;
        }

        Clear();
        _travel.Cancel();
        _groundScene = string.Empty;
    }

    private void Clear() {
        _wanted = 0;
        _wantedName = string.Empty;
        _sentAt = -1f;
        _hasDestination = false;
    }

    /// <summary>Si hay una petición o un viaje en curso.</summary>
    public bool Busy => _wanted != 0 || _hasDestination || _travel.Active;

    // =====================================================================================================
    // Petición (jugador local)
    // =====================================================================================================

    /// <summary>
    /// Ir con un compañero al quitar la pausa. Devuelve el error (para el panel) o null si se aceptó; lo que pase después
    /// (respuesta del compañero, viaje) se avisa en pantalla.
    /// </summary>
    public string Request(ushort targetId) {
        var error = LocalError(targetId);
        if (error != null) {
            return error;
        }

        _wanted = targetId;
        _wantedName = _party.Get(targetId)?.Name ?? Lang.T("tu compañero", "your partner");
        _sentAt = -1f;
        _hasDestination = false;
        Log.Info($"Ir con {_wantedName}: al volver al juego");
        return null;
    }

    /// <summary>Lo que impide ir con el compañero según lo que se sabe en este PC (null si nada).</summary>
    private string LocalError(ushort targetId) {
        if (_session == null || !_session.CanSend || _party == null) {
            return Lang.T("No estás conectado a ninguna partida", "You aren't connected to any game");
        }

        if (!HostSettings.Current.AllowPartnerTravel) {
            return Lang.T("El host desactivó ir con un compañero", "The host disabled going to a partner");
        }

        if (_revive != null && _revive.IsDowned) {
            return Lang.T("Estás caído: espera a que te revivan", "You're down: wait to be revived");
        }

        if (_cutscenes != null && _cutscenes.Watching) {
            return Lang.T("Estás viendo una cinemática", "You're watching a cutscene");
        }

        if (_dreams != null && _dreams.InDream) {
            return Lang.T("Estás en un sueño: sal de él para ir con un compañero", "You're in a dream: leave it to go to a partner");
        }

        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (gm == null || hero == null || gm.IsInSceneTransition || hero.cState.transitioning || _travel.Active ||
            _revive != null && _revive.IsTraveling) {
            return Lang.T("Estás cambiando de sala: espera a llegar", "You're changing rooms: wait until you arrive");
        }

        var busy = BusyError(hero);
        if (busy != null) {
            return busy;
        }

        if (!InSharedWorld()) {
            return Lang.T("Solo se puede ir con un compañero dentro del mismo mundo coop", "You can only go to a partner in the same coop world");
        }

        var target = _party.Get(targetId);
        if (target == null || target.Player == null || targetId == _session.LocalId) {
            return target != null ? Lang.T($"{target.Name} ya no está en la partida", $"{target.Name} is no longer in the game") : Lang.T("Ese compañero ya no está en la partida", "That partner is no longer in the game");
        }

        var name = target.Name;

        var refusal = target.Status switch {
            PlayerStatus.Downed => Refusal.Downed,
            PlayerStatus.Cutscene => Refusal.Cutscene,
            PlayerStatus.Dream => Refusal.Dream,
            PlayerStatus.Joining => Refusal.Changing,
            _ => target.Player.Scene.Length == 0 ? Refusal.Changing
                : !Revive.IsFollowedScene(target.Player.Scene) ? Refusal.Unreachable : Refusal.None
        };
        return refusal == Refusal.None ? null : Describe(refusal, name);
    }

    /// <summary>
    /// Si el caballero local está ocupado: sentado en un banco (movido así se quedaba sentado en el aire) o sin control
    /// (diálogo, ascensor, presentación de un jefe...). Null si puede ir.
    /// </summary>
    private static string BusyError(HeroController hero) {
        var pd = PlayerData.instance;
        if (pd != null && pd.atBench) {
            return Lang.T("Estás sentado en un banco: levántate antes de ir con un compañero", "You're sitting on a bench: get up before going to a partner");
        }

        return hero.controlReqlinquished ? Lang.T("Ahora no puedes moverte: inténtalo cuando recuperes el control", "You can't move right now: try again when you regain control") : null;
    }

    private static string Describe(Refusal refusal, string name) => refusal switch {
        Refusal.Disabled => Lang.T("El host desactivó ir con un compañero", "The host disabled going to a partner"),
        Refusal.Gone => Lang.T($"{name} ya no está en la partida", $"{name} is no longer in the game"),
        Refusal.Downed => Lang.T($"No se puede ir con {name}: está caído", $"Can't go to {name}: they're down"),
        Refusal.Cutscene => Lang.T($"No se puede ir con {name}: está viendo una cinemática", $"Can't go to {name}: they're watching a cutscene"),
        Refusal.Dream => Lang.T($"No se puede ir con {name}: está en un sueño", $"Can't go to {name}: they're in a dream"),
        Refusal.Changing => Lang.T($"No se puede ir con {name}: está cambiando de sala (prueba en un momento)", $"Can't go to {name}: they're changing rooms (try again in a moment)"),
        Refusal.OtherWorld => Lang.T($"No se puede ir con {name}: está en otro mundo", $"Can't go to {name}: they're in another world"),
        Refusal.Unreachable => Lang.T($"No se puede ir con {name}: está en un sitio al que no se puede ir", $"Can't go to {name}: they're somewhere you can't go"),
        Refusal.RequesterDowned => Lang.T("Estás caído: espera a que te revivan", "You're down: wait to be revived"),
        _ => Lang.T($"No se puede ir con {name}", $"Can't go to {name}")
    };

    /// <summary>
    /// Si este jugador está en el mundo coop de la partida (el host en el suyo, el invitado en el del host). Si no, cada
    /// uno juega su propia partida y las salas, aunque se llamen igual, no son el mismo mundo.
    /// </summary>
    private bool InSharedWorld() =>
        _session != null && CoopSaves.ActiveWorld != null && (_session.IsHost ? CoopSaves.IsHostWorld : !CoopSaves.IsHostWorld);

    /// <summary>Llamar cada frame.</summary>
    public void Update() {
        TrackGround();
        _travel.Update();
        if (_wanted == 0 && !_hasDestination) {
            return;
        }

        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (gm == null || hero == null || gm.isPaused || gm.gameState != GlobalEnums.GameState.PLAYING) {
            return;
        }

        if (_hasDestination) {
            _hasDestination = false;
            Go(hero);
            return;
        }

        if (_sentAt < 0f) {
            if (gm.IsInSceneTransition) {
                return;
            }

            // Desde que se pulsó pudo cambiar algo (el compañero cayó, entró en un sueño...)
            var error = LocalError(_wanted);
            if (error != null) {
                Notice.Show(error, 4f);
                Clear();
                return;
            }

            _sentAt = Time.unscaledTime;
            var writer = _session.Begin(GoToRequest);
            writer.WriteUShort(_wanted);
            _session.End();
            return;
        }

        if (Time.unscaledTime - _sentAt > ReplyTimeout) {
            Log.Warn($"Ir con {_wantedName}: sin respuesta");
            Notice.Show(Lang.T($"{_wantedName} no respondió: inténtalo otra vez", $"{_wantedName} didn't answer: try again"), 4f);
            Clear();
        }
    }

    private void OnResult(SessionPlayer from, PacketReader reader) {
        var targetId = reader.ReadUShort();
        var refusal = (Refusal) reader.ReadByte();
        var scene = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        if (_session == null || from.Id != GameSession.HostPlayerId || targetId != _wanted || _sentAt < 0f) {
            return;
        }

        HandleResult(refusal, scene, x, y);
    }

    private void HandleResult(Refusal refusal, string scene, float x, float y) {
        var name = _wantedName;
        _destinationId = _wanted;
        _wanted = 0;
        _sentAt = -1f;
        if (refusal != Refusal.None) {
            Log.Info($"Ir con {name}: no ({refusal})");
            Notice.Show(Describe(refusal, name), 4f);
            return;
        }

        Log.Info($"Ir con {name}: {scene} ({x:0.0}, {y:0.0})");
        _destinationScene = scene;
        _destination = new Vector3(x, y, 0f);
        _hasDestination = true;
        _wantedName = name;
    }

    /// <summary>Hace el viaje: a su lado si está en esta sala; si no, a su sala.</summary>
    private void Go(HeroController hero) {
        var gm = GameManager.instance;
        var name = _wantedName;
        if (_revive != null && _revive.IsDowned) {
            Notice.Show(Lang.T("Estás caído: espera a que te revivan", "You're down: wait to be revived"), 4f);
            return;
        }

        if (gm.IsInSceneTransition || hero.cState.transitioning || _travel.Active || _revive != null && _revive.IsTraveling) {
            Notice.Show(Lang.T("Estás cambiando de sala: inténtalo al llegar", "You're changing rooms: try again when you arrive"), 4f);
            return;
        }

        var busy = BusyError(hero);
        if (busy != null) {
            Notice.Show(busy, 4f);
            return;
        }

        var current = gm.sceneName ?? string.Empty;
        var target = new Vector3(_destination.x, _destination.y, hero.transform.position.z);
        if (current == _destinationScene) {
            var placed = Place(hero, target, Occupied());
            var cameras = GameCameras.instance;
            if (cameras != null && cameras.cameraController != null) {
                cameras.cameraController.PositionToHero(true);
            }

            Log.Info($"Junto a {name} en la misma sala ({placed.x:0.0}, {placed.y:0.0})");
            Notice.Show(Lang.T($"Estás junto a {name}", $"You're next to {name}"), 3f);
            return;
        }

        if (!_travel.Begin(_destinationScene, target, _onArrival)) {
            Notice.Show(Lang.T($"Ahora no se puede ir con {name}", $"Can't go to {name} right now"), 4f);
            return;
        }

        Log.Info($"Viajando con {name} a {_destinationScene}");
    }

    /// <summary>Al llegar a la sala del compañero: se coloca a su lado (con la sala ya cargada para mirar el suelo).</summary>
    private Vector3 OnArrival(Vector3 target) {
        var hero = HeroController.instance;
        if (hero == null) {
            return target;
        }

        var placed = Place(hero, target, Occupied());
        Log.Info($"Junto a {_wantedName} en {_destinationScene} ({placed.x:0.0}, {placed.y:0.0})");
        Notice.Show(Lang.T($"Estás junto a {_wantedName}", $"You're next to {_wantedName}"), 3f);
        return placed;
    }

    /// <summary>
    /// Coloca al caballero junto al punto del compañero, quieto, mirándolo, y fija ahí la reaparición tras pinchos.
    /// Devuelve dónde quedó.
    /// </summary>
    private Vector3 Place(HeroController hero, Vector3 target, List<Vector3> occupied) {
        // Cada uno empieza por un sitio según su número: dos que van a la vez al mismo compañero (aún no se ven el uno al
        // otro) no acaban uno encima del otro
        var side = BesideOffset(hero, target, occupied, _session != null ? _session.LocalId : (ushort) 1);
        var position = target + new Vector3(side, 0f, 0f);
        hero.transform.position = position;
        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.linearVelocity = Vector2.zero;
        }

        // Mirando hacia el compañero
        if (side < 0f) {
            hero.FaceRight();
        } else if (side > 0f) {
            hero.FaceLeft();
        }

        hero.SetHazardRespawn(position, hero.cState.facingRight);
        return position;
    }

    /// <summary>
    /// Cuánto apartarse a un lado del compañero: a la izquierda o a la derecha si el caballero cabe (sin pared), hay suelo
    /// a la misma altura que el suyo y nada que haga daño; si no, en su mismo sitio (0).
    /// </summary>
    private static float BesideOffset(HeroController hero, Vector3 target, List<Vector3> occupied, ushort playerId) {
        var collider = hero.GetComponent<Collider2D>();
        if (collider == null) {
            return 0f;
        }

        var mask = LayerMask.GetMask("Terrain");
        if (mask == 0) {
            mask = 1 << 8;
        }

        var bounds = collider.bounds;
        var offset = (Vector2) (bounds.center - hero.transform.position);
        var size = (Vector2) bounds.size * 0.9f;
        var reach = size.y * 0.5f + 0.6f;
        var partnerGround = Physics2D.Raycast((Vector2) target + offset, Vector2.down, reach, mask);
        if (partnerGround.collider == null) {
            return 0f;
        }

        var first = (playerId + Sides.Length - 1) % Sides.Length;
        for (var i = 0; i < Sides.Length; i++) {
            var side = Sides[(first + i) % Sides.Length] * SideStep;
            if (Taken(occupied, target, side)) {
                continue;
            }

            var center = (Vector2) target + offset + new Vector2(side, 0f);
            if (Physics2D.OverlapBox(center, size, 0f, mask) != null) {
                continue;
            }

            var ground = Physics2D.Raycast(center, Vector2.down, reach, mask);
            if (ground.collider == null || Mathf.Abs(ground.point.y - partnerGround.point.y) > 0.3f ||
                Harmful(center, size)) {
                continue;
            }

            return side;
        }

        return 0f;
    }

    /// <summary>Sitios junto al compañero (en pasos a un lado); cada número de jugador empieza por uno distinto.</summary>
    private static readonly float[] Sides = { -1f, 1f, -2f, 2f };

    /// <summary>Si ya hay otro jugador en ese sitio junto al compañero.</summary>
    private static bool Taken(List<Vector3> occupied, Vector3 target, float side) {
        foreach (var other in occupied) {
            if (Mathf.Abs(other.x - (target.x + side)) < SideStep * 0.6f && Mathf.Abs(other.y - target.y) < 1.5f) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Dónde están los demás jugadores dibujados en la sala (menos el compañero, que ocupa el centro). Con 3 o más, dos
    /// que iban al mismo compañero aparecían uno encima del otro.
    /// </summary>
    private List<Vector3> Occupied() {
        var list = new List<Vector3>();
        if (_session == null || _players == null) {
            return list;
        }

        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Id != _destinationId && _players.TryGetAvatarPosition(player.Id, out var position)) {
                list.Add(position);
            }
        }

        return list;
    }

    /// <summary>Si en ese hueco (y justo debajo, donde están los pinchos) hay algo que hace daño.</summary>
    private static bool Harmful(Vector2 center, Vector2 size) {
        // Solo al pulsar "Ir con ...": la lista que crea no es memoria por frame
        foreach (var hit in Physics2D.OverlapBoxAll(center + new Vector2(0f, -0.3f), size + new Vector2(0.4f, 0.6f), 0f)) {
            var damage = hit.GetComponent<DamageHero>();
            if (damage != null && damage.damageDealt > 0) {
                return true;
            }
        }

        return false;
    }

    // =====================================================================================================
    // Host
    // =====================================================================================================

    private void OnRequest(SessionPlayer from, PacketReader reader) {
        var targetId = reader.ReadUShort();
        if (_session == null || !_session.IsHost || _party == null) {
            return;
        }

        var refusal = HostCheck(from.Id, targetId);
        if (refusal != Refusal.None) {
            Deliver(from.Id, targetId, refusal, string.Empty, 0f, 0f);
            return;
        }

        if (targetId == _session.LocalId) {
            // El compañero es el host: responde él mismo
            var answer = LocalAnswer(out var scene, out var position);
            Deliver(from.Id, targetId, answer, scene, position.x, position.y);
            return;
        }

        var target = _party.Get(targetId)?.Player;
        if (target?.Connection == null) {
            Deliver(from.Id, targetId, Refusal.Gone, string.Empty, 0f, 0f);
            return;
        }

        var writer = _session.BeginTo(target, GoToQuery);
        writer.WriteUShort(from.Id);
        _session.EndTo();
    }

    /// <summary>Lo que el host comprueba antes de preguntar al compañero.</summary>
    private Refusal HostCheck(ushort requesterId, ushort targetId) {
        if (!HostSettings.Current.AllowPartnerTravel) {
            return Refusal.Disabled;
        }

        var requester = _party.Get(requesterId);
        var target = _party.Get(targetId);
        if (target == null || target.Player == null || targetId == requesterId) {
            return Refusal.Gone;
        }

        if (requester != null && requester.Status == PlayerStatus.Downed) {
            return Refusal.RequesterDowned;
        }

        return target.Status switch {
            PlayerStatus.Downed => Refusal.Downed,
            PlayerStatus.Cutscene => Refusal.Cutscene,
            PlayerStatus.Dream => Refusal.Dream,
            PlayerStatus.Joining => Refusal.Changing,
            _ => Refusal.None
        };
    }

    private void OnAnswer(SessionPlayer from, PacketReader reader) {
        var requesterId = reader.ReadUShort();
        var refusal = (Refusal) reader.ReadByte();
        var scene = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        if (_session == null || !_session.IsHost) {
            return;
        }

        // Mientras tanto pudo cambiar algo (o el host desactivarlo)
        var check = HostCheck(requesterId, from.Id);
        if (check != Refusal.None) {
            refusal = check;
        }

        Deliver(requesterId, from.Id, refusal, scene, x, y);
    }

    /// <summary>Host: da la respuesta a quien la pidió (a sí mismo, si fue él).</summary>
    private void Deliver(ushort requesterId, ushort targetId, Refusal refusal, string scene, float x, float y) {
        if (requesterId == _session.LocalId) {
            if (targetId == _wanted && _sentAt >= 0f) {
                HandleResult(refusal, scene, x, y);
            }

            return;
        }

        var requester = _party.Get(requesterId)?.Player;
        if (requester?.Connection == null) {
            return;
        }

        var writer = _session.BeginTo(requester, GoToResult);
        writer.WriteUShort(targetId);
        writer.WriteByte((byte) refusal);
        writer.WriteString(scene ?? string.Empty);
        writer.WriteFloat(x);
        writer.WriteFloat(y);
        _session.EndTo();
    }

    // =====================================================================================================
    // Compañero (al que se quiere ir)
    // =====================================================================================================

    private void OnQuery(SessionPlayer from, PacketReader reader) {
        var requesterId = reader.ReadUShort();
        if (_session == null || from.Id != GameSession.HostPlayerId) {
            return;
        }

        var refusal = LocalAnswer(out var scene, out var position);
        var writer = _session.Begin(GoToAnswer);
        writer.WriteUShort(requesterId);
        writer.WriteByte((byte) refusal);
        writer.WriteString(scene);
        writer.WriteFloat(position.x);
        writer.WriteFloat(position.y);
        _session.End();
    }

    /// <summary>Dónde está el caballero local para que un compañero venga, o por qué no puede.</summary>
    private Refusal LocalAnswer(out string scene, out Vector3 position) {
        scene = string.Empty;
        position = Vector3.zero;
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (_revive != null && _revive.IsDowned) {
            return Refusal.Downed;
        }

        if (_cutscenes != null && _cutscenes.Watching) {
            return Refusal.Cutscene;
        }

        if (_dreams != null && _dreams.InDream) {
            return Refusal.Dream;
        }

        if (!InSharedWorld()) {
            return Refusal.OtherWorld;
        }

        if (gm == null || hero == null || !gm.IsGameplayScene() || gm.IsInSceneTransition || hero.cState.transitioning ||
            _travel.Active || _revive != null && _revive.IsTraveling) {
            return Refusal.Changing;
        }

        var current = gm.sceneName ?? string.Empty;
        if (!Revive.IsFollowedScene(current)) {
            return Refusal.Unreachable;
        }

        scene = current;
        position = _groundScene == current ? _groundPosition : (Vector3) PlayerData.instance.hazardRespawnLocation;
        return Refusal.None;
    }

    /// <summary>Apunta el último suelo que pisa el caballero local (sin pinchos ni caída en curso).</summary>
    private void TrackGround() {
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (gm == null || hero == null || gm.gameState != GlobalEnums.GameState.PLAYING || gm.IsInSceneTransition) {
            return;
        }

        var state = hero.cState;
        if (!state.onGround || state.transitioning || state.hazardDeath || state.hazardRespawning || state.dead ||
            _revive != null && _revive.IsDowned) {
            return;
        }

        _groundScene = gm.sceneName ?? string.Empty;
        _groundPosition = hero.transform.position;
    }

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() =>
        $"pedido={(_wanted != 0 ? _wantedName : "-")} enviado={_sentAt >= 0f} destino={(_hasDestination ? _destinationScene : "-")} " +
        $"viaje={_travel.Active} suelo={_groundScene} ({_groundPosition.x:0.0}, {_groundPosition.y:0.0}) " +
        $"mundo compartido={InSharedWorld()} permitido={HostSettings.Current.AllowPartnerTravel}";
}
