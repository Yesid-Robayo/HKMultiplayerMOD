using System;
using System.Collections;
using GlobalEnums;
using HKMultiplayer.Game;
using HKMultiplayer.Game.World;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Menú "Multijugador" del menú principal: botón bajo "Iniciar partida" (copia del botón del juego) y pantalla con
/// páginas: inicio (nombre y puerto), hostear (hasta 4 mundos coop, borrar, nuevo), nuevo mundo y unirse.
/// </summary>
internal sealed class MultiplayerMenu : GameStyledMenu {
    public static MultiplayerMenu Instance { get; private set; }

    private const int StartPage = 0;
    private const int HostPage = 1;
    private const int NewWorldPage = 2;
    private const int JoinPage = 3;
    private const int DeletePage = 4;

    private InputField _name;
    private InputField _port;
    private InputField _newWorldName;
    private InputField _address;
    private InputField _code;
    private Text _joinStatus;
    private Text _hostStatus;
    private GameObject _worldList;
    private MenuButton _newWorldButton;
    private Text _limitText;
    private Text _deleteQuestion;
    private CoopWorldInfo _pendingDelete;

    private static MenuButton _mainMenuButton;

    /// <summary>Añade el botón y la pantalla si el menú principal aún no los tiene. Barato: llamar cada frame.</summary>
    public static void EnsureInstalled() {
        if (_mainMenuButton != null) {
            return;
        }

        var ui = UIManager.instance;
        if (ui == null || ui.mainMenuScreen == null || ui.extrasMenuScreen == null) {
            return;
        }

        try {
            Build(ui);
        } catch (Exception e) {
            Log.Error($"No se pudo crear el menú Multijugador: {e}");
            _mainMenuButton = ui.mainMenuButtons.startButton;
        }
    }

    private static void Build(UIManager ui) {
        var startButton = ui.mainMenuButtons.startButton;
        var buttonObject = Instantiate(startButton.gameObject, startButton.transform.parent);
        buttonObject.name = "MultiplayerButton";
        buttonObject.transform.SetSiblingIndex(startButton.transform.GetSiblingIndex() + 1);
        var button = buttonObject.GetComponent<MenuButton>();
        MakeCustom(button, "Multijugador", () => Instance?.Open());
        var buttonList = startButton.transform.parent.GetComponent<MenuButtonList>();
        if (buttonList != null) {
            buttonList.AddSelectable(button, 1);
            buttonList.RecalculateNavigation();
        }

        var listRect = (RectTransform) startButton.transform.parent;
        listRect.sizeDelta += new Vector2(0f, ((RectTransform) startButton.transform).sizeDelta.y + 40f);

        Instance = CreateScreen<MultiplayerMenu>(ui, "MultiplayerMenuScreen", "Multijugador");
        Instance.BuildPages();
        _mainMenuButton = button;
        Log.Info("Menú Multijugador añadido al menú principal");
    }

    private void BuildPages() {
        var settings = HKMultiplayerMod.Settings;

        var start = NewPage(StartPage);
        AddText(start, "Juega con amigos en tu red o por internet.", 40);
        AddSpace(start, 20);
        _name = AddInput(start, "Tu nombre", settings.PlayerName, 24, InputField.ContentType.Standard);
        _port = AddInput(start, "Puerto", settings.Port.ToString(), 5, InputField.ContentType.IntegerNumber);
        AddSpace(start, 30);
        AddButton(start, "Hostear un mundo", () => ShowPage(HostPage));
        AddButton(start, "Unirse a un mundo", () => ShowPage(JoinPage));

        var host = NewPage(HostPage);
        AddText(host, $"Elige un mundo coop (hasta {WorldStore.MaxWorlds}). Tus partidas normales no se tocan.", 36);
        AddSpace(host, 10);
        _worldList = new GameObject("Worlds", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _worldList.transform.SetParent(host.transform, false);
        var listLayout = _worldList.GetComponent<VerticalLayoutGroup>();
        listLayout.childControlHeight = true;
        listLayout.childControlWidth = true;
        listLayout.childForceExpandHeight = false;
        AddSpace(host, 10);
        _newWorldButton = AddButton(host, "Nuevo mundo", () => ShowPage(NewWorldPage));
        _limitText = AddText(host, $"Ya tienes {WorldStore.MaxWorlds} mundos: borra uno para crear otro.", 34);
        _hostStatus = AddText(host, string.Empty, 34);

        var newWorld = NewPage(NewWorldPage);
        AddText(newWorld, "Un mundo coop empieza como una partida nueva y lo guarda tu PC.", 38);
        AddSpace(newWorld, 20);
        _newWorldName = AddInput(newWorld, "Nombre del mundo", string.Empty, 40, InputField.ContentType.Standard);
        AddSpace(newWorld, 30);
        AddButton(newWorld, "Crear y hostear", () => {
            var world = WorldStore.Create(_newWorldName.text);
            if (world == null) {
                ShowPage(HostPage);
                return;
            }

            _newWorldName.text = string.Empty;
            UIManager.instance.StartCoroutine(HostRoutine(world));
        });

        var join = NewPage(JoinPage);
        AddText(join, "Pide al host su dirección y el código de la partida.", 38);
        AddSpace(join, 20);
        _address = AddInput(join, "Dirección del host", settings.LastAddress, 64, InputField.ContentType.Standard);
        _code = AddInput(join, "Código", string.Empty, 9, InputField.ContentType.Custom);
        _code.onValidateInput = (_, _, c) => c == '-' || char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '\0';
        AddSpace(join, 30);
        AddButton(join, "Unirse", Join);
        _joinStatus = AddText(join, string.Empty, 34);

        var delete = NewPage(DeletePage);
        _deleteQuestion = AddText(delete, string.Empty, 44, title: true);
        AddText(delete, "Se borra de este PC con todo su progreso y no se puede deshacer.", 36);
        AddSpace(delete, 30);
        AddButton(delete, "Cancelar", () => ShowPage(HostPage));
        AddButton(delete, "Borrar para siempre", () => {
            if (_pendingDelete != null) {
                WorldStore.Delete(_pendingDelete.Id);
                _pendingDelete = null;
            }

            ShowPage(HostPage);
        });

        ShowPage(StartPage, instant: true);
    }

    // =====================================================================================================
    // Navegación
    // =====================================================================================================

    private void Open() {
        if (Busy) {
            return;
        }

        ShowPage(StartPage, instant: true);
        UIManager.instance.StartCoroutine(OpenRoutine());
    }

    /// <summary>Igual que el juego al ir a "Extras": se apaga el título y el menú principal y entra esta pantalla.</summary>
    private IEnumerator OpenRoutine() {
        Busy = true;
        var ui = UIManager.instance;
        var input = GameManager.instance.inputHandler;
        input.StopUIInput();
        ui.StartCoroutine(FadeSprite(ui.gameTitle, 0f));
        ui.subtitleFSM.SendEvent("FADE OUT");
        yield return ui.StartCoroutine(ui.FadeOutCanvasGroup(ui.mainMenuScreen));
        ui.currentDynamicMenu = Screen;
        SetMenuState?.Invoke(ui, new object[] { MainMenuState.DYNAMIC_MENU });
        yield return ui.StartCoroutine(ui.ShowMenu(Screen));
        input.StartUIInput();
        Busy = false;
        SelectFirst();
    }

    protected override void OnBack() {
        if (Busy) {
            return;
        }

        switch (CurrentPage) {
            case StartPage:
                SaveFields();
                UIManager.instance.StartCoroutine(LeaveRoutine());
                break;
            case NewWorldPage:
            case DeletePage:
                ShowPage(HostPage);
                break;
            default:
                ShowPage(StartPage);
                break;
        }
    }

    private IEnumerator LeaveRoutine() {
        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(Screen));
        ui.UIGoToMainMenu();
        Busy = false;
    }

    protected override void OnPageShowing(int page) {
        if (page == HostPage) {
            RefreshWorlds();
        }
    }

    private void RefreshWorlds() {
        foreach (Transform child in _worldList.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        var worlds = WorldStore.List();
        foreach (var world in worlds) {
            var label = world.IsNew
                ? $"{world.Name}  ·  nuevo"
                : $"{world.Name}  ·  {FormatTime(world.PlayTime)}  ·  {AreaName(world.Area)}";
            var target = world;
            var row = AddRow(_worldList, world.Name, 84, 10);
            AddButton(row, label, () => UIManager.instance.StartCoroutine(HostRoutine(target)), 40, 1080);
            AddButton(row, "Borrar", () => {
                _pendingDelete = target;
                _deleteQuestion.text = $"¿Borrar el mundo «{target.Name}»?";
                ShowPage(DeletePage);
            }, 32, 240);
        }

        if (worlds.Count == 0) {
            AddText(_worldList, "Aún no tienes mundos coop.", 36);
        }

        var full = worlds.Count >= WorldStore.MaxWorlds;
        _newWorldButton.gameObject.SetActive(!full);
        _limitText.gameObject.SetActive(full);
        SetFirst(HostPage, null);
    }

    // =====================================================================================================
    // Acciones
    // =====================================================================================================

    private bool TryPort(out int port) => int.TryParse(_port.text, out port) && port is > 0 and < 65536;

    private IEnumerator HostRoutine(CoopWorldInfo world) {
        if (Busy) {
            yield break;
        }

        SaveFields();
        if (!TryPort(out var port)) {
            _hostStatus.text = "El puerto no es válido";
            yield break;
        }

        if (!CoopSaves.CanStart) {
            _hostStatus.text = "Vuelve al menú principal para abrir un mundo coop";
            yield break;
        }

        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(Screen));
        Busy = false;
        var runner = MultiplayerRunner.Instance;
        if (!runner.HostWorld(world, _name.text, port)) {
            // No se pudo: se vuelve a mostrar el menú con el motivo
            _hostStatus.text = runner.StatusMessage;
            yield return ui.StartCoroutine(ui.ShowMenu(Screen));
        }
    }

    private void Join() {
        SaveFields();
        if (!TryPort(out var port)) {
            _joinStatus.text = "El puerto no es válido";
            return;
        }

        MultiplayerRunner.Instance.Join(_address.text, port, _code.text, _name.text);
    }

    private void Update() {
        if (CurrentPage == JoinPage && _joinStatus != null && MultiplayerRunner.Instance != null) {
            var status = MultiplayerRunner.Instance.StatusMessage;
            if (_joinStatus.text != status) {
                _joinStatus.text = status;
            }
        }
    }

    private void SaveFields() {
        var settings = HKMultiplayerMod.Settings;
        if (!string.IsNullOrWhiteSpace(_name.text)) {
            settings.PlayerName = _name.text.Trim();
        }

        settings.LastAddress = _address.text.Trim();
        if (TryPort(out var port)) {
            settings.Port = port;
        }

        HKMultiplayerMod.SaveSettings();
    }

    // =====================================================================================================
    // Textos
    // =====================================================================================================

    private static IEnumerator FadeSprite(SpriteRenderer sprite, float target) {
        if (sprite == null) {
            yield break;
        }

        var color = sprite.color;
        var start = color.a;
        for (var t = 0f; t < 1f; t += Time.unscaledDeltaTime * 3.2f) {
            color.a = Mathf.Lerp(start, target, t);
            sprite.color = color;
            yield return null;
        }

        color.a = target;
        sprite.color = color;
    }

    private static string FormatTime(float seconds) {
        var minutes = (int) (seconds / 60);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    /// <summary>Nombre de la zona en el idioma del juego si existe, si no el interno.</summary>
    public static string AreaName(string mapZone) {
        if (string.IsNullOrEmpty(mapZone)) {
            return "-";
        }

        try {
            var localized = Language.Language.Get(mapZone, "Map Zones");
            return string.IsNullOrEmpty(localized) || localized.StartsWith("#!#") ? mapZone : localized;
        } catch (Exception) {
            return mapZone;
        }
    }

    /// <summary>Abre el menú (modo de prueba).</summary>
    public void TestOpen() => Open();
}
