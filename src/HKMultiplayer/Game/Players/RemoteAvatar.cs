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

    private MeshRenderer _labelRenderer;

    /// <summary>Halo del caballero (copia de su <c>heroLight</c>), solo en salas oscuras; se crea la primera vez.</summary>
    private SpriteRenderer _light;

    private bool _inDarkness;
    private int _sortingLayer;
    private int _sortingOrder;

    private tk2dSpriteCollectionData _collection;
    private int _spriteId = -1;
    private bool _facingRight;
    private bool _visible = true;

    /// <summary>Parpadeo de invulnerabilidad, como <see cref="InvulnerablePulse"/> del caballero: color y ritmo los suyos.</summary>
    private readonly Color _normalColor = Color.white;

    private readonly Color _invulnerableColor = new(1f, 1f, 1f, 0.5f);
    private readonly float _pulseDuration = 0.3f;
    private bool _pulsing;
    private bool _pulseReverse;
    private float _pulseTime;

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
        var pulse = hero.GetComponent<InvulnerablePulse>();
        if (pulse != null && pulse.pulseDuration > 0f) {
            _invulnerableColor = pulse.invulColor;
            _pulseDuration = pulse.pulseDuration;
        }

        _normalColor = _sprite.color;
        _renderer = spriteObject.GetComponent<MeshRenderer>();
        // Destellos como los del caballero (al curarse o recibir un golpe)
        _flash = spriteObject.AddComponent<SpriteFlash>();
        if (heroRenderer != null) {
            // Mismo orden de dibujo que el caballero local (con uno menor quedaría detrás de capas del escenario); un
            // poco más al fondo para que el propio se vea encima (ver Apply)
            _renderer.sortingLayerID = heroRenderer.sortingLayerID;
            _renderer.sortingOrder = heroRenderer.sortingOrder;
        }

        _sortingLayer = _renderer.sortingLayerID;
        _sortingOrder = _renderer.sortingOrder;

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
            _labelRenderer = labelRenderer;
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

        DungAura = state.DungAura;
        UpdatePulse(state.Invulnerable);
    }

    /// <summary>Va y vuelve entre su color y el de invulnerable mientras lo es; al acabar, su color de siempre.</summary>
    private void UpdatePulse(bool invulnerable) {
        if (!invulnerable) {
            if (_pulsing) {
                _pulsing = false;
                _sprite.color = _normalColor;
            }

            return;
        }

        if (!_pulsing) {
            _pulsing = true;
            _pulseReverse = false;
            _pulseTime = 0f;
        }

        _pulseTime += _pulseReverse ? -Time.deltaTime : Time.deltaTime;
        if (_pulseTime > _pulseDuration) {
            _pulseTime = _pulseDuration;
            _pulseReverse = true;
        } else if (_pulseTime < 0f) {
            _pulseTime = 0f;
            _pulseReverse = false;
        }

        _sprite.color = Color.Lerp(_normalColor, _invulnerableColor, _pulseTime / _pulseDuration);
    }

    /// <summary>
    /// En una sala oscura, la oscuridad es la <c>Vignette</c> del caballero local: un sprite negro con un agujero a su
    /// alrededor, dibujado encima de todo. Un compañero fuera de ese agujero no se veía. Allí se le dibuja encima de la
    /// oscuridad con su halo (una copia de <c>heroLight</c>, con su color de la sala), como si llevara su propia luz. Fuera
    /// de las salas oscuras vuelve a su orden de dibujo (si no, taparía lo que el escenario pone delante del caballero).
    /// </summary>
    public void UpdateDarkness(HeroController hero) {
        var vignette = hero != null ? hero.vignette : null;
        var scene = GameManager.instance != null ? GameManager.instance.sm : null;
        var dark = vignette != null && vignette.enabled && vignette.gameObject.activeInHierarchy && scene != null &&
                   scene.darknessLevel >= 1;
        if (dark != _inDarkness) {
            _inDarkness = dark;
            var layer = dark ? vignette.sortingLayerID : _sortingLayer;
            var order = dark ? vignette.sortingOrder + 2 : _sortingOrder;
            _renderer.sortingLayerID = layer;
            _renderer.sortingOrder = order;
            if (_labelRenderer != null) {
                _labelRenderer.sortingLayerID = layer;
                _labelRenderer.sortingOrder = order + 10;
            }
        }

        var heroLight = hero != null ? hero.heroLight : null;
        if (!dark || heroLight == null) {
            if (_light != null && _light.enabled) {
                _light.enabled = false;
            }

            return;
        }

        if (_light == null) {
            var lightObject = new GameObject("Luz");
            lightObject.transform.SetParent(_root.transform, false);
            _light = lightObject.AddComponent<SpriteRenderer>();
        }

        // El juego cambia su sprite, su color y su tamaño (al entrar en la sala, al recibir un golpe): se copian siempre
        var lightTransform = _light.transform;
        lightTransform.localPosition = heroLight.transform.localPosition;
        lightTransform.localScale = heroLight.transform.localScale;
        _light.sprite = heroLight.sprite;
        _light.sharedMaterial = heroLight.sharedMaterial;
        _light.color = heroLight.color;
        _light.sortingLayerID = vignette.sortingLayerID;
        _light.sortingOrder = vignette.sortingOrder + 1;
        _light.enabled = _visible && heroLight.enabled;
    }

    /// <summary>Si lleva el aura de Defender's Crest encendida (sus efectos la dibujan).</summary>
    public bool DungAura { get; private set; }

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

    /// <summary>Efectos suyos que no cuelgan del avatar (vuelan por su cuenta) y llevan también su skin; puede faltar.</summary>
    public Transform LooseEffects { get; set; }

    /// <summary>Lo que dibuja el cuerpo (su skin se pone aquí).</summary>
    public MeshRenderer Renderer => _renderer;

    /// <summary>Sprite del cuerpo (su colección dice qué hoja de la skin le toca).</summary>
    public tk2dSprite Sprite => _sprite;

    public bool FacingRight => _facingRight;

    /// <summary>Si su caballero se ve en su PC (no está escondido por una cinemática, un viaje o caído).</summary>
    public bool Visible => _visible;

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
