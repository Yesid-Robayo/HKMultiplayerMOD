using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Plantillas "solo visuales" de lo que crean los amuletos del caballero fuera de sus carpetas de efectos: los rayos
/// de Grubberfly's Elegy, las nubes de Spore Shroom y Defender's Crest al curarse, las moscas de Flukenest (y su versión
/// con Defender's Crest) y la onda del grito. El caballero los saca del pool del juego con su daño; en el compañero se
/// reproducen con copias sin colisiones ni lógica (<see cref="RemoteCharmEffects"/>). Cada objeto se reconoce por el
/// prefab con el que se crea (<see cref="TryGetSpawn"/>), igual en todos los PC.
/// </summary>
internal sealed class CharmTemplates {
    /// <summary>Qué es un objeto creado por un amuleto.</summary>
    public enum SpawnKind : byte {
        None = 0,
        Beam = 1,
        Cloud = 2,
        Fluke = 3
    }

    /// <summary>Un rayo de Grubberfly's Elegy (normal o de furia, en cada dirección).</summary>
    internal sealed class BeamInfo {
        public GameObject Prefab;
        public GameObject Template;

        /// <summary>Velocidad con la que sale y desplazamiento inicial (variables de su FSM "Control").</summary>
        public Vector2 Velocity;

        public Vector3 StartShift;
    }

    /// <summary>Nube al curarse (Spore Shroom o Defender's Crest).</summary>
    internal sealed class CloudInfo {
        public GameObject Prefab;
        public GameObject Template;

        /// <summary>Lo que dura antes de reciclarse (espera de su FSM).</summary>
        public float Life;
    }

    /// <summary>Cómo se mueve y acaba una mosca de Flukenest.</summary>
    public enum FlukeBehaviour : byte {
        /// <summary>La normal (<see cref="SpellFluke"/>): vuela, rebota hacia arriba y revienta a los 2-3 s o al tocar un enemigo.</summary>
        Normal,

        /// <summary>La de Defender's Crest: vuela girando, tiembla y explota en una nube.</summary>
        Dung,

        /// <summary>Otra: vuela y desaparece.</summary>
        Plain
    }

    internal sealed class FlukeInfo {
        public GameObject Prefab;
        public GameObject Template;
        public FlukeBehaviour Behaviour;
        public float GravityScale = 1f;

        /// <summary>Frenado del aire de su cuerpo (la de Defender's Crest flota despacio).</summary>
        public float Drag;

        public float BounceFactor = 0.5f;
        public float Radius = 0.25f;
        public AudioSource AudioPlayer;
        public AudioEventRandom SplatSounds;
        public string AirAnim;
        public string FlopAnim;
        public AudioClip BlowClip;
        public GameObject BlowAudioPlayer;
    }

    private readonly Dictionary<GameObject, int> _spawns = new();

    /// <summary>Rayos: R, L, U, D y los mismos de furia (orden de los campos del caballero).</summary>
    public readonly BeamInfo[] Beams = new BeamInfo[8];

    /// <summary>Nubes: 0 Spore Shroom, 1 Defender's Crest.</summary>
    public readonly CloudInfo[] Clouds = new CloudInfo[2];

    public readonly List<FlukeInfo> Flukes = new();

    /// <summary>La onda del grito (Howling Wraiths y Abyss Shriek); se crea tal cual, como hace el caballero.</summary>
    public GameObject RoarWave { get; private set; }

    public CharmTemplates(HeroEffectTemplates owner, HeroController hero, Transform holder) {
        GameObject[] beams = {
            hero.grubberFlyBeamPrefabR, hero.grubberFlyBeamPrefabL, hero.grubberFlyBeamPrefabU, hero.grubberFlyBeamPrefabD,
            hero.grubberFlyBeamPrefabR_fury, hero.grubberFlyBeamPrefabL_fury, hero.grubberFlyBeamPrefabU_fury,
            hero.grubberFlyBeamPrefabD_fury
        };
        for (var i = 0; i < beams.Length; i++) {
            var prefab = beams[i];
            if (prefab == null || _spawns.ContainsKey(prefab)) {
                continue;
            }

            var control = FSMUtility.LocateFSM(prefab, "Control");
            var info = new BeamInfo {
                Prefab = prefab,
                Template = Copy(prefab, holder),
                Velocity = control?.FsmVariables.FindFsmVector2("Launch Velocity")?.Value ?? Vector2.zero,
                StartShift = control?.FsmVariables.FindFsmVector3("Start Shift")?.Value ?? Vector3.zero
            };
            Beams[i] = info;
            _spawns[prefab] = Pack(SpawnKind.Beam, i);
        }

        var spells = FSMUtility.LocateFSM(hero.gameObject, "Spell Control");
        Clouds[0] = PrepareCloud(HeroEffectTemplates.FirstAction<SpawnObjectFromGlobalPool>(spells, "Spore Cloud")?.gameObject.Value,
            "Wait", holder);
        Clouds[1] = PrepareCloud(HeroEffectTemplates.FirstAction<SpawnObjectFromGlobalPool>(spells, "Dung Cloud")?.gameObject.Value,
            "Collider On", holder);
        for (var i = 0; i < Clouds.Length; i++) {
            if (Clouds[i] != null) {
                _spawns[Clouds[i].Prefab] = Pack(SpawnKind.Cloud, i);
            }
        }

        foreach (var top in new[] { owner.FireballTop1, owner.FireballTop2 }) {
            var cast = top != null ? FSMUtility.LocateFSM(top, "Fireball Cast") : null;
            if (cast == null) {
                continue;
            }

            foreach (var state in cast.FsmStates) {
                if (state.Name is not ("Flukes" or "Dung R" or "Dung L")) {
                    continue;
                }

                foreach (var action in state.Actions) {
                    var prefab = action switch {
                        FlingObjectsFromGlobalPool fling => fling.gameObject?.Value,
                        SpawnObjectFromGlobalPool spawn => spawn.gameObject?.Value,
                        _ => null
                    };
                    if (prefab != null && !_spawns.ContainsKey(prefab)) {
                        _spawns[prefab] = Pack(SpawnKind.Fluke, Flukes.Count);
                        Flukes.Add(PrepareFluke(prefab, holder));
                    }
                }
            }
        }

        foreach (var state in spells != null ? spells.FsmStates : System.Array.Empty<FsmState>()) {
            foreach (var action in state.Actions) {
                if (RoarWave == null && action is CreateObject create && create.gameObject?.Value != null &&
                    create.gameObject.Value.name.StartsWith("Roar Wave Emitter", System.StringComparison.Ordinal)) {
                    RoarWave = create.gameObject.Value;
                }
            }
        }

        Log.Info($"Efectos de amuletos del compañero: {_spawns.Count} objetos ({Flukes.Count} moscas), " +
                 $"rayo a {Beams[0]?.Velocity.x ?? 0:0.#}, onda del grito {(RoarWave != null ? "sí" : "no")}");
    }

    /// <summary>Si un prefab sacado del pool es de un amuleto del caballero, y cuál.</summary>
    public bool TryGetSpawn(GameObject prefab, out SpawnKind kind, out int index) {
        if (prefab != null && _spawns.TryGetValue(prefab, out var packed)) {
            kind = (SpawnKind) (packed >> 8);
            index = packed & 0xFF;
            return true;
        }

        kind = SpawnKind.None;
        index = 0;
        return false;
    }

    private static int Pack(SpawnKind kind, int index) => ((int) kind << 8) | index;

    /// <summary>Copia solo visual de un prefab (sin colisiones, cuerpo, scripts ni FSM).</summary>
    private static GameObject Copy(GameObject prefab, Transform holder) {
        var copy = Object.Instantiate(prefab, holder, false);
        copy.name = prefab.name;
        HeroEffectTemplates.Strip(copy, keepFsms: false);
        return copy;
    }

    private static CloudInfo PrepareCloud(GameObject prefab, string waitState, Transform holder) {
        if (prefab == null) {
            return null;
        }

        var control = FSMUtility.LocateFSM(prefab, "Control");
        var wait = HeroEffectTemplates.FirstAction<Wait>(control, waitState);
        return new CloudInfo {
            Prefab = prefab,
            Template = Copy(prefab, holder),
            Life = wait != null && wait.time.Value > 0f ? wait.time.Value : 4f
        };
    }

    private static FlukeInfo PrepareFluke(GameObject prefab, Transform holder) {
        var info = new FlukeInfo { Prefab = prefab, Behaviour = FlukeBehaviour.Plain };
        var spell = prefab.GetComponent<SpellFluke>();
        if (spell != null) {
            info.Behaviour = FlukeBehaviour.Normal;
            info.AudioPlayer = spell.audioPlayerPrefab;
            info.SplatSounds = spell.splatSounds;
            info.AirAnim = spell.airAnim;
            info.FlopAnim = spell.flopAnim;
        } else {
            var control = FSMUtility.LocateFSM(prefab, "Control");
            var blow = HeroEffectTemplates.FirstAction<AudioPlayerOneShotSingle>(control, "Blow");
            if (blow != null) {
                info.Behaviour = FlukeBehaviour.Dung;
                info.BlowClip = blow.audioClip.Value as AudioClip;
                info.BlowAudioPlayer = blow.audioPlayer.Value;
            }
        }

        var body = prefab.GetComponent<Rigidbody2D>();
        if (body != null) {
            info.GravityScale = body.gravityScale;
            info.Drag = body.linearDamping;
        }

        var bounce = prefab.GetComponent<ObjectBounce>();
        if (bounce != null) {
            info.BounceFactor = bounce.bounceFactor;
        }

        var box = prefab.GetComponent<BoxCollider2D>();
        if (box != null) {
            info.Radius = Mathf.Max(0.1f, Mathf.Min(box.size.x, box.size.y) * 0.5f);
        }

        info.Template = Copy(prefab, holder);
        Log.Info($"Efectos: mosca {prefab.name} {info.Behaviour} gravedad={info.GravityScale} frenado={info.Drag} " +
                 $"rebote={info.BounceFactor} radio={info.Radius:0.00}");
        return info;
    }
}
