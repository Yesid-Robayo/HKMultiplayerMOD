using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GlobalEnums;
using HKMultiplayer.Game;
using HKMultiplayer.Game.World;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// "Multijugador" en el menú de pausa (solo durante una partida multijugador): código de la partida, IP (oculta hasta
/// pedirla), jugadores con su ping y cerrar o salir de la partida. El botón es una copia del de "Opciones" de la pausa
/// y la pantalla tiene el mismo estilo que el resto de menús.
/// </summary>
internal sealed class SessionMenu : GameStyledMenu {
    public static SessionMenu Instance { get; private set; }

    private const int MainPage = 0;
    private const int LeavePage = 1;
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
        _players.GetComponent<LayoutElement>().preferredHeight = 200;
        _players.alignment = TextAnchor.UpperCenter;
        _leave = AddButton(main, "Cerrar partida", () => ShowPage(LeavePage));

        var leave = NewPage(LeavePage);
        _leaveQuestion = AddText(leave, string.Empty, 44, title: true);
        _leaveDetail = AddText(leave, string.Empty, 36);
        AddSpace(leave, 30);
        AddButton(leave, "Cancelar", () => ShowPage(MainPage));
        _leaveConfirm = AddButton(leave, "Cerrar partida", Leave);

        ShowPage(MainPage, instant: true);
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
}
