using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Entrar juntos a una prueba del Coliseo de los Tontos (issue #8). En el juego, al aceptar un cartel de
/// `Room_Colosseum_01` su FSM avisa a toda la sala (`GATE OPEN`: se abre la reja `Colosseum Gate`, se cierra la trampilla
/// y los carteles dejan de aceptar otra prueba) y cambia el destino de la salida `right1` a la arena de esa prueba. Eso
/// solo pasaba en el PC de quien lo aceptaba: los demás tenían la reja cerrada y entraban con «Ir con». Ahora el PC que
/// la abre avisa a los de la sala (y a quien entra después) con la arena de destino, y ellos hacen lo mismo que el cartel.
/// </summary>
internal sealed class ColosseumEntry {
    /// <summary>La reja de la entrada del Coliseo se abrió hacia una arena (→ los de la sala).</summary>
    public const MessageType GateOpened = (MessageType) 118;

    private const string EntryScene = "Room_Colosseum_01";
    private const string GateName = "Colosseum Gate";
    private const string GateFsm = "Control";
    private const string ExitName = "right1";
    private const string GateOpenEvent = "GATE OPEN";

    /// <summary>Las únicas arenas a las que lleva la reja (lo que llega de otro PC se valida con ellas).</summary>
    private static readonly string[] Arenas = { "Room_Colosseum_Bronze", "Room_Colosseum_Silver", "Room_Colosseum_Gold" };

    private readonly MessageHandler _onGateOpened;
    private GameSession _session;
    private string _scene = string.Empty;
    private PlayMakerFSM _gate;
    private TransitionPoint _exit;
    private float _nextLookup;

    /// <summary>Ya se avisó de la apertura de esta visita a la sala (o la abrió otro PC).</summary>
    private bool _announced;

    /// <summary>Arena recibida de otro PC, pendiente de que la reja termine de iniciarse.</summary>
    private string _pendingArena;

    static ColosseumEntry() {
        MessageRoutes.Register(GateOpened, Route.SameScene, reliable: true);
    }

    public ColosseumEntry() {
        _onGateOpened = OnGateOpened;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(GateOpened, _onGateOpened);
        session.PlayerSceneChanged += OnPlayerScene;
        Reset(string.Empty);
    }

    public void Detach() {
        if (_session != null) {
            _session.On(GateOpened, null);
            _session.PlayerSceneChanged -= OnPlayerScene;
            _session = null;
        }

        Reset(string.Empty);
    }

    /// <summary>Cada frame (solo trabaja en la entrada del Coliseo).</summary>
    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            Reset(scene);
        }

        if (scene != EntryScene || !FindObjects()) {
            return;
        }

        var state = _gate.ActiveStateName;
        if (_pendingArena != null && state is "Closed" or "Close") {
            OpenFor(_pendingArena);
            _pendingArena = null;
            return;
        }

        if (!_announced && state == "Open" && IsArena(_exit.targetScene) && _session.CanSend) {
            _announced = true;
            Log.Info($"Reja del Coliseo abierta hacia {_exit.targetScene}: se avisa a los de la sala");
            Send(_exit.targetScene);
        }
    }

    private void Reset(string scene) {
        _scene = scene;
        _gate = null;
        _exit = null;
        _announced = false;
        _pendingArena = null;
        _nextLookup = 0f;
    }

    private bool FindObjects() {
        if (_gate != null && _exit != null) {
            return true;
        }

        if (Time.unscaledTime < _nextLookup) {
            return false;
        }

        _nextLookup = Time.unscaledTime + 1f;
        var gate = GameObject.Find(GateName);
        _gate = gate != null ? FSMUtility.LocateFSM(gate, GateFsm) : null;
        var exit = GameObject.Find(ExitName);
        _exit = exit != null ? exit.GetComponent<TransitionPoint>() : null;
        return _gate != null && _exit != null;
    }

    /// <summary>Lo que hace el cartel al aceptar la prueba, sin su diálogo ni su cobro.</summary>
    private void OpenFor(string arena) {
        _announced = true;
        _exit.SetTargetScene(arena);
        PlayMakerFSM.BroadcastEvent(GateOpenEvent);
        Log.Info($"Reja del Coliseo abierta por otro jugador hacia {arena}");
    }

    private void Send(string arena) {
        var writer = _session.Begin(GateOpened);
        writer.WriteString(arena);
        _session.End();
    }

    /// <summary>Quien entra en la sala con la reja ya abierta aquí también la recibe abierta.</summary>
    private void OnPlayerScene(SessionPlayer player) {
        if (_session == null || player.IsLocal || player.Scene != EntryScene || _scene != EntryScene || _gate == null ||
            _exit == null || _gate.ActiveStateName != "Open" || !IsArena(_exit.targetScene) || !_session.CanSend) {
            return;
        }

        Log.Info($"{player.Name} entró con la reja del Coliseo abierta: se le avisa");
        Send(_exit.targetScene);
    }

    private void OnGateOpened(SessionPlayer from, PacketReader reader) {
        var arena = reader.ReadString(64);
        if (!IsArena(arena) || _scene != EntryScene) {
            return;
        }

        if (_gate != null && _gate.ActiveStateName == "Open") {
            return;
        }

        Log.Info($"{from.Name} abrió la reja del Coliseo hacia {arena}");
        _pendingArena = arena;
    }

    private static bool IsArena(string scene) => System.Array.IndexOf(Arenas, scene) >= 0;
}
