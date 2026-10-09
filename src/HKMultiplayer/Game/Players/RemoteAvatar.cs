using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Lo que se ve de un jugador remoto en la sala: su sprite (los mismos sprites del caballero, sin física ni colisión)
/// y su nombre encima. La raíz no se voltea para que el nombre no salga al revés; se voltea el sprite.
/// </summary>
internal sealed class RemoteAvatar {
    private readonly GameObject _root;

    /// <summary>Objeto raíz del avatar (origen de los golpes de este jugador al aplicarlos en este PC).</summary>
    public GameObject Root => _root;
    private readonly Transform _spriteTransform;
    private readonly tk2dSprite _sprite;
    private readonly MeshRenderer _renderer;
    private readonly SpriteFlash _flash;
    private readonly float _scale;

    /// <summary>Nombre encima (se oculta si el host lo pide).</summary>
    private GameObject _label;

    private bool _nameVisible = true;

    private tk2dSpriteCollectionData _collection;
    private int _spriteId = -1;
    private bool _facingRight;
    private bool _visible = true;

    public RemoteAvatar(string name, HeroController hero, Font font) {
        _root = new GameObject($"Jugador remoto {name}");
        Object.DontDestroyOnLoad(_root);

        var heroTransform = hero.transform;
        _scale = Mathf.Abs(heroTransform.localScale.x);
        var heroSprite = hero.GetComponent<tk2dSprite>();
        var heroRenderer = hero.GetComponent<MeshRenderer>();

        var spriteObject = new GameObject("Sprite");
        spriteObject.layer = hero.gameObject.layer;
        _spriteTransform = spriteObject.transform;
        _spriteTransform.SetParent(_root.transform, false);
        _spriteTransform.localScale = new Vector3(_scale, Mathf.Abs(heroTransform.localScale.y), 1f);
        _sprite = spriteObject.AddComponent<tk2dSprite>();
        _sprite.SetSprite(heroSprite.Collection, heroSprite.spriteId);
        _renderer = spriteObject.GetComponent<MeshRenderer>();
        // Destellos como los del caballero (al curarse o recibir un golpe)
        _flash = spriteObject.AddComponent<SpriteFlash>();
        if (heroRenderer != null) {
            // Mismo orden de dibujo que el caballero local (con uno menor quedaría detrás de capas del escenario); un
            // poco más al fondo para que el propio se vea encima (ver Apply)
            _renderer.sortingLayerID = heroRenderer.sortingLayerID;
            _renderer.sortingOrder = heroRenderer.sortingOrder;
        }

        if (font != null) {
            var label = new GameObject("Nombre");
            _label = label;
            label.transform.SetParent(_root.transform, false);
            label.transform.localPosition = new Vector3(0f, 1.3f, 0f);
            var text = label.AddComponent<TextMesh>();
            text.font = font;
            text.text = name;
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

        _root.transform.position = heroTransform.position;
    }

    /// <summary>Profundidad extra respecto al jugador local: el propio queda delante si se solapan.</summary>
    private const float BehindLocal = 0.0005f;

    /// <summary>Dibuja el estado (ya interpolado). <paramref name="z"/> es la profundidad del jugador local.</summary>
    public void Apply(in PlayerSnapshot state, HeroSprites sprites, float z) {
        _root.transform.position = new Vector3(state.X, state.Y, z + BehindLocal);

        if (state.FacingRight != _facingRight) {
            _facingRight = state.FacingRight;
            // El sprite del caballero mira a la izquierda: escala negativa = mira a la derecha
            var scale = _spriteTransform.localScale;
            scale.x = _facingRight ? -_scale : _scale;
            _spriteTransform.localScale = scale;
        }

        if (state.Visible != _visible) {
            _visible = state.Visible;
            _renderer.enabled = _visible;
        }

        var collection = sprites.Get(state.Collection);
        if (collection != null && (collection != _collection || state.SpriteId != _spriteId) &&
            state.SpriteId < collection.spriteDefinitions.Length) {
            _collection = collection;
            _spriteId = state.SpriteId;
            _sprite.SetSprite(collection, state.SpriteId);
        }
    }

    public Vector3 Position => _root.transform.position;

    /// <summary>Muestra u oculta el nombre (ajuste del host "Nombres").</summary>
    public void SetNameVisible(bool visible) {
        if (visible == _nameVisible || _label == null) {
            return;
        }

        _nameVisible = visible;
        _label.SetActive(visible);
    }

    /// <summary>Lo que se voltea al mirar a un lado: sus efectos van debajo, como en el caballero.</summary>
    public Transform SpriteTransform => _spriteTransform;

    /// <summary>Lo que dibuja el cuerpo (su skin se pone aquí).</summary>
    public MeshRenderer Renderer => _renderer;

    /// <summary>Sprite del cuerpo (su colección dice qué hoja de la skin le toca).</summary>
    public tk2dSprite Sprite => _sprite;

    public bool FacingRight => _facingRight;

    /// <summary>Destello al curarse (como el del caballero).</summary>
    public void FlashHeal() => _flash.flashFocusHeal();

    /// <summary>Destello al recibir un golpe.</summary>
    public void FlashHit() => _flash.flashWhiteQuick();

    public void Destroy() {
        if (_root != null) {
            Object.Destroy(_root);
        }
    }
}
