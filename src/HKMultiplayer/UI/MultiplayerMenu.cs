using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using HKMultiplayer.Game;
using HKMultiplayer.Game.World;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Menú "Multijugador" dentro del menú principal del juego. El botón es una copia del de "Iniciar partida" y la
/// pantalla una copia de la de "Extras" (título, adorno, botones con sus cursores, "Volver"), así que se ve y se
/// mueve como el resto de menús. Páginas: inicio (nombre), hostear (mundos coop), nuevo mundo y unirse.
/// </summary>
internal sealed class MultiplayerMenu : MonoBehaviour {
    public static MultiplayerMenu Instance { get; private set; }

    private enum Page {
        Start,
        Host,
        NewWorld,
        Join
    }

    private MenuScreen _screen;
    private GameObject _buttonTemplate;
    private Font _titleFont;
    private Font _bodyFont;
    private RectTransform _content;
    private readonly Dictionary<Page, GameObject> _pages = new();
    private readonly Dictionary<Page, Selectable> _firstSelectable = new();
    private Page _page;
    private bool _busy;

    // Campos
    private InputField _name;
    private InputField _hostPort;
    private InputField _newWorldName;
    private InputField _address;
    private InputField _joinPort;
    private InputField _code;
    private Text _joinStatus;
    private Text _hostStatus;
    private RectTransform _worldList;

    private static MenuButton _mainMenuButton;

    /// <summary>
    /// Añade el botón y la pantalla si el menú principal existe y aún no los tiene (el menú se vuelve a crear al
    /// volver de una partida). Barato: llamar cada frame.
    /// </summary>
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
            // Para no reintentar cada frame
            _mainMenuButton = ui.mainMenuButtons.startButton;
        }
    }

    // =====================================================================================================
    // Construcción
    // =====================================================================================================

    private static void Build(UIManager ui) {
        var startButton = ui.mainMenuButtons.startButton;
        var titleFont = startButton.GetComponentInChildren<Text>(true).font;
        var bodyFont = ui.mainMenuScreen.GetComponentsInChildren<Text>(true)
            .Select(t => t.font).FirstOrDefault(f => f != null && f.name.Contains("Perpetua")) ?? titleFont;

        // ---- Botón del menú principal, justo debajo de "Iniciar partida"
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

        // ---- Pantalla: copia de Extras
        var extras = ui.extrasMenuScreen;
        var screenObject = Instantiate(extras.gameObject, extras.transform.parent);
        screenObject.name = "MultiplayerMenuScreen";
        screenObject.SetActive(false);
        var menu = screenObject.AddComponent<MultiplayerMenu>();
        menu._screen = screenObject.GetComponent<MenuScreen>();
        menu._titleFont = titleFont;
        menu._bodyFont = bodyFont;
        menu.Setup();
        _mainMenuButton = button;
        Log.Info("Menú Multijugador añadido al menú principal");
    }

    private void Setup() {
        Instance = this;
        RemoveLocalization(gameObject);
        foreach (var name in new[] { "CheatCodeListener" }) {
            var child = transform.Find(name);
            if (child != null) {
                Destroy(child.gameObject);
            }
        }

        _screen.title.GetComponent<Text>().text = "Multijugador";

        // Plantilla de botón (el de créditos) y contenido vacío
        _content = (RectTransform) _screen.content.transform;
        var template = _content.GetComponentsInChildren<MenuButton>(true).First(b => b.name == "CreditsButton");
        _buttonTemplate = Instantiate(template.gameObject, transform);
        _buttonTemplate.SetActive(false);
        foreach (Transform child in _content) {
            Destroy(child.gameObject);
        }

        var layout = _content.GetComponent<VerticalLayoutGroup>();
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.spacing = 0f;

        var list = GetComponent<MenuButtonList>();
        list?.ClearSelectables();

        // Volver
        var back = _screen.controls.GetComponentInChildren<MenuButton>(true);
        MakeCustom(back, "Volver", Back);
        back.cancelAction = CancelAction.CustomCancelAction;
        back.customCancelAction = _ => Back();

        var settings = HKMultiplayerMod.Settings;
        BuildStartPage(settings.PlayerName);
        BuildHostPage(settings.Port);
        BuildNewWorldPage();
        BuildJoinPage(settings.LastAddress, settings.Port);
        ShowPage(Page.Start, instant: true);
    }

    private void BuildStartPage(string playerName) {
        var page = NewPage(Page.Start);
        AddText(page, "Juega con amigos en tu red o por internet.", 42, _bodyFont);
        AddSpace(page, 20);
        _name = AddInput(page, "Tu nombre", playerName, 24, InputField.ContentType.Standard);
        AddSpace(page, 30);
        AddButton(page, "Hostear un mundo", () => ShowPage(Page.Host));
        AddButton(page, "Unirse a un mundo", () => ShowPage(Page.Join));
    }

    private void BuildHostPage(int port) {
        var page = NewPage(Page.Host);
        AddText(page, "Elige un mundo coop. Tus partidas normales no se tocan.", 38, _bodyFont);
        AddSpace(page, 10);
        var listObject = new GameObject("Worlds", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _worldList = (RectTransform) listObject.transform;
        _worldList.SetParent(page.transform, false);
        var listLayout = listObject.GetComponent<VerticalLayoutGroup>();
        listLayout.childControlHeight = true;
        listLayout.childControlWidth = true;
        listLayout.childForceExpandHeight = false;
        AddButton(page, "Nuevo mundo", () => ShowPage(Page.NewWorld));
        AddSpace(page, 10);
        _hostPort = AddInput(page, "Puerto", port.ToString(), 5, InputField.ContentType.IntegerNumber);
        _hostStatus = AddText(page, string.Empty, 34, _bodyFont);
    }

    private void BuildNewWorldPage() {
        var page = NewPage(Page.NewWorld);
        AddText(page, "Un mundo coop empieza como una partida nueva y lo guarda tu PC.", 38, _bodyFont);
        AddSpace(page, 20);
        _newWorldName = AddInput(page, "Nombre del mundo", string.Empty, 40, InputField.ContentType.Standard);
        AddSpace(page, 30);
        AddButton(page, "Crear y hostear", () => {
            var world = WorldStore.Create(_newWorldName.text);
            UIManager.instance.StartCoroutine(HostRoutine(world));
        });
    }

    private void BuildJoinPage(string address, int port) {
        var page = NewPage(Page.Join);
        AddText(page, "Pide al host su dirección y el código de la partida.", 38, _bodyFont);
        AddSpace(page, 20);
        _address = AddInput(page, "Dirección del host", address, 64, InputField.ContentType.Standard);
        _code = AddInput(page, "Código", string.Empty, 9, InputField.ContentType.Alphanumeric);
        _code.onValidateInput = (_, _, c) => c == '-' || char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '\0';
        _code.contentType = InputField.ContentType.Custom;
        _joinPort = AddInput(page, "Puerto", port.ToString(), 5, InputField.ContentType.IntegerNumber);
        AddSpace(page, 30);
        AddButton(page, "Unirse", Join);
        _joinStatus = AddText(page, string.Empty, 34, _bodyFont);
    }

    // =====================================================================================================
    // Navegación (las rutinas corren en el UIManager: esta pantalla se desactiva al ocultarse y cortaría las suyas)
    // =====================================================================================================

    private static readonly System.Reflection.MethodInfo SetMenuState = typeof(UIManager).GetMethod("SetMenuState",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private void Open() {
        if (_busy) {
            return;
        }

        RefreshWorlds();
        ShowPage(Page.Start, instant: true);
        UIManager.instance.StartCoroutine(OpenRoutine());
    }

    /// <summary>Igual que el juego al ir a "Extras": se apaga el título y el menú principal y entra esta pantalla.</summary>
    private IEnumerator OpenRoutine() {
        _busy = true;
        var ui = UIManager.instance;
        var input = GameManager.instance.inputHandler;
        input.StopUIInput();
        ui.StartCoroutine(FadeSprite(ui.gameTitle, 0f));
        ui.subtitleFSM.SendEvent("FADE OUT");
        yield return ui.StartCoroutine(ui.FadeOutCanvasGroup(ui.mainMenuScreen));
        ui.currentDynamicMenu = _screen;
        SetMenuState?.Invoke(ui, new object[] { MainMenuState.DYNAMIC_MENU });
        yield return ui.StartCoroutine(ui.ShowMenu(_screen));
        input.StartUIInput();
        _busy = false;
        Select(_page);
    }

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

    private void Back() {
        if (_busy) {
            return;
        }

        switch (_page) {
            case Page.Start:
                SaveFields();
                UIManager.instance.StartCoroutine(LeaveRoutine());
                break;
            case Page.NewWorld:
                ShowPage(Page.Host);
                break;
            default:
                ShowPage(Page.Start);
                break;
        }
    }

    private IEnumerator LeaveRoutine() {
        _busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(_screen));
        ui.UIGoToMainMenu();
        _busy = false;
    }

    private void ShowPage(Page page, bool instant = false) {
        if (page == Page.Host) {
            RefreshWorlds();
        }

        if (instant || !gameObject.activeInHierarchy) {
            SwitchTo(page);
            return;
        }

        UIManager.instance.StartCoroutine(SwitchRoutine(page));
    }

    private IEnumerator SwitchRoutine(Page page) {
        _busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.FadeOutCanvasGroup(_screen.content));
        SwitchTo(page);
        yield return ui.StartCoroutine(ui.FadeInCanvasGroup(_screen.content));
        _busy = false;
        Select(page);
    }

    private void SwitchTo(Page page) {
        _page = page;
        foreach (var pair in _pages) {
            pair.Value.SetActive(pair.Key == page);
        }

        if (_firstSelectable.TryGetValue(page, out var first)) {
            _screen.defaultHighlight = first;
        }

        Select(page);
    }

    private void Select(Page page) {
        if (gameObject.activeInHierarchy && _firstSelectable.TryGetValue(page, out var first) && first != null &&
            EventSystem.current != null) {
            EventSystem.current.SetSelectedGameObject(first.gameObject);
        }
    }

    private void RefreshWorlds() {
        foreach (Transform child in _worldList) {
            Destroy(child.gameObject);
        }

        var worlds = WorldStore.List().Take(5).ToList();
        foreach (var world in worlds) {
            var label = world.IsNew
                ? $"{world.Name}  ·  nuevo"
                : $"{world.Name}  ·  {FormatTime(world.PlayTime)}  ·  {AreaName(world.Area)}";
            var target = world;
            AddButton(_worldList.gameObject, label, () => UIManager.instance.StartCoroutine(HostRoutine(target)), 40);
        }

        if (worlds.Count == 0) {
            AddText(_worldList.gameObject, "Aún no tienes mundos coop.", 36, _bodyFont);
        }

        // Primer elemento seleccionable de la página: el primer mundo o "Nuevo mundo"
        _firstSelectable[Page.Host] = _pages[Page.Host].GetComponentsInChildren<MenuButton>(true)
            .FirstOrDefault(b => b.transform.parent == _worldList) ?? FindButton(Page.Host, "Nuevo mundo");
        if (_page == Page.Host) {
            _screen.defaultHighlight = _firstSelectable[Page.Host];
        }
    }

    // =====================================================================================================
    // Acciones
    // =====================================================================================================

    private IEnumerator HostRoutine(CoopWorldInfo world) {
        if (_busy) {
            yield break;
        }

        SaveFields();
        if (!int.TryParse(_hostPort.text, out var port) || port <= 0 || port > 65535) {
            _hostStatus.text = "El puerto no es válido";
            yield break;
        }

        var runner = MultiplayerRunner.Instance;
        if (!CoopSaves.CanStart) {
            _hostStatus.text = "Vuelve al menú principal para abrir un mundo coop";
            yield break;
        }

        _busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(_screen));
        _busy = false;
        if (!runner.HostWorld(world, _name.text, port)) {
            // No se pudo: se vuelve a mostrar el menú con el motivo
            _hostStatus.text = runner.StatusMessage;
            yield return ui.StartCoroutine(ui.ShowMenu(_screen));
        }
    }

    private void Join() {
        SaveFields();
        if (!int.TryParse(_joinPort.text, out var port) || port <= 0 || port > 65535) {
            _joinStatus.text = "El puerto no es válido";
            return;
        }

        MultiplayerRunner.Instance.Join(_address.text, port, _code.text, _name.text);
    }

    private void Update() {
        if (_page == Page.Join && _joinStatus != null && MultiplayerRunner.Instance != null) {
            var status = MultiplayerRunner.Instance.StatusMessage;
            if (_joinStatus.text != status) {
                _joinStatus.text = status;
            }
        }
    }

    private void SaveFields() {
        var settings = HKMultiplayerMod.Settings;
        settings.PlayerName = string.IsNullOrWhiteSpace(_name.text) ? settings.PlayerName : _name.text.Trim();
        settings.LastAddress = _address.text.Trim();
        if (int.TryParse(_page == Page.Join ? _joinPort.text : _hostPort.text, out var port) && port is > 0 and < 65536) {
            settings.Port = port;
        }

        HKMultiplayerMod.SaveSettings();
    }

    // =====================================================================================================
    // Elementos
    // =====================================================================================================

    private GameObject NewPage(Page page) {
        var pageObject = new GameObject($"Page{page}", typeof(RectTransform), typeof(VerticalLayoutGroup));
        pageObject.transform.SetParent(_content, false);
        var layout = pageObject.GetComponent<VerticalLayoutGroup>();
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.childAlignment = TextAnchor.UpperCenter;
        _pages[page] = pageObject;
        return pageObject;
    }

    private MenuButton AddButton(GameObject parent, string text, Action action, int fontSize = 0) {
        var buttonObject = Instantiate(_buttonTemplate, parent.transform, false);
        buttonObject.name = text;
        buttonObject.SetActive(true);
        var button = buttonObject.GetComponent<MenuButton>();
        MakeCustom(button, text, action);
        button.cancelAction = CancelAction.CustomCancelAction;
        button.customCancelAction = _ => Back();
        var nav = button.navigation;
        nav.mode = Navigation.Mode.Vertical;
        button.navigation = nav;
        if (fontSize > 0) {
            buttonObject.GetComponentInChildren<Text>(true).fontSize = fontSize;
        }

        var element = buttonObject.AddComponent<LayoutElement>();
        element.preferredHeight = 88;
        RegisterFirst(parent, button);
        return button;
    }

    private Text AddText(GameObject parent, string text, int size, Font font) {
        var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        textObject.transform.SetParent(parent.transform, false);
        var label = textObject.GetComponent<Text>();
        label.font = font;
        label.fontSize = size;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = new Color(1f, 1f, 1f, 0.9f);
        label.text = text;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        textObject.GetComponent<LayoutElement>().preferredHeight = size * 1.6f;
        return label;
    }

    private static void AddSpace(GameObject parent, float height) {
        var space = new GameObject("Space", typeof(RectTransform), typeof(LayoutElement));
        space.transform.SetParent(parent.transform, false);
        space.GetComponent<LayoutElement>().preferredHeight = height;
    }

    /// <summary>Fila "etiqueta: [campo]" con un campo de texto con el estilo del menú (texto claro y subrayado).</summary>
    private InputField AddInput(GameObject parent, string label, string value, int maxLength,
        InputField.ContentType contentType) {
        var row = new GameObject(label, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent.transform, false);
        row.GetComponent<LayoutElement>().preferredHeight = 80;
        var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        rowLayout.childControlHeight = true;
        rowLayout.childControlWidth = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;
        rowLayout.childAlignment = TextAnchor.MiddleCenter;
        rowLayout.spacing = 30;

        var labelText = AddText(row, label, 40, _titleFont);
        labelText.alignment = TextAnchor.MiddleRight;
        var labelElement = labelText.GetComponent<LayoutElement>();
        labelElement.preferredWidth = 520;
        labelElement.preferredHeight = 70;

        var fieldObject = new GameObject("Field", typeof(RectTransform), typeof(Image), typeof(InputField),
            typeof(LayoutElement));
        fieldObject.transform.SetParent(row.transform, false);
        var fieldElement = fieldObject.GetComponent<LayoutElement>();
        fieldElement.preferredWidth = 620;
        fieldElement.preferredHeight = 64;
        var background = fieldObject.GetComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.35f);

        var underline = new GameObject("Underline", typeof(RectTransform), typeof(Image));
        underline.transform.SetParent(fieldObject.transform, false);
        var underlineRect = (RectTransform) underline.transform;
        underlineRect.anchorMin = new Vector2(0f, 0f);
        underlineRect.anchorMax = new Vector2(1f, 0f);
        underlineRect.sizeDelta = new Vector2(0f, 2f);
        underline.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.6f);

        Text MakeText(string name, Color color) {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(fieldObject.transform, false);
            var rect = (RectTransform) textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(18f, 4f);
            rect.offsetMax = new Vector2(-18f, -4f);
            var text = textObject.GetComponent<Text>();
            text.font = _bodyFont;
            text.fontSize = 42;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = color;
            text.supportRichText = false;
            return text;
        }

        var input = fieldObject.GetComponent<InputField>();
        input.textComponent = MakeText("Text", Color.white);
        input.placeholder = MakeText("Placeholder", new Color(1f, 1f, 1f, 0.3f));
        ((Text) input.placeholder).text = "…";
        input.characterLimit = maxLength;
        input.contentType = contentType;
        input.caretColor = Color.white;
        input.selectionColor = new Color(1f, 1f, 1f, 0.25f);
        input.text = value ?? string.Empty;
        var colors = input.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.3f, 1.3f, 1.3f);
        colors.selectedColor = new Color(1.5f, 1.5f, 1.5f);
        input.colors = colors;
        input.targetGraphic = background;
        var nav = input.navigation;
        nav.mode = Navigation.Mode.Vertical;
        input.navigation = nav;
        RegisterFirst(parent, input);
        return input;
    }

    /// <summary>El primer elemento seleccionable de cada página se resalta al mostrarla.</summary>
    private void RegisterFirst(GameObject parent, Selectable selectable) {
        foreach (var pair in _pages) {
            if (pair.Value == parent && !_firstSelectable.ContainsKey(pair.Key)) {
                _firstSelectable[pair.Key] = selectable;
            }
        }
    }

    private MenuButton FindButton(Page page, string text) =>
        _pages[page].GetComponentsInChildren<MenuButton>(true).FirstOrDefault(b => b.name == text);

    /// <summary>Convierte un botón copiado del juego en uno propio: texto fijo y acción al pulsarlo.</summary>
    private static void MakeCustom(MenuButton button, string text, Action action) {
        RemoveLocalization(button.gameObject);
        foreach (var trigger in button.GetComponents<EventTrigger>()) {
            DestroyImmediate(trigger);
        }

        button.buttonType = MenuButton.MenuButtonType.CustomSubmit;
        button.proceed = false;
        button.submitAction = _ => action();
        var label = button.GetComponentInChildren<Text>(true);
        if (label != null) {
            label.text = text;
        }
    }

    private static void RemoveLocalization(GameObject root) {
        foreach (var localize in root.GetComponentsInChildren<AutoLocalizeTextUI>(true)) {
            DestroyImmediate(localize);
        }
    }

    private static string FormatTime(float seconds) {
        var minutes = (int) (seconds / 60);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    private static string AreaName(string mapZone) {
        if (string.IsNullOrEmpty(mapZone)) {
            return "-";
        }

        // Nombre de la zona en el idioma del juego si existe, si no el interno
        try {
            var key = mapZone;
            var localized = Language.Language.Get(key, "Map Zones");
            return string.IsNullOrEmpty(localized) || localized.StartsWith("#!#") ? key : localized;
        } catch (Exception) {
            return mapZone;
        }
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    /// <summary>Abre el menú (como el botón del menú principal).</summary>
    public void TestOpen() => Open();

    /// <summary>Pulsa el botón visible con ese texto.</summary>
    public bool TestClick(string text) {
        var button = GetComponentsInChildren<MenuButton>(false).FirstOrDefault(b =>
            b.GetComponentInChildren<Text>(true)?.text.StartsWith(text, StringComparison.OrdinalIgnoreCase) == true);
        if (button == null) {
            return false;
        }

        button.OnSubmit(new BaseEventData(EventSystem.current));
        return true;
    }

    /// <summary>Escribe en el campo visible con esa etiqueta.</summary>
    public bool TestType(string label, string value) {
        var row = GetComponentsInChildren<HorizontalLayoutGroup>(false).FirstOrDefault(r => r.name == label);
        var input = row?.GetComponentInChildren<InputField>();
        if (input == null) {
            return false;
        }

        input.text = value;
        return true;
    }
}
