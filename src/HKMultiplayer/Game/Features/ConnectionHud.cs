using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Aviso de conexión arriba en el centro, como en los juegos en línea: un icono de señal y un texto que solo salen cuando
/// algo va mal. Ping alto en amarillo; sin datos del otro lado, en rojo con los segundos que lleva; y al volver, «Conexión
/// recuperada» en verde un momento. Antes un microcorte no se veía y a los 10 s echaba al invitado (issue #29); ahora la
/// conexión aguanta hasta <see cref="NetConstants.TimeoutSeconds"/> y el jugador sabe por qué su compañero se quedó quieto.
/// <para>
/// En el invitado mira el enlace con el host (todo le llega por él); en el host, el del invitado que peor va (ver
/// <see cref="GameSession.TryGetWorstLink"/>). Todo se crea al principio; cada frame solo cambia colores y, si hace falta,
/// el texto.
/// </para>
/// </summary>
internal sealed class ConnectionHud {
    private const int BarCount = 4;

    /// <summary>Segundos sin datos para avisar de que la conexión va mal y para darla por perdida (hasta que vuelva o se corte).</summary>
    private const double UnstableSeconds = 1.5;

    private const double LostSeconds = 5;

    /// <summary>Latencia (ms) a partir de la que se avisa, y por debajo de la que se quita el aviso (para que no parpadee).</summary>
    private const int HighPingMs = 250;

    private const int HighPingClearMs = 200;

    /// <summary>Lo que se ve «Conexión recuperada».</summary>
    private const float RecoveredSeconds = 4f;

    private static readonly Color Yellow = new(1f, 0.85f, 0.3f, 1f);
    private static readonly Color Red = new(1f, 0.38f, 0.32f, 1f);
    private static readonly Color Green = new(0.55f, 1f, 0.6f, 1f);
    private static readonly Color Dim = new(1f, 1f, 1f, 0.25f);

    private enum Level {
        None,
        Recovered,
        HighPing,
        Unstable,
        Lost
    }

    private GameSession _session;
    private RectTransform _root;
    private readonly Image[] _bars = new Image[BarCount];
    private Text _text;
    private Image _background;
    private bool _visible;

    private Level _shown = Level.None;
    private int _shownValue;
    private string _shownName;
    private bool _highPing;
    private bool _wasSilent;
    private float _recoveredUntil;

    public void Attach(GameSession session) {
        _session = session;
        _highPing = _wasSilent = false;
        _recoveredUntil = 0f;
    }

    public void Detach() {
        _session = null;
        SetVisible(false);
    }

    private void Build() {
        _root = HudCanvas.NewGroup("Conexión");
        _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 1f);
        // Debajo de la fila de máscaras de los compañeros (que con mucha vida llega hasta el centro)
        _root.anchoredPosition = new Vector2(0f, -150f);
        // Fondo oscuro detrás del icono y el texto: sobre una sala clara no se leía
        _background = HudCanvas.NewImage("Fondo", _root, null, new Vector2(400f, 52f), new Color(0f, 0f, 0f, 0.6f));
        _background.rectTransform.anchorMin = _background.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        // Barras de señal de menos a más altas, a la izquierda del texto
        for (var i = 0; i < BarCount; i++) {
            var height = 10f + i * 7f;
            var bar = HudCanvas.NewImage($"Barra {i + 1}", _root, null, new Vector2(7f, height), Dim);
            bar.rectTransform.anchorMin = bar.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0f);
            _bars[i] = bar;
        }

        _text = HudCanvas.NewText("Texto", _root, 30, TextAnchor.MiddleLeft);
        _text.rectTransform.anchorMin = _text.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        _text.rectTransform.pivot = new Vector2(0f, 0.5f);
        _root.gameObject.SetActive(false);
    }

    /// <summary>Centra el icono y el texto (y ajusta el fondo) al ancho del texto que se acaba de poner.</summary>
    private void Layout() {
        const float icon = 38f;
        const float gap = 14f;
        const float padding = 22f;
        var width = icon + gap + _text.preferredWidth;
        var left = -width / 2f;
        for (var i = 0; i < BarCount; i++) {
            _bars[i].rectTransform.anchoredPosition = new Vector2(left + 4f + i * 10f, -15f);
        }

        _text.rectTransform.anchoredPosition = new Vector2(left + icon + gap, 0f);
        _background.rectTransform.sizeDelta = new Vector2(width + padding * 2f, 52f);
    }

    private void SetVisible(bool visible) {
        if (_visible == visible) {
            return;
        }

        _visible = visible;
        if (_root != null) {
            _root.gameObject.SetActive(visible);
        }
    }

    /// <summary>Llamar una vez por frame, con el resto del HUD.</summary>
    public void Update(string scene) {
        if (_session == null || scene.Length == 0 || HudCanvas.GameHudHidden() ||
            !_session.TryGetWorstLink(out var player, out var silent, out var ping)) {
            _wasSilent = false;
            SetVisible(false);
            return;
        }

        _highPing = _highPing ? ping >= HighPingClearMs : ping >= HighPingMs;
        var level = silent >= LostSeconds ? Level.Lost
            : silent >= UnstableSeconds ? Level.Unstable
            : _highPing ? Level.HighPing
            : Time.unscaledTime < _recoveredUntil ? Level.Recovered
            : Level.None;
        if (level is Level.Unstable or Level.Lost) {
            _wasSilent = true;
        } else if (_wasSilent && silent < 0.5) {
            // Volvió la red: lo pendiente ya se está poniendo al día (ver NetConstants.TimeoutSeconds)
            _wasSilent = false;
            _recoveredUntil = Time.unscaledTime + RecoveredSeconds;
            level = _highPing ? Level.HighPing : Level.Recovered;
        }

        if (level == Level.None) {
            SetVisible(false);
            return;
        }

        if (_root == null) {
            Build();
        }

        SetVisible(true);
        // El texto solo se rehace cuando cambia lo que dice (los segundos o el ping, de 10 en 10 ms)
        var value = level == Level.Lost ? (int) silent : level == Level.HighPing ? ping / 10 * 10 : 0;
        var name = player?.Name;
        if (level != _shown || value != _shownValue || !ReferenceEquals(name, _shownName)) {
            _shown = level;
            _shownValue = value;
            _shownName = name;
            var who = !string.IsNullOrEmpty(name) ? name : Lang.T("el host", "the host");
            _text.text = level switch {
                Level.Lost => Lang.F("Sin conexión con {0}: esperando ({1} s)", "No connection with {0}: waiting ({1} s)",
                    who, value),
                Level.Unstable => Lang.F("Conexión inestable con {0}", "Unstable connection with {0}", who),
                Level.HighPing => Lang.F("Ping alto: {0} ms", "High ping: {0} ms", value),
                _ => Lang.T("Conexión recuperada", "Connection restored")
            };
            var color = level switch {
                Level.Lost or Level.Unstable => Red,
                Level.HighPing => Yellow,
                _ => Green
            };
            _text.color = color;
            var lit = level switch {
                Level.Lost => 0,
                Level.Unstable => 1,
                Level.HighPing => 2,
                _ => BarCount
            };
            for (var i = 0; i < BarCount; i++) {
                _bars[i].color = i < lit ? color : Dim;
            }

            Layout();
        }
    }
}
