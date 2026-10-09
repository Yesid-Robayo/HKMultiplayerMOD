using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Lienzo de pantalla (uGUI) de las funciones del coop durante la partida: HUD del compañero, flechas, barra del jefe y
/// textos de revivir. Se crea una vez y se reutiliza; cada pieza se crea al principio y luego solo se mueve, se muestra
/// o se oculta (sin memoria nueva por frame). Coordenadas de referencia 1920×1080, origen abajo a la izquierda.
/// <para>Como el HUD del juego, ocupa la zona de la cámara del juego (16:9; con otra proporción el juego deja franjas
/// negras) y escala con su alto: <see cref="Sync"/> lo ajusta al cambiar la ventana.</para>
/// </summary>
internal static class HudCanvas {
    private static Canvas _canvas;
    private static CanvasScaler _scaler;
    private static RectTransform _root;

    /// <summary>Zona de la cámara del juego en píxeles de pantalla (la última aplicada).</summary>
    private static Rect _viewport;
    private static Font _bodyFont;
    private static Font _titleFont;
    private static Sprite _circle;
    private static Sprite _arrow;

    /// <summary>Raíz de las piezas (se crea al pedirla).</summary>
    public static RectTransform Root {
        get {
            if (_root == null) {
                Create();
            }

            return _root;
        }
    }

    /// <summary>Escala del lienzo: píxeles de pantalla por unidad de referencia.</summary>
    public static float ScaleFactor => _scaler != null ? _scaler.scaleFactor : 1f;

    /// <summary>Letra del texto del juego (Perpetua).</summary>
    public static Font BodyFont {
        get {
            if (_bodyFont == null) {
                _bodyFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name.Contains("Perpetua")) ??
                            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            return _bodyFont;
        }
    }

    /// <summary>Letra de los títulos del juego (Trajan).</summary>
    public static Font TitleFont {
        get {
            if (_titleFont == null) {
                _titleFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name.Contains("TrajanPro-Bold")) ??
                             BodyFont;
            }

            return _titleFont;
        }
    }

    private static void Create() {
        var root = new GameObject("HKMultiplayer HUD", typeof(Canvas), typeof(CanvasScaler));
        Object.DontDestroyOnLoad(root);
        _canvas = root.GetComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Debajo de los avisos (500) y del cartel (510)
        _canvas.sortingOrder = 400;
        _scaler = root.GetComponent<CanvasScaler>();
        _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        var viewport = new GameObject("Zona de juego", typeof(RectTransform));
        _root = (RectTransform) viewport.transform;
        _root.SetParent(root.transform, false);
        _root.anchorMin = _root.anchorMax = Vector2.zero;
        _root.pivot = Vector2.zero;
        _viewport = default;
        Sync();
    }

    /// <summary>
    /// Ajusta el lienzo a la zona de la cámara del juego (barato: solo cambia algo si cambió la ventana). Llamar cada frame.
    /// </summary>
    public static void Sync() {
        if (_root == null) {
            return;
        }

        var camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
        var rect = camera != null ? camera.pixelRect : new Rect(0f, 0f, UnityEngine.Screen.width, UnityEngine.Screen.height);
        if (rect == _viewport || rect.height < 1f) {
            return;
        }

        _viewport = rect;
        var scale = rect.height / 1080f;
        _scaler.scaleFactor = scale;
        _root.anchoredPosition = rect.position / scale;
        _root.sizeDelta = rect.size / scale;
    }

    /// <summary>Contenedor vacío anclado abajo a la izquierda (para moverlo por posición).</summary>
    public static RectTransform NewGroup(string name, Transform parent = null) {
        var group = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform) group.transform;
        rect.SetParent(parent != null ? parent : Root, false);
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = Vector2.zero;
        return rect;
    }

    /// <summary>Texto con sombra.</summary>
    public static Text NewText(string name, Transform parent, int size, TextAnchor alignment, bool title = false) {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
        var rect = (RectTransform) textObject.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.sizeDelta = new Vector2(900f, size * 1.5f);
        var text = textObject.GetComponent<Text>();
        text.font = title ? TitleFont : BodyFont;
        text.fontSize = size;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        textObject.GetComponent<Shadow>().effectDistance = new Vector2(2f, -2f);
        return text;
    }

    /// <summary>Imagen (sin sprite: un rectángulo del color dado).</summary>
    public static Image NewImage(string name, Transform parent, Sprite sprite, Vector2 size, Color color) {
        var imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform) imageObject.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.sizeDelta = size;
        var image = imageObject.GetComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        image.preserveAspect = sprite != null;
        return image;
    }

    /// <summary>Barra horizontal: fondo y relleno (el relleno se ajusta con <see cref="SetFill"/>).</summary>
    public static Image NewBar(string name, Transform parent, Vector2 size, Color fill, out RectTransform root) {
        var background = NewImage(name, parent, null, size, new Color(0f, 0f, 0f, 0.65f));
        root = background.rectTransform;
        var inner = NewImage("Fondo", root, null, Vector2.zero, new Color(0.25f, 0.25f, 0.25f, 0.9f));
        Stretch(inner.rectTransform, 3f);
        var bar = NewImage("Relleno", root, null, Vector2.zero, fill);
        Stretch(bar.rectTransform, 3f);
        return bar;
    }

    /// <summary>Cuánto se llena una barra (0 a 1).</summary>
    public static void SetFill(Image bar, float fraction) {
        var rect = bar.rectTransform;
        var max = rect.anchorMax;
        max.x = Mathf.Clamp01(fraction);
        rect.anchorMax = max;
    }

    private static void Stretch(RectTransform rect, float margin) {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(margin, margin);
        rect.offsetMax = new Vector2(-margin, -margin);
    }

    /// <summary>Posición en el lienzo de un punto del mundo (false si queda detrás de la cámara).</summary>
    public static bool WorldToCanvas(Camera camera, Vector3 world, out Vector2 position) {
        var screen = camera.WorldToScreenPoint(world);
        var scale = ScaleFactor;
        position = new Vector2((screen.x - _viewport.x) / scale, (screen.y - _viewport.y) / scale);
        return screen.z > 0f;
    }

    /// <summary>Tamaño de la zona de juego en unidades de referencia.</summary>
    public static Vector2 Size => Root.sizeDelta;

    /// <summary>Círculo blanco (orbe de alma), creado una vez.</summary>
    public static Sprite Circle {
        get {
            if (_circle == null) {
                const int size = 64;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                for (var y = 0; y < size; y++) {
                    for (var x = 0; x < size; x++) {
                        var dx = x + 0.5f - size / 2f;
                        var dy = y + 0.5f - size / 2f;
                        var alpha = Mathf.Clamp01(size / 2f - Mathf.Sqrt(dx * dx + dy * dy));
                        texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                    }
                }

                texture.Apply();
                _circle = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            }

            return _circle;
        }
    }

    /// <summary>Triángulo blanco que apunta a la derecha (flechas del borde), creado una vez.</summary>
    public static Sprite Arrow {
        get {
            if (_arrow == null) {
                const int size = 32;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                for (var x = 0; x < size; x++) {
                    for (var y = 0; y < size; y++) {
                        var inside = x >= 4 && x <= 28 && Mathf.Abs(y - 15.5f) <= (28 - x) * 0.5f;
                        texture.SetPixel(x, y, inside ? Color.white : Color.clear);
                    }
                }

                texture.Apply();
                _arrow = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            }

            return _arrow;
        }
    }
}
