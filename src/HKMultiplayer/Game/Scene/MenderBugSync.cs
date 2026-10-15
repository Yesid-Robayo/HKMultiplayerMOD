using HKMultiplayer.Game.Players;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// El Menderbug del cartel roto del Cruce Olvidado: el juego decide al azar al entrar en la sala si está (1 de cada 50).
/// Cada PC tiraba su propio dado, así que casi siempre lo veía un solo jugador, o ninguno, y los demás no tenían bicho al
/// que pegar. Si sale en cualquier PC de la sala, sale en todos: el que lo tiene lo avisa y los demás lo ponen esperando
/// en su sitio. Lo demás (quién lo mueve, su vida, que muera para todos) va como cualquier enemigo.
/// </summary>
internal sealed class MenderBugSync {
    /// <summary>El Menderbug está en esta sala en este PC (→ sala).</summary>
    public const MessageType MenderBugHere = (MessageType) 116;

    private const string BugScene = "Crossroads_01";

    private const string BugFsm = "Mender Bug Ctrl";

    /// <summary>Estado en el que espera en el cartel (y el que pone su lógica si no sale).</summary>
    private const string WaitingState = "Idle";

    private const string GoneState = "Destroy";

    /// <summary>Cada cuánto lo vuelve a avisar quien lo tiene (por los que entran después).</summary>
    private const float AnnounceInterval = 3f;

    private readonly MessageHandler _onHere;
    private GameSession _session;
    private string _scene = string.Empty;
    private ushort _sceneHash;
    private PlayMakerFSM _fsm;
    private float _nextLook;
    private float _nextAnnounce;

    /// <summary>En otro PC salió: aquí también, en cuanto su lógica haya decidido.</summary>
    private bool _wanted;

    static MenderBugSync() {
        MessageRoutes.Register(MenderBugHere, Route.SameScene, reliable: true);
    }

    public MenderBugSync() {
        _onHere = OnHere;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(MenderBugHere, _onHere);
    }

    public void Detach() {
        _session?.On(MenderBugHere, null);
        _session = null;
        _fsm = null;
    }

    public void Update(string scene) {
        if (scene != _scene) {
            _scene = scene;
            _sceneHash = PlayerSnapshot.HashScene(scene);
            _fsm = null;
            _wanted = false;
            _nextAnnounce = 0f;
        }

        var now = Time.unscaledTime;
        if (_session == null || scene != BugScene || now < _nextLook) {
            return;
        }

        _nextLook = now + 0.25f;
        _fsm ??= FindBug();
        if (_fsm == null) {
            return;
        }

        var state = _fsm.ActiveStateName;
        if (_wanted && (state == GoneState || !_fsm.gameObject.activeSelf)) {
            Show();
            return;
        }

        // Aunque aquí no se vea: si este PC no lo mueve, su copia sigue al que lo mueve, en cuyo PC no salió y está apagado.
        // Al recibir el aviso, ese lo enciende y desde ahí se ve en todos
        if (state == WaitingState && now >= _nextAnnounce && _session.CanSend) {
            _nextAnnounce = now + AnnounceInterval;
            var writer = _session.Begin(MenderBugHere);
            writer.WriteUShort(_sceneHash);
            _session.End();
        }
    }

    private void OnHere(SessionPlayer from, PacketReader reader) {
        if (reader.ReadUShort() == _sceneHash && _scene == BugScene) {
            _wanted = true;
        }
    }

    /// <summary>Lo pone esperando en el cartel, como si aquí también hubiera salido el 50.</summary>
    private void Show() {
        _wanted = false;
        var pd = PlayerData.instance;
        // Muerto ya (en otro PC, o antes): no vuelve
        if (pd == null || pd.GetInt("menderState") >= 2) {
            return;
        }

        Log.Info("Menderbug: salió en otro PC de la sala, aquí también");
        var go = _fsm.gameObject;
        // Al encenderse no vuelve a empezar (volvería a tirar el dado): sigue desde el estado que se le pone
        _fsm.Fsm.RestartOnEnable = false;
        go.SetActive(true);
        _fsm.SetState(WaitingState);
    }

    private static PlayMakerFSM FindBug() {
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            if (fsm.FsmName == BugFsm && fsm.gameObject.scene.name == BugScene) {
                return fsm;
            }
        }

        return null;
    }
}
