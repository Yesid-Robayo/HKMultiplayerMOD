using HutongGames.PlayMaker;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Lo que crean los amuletos de un compañero fuera de su caballero, reproducido en el PC que mira con copias solo
/// visuales (<see cref="CharmTemplates"/>): rayos de Grubberfly's Elegy, nubes de Spore Shroom y Defender's Crest al
/// curarse, moscas de Flukenest (y su versión con Defender's Crest) y la onda de los gritos. No dañan, no chocan con
/// nada del juego (se mueven solas, con rayos contra el terreno) y no gastan nada: el daño ya lo hizo el PC del dueño.
/// Las copias se crean la primera vez que hacen falta y se reutilizan (sin memoria nueva por efecto).
/// </summary>
internal sealed class RemoteCharmEffects {
    private const int BeamsPerKind = 3;
    private const int CloudsPerKind = 2;
    private const int FlukesPerKind = 16;
    private const int PuffsPerKind = 3;

    /// <summary>Lo que tarda un rayo en empezar a deshacerse (esperas "Inactive" y "Active" de su FSM).</summary>
    private const float BeamFlight = 0.23f;

    /// <summary>Lo que se deja encendido un rayo (su FSM lo recicla 1 s después de acabar su animación final).</summary>
    private const float BeamLife = 1.4f;

    /// <summary>Frenado por frame de un rayo al deshacerse (DecelerateV2 de su FSM).</summary>
    private const float BeamDeceleration = 0.85f;

    /// <summary>La nube de Defender's Crest deja de echar humo al acabar su FSM y se apaga después.</summary>
    private const float CloudFade = 1.5f;

    /// <summary>Fases de la mosca de Defender's Crest: vuela, tiembla y explota en una nube que dura.</summary>
    private const float DungAir = 1f;

    private const float DungAntic = 1f;
    private const float DungCloudLife = 3f;

    /// <summary>Lo que se deja encendida una mosca al reventar (su salpicadura).</summary>
    private const float SplatLife = 1f;

    /// <summary>Como mucho dura la onda de un grito si no llega su final (no debe quedarse temblando la cámara).</summary>
    private const float RoarWaveMax = 3f;

    /// <summary>Capa de los enemigos (al tocar uno, la mosca revienta como en el PC del dueño).</summary>
    private const int EnemyMask = 1 << 11;

    private sealed class Beam {
        public GameObject Object;
        public Transform Transform;
        public MeshRenderer Renderer;
        public tk2dSpriteAnimator Animator;
        public tk2dSpriteAnimationClip IdleClip;
        public tk2dSpriteAnimationClip EndClip;
        public GameObject Impact;
        public tk2dSpriteAnimator ImpactAnimator;
        public AudioSource Audio;
        public Vector2 Velocity;
        public float Age;
        public bool Active;
        public bool Ending;
    }

    private sealed class Cloud {
        public GameObject Object;
        public Transform Transform;
        public ParticleSystem[] Particles;
        public tk2dSpriteAnimator[] Animators;
        public GameObject Normal;
        public GameObject Deep;
        public float Age;
        public float Life;
        public bool Fading;
        public bool Active;
    }

    private sealed class Fluke {
        public CharmTemplates.FlukeInfo Info;
        public GameObject Object;
        public Transform Transform;
        public MeshRenderer Renderer;
        public tk2dSpriteAnimator Animator;
        public GameObject Splat;
        public GameObject DungCloud;
        public ParticleSystem Antic;
        public Vector2 Velocity;
        public float Spin;
        public float Age;
        public float Life;
        public bool Bounced;
        public bool Burst;
        public bool Antics;
        public bool Active;
    }

    private readonly CharmTemplates _templates;
    private readonly HeroController _hero;
    private readonly Transform _parking;
    private readonly Beam[] _beams;
    private readonly Cloud[] _clouds;
    private readonly Fluke[] _flukes;
    private readonly Puff[] _puffs;
    private GameObject _roarWave;
    private PlayMakerFSM _roarEmitter;
    private float _roarWaveAge;

    public RemoteCharmEffects(CharmTemplates templates, HeroController hero, Transform parking) {
        _templates = templates;
        _hero = hero;
        _parking = parking;
        _beams = new Beam[templates.Beams.Length * BeamsPerKind];
        _clouds = new Cloud[templates.Clouds.Length * CloudsPerKind];
        _flukes = new Fluke[templates.Flukes.Count * FlukesPerKind];
        _puffs = new Puff[templates.Puffs.Count * PuffsPerKind];
    }

    // =====================================================================================================
    // Rayos de Grubberfly's Elegy
    // =====================================================================================================

    /// <summary>Un rayo que sale del avatar, como HeroController.Attack y la FSM "Control" del rayo.</summary>
    public void SpawnBeam(int index, Vector3 origin, float angle, float scaleY) {
        if (index >= _templates.Beams.Length || _templates.Beams[index] == null) {
            return;
        }

        var info = _templates.Beams[index];
        var beam = FreeBeam(index, info);
        if (beam == null) {
            return;
        }

        var baseScale = info.Template.transform.localScale;
        beam.Transform.position = origin + info.StartShift;
        beam.Transform.localEulerAngles = new Vector3(0f, 0f, angle);
        beam.Transform.localScale = new Vector3(baseScale.x, scaleY, baseScale.z);
        beam.Velocity = info.Velocity;
        beam.Age = 0f;
        beam.Ending = false;
        beam.Active = true;
        if (beam.Impact != null) {
            beam.Impact.SetActive(false);
        }

        beam.Object.SetActive(true);
        if (beam.Renderer != null) {
            beam.Renderer.enabled = true;
        }

        if (beam.Animator != null && beam.IdleClip != null) {
            beam.Animator.AnimationCompleted = null;
            beam.Animator.PlayFromFrame(beam.IdleClip, 0);
        }

        if (beam.Audio != null) {
            if (beam.Audio.playOnAwake && Audible(origin)) {
                beam.Audio.Play();
            } else {
                beam.Audio.Stop();
            }
        }
    }

    private Beam FreeBeam(int index, CharmTemplates.BeamInfo info) {
        for (var i = index * BeamsPerKind; i < (index + 1) * BeamsPerKind; i++) {
            var beam = _beams[i];
            if (beam == null) {
                var copy = Object.Instantiate(info.Template, _parking, false);
                copy.name = info.Template.name;
                copy.SetActive(false);
                var animator = copy.GetComponent<tk2dSpriteAnimator>();
                var impact = copy.transform.Find("Impact")?.gameObject;
                beam = _beams[i] = new Beam {
                    Object = copy,
                    Transform = copy.transform,
                    Renderer = copy.GetComponent<MeshRenderer>(),
                    Animator = animator,
                    IdleClip = animator != null ? animator.GetClipByName("Idle") : null,
                    EndClip = animator != null ? animator.GetClipByName("End") : null,
                    Impact = impact,
                    ImpactAnimator = impact != null ? impact.GetComponent<tk2dSpriteAnimator>() : null,
                    Audio = copy.GetComponent<AudioSource>()
                };
                if (animator != null) {
                    animator.AnimationCompleted = RemoteEffects.HideWhenDone;
                }

                if (beam.ImpactAnimator != null) {
                    beam.ImpactAnimator.AnimationCompleted = RemoteEffects.HideWhenDone;
                }
            }

            if (!beam.Active) {
                return beam;
            }
        }

        return null;
    }

    private static void UpdateBeam(Beam beam, float deltaTime) {
        if (beam.Object == null) {
            beam.Active = false;
            return;
        }

        beam.Age += deltaTime;
        if (beam.Age >= BeamLife) {
            beam.Active = false;
            beam.Object.SetActive(false);
            return;
        }

        if (beam.Velocity == Vector2.zero) {
            return;
        }

        if (beam.Age >= BeamFlight && !beam.Ending) {
            // Se deshace: frena y hace su animación final
            beam.Ending = true;
            if (beam.Animator != null && beam.EndClip != null) {
                beam.Animator.AnimationCompleted = RemoteEffects.HideWhenDone;
                beam.Animator.PlayFromFrame(beam.EndClip, 0);
            }
        }

        if (beam.Ending) {
            beam.Velocity *= Mathf.Pow(BeamDeceleration, deltaTime * 60f);
        }

        var position = beam.Transform.position;
        var step = beam.Velocity * deltaTime;
        var distance = step.magnitude;
        var hit = Physics2D.Raycast(position, step / Mathf.Max(distance, 0.0001f), distance + 0.3f, RemoteEffects.FireballWalls);
        if (hit.collider != null && !beam.Ending) {
            // Choca con el terreno: se apaga y sale su impacto
            beam.Velocity = Vector2.zero;
            if (beam.Renderer != null) {
                beam.Renderer.enabled = false;
            }

            if (beam.Impact != null) {
                beam.Impact.SetActive(true);
                var impactRenderer = beam.Impact.GetComponent<MeshRenderer>();
                if (impactRenderer != null) {
                    impactRenderer.enabled = true;
                }

                beam.ImpactAnimator?.PlayFromFrame(0);
            }

            return;
        }

        beam.Transform.position = new Vector3(position.x + step.x, position.y + step.y, position.z);
    }

    // =====================================================================================================
    // Nubes al curarse
    // =====================================================================================================

    /// <summary>Nube de Spore Shroom (0) o de Defender's Crest (1), como su FSM "Control" (con Deep Focus, más grande).</summary>
    public void SpawnCloud(int index, Vector3 position, float scale) {
        if (index >= _templates.Clouds.Length || _templates.Clouds[index] == null) {
            return;
        }

        var info = _templates.Clouds[index];
        var cloud = FreeCloud(index, info);
        if (cloud == null) {
            return;
        }

        cloud.Object.SetActive(false);
        cloud.Transform.position = new Vector3(position.x, position.y, info.Template.transform.position.z);
        cloud.Transform.localScale = new Vector3(scale, scale, 1f);
        cloud.Age = 0f;
        cloud.Life = info.Life;
        cloud.Fading = false;
        cloud.Active = true;
        for (var i = 0; i < cloud.Transform.childCount; i++) {
            cloud.Transform.GetChild(i).gameObject.SetActive(true);
        }

        // Spore Shroom con Deep Focus: más grande y con sus propias partículas
        var deep = scale > 1.2f;
        if (cloud.Normal != null && cloud.Deep != null) {
            cloud.Normal.SetActive(!deep);
            cloud.Deep.SetActive(deep);
        }

        cloud.Object.SetActive(true);
        foreach (var particles in cloud.Particles) {
            if (particles.gameObject.activeInHierarchy) {
                var emission = particles.emission;
                emission.enabled = true;
                particles.Play();
            }
        }

        // Las líneas del golpe, giradas y de tamaño al azar como las suyas
        foreach (var animator in cloud.Animators) {
            var lines = animator.transform;
            var size = Random.value < 0.5f ? 1f : 1.25f;
            size *= Random.value < 0.5f ? 1f : -1f;
            lines.localEulerAngles = new Vector3(0f, 0f, Random.Range(0f, 360f));
            lines.localScale = new Vector3(size * Mathf.Abs(lines.localScale.x), size * Mathf.Abs(lines.localScale.y), 1f);
            var renderer = animator.GetComponent<MeshRenderer>();
            if (renderer != null) {
                renderer.enabled = true;
            }

            animator.PlayFromFrame(0);
        }
    }

    private Cloud FreeCloud(int index, CharmTemplates.CloudInfo info) {
        Cloud oldest = null;
        for (var i = index * CloudsPerKind; i < (index + 1) * CloudsPerKind; i++) {
            var cloud = _clouds[i];
            if (cloud == null) {
                var copy = Object.Instantiate(info.Template, _parking, false);
                copy.name = info.Template.name;
                copy.SetActive(false);
                cloud = _clouds[i] = new Cloud {
                    Object = copy,
                    Transform = copy.transform,
                    Particles = copy.GetComponentsInChildren<ParticleSystem>(true),
                    Animators = copy.GetComponentsInChildren<tk2dSpriteAnimator>(true),
                    Normal = copy.transform.Find("Pt Normal")?.gameObject,
                    Deep = copy.transform.Find("Pt Deep")?.gameObject
                };
                foreach (var animator in cloud.Animators) {
                    animator.AnimationCompleted = RemoteEffects.HideWhenDone;
                }
            }

            if (!cloud.Active) {
                return cloud;
            }

            if (oldest == null || cloud.Age > oldest.Age) {
                oldest = cloud;
            }
        }

        // Todas en uso: se reutiliza la más vieja
        return oldest;
    }

    private static void UpdateCloud(Cloud cloud, float deltaTime) {
        if (cloud.Object == null) {
            cloud.Active = false;
            return;
        }

        cloud.Age += deltaTime;
        if (!cloud.Fading && cloud.Age >= cloud.Life) {
            cloud.Fading = true;
            foreach (var particles in cloud.Particles) {
                var emission = particles.emission;
                emission.enabled = false;
            }
        }

        if (cloud.Age >= cloud.Life + CloudFade) {
            cloud.Active = false;
            cloud.Object.SetActive(false);
        }
    }

    // =====================================================================================================
    // Moscas de Flukenest
    // =====================================================================================================

    /// <summary>Una mosca, con la posición y velocidad con las que salió en el PC del dueño.</summary>
    public void SpawnFluke(int index, Vector2 position, Vector2 velocity, float angle, float scale, float spin) {
        if (index >= _templates.Flukes.Count) {
            return;
        }

        var info = _templates.Flukes[index];
        var fluke = FreeFluke(index, info);
        if (fluke == null) {
            return;
        }

        var z = info.Template.transform.position.z;
        fluke.Transform.position = new Vector3(position.x, position.y, z);
        fluke.Transform.localEulerAngles = new Vector3(0f, 0f, angle);
        var baseScale = info.Template.transform.localScale;
        fluke.Transform.localScale = new Vector3(scale, Mathf.Abs(scale) * Mathf.Sign(baseScale.y), baseScale.z);
        fluke.Velocity = velocity;
        fluke.Spin = spin;
        fluke.Age = 0f;
        fluke.Life = info.Behaviour == CharmTemplates.FlukeBehaviour.Normal ? Random.Range(2f, 3f) : 2f;
        fluke.Bounced = false;
        fluke.Burst = false;
        fluke.Antics = false;
        fluke.Active = true;
        if (fluke.Splat != null) {
            fluke.Splat.SetActive(false);
        }

        if (fluke.DungCloud != null) {
            fluke.DungCloud.SetActive(false);
        }

        fluke.Object.SetActive(true);
        if (fluke.Renderer != null) {
            fluke.Renderer.enabled = true;
        }

        PlayFlukeClip(fluke, info.Behaviour == CharmTemplates.FlukeBehaviour.Dung ? "Dung Air" : info.AirAnim);
    }

    private Fluke FreeFluke(int index, CharmTemplates.FlukeInfo info) {
        for (var i = index * FlukesPerKind; i < (index + 1) * FlukesPerKind; i++) {
            var fluke = _flukes[i];
            if (fluke == null) {
                var copy = Object.Instantiate(info.Template, _parking, false);
                copy.name = info.Template.name;
                copy.SetActive(false);
                var transform = copy.transform;
                fluke = _flukes[i] = new Fluke {
                    Info = info,
                    Object = copy,
                    Transform = transform,
                    Renderer = copy.GetComponent<MeshRenderer>(),
                    Animator = copy.GetComponent<tk2dSpriteAnimator>(),
                    Splat = transform.Find("Splat")?.gameObject,
                    DungCloud = transform.Find("Knight Dung Cloud")?.gameObject,
                    Antic = transform.Find("Pt Antic")?.GetComponent<ParticleSystem>()
                };
            }

            if (!fluke.Active) {
                return fluke;
            }
        }

        return null;
    }

    private static void PlayFlukeClip(Fluke fluke, string clipName) {
        if (fluke.Animator == null || string.IsNullOrEmpty(clipName)) {
            return;
        }

        var clip = fluke.Animator.GetClipByName(clipName);
        if (clip != null) {
            fluke.Animator.PlayFromFrame(clip, 0);
        }
    }

    private void UpdateFluke(Fluke fluke, float deltaTime) {
        if (fluke.Object == null) {
            fluke.Active = false;
            return;
        }

        fluke.Age += deltaTime;
        var info = fluke.Info;
        if (fluke.Burst) {
            var linger = info.Behaviour == CharmTemplates.FlukeBehaviour.Dung ? DungCloudLife : SplatLife;
            if (fluke.Age >= linger) {
                fluke.Active = false;
                fluke.Object.SetActive(false);
            }

            return;
        }

        var position = (Vector2) fluke.Transform.position;
        switch (info.Behaviour) {
            case CharmTemplates.FlukeBehaviour.Normal:
                if (fluke.Age >= fluke.Life || Physics2D.OverlapCircle(position, info.Radius, EnemyMask) != null) {
                    BurstFluke(fluke, position);
                    return;
                }

                break;
            case CharmTemplates.FlukeBehaviour.Dung:
                if (!fluke.Antics && fluke.Age >= DungAir) {
                    // Tiembla antes de explotar
                    fluke.Antics = true;
                    PlayFlukeClip(fluke, "Dung Antic");
                    fluke.Antic?.Play();
                }

                if (fluke.Age >= DungAir + DungAntic || Physics2D.OverlapCircle(position, info.Radius, EnemyMask) != null) {
                    BurstFluke(fluke, position);
                    return;
                }

                fluke.Transform.Rotate(0f, 0f, fluke.Spin * deltaTime);
                break;
            default:
                if (fluke.Age >= fluke.Life) {
                    fluke.Active = false;
                    fluke.Object.SetActive(false);
                    return;
                }

                break;
        }

        // Cae con su gravedad y rebota en el terreno, como su cuerpo y ObjectBounce
        fluke.Velocity.y += Physics2D.gravity.y * info.GravityScale * deltaTime;
        if (info.Drag > 0f) {
            fluke.Velocity *= 1f / (1f + deltaTime * info.Drag);
        }

        var step = fluke.Velocity * deltaTime;
        var distance = step.magnitude;
        if (distance > 0.0001f) {
            var hit = Physics2D.CircleCast(position, info.Radius, step / distance, distance, RemoteEffects.FireballWalls);
            if (hit.collider != null) {
                position = hit.centroid + hit.normal * 0.02f;
                var speed = fluke.Velocity.magnitude;
                fluke.Velocity = Vector2.Reflect(fluke.Velocity / Mathf.Max(speed, 0.0001f), hit.normal) *
                                 (speed * info.BounceFactor * Random.Range(0.8f, 1.2f));
                if (info.Behaviour == CharmTemplates.FlukeBehaviour.Normal) {
                    // SpellFluke: rebota hacia arriba, de lado al azar, y cambia de animación
                    fluke.Bounced = true;
                    fluke.Velocity.x = Random.Range(-5f, 5f);
                    fluke.Velocity.y = Mathf.Clamp(fluke.Velocity.y, Random.Range(7.3f, 15f), Random.Range(20f, 25f));
                    PlayFlukeClip(fluke, info.FlopAnim);
                    fluke.Transform.localEulerAngles = Vector3.zero;
                }
            } else {
                position += step;
            }
        }

        fluke.Transform.position = new Vector3(position.x, position.y, fluke.Transform.position.z);
        if (info.Behaviour == CharmTemplates.FlukeBehaviour.Normal && !fluke.Bounced) {
            fluke.Transform.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(fluke.Velocity.y, fluke.Velocity.x) * Mathf.Rad2Deg);
        }
    }

    /// <summary>Revienta (SpellFluke.Burst) o explota en su nube (estado "Blow" de la de Defender's Crest).</summary>
    private void BurstFluke(Fluke fluke, Vector2 position) {
        fluke.Burst = true;
        fluke.Age = 0f;
        fluke.Velocity = Vector2.zero;
        if (fluke.Renderer != null) {
            fluke.Renderer.enabled = false;
        }

        if (fluke.Splat != null) {
            fluke.Splat.SetActive(true);
        }

        var info = fluke.Info;
        var audible = Audible(position);
        if (info.Behaviour == CharmTemplates.FlukeBehaviour.Dung) {
            fluke.Antic?.Stop();
            if (fluke.DungCloud != null) {
                fluke.DungCloud.SetActive(true);
                for (var i = 0; i < fluke.DungCloud.transform.childCount; i++) {
                    fluke.DungCloud.transform.GetChild(i).gameObject.SetActive(true);
                }
            }

            if (audible && info.BlowClip != null && info.BlowAudioPlayer != null) {
                var source = info.BlowAudioPlayer.Spawn(position, Quaternion.identity).GetComponent<AudioSource>();
                source?.PlayOneShot(info.BlowClip);
            }
        } else if (audible && info.AudioPlayer != null) {
            info.SplatSounds.SpawnAndPlayOneShot(info.AudioPlayer, position);
        }
    }

    // =====================================================================================================
    // Efectos sueltos del caballero (dash sombra, aterrizaje fuerte...)
    // =====================================================================================================

    private sealed class Puff {
        public GameObject Object;
        public Transform Transform;
        public tk2dSpriteAnimator[] Animators;
        public MeshRenderer[] Renderers;
        public ParticleSystem[] Particles;
        public SpriteRenderer Sprite;
        public AudioSource Audio;
        public CharmTemplates.PuffInfo Info;
        public float StartScale;
        public float Age;
        public float Life;
        public bool Active;
    }

    /// <summary>Un efecto suelto donde lo sacó el caballero del dueño, con su giro y escala.</summary>
    public void SpawnPuff(int index, Vector2 position, float angle, float scaleX) {
        if (index >= _templates.Puffs.Count) {
            return;
        }

        var info = _templates.Puffs[index];
        Puff puff = null;
        Puff oldest = null;
        for (var i = index * PuffsPerKind; i < (index + 1) * PuffsPerKind; i++) {
            var candidate = _puffs[i];
            if (candidate == null) {
                var copy = Object.Instantiate(info.Template, _parking, false);
                copy.name = info.Template.name;
                copy.SetActive(false);
                candidate = _puffs[i] = new Puff {
                    Object = copy,
                    Transform = copy.transform,
                    Animators = copy.GetComponentsInChildren<tk2dSpriteAnimator>(true),
                    Renderers = copy.GetComponentsInChildren<MeshRenderer>(true),
                    Particles = copy.GetComponentsInChildren<ParticleSystem>(true),
                    Sprite = copy.GetComponent<SpriteRenderer>(),
                    Audio = copy.GetComponent<AudioSource>(),
                    Info = info
                };
                foreach (var animator in candidate.Animators) {
                    animator.AnimationCompleted = RemoteEffects.HideWhenDone;
                }
            }

            if (!candidate.Active) {
                puff = candidate;
                break;
            }

            if (oldest == null || candidate.Age > oldest.Age) {
                oldest = candidate;
            }
        }

        puff ??= oldest;
        if (puff == null) {
            return;
        }

        var baseScale = info.Template.transform.localScale;
        puff.Object.SetActive(false);
        var z = float.IsNaN(info.Z) ? info.Template.transform.position.z : info.Z;
        puff.Transform.position = new Vector3(position.x, position.y, z);
        puff.Transform.localEulerAngles = new Vector3(0f, 0f, angle);
        puff.Transform.localScale = new Vector3(scaleX, baseScale.y, baseScale.z);
        puff.Age = 0f;
        puff.Life = info.Life;
        puff.StartScale = scaleX;
        if (puff.Sprite != null) {
            puff.Sprite.color = Color.white;
        }

        puff.Active = true;
        puff.Object.SetActive(true);
        if (info.ByGround) {
            ShowGround(puff, position);
        }

        foreach (var renderer in puff.Renderers) {
            renderer.enabled = true;
        }

        foreach (var animator in puff.Animators) {
            var clip = animator.DefaultClip;
            if (clip != null) {
                animator.PlayFromFrame(clip, 0);
            }
        }

        foreach (var particles in puff.Particles) {
            particles.Clear();
            particles.Play();
        }
    }

    /// <summary>
    /// Polvo del salto o del aterrizaje suave: como <see cref="SoftLandEffect"/> y <see cref="JumpEffects"/>, solo el
    /// hijo del suelo de la sala (el mismo en los dos PCs: están en la misma sala), y el sonido del aterrizaje.
    /// </summary>
    private void ShowGround(Puff puff, Vector2 position) {
        var info = puff.Info;
        var transform = puff.Transform;
        for (var i = 0; i < transform.childCount; i++) {
            transform.GetChild(i).gameObject.SetActive(false);
        }

        var ground = PlayerData.instance != null ? PlayerData.instance.environmentType : 0;
        var shown = ground switch {
            1 => info.Grass,
            2 => info.Bone,
            // Agua poco profunda (salpica gotas sueltas) y sin efecto
            3 or 5 => -1,
            6 => info.Splash,
            _ => info.Dust
        };
        SetChildActive(transform, shown);
        if (shown >= 0 && shown != info.Splash) {
            SetChildActive(transform, info.JumpPuff);
            SetChildActive(transform, info.Trail);
        }

        var clip = ground is 3 or 6 ? info.WetClip : ground == 5 ? null : info.LandClip;
        if (puff.Audio != null && clip != null && Audible(position)) {
            puff.Audio.PlayOneShot(clip);
        }
    }

    private static void SetChildActive(Transform parent, int index) {
        if (index >= 0 && index < parent.childCount) {
            parent.GetChild(index).gameObject.SetActive(true);
        }
    }

    private static void UpdatePuff(Puff puff, float deltaTime) {
        if (puff.Object == null) {
            puff.Active = false;
            return;
        }

        puff.Age += deltaTime;
        if (puff.Age >= puff.Life) {
            puff.Active = false;
            puff.Object.SetActive(false);
            return;
        }

        // Crece y se desvanece como su FSM "Play Effect" (ScaleTo y EaseColor, easeOutQuad)
        var info = puff.Info;
        if (info.GrowTime > 0f) {
            var t = Mathf.Clamp01(puff.Age / info.GrowTime);
            var eased = 1f - (1f - t) * (1f - t);
            var scale = Mathf.Lerp(puff.StartScale, info.GrowTo, eased);
            puff.Transform.localScale = new Vector3(scale, scale, scale);
            if (puff.Sprite != null) {
                puff.Sprite.color = new Color(1f, 1f, 1f, 1f - eased);
            }
        }
    }

    // =====================================================================================================
    // Onda de los gritos
    // =====================================================================================================

    /// <summary>
    /// La onda de un grito (Howling Wraiths, Abyss Shriek), como la crea el caballero (sin sus ondas, solo las líneas).
    /// Solo si el avatar está cerca de la cámara: su FSM también hace temblar la cámara.
    /// </summary>
    public void StartRoarWave(Vector3 position) {
        EndRoarWave("END");
        if (_templates.RoarWave == null || !NearCamera(position)) {
            return;
        }

        // Como CreateObject en el caballero (se destruye sola al acabar); "No Waves" como lo pone Spell Control
        _roarWave = Object.Instantiate(_templates.RoarWave, position, Quaternion.identity);
        _roarWaveAge = 0f;
        _roarEmitter = FSMUtility.LocateFSM(_roarWave, "emitter");
        var noWaves = _roarEmitter != null ? _roarEmitter.FsmVariables.FindFsmBool("No Waves") : null;
        if (noWaves != null) {
            noWaves.Value = true;
        }
    }

    /// <summary>Aviso a la onda del grito (END al terminar o cancelarse).</summary>
    public void EndRoarWave(string eventName) {
        if (_roarWave == null || _roarEmitter == null) {
            _roarWave = null;
            _roarEmitter = null;
            return;
        }

        _roarEmitter.SendEvent(eventName);
        if (eventName == "END") {
            // Se destruye sola al acabar (su FSM); ya no es la de este grito
            _roarWave = null;
            _roarEmitter = null;
        }
    }

    /// <summary>Si algo está a la vista de la cámara local (con margen): para los temblores y la onda de los gritos.</summary>
    public static bool NearCamera(Vector3 position) {
        var cameras = GameCameras.instance;
        var camera = cameras != null && cameras.mainCamera != null ? cameras.mainCamera.transform.position : Vector3.zero;
        return cameras != null && Mathf.Abs(position.x - camera.x) < 16f && Mathf.Abs(position.y - camera.y) < 10f;
    }

    // =====================================================================================================
    // Común
    // =====================================================================================================

    public void Update(float deltaTime) {
        foreach (var beam in _beams) {
            if (beam is { Active: true }) {
                UpdateBeam(beam, deltaTime);
            }
        }

        foreach (var cloud in _clouds) {
            if (cloud is { Active: true }) {
                UpdateCloud(cloud, deltaTime);
            }
        }

        foreach (var fluke in _flukes) {
            if (fluke is { Active: true }) {
                UpdateFluke(fluke, deltaTime);
            }
        }

        foreach (var puff in _puffs) {
            if (puff is { Active: true }) {
                UpdatePuff(puff, deltaTime);
            }
        }

        if (_roarWave != null) {
            _roarWaveAge += deltaTime;
            if (_roarWaveAge >= RoarWaveMax) {
                EndRoarWave("END");
            }
        }
    }

    /// <summary>Lo apaga todo (el avatar se fue de la sala).</summary>
    public void Hide() {
        EndRoarWave("END");
        foreach (var beam in _beams) {
            if (beam?.Object != null) {
                beam.Active = false;
                beam.Object.SetActive(false);
            }
        }

        foreach (var cloud in _clouds) {
            if (cloud?.Object != null) {
                cloud.Active = false;
                cloud.Object.SetActive(false);
            }
        }

        foreach (var fluke in _flukes) {
            if (fluke?.Object != null) {
                fluke.Active = false;
                fluke.Object.SetActive(false);
            }
        }

        foreach (var puff in _puffs) {
            if (puff?.Object != null) {
                puff.Active = false;
                puff.Object.SetActive(false);
            }
        }
    }

    public void Destroy() {
        Hide();
        foreach (var beam in _beams) {
            if (beam?.Object != null) {
                Object.Destroy(beam.Object);
            }
        }

        foreach (var cloud in _clouds) {
            if (cloud?.Object != null) {
                Object.Destroy(cloud.Object);
            }
        }

        foreach (var fluke in _flukes) {
            if (fluke?.Object != null) {
                Object.Destroy(fluke.Object);
            }
        }

        foreach (var puff in _puffs) {
            if (puff?.Object != null) {
                Object.Destroy(puff.Object);
            }
        }
    }

    /// <summary>Copias encendidas ahora, con dónde están y si se dibujan (modo de prueba).</summary>
    public string Describe() {
        var text = new System.Text.StringBuilder();
        foreach (var beam in _beams) {
            if (beam is { Active: true }) {
                text.Append($" rayo {beam.Object.name} en {beam.Transform.position} v={beam.Velocity} " +
                            $"dibuja={beam.Renderer?.enabled} clip={beam.Animator?.CurrentClip?.name} edad={beam.Age:0.00};");
            }
        }

        foreach (var cloud in _clouds) {
            if (cloud is { Active: true }) {
                text.Append($" nube {cloud.Object.name} en {cloud.Transform.position} edad={cloud.Age:0.0};");
            }
        }

        foreach (var fluke in _flukes) {
            if (fluke is { Active: true }) {
                text.Append($" mosca {fluke.Object.name} en {fluke.Transform.position} v={fluke.Velocity} " +
                            $"dibuja={fluke.Renderer?.enabled} clip={fluke.Animator?.CurrentClip?.name} reventada={fluke.Burst};");
            }
        }

        if (_roarWave != null) {
            text.Append($" onda en {_roarWave.transform.position} estado={_roarEmitter?.ActiveStateName};");
        }

        foreach (var puff in _puffs) {
            if (puff is { Active: true }) {
                text.Append($" efecto {puff.Object.name} en {puff.Transform.position} escala={puff.Transform.localScale.x:0.00} " +
                            $"edad={puff.Age:0.00}/{puff.Life:0.00} crece={puff.Info.GrowTime:0.00};");
            }
        }

        return text.ToString();
    }

    private bool Audible(Vector2 position) =>
        _hero != null && ((Vector2) _hero.transform.position - position).sqrMagnitude <
        RemoteEffects.AudibleDistance * RemoteEffects.AudibleDistance;
}
