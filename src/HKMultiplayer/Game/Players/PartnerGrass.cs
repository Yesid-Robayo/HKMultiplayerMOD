using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Pasto y telas que se mueven al paso de los compañeros, sin red: en cada PC, lo que toca el avatar de otro jugador se
/// empuja como lo empuja el caballero local al entrar en él (<see cref="GrassBehaviour"/>, <see cref="GrassSpriteBehaviour"/>
/// y <see cref="Grass"/>, que incluye las telas). El corte ya va por <c>BreakableSync</c>.
/// <para>
/// El avatar no tiene colisión ni la etiqueta <c>Player</c> (muchas cosas del juego reaccionan a ella: puertas, zonas de
/// cámara, personajes, enemigos), así que se mira cada poco qué pasto toca su caja (la del caballero local) y se llama a
/// lo mismo que su entrada en él. Sin memoria nueva por frame: colas y conjuntos fijos; el pasto de la sala se busca una
/// vez al cambiar de sala.
/// </para>
/// </summary>
internal sealed class PartnerGrass {
    /// <summary>Cada cuánto se mira lo que toca cada avatar (unas 20 veces por segundo).</summary>
    private const float CheckInterval = 0.05f;

    /// <summary>Espera tras cambiar de sala antes de buscar su pasto (lo crea al cargar).</summary>
    private const float ScanDelay = 0.5f;

    /// <summary>Lo que se mueve en horizontal entre dos miradas para contar como andar por el pasto.</summary>
    private const float MoveThreshold = 0.05f;

    private const int MaxHits = 32;

    private sealed class Walker {
        public HashSet<Collider2D> Inside = new();
        public HashSet<Collider2D> Next = new();
        public float LastX;
        public float NextCheck;
    }

    private static readonly MethodInfo SpriteGrassEnter =
        typeof(GrassSpriteBehaviour).GetMethod("OnTriggerEnter2D", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Colisión de cada pasto de la sala y quién reacciona a ella.</summary>
    private readonly Dictionary<Collider2D, Component> _grass = new();

    private readonly Dictionary<ushort, Walker> _walkers = new();
    private readonly Collider2D[] _hits = new Collider2D[MaxHits];
    private readonly object[] _enterArgs = new object[1];
    private ContactFilter2D _filter;
    private string _scene = string.Empty;
    private float _scanAt = -1f;

    /// <summary>
    /// Lo que le llega al pasto como "quién entró": una caja apagada (no choca con nada) que se pone donde está el avatar.
    /// El pasto solo mira su posición para saber hacia dónde inclinarse.
    /// </summary>
    private BoxCollider2D _probe;

    private Vector2 _size = new(0.5f, 1.3f);
    private Vector2 _offset;

    /// <summary>Pasto de la sala (modo de prueba).</summary>
    public int GrassCount => _grass.Count;

    /// <summary>Veces que el avatar de un compañero empujó pasto (modo de prueba).</summary>
    public int Pushes { get; private set; }

    /// <summary>La sala local cambió: su pasto se busca un poco después y lo tocado se olvida.</summary>
    public void SetScene(string scene) {
        if (scene == _scene) {
            return;
        }

        _scene = scene;
        _grass.Clear();
        _scanAt = scene.Length > 0 ? Time.unscaledTime + ScanDelay : -1f;
        foreach (var walker in _walkers.Values) {
            walker.Inside.Clear();
            walker.Next.Clear();
        }
    }

    /// <summary>El avatar de un jugador se dibujó en <paramref name="position"/>: empuja el pasto en el que entra.</summary>
    public void Touch(ushort playerId, Vector3 position) {
        var now = Time.unscaledTime;
        if (_scanAt >= 0f && now >= _scanAt) {
            Scan();
        }

        if (_grass.Count == 0) {
            return;
        }

        if (!_walkers.TryGetValue(playerId, out var walker)) {
            walker = _walkers[playerId] = new Walker { LastX = position.x };
        }

        if (now < walker.NextCheck) {
            return;
        }

        walker.NextCheck = now + CheckInterval;
        var moving = Mathf.Abs(position.x - walker.LastX) >= MoveThreshold;
        walker.LastX = position.x;
        var count = Physics2D.OverlapBox((Vector2) position + _offset, _size, 0f, _filter, _hits);
        var next = walker.Next;
        next.Clear();
        for (var i = 0; i < count; i++) {
            var hit = _hits[i];
            _hits[i] = null;
            if (hit == null || !_grass.TryGetValue(hit, out var grass) || grass == null) {
                continue;
            }

            next.Add(hit);
            // Al entrar, como OnTriggerEnter2D; andando por dentro, GrassBehaviour se vuelve a mover (su FixedUpdate)
            if (!walker.Inside.Contains(hit)) {
                Push(grass, position, entering: true);
            } else if (moving) {
                Push(grass, position, entering: false);
            }
        }

        walker.Next = walker.Inside;
        walker.Inside = next;
    }

    /// <summary>El avatar de un jugador dejó la sala (o el jugador se fue).</summary>
    public void Forget(ushort playerId) => _walkers.Remove(playerId);

    public void Destroy() {
        if (_probe != null) {
            Object.Destroy(_probe.gameObject);
            _probe = null;
        }

        _grass.Clear();
        _walkers.Clear();
    }

    private void Push(Component grass, Vector3 position, bool entering) {
        if (_probe == null) {
            var probe = new GameObject("HKMultiplayer paso del compañero");
            Object.DontDestroyOnLoad(probe);
            _probe = probe.AddComponent<BoxCollider2D>();
            _probe.isTrigger = true;
            _probe.enabled = false;
        }

        _probe.transform.position = position;
        switch (grass) {
            case GrassBehaviour behaviour:
                // Lo mismo que al entrar el caballero (solo si ya volvió a su sitio): inclinarse hacia el otro lado y sonar
                behaviour.WindReact(_probe);
                break;
            case GrassSpriteBehaviour sprite when entering && SpriteGrassEnter != null:
                _enterArgs[0] = _probe;
                SpriteGrassEnter.Invoke(sprite, _enterArgs);
                _enterArgs[0] = null;
                break;
            case Grass rag when entering:
                rag.Push(isAllGrass: false);
                break;
            default:
                return;
        }

        Pushes++;
    }

    /// <summary>El pasto de la sala: sus colisiones y en qué capas están (solo esas se miran).</summary>
    private void Scan() {
        _scanAt = -1f;
        _grass.Clear();
        var hero = HeroController.instance;
        var box = hero != null ? hero.GetComponent<BoxCollider2D>() : null;
        if (box != null) {
            _size = box.size;
            _offset = box.offset;
        }

        var layers = 0;
        layers |= Add(Object.FindObjectsByType<GrassBehaviour>(FindObjectsSortMode.None));
        layers |= Add(Object.FindObjectsByType<GrassSpriteBehaviour>(FindObjectsSortMode.None));
        layers |= Add(Object.FindObjectsByType<Grass>(FindObjectsSortMode.None));
        _filter = new ContactFilter2D { useTriggers = true, useLayerMask = true, layerMask = layers };
    }

    private int Add<T>(T[] found) where T : Component {
        var layers = 0;
        foreach (var grass in found) {
            foreach (var collider in grass.GetComponentsInChildren<Collider2D>()) {
                if (!collider.isTrigger || _grass.ContainsKey(collider)) {
                    continue;
                }

                _grass[collider] = grass;
                layers |= 1 << collider.gameObject.layer;
            }
        }

        return layers;
    }
}
