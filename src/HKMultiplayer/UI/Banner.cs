using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Cartel grande arriba en el centro de la pantalla, con la letra del juego. Lo usa la demostración del modo de prueba
/// para explicar cada paso. Se crea una vez y se reutiliza.
/// </summary>
internal sealed class Banner : MonoBehaviour {
    private static Banner _instance;
    private Text _text;
    private CanvasGroup _group;
    private Coroutine _routine;

    public static void Show(string message, float seconds) {
        if (_instance == null) {
            Create();
        }

        if (_instance._routine != null) {
            _instance.StopCoroutine(_instance._routine);
        }

        _instance._text.text = message;
        _instance._routine = _instance.StartCoroutine(_instance.Run(seconds));
    }

    private static void Create() {
        var root = new GameObject("HKMultiplayer Cartel", typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        DontDestroyOnLoad(root);
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 510;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var background = new GameObject("Fondo", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(root.transform, false);
        var backgroundRect = (RectTransform) background.transform;
        backgroundRect.anchorMin = new Vector2(0f, 1f);
        backgroundRect.anchorMax = new Vector2(1f, 1f);
        backgroundRect.pivot = new Vector2(0.5f, 1f);
        backgroundRect.sizeDelta = new Vector2(0f, 230f);
        var image = background.GetComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0.6f);
        image.raycastTarget = false;

        var textObject = new GameObject("Texto", typeof(RectTransform), typeof(Text), typeof(Shadow));
        textObject.transform.SetParent(background.transform, false);
        var rect = (RectTransform) textObject.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(60f, 20f);
        rect.offsetMax = new Vector2(-60f, -20f);
        var text = textObject.GetComponent<Text>();
        text.font = FindGameFont() ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 52;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        textObject.GetComponent<Shadow>().effectDistance = new Vector2(2f, -2f);

        _instance = root.AddComponent<Banner>();
        _instance._text = text;
        _instance._group = root.GetComponent<CanvasGroup>();
        _instance._group.alpha = 0f;
        _instance._group.blocksRaycasts = false;
    }

    private static Font FindGameFont() {
        foreach (var font in Resources.FindObjectsOfTypeAll<Font>()) {
            if (font.name.Contains("Perpetua")) {
                return font;
            }
        }

        return null;
    }

    private IEnumerator Run(float seconds) {
        for (var a = _group.alpha; a < 1f; a += Time.unscaledDeltaTime * 4f) {
            _group.alpha = a;
            yield return null;
        }

        _group.alpha = 1f;
        yield return new WaitForSecondsRealtime(seconds);
        for (var a = 1f; a > 0f; a -= Time.unscaledDeltaTime * 2f) {
            _group.alpha = a;
            yield return null;
        }

        _group.alpha = 0f;
        _routine = null;
    }
}
