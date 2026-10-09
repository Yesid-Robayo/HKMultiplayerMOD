using System;
using System.Reflection;
using HKMultiplayer.Util;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Viaje del caballero local a una sala y un punto concretos sin puerta de entrada (como el viaje del caído en
/// <see cref="Revive"/>): el juego no lo coloca, se coloca al llegar y se cierra la entrada como al cruzar una puerta.
/// Lo usan las cinemáticas compartidas para devolver a quien fue llevado a donde estaba.
/// </summary>
internal sealed class SceneTravel {
    /// <summary>Si el viaje no termina en este tiempo (el juego lo rechazó), se abandona.</summary>
    private const float Timeout = 25f;

    private static readonly MethodInfo FinishedEnteringSceneMethod = typeof(HeroController).GetMethod(
        "FinishedEnteringScene", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool), typeof(bool) },
        null);

    private static readonly MethodInfo SendHeroInPositionMethod =
        typeof(HeroController).GetMethod("SendHeroInPosition", BindingFlags.NonPublic | BindingFlags.Instance);

    private string _target;
    private Vector3 _position;
    private float _startedAt;

    /// <summary>Si hay un viaje en curso.</summary>
    public bool Active => _target != null;

    /// <summary>Empieza el viaje. False si ahora no se puede (sin juego o ya cambiando de sala).</summary>
    public bool Begin(string scene, Vector3 position) {
        var gm = GameManager.instance;
        if (gm == null || string.IsNullOrEmpty(scene) || gm.IsInSceneTransition) {
            return false;
        }

        Log.Info($"Volviendo a {scene} ({position.x:0.0}, {position.y:0.0})");
        _target = scene;
        _position = position;
        _startedAt = Time.unscaledTime;
        gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = string.Empty,
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            AlwaysUnloadUnusedAssets = true
        });
        return true;
    }

    public void Cancel() => _target = null;

    /// <summary>Llamar cada frame: al llegar coloca al caballero y le devuelve el control.</summary>
    public void Update() {
        if (_target == null) {
            return;
        }

        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (gm == null || hero == null || gm.IsInSceneTransition || UnitySceneManager.GetActiveScene().name != _target) {
            if (gm == null || !gm.IsInSceneTransition && Time.unscaledTime - _startedAt > Timeout) {
                Log.Warn($"El viaje a {_target} no terminó");
                _target = null;
            }

            return;
        }

        _target = null;
        hero.transform.position = _position;
        try {
            FinishedEnteringSceneMethod?.Invoke(hero, new object[] { true, false });
        } catch (Exception e) {
            Log.Warn($"FinishedEnteringScene falló: {e.Message}");
        }

        try {
            SendHeroInPositionMethod?.Invoke(hero, new object[] { true });
        } catch (Exception e) {
            Log.Warn($"SendHeroInPosition falló: {e.Message}");
        }

        var cameras = GameCameras.instance;
        if (cameras != null && cameras.cameraController != null) {
            cameras.cameraController.PositionToHero(true);
        }

        gm.FadeSceneIn();
        hero.RegainControl();
    }
}
