using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Scene;

/// <summary>
/// Identificador de un objeto de la sala igual en todos los PCs: huella de su ruta en la jerarquía (nombre de la escena
/// y, por cada nivel, nombre e índice entre sus hermanos). Crea memoria: usar al buscar objetos, no cada frame.
/// <para>El índice entre hermanos cambia durante la partida (un enemigo de la raíz que muere se destruye; EnemySync suelta
/// piezas de su padre). Por eso, con partida activa, al cargarse cada sala se apunta la huella de lo que se sincroniza por
/// su ruta (<see cref="StableHash"/>): antes, una estalactita que caía después de morir un enemigo de la raíz tenía otra
/// huella que la apuntada al cargar y no caía en el otro PC, y el botín salía con otra semilla.</para>
/// </summary>
internal static class ScenePaths {
    private static readonly StringBuilder Path = new();

    /// <summary>Huella de cada objeto apuntada al cargarse su sala.</summary>
    private static readonly Dictionary<Transform, uint> Stable = new();

    /// <summary>Componentes cuyos objetos se identifican por su ruta (sincronizados en alguna capa).</summary>
    private static readonly HashSet<string> TrackedTypes = new() {
        "PlayMakerFSM", "HealthManager", "StalactiteControl", "BigCentipede", "GeoRock", "Breakable", "BreakableObject",
        "BreakablePole", "BreakablePoleSimple", "BreakableInfectedVine", "VinePlatformCut", "GlobControl", "GrassCut",
        "Grass", "GrassSpriteBehaviour", "HealthCocoon"
    };

    private static readonly List<MonoBehaviour> Behaviours = new();
    private static readonly List<Animator> Animators = new();
    private static readonly List<GameObject> Roots = new();
    private static readonly List<Transform> Dead = new();
    private static bool _hooked;

    /// <summary>Con partida: se apuntan las huellas al cargar cada sala. Sin ella no se hace nada.</summary>
    public static bool Enabled { get; set; }

    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        UnitySceneManager.sceneLoaded += (scene, _) => {
            if (Enabled && scene.buildIndex >= 0) {
                Remember(scene);
            }
        };
        UnitySceneManager.sceneUnloaded += _ => Prune();
    }

    /// <summary>Huella apuntada al cargar la sala; si el objeto no estaba entonces (creado después), la de ahora.</summary>
    public static uint StableHash(Transform transform) =>
        transform != null && Stable.TryGetValue(transform, out var hash) ? hash : Hash(transform);

    public static uint Hash(Transform transform) {
        Path.Length = 0;
        for (var t = transform; t != null; t = t.parent) {
            Path.Insert(0, t.GetSiblingIndex()).Insert(0, '#').Insert(0, t.name).Insert(0, '/');
        }

        Path.Insert(0, transform.gameObject.scene.name);
        var hash = 2166136261u;
        for (var i = 0; i < Path.Length; i++) {
            hash = (hash ^ Path[i]) * 16777619u;
        }

        return hash;
    }

    private static void Remember(UnityEngine.SceneManagement.Scene scene) {
        Roots.Clear();
        scene.GetRootGameObjects(Roots);
        foreach (var root in Roots) {
            Behaviours.Clear();
            root.GetComponentsInChildren(true, Behaviours);
            foreach (var behaviour in Behaviours) {
                if (behaviour != null && TrackedTypes.Contains(behaviour.GetType().Name)) {
                    Note(behaviour.transform);
                }
            }

            Animators.Clear();
            root.GetComponentsInChildren(true, Animators);
            foreach (var animator in Animators) {
                Note(animator.transform);
            }
        }

        Roots.Clear();
        Behaviours.Clear();
        Animators.Clear();
    }

    private static void Note(Transform transform) {
        if (!Stable.ContainsKey(transform)) {
            Stable[transform] = Hash(transform);
        }
    }

    /// <summary>Quita los objetos ya destruidos (sus salas se descargaron).</summary>
    private static void Prune() {
        Dead.Clear();
        foreach (var transform in Stable.Keys) {
            if (transform == null) {
                Dead.Add(transform);
            }
        }

        foreach (var transform in Dead) {
            Stable.Remove(transform);
        }

        Dead.Clear();
    }

    /// <summary>Al empezar la partida: las salas ya cargadas.</summary>
    public static void RememberLoaded() {
        for (var i = 0; i < UnitySceneManager.sceneCount; i++) {
            var scene = UnitySceneManager.GetSceneAt(i);
            if (scene.isLoaded && scene.buildIndex >= 0) {
                Remember(scene);
            }
        }
    }

    /// <summary>Al cerrar la partida.</summary>
    public static void Clear() => Stable.Clear();
}
