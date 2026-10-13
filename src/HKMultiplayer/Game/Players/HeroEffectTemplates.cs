using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Plantillas "solo visuales" de los efectos del caballero local: copias de sus carpetas de efectos (tajos, efectos,
/// hechizos, curación, sonidos, aguijón onírico) sin nada que juegue: sin colisiones, sin daño y sin la lógica que toca
/// al caballero. Queda lo que se ve y se oye (sprites, animaciones, partículas, sonidos y las FSM que solo animan su
/// objeto), que cada compañero copia bajo su avatar (<see cref="RemoteEffects"/>) para hacer sus acciones con los mismos
/// efectos del juego. Se crean una vez por caballero (al cargar otra partida el caballero es otro).
/// </summary>
internal sealed class HeroEffectTemplates {
    /// <summary>
    /// Carpetas del caballero que se copian, en este orden. Además de las de efectos: el escudo de Carefree Melody
    /// (<c>carefreeShield</c>), la muerte de verdad (<c>heroDeathPrefab</c>, sin su FSM, que hace la sombra y vacía el alma)
    /// y el caparazón de Baldur Shell (su FSM se reproduce, ver <see cref="ActionFsmNames"/>).
    /// </summary>
    private static readonly string[] Folders = {
        "Attacks", "Effects", "Spells", "Focus Effects", "Sounds", "Dream Effects", "Brumm_shield", "Hero Death",
        "Charm Effects/Blocker Shield"
    };

    /// <summary>
    /// FSM del caballero cuyos estados se reproducen en el compañero (el índice viaja en el evento). Con "hijo/FSM", la
    /// de un hijo del caballero (Thorns of Agony: su sonido, la animación del caballero ya llega en su estado; el efecto
    /// del golpe recibido: la grieta blanca y los tajos oscuros que salen despedidos).
    /// </summary>
    private static readonly string[] ActionFsmNames = {
        "Spell Control", "Nail Arts", "Superdash", "Dream Nail", "Charm Effects/Thorn Counter",
        "Effects/Damage Effect/Knight Damage", "Charm Effects/Blocker Shield/Control"
    };

    /// <summary>
    /// FSM de los efectos que solo animan su propio objeto (aparecer, animarse, desvanecerse y apagarse): se quedan en
    /// las copias. Las demás (daño, colisiones, las que mueven un ataque con su colisión) se quitan.
    /// </summary>
    private static readonly HashSet<string> VisualFsms = new() {
        "Effect Control", "color_fader", "FSM", "Play Effect", "Recharge Effect", "Rotate", "Deactivate",
        "fade and deactivate", "Deactivate After Delay", "Play", "Hit Box Control"
    };

    /// <summary>Lo que crean los estados reproducidos y es solo visual (se crea igual junto al avatar).</summary>
    private static readonly HashSet<string> VisualSpawns = new() {
        "White Flash R", "Slash Effect GhostDark1", "Slash Effect GhostDark2",
        // Al perder una máscara azul ("Blue Effects" de Knight Damage): gotas, estallido y destello azules
        "Spatter Blue", "Splat Explode Blue", "Blue Flash",
        // Los trozos del caparazón de Baldur Shell al romperse (estado "Bits" de su FSM)
        "Blocker Bit 1", "Blocker Bit 2", "Blocker Bit 3", "Blocker Bit 4", "Blocker Bit 5"
    };

    /// <summary>Tamaño con el que la FSM de un arte del aguijón pone su efecto (Great Slash y Dash Slash).</summary>
    private const float NailArtScale = 1.316208f;

    private readonly GameObject[] _templates = new GameObject[Folders.Length];
    private readonly Dictionary<GameObject, int> _index = new();

    public HeroController Hero { get; }

    /// <summary>Sonidos del caballero (Sounds/...): dash, salto de pared, golpe, carga de las artes.</summary>
    public HeroAudioController HeroAudio { get; }

    /// <summary>Contenedor (inactivo, entre salas) de las plantillas.</summary>
    private readonly GameObject _holder;

    /// <summary>FSM del caballero local que se reproducen (<see cref="ActionFsmNames"/>); pueden faltar.</summary>
    public readonly PlayMakerFSM[] HeroFsms = new PlayMakerFSM[ActionFsmNames.Length];

    /// <summary>Por FSM y estado: si tiene algo que se ve o se oye en el compañero (si no, no se envía).</summary>
    public readonly bool[][] EffectStates = new bool[ActionFsmNames.Length][];

    /// <summary>Tajos del aguijón: normal, alternativo, arriba, abajo y en la pared.</summary>
    public readonly SlashInfo[] Slashes = new SlashInfo[5];

    /// <summary>Artes del aguijón cuya FSM se quita (también enciende su colisión): se animan con <see cref="NailArtEffect"/>.</summary>
    public readonly NailArtInfo[] NailArts = new NailArtInfo[3];

    /// <summary>Lo que lanzan los estados "Fireball 1" y "Fireball 2" (la bola de fuego con su lógica y daño).</summary>
    public GameObject FireballTop1 { get; private set; }

    public GameObject FireballTop2 { get; private set; }

    /// <summary>Bolas de fuego solo visuales (Vengeful Spirit y Shade Soul).</summary>
    public FireballInfo Fireball1 { get; private set; }

    public FireballInfo Fireball2 { get; private set; }

    /// <summary>Partículas del golpe recibido.</summary>
    public readonly GameObject[] HitParticles = new GameObject[2];

    /// <summary>Sonidos de la muerte de verdad (estado "Start" de la FSM de <c>heroDeathPrefab</c>).</summary>
    public readonly List<AudioClip> DeathSounds = new();

    /// <summary>
    /// Sprites de la animación "Wall Slide" del caballero (colección y número): mientras el avatar enseña uno, su copia
    /// del polvo de la pared (<c>wallslideDustPrefab</c>, que el juego enciende al deslizarse) emite.
    /// </summary>
    public readonly HashSet<int> WallSlideSprites = new();

    public string WallSlideCollection { get; private set; }

    /// <summary>Efectos de amuletos que crean objetos aparte (rayos, nubes, moscas, onda del grito); puede faltar.</summary>
    public CharmTemplates Charms { get; private set; }

    /// <summary>Acompañantes que atacan solos (crías, tejedoras, Grimmchild); puede faltar.</summary>
    public CompanionTemplates Companions { get; private set; }

    public HeroEffectTemplates(HeroController hero) {
        Hero = hero;
        HeroAudio = hero.GetComponent<HeroAudioController>();
        _holder = new GameObject("HKMultiplayer plantillas de efectos");
        _holder.SetActive(false);
        Object.DontDestroyOnLoad(_holder);

        var heroTransform = hero.transform;
        for (var f = 0; f < Folders.Length; f++) {
            var folder = heroTransform.Find(Folders[f]);
            if (folder == null) {
                Log.Warn($"Efectos: el caballero no tiene la carpeta {Folders[f]}");
                continue;
            }

            var nodes = folder.GetComponentsInChildren<Transform>(true);
            var template = Object.Instantiate(folder.gameObject, _holder.transform, false);
            template.name = Folders[f];
            Strip(template, keepFsms: true);
            if (template.GetComponentsInChildren<Transform>(true).Length != nodes.Length) {
                Log.Warn($"Efectos: la copia de {Folders[f]} no coincide con el caballero");
                Object.Destroy(template);
                continue;
            }

            _templates[f] = template;
            for (var i = 0; i < nodes.Length; i++) {
                _index[nodes[i].gameObject] = (f << 16) | i;
            }
        }

        for (var i = 0; i < ActionFsmNames.Length; i++) {
            var name = ActionFsmNames[i];
            var slash = name.LastIndexOf('/');
            var owner = slash < 0 ? hero.gameObject : heroTransform.Find(name.Substring(0, slash))?.gameObject;
            HeroFsms[i] = owner != null ? FSMUtility.LocateFSM(owner, name.Substring(slash + 1)) : null;
        }

        // El caparazón de Baldur Shell apunta a sus hijos con variables que su FSM rellena en "Init", tras esperar 0,25 s:
        // si el caballero acaba de cargar, aún están vacías y ningún estado contaría como efecto
        var blocker = System.Array.IndexOf(ActionFsmNames, "Charm Effects/Blocker Shield/Control");
        if (HeroFsms[blocker] != null) {
            FillChild(HeroFsms[blocker], "Shell Anim", "Shell Anim");
            FillChild(HeroFsms[blocker], "Pusher", "Pusher");
            FillChild(HeroFsms[blocker], "Crack", "Hit Crack");
        }

        PrepareSlashes(hero);
        PrepareNailArts(heroTransform);
        try {
            PrepareFireballs();
        } catch (System.Exception e) {
            Log.Warn($"Efectos: sin bolas de fuego del compañero: {e}");
        }

        try {
            Charms = new CharmTemplates(this, hero, _holder.transform);
        } catch (System.Exception e) {
            Log.Warn($"Efectos: sin los efectos de amuletos del compañero: {e}");
        }

        try {
            Companions = new CompanionTemplates(hero, _holder.transform);
        } catch (System.Exception e) {
            Log.Warn($"Efectos: sin los acompañantes del compañero: {e}");
        }

        var wallSlide = hero.GetComponent<tk2dSpriteAnimator>()?.Library?.GetClipByName("Wall Slide");
        if (wallSlide?.frames != null) {
            foreach (var frame in wallSlide.frames) {
                if (frame?.spriteCollection == null) {
                    continue;
                }

                WallSlideCollection ??= frame.spriteCollection.spriteCollectionName;
                if (frame.spriteCollection.spriteCollectionName == WallSlideCollection) {
                    WallSlideSprites.Add(frame.spriteId);
                }
            }
        }

        var death = hero.heroDeathPrefab != null ? FSMUtility.LocateFSM(hero.heroDeathPrefab, "Hero Death Anim") : null;
        foreach (var state in death != null ? death.FsmStates : System.Array.Empty<FsmState>()) {
            if (state.Name != "Start") {
                continue;
            }

            foreach (var action in state.Actions) {
                if (action is AudioPlayerOneShotSingle { audioClip.Value: AudioClip clip }) {
                    DeathSounds.Add(clip);
                }
            }
        }

        HitParticles[0] = heroTransform.Find("Effects/Damage Effect/Hit Pt 1")?.gameObject;
        HitParticles[1] = heroTransform.Find("Effects/Damage Effect/Hit Pt 2")?.gameObject;

        // Después de las bolas de fuego: lo que las lanza cuenta como efecto
        var effectStates = 0;
        for (var i = 0; i < HeroFsms.Length; i++) {
            EffectStates[i] = HeroFsms[i] != null ? FindEffectStates(HeroFsms[i]) : System.Array.Empty<bool>();
            foreach (var effect in EffectStates[i]) {
                effectStates += effect ? 1 : 0;
            }
        }

        Log.Info($"Efectos del caballero preparados: {_index.Count} objetos, {effectStates} estados con efectos, " +
                 $"bolas de fuego a {Fireball1?.Speed ?? 0:0.#}/{Fireball2?.Speed ?? 0:0.#}");
    }

    public bool IsValid => Hero != null && _holder != null;

    public int FolderCount => Folders.Length;

    public GameObject Template(int folder) => _templates[folder];

    /// <summary>Dónde está un objeto del caballero local en las copias: carpeta y posición en su recorrido.</summary>
    public bool TryIndex(GameObject heroObject, out int folder, out int node) {
        if (heroObject != null && _index.TryGetValue(heroObject, out var packed)) {
            folder = packed >> 16;
            node = packed & 0xFFFF;
            return true;
        }

        folder = node = 0;
        return false;
    }

    /// <summary>Pone en una variable de la FSM su hijo con ese nombre, si aún está vacía (como su FindChild).</summary>
    private static void FillChild(PlayMakerFSM fsm, string variableName, string childName) {
        var variable = fsm.FsmVariables.FindFsmGameObject(variableName);
        var child = fsm.transform.Find(childName);
        if (variable != null && variable.Value == null && child != null) {
            variable.Value = child.gameObject;
        }
    }

    public bool IsVisualSpawn(GameObject prefab) => prefab != null && VisualSpawns.Contains(prefab.name);

    public void Destroy() {
        if (_holder != null) {
            Object.Destroy(_holder);
        }
    }

    // =====================================================================================================
    // Quitar lo que juega
    // =====================================================================================================

    /// <summary>
    /// Deja solo lo que se ve y se oye. Primero los scripts y luego lo demás (colisiones, cuerpos), cada objeto del
    /// último componente al primero: así se quita antes lo que depende de otro.
    /// </summary>
    public static void Strip(GameObject root, bool keepFsms) {
        foreach (var node in root.GetComponentsInChildren<Transform>(true)) {
            var components = node.GetComponents<Component>();
            for (var pass = 0; pass < 2; pass++) {
                for (var i = components.Length - 1; i >= 0; i--) {
                    var component = components[i];
                    if (component == null || (component is MonoBehaviour) != (pass == 0) || Keep(component, keepFsms)) {
                        continue;
                    }

                    Object.DestroyImmediate(component);
                }
            }

            // Sin etiqueta de ataque: aunque ya no choca, nada debe tomarlo por un ataque del caballero
            node.gameObject.tag = "Untagged";
        }
    }

    private static bool Keep(Component component, bool keepFsms) => component switch {
        Transform or MeshFilter or MeshRenderer or SpriteRenderer or ParticleSystem or ParticleSystemRenderer or
            AudioSource or Animator or tk2dBaseSprite or tk2dSpriteAnimator => true,
        SetParticleScale or KeepWorldScalePositive or DeactivateAfter2dtkAnimation or DeactivateAfterDelay or
            SimpleSpriteFade or RandomRotationRange => true,
        PlayMakerFSM fsm => keepFsms && VisualFsms.Contains(fsm.FsmName),
        _ => false
    };

    // =====================================================================================================
    // Qué estados se reproducen
    // =====================================================================================================

    private bool[] FindEffectStates(PlayMakerFSM fsm) {
        var states = fsm.FsmStates;
        var result = new bool[states.Length];
        for (var s = 0; s < states.Length; s++) {
            foreach (var action in states[s].Actions) {
                if (action.Enabled && IsEffectAction(fsm.Fsm, action)) {
                    result[s] = true;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Si la acción tiene algo que hacer en el compañero (ver <see cref="RemoteEffects"/>).</summary>
    private bool IsEffectAction(Fsm fsm, FsmStateAction action) {
        switch (action) {
            case ActivateGameObject a:
                return IsCopied(fsm, a.gameObject);
            case SetMeshRenderer a:
                return IsCopied(fsm, a.gameObject);
            case Tk2dPlayAnimation a:
                return IsCopied(fsm, a.gameObject);
            case Tk2dPlayAnimationWithEvents a:
                return IsCopied(fsm, a.gameObject);
            case Tk2dPlayFrame a:
                return IsCopied(fsm, a.gameObject);
            case Tk2dStopAnimation a:
                return IsCopied(fsm, a.gameObject);
            case PlayParticleEmitter a:
                return IsCopied(fsm, a.gameObject);
            case StopParticleEmitter a:
                return IsCopied(fsm, a.gameObject);
            case SetParticleEmissionRate a:
                return IsCopied(fsm, a.gameObject);
            case SetParticleEmission a:
                return IsCopied(fsm, a.gameObject);
            case SendEventByName a:
                return a.eventTarget.target is FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM &&
                       (IsCopied(fsm, a.eventTarget.gameObject) || IsRoarWave(a.eventTarget.gameObject));
            case SendEventByNameV2 a:
                return a.eventTarget.target is FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM &&
                       IsCopied(fsm, a.eventTarget.gameObject);
            case AudioPlay a:
                return IsCopiedOrHero(fsm, a.gameObject);
            case AudioPlaySimple a:
                return IsCopiedOrHero(fsm, a.gameObject);
            case AudioStop a:
                return IsCopiedOrHero(fsm, a.gameObject);
            case FadeAudio a:
                return IsCopiedOrHero(fsm, a.gameObject);
            case AudioPlayRandom:
            case AudioPlayerOneShotSingle:
                return true;
            case SpawnObjectFromGlobalPool a:
                return IsSpawnEffect(a.gameObject.Value);
            case FlingObjectsFromGlobalPool a:
                return IsVisualSpawn(a.gameObject.Value);
            case SpawnBlood:
                return true;
            case CreateObject a:
                return IsSpawnEffect(a.gameObject.Value);
            default:
                return false;
        }
    }

    public bool IsSpawnEffect(GameObject prefab) =>
        prefab != null && (prefab == FireballTop1 || prefab == FireballTop2 || IsVisualSpawn(prefab) ||
                           Charms != null && prefab == Charms.RoarWave);

    /// <summary>
    /// Si una acción apunta a la onda del grito que creó el estado anterior (variable "Roar Wave" de Spell Control): en
    /// el compañero es la suya (<see cref="RemoteCharmEffects"/>).
    /// </summary>
    public static bool IsRoarWave(FsmOwnerDefault owner) =>
        owner is { OwnerOption: OwnerDefaultOption.SpecifyGameObject, GameObject: { UseVariable: true } variable } &&
        variable.Name == "Roar Wave";

    private bool IsCopied(Fsm fsm, FsmOwnerDefault owner) {
        var target = fsm.GetOwnerDefaultTarget(owner);
        return target != null && target != Hero.gameObject && _index.ContainsKey(target);
    }

    private bool IsCopiedOrHero(Fsm fsm, FsmOwnerDefault owner) {
        var target = fsm.GetOwnerDefaultTarget(owner);
        return target != null && (target == Hero.gameObject || _index.ContainsKey(target));
    }

    // =====================================================================================================
    // Tajos, artes del aguijón y bolas de fuego
    // =====================================================================================================

    /// <summary>Un tajo del aguijón: su objeto en el caballero, tamaño base y animaciones (normal, Mark of Pride, furia).</summary>
    internal struct SlashInfo {
        public GameObject HeroObject;
        public Vector3 Scale;
        public tk2dSpriteAnimationClip Clip;
        public tk2dSpriteAnimationClip MantisClip;
        public tk2dSpriteAnimationClip FuryClip;
    }

    private void PrepareSlashes(HeroController hero) {
        NailSlash[] slashes = { hero.normalSlash, hero.alternateSlash, hero.upSlash, hero.downSlash, hero.wallSlash };
        for (var i = 0; i < slashes.Length; i++) {
            var slash = slashes[i];
            var animator = slash != null ? slash.GetComponent<tk2dSpriteAnimator>() : null;
            if (animator == null) {
                continue;
            }

            // Como NailSlash.StartSlash, que añade " M" (Mark of Pride) o " F" (furia) al nombre de la animación
            Slashes[i] = new SlashInfo {
                HeroObject = slash.gameObject,
                Scale = slash.scale,
                Clip = animator.GetClipByName(slash.animName),
                MantisClip = animator.GetClipByName(slash.animName + " M"),
                FuryClip = animator.GetClipByName(slash.animName + " F")
            };
        }
    }

    /// <summary>Un arte del aguijón: su objeto en el caballero, dónde y cómo se pone y sus animaciones.</summary>
    internal struct NailArtInfo {
        public GameObject HeroObject;
        public Vector3 Position;
        public Vector3 Scale;
        public string Clip;
        public string EndClip;
    }

    private void PrepareNailArts(Transform hero) {
        NailArts[0] = NailArt(hero, "Attacks/Great Slash", "NA Big Slash Effect", null, fixedScale: true);
        NailArts[1] = NailArt(hero, "Attacks/Dash Slash", "NA Dash Slash Effect", null, fixedScale: true);
        NailArts[2] = NailArt(hero, "Attacks/Cyclone Slash", "Cyclone Effect", "Cyclone Effect End", fixedScale: false);
    }

    private static NailArtInfo NailArt(Transform hero, string path, string clip, string endClip, bool fixedScale) {
        var art = hero.Find(path);
        if (art == null) {
            return default;
        }

        // Su FSM lo pone en "Start Pos" (si no, donde está) y con un tamaño fijo
        var position = art.localPosition;
        var control = FSMUtility.LocateFSM(art.gameObject, "Control Collider");
        var start = control != null ? control.FsmVariables.FindFsmVector3("Start Pos") : null;
        if (start != null) {
            position = start.Value;
        }

        return new NailArtInfo {
            HeroObject = art.gameObject,
            Position = position,
            Scale = fixedScale ? new Vector3(NailArtScale, NailArtScale, NailArtScale) : art.localScale,
            Clip = clip,
            EndClip = endClip
        };
    }

    /// <summary>
    /// Bola de fuego solo visual: lo que sale junto al caballero al lanzarla (el objeto que la lanza, con su estallido y
    /// partículas), el estallido aparte de Vengeful Spirit, el proyectil, sonido, velocidad y dónde sale.
    /// </summary>
    internal sealed class FireballInfo {
        public GameObject Projectile;
        public GameObject Cast;
        public GameObject Blast;
        public AudioClip Sound;

        /// <summary>Sonido al lanzarla con Flukenest (estado "Fluke R" de su FSM).</summary>
        public AudioClip FlukeSound;
        public float Speed;
        public Vector2 Offset;
        public Vector2 BlastOffset;
        public Vector3 BlastScale = Vector3.one;
    }

    private void PrepareFireballs() {
        var spells = FSMUtility.LocateFSM(Hero.gameObject, "Spell Control");
        FireballTop1 = FirstAction<SpawnObjectFromGlobalPool>(spells, "Fireball 1")?.gameObject.Value;
        FireballTop2 = FirstAction<SpawnObjectFromGlobalPool>(spells, "Fireball 2")?.gameObject.Value;
        Fireball1 = PrepareFireball(FireballTop1, new Vector2(1.17f, -0.54f), new Vector2(1.32f, -1f));
        Fireball2 = PrepareFireball(FireballTop2, new Vector2(0.7f, -0.54f), Vector2.zero);
    }

    /// <summary>
    /// Copia visual de una bola de fuego a partir de lo que lanza (su FSM "Fireball Cast"). Ese prefab aún no se usó y
    /// sus acciones no tienen todos sus valores (las posiciones salen vacías): para esas, las del juego.
    /// </summary>
    private FireballInfo PrepareFireball(GameObject top, Vector2 offset, Vector2 blastOffset) {
        var cast = top != null ? FSMUtility.LocateFSM(top, "Fireball Cast") : null;
        var spawn = FirstAction<SpawnObjectFromGlobalPool>(cast, "Cast Right");
        var prefab = spawn?.gameObject?.Value;
        if (prefab == null) {
            Log.Warn($"Efectos: no se encontró el proyectil de {(top != null ? top.name : "la bola de fuego")}");
            return null;
        }

        var info = new FireballInfo {
            Projectile = Object.Instantiate(prefab, _holder.transform, false),
            Speed = cast.FsmVariables.FindFsmFloat("Fire Speed")?.Value ?? 0f,
            Sound = FirstAction<AudioPlayerOneShotSingle>(cast, "Cast Right")?.audioClip?.Value as AudioClip,
            FlukeSound = FirstAction<AudioPlayerOneShotSingle>(cast, "Fluke R")?.audioClip?.Value as AudioClip,
            Offset = spawn.position != null && !spawn.position.IsNone
                ? new Vector2(Mathf.Abs(spawn.position.Value.x), spawn.position.Value.y)
                : offset
        };
        if (info.Speed <= 0f) {
            info.Speed = 40f;
        }

        info.Projectile.name = prefab.name;
        Strip(info.Projectile, keepFsms: false);

        // Lo que la lanza, sin su lógica: su estallido y sus partículas junto al caballero
        info.Cast = Object.Instantiate(top, _holder.transform, false);
        info.Cast.name = top.name;
        Strip(info.Cast, keepFsms: false);

        // Vengeful Spirit crea además otro estallido al lado (con tamaño 2)
        var create = FirstAction<CreateObject>(cast, "Cast Right");
        var blast = create?.gameObject?.Value;
        if (blast != null) {
            info.BlastOffset = create.position != null && !create.position.IsNone
                ? new Vector2(Mathf.Abs(create.position.Value.x), create.position.Value.y)
                : blastOffset;
            info.BlastScale = new Vector3(2f, 2f, 1f);
            info.Blast = Object.Instantiate(blast, _holder.transform, false);
            info.Blast.name = blast.name;
            Strip(info.Blast, keepFsms: false);
        }

        return info;
    }

    internal static T FirstAction<T>(PlayMakerFSM fsm, string stateName) where T : FsmStateAction {
        if (fsm == null) {
            return null;
        }

        foreach (var state in fsm.FsmStates) {
            if (state.Name != stateName) {
                continue;
            }

            foreach (var action in state.Actions) {
                if (action is T typed) {
                    return typed;
                }
            }
        }

        return null;
    }
}
