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
/// poder cambiarlos) y Compañeros (vida de cada uno, dar geo, ir con él, desatascarse y recargar la sala). El botón es una copia del
/// de "Opciones" de la pausa y la pantalla tiene el mismo estilo que el resto de menús.
/// </summary>
internal sealed class SessionMenu : GameStyledMenu {
    public static SessionMenu Instance { get; private set; }

    private const int MainPage = 0;
    private const int LeavePage = 1;
    private const int SettingsPage = 2;
    private const int MoreSettingsPage = 3;
    private const int PartnersPage = 4;
    private const int SkinPage = 5;

    /// <summary>
    /// Ajustes de la primera página (dificultad y revivir: 10); el resto (pantalla y permisos: 9) va en la segunda. Así
    /// las dos tienen casi las mismas filas (con 8 y 11 la segunda quedaba apretada).
    /// </summary>
    private const int FirstPageOptions = 10;

    private const int MaxPartnerRows = 3;

    /// <summary>Cantidades de geo que se pueden dar (0 = todo).</summary>
    private static readonly int[] GeoAmounts = { 10, 50, 100, 200, 500, 0 };

    private static readonly string[] GeoLabels = { "10", "50", "100", "200", "500", Lang.T("Todo", "All") };
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
    private readonly MenuButton[] _goToButtons = new MenuButton[MaxPartnerRows];
    private readonly GameObject[] _partnerRows = new GameObject[MaxPartnerRows];
    private readonly ushort[] _partnerIds = new ushort[MaxPartnerRows];
    private Text _partnersMessage;
    private Text _ownGeo;
    private int _geoIndex = 2;

    // Página "Skin" (ajustes de este PC, no del host)
    private MenuOptionHorizontal _skinOption;
    private MenuOptionHorizontal _showSkinsOption;
    private Text _skinStatus;
    private string[] _skinChoices = { "Normal" };

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
        buttonObject.GetComponentInChildren<Text>(true).text = Lang.T("Multijugador", "Multiplayer");
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
        Instance = CreateScreen<SessionMenu>(ui, "MultiplayerSessionScreen", Lang.T("Multijugador", "Multiplayer"));
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
        AddButton(row, Lang.T("Copiar código", "Copy code"), CopyCode, 36, 520);
        _showAddress = AddButton(row, Lang.T("Mostrar mi IP", "Show my IP"), ToggleAddress, 36, 520);
        AddSpace(main, 10);
        AddText(main, Lang.T("Jugadores", "Players"), 40, title: true);
        _players = AddText(main, string.Empty, 36);
        _players.GetComponent<LayoutElement>().preferredHeight = 150;
        _players.alignment = TextAnchor.UpperCenter;
        var pages = AddRow(main, "Páginas", 84, 10);
        AddButton(pages, Lang.T("Ajustes de la partida", "Game settings"), () => ShowPage(SettingsPage), 36, 560);
        AddButton(pages, Lang.T("Compañeros", "Partners"), () => ShowPage(PartnersPage), 36, 420);
        AddButton(pages, "Skin", () => ShowPage(SkinPage), 36, 260);
        _leave = AddButton(main, Lang.T("Cerrar partida", "Close game"), () => ShowPage(LeavePage));

        var leave = NewPage(LeavePage);
        _leaveQuestion = AddText(leave, string.Empty, 44, title: true);
        _leaveDetail = AddText(leave, string.Empty, 36);
        AddSpace(leave, 30);
        AddButton(leave, Lang.T("Cancelar", "Cancel"), () => ShowPage(MainPage));
        _leaveConfirm = AddButton(leave, Lang.T("Cerrar partida", "Close game"), Leave);

        BuildSettingsPages();
        BuildPartnersPage();
        BuildSkinPage();
        ShowPage(MainPage, instant: true);
    }

    /// <summary>Skin propia y si se ven las de los demás (de este PC; se guardan en sus ajustes).</summary>
    private void BuildSkinPage() {
        var page = NewPage(SkinPage);
        AddText(page, Lang.T("Skins compatibles con CustomKnight: una carpeta con Knight.png (y, si quiere, los hechizos y el HUD)", "CustomKnight-compatible skins: a folder with Knight.png (and, optionally, the spells and the HUD)"), 30);
        _skinOption = AddOption(page, Lang.T("Tu skin", "Your skin"), _skinChoices, 0, index => {
            Skins.SelectLocalSkin(index <= 0 || index >= _skinChoices.Length ? string.Empty : _skinChoices[index]);
            Refresh();
        });
        _showSkinsOption = AddOption(page, Lang.T("Ver las skins de los demás", "Show other players' skins"), Lang.Spanish ? new[] { "Sí", "No" } : new[] { "Yes", "No" },
            Skins.ShowOthers ? 0 : 1, index => Skins.SetShowOthers(index == 0));
        _skinStatus = AddText(page, string.Empty, 30);
        _skinStatus.GetComponent<LayoutElement>().preferredHeight = 120;
        AddSpace(page, 6);
        AddButton(page, Lang.T("Abrir la carpeta de skins", "Open skins folder"), Skins.OpenFolder, 36);
    }

    /// <summary>Lista de skins (puede cambiar con el juego abierto) y qué lleva cada uno.</summary>
    private void RefreshSkins(Session.GameSession session) {
        if (_skinOption == null) {
            return;
        }

        var available = Skins.Available;
        var choices = new string[available.Count + 1];
        choices[0] = Lang.T("Normal", "Default");
        for (var i = 0; i < available.Count; i++) {
            choices[i + 1] = available[i];
        }

        if (!choices.SequenceEqual(_skinChoices)) {
            _skinChoices = choices;
            _skinOption.optionList = choices;
        }

        var selected = Array.IndexOf(_skinChoices, Skins.LocalSkinName);
        SetOptionValue(_skinOption, selected > 0 ? selected : 0);
        SetOptionValue(_showSkinsOption, Skins.ShowOthers ? 0 : 1);
        _skinStatus.text = Skins.Current?.DescribeForPanel(session) ?? string.Empty;
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
        AddButton(first, Lang.T("Más ajustes", "More settings"), () => ShowPage(MoreSettingsPage), 36);
        AddSpace(second, 6);
        AddButton(second, Lang.T("Ajustes anteriores", "Previous settings"), () => ShowPage(SettingsPage), 36);
    }

    /// <summary>Compañeros: su vida y alma, dar geo, ir con él, desatascarse y recargar la sala.</summary>
    private void BuildPartnersPage() {
        var page = NewPage(PartnersPage);
        _ownGeo = AddText(page, string.Empty, 34);
        AddOption(page, Lang.T("Geo a dar", "Geo to give"), GeoLabels, _geoIndex, index => _geoIndex = index);
        for (var i = 0; i < MaxPartnerRows; i++) {
            var row = i;
            _partnerTexts[i] = AddText(page, string.Empty, 32);
            _partnerRows[i] = AddRow(page, $"Compañero {i + 1}", 84, 10);
            _partnerButtons[i] = AddButton(_partnerRows[i], Lang.T("Dar geo", "Give geo"), () => GiveGeo(row), 34, 520);
            _goToButtons[i] = AddButton(_partnerRows[i], Lang.T("Ir con", "Go to"), () => GoTo(row), 34, 520);
        }

        AddSpace(page, 6);
        var help = AddRow(page, "Ayuda", 84, 10);
        AddButton(help, Lang.T("Desatascar", "Unstuck"), () => Resume(MultiplayerRunner.Instance.Actions.RequestUnstuck()), 36, 420);
        AddButton(help, Lang.T("Recargar sala", "Reload room"), () => Resume(MultiplayerRunner.Instance.Actions.RequestReload()), 36, 420);
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
        _partnersMessage.text = error ?? Lang.T($"Le diste {amount} geo a {name}", $"You gave {amount} geo to {name}");
        Refresh();
    }

    /// <summary>Ir junto a ese compañero al quitar la pausa (si el host lo permite).</summary>
    private void GoTo(int row) => Resume(MultiplayerRunner.Instance.Travel.Request(_partnerIds[row]));

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
        // Quitar la pausa como con Esc (la acción se hace al volver a jugar). Mientras el menú de pausa termina de
        // aparecer, el juego ignora quitar la pausa (ignoreUnpause): con una espera fija de 0,3 s a veces no había
        // terminado y la partida se quedaba en pausa sin hacer la acción
        yield return new WaitForSecondsRealtime(0.3f);
        var until = Time.unscaledTime + 3f;
        while (Time.unscaledTime < until && IgnoreUnpauseField?.GetValue(ui) is true) {
            yield return null;
        }

        if (GameManager.instance != null && GameManager.instance.isPaused) {
            ui.TogglePauseGame();
        }
    }

    /// <summary>Si el juego ignora ahora quitar la pausa (privado en UIManager).</summary>
    private static readonly System.Reflection.FieldInfo IgnoreUnpauseField = typeof(UIManager).GetField("ignoreUnpause",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

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
            ? world != null ? Lang.T($"Mundo «{world.Name}»", $"World \"{world.Name}\"") : Lang.T("Partida abierta", "Game open")
            : session.IsActive ? Lang.T("Conectado a la partida", "Connected to the game") : Lang.T("Conectando...", "Connecting...");
        _code.text = Lang.T($"Código de la partida: {MultiplayerRunner.FormatCode(session.RoomCode)}", $"Game code: {MultiplayerRunner.FormatCode(session.RoomCode)}");
        _address.text = session.IsHost
            ? _addressVisible
                ? Lang.T($"Tu IP: {MultiplayerRunner.LocalAddresses()}   ·   puerto {session.Port}", $"Your IP: {MultiplayerRunner.LocalAddresses()}   ·   port {session.Port}")
                : Lang.T("Tu IP está oculta", "Your IP is hidden")
            : string.Empty;
        _showAddress.gameObject.SetActive(session.IsHost);
        SetButtonText(_showAddress, _addressVisible ? Lang.T("Ocultar mi IP", "Hide my IP") : Lang.T("Mostrar mi IP", "Show my IP"));

        var text = new StringBuilder();
        foreach (var player in session.Players.OrderBy(p => p.Id)) {
            text.Append(player.Name);
            if (player.IsLocal) {
                text.Append(Lang.T(" (tú)", " (you)"));
            }

            if (player.Id == Session.GameSession.HostPlayerId) {
                text.Append("  ·  host");
            } else {
                text.Append($"  ·  {player.Ping} ms");
            }

            text.Append('\n');
        }

        _players.text = text.ToString().TrimEnd('\n');

        var leaveText = session.IsHost ? Lang.T("Cerrar partida", "Close game") : Lang.T("Salir de la partida", "Leave game");
        SetButtonText(_leave, leaveText);
        SetButtonText(_leaveConfirm, leaveText);
        _leaveQuestion.text = session.IsHost ? Lang.T("¿Cerrar la partida?", "Close the game?") : Lang.T("¿Salir de la partida?", "Leave the game?");
        _leaveDetail.text = session.IsHost
            ? Lang.T("Los demás jugadores se desconectarán. Tú sigues jugando en tu mundo.", "The other players will be disconnected. You keep playing in your world.")
            : Lang.T("Tu personaje se guarda en el mundo del host y vuelves al menú principal.", "Your character is saved in the host's world and you go back to the main menu.");
        RefreshSettings(session.IsHost);
        RefreshPartners(session);
        RefreshSkins(session);
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
            ? Lang.T("Se aplican a todos al momento y se guardan en este mundo", "They apply to everyone right away and are saved in this world")
            : Lang.T("Los decide el host: aquí solo se ven", "The host decides them: here you can only see them");
        _settingsNote.text = note;
        _moreSettingsNote.text = note;
    }

    private void RefreshPartners(Session.GameSession session) {
        var pd = PlayerData.instance;
        _ownGeo.text = pd != null ? Lang.T($"Tu geo: {pd.geo}", $"Your geo: {pd.geo}") : string.Empty;
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
                    text.Append(Lang.T("  ·  caído", "  ·  down"));
                } else if (member.Paused) {
                    text.Append(Lang.T("  ·  en pausa", "  ·  paused"));
                } else if (entry != null) {
                    text.Append(Lang.T($"  ·  {entry.Health}/{entry.MaxHealth} máscaras", $"  ·  {entry.Health}/{entry.MaxHealth} masks"));
                    if (entry.Blue > 0) {
                        text.Append(Lang.T($" (+{entry.Blue} azules)", $" (+{entry.Blue} blue)"));
                    }

                    // Dónde está: en una cinemática la zona es "ninguna" (salía "None"); en un sueño, se dice así
                    text.Append(Lang.T($"  ·  {entry.Soul} de alma  ·  ", $"  ·  {entry.Soul} soul  ·  "));
                    text.Append(member.Status switch {
                        PlayerStatus.Cutscene => Lang.T("viendo una cinemática", "watching a cutscene"),
                        PlayerStatus.Dream => Lang.T("en un sueño", "in a dream"),
                        PlayerStatus.Joining => Lang.T("entrando en la partida", "joining the game"),
                        _ => entry.Zone == (byte) MapZone.NONE ? Lang.T("cambiando de sala", "changing rooms") : Zones.Name(entry.Zone)
                    });
                }

                _partnerIds[row] = member.Id;
                _partnerTexts[row].text = text.ToString();
                _partnerTexts[row].gameObject.SetActive(true);
                _partnerRows[row].SetActive(true);
                SetButtonText(_partnerButtons[row], Lang.T($"Dar geo a {member.Name}", $"Give geo to {member.Name}"));
                SetButtonEnabled(_partnerButtons[row], HostSettings.Current.AllowGeoGifts);
                SetButtonText(_goToButtons[row], Lang.T($"Ir con {member.Name}", $"Go to {member.Name}"));
                SetButtonEnabled(_goToButtons[row], HostSettings.Current.AllowPartnerTravel);
                row++;
            }
        }

        for (var i = row; i < MaxPartnerRows; i++) {
            _partnerTexts[i].gameObject.SetActive(i == 0);
            _partnerTexts[i].text = i == 0 ? Lang.T("No hay compañeros conectados", "No partners connected") : string.Empty;
            _partnerRows[i].SetActive(false);
        }
    }

    private void CopyCode() {
        var session = MultiplayerRunner.Instance?.Session;
        if (session != null) {
            GUIUtility.systemCopyBuffer = MultiplayerRunner.FormatCode(session.RoomCode);
            Notice.Show(Lang.T("Código copiado", "Code copied"), 2f);
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
            "skin" => SkinPage,
            _ => -1
        };
        if (page < 0) {
            return false;
        }

        ShowPage(page, instant: true);
        return true;
    }
}
