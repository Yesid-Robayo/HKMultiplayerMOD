using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Sprites del HUD del juego (colección de tk2d "HUD Cln") para la interfaz uGUI del coop: máscara llena, máscara
/// perdida y máscara azul. Se crean una vez sobre la textura del propio juego (no hace falta leer sus píxeles). tk2d
/// guarda algunos girados 90° en su atlas: su imagen se gira al dibujarla (<see cref="Apply"/>).
/// </summary>
internal static class HudSprites {
    public enum Kind {
        Full,
        Empty,
        Blue
    }

    private const string CollectionName = "HUD Cln";

    /// <summary>Máscara llena (la que el HUD muestra quieta).</summary>
    private static readonly string[] FullNames = { "appear_v020004" };

    /// <summary>Hueco de una máscara perdida.</summary>
    private static readonly string[] EmptyNames = { "health_backboard" };

    /// <summary>Máscara azul (Lifeblood).</summary>
    private static readonly string[] BlueNames = { "idle0000" };

    /// <summary>Cuánto se gira la imagen de un sprite guardado girado en el atlas.</summary>
    public static float RotatedAngle = 90f;

    private sealed class Entry {
        public Sprite Sprite;
        public bool Rotated;

        /// <summary>Ancho entre alto tal como se ve.</summary>
        public float Aspect = 0.8f;

        /// <summary>Alto en el juego (sus vértices de tk2d): las máscaras se dibujan a escala entre sí, como en el HUD.</summary>
        public float WorldHeight = 1f;
    }

    private static readonly Entry[] Entries = { new(), new(), new() };
    private static tk2dSpriteCollectionData _collection;
    private static bool _loaded;
    private static float _retryAt;

    public static Sprite FullMask => Entries[(int) Kind.Full].Sprite;
    public static Sprite EmptyMask => Entries[(int) Kind.Empty].Sprite;
    public static Sprite BlueMask => Entries[(int) Kind.Blue].Sprite;

    /// <summary>Ancho entre alto de la máscara llena, tal como se ve.</summary>
    public static float FullAspect => Entries[(int) Kind.Full].Aspect;

    /// <summary>Carga los sprites la primera vez que el HUD del juego existe. Devuelve si hay máscara llena.</summary>
    public static bool TryLoad() {
        if (_loaded) {
            // Sin máscara llena (no se encontró el sprite) o con su textura ya destruida (se cambió una skin del HUD): se
            // vuelve a intentar cada 10 s. Antes el HUD del compañero no salía en toda la partida, sin más aviso
            if (FullMask != null && FullMask.texture != null) {
                return true;
            }

            if (Time.unscaledTime < _retryAt) {
                return false;
            }

            _loaded = false;
            _collection = null;
        }

        _retryAt = Time.unscaledTime + 10f;

        foreach (var candidate in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()) {
            if (candidate.name == CollectionName || candidate.spriteCollectionName == CollectionName) {
                _collection = candidate;
                break;
            }
        }

        if (_collection == null) {
            return false;
        }

        _loaded = true;
        Load(Kind.Full, FullNames);
        Load(Kind.Empty, EmptyNames);
        Load(Kind.Blue, BlueNames);
        Log.Info($"Sprites del HUD: llena={Describe(Kind.Full)} vacía={Describe(Kind.Empty)} azul={Describe(Kind.Blue)}");
        if (FullMask == null) {
            Log.Warn("No se encontró la máscara llena del HUD: el HUD del compañero no se verá (se vuelve a intentar)");
        }

        return FullMask != null;
    }

    /// <summary>Cambia el sprite de un tipo por otro de la colección (modo de prueba). Devuelve si existe.</summary>
    public static bool Override(Kind kind, string name) => TryLoad() && Load(kind, new[] { name });

    private static string Describe(Kind kind) {
        var entry = Entries[(int) kind];
        return entry.Sprite != null
            ? $"{entry.Sprite.name}{(entry.Rotated ? " (girado)" : "")} {entry.Aspect:0.00} alto={entry.WorldHeight:0.00}"
            : "-";
    }

    private static bool Load(Kind kind, string[] names) {
        foreach (var name in names) {
            foreach (var definition in _collection.spriteDefinitions) {
                if (definition.name == name && Create(definition, Entries[(int) kind])) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Sprite sobre la región del atlas que ocupa la definición de tk2d.</summary>
    private static bool Create(tk2dSpriteDefinition definition, Entry entry) {
        // La del juego aunque el HUD propio lleve skin: creadas sobre la de la skin, al cambiarla o quitarla esa textura se
        // destruía y las máscaras del compañero salían en blanco
        var texture = Game.Features.Skins.VanillaTextureOf(_collection.spriteCollectionName) ??
                      (definition.material != null ? definition.material.mainTexture as Texture2D : null);
        if (texture == null || definition.uvs == null || definition.uvs.Length < 4) {
            return false;
        }

        float u0 = 1f, u1 = 0f, v0 = 1f, v1 = 0f;
        foreach (var uv in definition.uvs) {
            u0 = Mathf.Min(u0, uv.x);
            u1 = Mathf.Max(u1, uv.x);
            v0 = Mathf.Min(v0, uv.y);
            v1 = Mathf.Max(v1, uv.y);
        }

        var rect = new Rect(u0 * texture.width, v0 * texture.height, (u1 - u0) * texture.width, (v1 - v0) * texture.height);
        var sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprite.name = definition.name;
        entry.Sprite = sprite;
        entry.Rotated = definition.flipped == tk2dSpriteDefinition.FlipMode.Tk2d;
        entry.Aspect = entry.Rotated ? rect.height / rect.width : rect.width / rect.height;
        if (definition.positions is { Length: >= 4 }) {
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var position in definition.positions) {
                x0 = Mathf.Min(x0, position.x);
                x1 = Mathf.Max(x1, position.x);
                y0 = Mathf.Min(y0, position.y);
                y1 = Mathf.Max(y1, position.y);
            }

            if (y1 > y0 && x1 > x0) {
                entry.WorldHeight = y1 - y0;
                entry.Aspect = (x1 - x0) / (y1 - y0);
            }
        }

        return true;
    }

    /// <summary>Pone el sprite en la imagen con el alto dado (el ancho sale de su proporción), girada si hace falta.</summary>
    public static void Apply(Image image, Kind kind, float height) {
        var entry = Entries[(int) kind];
        // A escala de la llena (la vacía y la azul tienen otro tamaño en el juego)
        var full = Entries[(int) Kind.Full];
        if (entry != full && full.WorldHeight > 0f) {
            height *= entry.WorldHeight / full.WorldHeight;
        }

        image.sprite = entry.Sprite;
        var rect = image.rectTransform;
        if (entry.Rotated) {
            // Antes de girarla, su ancho es lo que se verá de alto
            rect.sizeDelta = new Vector2(height, height * entry.Aspect);
            rect.localEulerAngles = new Vector3(0f, 0f, RotatedAngle);
        } else {
            rect.sizeDelta = new Vector2(height * entry.Aspect, height);
            rect.localEulerAngles = Vector3.zero;
        }
    }
}
