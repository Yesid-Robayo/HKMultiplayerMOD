using System.Collections.Generic;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// La oscuridad de las salas oscuras con un agujero de luz por jugador. En el juego la oscuridad es la <c>Vignette</c>
/// del caballero: un sprite negro con un agujero a su alrededor, dibujado encima de todo; un compañero fuera de ese
/// agujero no se veía (y dibujarlo encima con un halo parecía una imagen pegada, sin el túnel alrededor). Mientras haya
/// compañeros en una sala oscura, la <c>Vignette</c> no se dibuja y en su lugar va una malla del tamaño de la pantalla:
/// en cada vértice, la oscuridad es la menor entre la de la <c>Vignette</c> centrada en el caballero local y la misma
/// centrada en cada compañero (su textura se lee una vez). Así cada uno lleva su agujero, con la forma y el borde del juego.
/// </summary>
internal sealed class PartnerDarkness {
    /// <summary>Vértices de la malla a lo ancho y a lo alto (el color se interpola entre ellos).</summary>
    private const int Columns = 72;

    private const int Rows = 42;

    /// <summary>Lo que la malla sobresale de la vista, por si la cámara se mueve dentro del frame.</summary>
    private const float Margin = 2f;

    private GameObject _object;
    private Mesh _mesh;
    private MeshRenderer _renderer;
    private Material _material;
    private readonly Vector3[] _vertices = new Vector3[Columns * Rows];
    private readonly Color32[] _colors = new Color32[Columns * Rows];

    /// <summary>Sprite de la <c>Vignette</c> leído y su alfa (copia legible de su trozo de textura).</summary>
    private Sprite _sprite;

    private Color32[] _texels;
    private int _width;
    private int _height;

    /// <summary>Lo que se dejó de dibujar (la <c>Vignette</c> y los rectángulos negros de su borde), para devolverlo.</summary>
    private SpriteRenderer _vignette;

    private readonly List<Renderer> _border = new();
    private bool _active;

    /// <summary>Desplazamiento de cada compañero respecto al caballero local en este frame.</summary>
    private readonly List<Vector2> _offsets = new();

    public bool Active => _active;

    /// <summary>
    /// Pone o quita la oscuridad con agujeros. <paramref name="partners"/>: posiciones de los compañeros que se ven en la
    /// sala. Llamar cada frame, después de mover los avatares.
    /// </summary>
    public void Update(HeroController hero, List<Vector3> partners) {
        var vignette = hero != null ? hero.vignette : null;
        var scene = GameManager.instance != null ? GameManager.instance.sm : null;
        var camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
        if (vignette == null || camera == null || scene == null || scene.darknessLevel < 1 || partners.Count == 0 ||
            !vignette.enabled || !vignette.gameObject.activeInHierarchy || vignette.sprite == null ||
            !Read(vignette.sprite)) {
            Restore();
            return;
        }

        if (_vignette != vignette) {
            Restore();
            _vignette = vignette;
            _border.Clear();
            var border = vignette.transform.Find("Darkness Border");
            if (border != null) {
                _border.AddRange(border.GetComponentsInChildren<Renderer>(true));
            }
        }

        Prepare(vignette);
        _active = true;
        vignette.forceRenderingOff = true;
        foreach (var renderer in _border) {
            if (renderer != null) {
                renderer.forceRenderingOff = true;
            }
        }

        var center = (Vector2) hero.transform.position;
        _offsets.Clear();
        foreach (var partner in partners) {
            _offsets.Add((Vector2) partner - center);
        }

        Fill(vignette, camera);
    }

    /// <summary>Vuelve a dibujar la <c>Vignette</c> del juego y esconde la malla.</summary>
    public void Restore() {
        if (_active) {
            _active = false;
            if (_vignette != null) {
                _vignette.forceRenderingOff = false;
            }

            foreach (var renderer in _border) {
                if (renderer != null) {
                    renderer.forceRenderingOff = false;
                }
            }
        }

        if (_renderer != null && _renderer.enabled) {
            _renderer.enabled = false;
        }
    }

    public void Destroy() {
        Restore();
        if (_object != null) {
            Object.Destroy(_object);
        }

        if (_mesh != null) {
            Object.Destroy(_mesh);
        }

        if (_material != null) {
            Object.Destroy(_material);
        }
    }

    /// <summary>Crea la malla la primera vez y le pone el material y el orden de dibujo de la <c>Vignette</c>.</summary>
    private void Prepare(SpriteRenderer vignette) {
        if (_object == null) {
            _object = new GameObject("HKMultiplayer oscuridad con compañeros");
            Object.DontDestroyOnLoad(_object);
            _mesh = new Mesh { name = "Oscuridad" };
            _mesh.MarkDynamic();
            var triangles = new int[(Columns - 1) * (Rows - 1) * 6];
            var t = 0;
            for (var row = 0; row < Rows - 1; row++) {
                for (var column = 0; column < Columns - 1; column++) {
                    var i = row * Columns + column;
                    triangles[t++] = i;
                    triangles[t++] = i + Columns;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + Columns;
                    triangles[t++] = i + Columns + 1;
                }
            }

            _mesh.vertices = _vertices;
            _mesh.colors32 = _colors;
            _mesh.triangles = triangles;
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _object.AddComponent<MeshRenderer>();
        }

        if (_material == null) {
            // El de la Vignette (Sprites/Default): tiñe con el color de cada vértice; la textura, blanca
            _material = new Material(vignette.sharedMaterial) { mainTexture = Texture2D.whiteTexture };
            _renderer.sharedMaterial = _material;
        }

        _object.layer = vignette.gameObject.layer;
        _renderer.sortingLayerID = vignette.sortingLayerID;
        _renderer.sortingOrder = vignette.sortingOrder;
        if (!_renderer.enabled) {
            _renderer.enabled = true;
        }
    }

    /// <summary>Coloca la malla sobre lo que ve la cámara (en la profundidad de la Vignette) y calcula su oscuridad.</summary>
    private void Fill(SpriteRenderer vignette, Camera camera) {
        var transform = vignette.transform;
        var z = transform.position.z;
        var distance = z - camera.transform.position.z;
        var min = camera.ViewportToWorldPoint(new Vector3(0f, 0f, distance));
        var max = camera.ViewportToWorldPoint(new Vector3(1f, 1f, distance));
        min.x -= Margin;
        min.y -= Margin;
        max.x += Margin;
        max.y += Margin;

        var toLocal = transform.worldToLocalMatrix;
        var bounds = _sprite.bounds;
        var tint = vignette.color;
        for (var row = 0; row < Rows; row++) {
            var y = Mathf.Lerp(min.y, max.y, row / (float) (Rows - 1));
            for (var column = 0; column < Columns; column++) {
                var x = Mathf.Lerp(min.x, max.x, column / (float) (Columns - 1));
                var point = new Vector3(x, y, z);

                // La menor oscuridad entre el agujero propio y el de cada compañero
                var darkest = Sample(toLocal.MultiplyPoint3x4(point), bounds);
                foreach (var offset in _offsets) {
                    var other = Sample(toLocal.MultiplyPoint3x4(new Vector3(x - offset.x, y - offset.y, z)), bounds);
                    if (other.a < darkest.a) {
                        darkest = other;
                    }
                }

                var index = row * Columns + column;
                _vertices[index] = point;
                _colors[index] = new Color32((byte) (darkest.r * tint.r), (byte) (darkest.g * tint.g),
                    (byte) (darkest.b * tint.b), (byte) (darkest.a * tint.a));
            }
        }

        _mesh.vertices = _vertices;
        _mesh.colors32 = _colors;
        _mesh.RecalculateBounds();
    }

    /// <summary>Color de la Vignette en ese punto de su espacio local; fuera de su sprite, negro (lo tapan sus bordes).</summary>
    private Color32 Sample(Vector3 local, Bounds bounds) {
        var u = (local.x - bounds.min.x) / bounds.size.x;
        var v = (local.y - bounds.min.y) / bounds.size.y;
        if (u < 0f || u > 1f || v < 0f || v > 1f) {
            return new Color32(0, 0, 0, 255);
        }

        var fx = u * (_width - 1);
        var fy = v * (_height - 1);
        var x0 = (int) fx;
        var y0 = (int) fy;
        var x1 = Mathf.Min(x0 + 1, _width - 1);
        var y1 = Mathf.Min(y0 + 1, _height - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        var bottom = Color32.Lerp(_texels[y0 * _width + x0], _texels[y0 * _width + x1], tx);
        var top = Color32.Lerp(_texels[y1 * _width + x0], _texels[y1 * _width + x1], tx);
        return Color32.Lerp(bottom, top, ty);
    }

    /// <summary>
    /// Copia legible del trozo de textura del sprite (la del juego no se puede leer desde el código: se pasa por una
    /// RenderTexture). Una vez por sprite.
    /// </summary>
    private bool Read(Sprite sprite) {
        if (sprite == _sprite) {
            return _texels != null;
        }

        _sprite = sprite;
        _texels = null;
        var texture = sprite.texture;
        var rect = sprite.textureRect;
        var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try {
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            _width = Mathf.Max(1, (int) rect.width);
            _height = Mathf.Max(1, (int) rect.height);
            var copy = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(rect.x, rect.y, _width, _height), 0, 0, false);
            _texels = copy.GetPixels32();
            Object.Destroy(copy);
            Log.Info($"Oscuridad con compañeros: leída la textura {sprite.name} ({_width}x{_height})");
        } catch (System.Exception e) {
            Log.Warn($"Oscuridad con compañeros: no se pudo leer {sprite.name}: {e.Message}");
        } finally {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
        }

        return _texels != null;
    }
}
