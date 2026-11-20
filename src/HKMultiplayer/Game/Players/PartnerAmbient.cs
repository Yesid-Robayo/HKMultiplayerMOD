using System.Collections.Generic;
using UnityEngine;
using Bounds = UnityEngine.Bounds;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// El ambiente que reacciona al paso de los compañeros, sin red, como reacciona al del caballero local:
/// <list type="bullet">
/// <item>Las palomas (FSM <c>Pigeon</c>) salen volando cuando un compañero entra en su radio (su hijo <c>Hero Range</c>,
/// con un círculo): se les manda el mismo <c>PIGEON FLY</c> que su FSM se manda al ver al caballero.</item>
/// <item>Los escombros, botellas y piedras sueltas (FSM <c>rubble_push</c>) se empujan cuando la caja de un compañero
/// entra en ellos: se les manda el mismo <c>HIT</c> que les llega al tocarlos el caballero.</item>
/// </list>
/// El avatar no tiene colisión (muchas cosas del juego reaccionan a ella), así que, como con el pasto, se mira cada poco qué
/// toca su caja. Las palomas huyen del lado del caballero local, como en el juego: solo se cambia quién las asusta.
/// </summary>
internal sealed class PartnerAmbient {
    private const float ScanDelay = 0.5f;
    private const float CheckInterval = 0.1f;
    private const string PigeonFsm = "Pigeon";
    private const string PigeonIdle = "Idle";
    private const string PigeonFly = "PIGEON FLY";
    private const string RubbleFsm = "rubble_push";
    private const string RubbleWait = "Wait";
    private const string RubbleHit = "HIT";

    private sealed class Pigeon {
        public PlayMakerFSM Fsm;
        public CircleCollider2D Range;
    }

    private sealed class Rubble {
        public PlayMakerFSM Fsm;
        public Collider2D Box;
    }

    private readonly List<Pigeon> _pigeons = new();
    private readonly List<Rubble> _rubble = new();

    /// <summary>Escombros que tocaba cada compañero en la mirada anterior (solo se empuja al entrar).</summary>
    private readonly Dictionary<ushort, HashSet<Rubble>> _touching = new();

    private readonly Dictionary<ushort, float> _nextCheck = new();

    /// <summary>Dónde estaba cada compañero en la mirada anterior.</summary>
    private readonly Dictionary<ushort, Vector3> _lastPosition = new();

    /// <summary>Más que esto entre dos miradas es un salto (teletransporte, cambio de sitio), no un recorrido.</summary>
    private const float MaxSweep = 4f;
    private string _scene = string.Empty;
    private float _scanAt = -1f;
    private Vector2 _size = new(0.5f, 1.3f);
    private Vector2 _offset;

    /// <summary>Palomas que espantó un compañero y escombros que empujó.</summary>
    public int Scared { get; private set; }

    public int Pushed { get; private set; }

    /// <summary>Palomas y escombros encontrados en la sala (diagnóstico).</summary>
    public string Describe() => $"{_pigeons.Count}/{_rubble.Count}";

    public void SetScene(string scene) {
        if (scene == _scene) {
            return;
        }

        _scene = scene;
        _pigeons.Clear();
        _rubble.Clear();
        _touching.Clear();
        _lastPosition.Clear();
        _scanAt = scene.Length > 0 ? Time.unscaledTime + ScanDelay : -1f;
    }

    /// <summary>El avatar de un compañero se dibujó en <paramref name="position"/>.</summary>
    public void Touch(ushort playerId, Vector3 position) {
        var now = Time.unscaledTime;
        if (_scanAt >= 0f && now >= _scanAt) {
            Scan();
        }

        if (_pigeons.Count == 0 && _rubble.Count == 0 ||
            _nextCheck.TryGetValue(playerId, out var next) && now < next) {
            return;
        }

        _nextCheck[playerId] = now + CheckInterval;
        var box = new Bounds((Vector2) position + _offset, _size);
        // Corriendo, entre dos miradas se avanza casi un trozo entero: la caja cubre también el camino desde la anterior
        if (_lastPosition.TryGetValue(playerId, out var last) && ((Vector2) (position - last)).sqrMagnitude < MaxSweep * MaxSweep) {
            box.Encapsulate(new Bounds((Vector2) last + _offset, _size));
        }

        _lastPosition[playerId] = position;
        foreach (var pigeon in _pigeons) {
            if (pigeon.Fsm == null || pigeon.Range == null || !pigeon.Fsm.isActiveAndEnabled ||
                pigeon.Fsm.ActiveStateName != PigeonIdle) {
                continue;
            }

            var center = (Vector2) pigeon.Range.transform.TransformPoint(pigeon.Range.offset);
            var radius = pigeon.Range.radius * Mathf.Abs(pigeon.Range.transform.lossyScale.x);
            if (((Vector2) box.ClosestPoint(center) - center).sqrMagnitude <= radius * radius) {
                pigeon.Fsm.SendEvent(PigeonFly);
                Scared++;
            }
        }

        if (!_touching.TryGetValue(playerId, out var touching)) {
            touching = _touching[playerId] = new HashSet<Rubble>();
        }

        foreach (var rubble in _rubble) {
            // Los trozos de una farola están apagados hasta que se rompe
            if (rubble.Fsm == null || rubble.Box == null || !rubble.Box.enabled || !rubble.Box.gameObject.activeInHierarchy) {
                continue;
            }

            var inside = Bounds2D.Overlaps(rubble.Box.bounds, box);
            if (inside && touching.Add(rubble)) {
                if (rubble.Fsm.ActiveStateName == RubbleWait) {
                    rubble.Fsm.SendEvent(RubbleHit);
                    Pushed++;
                }
            } else if (!inside) {
                touching.Remove(rubble);
            }
        }
    }

    public void Forget(ushort playerId) {
        _touching.Remove(playerId);
        _lastPosition.Remove(playerId);
        _nextCheck.Remove(playerId);
    }

    public void Destroy() {
        _pigeons.Clear();
        _rubble.Clear();
        _touching.Clear();
        _lastPosition.Clear();
    }

    private void Scan() {
        _scanAt = -1f;
        _pigeons.Clear();
        _rubble.Clear();
        var hero = HeroController.instance;
        var heroBox = hero != null ? hero.GetComponent<BoxCollider2D>() : null;
        if (heroBox != null) {
            _size = heroBox.size;
            _offset = heroBox.offset;
        }

        // También los apagados: los trozos de cristal de las farolas se encienden al romperlas, después de buscar
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            switch (fsm.FsmName) {
                case PigeonFsm:
                    var range = fsm.transform.Find("Hero Range")?.GetComponent<CircleCollider2D>();
                    if (range != null) {
                        _pigeons.Add(new Pigeon { Fsm = fsm, Range = range });
                    }

                    break;
                case RubbleFsm:
                    var collider = fsm.GetComponent<Collider2D>();
                    if (collider != null) {
                        _rubble.Add(new Rubble { Fsm = fsm, Box = collider });
                    }

                    break;
            }
        }
    }
}
