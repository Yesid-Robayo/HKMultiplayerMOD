using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Bounds = UnityEngine.Bounds;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// El agua y el ácido en los que se nada salpican también al paso de los compañeros, sin red. En el juego lo hace la FSM
/// <c>Surface Water Region</c> de cada zona de agua, pero solo con el caballero local (lo mira con su colisión): el
/// compañero entraba y salía del agua sin salpicar ni sonar.
/// <para>
/// En cada PC, cuando la caja del avatar de un compañero entra en una de esas zonas, se repiten en su sitio las acciones
/// de su estado de entrada (<c>Splash In Norm</c>: gotas, la salpicadura con el color del agua y su sonido), y al salir
/// las de <c>Splash Out effects</c>. De esas acciones solo se repite lo que se ve y suena: lo que mueve al caballero, la
/// cámara o el inventario es del caballero local y no se toca. Un compañero que ya estaba en el agua al verlo por primera
/// vez no salpica.
/// </para>
/// </summary>
internal sealed class PartnerWater {
    private const string RegionFsm = "Surface Water Region";
    private const string EnterState = "Splash In Norm";
    private const string ExitState = "Splash Out effects";

    /// <summary>Espera tras cambiar de sala antes de buscar su agua (la crea al cargar).</summary>
    private const float ScanDelay = 0.5f;

    /// <summary>Cada cuánto se mira dónde está cada avatar (unas 20 veces por segundo).</summary>
    private const float CheckInterval = 0.05f;

    private sealed class Region {
        public PlayMakerFSM Fsm;
        public Collider2D Zone;
        public FsmState Enter;
        public FsmState Exit;
    }

    private sealed class Swimmer {
        public readonly HashSet<Region> Inside = new();
        public float NextCheck;
        public bool Seen;
    }

    private readonly List<Region> _regions = new();
    private readonly Dictionary<ushort, Swimmer> _swimmers = new();
    private string _scene = string.Empty;
    private float _scanAt = -1f;
    private Vector2 _size = new(0.5f, 1.3f);
    private Vector2 _offset;

    /// <summary>Zonas de agua de la sala.</summary>
    public int RegionCount => _regions.Count;

    /// <summary>Salpicaduras de compañeros reproducidas (al entrar y al salir).</summary>
    public int Splashes { get; private set; }

    /// <summary>La sala local cambió: su agua se busca un poco después y lo de cada avatar se olvida.</summary>
    public void SetScene(string scene) {
        if (scene == _scene) {
            return;
        }

        _scene = scene;
        _regions.Clear();
        _swimmers.Clear();
        _scanAt = scene.Length > 0 ? Time.unscaledTime + ScanDelay : -1f;
    }

    /// <summary>El avatar de un jugador se dibujó en <paramref name="position"/>: salpica si entra o sale del agua.</summary>
    public void Touch(ushort playerId, Vector3 position) {
        var now = Time.unscaledTime;
        if (_scanAt >= 0f && now >= _scanAt) {
            Scan();
        }

        if (_regions.Count == 0) {
            return;
        }

        if (!_swimmers.TryGetValue(playerId, out var swimmer)) {
            swimmer = _swimmers[playerId] = new Swimmer();
        }

        if (now < swimmer.NextCheck) {
            return;
        }

        swimmer.NextCheck = now + CheckInterval;
        var box = new Bounds((Vector2) position + _offset, _size);
        foreach (var region in _regions) {
            if (region.Zone == null || region.Fsm == null) {
                continue;
            }

            var inside = region.Zone.enabled && region.Zone.gameObject.activeInHierarchy &&
                         Bounds2D.Overlaps(region.Zone.bounds, box);
            if (inside == swimmer.Inside.Contains(region)) {
                continue;
            }

            if (inside) {
                swimmer.Inside.Add(region);
            } else {
                swimmer.Inside.Remove(region);
            }

            // La primera vez que se ve al avatar solo se apunta dónde está
            if (swimmer.Seen) {
                Replay(inside ? region.Enter : region.Exit, position);
            }
        }

        swimmer.Seen = true;
    }

    /// <summary>El avatar de un jugador dejó la sala (o el jugador se fue).</summary>
    public void Forget(ushort playerId) => _swimmers.Remove(playerId);

    public void Destroy() {
        _regions.Clear();
        _swimmers.Clear();
    }

    /// <summary>Lo que se ve y suena de un estado de la FSM del agua, en el sitio del avatar en vez del caballero local.</summary>
    private void Replay(FsmState state, Vector3 position) {
        if (state == null) {
            return;
        }

        GameObject last = null;
        foreach (var action in state.Actions) {
            if (!action.Enabled) {
                continue;
            }

            switch (action) {
                case FlingObjectsFromGlobalPool fling:
                    Fling(fling, position);
                    break;
                case SpawnObjectFromGlobalPool spawn:
                    // Sin sitio (el goteo al salir) el juego lo pega al caballero local: no es del compañero
                    if (spawn.gameObject.Value != null && spawn.spawnPoint.Value != null) {
                        var at = position + (spawn.position.IsNone ? Vector3.zero : spawn.position.Value);
                        var euler = spawn.rotation.IsNone ? Vector3.zero : spawn.rotation.Value;
                        last = spawn.gameObject.Value.Spawn(at, Quaternion.Euler(euler));
                    }

                    break;
                case SetScale scale when last != null:
                    var current = last.transform.localScale;
                    last.transform.localScale = new Vector3(scale.x.IsNone ? current.x : scale.x.Value,
                        scale.y.IsNone ? current.y : scale.y.Value, scale.z.IsNone ? current.z : scale.z.Value);
                    break;
                case Tk2dSpriteSetColor color when last != null:
                    var sprite = last.GetComponent<tk2dSprite>();
                    if (sprite != null && !color.color.IsNone) {
                        sprite.color = color.color.Value;
                    }

                    break;
                case AudioPlayerOneShotSingle sound:
                    PlaySound(sound, position);
                    break;
            }
        }

        Splashes++;
    }

    private static void Fling(FlingObjectsFromGlobalPool fling, Vector3 position) {
        var prefab = fling.gameObject.Value;
        if (prefab == null) {
            return;
        }

        var at = position + (fling.position.IsNone ? Vector3.zero : fling.position.Value);
        var count = Random.Range(fling.spawnMin.Value, fling.spawnMax.Value + 1);
        for (var i = 0; i < count; i++) {
            var drop = prefab.Spawn(at, Quaternion.identity);
            if (drop == null) {
                continue;
            }

            if (fling.originVariationX != null || fling.originVariationY != null) {
                var p = drop.transform.position;
                var dx = fling.originVariationX != null ? Random.Range(-fling.originVariationX.Value, fling.originVariationX.Value) : 0f;
                var dy = fling.originVariationY != null ? Random.Range(-fling.originVariationY.Value, fling.originVariationY.Value) : 0f;
                drop.transform.position = new Vector3(p.x + dx, p.y + dy, p.z);
            }

            var body = drop.GetComponent<Rigidbody2D>();
            if (body != null) {
                var speed = Random.Range(fling.speedMin.Value, fling.speedMax.Value);
                var angle = Random.Range(fling.angleMin.Value, fling.angleMax.Value) * Mathf.Deg2Rad;
                body.linearVelocity = new Vector2(speed * Mathf.Cos(angle), speed * Mathf.Sin(angle));
            }
        }
    }

    private static void PlaySound(AudioPlayerOneShotSingle sound, Vector3 position) {
        var hero = HeroController.instance;
        var clip = sound.audioClip.Value as AudioClip;
        if (sound.audioPlayer.Value == null || clip == null || hero == null ||
            (hero.transform.position - position).sqrMagnitude > RemoteEffects.AudibleDistance * RemoteEffects.AudibleDistance) {
            return;
        }

        var player = sound.audioPlayer.Value.Spawn(position, Quaternion.identity);
        var source = player != null ? player.GetComponent<AudioSource>() : null;
        if (source != null) {
            source.pitch = Random.Range(sound.pitchMin.Value, sound.pitchMax.Value);
            source.volume = sound.volume.Value;
            source.PlayOneShot(clip);
        }
    }

    /// <summary>Las zonas de agua de la sala, con sus estados de entrada y salida.</summary>
    private void Scan() {
        _scanAt = -1f;
        _regions.Clear();
        var hero = HeroController.instance;
        var box = hero != null ? hero.GetComponent<BoxCollider2D>() : null;
        if (box != null) {
            _size = box.size;
            _offset = box.offset;
        }

        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)) {
            if (fsm.FsmName != RegionFsm) {
                continue;
            }

            var zone = fsm.GetComponent<Collider2D>();
            var states = fsm.FsmStates;
            var enter = states.FirstOrDefault(s => s.Name == EnterState);
            var exit = states.FirstOrDefault(s => s.Name == ExitState);
            if (zone != null && (enter != null || exit != null)) {
                _regions.Add(new Region { Fsm = fsm, Zone = zone, Enter = enter, Exit = exit });
            }
        }

        if (_regions.Count > 0) {
            Util.Log.Info($"Zonas de agua en {_scene}: {_regions.Count}");
        }
    }
}
