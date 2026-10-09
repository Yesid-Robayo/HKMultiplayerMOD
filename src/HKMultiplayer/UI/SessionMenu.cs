using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GlobalEnums;
using HKMultiplayer.Game;
using HKMultiplayer.Game.Features;
using HKMultiplayer.Game.World;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// "Multijugador" en el menú de pausa (solo durante una partida multijugador). Páginas: Partida (código, IP oculta hasta
/// pedirla, jugadores con su ping y cerrar o salir), Ajustes de la partida (los decide el host; los invitados los ven sin
/// poder cambiarlos) y Compañeros (vida de cada uno, dar geo, desatascarse y recargar la sala). El botón es una copia del
/// de "Opciones" de la pausa y la pantalla tiene el mismo estilo que el resto de menús.
/// </summary>
internal sealed class SessionMenu : GameStyledMenu {
    public static SessionMenu Instance { get; private set; }

    private const int MainPage = 0;
    private const int LeavePage = 1;
    private const int SettingsPage = 2;
    private const int MoreSettingsPage = 3;
    private const int PartnersPage = 4;

    /// <summary>Ajustes de la primera página (dificultad y revivir); el resto va en la segunda.</summary>
    private const int FirstPageOptions = 8;

    private const int MaxPartnerRows = 3;

    /// <summary>Cantidades de geo que se pueden dar (0 = todo).</summary>
    private static readonly int[] GeoAmounts = { 10, 50, 100, 200, 500, 0 };

    private static readonly string[] GeoLabels = { "10", "50", "100", "200", "500", "Todo" };
    private const string ButtonName = "MultiplayerButton";

    private static PauseMenuButton _pauseButton;
    private static bool _hooked;
    private static readonly List<(RectTransform Rect, Vector2 Position)> OriginalLayout = new();

    private Text _header;
    private Text _code;
    private Text _address;
    private Text _players;
    private MenuButton _showAddress;
    private MenuButton _leave;
    private Text _leaveQuestion;
    private Text _leaveDetail;
    private MenuButton _leaveConfirm;
    private bool _addressVisible;
    private float _nextRefresh;

    private readonly MenuOptionHorizontal[] _settings = new MenuOptionHorizontal[HostSettings.Options.Length];
    private Text _settingsNote;
    private Text _moreSettingsNote;
    private readonly Text[] _partnerTexts = new Text[MaxPartnerRows];
    private readonly MenuButton[] _partnerButtons = new MenuButton[MaxPartnerRows];
    private readonly ushort[] _partnerIds = new ushort[MaxPartnerRows];
    private Text _partnersMessage;
    private Text _ownGeo;
    private int _geoIndex = 2;

    /// <summary>
    /// Crea el botón de la pausa y la pantalla si faltan, y muestra el botón solo durante una partida multijugador.
    /// Barato: llamar cada frame.
    /// </summary>
    public static void UpdateInstalled(bool inSession) {
        var ui = UIManager.instance;
        if (ui == null || ui.pauseMenuScreen == null || ui.extrasMenuScreen == null) {
            return;
        }

        if (_pauseButton == null) {
            try {
                Build(ui);
            } catch (Exception e) {
                Log.Error($"No se pudo añadir Multijugador al menú de pausa: {e}");
                return;
            }
        }

        if (_pauseButton.gameObject.activeSelf != inSession) {
            SetButtonVisible(inSession);
        }
    }

    private static void Build(UIManager ui) {
        if (!_hooked) {
            _hooked = true;
            On.UnityEngine.UI.PauseMenuButton.OnSubmit += (orig, self, eventData) => {
                if (self.name != ButtonName) {
                    orig(self, eventData);
                    return;
                }

                if (self.flashEffect != null) {
                    self.flashEffect.ResetTrigger("Flash");
                    self.flashEffect.SetTrigger("Flash");
                }

                self.ForceDeselect();
                Instance?.OpenFromPause();
            };
        }

        var pause = ui.pauseMenuScreen;
        var options = pause.GetComponentsInChildren<PauseMenuButton>(true).First(b => b.name == "OptionsButton");
        var buttonObject = Instantiate(options.gameObject, options.transform.parent);
        buttonObject.name = ButtonName;
        buttonObject.transform.SetSiblingIndex(options.transform.GetSiblingIndex() + 1);
        RemoveLocalization(buttonObject);
        buttonObject.GetComponentInChildren<Text>(true).text = "Multijugador";
        _pauseButton = buttonObject.GetComponent<PauseMenuButton>();

        // Posiciones originales de los botones y adornos de la pausa (se separan para hacer sitio)
        OriginalLayout.Clear();
        foreach (Transform child in options.transform.parent) {
            if (child != buttonObject.transform) {
                OriginalLayout.Add(((RectTransform) child, ((RectTransform) child).anchoredPosition));
            }
        }

        foreach (var fleur in new[] { "TopFleur", "BottomFleur" }) {
            var rect = pause.transform.Find(fleur) as RectTransform;
            if (rect != null) {
                OriginalLayout.Add((rect, rect.anchoredPosition));
            }
        }

        buttonObject.SetActive(false);
        Instance = CreateScreen<SessionMenu>(ui, "MultiplayerSessionScreen", "Multijugador");
        Instance.BuildPages();
        Log.Info("Multijugador añadido al menú de pausa");
    }

    /// <summary>Muestra u oculta el botón y separa los demás para que quepa (paso de 110 como el juego).</summary>
    private static void SetButtonVisible(bool visible) {
        foreach (var (rect, position) in OriginalLayout) {
            rect.anchoredPosition = position;
        }

        _pauseButton.gameObject.SetActive(visible);
        if (visible) {
            foreach (var (rect, position) in OriginalLayout) {
                switch (rect.name) {
                    case "ContinueButton":
                    case "OptionsButton":
                    case "TopFleur":
                        rect.anchoredPosition = position + new Vector2(0f, 55f);
                        break;
                    case "ExitButton":
                    case "BottomFleur":
                        rect.anchoredPosition = position - new Vector2(0f, 55f);
                        break;
                }
            }

            var options = OriginalLayout.First(e => e.Rect.name == "OptionsButton");
            ((RectTransform) _pauseButton.transform).anchoredPosition = options.Position - new Vector2(0f, 55f);
        }

        var list = UIManager.instance.pauseMenuScreen.GetComponent<MenuButtonList>();
        if (list != null) {
            if (visible) {
                list.AddSelectable(_pauseButton, 2);
            } else {
                // Se rehace la lista sin el botón
                var selectables = UIManager.instance.pauseMenuScreen.GetComponentsInChildren<PauseMenuButton>(false)
                    .Where(b => b != _pauseButton).Cast<Selectable>().ToList();
                list.ClearSelectables();
                foreach (var selectable in selectables) {
                    list.AddSelectable(selectable);
                }
            }

            list.RecalculateNavigation();
        }
    }

    private void BuildPages() {
        var main = NewPage(MainPage);
        _header = AddText(main, string.Empty, 44, title: true);
        AddSpace(main, 10);
        _code = AddText(main, string.Empty, 40);
        _address = AddText(main, string.Empty, 36);
        var row = AddRow(main, "Acciones", 84, 10);
        AddButton(row, "Copiar código", CopyCode, 36, 520);
        _showAddress = AddButton(row, "Mostrar mi IP", ToggleAddress, 36, 520);
        AddSpace(main, 10);
        AddText(main, "Jugadores", 40, title: true);
        _players = AddText(main, string.Empty, 36);
        _players.GetComponent<LayoutElement>().preferredHeight = 150;
        _players.alignment = TextAnchor.UpperCenter;
        var pages = AddRow(main, "Páginas", 84, 10);
        AddButton(pages, "Ajustes de la partida", () => ShowPage(SettingsPage), 36, 620);
        AddButton(pages, "Compañeros", () => ShowPage(PartnersPage), 36, 520);
        _leave = AddButton(main, "Cerrar partida", () => ShowPage(LeavePage));

        var leave = NewPage(LeavePage);
        _leaveQuestion = AddText(leave, string.Empty, 44, title: true);
        _leaveDetail = AddText(leave, string.Empty, 36);
        AddSpace(leave, 30);
        AddButton(leave, "Cancelar", () => ShowPage(MainPage));
        _leaveConfirm = AddButton(leave, "Cerrar partida", Leave);

        BuildSettingsPages();
        BuildPartnersPage();
        ShowPage(MainPage, instant: true);
    }

    /// <summary>Ajustes de la partida en dos páginas (dificultad y revivir; pantalla y permisos).</summary>
    private void BuildSettingsPages() {
        var settings = HostSettings.Current;
        var first = NewPage(SettingsPage);
        _settingsNote = AddText(first, string.Empty, 32);
        var second = NewPage(MoreSettingsPage);
        _moreSettingsNote = AddText(second, string.Empty, 32);
        var options = HostSettings.Options;
        for (var i = 0; i < options.Length; i++) {
            var option = options[i];
            var page = i < FirstPageOptions ? first : second;
            _settings[i] = AddOption(page, option.Label, option.Labels, settings.IndexOf(option),
                index => HostSettings.Current.Set(option, index));
        }

        AddSpace(first, 6);
        AddButton(first, "Más ajustes", () => ShowPage(MoreSettingsPage), 36);
        AddSpace(second, 6);
        AddButton(second, "Ajustes anteriores", () => ShowPage(SettingsPage), 36);
    }

    /// <summary>Compañeros: su vida y alma, dar geo, desatascarse y recargar la sala.</summary>
    private void BuildPartnersPage() {
        var page = NewPage(PartnersPage);
        _ownGeo = AddText(page, string.Empty, 34);
        AddOption(page, "Geo a dar", GeoLabels, _geoIndex, index => _geoIndex = index);
        for (var i = 0; i < MaxPartnerRows; i++) {
            var row = i;
            _partnerTexts[i] = AddText(page, string.Empty, 32);
            _partnerButtons[i] = AddButton(page, "Dar geo", () => GiveGeo(row), 34);
        }

        AddSpace(page, 6);
        var help = AddRow(page, "Ayuda", 84, 10);
        AddButton(help, "Desatascar", () => Resume(MultiplayerRunner.Instance.Actions.RequestUnstuck()), 36, 420);
        AddButton(help, "Recargar sala", () => Resume(MultiplayerRunner.Instance.Actions.RequestReload()), 36, 420);
        _partnersMessage = AddText(page, string.Empty, 30);
    }

    private void GiveGeo(int row) {
        var pd = PlayerData.instance;
        var amount = GeoAmounts[_geoIndex];
        if (amount == 0 && pd != null) {
            amount = pd.geo;
        }

        var error = MultiplayerRunner.Instance.Actions.TryGiftGeo(_partnerIds[row], amount);
        var name = PartyState.Current?.Get(_partnerIds[row])?.Name ?? "?";
        _partnersMessage.text = error ?? $"Le diste {amount} geo a {name}";
        Refresh();
    }

    /// <summary>Si la acción se aceptó, vuelve al juego (se hace al quitar la pausa); si no, dice por qué.</summary>
    private void Resume(string error) {
        if (error != null) {
            _partnersMessage.text = error;
            return;
        }

        UIManager.instance.StartCoroutine(ResumeRoutine());
    }

    private IEnumerator ResumeRoutine() {
        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(Screen));
        ui.UIGoToPauseMenu();
        Busy = false;
        // Quitar la pausa como con Esc (la acción se hace al volver a jugar)
        yield return new WaitForSecondsRealtime(0.3f);
        if (GameManager.instance != null && GameManager.instance.isPaused) {
            ui.TogglePauseGame();
        }
    }

    // =====================================================================================================
    // Navegación
    // =====================================================================================================

    private void OpenFromPause() {
        if (Busy || MultiplayerRunner.Instance.Session == null) {
            return;
        }

        _addressVisible = false;
        Refresh();
        ShowPage(MainPage, instant: true);
        UIManager.instance.StartCoroutine(OpenRoutine());
    }

    private IEnumerator OpenRoutine() {
        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(ui.pauseMenuScreen));
        ui.currentDynamicMenu = Screen;
        SetMenuState?.Invoke(ui, new object[] { MainMenuState.DYNAMIC_MENU });
        yield return ui.StartCoroutine(ui.ShowMenu(Screen));
        Busy = false;
        SelectFirst();
    }

    protected override void OnBack() {
        if (Busy) {
            return;
        }

        if (CurrentPage == MoreSettingsPage) {
            ShowPage(SettingsPage);
            return;
        }

        if (CurrentPage != MainPage) {
            ShowPage(MainPage);
            return;
        }

        UIManager.instance.StartCoroutine(BackToPauseRoutine());
    }

    private IEnumerator BackToPauseRoutine() {
        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(Screen));
        ui.UIGoToPauseMenu();
        Busy = false;
    }

    protected override void OnPageShowing(int page) => Refresh();

    // =====================================================================================================
    // Contenido
    // =====================================================================================================

    private void Update() {
        if (Time.unscaledTime >= _nextRefresh) {
            _nextRefresh = Time.unscaledTime + 0.5f;
            Refresh();
        }
    }

    private void Refresh() {
        var session = MultiplayerRunner.Instance?.Session;
        if (session == null) {
            return;
        }

        var world = CoopSaves.ActiveWorld;
        _header.text = session.IsHost
            ? world != null ? $"Mundo «{world.Name}»" : "Partida abierta"
            : session.IsActive ? "Conectado a la partida" : "Conectando...";
        _code.text = $"Código de la partida: {MultiplayerRunner.FormatCode(session.RoomCode)}";
        _address.text = session.IsHost
            ? _addressVisible
                ? $"Tu IP: {MultiplayerRunner.LocalAddresses()}   ·   puerto {session.Port}"
                : "Tu IP está oculta"
            : string.Empty;
        _showAddress.gameObject.SetActive(session.IsHost);
        SetButtonText(_showAddress, _addressVisible ? "Ocultar mi IP" : "Mostrar mi IP");

        var text = new StringBuilder();
        foreach (var player in session.Players.OrderBy(p => p.Id)) {
            text.Append(player.Name);
            if (player.IsLocal) {
                text.Append(" (tú)");
            }

            if (player.Id == Session.GameSession.HostPlayerId) {
                text.Append("  ·  host");
            } else {
                text.Append($"  ·  {player.Ping} ms");
            }

            text.Append('\n');
        }

        _players.text = text.ToString().TrimEnd('\n');

        var leaveText = session.IsHost ? "Cerrar partida" : "Salir de la partida";
        SetButtonText(_leave, leaveText);
        SetButtonText(_leaveConfirm, leaveText);
        _leaveQuestion.text = session.IsHost ? "¿Cerrar la partida?" : "¿Salir de la partida?";
        _leaveDetail.text = session.IsHost
            ? "Los demás jugadores se desconectarán. Tú sigues jugando en tu mundo."
            : "Tu personaje se guarda en el mundo del host y vuelves al menú principal.";
        RefreshSettings(session.IsHost);
        RefreshPartners(session);
    }

    /// <summary>Los valores que rigen (en un invitado, los que dice el host) y si se pueden cambiar.</summary>
    private void RefreshSettings(bool editable) {
        var settings = HostSettings.Current;
        var options = HostSettings.Options;
        for (var i = 0; i < options.Length; i++) {
            var option = _settings[i];
            if (option == null) {
                continue;
            }

            if (option.interactable != editable) {
                option.interactable = editable;
            }

            if (option.selectedOptionIndex != settings.IndexOf(options[i])) {
                SetOptionValue(option, settings.IndexOf(options[i]));
            }
        }

        var note = editable
            ? "Se aplican a todos al momento y se guardan en este mundo"
            : "Los decide el host: aquí solo se ven";
        _settingsNote.text = note;
        _moreSettingsNote.text = note;
    }

    private void RefreshPartners(Session.GameSession session) {
        var pd = PlayerData.instance;
        _ownGeo.text = pd != null ? $"Tu geo: {pd.geo}" : string.Empty;
        var party = PartyState.Current;
        var vitals = MultiplayerRunner.Instance.Vitals;
        var row = 0;
        if (party != null) {
            foreach (var member in party.Members) {
                if (member.Id == session.LocalId || row >= MaxPartnerRows) {
                    continue;
                }

                var entry = vitals.Get(member.Id);
                var text = new StringBuilder(member.Name);
                if (member.Status == PlayerStatus.Downed) {
                    text.Append("  ·  caído");
                } else if (entry != null) {
                    text.Append($"  ·  {entry.Health}/{entry.MaxHealth} máscaras");
                    if (entry.Blue > 0) {
                        text.Append($" (+{entry.Blue} azules)");
                    }

                    text.Append($"  ·  {entry.Soul} de alma  ·  {Zones.Name(entry.Zone)}");
                }

                _partnerIds[row] = member.Id;
                _partnerTexts[row].text = text.ToString();
                _partnerTexts[row].gameObject.SetActive(true);
                _partnerButtons[row].gameObject.SetActive(true);
                SetButtonText(_partnerButtons[row], $"Dar geo a {member.Name}");
                _partnerButtons[row].interactable = HostSettings.Current.AllowGeoGifts;
                row++;
            }
        }

        for (var i = row; i < MaxPartnerRows; i++) {
            _partnerTexts[i].gameObject.SetActive(i == 0);
            _partnerTexts[i].text = i == 0 ? "No hay compañeros conectados" : string.Empty;
            _partnerButtons[i].gameObject.SetActive(false);
        }
    }

    private void CopyCode() {
        var session = MultiplayerRunner.Instance?.Session;
        if (session != null) {
            GUIUtility.systemCopyBuffer = MultiplayerRunner.FormatCode(session.RoomCode);
            Notice.Show("Código copiado", 2f);
        }
    }

    private void ToggleAddress() {
        _addressVisible = !_addressVisible;
        Refresh();
    }

    private void Leave() {
        var runner = MultiplayerRunner.Instance;
        if (runner.Session is { IsHost: false } && CoopSaves.ActiveWorld != null) {
            // Mundo del host: se guarda el personaje y se vuelve al menú principal
            runner.LeaveFromGame();
            return;
        }

        // Si es el host, antes de cerrar recoge el personaje de cada invitado
        runner.CloseGame();
        UIManager.instance.StartCoroutine(BackToPauseRoutine());
    }

    /// <summary>Abre la pantalla como el botón de la pausa (modo de prueba).</summary>
    public void TestOpen() => OpenFromPause();

    /// <summary>Muestra una página (modo de prueba): partida, ajustes, ajustes2, compañeros.</summary>
    public bool TestPage(string name) {
        var page = name switch {
            "partida" => MainPage,
            "ajustes" => SettingsPage,
            "ajustes2" => MoreSettingsPage,
            "compañeros" or "companeros" => PartnersPage,
            _ => -1
        };
        if (page < 0) {
            return false;
        }

        ShowPage(page, instant: true);
        return true;
    }
}
