using System;
using System.Reflection;
using HKMultiplayer.Util;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Viaje del caballero local a una sala y un punto concretos sin puerta de entrada: se entra como con la Puerta de los
/// sueños (entrada "dreamGate"), que coloca al caballero en el punto guardado en <c>dreamGateX/Y</c> antes de mostrar la
/// sala; ese punto se cambia un momento por el pedido y se repone al llegar. Al llegar se cierra la entrada como al
/// cruzar una puerta. Lo usan las cinemáticas compartidas para devolver a quien fue llevado a donde estaba, e
/// "Ir con ..." del panel para llevar al jugador junto a un compañero de otra sala.
/// <para>Antes se entraba sin nombre de puerta: el juego ponía entonces al caballero en la primera puerta de la sala
/// (<c>FindTransitionPoint</c> con "cualquiera") y, en algunas (Cruces Olvidados junto a Madre Gruz), salía por ella
/// a otra sala antes de que el mod lo colocara.</para>
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

    /// <summary>Al llegar, antes de colocar al caballero: dónde colocarlo (recibe el punto pedido).</summary>
    private Func<Vector3, Vector3> _onArrival;

    /// <summary>Punto de la Puerta de los sueños del jugador, cambiado durante el viaje (se repone al acabar).</summary>
    private readonly DreamGatePoint _dreamGate = new();


    /// <summary>Nombre de entrada con el que el juego coloca al caballero en <c>dreamGateX/Y</c>.</summary>
    public const string DreamGateEntry = "dreamGate";

    /// <summary>Si hay un viaje en curso.</summary>
    public bool Active => _target != null;

    /// <summary>
    /// Empieza el viaje. False si ahora no se puede (sin juego o ya cambiando de sala). Con <paramref name="onArrival"/>,
    /// al llegar (la sala ya cargada) decide el punto final a partir del pedido y puede hacer lo que haga falta allí.
    /// </summary>
    public bool Begin(string scene, Vector3 position, Func<Vector3, Vector3> onArrival = null) {
        var gm = GameManager.instance;
        if (gm == null || string.IsNullOrEmpty(scene) || gm.IsInSceneTransition) {
            return false;
        }

        Log.Info($"Viaje a {scene} ({position.x:0.0}, {position.y:0.0})");
        _target = scene;
        _position = position;
        _onArrival = onArrival;
        _startedAt = Time.unscaledTime;

        // El juego coloca al caballero en el punto de la Puerta de los sueños al entrar así: se pone ahí el pedido
        var pd = PlayerData.instance;
        var entry = string.Empty;
        if (pd != null) {
            _dreamGate.Use(pd, position);
            entry = DreamGateEntry;
        }

        gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = entry,
            EntryDelay = 0f,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            AlwaysUnloadUnusedAssets = true
        });
        return true;
    }

    public void Cancel() {
        _target = null;
        _onArrival = null;
        RestoreDreamGate();
    }

    /// <summary>Repone el punto de la Puerta de los sueños del jugador.</summary>
    private void RestoreDreamGate() => _dreamGate.Restore();

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
                _onArrival = null;
                RestoreDreamGate();
            }

            return;
        }

        _target = null;
        RestoreDreamGate();
        var position = _position;
        var onArrival = _onArrival;
        _onArrival = null;
        if (onArrival != null) {
            try {
                position = onArrival(position);
            } catch (Exception e) {
                Log.Warn($"Al llegar: {e.Message}");
            }
        }

        hero.transform.position = position;
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

/// <summary>
/// El punto de la Puerta de los sueños del jugador, cambiado un momento para entrar en una sala en un punto (la entrada
/// <c>dreamGate</c> coloca al caballero en <c>dreamGateX/Y</c>). Mientras dura, su sala (<c>dreamGateScene</c>) se vacía:
/// si el destino era la sala de la marca, el juego la ponía al empezar la sala en el punto pedido. Al reponerlo, si la
/// marca es de la sala en la que se está, se pone en su sitio como lo hace el juego (<c>SceneManager.Start</c>).
/// </summary>
internal sealed class DreamGatePoint {
    private bool _saved;
    private float _x;
    private float _y;
    private string _scene;

    public void Use(PlayerData pd, Vector3 position) {
        if (!_saved) {
            _x = pd.GetFloat("dreamGateX");
            _y = pd.GetFloat("dreamGateY");
            _scene = pd.GetString("dreamGateScene");
            _saved = true;
        }

        pd.SetFloat("dreamGateX", position.x);
        pd.SetFloat("dreamGateY", position.y);
        pd.SetString("dreamGateScene", string.Empty);
    }

    public void Restore() {
        if (!_saved) {
            return;
        }

        _saved = false;
        var pd = PlayerData.instance;
        if (pd == null) {
            return;
        }

        pd.SetFloat("dreamGateX", _x);
        pd.SetFloat("dreamGateY", _y);
        pd.SetString("dreamGateScene", _scene ?? string.Empty);
        SpawnMarker(pd);
    }

    private static void SpawnMarker(PlayerData pd) {
        var gm = GameManager.instance;
        var sm = gm != null ? gm.sm : null;
        var scene = pd.GetString("dreamGateScene");
        if (sm == null || sm.dreamgateObject == null || string.IsNullOrEmpty(scene) || !gm.IsGameplayScene() ||
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != scene) {
            return;
        }

        var marker = UnityEngine.Object.Instantiate(sm.dreamgateObject,
            new Vector3(pd.GetFloat("dreamGateX"), pd.GetFloat("dreamGateY") - 1.429361f, -0.002f), Quaternion.identity);
        marker.transform.SetParent(sm.transform, worldPositionStays: true);
        marker.transform.SetParent(null);
    }
}
