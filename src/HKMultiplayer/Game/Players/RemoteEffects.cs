using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using HKMultiplayer.Util;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Efectos de un compañero: una copia "solo visual" de las carpetas de efectos del caballero
/// (<see cref="HeroEffectTemplates"/>) que va pegada a su avatar, y lo que hace cada evento suyo con ella.
/// <para>
/// Los estados de sus FSM de acciones (hechizos, artes del aguijón, Crystal Heart, curación, aguijón onírico) se
/// reproducen leyendo las acciones del mismo estado en el caballero local y haciéndolas sobre la copia: lo que se activa,
/// se anima, emite partículas o suena, y los avisos a las FSM visuales de sus efectos. Lo que no se ve (velocidad,
/// cámara, avisos al caballero) no se hace. Así cada hechizo sale igual que en el juego sin reescribirlo efecto a efecto.
/// Lo que hace el código del caballero y no una FSM (tajos, dash, doble salto, salto de pared, carga de las artes,
/// golpes recibidos) tiene aquí su propio método.
/// </para>
/// <para>
/// La copia dura toda la partida (crearla cuesta): al cambiar de sala se aparca apagada y se vuelve a pegar al avatar
/// nuevo. Nada de ella daña, choca ni gasta nada: solo se ve y se oye.
/// </para>
/// </summary>
internal sealed class RemoteEffects {
    /// <summary>Más lejos del caballero local que esto, sus sonidos no se oyen (el juego los oiría en toda la sala).</summary>
    internal const float AudibleDistance = 30f;

    /// <summary>Bolas de fuego a la vez de cada nivel.</summary>
    private const int FireballsPerLevel = 3;

    /// <summary>Tiempo que vuela una bola de fuego antes de deshacerse (su FSM espera esto en "Idle").</summary>
    private const float FireballLife = 0.45f;

    /// <summary>Capas contra las que choca la bola de fuego (las de su FSM: terreno).</summary>
    internal const int FireballWalls = (1 << 8) | (1 << 25);

    /// <summary>Los tajos se apagan al terminar su animación (como NailSlash).</summary>
    internal static readonly Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip> HideWhenDone = (animator, _) => {
        var renderer = animator.GetComponent<MeshRenderer>();
        if (renderer != null) {
            renderer.enabled = false;
        }
    };

    /// <summary>El estallido al lanzar una bola de fuego se apaga al terminar su animación.</summary>
    private static readonly Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip> DeactivateWhenDone =
        (animator, _) => animator.gameObject.SetActive(false);

    private static readonly FieldInfo ParticleScaleParent =
        typeof(SetParticleScale).GetField("parent", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly HeroEffectTemplates _templates;
    private readonly Transform _parking;

    /// <summary>Raíz de la copia: hace de "caballero" para sus FSM (escala, sonidos) y tiene su propio sonido.</summary>
    private readonly GameObject _root;

    private readonly AudioSource _audio;

    /// <summary>
    /// Lo que sale del avatar y vuela por su cuenta (bolas de fuego, rayos, nubes, moscas): no cuelga del avatar, pero
    /// lleva la skin de su dueño como lo demás (<see cref="RemoteAvatar.LooseEffects"/>).
    /// </summary>
    private readonly GameObject _loose;
    private readonly Transform[][] _nodes;
    private readonly bool[][] _activeAtStart;
    private readonly Transform[][] _parentAtStart;
    private readonly List<PlayMakerFSM> _fsms = new();
    private readonly Fireball[] _fireballs = new Fireball[FireballsPerLevel * 2];
    private readonly GameObject[] _blasts = new GameObject[2];
    private readonly CastCopy[] _casts = new CastCopy[2];

    /// <summary>Lo que crean sus amuletos fuera de su caballero (rayos, nubes, moscas, onda del grito); puede faltar.</summary>
    private readonly RemoteCharmEffects _charms;

    private RemoteAvatar _avatar;
    private ParticleSystem _dashParticles;
    private float _dashParticlesUntil;
    private bool _warned;

    /// <summary>Se está reproduciendo un estado de un grito (sus temblores de cámara se hacen si está cerca).</summary>
    private bool _screaming;

    public RemoteEffects(HeroEffectTemplates templates, string playerName, Transform parking) {
        _templates = templates;
        _parking = parking;
        _root = new GameObject($"Efectos de {playerName}");
        _root.SetActive(false);
        _root.transform.SetParent(parking, false);
        _loose = new GameObject($"Efectos sueltos de {playerName}");
        _loose.transform.SetParent(parking, false);

        var hero = templates.Hero;
        _audio = _root.AddComponent<AudioSource>();
        var heroAudio = hero.GetComponent<AudioSource>();
        if (heroAudio != null) {
            _audio.outputAudioMixerGroup = heroAudio.outputAudioMixerGroup;
            _audio.spatialBlend = heroAudio.spatialBlend;
            _audio.volume = heroAudio.volume;
        }

        _audio.playOnAwake = false;

        var folders = templates.FolderCount;
        _nodes = new Transform[folders][];
        _activeAtStart = new bool[folders][];
        _parentAtStart = new Transform[folders][];
        for (var f = 0; f < folders; f++) {
            var template = templates.Template(f);
            if (template == null) {
                continue;
            }

            var copy = Object.Instantiate(template, _root.transform, false);
            copy.name = template.name;
            var nodes = copy.GetComponentsInChildren<Transform>(true);
            _nodes[f] = nodes;
            _activeAtStart[f] = new bool[nodes.Length];
            _parentAtStart[f] = new Transform[nodes.Length];
            for (var i = 0; i < nodes.Length; i++) {
                _activeAtStart[f][i] = nodes[i].gameObject.activeSelf;
                _parentAtStart[f][i] = nodes[i].parent;
            }
        }

        // Lo que en el caballero apuntaba a él (su escala para mirar a un lado, dónde sonar) apunta a la copia
        foreach (var fsm in _root.GetComponentsInChildren<PlayMakerFSM>(true)) {
            foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
                if (variable.Value == hero.gameObject) {
                    variable.Value = _root;
                }
            }
        }

        if (ParticleScaleParent != null) {
            foreach (var scale in _root.GetComponentsInChildren<SetParticleScale>(true)) {
                if (ParticleScaleParent.GetValue(scale) as GameObject == hero.gameObject) {
                    ParticleScaleParent.SetValue(scale, _root);
                }
            }
        }

        foreach (var slash in templates.Slashes) {
            var animator = Component<tk2dSpriteAnimator>(Resolve(slash.HeroObject));
            if (animator != null) {
                animator.AnimationCompleted = HideWhenDone;
            }
        }

        if (templates.Charms != null) {
            _charms = new RemoteCharmEffects(templates.Charms, hero, _loose.transform);
        }

        if (templates.Companions != null) {
            Companions = new RemoteCompanions(templates.Companions, _loose.transform);
        }

        foreach (var art in templates.NailArts) {
            var copy = Resolve(art.HeroObject);
            var animator = Component<tk2dSpriteAnimator>(copy);
            if (animator == null) {
                continue;
            }

            var effect = copy.AddComponent<NailArtEffect>();
            effect.Clip = animator.GetClipByName(art.Clip);
            effect.EndClip = art.EndClip != null ? animator.GetClipByName(art.EndClip) : null;
            effect.Position = art.Position;
            effect.Scale = art.Scale;
        }
    }

    /// <summary>Sus acompañantes (crías, tejedoras, Grimmchild); puede faltar.</summary>
    public RemoteCompanions Companions { get; }

    /// <summary>Lo encendido de sus amuletos (modo de prueba).</summary>
    public string DescribeCharms() =>
        (_charms?.Describe() ?? " sin efectos de amuletos") + $" acompañantes={Companions?.ShownCount ?? 0}";

    /// <summary>
    /// Algún objeto de la copia se perdió (una FSM lo sacó al mundo y la sala se descargó): hay que crearla de nuevo.
    /// </summary>
    public bool Broken { get; private set; }

    // =====================================================================================================
    // Pegarse al avatar
    // =====================================================================================================

    /// <summary>Pega la copia al avatar (se llama cada frame; solo hace algo si el avatar es otro).</summary>
    public void AttachTo(RemoteAvatar avatar) {
        if (_avatar == avatar) {
            return;
        }

        if (_avatar != null) {
            Detach();
        }

        _avatar = avatar;
        avatar.LooseEffects = _loose.transform;
        var root = _root.transform;
        root.SetParent(avatar.SpriteTransform, false);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;
        _root.SetActive(true);
    }

    /// <summary>
    /// Separa la copia del avatar (que se va a destruir) y la aparca apagada, con todo como al crearla. Antes de
    /// destruir el avatar: si no, la copia se destruiría con él.
    /// </summary>
    public void Detach() {
        if (_avatar == null) {
            return;
        }

        _avatar = null;
        _audio.Stop();
        HideFireballs();
        _charms?.Hide();
        Companions?.Hide();
        for (var f = 0; f < _nodes.Length; f++) {
            var nodes = _nodes[f];
            if (nodes == null) {
                continue;
            }

            for (var i = 0; i < nodes.Length; i++) {
                var node = nodes[i];
                if (node == null || node.parent != _parentAtStart[f][i]) {
                    Broken = true;
                    continue;
                }

                if (node.gameObject.activeSelf != _activeAtStart[f][i]) {
                    node.gameObject.SetActive(_activeAtStart[f][i]);
                }
            }
        }

        _root.SetActive(false);
        _root.transform.SetParent(_parking, false);
    }

    public void Destroy() {
        HideFireballs();
        _charms?.Destroy();
        Companions?.Destroy();
        foreach (var fireball in _fireballs) {
            if (fireball?.Object != null) {
                Object.Destroy(fireball.Object);
            }
        }

        foreach (var blast in _blasts) {
            if (blast != null) {
                Object.Destroy(blast);
            }
        }

        foreach (var cast in _casts) {
            if (cast?.Object != null) {
                Object.Destroy(cast.Object);
            }
        }

        if (_root != null) {
            Object.Destroy(_root);
        }

        if (_loose != null) {
            Object.Destroy(_loose);
        }
    }

    // =====================================================================================================
    // Eventos
    // =====================================================================================================

    /// <summary>Reproduce un evento sobre el avatar. false si no hay avatar al que pegarlo (no se pudo reproducir).</summary>
    public bool Play(in EffectEvent effect) {
        if (_avatar == null) {
            return false;
        }

        try {
            switch (effect.Kind) {
                case EffectKind.FsmState:
                    PlayState(effect.A, effect.B, effect.Charms);
                    break;
                case EffectKind.Slash:
                    PlaySlash(effect.A, effect.Charms);
                    break;
                case EffectKind.Dash:
                    PlayDash(effect);
                    break;
                case EffectKind.DoubleJump:
                    PlayDoubleJump();
                    break;
                case EffectKind.WallJump:
                    PlayWallJump();
                    break;
                case EffectKind.NailCharge:
                    PlayNailCharge(effect.A);
                    break;
                case EffectKind.Hit:
                    PlayHit();
                    break;
                case EffectKind.Beam:
                    _charms?.SpawnBeam(effect.A, _avatar.Position, effect.Angle, effect.Scale);
                    break;
                case EffectKind.Cloud:
                    _charms?.SpawnCloud(effect.A, new Vector3(effect.X, effect.Y, 0f), effect.Scale);
                    break;
                case EffectKind.Puff:
                    _charms?.SpawnPuff(effect.A, new Vector2(effect.X, effect.Y), effect.Angle, effect.Scale);
                    break;
                case EffectKind.Fluke:
                    _charms?.SpawnFluke(effect.A, new Vector2(effect.X, effect.Y), new Vector2(effect.VX, effect.VY),
                        effect.Angle, effect.Scale, effect.Spin);
                    break;
            }
        } catch (Exception e) {
            WarnOnce($"Efecto {effect.Kind} del compañero no reproducido: {e}");
        }

        return true;
    }

    /// <summary>Lo que se mueve o se apaga solo con el tiempo: bolas de fuego y partículas del dash.</summary>
    public void Update(float deltaTime) {
        if (_dashParticles != null && Time.time >= _dashParticlesUntil) {
            var emission = _dashParticles.emission;
            emission.enabled = false;
            _dashParticles = null;
        }

        foreach (var fireball in _fireballs) {
            if (fireball is { Active: true }) {
                UpdateFireball(fireball, deltaTime);
            }
        }

        foreach (var cast in _casts) {
            if (cast?.Object != null && cast.Object.activeSelf && Time.time >= cast.Until) {
                cast.Object.SetActive(false);
            }
        }

        _charms?.Update(deltaTime);
    }

    // =====================================================================================================
    // Estados de las FSM de acciones
    // =====================================================================================================

    private void PlayState(byte fsmIndex, ushort stateIndex, byte charms) {
        var fsms = _templates.HeroFsms;
        if (fsmIndex >= fsms.Length || fsms[fsmIndex] == null) {
            return;
        }

        var heroFsm = fsms[fsmIndex];
        var states = heroFsm.FsmStates;
        if (stateIndex >= states.Length) {
            return;
        }

        var state = states[stateIndex];
        _screaming = state.Name.StartsWith("Scream", StringComparison.Ordinal);
        foreach (var action in state.Actions) {
            if (!action.Enabled) {
                continue;
            }

            try {
                PlayAction(heroFsm.Fsm, action, charms);
            } catch (Exception e) {
                WarnOnce($"Acción {state.Name}/{action.GetType().Name} del compañero no reproducida: {e.Message}");
            }
        }

        // Al curarse, el caballero destella (no lo hace una acción de la FSM)
        if (state.Name is "Focus Heal" or "Focus Heal 2") {
            _avatar.FlashHeal();
        }
    }

    /// <summary>
    /// Hace sobre la copia lo que se ve y se oye de una acción del caballero local. Lo que va al propio caballero
    /// (animarlo, mostrarlo u ocultarlo) ya llega en su estado; solo su sonido se hace aquí.
    /// </summary>
    private void PlayAction(Fsm fsm, FsmStateAction action, byte charms) {
        switch (action) {
            case ActivateGameObject a:
                SetActive(Copy(fsm, a.gameObject), a.activate.Value, a.recursive.Value);
                break;
            case SetMeshRenderer a:
                var renderer = Component<MeshRenderer>(Copy(fsm, a.gameObject));
                if (renderer != null) {
                    renderer.enabled = a.active.Value;
                }

                break;
            case Tk2dPlayAnimation a:
                PlayClip(Copy(fsm, a.gameObject), a.clipName.Value);
                break;
            case Tk2dPlayAnimationWithEvents a:
                PlayClip(Copy(fsm, a.gameObject), a.clipName.Value);
                break;
            case Tk2dPlayFrame a:
                Component<tk2dSpriteAnimator>(Copy(fsm, a.gameObject))?.PlayFromFrame(a.frame.Value);
                break;
            case Tk2dStopAnimation a:
                Component<tk2dSpriteAnimator>(Copy(fsm, a.gameObject))?.Stop();
                break;
            case PlayParticleEmitter a:
                var particles = Component<ParticleSystem>(Copy(fsm, a.gameObject));
                if (particles != null) {
                    if (a.emit.Value > 0) {
                        particles.Emit(a.emit.Value);
                    } else {
                        particles.Play();
                    }
                }

                break;
            case StopParticleEmitter a:
                Component<ParticleSystem>(Copy(fsm, a.gameObject))?.Stop();
                break;
            case SetParticleEmissionRate a:
                var rated = Component<ParticleSystem>(Copy(fsm, a.gameObject));
                if (rated != null) {
                    var emission = rated.emission;
                    emission.rateOverTime = a.emissionRate.Value;
                }

                break;
            case SetParticleEmission a:
                var emitter = Component<ParticleSystem>(Copy(fsm, a.gameObject));
                if (emitter != null) {
                    var emission = emitter.emission;
                    emission.enabled = a.emission.Value;
                }

                break;
            case SendEventByName a:
                SendEvent(fsm, a.eventTarget, a.sendEvent.Value);
                break;
            case SendEventByNameV2 a:
                SendEvent(fsm, a.eventTarget, a.sendEvent.Value);
                break;
            case AudioPlay a:
                PlayAudio(Target(fsm, a.gameObject), a.oneShotClip.Value as AudioClip, a.volume.Value);
                break;
            case AudioPlaySimple a:
                PlayAudio(Target(fsm, a.gameObject), a.oneShotClip.Value as AudioClip, a.volume.Value);
                break;
            case AudioPlayRandom a:
                if (a.audioClips is { Length: > 0 } && Audible()) {
                    var clip = a.audioClips[Random.Range(0, a.audioClips.Length)];
                    if (clip != null) {
                        _audio.PlayOneShot(clip);
                    }
                }

                break;
            case AudioStop a:
                AudioOf(Target(fsm, a.gameObject))?.Stop();
                break;
            case FadeAudio a:
                // Solo bajan el sonido hasta apagarlo: se apaga sin más
                if (a.endVolume.Value <= 0f) {
                    AudioOf(Target(fsm, a.gameObject))?.Stop();
                }

                break;
            case AudioPlayerOneShotSingle a:
                PlayActorSound(a.audioPlayer.Value, a.audioClip.Value as AudioClip, Resolve(a.spawnPoint.Value),
                    Random.Range(a.pitchMin.Value, a.pitchMax.Value), a.volume.Value);
                break;
            case SpawnObjectFromGlobalPool a:
                Spawn(a.gameObject.Value, a.position.Value, charms);
                break;
            case CreateObject a:
                Spawn(a.gameObject.Value, a.position.Value, charms);
                break;
            case FlingObjectsFromGlobalPool a:
                Fling(a);
                break;
        }
    }

    private void SendEvent(Fsm fsm, FsmEventTarget target, string eventName) {
        if (target.target is not (FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM)) {
            return;
        }

        // La onda del grito que creó un estado anterior: en el compañero, la suya
        if (HeroEffectTemplates.IsRoarWave(target.gameObject)) {
            _charms?.EndRoarWave(eventName);
            return;
        }

        // Los temblores de cámara de un grito, solo si el compañero está a la vista
        if (_screaming && IsCamera(target.gameObject)) {
            if (RemoteCharmEffects.NearCamera(_avatar.Position)) {
                GameCameras.instance.cameraShakeFSM?.SendEvent(eventName);
            }

            return;
        }

        var copy = Copy(fsm, target.gameObject);
        if (copy == null || !copy.activeInHierarchy) {
            return;
        }

        // El fin del ciclón (su FSM se quitó: lo hace NailArtEffect)
        var nailArt = copy.GetComponent<NailArtEffect>();
        if (nailArt != null) {
            nailArt.End();
            return;
        }

        copy.GetComponents(_fsms);
        var onlyFsm = target.target == FsmEventTarget.EventTarget.GameObjectFSM ? target.fsmName.Value : null;
        foreach (var effectFsm in _fsms) {
            if (onlyFsm == null || effectFsm.FsmName == onlyFsm) {
                effectFsm.SendEvent(eventName);
            }
        }

        _fsms.Clear();
    }

    private void Spawn(GameObject prefab, Vector3 offset, byte charms) {
        if (prefab == null) {
            return;
        }

        if (prefab == _templates.FireballTop1) {
            CastFireball(0, charms);
        } else if (prefab == _templates.FireballTop2) {
            CastFireball(1, charms);
        } else if (_templates.Charms != null && prefab == _templates.Charms.RoarWave) {
            _charms?.StartRoarWave(_root.transform.position + offset);
        } else if (_templates.IsVisualSpawn(prefab)) {
            prefab.Spawn(_root.transform.position + offset);
        }
    }

    /// <summary>Como FlingObjectsFromGlobalPool, solo con objetos visuales (los tajos oscuros del golpe recibido).</summary>
    private void Fling(FlingObjectsFromGlobalPool action) {
        var prefab = action.gameObject.Value;
        if (!_templates.IsVisualSpawn(prefab)) {
            return;
        }

        var origin = _root.transform.position;
        var count = Random.Range(action.spawnMin.Value, action.spawnMax.Value + 1);
        for (var i = 0; i < count; i++) {
            var spawned = prefab.Spawn(origin, Quaternion.identity);
            var body = spawned.GetComponent<Rigidbody2D>();
            if (body == null) {
                continue;
            }

            var angle = Random.Range(action.angleMin.Value, action.angleMax.Value) * Mathf.Deg2Rad;
            var speed = Random.Range(action.speedMin.Value, action.speedMax.Value);
            body.linearVelocity = new Vector2(Mathf.Cos(angle) * speed, Mathf.Sin(angle) * speed);
        }
    }

    // =====================================================================================================
    // Lo que hace el código del caballero
    // =====================================================================================================

    /// <summary>Tajo del aguijón, como NailSlash.StartSlash: tamaño y animación según Mark of Pride, Longnail y furia.</summary>
    private void PlaySlash(int index, byte charms) {
        if (index >= _templates.Slashes.Length) {
            return;
        }

        var info = _templates.Slashes[index];
        var copy = Resolve(info.HeroObject);
        var animator = Component<tk2dSpriteAnimator>(copy);
        if (animator == null) {
            return;
        }

        var mantis = (charms & EffectCharms.MarkOfPride) != 0;
        var longnail = (charms & EffectCharms.Longnail) != 0;
        var scale = mantis && longnail ? 1.4f : mantis ? 1.25f : longnail ? 1.15f : 1f;
        copy.transform.localScale = info.Scale * scale;
        var clip = (charms & EffectCharms.Fury) != 0 && info.FuryClip != null ? info.FuryClip :
            mantis && info.MantisClip != null ? info.MantisClip : info.Clip;
        if (clip != null) {
            animator.PlayFromFrame(clip, 0);
        }

        var renderer = copy.GetComponent<MeshRenderer>();
        if (renderer != null) {
            renderer.enabled = true;
        }

        if (Audible()) {
            copy.GetComponent<AudioSource>()?.Play();
        }
    }

    /// <summary>Dash y Shade Dash, como HeroController.HeroDash.</summary>
    private void PlayDash(in EffectEvent effect) {
        var hero = _templates.Hero;
        var shadow = (effect.A & EffectEvent.DashShadow) != 0;
        if (shadow) {
            if (Audible()) {
                _audio.PlayOneShot((effect.A & EffectEvent.DashSharp) != 0 ? hero.sharpShadowClip : hero.shadowDashClip);
            }

            Restart(Resolve((effect.A & EffectEvent.DashDown) != 0 ? hero.shadowdashDownBurstPrefab : hero.shadowdashBurstPrefab));
            Restart(Resolve(hero.shadowRingPrefab));
            EmitFor(Resolve(hero.shadowdashParticlesPrefab), hero.SHADOW_DASH_TIME);
        } else {
            // La ráfaga va donde la puso el caballero (delante, o girada al hacer el dash hacia abajo)
            var burst = Resolve(hero.dashBurst != null ? hero.dashBurst.gameObject : null);
            if (burst != null) {
                var burstTransform = burst.transform;
                burstTransform.localPosition = new Vector3(effect.X, effect.Y, burstTransform.localPosition.z);
                burstTransform.localEulerAngles = new Vector3(0f, 0f, effect.Angle);
                SendEventTo(burst, "PLAY");
            }

            EmitFor(Resolve(hero.dashParticlesPrefab), hero.DASH_TIME);
        }

        PlaySource(_templates.HeroAudio?.dash);
    }

    private void PlayDoubleJump() {
        var hero = _templates.Hero;
        var wings = Resolve(hero.dJumpWingsPrefab);
        Restart(wings);
        Component<tk2dSpriteAnimator>(wings)?.PlayFromFrame(0);
        Restart(Resolve(hero.dJumpFlashPrefab));
        Component<ParticleSystem>(Resolve(hero.dJumpFeathers != null ? hero.dJumpFeathers.gameObject : null))?.Play();
        if (Audible() && hero.doubleJumpClip != null) {
            _audio.PlayOneShot(hero.doubleJumpClip);
        }
    }

    private void PlayWallJump() {
        Restart(Resolve(_templates.Hero.wallPuffPrefab));
        PlaySource(_templates.HeroAudio?.walljump);
    }

    /// <summary>Carga de las artes del aguijón: 1 cargando, 2 cargada, 0 terminada.</summary>
    private void PlayNailCharge(byte stage) {
        var hero = _templates.Hero;
        var charge = Resolve(hero.artChargeEffect);
        var charged = Resolve(hero.artChargedEffect);
        var chargeSound = Resolve(_templates.HeroAudio?.nailArtCharge != null ? _templates.HeroAudio.nailArtCharge.gameObject : null);
        var readySound = Resolve(_templates.HeroAudio?.nailArtReady != null ? _templates.HeroAudio.nailArtReady.gameObject : null);
        switch (stage) {
            case 1:
                SetActive(charged, false, false);
                SetActive(charge, true, false);
                PlayAudio(chargeSound, null, 1f);
                break;
            case 2:
                SetActive(charge, false, false);
                SetActive(charged, true, false);
                Component<tk2dSpriteAnimator>(charged)?.PlayFromFrame(0);
                Restart(Resolve(hero.artChargedFlash));
                AudioOf(chargeSound)?.Stop();
                PlayAudio(readySound, null, 1f);
                if (Audible() && hero.nailArtChargeComplete != null) {
                    _audio.PlayOneShot(hero.nailArtChargeComplete);
                }

                break;
            default:
                SetActive(charge, false, false);
                SetActive(charged, false, false);
                AudioOf(chargeSound)?.Stop();
                AudioOf(readySound)?.Stop();
                break;
        }
    }

    /// <summary>Golpe recibido: partículas, sonido y destello del avatar.</summary>
    private void PlayHit() {
        foreach (var particles in _templates.HitParticles) {
            Component<ParticleSystem>(Resolve(particles))?.Play();
        }

        PlaySource(_templates.HeroAudio?.takeHit);
        _avatar.FlashHit();
    }

    // =====================================================================================================
    // Bolas de fuego
    // =====================================================================================================

    private sealed class Fireball {
        public GameObject Object;
        public Transform Transform;
        public MeshRenderer Renderer;
        public tk2dSpriteAnimator Animator;
        public tk2dSpriteAnimationClip FlyClip;
        public tk2dSpriteAnimationClip EndClip;
        public GameObject WallImpact;
        public bool Active;
        public bool Ending;
        public float Velocity;
        public float Life;
    }

    /// <summary>Lo que sale junto al caballero al lanzar una bola de fuego (estallido, partículas, luz).</summary>
    private sealed class CastCopy {
        public GameObject Object;
        public Transform[] Nodes;
        public bool[] ActiveAtStart;
        public tk2dSpriteAnimator[] Animators;
        public float Until;
    }

    /// <summary>Tiempo que se deja encendido lo que sale al lanzarla (sus animaciones acaban antes).</summary>
    private const float CastSeconds = 1.5f;

    /// <summary>
    /// Vengeful Spirit (nivel 0) o Shade Soul (1): el estallido al lanzarla, el sonido y la bola, que vuela recta hasta
    /// chocar con una pared o deshacerse. Con Flukenest, el estallido y su sonido sin la bola: sus moscas llegan
    /// aparte, cada una como salió en el PC del dueño (<see cref="RemoteCharmEffects"/>).
    /// </summary>
    private void CastFireball(int level, byte charms) {
        var info = level == 0 ? _templates.Fireball1 : _templates.Fireball2;
        if (info == null) {
            return;
        }

        var flukes = (charms & EffectCharms.Flukenest) != 0;
        var direction = _avatar.FacingRight ? 1f : -1f;
        var origin = _avatar.Position;
        var sound = flukes ? info.FlukeSound : info.Sound;
        if (Audible() && sound != null) {
            _audio.PlayOneShot(sound);
        }

        PlayCast(level, info, origin, direction);
        if (info.Blast != null) {
            var blast = _blasts[level];
            if (blast == null) {
                blast = _blasts[level] = Object.Instantiate(info.Blast, _loose.transform, false);
                blast.SetActive(false);
                var blastAnimator = blast.GetComponent<tk2dSpriteAnimator>();
                if (blastAnimator != null) {
                    blastAnimator.AnimationCompleted = DeactivateWhenDone;
                }
            }

            blast.transform.position = origin + new Vector3(direction * info.BlastOffset.x, info.BlastOffset.y, -0.001f);
            blast.transform.localScale = new Vector3(direction * Mathf.Abs(info.BlastScale.x), info.BlastScale.y, info.BlastScale.z);
            Restart(blast);
            blast.GetComponent<tk2dSpriteAnimator>()?.PlayFromFrame(0);
        }

        var fireball = flukes ? null : FreeFireball(level, info);
        if (fireball == null) {
            return;
        }

        // Como su FSM: tamaño 1 (1,3 × 1,6 con Shaman Stone), girada si va a la izquierda
        var shaman = (charms & EffectCharms.ShamanStone) != 0;
        var baseScale = info.Projectile.transform.localScale;
        fireball.Transform.position = origin + new Vector3(direction * info.Offset.x, info.Offset.y, -0.002f);
        fireball.Transform.localScale = new Vector3(direction * Mathf.Abs(baseScale.x) * (shaman ? 1.3f : 1f),
            baseScale.y * (shaman ? 1.6f : 1f), baseScale.z);
        fireball.Velocity = direction * info.Speed;
        fireball.Life = FireballLife;
        fireball.Ending = false;
        fireball.Active = true;
        if (fireball.WallImpact != null) {
            fireball.WallImpact.SetActive(false);
        }

        fireball.Object.SetActive(true);
        if (fireball.Renderer != null) {
            fireball.Renderer.enabled = true;
        }

        if (fireball.Animator != null && fireball.FlyClip != null) {
            fireball.Animator.PlayFromFrame(fireball.FlyClip, 0);
        }
    }

    private void PlayCast(int level, HeroEffectTemplates.FireballInfo info, Vector3 origin, float direction) {
        if (info.Cast == null) {
            return;
        }

        var cast = _casts[level];
        if (cast == null) {
            var copy = Object.Instantiate(info.Cast, _loose.transform, false);
            copy.SetActive(false);
            var nodes = copy.GetComponentsInChildren<Transform>(true);
            cast = _casts[level] = new CastCopy {
                Object = copy,
                Nodes = nodes,
                ActiveAtStart = new bool[nodes.Length],
                Animators = copy.GetComponentsInChildren<tk2dSpriteAnimator>(true)
            };
            for (var i = 0; i < nodes.Length; i++) {
                cast.ActiveAtStart[i] = nodes[i].gameObject.activeSelf;
            }
        }

        // Como al lanzarla: en el caballero y girado hacia donde mira; lo que se apagó solo vuelve a estar
        var scale = info.Cast.transform.localScale;
        cast.Object.transform.position = origin;
        cast.Object.transform.localScale = new Vector3(direction * Mathf.Abs(scale.x), scale.y, scale.z);
        cast.Object.SetActive(false);
        for (var i = 1; i < cast.Nodes.Length; i++) {
            cast.Nodes[i].gameObject.SetActive(cast.ActiveAtStart[i]);
        }

        cast.Object.SetActive(true);
        foreach (var animator in cast.Animators) {
            animator.PlayFromFrame(0);
        }

        cast.Until = Time.time + CastSeconds;
    }

    private Fireball FreeFireball(int level, HeroEffectTemplates.FireballInfo info) {
        for (var i = level * FireballsPerLevel; i < (level + 1) * FireballsPerLevel; i++) {
            var fireball = _fireballs[i];
            if (fireball == null) {
                var copy = Object.Instantiate(info.Projectile, _loose.transform, false);
                copy.SetActive(false);
                var animator = copy.GetComponent<tk2dSpriteAnimator>();
                fireball = _fireballs[i] = new Fireball {
                    Object = copy,
                    Transform = copy.transform,
                    Renderer = copy.GetComponent<MeshRenderer>(),
                    Animator = animator,
                    // La suya (la de Shade Soul es otra); al deshacerse, la de Vengeful Spirit tiene animación
                    FlyClip = animator != null ? animator.DefaultClip : null,
                    EndClip = animator != null && level == 0 ? animator.GetClipByName("Ball End") : null,
                    WallImpact = copy.transform.Find("Wall Impact")?.gameObject
                };
            }

            if (!fireball.Active) {
                return fireball;
            }
        }

        return null;
    }

    private void UpdateFireball(Fireball fireball, float deltaTime) {
        if (fireball.Object == null) {
            fireball.Active = false;
            return;
        }

        fireball.Life -= deltaTime;
        if (fireball.Ending) {
            if (fireball.Life <= 0f) {
                fireball.Active = false;
                fireball.Object.SetActive(false);
            }

            return;
        }

        var position = fireball.Transform.position;
        var step = fireball.Velocity * deltaTime;
        var direction = fireball.Velocity >= 0f ? Vector2.right : Vector2.left;
        var hit = Physics2D.Raycast(position, direction, Mathf.Abs(step) + 0.3f, FireballWalls);
        if (hit.collider != null) {
            // Choca: se apaga y sale su impacto contra la pared
            fireball.Transform.position = new Vector3(hit.point.x - direction.x * 0.3f, position.y, position.z);
            if (fireball.Renderer != null) {
                fireball.Renderer.enabled = false;
            }

            Restart(fireball.WallImpact);
            fireball.Ending = true;
            fireball.Life = 0.6f;
            return;
        }

        fireball.Transform.position = new Vector3(position.x + step, position.y, position.z);
        if (fireball.Life > 0f) {
            return;
        }

        // Se deshace: su animación de final, y se apaga
        fireball.Ending = true;
        fireball.Life = 0.5f;
        if (fireball.Animator != null && fireball.EndClip != null) {
            fireball.Animator.PlayFromFrame(fireball.EndClip, 0);
            fireball.Life = fireball.EndClip.frames.Length / Mathf.Max(1f, fireball.EndClip.fps);
        }
    }

    private void HideFireballs() {
        foreach (var fireball in _fireballs) {
            if (fireball?.Object != null) {
                fireball.Active = false;
                fireball.Object.SetActive(false);
            }
        }

        foreach (var blast in _blasts) {
            if (blast != null) {
                blast.SetActive(false);
            }
        }

        foreach (var cast in _casts) {
            if (cast?.Object != null) {
                cast.Object.SetActive(false);
            }
        }
    }

    // =====================================================================================================
    // Ayudas
    // =====================================================================================================

    /// <summary>La copia de un objeto del caballero local (la raíz si es el propio caballero), o null.</summary>
    private GameObject Resolve(GameObject heroObject) {
        if (heroObject == null) {
            return null;
        }

        if (heroObject == _templates.Hero.gameObject) {
            return _root;
        }

        if (!_templates.TryIndex(heroObject, out var folder, out var index) || _nodes[folder] == null) {
            return null;
        }

        var node = _nodes[folder][index];
        if (node == null) {
            Broken = true;
            return null;
        }

        return node.gameObject;
    }

    /// <summary>Copia del objeto al que apunta una acción del caballero local, incluido él mismo (para sus sonidos).</summary>
    private GameObject Target(Fsm fsm, FsmOwnerDefault owner) => Resolve(fsm.GetOwnerDefaultTarget(owner));

    /// <summary>Como <see cref="Target"/>, pero sin el propio caballero: lo suyo ya llega en su estado.</summary>
    private GameObject Copy(Fsm fsm, FsmOwnerDefault owner) {
        var copy = Target(fsm, owner);
        return copy == _root ? null : copy;
    }

    /// <summary>Si una acción apunta a la cámara (variable "CameraParent" de Spell Control, la de sus temblores).</summary>
    private static bool IsCamera(FsmOwnerDefault owner) =>
        owner is { OwnerOption: OwnerDefaultOption.SpecifyGameObject, GameObject: { UseVariable: true } variable } &&
        variable.Name == "CameraParent";

    private static T Component<T>(GameObject target) where T : Component =>
        target != null ? target.GetComponent<T>() : null;

    private AudioSource AudioOf(GameObject target) => target == _root ? _audio : Component<AudioSource>(target);

    private static void SetActive(GameObject target, bool active, bool recursive) {
        if (target == null) {
            return;
        }

        target.SetActive(active);
        if (recursive) {
            SetChildrenActive(target.transform, active);
        }
    }

    private static void SetChildrenActive(Transform parent, bool active) {
        for (var i = 0; i < parent.childCount; i++) {
            var child = parent.GetChild(i);
            child.gameObject.SetActive(active);
            SetChildrenActive(child, active);
        }
    }

    /// <summary>Lo apaga y enciende: sus FSM y animaciones empiezan de nuevo.</summary>
    private static void Restart(GameObject target) {
        if (target == null) {
            return;
        }

        target.SetActive(false);
        target.SetActive(true);
    }

    private static void PlayClip(GameObject target, string clipName) {
        var animator = Component<tk2dSpriteAnimator>(target);
        var clip = animator != null && !string.IsNullOrEmpty(clipName) ? animator.GetClipByName(clipName) : null;
        if (clip != null) {
            animator.Play(clip);
        }
    }

    private void SendEventTo(GameObject target, string eventName) {
        if (!target.activeInHierarchy) {
            return;
        }

        target.GetComponents(_fsms);
        foreach (var fsm in _fsms) {
            fsm.SendEvent(eventName);
        }

        _fsms.Clear();
    }

    /// <summary>Partículas que salen mientras dura el dash (en el caballero se apagan al terminarlo).</summary>
    private void EmitFor(GameObject target, float seconds) {
        var particles = Component<ParticleSystem>(target);
        if (particles == null) {
            return;
        }

        if (_dashParticles != null && _dashParticles != particles) {
            var previous = _dashParticles.emission;
            previous.enabled = false;
        }

        var emission = particles.emission;
        emission.enabled = true;
        if (!particles.isPlaying) {
            particles.Play();
        }

        _dashParticles = particles;
        _dashParticlesUntil = Time.time + seconds;
    }

    private bool Audible() {
        var hero = _templates.Hero;
        return hero != null && (hero.transform.position - _root.transform.position).sqrMagnitude < AudibleDistance * AudibleDistance;
    }

    /// <summary>Como AudioPlay: con sonido suelto, una vez; si no, el suyo (que puede repetirse hasta pararlo).</summary>
    private void PlayAudio(GameObject target, AudioClip clip, float volume) {
        var source = AudioOf(target);
        if (source == null || !Audible()) {
            return;
        }

        if (clip != null) {
            source.PlayOneShot(clip, volume);
        } else {
            source.volume = volume;
            source.Play();
        }
    }

    /// <summary>Un sonido del caballero (Sounds/...): su copia.</summary>
    private void PlaySource(AudioSource heroSource) {
        if (heroSource == null || !Audible()) {
            return;
        }

        Component<AudioSource>(Resolve(heroSource.gameObject))?.Play();
    }

    /// <summary>Como AudioPlayerOneShotSingle: un reproductor del pool del juego que suena una vez y se recicla.</summary>
    private void PlayActorSound(GameObject actor, AudioClip clip, GameObject at, float pitch, float volume) {
        if (actor == null || clip == null || !Audible()) {
            return;
        }

        var position = at != null ? at.transform.position : _root.transform.position;
        var source = actor.Spawn(position, Quaternion.identity).GetComponent<AudioSource>();
        if (source != null) {
            source.pitch = pitch;
            source.volume = volume;
            source.PlayOneShot(clip);
        }
    }

    private void WarnOnce(string message) {
        if (_warned) {
            return;
        }

        _warned = true;
        Log.Warn(message);
    }
}
