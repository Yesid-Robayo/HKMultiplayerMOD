using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Viajar juntos en ciervo: cuando un jugador elige destino en una estación, los demás que están en esa misma sala
/// viajan con él al mismo sitio. Antes cada uno viajaba por su cuenta y el que se quedaba veía irse al ciervo (su
/// posición es del mundo) sin poder seguirlo.
/// <para>
/// Quien viaja avisa al entrar su FSM `Stag Control` en "Fade" (ya con el destino en `To Scene` y `To Position`). En los
/// demás PCs se usa su propio ciervo de esa sala: se le pone el mismo destino y pasa a "Fade", así que su lógica hace lo
/// del juego (fundido, sonido, `travelling`, la escena del viaje y la llegada a la salida del ciervo).
/// </para>
/// </summary>
internal sealed class StagTogether {
    /// <summary>Un jugador sale en ciervo desde esta sala (→ la sala).</summary>
    public const MessageType StagTravel = (MessageType) 106;

    private const string StagFsm = "Stag Control";
    private const string FadeState = "Fade";

    /// <summary>Estados en los que su ciervo ya está saliendo (no se vuelve a empezar).</summary>
    private static readonly string[] LeavingStates = { FadeState, "Time Passes", "Go To Stag Cutscene" };

    private readonly MessageHandler _onTravel;
    private GameSession _session;
    private string _scene = string.Empty;
    private PlayMakerFSM _stag;
    private string _lastState;

    /// <summary>Este PC sale porque viaja otro jugador: no se avisa a los demás (ya lo hizo él).</summary>
    private bool _following;

    static StagTogether() {
        MessageRoutes.Register(StagTravel, Route.SameScene, reliable: true);
    }

    public StagTogether() {
        _onTravel = OnTravel;
    }

    public void Attach(GameSession session) {
        _session = session;
        session.On(StagTravel, _onTravel);
        Clear();
    }

    public void Detach() {
        _session?.On(StagTravel, null);
        _session = null;
        Clear();
    }

    private void Clear() {
        _scene = string.Empty;
        _stag = null;
        _lastState = null;
        _following = false;
    }

    /// <summary>Cada frame: mira si el ciervo de esta sala sale por este jugador.</summary>
    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (scene != _scene) {
            _scene = scene;
            _stag = null;
            _lastState = null;
            _following = false;
            if (scene.Length > 0) {
                foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)) {
                    if (fsm.FsmName == StagFsm && fsm.gameObject.scene.buildIndex >= 0) {
                        _stag = fsm;
                        break;
                    }
                }
            }
        }

        if (_stag == null) {
            return;
        }

        var state = _stag.ActiveStateName;
        if (state == _lastState) {
            return;
        }

        _lastState = state;
        if (state != FadeState || _following || !_session.CanSend) {
            return;
        }

        var toScene = _stag.FsmVariables.FindFsmString("To Scene")?.Value;
        var toPosition = _stag.FsmVariables.FindFsmInt("To Position")?.Value ?? 0;
        if (string.IsNullOrEmpty(toScene)) {
            return;
        }

        Log.Info($"Viaje en ciervo a {toScene}: los compañeros de la sala vienen también");
        var writer = _session.Begin(StagTravel);
        writer.WriteString(_scene);
        writer.WriteString(toScene);
        writer.WriteInt(toPosition);
        _session.End();
    }

    private void OnTravel(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var toScene = reader.ReadString(128);
        var toPosition = reader.ReadInt();
        if (scene != _scene || _stag == null || System.Array.IndexOf(LeavingStates, _stag.ActiveStateName) >= 0) {
            return;
        }

        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        var why = hero == null || pd == null ? "sin caballero"
            : PartyState.IsDown(_session.LocalId) ? "caído"
            : hero.cState.dead || hero.cState.transitioning ? "cambiando de sala"
            : pd.atBench ? "sentado en un banco"
            : GameManager.instance != null && GameManager.instance.isPaused ? "en pausa"
            : null;
        if (why != null) {
            Log.Info($"{from.Name} viaja en ciervo a {toScene}; este jugador no va ({why})");
            Notice.Show(Lang.T($"{from.Name} se fue en ciervo", $"{from.Name} left by stag"), 4f);
            return;
        }

        var variables = _stag.FsmVariables;
        var target = variables.FindFsmString("To Scene");
        var position = variables.FindFsmInt("To Position");
        if (target == null || position == null || _stag.Fsm.GetState(FadeState) == null) {
            return;
        }

        Log.Info($"{from.Name} viaja en ciervo a {toScene}: este jugador va con él");
        target.Value = toScene;
        position.Value = toPosition;
        _following = true;
        hero.RelinquishControl();
        _stag.SetState(FadeState);
        Notice.Show(Lang.T($"Viajando en ciervo con {from.Name}", $"Riding the stag with {from.Name}"), 4f);
    }
}
