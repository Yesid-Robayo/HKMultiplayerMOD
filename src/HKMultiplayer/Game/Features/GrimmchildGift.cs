using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Grimmchild: si un jugador lo recibe (al encender el Farol de la Pesadilla y hablar con Grimm), los demás también. Los
/// amuletos son de cada jugador, pero el nivel de Grimmchild, las llamas y la Compañía son del mundo: sin el amuleto,
/// el compañero no podría seguir la misión de la Compañía (ni pelear con Grimm, que lo pide equipado). Cada PC mira cada
/// medio segundo si su jugador lo acaba de recibir y avisa a todos (<see cref="CharmGift"/>).
/// </summary>
internal sealed class GrimmchildGift {
    /// <summary>El jugador recibió Grimmchild (→ todos).</summary>
    public const MessageType CharmGift = (MessageType) 95;

    private const int GrimmchildCharm = 40;
    private const string GotCharm = "gotCharm_40";

    private readonly MessageHandler _onGift;
    private GameSession _session;

    /// <summary>Lo último visto (para saber cuándo cambia): -1 sin mirar aún.</summary>
    private int _had = -1;

    private float _nextCheck;

    static GrimmchildGift() {
        MessageRoutes.Register(CharmGift, Route.Everyone, reliable: true);
    }

    public GrimmchildGift() {
        _onGift = OnGift;
    }

    public void Attach(GameSession session) {
        _session = session;
        _had = -1;
        session.On(CharmGift, _onGift);
    }

    public void Detach() {
        if (_session != null) {
            _session.On(CharmGift, null);
            _session = null;
        }

        _had = -1;
    }

    /// <summary>Cada frame (mira cada medio segundo, sin crear memoria).</summary>
    public void Update(string scene) {
        if (_session == null || Time.unscaledTime < _nextCheck) {
            return;
        }

        _nextCheck = Time.unscaledTime + 0.5f;
        var pd = PlayerData.instance;
        if (pd == null || scene.Length == 0) {
            // Cargando o en el menú: al volver se mira desde cero (cargar una partida no es recibirlo)
            _had = -1;
            return;
        }

        var has = pd.GetBool(GotCharm) ? 1 : 0;
        if (_had == 0 && has == 1 && _session.CanSend) {
            Log.Info("Grimmchild recibido: se da también a los compañeros");
            var writer = _session.Begin(CharmGift);
            writer.WriteByte(GrimmchildCharm);
            _session.End();
        }

        _had = has;
    }

    private void OnGift(SessionPlayer from, PacketReader reader) {
        var charm = reader.ReadByte();
        var pd = PlayerData.instance;
        if (charm != GrimmchildCharm || pd == null || pd.GetBool(GotCharm)) {
            return;
        }

        pd.SetBool(GotCharm, true);
        pd.SetBool("newCharm_40", true);
        if (pd.GetInt("grimmChildLevel") < 1) {
            pd.SetInt("grimmChildLevel", 1);
        }

        pd.CountCharms();
        // Así este PC no lo vuelve a avisar como recibido por su cuenta
        _had = 1;
        Log.Info($"Grimmchild recibido gracias a {from.Name}");
        if (HostSettings.Current.ShowNotices) {
            Notice.Show(Lang.T($"Recibiste a Grimmchild gracias a {from.Name}", $"You received Grimmchild thanks to {from.Name}"));
        }
    }
}
