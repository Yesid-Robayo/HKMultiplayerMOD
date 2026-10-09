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

    private readonly GameObject _root;

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
        label.transform.SetParent(_root.transform, false);
        label.transform.localPosition = new Vector3(0f, 1.3f, 0f);
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

    public void Destroy() {
        if (_root != null) {
            Object.Destroy(_root);
        }
    }
}
