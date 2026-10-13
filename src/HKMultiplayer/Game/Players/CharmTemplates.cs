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
        Fluke = 3,

        /// <summary>Un efecto suelto del caballero que sale del pool (ráfagas del dash sombra, aterrizaje fuerte...).</summary>
        Puff = 4,

        /// <summary>Muerte en pinchos (0) o en ácido (1): el caballero que sale volando o se hunde.</summary>
        HazardDeath = 5
    }

    /// <summary>
    /// Muerte en pinchos o en ácido (<c>spikeDeathPrefab</c>, <c>acidDeathPrefab</c>): copia sin su FSM, que solo mueve el
    /// cuerpo y suena; lo hace <see cref="HazardDeathEffect"/>.
    /// </summary>
    internal sealed class HazardDeathInfo {
        public GameObject Template;
        public readonly List<AudioClip> Sounds = new();
    }

    /// <summary>Muertes en pinchos (0) y en ácido (1); pueden faltar.</summary>
    public readonly HazardDeathInfo[] HazardDeaths = new HazardDeathInfo[2];

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

    /// <summary>Efecto suelto del pool: su copia y lo que dura como mucho (lo que tarda su animación más larga).</summary>
    internal sealed class PuffInfo {
        public GameObject Prefab;
        public GameObject Template;
        public float Life;

        /// <summary>
        /// Si crece y se desvanece con su FSM "Play Effect" (el anillo del dash sombra): hasta qué escala y en cuánto
        /// tiempo (0 si no).
        /// </summary>
        public float GrowTo;

        public float GrowTime;

        /// <summary>
        /// Polvo del salto y del aterrizaje suave: como <see cref="SoftLandEffect"/> y <see cref="JumpEffects"/>, se
        /// enciende solo el hijo del suelo de la sala (polvo, pasto, hueso o salpicadura). Índices entre sus hijos
        /// directos (-1 si no tiene).
        /// </summary>
        public bool ByGround;

        public int Dust = -1;
        public int Grass = -1;
        public int Bone = -1;
        public int Splash = -1;

        /// <summary>Lo que el salto enciende además con polvo, pasto o hueso: su soplido y la estela.</summary>
        public int JumpPuff = -1;

        public int Trail = -1;

        /// <summary>Sonido del aterrizaje suave (en seco y en agua).</summary>
        public AudioClip LandClip;

        public AudioClip WetClip;

        /// <summary>
        /// Profundidad que le pone su FSM al salir (la nube de Defender's Crest se va detrás del caballero), o NaN si se
        /// queda en la suya. Su FSM la pone después de enviarse, así que no viaja.
        /// </summary>
        public float Z = float.NaN;
    }

    /// <summary>
    /// Efectos sueltos que el código del caballero saca del pool: ráfagas y anillo del dash sombra (también con Sharp
    /// Shadow y hacia abajo con Dashmaster), el polvo del dash en el suelo y el aterrizaje fuerte.
    /// </summary>
    public readonly List<PuffInfo> Puffs = new();

    /// <summary>Índice en <see cref="Puffs"/> del efecto del golpe de 2 o más (<c>takeHitDoublePrefab</c>), o -1.</summary>
    public int DoubleHitPuff { get; private set; } = -1;

    /// <summary>La onda del grito (Howling Wraiths y Abyss Shriek); se crea tal cual, como hace el caballero.</summary>
    public GameObject RoarWave { get; private set; }

    /// <summary>Hijo del caballero con el aura de Defender's Crest (humo, zumbido y la nube que deja cada 0,75 s).</summary>
    public const string DungAuraPath = "Charm Effects/Dung";

    /// <summary>Copia solo visual del aura de Defender's Crest (su humo "Particle 1" y su zumbido); puede faltar.</summary>
    public GameObject DungAura { get; private set; }

    /// <summary>
    /// Polvo al correr (<c>runEffectPrefab</c>): el caballero lo saca del pool, lo lleva pegado mientras corre y al parar
    /// lo suelta (su FSM deja de emitir y se recicla a los 2 s). Copia solo visual; puede faltar.
    /// </summary>
    public GameObject RunEffects { get; private set; }

    public GameObject RunEffectsPrefab { get; private set; }

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

        // La nube que deja el aura de Defender's Crest cada 0,75 s (la saca del pool la FSM de su hijo "Dung")
        var dung = hero.transform.Find(DungAuraPath);
        var dungTrail = dung != null
            ? HeroEffectTemplates.FirstAction<SpawnObjectFromGlobalPoolOverTime>(FSMUtility.LocateFSM(dung.gameObject, "Control"),
                "Equipped")?.gameObject.Value
            : null;
        // Las chispas del aguijón contra la pared también salen del pool. El golpe de 2 o más se crea con Instantiate (no
        // pasa por el pool): va como una más para tener su copia, y se enciende con el aviso del golpe (DoubleHitPuff)
        foreach (var prefab in new[] {
                     hero.shadowdashBurstPrefab, hero.shadowdashDownBurstPrefab, hero.shadowRingPrefab, hero.backDashPrefab,
                     hero.hardLandingEffectPrefab, hero.softLandingEffectPrefab, hero.jumpEffectPrefab, dungTrail,
                     hero.nailTerrainImpactEffectPrefab, hero.takeHitDoublePrefab
                 }) {
            // Solo los que salen del pool (algunos campos apuntan a hijos del caballero, que ya se copian)
            if (prefab == null || prefab.scene.IsValid() || _spawns.ContainsKey(prefab)) {
                continue;
            }

            if (prefab == hero.takeHitDoublePrefab) {
                DoubleHitPuff = Puffs.Count;
            }

            _spawns[prefab] = Pack(SpawnKind.Puff, Puffs.Count);
            var puff = new PuffInfo { Prefab = prefab, Template = Copy(prefab, holder), Life = LifeOf(prefab) };
            // Las acciones de un prefab que aún no se usó pueden no tener sus valores: entonces, los de su FSM en el juego
            // (ScaleTo hasta 1,4 y EaseColor hasta transparente en 0,4 s)
            var effect = FSMUtility.LocateFSM(prefab, "Play Effect");
            if (effect != null && prefab.GetComponent<SpriteRenderer>() != null) {
                var grow = HeroEffectTemplates.FirstAction<ScaleTo>(effect, "Grow");
                var valid = grow != null && grow.duration.Value > 0f && grow.target.Value.x > 0f;
                puff.GrowTo = valid ? grow.target.Value.x : 1.4f;
                puff.GrowTime = valid ? grow.duration.Value : 0.4f;
                puff.Life = puff.GrowTime;
            }

            PrepareGround(puff, prefab);
            var place = HeroEffectTemplates.FirstAction<SetPosition>(FSMUtility.LocateFSM(prefab, "Control"), "Init");
            if (place != null && !place.z.IsNone) {
                puff.Z = place.z.Value;
            }

            Puffs.Add(puff);
        }

        if (dung != null) {
            DungAura = Copy(dung.gameObject, holder);
        }

        // Sus sonidos son los de su FSM (el golpe y, en el ácido, la salpicadura)
        GameObject[] hazards = { hero.spikeDeathPrefab, hero.acidDeathPrefab };
        string[] hazardStates = { "Stab", "Effects" };
        for (var i = 0; i < hazards.Length; i++) {
            var prefab = hazards[i];
            if (prefab == null || prefab.scene.IsValid() || _spawns.ContainsKey(prefab)) {
                continue;
            }

            var info = new HazardDeathInfo { Template = Copy(prefab, holder) };
            var control = prefab.GetComponent<PlayMakerFSM>();
            foreach (var state in control != null ? control.FsmStates : System.Array.Empty<FsmState>()) {
                if (state.Name != hazardStates[i]) {
                    continue;
                }

                foreach (var action in state.Actions) {
                    if (action is AudioPlayerOneShot { audioClips: { Length: > 0 } clips } && clips[0] != null) {
                        info.Sounds.Add(clips[0]);
                    }
                }
            }

            HazardDeaths[i] = info;
            _spawns[prefab] = Pack(SpawnKind.HazardDeath, i);
        }

        if (hero.runEffectPrefab != null) {
            RunEffectsPrefab = hero.runEffectPrefab;
            RunEffects = Copy(hero.runEffectPrefab, holder);
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

    /// <summary>Lo que tarda la animación más larga de un efecto (como mucho 2 s; 1 s si no tiene).</summary>
    private static float LifeOf(GameObject prefab) {
        var life = 0f;
        foreach (var animator in prefab.GetComponentsInChildren<tk2dSpriteAnimator>(true)) {
            var clip = animator.DefaultClip;
            if (clip != null && clip.fps > 0f && clip.frames != null) {
                life = Mathf.Max(life, clip.frames.Length / clip.fps);
            }
        }

        foreach (var particles in prefab.GetComponentsInChildren<ParticleSystem>(true)) {
            life = Mathf.Max(life, particles.main.duration + particles.main.startLifetime.constantMax);
        }

        return life > 0f ? Mathf.Min(life, 2f) : 1f;
    }

    /// <summary>Hijos por suelo del polvo del salto y del aterrizaje suave (sus scripts se quitan en la copia).</summary>
    private static void PrepareGround(PuffInfo puff, GameObject prefab) {
        var land = prefab.GetComponent<SoftLandEffect>();
        var jump = prefab.GetComponent<JumpEffects>();
        if (land == null && jump == null) {
            return;
        }

        var root = prefab.transform;
        puff.ByGround = true;
        puff.Dust = ChildIndex(root, land != null ? land.dustEffects : jump.dustEffects);
        puff.Grass = ChildIndex(root, land != null ? land.grassEffects : jump.grassEffects);
        puff.Bone = ChildIndex(root, land != null ? land.boneEffects : jump.boneEffects);
        puff.Splash = ChildIndex(root, land != null ? land.splash : jump.splash);
        // Lo que tardan en reciclarse (1 s; 1,4 s en el pasto)
        puff.Life = 1.4f;
        if (jump != null) {
            puff.JumpPuff = ChildIndex(root, jump.jumpPuff);
            puff.Trail = ChildIndex(root, jump.dustTrail);
        } else {
            puff.LandClip = land.softLandClip;
            puff.WetClip = land.wetLandClip;
        }
    }

    private static int ChildIndex(Transform root, GameObject child) =>
        child != null && child.transform.parent == root ? child.transform.GetSiblingIndex() : -1;

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
