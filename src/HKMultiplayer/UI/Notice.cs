using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Aviso en pantalla durante la partida (abajo a la derecha, con la letra del juego): código de la partida, quién se
/// unió o se fue... Aparece y se desvanece solo. El lienzo se crea una vez y se reutiliza.
/// </summary>
internal sealed class Notice : MonoBehaviour {
    private static Notice _instance;
    private Text _text;
    private CanvasGroup _group;
    private Coroutine _routine;

    public static void Show(string message, float seconds = 6f) {
        if (_instance == null) {
            Create();
        }

        if (_instance._routine != null) {
            _instance.StopCoroutine(_instance._routine);
        }

        _instance._text.text = message;
        _instance._routine = _instance.StartCoroutine(_instance.Run(seconds));
    }

    /// <summary>Quita ya el aviso si es el que empieza por ese texto (p. ej. una espera que ya terminó).</summary>
    public static void Hide(string prefix) {
        if (_instance == null || !_instance._text.text.StartsWith(prefix, System.StringComparison.Ordinal)) {
            return;
        }

        if (_instance._routine != null) {
            _instance.StopCoroutine(_instance._routine);
            _instance._routine = null;
        }

        _instance._group.alpha = 0f;
    }

    private static void Create() {
        var root = new GameObject("HKMultiplayer Aviso", typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        DontDestroyOnLoad(root);
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var textObject = new GameObject("Texto", typeof(RectTransform), typeof(Text), typeof(Shadow));
        textObject.transform.SetParent(root.transform, false);
        var rect = (RectTransform) textObject.transform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-48f, 40f);
        rect.sizeDelta = new Vector2(1200f, 200f);
        var text = textObject.GetComponent<Text>();
        text.font = FindGameFont() ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 36;
        text.alignment = TextAnchor.LowerRight;
        text.color = Color.white;
        text.raycastTarget = false;
        textObject.GetComponent<Shadow>().effectDistance = new Vector2(2f, -2f);

        _instance = root.AddComponent<Notice>();
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
        for (var a = _group.alpha; a < 1f; a += Time.unscaledDeltaTime * 3f) {
            _group.alpha = a;
            yield return null;
        }

        _group.alpha = 1f;
        yield return new WaitForSecondsRealtime(seconds);
        for (var a = 1f; a > 0f; a -= Time.unscaledDeltaTime * 1.5f) {
            _group.alpha = a;
            yield return null;
        }

        _group.alpha = 0f;
        _routine = null;
    }
}
