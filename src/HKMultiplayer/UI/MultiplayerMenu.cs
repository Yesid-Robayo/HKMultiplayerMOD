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
    private Text _updateText;
    private MenuButton _updateButton;
    private string _updateUrl;

    private static readonly string[] YesNo = { "Sí", "No" };
    private static readonly string[] YesNoEnglish = { "Yes", "No" };

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
        MakeCustom(button, Lang.T("Multijugador", "Multiplayer"), () => Instance?.Open());
        var buttonList = startButton.transform.parent.GetComponent<MenuButtonList>();
        if (buttonList != null) {
            buttonList.AddSelectable(button, 1);
            buttonList.RecalculateNavigation();
        }

        var listRect = (RectTransform) startButton.transform.parent;
        listRect.sizeDelta += new Vector2(0f, ((RectTransform) startButton.transform).sizeDelta.y + 40f);

        Instance = CreateScreen<MultiplayerMenu>(ui, "MultiplayerMenuScreen", Lang.T("Multijugador", "Multiplayer"));
        Instance.BuildPages();
        _mainMenuButton = button;
        Log.Info("Menú Multijugador añadido al menú principal");
    }

    private void BuildPages() {
        var settings = HKMultiplayerMod.Settings;

        var start = NewPage(StartPage);
        AddText(start, Lang.T("Juega con amigos en tu red o por internet.", "Play with friends on your network or over the internet."), 40);
        AddSpace(start, 20);
        _name = AddInput(start, Lang.T("Tu nombre", "Your name"), settings.PlayerName, 24, InputField.ContentType.Standard);
        _port = AddInput(start, Lang.T("Puerto", "Port"), settings.Port.ToString(), 5, InputField.ContentType.IntegerNumber);
        AddSpace(start, 30);
        AddButton(start, Lang.T("Hostear un mundo", "Host a world"), () => ShowPage(HostPage));
        AddButton(start, Lang.T("Unirse a un mundo", "Join a world"), () => ShowPage(JoinPage));
        AddSpace(start, 16);
        _updateText = AddText(start, string.Empty, 28);
        _updateButton = AddButton(start, string.Empty, OpenUpdatePage, 34);
        AddOption(start, Lang.T("Buscar actualizaciones", "Check for updates"), Lang.Spanish ? YesNo : YesNoEnglish,
            settings.CheckForUpdates ? 0 : 1, index => {
                HKMultiplayerMod.Settings.CheckForUpdates = index == 0;
                HKMultiplayerMod.SaveSettings();
                if (index == 0) {
                    UpdateCheck.CheckAgain(UIManager.instance);
                }

                RefreshUpdate();
            });
        UpdateCheck.Changed += RefreshUpdate;
        RefreshUpdate();

        var host = NewPage(HostPage);
        AddText(host, Lang.T($"Elige un mundo coop (hasta {WorldStore.MaxWorlds}). Tus partidas normales no se tocan.", $"Pick a coop world (up to {WorldStore.MaxWorlds}). Your normal saves are not touched."), 36);
        AddSpace(host, 10);
        _worldList = new GameObject("Worlds", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _worldList.transform.SetParent(host.transform, false);
        var listLayout = _worldList.GetComponent<VerticalLayoutGroup>();
        listLayout.childControlHeight = true;
        listLayout.childControlWidth = true;
        listLayout.childForceExpandHeight = false;
        AddSpace(host, 10);
        _newWorldButton = AddButton(host, Lang.T("Nuevo mundo", "New world"), () => ShowPage(NewWorldPage));
        _limitText = AddText(host, Lang.T($"Ya tienes {WorldStore.MaxWorlds} mundos: borra uno para crear otro.", $"You already have {WorldStore.MaxWorlds} worlds: delete one to create another."), 34);
        _hostStatus = AddText(host, string.Empty, 34);
        AddSpace(host, 10);
        // Dónde están los mundos: para hacer copias de seguridad o pasarlos a otro PC
        var where = AddText(host, Lang.T($"Los mundos se guardan en este PC, en:\n{WorldStore.Root.Replace('/', '\\')}", $"Worlds are saved on this PC, in:\n{WorldStore.Root.Replace('/', '\\')}"), 30);
        where.GetComponent<LayoutElement>().preferredHeight = 30 * 3.4f;
        AddButton(host, Lang.T("Abrir carpeta de los mundos", "Open worlds folder"), OpenWorldsFolder, fontSize: 34);

        var newWorld = NewPage(NewWorldPage);
        AddText(newWorld, Lang.T("Un mundo coop empieza como una partida nueva y lo guarda tu PC.", "A coop world starts like a new game and is saved on your PC."), 38);
        AddSpace(newWorld, 20);
        _newWorldName = AddInput(newWorld, Lang.T("Nombre del mundo", "World name"), string.Empty, 40, InputField.ContentType.Standard);
        AddSpace(newWorld, 30);
        AddButton(newWorld, Lang.T("Crear y hostear", "Create and host"), () => {
            var world = WorldStore.Create(_newWorldName.text);
            if (world == null) {
                ShowPage(HostPage);
                return;
            }

            _newWorldName.text = string.Empty;
            UIManager.instance.StartCoroutine(HostRoutine(world));
        });

        var join = NewPage(JoinPage);
        AddText(join, Lang.T("Pide al host su dirección y el código de la partida.", "Ask the host for their address and the game code."), 38);
        AddSpace(join, 20);
        _address = AddInput(join, Lang.T("Dirección del host", "Host address"), settings.LastAddress, 64, InputField.ContentType.Standard);
        _code = AddInput(join, Lang.T("Código", "Code"), string.Empty, 9, InputField.ContentType.Custom);
        _code.onValidateInput = (_, _, c) => c == '-' || char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '\0';
        AddSpace(join, 30);
        AddButton(join, Lang.T("Unirse", "Join"), Join);
        _joinStatus = AddText(join, string.Empty, 34);
        AddText(join, Lang.T("Tu personaje se guarda en el mundo del host, en su PC (al volver a unirte lo recuperas).", "Your character is saved in the host's world, on their PC (you get it back when you join again)."), 28);

        var delete = NewPage(DeletePage);
        _deleteQuestion = AddText(delete, string.Empty, 44, title: true);
        AddText(delete, Lang.T("Se borra de este PC con todo su progreso y no se puede deshacer.", "It is deleted from this PC with all its progress and cannot be undone."), 36);
        AddSpace(delete, 30);
        AddButton(delete, Lang.T("Cancelar", "Cancel"), () => ShowPage(HostPage));
        AddButton(delete, Lang.T("Borrar para siempre", "Delete forever"), () => {
            if (_pendingDelete != null) {
                WorldStore.Delete(_pendingDelete.Id);
                _pendingDelete = null;
            }

            ShowPage(HostPage);
        });

        ShowPage(StartPage, instant: true);
    }

    // =====================================================================================================
    // Versiones
    // =====================================================================================================

    /// <summary>
    /// Lo que se sabe de las versiones: una nueva para descargar (todos tienen que llevar la misma para jugar juntos),
    /// las novedades de la recién instalada, o que es la más nueva.
    /// </summary>
    private void RefreshUpdate() {
        if (_updateText == null) {
            return;
        }

        var current = HKMultiplayerMod.Version;
        string text;
        string button = null;
        _updateUrl = null;
        if (UpdateCheck.NewerAvailable) {
            text = Lang.T($"Hay una versión nueva: la {UpdateCheck.Latest} (tienes la {current}). Para jugar juntos, todos necesitan la misma.",
                $"A new version is out: {UpdateCheck.Latest} (you have {current}). To play together, everyone needs the same one.");
            button = Lang.T($"Descargar la {UpdateCheck.Latest}", $"Download {UpdateCheck.Latest}");
            _updateUrl = UpdateCheck.LatestUrl;
        } else if (UpdateCheck.JustUpdated) {
            text = Lang.T($"Actualizado a la {current}.", $"Updated to {current}.");
            button = Lang.T($"Novedades de la {current}", $"What's new in {current}");
            _updateUrl = UpdateCheck.CurrentNotesUrl;
        } else if (UpdateCheck.Latest != null) {
            text = Lang.T($"Tienes la versión más nueva ({current}).", $"You have the newest version ({current}).");
        } else {
            text = Lang.T($"Versión {current}", $"Version {current}");
        }

        _updateText.text = text;
        if (button != null) {
            SetButtonText(_updateButton, button);
        }

        if (_updateButton.gameObject.activeSelf != (button != null)) {
            _updateButton.gameObject.SetActive(button != null);
        }
    }

    private void OpenUpdatePage() {
        if (!string.IsNullOrEmpty(_updateUrl)) {
            Log.Info($"Abriendo {_updateUrl}");
            Application.OpenURL(_updateUrl);
        }
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
                ? Lang.T($"{world.Name}  ·  nuevo", $"{world.Name}  ·  new")
                : $"{world.Name}  ·  {FormatTime(world.PlayTime)}  ·  {AreaName(world.Area)}";
            var target = world;
            var row = AddRow(_worldList, world.Name, 84, 10);
            AddButton(row, label, () => UIManager.instance.StartCoroutine(HostRoutine(target)), 40, 1080);
            AddButton(row, Lang.T("Borrar", "Delete"), () => {
                _pendingDelete = target;
                _deleteQuestion.text = Lang.T($"¿Borrar el mundo «{target.Name}»?", $"Delete the world \"{target.Name}\"?");
                ShowPage(DeletePage);
            }, 32, 240);
        }

        if (worlds.Count == 0) {
            AddText(_worldList, Lang.T("Aún no tienes mundos coop.", "You don't have any coop worlds yet."), 36);
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
            _hostStatus.text = Lang.T("El puerto no es válido", "The port is not valid");
            yield break;
        }

        if (!CoopSaves.CanStart) {
            _hostStatus.text = Lang.T("Vuelve al menú principal para abrir un mundo coop", "Go back to the main menu to open a coop world");
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

    /// <summary>Abre en el explorador la carpeta de los mundos coop (la crea si aún no existe).</summary>
    private static void OpenWorldsFolder() {
        try {
            System.IO.Directory.CreateDirectory(WorldStore.Root);
            Application.OpenURL("file:///" + WorldStore.Root.Replace('\\', '/'));
        } catch (Exception e) {
            Log.Warn($"No se pudo abrir la carpeta de los mundos ({WorldStore.Root}): {e.Message}");
        }
    }

    private void Join() {
        SaveFields();
        if (!CoopSaves.CanStart) {
            _joinStatus.text = Lang.T("Únete desde el menú principal", "Join from the main menu");
            return;
        }

        if (!TryPort(out var port)) {
            _joinStatus.text = Lang.T("El puerto no es válido", "The port is not valid");
            return;
        }

        MultiplayerRunner.Instance.Join(_address.text, port, _code.text, _name.text);
    }

    private void Update() {
        if (CurrentPage == JoinPage && _joinStatus != null && MultiplayerRunner.Instance != null) {
            var runner = MultiplayerRunner.Instance;
            var status = runner.Session is { IsActive: true } && runner.World.JoinStatus.Length > 0
                ? runner.World.JoinStatus
                : runner.StatusMessage;
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
