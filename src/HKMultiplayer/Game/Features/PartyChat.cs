using System.Collections.Generic;
using System.Text;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Chat de texto durante la partida. La T abre un campo abajo a la izquierda; Enter envía y Esc cancela. Mientras se
/// escribe, ni el caballero ni la pausa leen las teclas (si no, escribir movía al caballero o abría el menú). Los
/// mensajes salen encima del campo con el nombre en el color del jugador y se desvanecen al rato; con el campo abierto
/// se ven los últimos aunque ya se hubieran ido.
/// <para>
/// Lo recibido es de otro PC y se trata como texto sin más: se quitan los caracteres de control y las marcas de formato
/// (&lt; y &gt;) y se corta a <see cref="MaxLength"/>.
/// </para>
/// </summary>
internal sealed class PartyChat {
    /// <summary>Un mensaje de chat (jugador → todos).</summary>
    public const MessageType ChatMessage = (MessageType) 123;

    public const int MaxLength = 100;

    private const int MaxLines = 6;
    private const float LineSeconds = 10f;
    private const float FadeSeconds = 1.5f;
    private const KeyCode OpenKey = KeyCode.T;

    private sealed class Line {
        public string Text;
        public float At;
    }

    private readonly MessageHandler _onMessage;
    private readonly List<Line> _lines = new();
    private readonly StringBuilder _builder = new();
    private GameSession _session;
    private GameObject _root;
    private Text _log;
    private CanvasGroup _logGroup;
    private InputField _input;
    private bool _open;

    /// <summary>Este chat le quitó las teclas al juego (y se las devuelve al cerrarse).</summary>
    private bool _heldInput;

    /// <summary>Si el caballero aceptaba teclas al abrir (sentado en un banco no: al cerrar no se le dan).</summary>
    private bool _heroWasAccepting;

    /// <summary>Frame en que se abrió: la T que lo abrió no debe escribirse en el campo.</summary>
    private int _openedFrame;

    static PartyChat() {
        MessageRoutes.Register(ChatMessage, Route.Everyone, reliable: true);
    }

    public PartyChat() {
        _onMessage = OnMessage;
    }

    public bool IsOpen => _open;

    /// <summary>Abre o cierra el campo como la T y Esc, sin teclado (para las órdenes de diagnóstico).</summary>
    public void TestToggle(bool open) {
        if (open && !_open && CanType()) {
            Open();
        } else if (!open) {
            Close();
        }
    }

    public int Received { get; private set; }

    public void Attach(GameSession session) {
        _session = session;
        session.On(ChatMessage, _onMessage);
    }

    public void Detach() {
        _session?.On(ChatMessage, null);
        _session = null;
        Close();
        _lines.Clear();
        if (_root != null) {
            Object.Destroy(_root);
            _root = null;
        }
    }

    /// <summary>Cada frame: abre o cierra el campo con las teclas y desvanece los mensajes viejos.</summary>
    public void Update() {
        if (_session == null) {
            return;
        }

        if (_open) {
            if (Input.GetKeyDown(KeyCode.Escape) || !CanType()) {
                Close();
            } else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) {
                Send(_input.text);
                Close();
            } else if (Time.frameCount > _openedFrame && !_input.isFocused) {
                _input.ActivateInputField();
            }
        } else if (Input.GetKeyDown(OpenKey) && CanType()) {
            Open();
        }

        RefreshLog();
    }

    /// <summary>
    /// Solo jugando: sin pausa, sin cambio de sala y con el caballero libre (aceptando teclas) o sentado en un banco, que es
    /// donde más se charla. No hablando, en una cinemática ni en un menú del juego.
    /// </summary>
    private bool CanType() {
        var hero = HeroController.instance;
        var gm = GameManager.instance;
        var pd = PlayerData.instance;
        var sitting = pd != null && pd.atBench && hero != null && hero.controlReqlinquished;
        return hero != null && gm != null && !gm.isPaused && !hero.cState.transitioning && !hero.cState.dead &&
               (_heldInput || sitting || hero.acceptingInput && !hero.controlReqlinquished);
    }

    private void Open() {
        Build();
        var hero = HeroController.instance;
        _heroWasAccepting = hero.acceptingInput;
        if (_heroWasAccepting) {
            hero.IgnoreInput();
        }

        InputHandler.Instance?.StopAcceptingInput();
        _heldInput = true;
        _open = true;
        _openedFrame = Time.frameCount;
        _input.text = string.Empty;
        _input.gameObject.SetActive(true);
        // Se activa en el frame siguiente (Update): activado ya, recogía la T que lo abrió
        EventSystem.current?.SetSelectedGameObject(_input.gameObject);
    }

    private void Close() {
        if (!_open) {
            return;
        }

        _open = false;
        if (_input != null) {
            _input.DeactivateInputField();
            _input.text = string.Empty;
            _input.gameObject.SetActive(false);
        }

        if (EventSystem.current != null && _input != null && EventSystem.current.currentSelectedGameObject == _input.gameObject) {
            EventSystem.current.SetSelectedGameObject(null);
        }

        if (_heldInput) {
            _heldInput = false;
            InputHandler.Instance?.StartAcceptingInput();
            if (_heroWasAccepting) {
                HeroController.instance?.AcceptInput();
            }
        }
    }

    /// <summary>Envía un mensaje del jugador local (también lo usan las órdenes de diagnóstico).</summary>
    public bool Send(string text) {
        var clean = Clean(text);
        if (clean.Length == 0 || _session == null || !_session.CanSend) {
            return false;
        }

        var writer = _session.Begin(ChatMessage);
        writer.WriteString(clean);
        _session.End();
        Add(_session.LocalId, _session.LocalPlayer?.Name ?? "?", clean);
        return true;
    }

    private void OnMessage(SessionPlayer from, PacketReader reader) {
        var text = Clean(reader.ReadString(MaxLength * 4));
        if (from.IsLocal || text.Length == 0) {
            return;
        }

        Received++;
        Add(from.Id, from.Name, text);
    }

    private void Add(ushort playerId, string name, string text) {
        Log.Info($"Chat {name}: {text}");
        var color = ColorUtility.ToHtmlStringRGB(PartnerMap.ColorOf(playerId));
        _lines.Add(new Line { Text = $"<color=#{color}>{Clean(name)}</color>: {text}", At = Time.unscaledTime });
        while (_lines.Count > MaxLines) {
            _lines.RemoveAt(0);
        }

        Build();
    }

    /// <summary>Texto sin caracteres de control ni marcas de formato, y como mucho <see cref="MaxLength"/> caracteres.</summary>
    public static string Clean(string text) {
        if (string.IsNullOrEmpty(text)) {
            return string.Empty;
        }

        var clean = new StringBuilder(Mathf.Min(text.Length, MaxLength));
        foreach (var c in text) {
            if (clean.Length >= MaxLength) {
                break;
            }

            clean.Append(c switch {
                '<' => '‹',
                '>' => '›',
                _ when char.IsControl(c) => ' ',
                _ => c
            });
        }

        return clean.ToString().Trim();
    }

    private void RefreshLog() {
        if (_log == null) {
            return;
        }

        var now = Time.unscaledTime;
        _builder.Clear();
        var newest = 0f;
        foreach (var line in _lines) {
            if (!_open && now - line.At > LineSeconds) {
                continue;
            }

            if (_builder.Length > 0) {
                _builder.Append('\n');
            }

            _builder.Append(line.Text);
            newest = Mathf.Max(newest, line.At);
        }

        var text = _builder.ToString();
        if (_log.text != text) {
            _log.text = text;
        }

        var age = now - newest;
        _logGroup.alpha = _open || text.Length == 0 ? 1f
            : age > LineSeconds - FadeSeconds ? Mathf.Clamp01((LineSeconds - age) / FadeSeconds) : 1f;
    }

    /// <summary>El lienzo del chat, una vez: los mensajes y, debajo, el campo (apagado hasta abrirlo).</summary>
    private void Build() {
        if (_root != null) {
            return;
        }

        _root = new GameObject("HKMultiplayer Chat", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Object.DontDestroyOnLoad(_root);
        var canvas = _root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 490;
        var scaler = _root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var logObject = new GameObject("Mensajes", typeof(RectTransform), typeof(Text), typeof(Shadow), typeof(CanvasGroup));
        logObject.transform.SetParent(_root.transform, false);
        var logRect = (RectTransform) logObject.transform;
        logRect.anchorMin = logRect.anchorMax = logRect.pivot = Vector2.zero;
        logRect.anchoredPosition = new Vector2(48f, 120f);
        logRect.sizeDelta = new Vector2(900f, 300f);
        _log = logObject.GetComponent<Text>();
        _log.font = HudCanvas.BodyFont;
        _log.fontSize = 30;
        _log.alignment = TextAnchor.LowerLeft;
        _log.color = Color.white;
        _log.raycastTarget = false;
        _log.supportRichText = true;
        _log.horizontalOverflow = HorizontalWrapMode.Wrap;
        _log.verticalOverflow = VerticalWrapMode.Overflow;
        logObject.GetComponent<Shadow>().effectDistance = new Vector2(2f, -2f);
        _logGroup = logObject.GetComponent<CanvasGroup>();

        var fieldObject = new GameObject("Campo", typeof(RectTransform), typeof(Image), typeof(InputField));
        fieldObject.transform.SetParent(_root.transform, false);
        var fieldRect = (RectTransform) fieldObject.transform;
        fieldRect.anchorMin = fieldRect.anchorMax = fieldRect.pivot = Vector2.zero;
        fieldRect.anchoredPosition = new Vector2(40f, 60f);
        fieldRect.sizeDelta = new Vector2(900f, 52f);
        var background = fieldObject.GetComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.55f);

        Text MakeText(string name, Color color) {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(fieldObject.transform, false);
            var rect = (RectTransform) textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(14f, 2f);
            rect.offsetMax = new Vector2(-14f, -2f);
            var text = textObject.GetComponent<Text>();
            text.font = HudCanvas.BodyFont;
            text.fontSize = 30;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = color;
            text.supportRichText = false;
            return text;
        }

        _input = fieldObject.GetComponent<InputField>();
        _input.textComponent = MakeText("Texto", Color.white);
        _input.placeholder = MakeText("Ayuda", new Color(1f, 1f, 1f, 0.4f));
        ((Text) _input.placeholder).text = Lang.T("Escribe y pulsa Enter (Esc para cancelar)", "Type and press Enter (Esc to cancel)");
        _input.characterLimit = MaxLength;
        _input.lineType = InputField.LineType.SingleLine;
        _input.caretColor = Color.white;
        _input.targetGraphic = background;
        fieldObject.SetActive(false);
    }
}
