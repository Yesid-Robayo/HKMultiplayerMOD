using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Base de las pantallas del mod con el aspecto de los menús del juego: copia de la pantalla "Extras" (título,
/// adorno, "Volver" y sus transiciones) con el contenido vaciado y dividido en páginas, y piezas para rellenarlas
/// (botones copiados de los del juego, textos y campos de texto con su letra).
/// <para>Las rutinas corren en el UIManager: la pantalla se desactiva al ocultarse y cortaría las suyas.</para>
/// </summary>
internal abstract class GameStyledMenu : MonoBehaviour {
    protected MenuScreen Screen;
    protected Font TitleFont;
    protected Font BodyFont;
    private GameObject _buttonTemplate;
    private RectTransform _content;
    private readonly Dictionary<int, GameObject> _pages = new();
    private readonly Dictionary<int, Selectable> _firstSelectable = new();

    protected int CurrentPage { get; private set; }
    protected bool Busy { get; set; }

    protected static readonly System.Reflection.MethodInfo SetMenuState = typeof(UIManager).GetMethod(
        "SetMenuState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Crea la pantalla (copia de Extras) con su componente de menú.</summary>
    protected static T CreateScreen<T>(UIManager ui, string name, string title) where T : GameStyledMenu {
        var extras = ui.extrasMenuScreen;
        var screenObject = Instantiate(extras.gameObject, extras.transform.parent);
        screenObject.name = name;
        screenObject.SetActive(false);
        var menu = screenObject.AddComponent<T>();
        menu.Screen = screenObject.GetComponent<MenuScreen>();
        menu.TitleFont = ui.mainMenuButtons.startButton.GetComponentInChildren<Text>(true).font;
        menu.BodyFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name.Contains("Perpetua")) ??
                        menu.TitleFont;
        menu.Prepare(title);
        return menu;
    }

    private void Prepare(string title) {
        RemoveLocalization(gameObject);
        var cheat = transform.Find("CheatCodeListener");
        if (cheat != null) {
            Destroy(cheat.gameObject);
        }

        Screen.title.GetComponent<Text>().text = title;
        _content = (RectTransform) Screen.content.transform;
        var template = _content.GetComponentsInChildren<MenuButton>(true).First(b => b.name == "CreditsButton");
        _buttonTemplate = Instantiate(template.gameObject, transform);
        _buttonTemplate.SetActive(false);
        foreach (Transform child in _content) {
            Destroy(child.gameObject);
        }

        ConfigureVertical(_content.GetComponent<VerticalLayoutGroup>());
        GetComponent<MenuButtonList>()?.ClearSelectables();

        var back = Screen.controls.GetComponentInChildren<MenuButton>(true);
        MakeCustom(back, "Volver", OnBack);
        back.cancelAction = CancelAction.CustomCancelAction;
        back.customCancelAction = _ => OnBack();
    }

    /// <summary>"Volver" o Escape.</summary>
    protected abstract void OnBack();

    // =====================================================================================================
    // Páginas
    // =====================================================================================================

    protected GameObject NewPage(int page) {
        var pageObject = new GameObject($"Page{page}", typeof(RectTransform), typeof(VerticalLayoutGroup));
        pageObject.transform.SetParent(_content, false);
        ConfigureVertical(pageObject.GetComponent<VerticalLayoutGroup>());
        _pages[page] = pageObject;
        return pageObject;
    }

    protected GameObject PageObject(int page) => _pages[page];

    protected void ShowPage(int page, bool instant = false) {
        OnPageShowing(page);
        if (instant || !gameObject.activeInHierarchy) {
            SwitchTo(page);
            return;
        }

        UIManager.instance.StartCoroutine(SwitchRoutine(page));
    }

    /// <summary>Antes de mostrar una página (para refrescar su contenido).</summary>
    protected virtual void OnPageShowing(int page) {
    }

    private IEnumerator SwitchRoutine(int page) {
        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.FadeOutCanvasGroup(Screen.content));
        SwitchTo(page);
        yield return ui.StartCoroutine(ui.FadeInCanvasGroup(Screen.content));
        Busy = false;
        SelectFirst();
    }

    private void SwitchTo(int page) {
        CurrentPage = page;
        foreach (var pair in _pages) {
            pair.Value.SetActive(pair.Key == page);
        }

        Screen.defaultHighlight = FirstSelectable(page);
        SelectFirst();
    }

    private Selectable FirstSelectable(int page) {
        if (_firstSelectable.TryGetValue(page, out var first) && first != null && first.gameObject.activeInHierarchy) {
            return first;
        }

        return _pages.TryGetValue(page, out var pageObject)
            ? pageObject.GetComponentsInChildren<Selectable>(false).FirstOrDefault()
            : null;
    }

    protected void SelectFirst() {
        var first = FirstSelectable(CurrentPage);
        if (gameObject.activeInHierarchy && first != null && EventSystem.current != null) {
            EventSystem.current.SetSelectedGameObject(first.gameObject);
        }
    }

    /// <summary>Elemento que se resalta al mostrar la página (si no, el primero que haya).</summary>
    protected void SetFirst(int page, Selectable selectable) => _firstSelectable[page] = selectable;

    // =====================================================================================================
    // Piezas
    // =====================================================================================================

    protected MenuButton AddButton(GameObject parent, string text, Action action, int fontSize = 0, float width = 0) {
        var buttonObject = Instantiate(_buttonTemplate, parent.transform, false);
        buttonObject.name = text;
        buttonObject.SetActive(true);
        var button = buttonObject.GetComponent<MenuButton>();
        MakeCustom(button, text, action);
        button.cancelAction = CancelAction.CustomCancelAction;
        button.customCancelAction = _ => OnBack();
        var nav = button.navigation;
        nav.mode = Navigation.Mode.Automatic;
        button.navigation = nav;
        if (fontSize > 0) {
            buttonObject.GetComponentInChildren<Text>(true).fontSize = fontSize;
        }

        var element = buttonObject.AddComponent<LayoutElement>();
        element.preferredHeight = 84;
        if (width > 0) {
            element.preferredWidth = width;
        }

        return button;
    }

    /// <summary>Cambia el texto de un botón ya creado.</summary>
    protected static void SetButtonText(MenuButton button, string text) {
        var label = button.GetComponentInChildren<Text>(true);
        if (label != null) {
            label.text = text;
        }
    }

    protected Text AddText(GameObject parent, string text, int size, bool title = false) {
        var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        textObject.transform.SetParent(parent.transform, false);
        var label = textObject.GetComponent<Text>();
        label.font = title ? TitleFont : BodyFont;
        label.fontSize = size;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = new Color(1f, 1f, 1f, 0.9f);
        label.text = text;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        textObject.GetComponent<LayoutElement>().preferredHeight = size * 1.6f;
        return label;
    }

    protected static void AddSpace(GameObject parent, float height) {
        var space = new GameObject("Space", typeof(RectTransform), typeof(LayoutElement));
        space.transform.SetParent(parent.transform, false);
        space.GetComponent<LayoutElement>().preferredHeight = height;
    }

    /// <summary>Fila horizontal centrada.</summary>
    protected static GameObject AddRow(GameObject parent, string name, float height, float spacing = 30) {
        var row = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        row.transform.SetParent(parent.transform, false);
        row.GetComponent<LayoutElement>().preferredHeight = height;
        var layout = row.GetComponent<HorizontalLayoutGroup>();
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = spacing;
        return row;
    }

    /// <summary>Fila "etiqueta  [campo]" con un campo con el estilo del menú (letra del juego y subrayado).</summary>
    protected InputField AddInput(GameObject parent, string label, string value, int maxLength,
        InputField.ContentType contentType, float labelWidth = 480, float fieldWidth = 560) {
        var row = AddRow(parent, label, 80);
        var labelText = AddText(row, label, 38, title: true);
        labelText.alignment = TextAnchor.MiddleRight;
        var labelElement = labelText.GetComponent<LayoutElement>();
        labelElement.preferredWidth = labelWidth;
        labelElement.preferredHeight = 70;

        var fieldObject = new GameObject("Field", typeof(RectTransform), typeof(Image), typeof(InputField),
            typeof(LayoutElement));
        fieldObject.transform.SetParent(row.transform, false);
        var fieldElement = fieldObject.GetComponent<LayoutElement>();
        fieldElement.preferredWidth = fieldWidth;
        fieldElement.preferredHeight = 62;
        var background = fieldObject.GetComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.35f);

        var underline = new GameObject("Underline", typeof(RectTransform), typeof(Image));
        underline.transform.SetParent(fieldObject.transform, false);
        var underlineRect = (RectTransform) underline.transform;
        underlineRect.anchorMin = Vector2.zero;
        underlineRect.anchorMax = new Vector2(1f, 0f);
        underlineRect.sizeDelta = new Vector2(0f, 2f);
        var underlineImage = underline.GetComponent<Image>();
        underlineImage.color = new Color(1f, 1f, 1f, 0.6f);
        underlineImage.raycastTarget = false;

        Text MakeText(string name, Color color) {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(fieldObject.transform, false);
            var rect = (RectTransform) textObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(18f, 4f);
            rect.offsetMax = new Vector2(-18f, -4f);
            var text = textObject.GetComponent<Text>();
            text.font = BodyFont;
            text.fontSize = 40;
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
        input.targetGraphic = background;
        var colors = input.colors;
        colors.highlightedColor = new Color(1.3f, 1.3f, 1.3f);
        colors.selectedColor = new Color(1.5f, 1.5f, 1.5f);
        input.colors = colors;
        return input;
    }

    // =====================================================================================================
    // Utilidades
    // =====================================================================================================

    /// <summary>Convierte un botón copiado del juego en uno propio: texto fijo y acción al pulsarlo.</summary>
    protected static void MakeCustom(MenuButton button, string text, Action action) {
        RemoveLocalization(button.gameObject);
        foreach (var trigger in button.GetComponents<EventTrigger>()) {
            DestroyImmediate(trigger);
        }

        button.buttonType = MenuButton.MenuButtonType.CustomSubmit;
        button.proceed = false;
        button.submitAction = _ => action();
        SetButtonText(button, text);
    }

    protected static void RemoveLocalization(GameObject root) {
        foreach (var localize in root.GetComponentsInChildren<AutoLocalizeTextUI>(true)) {
            DestroyImmediate(localize);
        }
    }

    private static void ConfigureVertical(VerticalLayoutGroup layout) {
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.spacing = 0f;
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    /// <summary>Pulsa el botón visible cuyo texto empieza así.</summary>
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
