using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
/// <item>Las setas que rebotan (<c>BounceShroom</c>) hacen su rebote (animación y sonido) cuando un compañero las toca,
/// como cuando las toca el caballero (el compañero ya rebota en su PC).</item>
/// <item>Los escarabajos que huyen (<c>ScuttlerControl</c>) despiertan cuando un compañero entra en su zona de alerta y
/// huyen del jugador más cercano, sea el local o un compañero (el juego solo mira al caballero local).</item>
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

    private sealed class Shroom {
        public BounceShroom Control;
        public Collider2D[] Boxes;
    }

    private sealed class Scuttler {
        public ScuttlerControl Control;
        public Collider2D Alert;
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo BounceSmallMethod = typeof(BounceShroom).GetMethod("BounceSmall", Private);
    private static readonly MethodInfo LandMethod = typeof(ScuttlerControl).GetMethod("Land", Private);
    private static readonly FieldInfo LandedField = typeof(ScuttlerControl).GetField("landed", Private);
    private static readonly FieldInfo AliveField = typeof(ScuttlerControl).GetField("alive", Private);
    private static readonly FieldInfo HeroField = typeof(ScuttlerControl).GetField("hero", Private);

    /// <summary>Un poco más que la caja de la seta: el caballero rebota al tocarla, sin llegar a entrar.</summary>
    private const float ShroomMargin = 0.3f;

    private readonly List<Shroom> _shrooms = new();
    private readonly List<Scuttler> _scuttlers = new();

    /// <summary>
    /// Agujeros de los que salen escarabajos (<c>Scuttler Spawn</c>): su FSM espera en <c>Detect</c> a que algo toque su
    /// caja (<c>ENTER</c>) y entonces los suelta. El avatar no tiene cuerpo: se le manda el mismo aviso.
    /// </summary>
    private readonly List<Rubble> _spawners = new();

    private const string SpawnerName = "Scuttler Spawn";
    private const string SpawnerDetect = "Detect";
    private const string SpawnerEnter = "ENTER";

    /// <summary>Setas que tocaba cada compañero en la mirada anterior (solo rebotan al tocarlas de nuevo).</summary>
    private readonly Dictionary<ushort, HashSet<Shroom>> _onShroom = new();

    /// <summary>Avatar de cada compañero (para que los escarabajos huyan del más cercano).</summary>
    private readonly Dictionary<ushort, Transform> _avatars = new();

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
    public string Describe() => $"{_pigeons.Count}/{_rubble.Count}/{_shrooms.Count}/{_scuttlers.Count}/{_spawners.Count}";

    /// <summary>Rebotes de seta y escarabajos despertados por un compañero.</summary>
    public int Bounced { get; private set; }

    public int Startled { get; private set; }

    public void SetScene(string scene) {
        if (scene == _scene) {
            return;
        }

        _scene = scene;
        _pigeons.Clear();
        _rubble.Clear();
        _shrooms.Clear();
        _scuttlers.Clear();
        _spawners.Clear();
        _touching.Clear();
        _onShroom.Clear();
        _lastPosition.Clear();
        _scanAt = scene.Length > 0 ? Time.unscaledTime + ScanDelay : -1f;
    }

    /// <summary>El avatar de un compañero se dibujó en <paramref name="position"/>.</summary>
    public void Touch(ushort playerId, Vector3 position, Transform avatar) {
        var now = Time.unscaledTime;
        if (_scanAt >= 0f && now >= _scanAt) {
            Scan();
        }

        _avatars[playerId] = avatar;
        if (_pigeons.Count == 0 && _rubble.Count == 0 && _shrooms.Count == 0 && _scuttlers.Count == 0 && _spawners.Count == 0 ||
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

        TouchShrooms(playerId, box);
        TouchSpawners(box);
        TouchScuttlers(box);
    }

    private void TouchShrooms(ushort playerId, Bounds box) {
        if (_shrooms.Count == 0 || BounceSmallMethod == null) {
            return;
        }

        if (!_onShroom.TryGetValue(playerId, out var on)) {
            on = _onShroom[playerId] = new HashSet<Shroom>();
        }

        foreach (var shroom in _shrooms) {
            if (shroom.Control == null || !shroom.Control.isActiveAndEnabled) {
                continue;
            }

            var inside = false;
            foreach (var collider in shroom.Boxes) {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) {
                    continue;
                }

                var bounds = collider.bounds;
                bounds.Expand(ShroomMargin);
                if (Bounds2D.Overlaps(bounds, box)) {
                    inside = true;
                    break;
                }
            }

            if (inside && on.Add(shroom)) {
                BounceSmallMethod.Invoke(shroom.Control, null);
                Bounced++;
            } else if (!inside) {
                on.Remove(shroom);
            }
        }
    }

    private void TouchSpawners(Bounds box) {
        foreach (var spawner in _spawners) {
            if (spawner.Fsm == null || !spawner.Fsm.isActiveAndEnabled || spawner.Fsm.ActiveStateName != SpawnerDetect ||
                spawner.Box == null || !spawner.Box.enabled || !Bounds2D.Overlaps(spawner.Box.bounds, box)) {
                continue;
            }

            spawner.Fsm.SendEvent(SpawnerEnter);
            Startled++;
            // Lo que suelta se encendió ahora: se buscan para que huyan del más cercano
            foreach (var control in spawner.Fsm.GetComponentsInChildren<ScuttlerControl>(true)) {
                if (!_scuttlers.Exists(known => known.Control == control)) {
                    _scuttlers.Add(new Scuttler { Control = control });
                }
            }
        }
    }

    private void TouchScuttlers(Bounds box) {
        if (_scuttlers.Count == 0 || LandedField == null || AliveField == null) {
            return;
        }

        foreach (var scuttler in _scuttlers) {
            var control = scuttler.Control;
            if (control == null || !control.isActiveAndEnabled || !(bool) AliveField.GetValue(control)) {
                continue;
            }

            // Dormido hasta que alguien entra en su zona: lo despierta también un compañero
            if (control.startIdle && !(bool) LandedField.GetValue(control) && scuttler.Alert != null &&
                scuttler.Alert.enabled && Bounds2D.Overlaps(scuttler.Alert.bounds, box) && LandMethod != null) {
                LandedField.SetValue(control, true);
                control.StartCoroutine((IEnumerator) LandMethod.Invoke(control, null));
                Startled++;
            }

            // Huye del más cercano (el juego mira cada frame a quien tenga en `hero`)
            if (HeroField != null && Nearest(control.transform.position) is { } nearest) {
                HeroField.SetValue(control, nearest);
            }
        }
    }

    /// <summary>El caballero local o el avatar de compañero más cercano a ese punto.</summary>
    private Transform Nearest(Vector3 point) {
        var hero = HeroController.instance;
        var best = hero != null ? hero.transform : null;
        var bestDistance = best != null ? ((Vector2) (best.position - point)).sqrMagnitude : float.MaxValue;
        foreach (var avatar in _avatars.Values) {
            if (avatar == null || !avatar.gameObject.activeInHierarchy) {
                continue;
            }

            var distance = ((Vector2) (avatar.position - point)).sqrMagnitude;
            if (distance < bestDistance) {
                best = avatar;
                bestDistance = distance;
            }
        }

        return best;
    }

    public void Forget(ushort playerId) {
        _touching.Remove(playerId);
        _onShroom.Remove(playerId);
        // Los escarabajos que huían de él vuelven a mirar al caballero local
        if (_avatars.TryGetValue(playerId, out var gone)) {
            _avatars.Remove(playerId);
            foreach (var scuttler in _scuttlers) {
                if (scuttler.Control != null && HeroField != null && HeroController.instance != null &&
                    ReferenceEquals(HeroField.GetValue(scuttler.Control), gone)) {
                    HeroField.SetValue(scuttler.Control, HeroController.instance.transform);
                }
            }
        }

        _lastPosition.Remove(playerId);
        _nextCheck.Remove(playerId);
    }

    public void Destroy() {
        _pigeons.Clear();
        _rubble.Clear();
        _shrooms.Clear();
        _scuttlers.Clear();
        _spawners.Clear();
        _onShroom.Clear();
        _avatars.Clear();
        _touching.Clear();
        _lastPosition.Clear();
    }

    private void Scan() {
        _scanAt = -1f;
        _pigeons.Clear();
        _rubble.Clear();
        _shrooms.Clear();
        _scuttlers.Clear();
        _spawners.Clear();
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
                default:
                    if (fsm.name.StartsWith(SpawnerName, System.StringComparison.Ordinal) &&
                        fsm.Fsm.GetState(SpawnerDetect) != null && fsm.GetComponent<Collider2D>() is { } spawnBox) {
                        _spawners.Add(new Rubble { Fsm = fsm, Box = spawnBox });
                    }

                    break;
            }
        }

        foreach (var shroom in Object.FindObjectsByType<BounceShroom>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            // Algunas solo llevan el componente como marca (sin `active`): no rebotan
            var boxes = shroom.GetComponentsInChildren<Collider2D>(true);
            if (shroom.active && boxes.Length > 0) {
                _shrooms.Add(new Shroom { Control = shroom, Boxes = boxes });
            }
        }

        foreach (var control in Object.FindObjectsByType<ScuttlerControl>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            _scuttlers.Add(new Scuttler {
                Control = control, Alert = control.heroAlert != null ? control.heroAlert.GetComponent<Collider2D>() : null
            });
        }
    }
}
