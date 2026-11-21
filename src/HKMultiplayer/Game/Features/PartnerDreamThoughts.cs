using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Los pensamientos que un compañero saca con el aguijón onírico (de enemigos y de personajes) los ven también los demás
/// de su sala. En el juego salen arriba de la pantalla en el cuadro <c>Dream Msg</c> (FSM <c>Display</c>), pero solo en el PC
/// de quien golpea.
/// <para>
/// Cuando ese cuadro empieza a mostrar un texto justo después de un golpe del aguijón onírico del caballero local, se envía
/// a la sala la hoja y la clave del texto elegido (el juego elige una frase al azar: así todos ven la misma). Los demás lo
/// muestran con el mismo cuadro y en su idioma, solo si esa clave existe en el juego.
/// </para>
/// </summary>
internal sealed class PartnerDreamThoughts {
    /// <summary>Un pensamiento sacado con el aguijón onírico (→ la sala): hoja y clave del texto.</summary>
    public const MessageType DreamThought = (MessageType) 127;

    private const string DisplayState = "Display Text";
    private const string EnemySheet = "Enemy Dreams";

    /// <summary>Tiempo tras el golpe del aguijón onírico en que un texto nuevo del cuadro cuenta como suyo.</summary>
    private const float HitWindow = 1.5f;

    private static bool _hooked;
    private static PartnerDreamThoughts _active;

    private readonly MessageHandler _onThought;
    private GameSession _session;
    private PlayMakerFSM _display;
    private string _lastState;
    private string _previousState;
    private float _hitAt = -100f;

    /// <summary>El cuadro muestra un pensamiento llegado de otro PC: no se vuelve a enviar.</summary>
    private bool _showingRemote;

    static PartnerDreamThoughts() {
        MessageRoutes.Register(DreamThought, Route.SameScene, reliable: true);
    }

    public PartnerDreamThoughts() {
        _onThought = OnThought;
    }

    public int Sent { get; private set; }
    public int Shown { get; private set; }

    public void Attach(GameSession session) {
        _session = session;
        _active = this;
        session.On(DreamThought, _onThought);
        if (_hooked) {
            return;
        }

        _hooked = true;
        // El golpe del aguijón onírico del caballero local: a enemigos y a los personajes con pensamientos
        On.EnemyDreamnailReaction.RecieveDreamImpact += (orig, self) => {
            orig(self);
            if (_active != null) {
                _active._hitAt = Time.unscaledTime;
            }
        };
        On.HutongGames.PlayMaker.Actions.ListenForDreamNail.OnEnter += (orig, self) => {
            orig(self);
            if (_active != null && self.Fsm?.GameObject == HeroController.instance?.gameObject) {
                _active._hitAt = Time.unscaledTime;
            }
        };
    }

    public void Detach() {
        _session?.On(DreamThought, null);
        _session = null;
        if (_active == this) {
            _active = null;
        }

        _display = null;
    }

    /// <summary>Cada frame: si el cuadro empieza a mostrar un pensamiento propio, se envía a la sala.</summary>
    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        if (_display == null) {
            var msg = HutongGames.PlayMaker.FsmVariables.GlobalVariables.FindFsmGameObject("Enemy Dream Msg")?.Value;
            _display = msg != null ? PlayMakerFSM.FindFsmOnGameObject(msg, "Display") : null;
            if (_display == null) {
                return;
            }
        }

        var state = _display.ActiveStateName;
        if (state == _lastState) {
            return;
        }

        _previousState = _lastState;
        _lastState = state;
        if (state != DisplayState) {
            if (state == "Idle") {
                _showingRemote = false;
            }

            return;
        }

        if (_showingRemote || Time.unscaledTime - _hitAt > HitWindow || !_session.CanSend || scene.Length == 0) {
            return;
        }

        // Por "Cancel" y "Msg Text" (un personaje), la hoja la puso quien lo pidió; si no, es un pensamiento de enemigo, de
        // hoja fija ("Set Convo" y "Display Text" pasan en el mismo frame: "Set Convo" no se llega a ver)
        var variables = _display.FsmVariables;
        var sheet = _previousState is "Cancel" or "Msg Text" ? variables.FindFsmString("Sheet")?.Value : EnemySheet;
        var key = variables.FindFsmString("Convo Title")?.Value;
        if (string.IsNullOrEmpty(sheet) || string.IsNullOrEmpty(key)) {
            return;
        }

        var writer = _session.Begin(DreamThought);
        writer.WriteString(scene);
        writer.WriteString(sheet);
        writer.WriteString(key);
        _session.End();
        Sent++;
        Log.Info($"Pensamiento del aguijón onírico enviado a la sala: {sheet}/{key}");
    }

    private void OnThought(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var sheet = reader.ReadString(64);
        var key = reader.ReadString(128);
        var gm = GameManager.instance;
        if (from.IsLocal || _display == null || gm == null || gm.sceneName != scene ||
            !TeamCherry.Localization.Language.Has(key, sheet)) {
            return;
        }

        // Si este jugador tiene ya un pensamiento propio en pantalla, el suyo va antes
        if (_display.ActiveStateName == DisplayState && !_showingRemote) {
            return;
        }

        var variables = _display.FsmVariables;
        variables.FindFsmString("Sheet").Value = sheet;
        variables.FindFsmString("Convo Title").Value = key;
        _showingRemote = true;
        _display.SendEvent("DISPLAY DREAM MSG");
        Shown++;
        Log.Info($"Pensamiento del aguijón onírico de {from.Name}: {sheet}/{key}");
    }
}
