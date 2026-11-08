using System;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Sonidos de revivir, con el volumen que elige cada jugador (<see cref="Settings.GlobalSettings.ReviveVolume"/>):
/// <list type="bullet">
/// <item>La carga de Focus mientras se revive a alguien o te están reviviendo. El juego no la pone: junto a un cuerpo
/// Focus no arranca (para que no cure), así que antes el que revivía no oía nada.</item>
/// <item>El sonido de curarse con Focus al levantarse.</item>
/// </list>
/// Por encima del 100 % se suman copias del mismo sonido a la vez (una fuente de audio no pasa de su volumen normal).
/// </summary>
internal static class ReviveAudio {
    /// <summary>Valores que ofrecen los menús, en %.</summary>
    public static readonly int[] VolumeSteps = { 0, 50, 100, 150, 200, 300, 400 };

    /// <summary>Copias como mucho (400 %): más fuerte satura y tapa el resto del juego.</summary>
    private const int MaxLayers = 4;

    /// <summary>Lo que tarda en apagarse la carga al soltar (como el Focus del juego).</summary>
    private const float FadeSeconds = 0.33f;

    private static GameObject _root;
    private static readonly AudioSource[] Layers = new AudioSource[MaxLayers];
    private static bool _loaded;
    private static AudioClip _chargeClip;
    private static AudioClip _healClip;
    private static VibrationDataAsset _healVibration;
    private static float _fade;

    /// <summary>Volumen elegido en este PC (0-4, 1 = normal).</summary>
    public static float Volume => Mathf.Clamp(HKMultiplayerMod.Settings.ReviveVolume, 0, MaxLayers * 100) / 100f;

    /// <summary>Etiquetas de <see cref="VolumeSteps"/> para los menús.</summary>
    public static string[] VolumeLabels() {
        var labels = new string[VolumeSteps.Length];
        for (var i = 0; i < labels.Length; i++) {
            labels[i] = VolumeSteps[i] == 0 ? Lang.T("Sin sonido", "Off") : $"{VolumeSteps[i]} %";
        }

        return labels;
    }

    /// <summary>Posición del volumen guardado en <see cref="VolumeSteps"/> (el más cercano si se editó a mano).</summary>
    public static int VolumeIndex() {
        var current = HKMultiplayerMod.Settings.ReviveVolume;
        var best = 0;
        for (var i = 1; i < VolumeSteps.Length; i++) {
            if (Math.Abs(VolumeSteps[i] - current) < Math.Abs(VolumeSteps[best] - current)) {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Cambia el volumen (se guarda) y suena el de levantarse para que se oiga cómo queda. Devuelve la posición que
    /// queda: la lista no da la vuelta, porque pasar de «Sin sonido» al 400 % de golpe asusta.
    /// </summary>
    public static int SetVolume(int index) {
        var last = VolumeSteps.Length - 1;
        var previous = VolumeIndex();
        if (previous == 0 && index == last || previous == last && index == 0) {
            return previous;
        }

        index = Mathf.Clamp(index, 0, last);
        HKMultiplayerMod.Settings.ReviveVolume = VolumeSteps[index];
        HKMultiplayerMod.SaveSettings();
        Log.Info($"Volumen de revivir: {HKMultiplayerMod.Settings.ReviveVolume} %");
        var hero = HeroController.instance;
        if (hero != null) {
            PlayRevived(hero, vibrate: false);
        }

        return index;
    }

    /// <summary>
    /// Cada frame: suena la carga mientras <paramref name="charging"/>; al dejar de cargar se apaga poco a poco.
    /// </summary>
    public static void UpdateCharge(bool charging) {
        if (!charging && _fade <= 0f) {
            return;
        }

        try {
            var hero = HeroController.instance;
            if (hero == null || !EnsureSources(hero) || _chargeClip == null) {
                return;
            }

            var volume = Volume;
            var layers = Mathf.Min(MaxLayers, Mathf.CeilToInt(volume));
            var started = charging && _fade <= 0f;
            if (charging) {
                _fade = 1f;
            } else {
                _fade = Mathf.Max(0f, _fade - Time.unscaledDeltaTime / FadeSeconds);
            }

            for (var i = 0; i < MaxLayers; i++) {
                var source = Layers[i];
                if (i >= layers || _fade <= 0f) {
                    if (source.isPlaying) {
                        source.Stop();
                    }

                    continue;
                }

                source.volume = volume / layers * _fade;
                if (!source.isPlaying) {
                    source.clip = _chargeClip;
                    source.loop = true;
                    source.Play();
                }
            }

            if (started) {
                var playing = 0;
                foreach (var source in Layers) {
                    playing += source.isPlaying ? 1 : 0;
                }

                Log.Info($"Carga de revivir: {playing} copia(s) sonando al {Mathf.RoundToInt(volume * 100f)} %");
            }
        } catch (Exception e) {
            _fade = 0f;
            Log.Warn($"No se pudo poner el sonido de revivir: {e.Message}");
        }
    }

    /// <summary>Corta la carga al momento (al terminar de revivir o al cerrar la partida).</summary>
    public static void StopCharge() {
        _fade = 0f;
        foreach (var source in Layers) {
            if (source != null && source.isPlaying) {
                source.Stop();
            }
        }
    }

    /// <summary>
    /// Aviso de que te revivieron: el sonido, la vibración y el destello blanco de curarse con Focus (estado "Focus Heal"
    /// de la FSM "Spell Control" del caballero). Lo pidió un jugador (issue #7): al levantarse no se notaba nada.
    /// </summary>
    public static void PlayRevived(HeroController hero, bool vibrate = true) {
        try {
            StopCharge();
            Load(hero);
            var audio = hero.GetComponent<AudioSource>();
            var volume = Volume;
            if (_healClip != null && audio != null && volume > 0f) {
                // Varias copias a la vez, cada una con su parte del volumen: una sola no se oía en mitad de un jefe (#8)
                var layers = Mathf.Min(MaxLayers, Mathf.CeilToInt(volume));
                for (var i = 0; i < layers; i++) {
                    audio.PlayOneShot(_healClip, volume / layers);
                }
            }

            if (vibrate && _healVibration != null) {
                VibrationManager.PlayVibrationClipOneShot(_healVibration);
            }

            if (vibrate) {
                hero.GetComponent<SpriteFlash>()?.flashFocusHeal();
            }
        } catch (Exception e) {
            Log.Warn($"No se pudo dar el aviso de revivir: {e.Message}");
        }
    }

    /// <summary>Saca del caballero los sonidos de Focus: la carga ("Focus Effects/Charge Audio") y el de curarse.</summary>
    private static void Load(HeroController hero) {
        if (_loaded) {
            return;
        }

        _loaded = true;
        var charge = hero.transform.Find("Focus Effects/Charge Audio");
        _chargeClip = charge != null ? charge.GetComponent<AudioSource>()?.clip : null;
        var spells = FSMUtility.LocateFSM(hero.gameObject, "Spell Control");
        foreach (var state in spells != null ? spells.FsmStates : Array.Empty<HutongGames.PlayMaker.FsmState>()) {
            if (state.Name != "Focus Heal") {
                continue;
            }

            foreach (var action in state.Actions) {
                if (action is HutongGames.PlayMaker.Actions.AudioPlayerOneShotSingle { audioClip.Value: AudioClip clip }) {
                    _healClip = clip;
                } else if (action is HutongGames.PlayMaker.Actions.PlayVibrationV2 { vibrationDataAsset.Value: VibrationDataAsset vibration }) {
                    _healVibration = vibration;
                }
            }
        }

        if (_chargeClip == null) {
            Log.Warn("Sin el sonido de la carga de Focus: mientras se revive no sonará nada");
        }

        if (_healClip == null) {
            Log.Warn("Sin el sonido de curarse: al revivir no sonará nada");
        }
    }

    /// <summary>
    /// Fuentes propias para la carga, en un hijo del caballero con los ajustes de su "Charge Audio" (mismo canal del
    /// mezclador, así que el volumen de efectos del juego también cuenta). El caballero dura toda la partida.
    /// </summary>
    private static bool EnsureSources(HeroController hero) {
        Load(hero);
        if (_root != null) {
            return true;
        }

        var template = hero.transform.Find("Focus Effects/Charge Audio")?.GetComponent<AudioSource>();
        _root = new GameObject("HKMP Revive Audio");
        _root.transform.SetParent(hero.transform, false);
        for (var i = 0; i < MaxLayers; i++) {
            var source = _root.AddComponent<AudioSource>();
            source.playOnAwake = false;
            if (template != null) {
                source.outputAudioMixerGroup = template.outputAudioMixerGroup;
                source.spatialBlend = template.spatialBlend;
                source.priority = template.priority;
                source.pitch = template.pitch;
            }

            Layers[i] = source;
        }

        return true;
    }
}
