using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Cuerpo de un jugador caído en su sala: el caballero haciendo su animación de muerte ("Death") y luego tendido con
/// "† Nombre" encima. Solo visual: sin física ni colisión. Lo ven todos los que están en esa sala, también el propio
/// caído si su cámara está ahí.
/// </summary>
internal sealed class DownedBody {
    private const string DeathClip = "Death";

    /// <summary>Tendido boca abajo (el mismo del caballero al despertar en el suelo).</summary>
    private const string LyingClip = "Prostrate";

    /// <summary>Altura del nombre sobre el cuerpo (la misma que el nombre de los avatares sobre el suyo).</summary>
    private const float LabelHeight = 1.3f;

    private readonly GameObject _root;

    /// <summary>El "† Nombre" (null si no hay fuente).</summary>
    private Transform _label;

    private float _labelLift;

    public DownedBody(string name, Vector3 position, HeroController hero, Font font) {
        _root = new GameObject($"Cuerpo de {name}");
        Object.DontDestroyOnLoad(_root);
        _root.transform.position = new Vector3(position.x, position.y, hero.transform.position.z + 0.001f);

        var heroSprite = hero.GetComponent<tk2dSprite>();
        var heroAnimator = hero.GetComponent<tk2dSpriteAnimator>();
        var heroRenderer = hero.GetComponent<MeshRenderer>();
        var spriteObject = new GameObject("Sprite");
        spriteObject.layer = hero.gameObject.layer;
        spriteObject.transform.SetParent(_root.transform, false);
        var heroScale = hero.transform.localScale;
        spriteObject.transform.localScale = new Vector3(Mathf.Abs(heroScale.x), Mathf.Abs(heroScale.y), 1f);
        var sprite = spriteObject.AddComponent<tk2dSprite>();
        sprite.SetSprite(heroSprite.Collection, heroSprite.spriteId);
        var renderer = spriteObject.GetComponent<MeshRenderer>();
        if (heroRenderer != null) {
            renderer.sortingLayerID = heroRenderer.sortingLayerID;
            renderer.sortingOrder = heroRenderer.sortingOrder;
        }

        if (heroAnimator != null && heroAnimator.Library != null) {
            var animator = spriteObject.AddComponent<tk2dSpriteAnimator>();
            animator.Library = heroAnimator.Library;
            var clip = heroAnimator.Library.GetClipByName(DeathClip);
            var lying = heroAnimator.Library.GetClipByName(LyingClip);
            if (clip != null) {
                // La animación de muerte acaba de pie, deshaciéndose: al terminar queda tendido
                animator.Play(clip);
                if (lying != null) {
                    animator.AnimationCompleted = (self, _) => self.Play(lying);
                }
            } else if (lying != null) {
                animator.Play(lying);
            }
        }

        if (font == null) {
            return;
        }

        var label = new GameObject("Nombre");
        _label = label.transform;
        label.transform.SetParent(_root.transform, false);
        label.transform.localPosition = new Vector3(0f, LabelHeight, 0f);
        var text = label.AddComponent<TextMesh>();
        text.font = font;
        text.text = $"† {name}";
        text.fontSize = 48;
        text.characterSize = 0.1f;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(1f, 1f, 1f, 0.85f);
        var labelRenderer = label.GetComponent<MeshRenderer>();
        labelRenderer.sharedMaterial = font.material;
        if (heroRenderer != null) {
            labelRenderer.sortingLayerID = heroRenderer.sortingLayerID;
            labelRenderer.sortingOrder = heroRenderer.sortingOrder + 10;
        }
    }

    public Vector3 Position => _root.transform.position;

    /// <summary>
    /// Sube el "† Nombre" sobre su altura normal (0: en su sitio). Con un compañero encima del cuerpo (el que lo revive),
    /// su nombre quedaba a la misma altura y se pisaban.
    /// </summary>
    public void SetLabelLift(float lift) {
        if (_label == null || Mathf.Abs(lift - _labelLift) < 0.01f) {
            return;
        }

        _labelLift = lift;
        _label.localPosition = new Vector3(0f, LabelHeight + lift, 0f);
    }

    public void Destroy() {
        if (_root != null) {
            Object.Destroy(_root);
        }
    }
}
