using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Viajar juntos con el Portal Onírico: cuando un jugador viaja a su portal, los compañeros que están cerca de él en la
/// sala viajan con él al mismo sitio, como con el ciervo (<see cref="StagTogether"/>). Antes cada uno se iba solo. Lo
/// activa el host con «Viajar juntos» (<see cref="HostSettings.StagTogether"/>).
/// <para>
/// Quien viaja avisa cuando la FSM <c>Dream Nail</c> de su caballero llega a "Warp End" (el viaje normal, no la salida de
/// un sueño ni de Dios Hogar, que van por otro lado): para entonces ya pagó la esencia y su destino es su portal
/// (<c>dreamGateScene</c>, <c>dreamGateX/Y</c>). Los que van con él viajan a ese punto con <see cref="SceneTravel"/>; su
/// propio portal no cambia.
/// </para>
/// </summary>
internal sealed class DreamGateTogether {
    /// <summary>Un jugador viaja a su Portal Onírico desde esta sala (→ la sala): sala y punto de destino.</summary>
    public const MessageType DreamGateTravel = (MessageType) 120;

    private const string DreamNailFsm = "Dream Nail";
    /// <summary>
    /// Estados del final del viaje: "Warp End" dura lo bastante para verse cada frame ("New Scene" pasa en el mismo frame).
    /// </summary>
    private static readonly string[] LeavingStates = { "Warp End", "Leave Type", "New Scene" };

    /// <summary>Zonas desde las que el viaje sale del sueño o de Dios Hogar en vez de ir al portal.</summary>
    private static readonly string[] OtherExits = { "DREAM_WORLD", "GODS_GLORY" };

    /// <summary>Ya se avisó de este viaje (se vuelve a poder al salir de esos estados).</summary>
    private bool _sent;

    /// <summary>Hasta dónde de quien viaja se va con él (como con el ciervo).</summary>
    private const float NearDistance = 10f;

    private const float MaxCoordinate = 10000f;

    private readonly MessageHandler _onTravel;
    private readonly SceneTravel _travel = new();
    private GameSession _session;
    private PlayMakerFSM _fsm;
    private HeroController _hero;
    private string _lastState;

    static DreamGateTogether() {
        MessageRoutes.Register(DreamGateTravel, Route.SameScene, reliable: true);
    }

    public DreamGateTogether() {
        _onTravel = OnTravel;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(DreamGateTravel, _onTravel);
    }

    public void Detach() {
        _session?.On(DreamGateTravel, null);
        _session = null;
        _travel.Cancel();
        _fsm = null;
        _hero = null;
        _lastState = null;
    }

    /// <summary>Cada frame: mira si el caballero local viaja a su portal y lleva a término un viaje con un compañero.</summary>
    public void Update(string scene) {
        _travel.Update();
        if (_session == null) {
            return;
        }

        var hero = HeroController.instance;
        if (hero != _hero) {
            _hero = hero;
            _fsm = hero != null ? PlayMakerFSM.FindFsmOnGameObject(hero.gameObject, DreamNailFsm) : null;
            _lastState = null;
        }

        if (_fsm == null) {
            return;
        }

        var state = _fsm.ActiveStateName;
        if (state == _lastState) {
            return;
        }

        _lastState = state;
        if (System.Array.IndexOf(LeavingStates, state) < 0) {
            _sent = false;
            return;
        }

        var pd = PlayerData.instance;
        var gm = GameManager.instance;
        if (_sent || pd == null || gm == null || !_session.CanSend || !HostSettings.Current.StagTogether || scene.Length == 0 ||
            System.Array.IndexOf(OtherExits, gm.GetCurrentMapZone()) >= 0) {
            return;
        }

        _sent = true;

        var target = pd.GetString("dreamGateScene");
        if (string.IsNullOrEmpty(target)) {
            return;
        }

        var position = hero.transform.position;
        Log.Info($"Viaje al Portal Onírico ({target}): los compañeros cercanos de la sala vienen también");
        var writer = _session.Begin(DreamGateTravel);
        writer.WriteString(scene);
        writer.WriteString(target);
        writer.WriteFloat(pd.GetFloat("dreamGateX"));
        writer.WriteFloat(pd.GetFloat("dreamGateY"));
        writer.WriteFloat(position.x);
        writer.WriteFloat(position.y);
        _session.End();
    }

    private void OnTravel(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var target = reader.ReadString(128);
        var x = reader.ReadFloat();
        var y = reader.ReadFloat();
        var fromPosition = new Vector2(reader.ReadFloat(), reader.ReadFloat());
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        if (gm == null || hero == null || pd == null || gm.sceneName != scene || string.IsNullOrEmpty(target) ||
            !Valid(x) || !Valid(y) || !Valid(fromPosition.x) || !Valid(fromPosition.y)) {
            return;
        }

        var why = !HostSettings.Current.StagTogether ? "desactivado por el host"
            : Vector2.Distance(hero.transform.position, fromPosition) > NearDistance ? "lejos de él"
            : _travel.Active ? "ya está viajando"
            : PartyState.IsDown(_session.LocalId) ? "caído"
            : hero.cState.dead || hero.cState.transitioning || gm.IsInSceneTransition ? "cambiando de sala"
            : pd.atBench && hero.controlReqlinquished ? "sentado en un banco"
            : gm.isPaused ? "en pausa"
            : hero.cState.hazardRespawning ? "reapareciendo"
            : hero.controlReqlinquished ? "ocupado"
            : null;
        if (why != null) {
            Log.Info($"{from.Name} viaja a su Portal Onírico; este jugador no va ({why})");
            Notice.Show(Lang.T($"{from.Name} viajó a su Portal Onírico", $"{from.Name} warped to their Dreamgate"), 4f);
            return;
        }

        Log.Info($"{from.Name} viaja a su Portal Onírico en {target}: este jugador va con él");
        if (_travel.Begin(target, new Vector3(x, y, 0f))) {
            Notice.Show(Lang.T($"Viajando al Portal Onírico de {from.Name}", $"Warping to {from.Name}'s Dreamgate"), 4f);
        }
    }

    private static bool Valid(float value) => !float.IsNaN(value) && Mathf.Abs(value) <= MaxCoordinate;
}
