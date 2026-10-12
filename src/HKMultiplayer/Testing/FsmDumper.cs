using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Testing;

/// <summary>
/// Vuelca a texto las FSM de las salas cargadas (orden <c>fsmdumpall</c> del modo de prueba): estados, transiciones,
/// acciones con sus valores y variables, tal como quedan en el juego después de lo que cambian el propio juego y la
/// Modding API. Las FSM no están en el código descompilado (van dentro de las escenas): así se pueden revisar y buscar
/// sin abrir el juego. Solo para pruebas: crea mucha memoria.
/// </summary>
internal static class FsmDumper {
    /// <summary>Elementos de un array que se escriben como mucho.</summary>
    private const int MaxArrayItems = 24;

    /// <summary>
    /// Escribe <c>&lt;carpeta&gt;/&lt;sala&gt;.txt</c> con las FSM de todas las salas cargadas (también las de los objetos
    /// apagados), los componentes de cada objeto con FSM, los tipos de componente de la sala y las FSM de los prefabs a los
    /// que apuntan sus acciones (lo que crean: disparos, crías). Devuelve un resumen para el registro.
    /// </summary>
    public static string DumpLoadedScenes(string folder) {
        // Números con punto en cualquier idioma (con coma se confundirían con las listas)
        var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try {
            var result = Dump(folder, persistent: false);
            if (!File.Exists(Path.Combine(folder, PersistentName + ".txt"))) {
                result += "; " + Dump(folder, persistent: true);
            }

            return result;
        } finally {
            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    /// <summary>
    /// Archivo con lo que no es de ninguna sala (`DontDestroyOnLoad`: el caballero, la cámara, el HUD, el gestor del juego),
    /// que el mod usa para los efectos del compañero. Se escribe una vez.
    /// </summary>
    private const string PersistentName = "_persistent";

    private static bool InDump(GameObject gameObject, bool persistent) {
        var scene = gameObject.scene;
        if (!persistent) {
            return scene.IsValid() && scene.buildIndex >= 0 && scene.isLoaded;
        }

        // Lo del propio mod no es del juego
        var root = gameObject.transform.root.name;
        return scene.IsValid() && scene.name == "DontDestroyOnLoad" &&
               !root.StartsWith("HKMultiplayer", StringComparison.Ordinal) &&
               !root.StartsWith("Jugador remoto", StringComparison.Ordinal);
    }

    private static string Dump(string folder, bool persistent) {
        var active = persistent ? PersistentName : UnitySceneManager.GetActiveScene().name;
        var scenes = new List<string>();
        if (persistent) {
            scenes.Add("DontDestroyOnLoad");
        } else {
            for (var i = 0; i < UnitySceneManager.sceneCount; i++) {
                var scene = UnitySceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.buildIndex >= 0) {
                    scenes.Add(scene.name);
                }
            }
        }

        var fsms = Resources.FindObjectsOfTypeAll<PlayMakerFSM>()
            .Where(f => f != null && f.Fsm != null && InDump(f.gameObject, persistent))
            .Select(f => (Path: TestDriver.PathOf(f.transform), Fsm: f))
            .OrderBy(f => f.Fsm.gameObject.scene.name, StringComparer.Ordinal)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Fsm.FsmName, StringComparer.Ordinal)
            .ToList();

        var text = new StringBuilder(1 << 20);
        text.Append("# sala ").Append(active).Append(" · cargadas: ").Append(string.Join(", ", scenes))
            .Append(" · ").Append(fsms.Count).Append(" FSM · juego ").Append(Constants.GAME_VERSION).Append('\n');
        text.Append("# Formato: '== ruta [fsm \"nombre\"]', luego componentes, variables, eventos, globales y cada estado")
            .Append(" con sus transiciones y acciones (campo=valor; {variable}). Una FSM igual a otra ya escrita solo")
            .Append(" dice de cuál es copia.\n\n");

        AppendComponentTypes(text, persistent);

        // Una FSM idéntica a otra (los rompibles, el pasto) se escribe una vez
        var seen = new Dictionary<string, string>();
        var prefabs = new Dictionary<GameObject, string>();
        foreach (var (path, fsm) in fsms) {
            var body = DescribeFsm(fsm, prefabs, $"{fsm.gameObject.scene.name}:{path}");
            var header = $"== {fsm.gameObject.scene.name}:{path} [fsm \"{fsm.FsmName}\"] " +
                         $"activo={fsm.gameObject.activeInHierarchy && fsm.enabled} actual={fsm.ActiveStateName}\n" +
                         $"  componentes: {DescribeComponents(fsm.gameObject)}\n";
            text.Append(header);
            if (seen.TryGetValue(body, out var original)) {
                text.Append("  (igual que ").Append(original).Append(")\n\n");
                continue;
            }

            seen[body] = $"{fsm.gameObject.scene.name}:{path} [{fsm.FsmName}]";
            text.Append(body).Append('\n');
        }

        // Prefabs (fuera de la sala) que crean las acciones: su lógica también corre en la sala
        var written = new HashSet<GameObject>();
        var pending = new Queue<GameObject>(prefabs.Keys);
        var prefabCount = 0;
        while (pending.Count > 0 && prefabCount < 400) {
            var prefab = pending.Dequeue();
            if (prefab == null || !written.Add(prefab)) {
                continue;
            }

            foreach (var fsm in prefab.GetComponentsInChildren<PlayMakerFSM>(true)) {
                if (fsm.Fsm == null) {
                    continue;
                }

                prefabCount++;
                var nested = new Dictionary<GameObject, string>();
                var body = DescribeFsm(fsm, nested, $"prefab {prefab.name}");
                text.Append($"== prefab {TestDriver.PathOf(fsm.transform)} [fsm \"{fsm.FsmName}\"] (lo crea {prefabs[prefab]})\n");
                text.Append($"  componentes: {DescribeComponents(fsm.gameObject)}\n");
                if (seen.TryGetValue(body, out var original)) {
                    text.Append("  (igual que ").Append(original).Append(")\n\n");
                } else {
                    seen[body] = $"prefab {fsm.gameObject.name} [{fsm.FsmName}]";
                    text.Append(body).Append('\n');
                }

                foreach (var pair in nested) {
                    if (!prefabs.ContainsKey(pair.Key)) {
                        prefabs[pair.Key] = pair.Value;
                        pending.Enqueue(pair.Key);
                    }
                }
            }
        }

        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, active + ".txt");
        File.WriteAllText(file, text.ToString());
        return $"{active}: {fsms.Count} FSM, {seen.Count} distintas, {prefabCount} de prefabs, salas [{string.Join(",", scenes)}] -> {file}";
    }

    /// <summary>Tipos de componente de la sala (del juego, no de Unity) con cuántos hay y dónde está el primero.</summary>
    private static void AppendComponentTypes(StringBuilder text, bool persistent) {
        var counts = new SortedDictionary<string, (int Count, string First)>(StringComparer.Ordinal);
        foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>()) {
            if (behaviour == null || !InDump(behaviour.gameObject, persistent)) {
                continue;
            }

            var type = behaviour.GetType();
            var ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("TMPro", StringComparison.Ordinal)) {
                continue;
            }

            var name = type.Name;
            counts[name] = counts.TryGetValue(name, out var entry)
                ? (entry.Count + 1, entry.First)
                : (1, TestDriver.PathOf(behaviour.transform));
        }

        text.Append("## componentes de la sala\n");
        foreach (var pair in counts) {
            text.Append("  ").Append(pair.Key).Append(" x").Append(pair.Value.Count).Append(" (p. ej. ")
                .Append(pair.Value.First).Append(")\n");
        }

        text.Append('\n');
    }

    private static string DescribeComponents(GameObject gameObject) {
        var parts = new List<string>();
        foreach (var component in gameObject.GetComponents<Component>()) {
            if (component == null || component is Transform) {
                continue;
            }

            var name = component.GetType().Name;
            if (component is HealthManager health) {
                name += $"(hp={health.hp})";
            } else if (component is Behaviour { enabled: false }) {
                name += "(apagado)";
            }

            parts.Add(name);
        }

        return string.Join(", ", parts);
    }

    /// <summary>Todo lo de la FSM menos su ruta: dos FSM con el mismo texto son la misma plantilla.</summary>
    private static string DescribeFsm(PlayMakerFSM fsm, Dictionary<GameObject, string> prefabs, string owner) {
        var text = new StringBuilder();
        text.Append("  inicio=").Append(fsm.Fsm.StartState).Append('\n');
        foreach (var group in fsm.FsmVariables.GetAllNamedVariables().GroupBy(v => v.VariableType)) {
            text.Append("  var ").Append(group.Key).Append(": ")
                .Append(string.Join(", ", group.Select(v => $"{v.Name}={VariableValue(v, prefabs, owner)}"))).Append('\n');
        }

        var events = fsm.FsmEvents.Where(e => e != null).Select(e => e.IsGlobal ? e.Name + "(global)" : e.Name);
        text.Append("  eventos: ").Append(string.Join(", ", events)).Append('\n');
        if (fsm.FsmGlobalTransitions.Length > 0) {
            text.Append("  globales: ")
                .Append(string.Join(", ", fsm.FsmGlobalTransitions.Select(t => $"{t.EventName}->{t.ToState}"))).Append('\n');
        }

        foreach (var state in fsm.FsmStates) {
            text.Append("  [").Append(state.Name).Append("]");
            if (state.Transitions.Length > 0) {
                text.Append(" -> ").Append(string.Join(", ", state.Transitions.Select(t => $"{t.EventName}->{t.ToState}")));
            }

            text.Append('\n');
            foreach (var action in state.Actions) {
                if (action == null) {
                    continue;
                }

                text.Append("    ").Append(action.GetType().Name);
                if (!action.Enabled) {
                    text.Append(" (desactivada)");
                }

                text.Append(':');
                foreach (var field in action.GetType().GetFields(System.Reflection.BindingFlags.Public |
                                                                 System.Reflection.BindingFlags.Instance)) {
                    object value;
                    try {
                        value = field.GetValue(action);
                    } catch (Exception) {
                        continue;
                    }

                    text.Append(' ').Append(field.Name).Append('=').Append(Value(value, prefabs, owner));
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Valor de una variable de la FSM (no su nombre, como en las acciones que la usan).</summary>
    private static string VariableValue(NamedVariable variable, Dictionary<GameObject, string> prefabs, string owner) {
        object raw;
        try {
            raw = variable.RawValue;
        } catch (Exception) {
            return "?";
        }

        return raw switch {
            null => "null",
            GameObject gameObject when gameObject != null => Value(gameObject, prefabs, owner),
            UnityEngine.Object unityObject => unityObject != null ? unityObject.name : "null",
            string plain => "\"" + plain.Replace('\n', ' ') + "\"",
            _ => Value(raw, prefabs, owner)
        };
    }

    /// <summary>Valor legible; los objetos fuera de la sala con FSM (prefabs) se apuntan para volcarlos después.</summary>
    private static string Value(object value, Dictionary<GameObject, string> prefabs, string owner) {
        switch (value) {
            case FsmGameObject { UseVariable: false } fsmObject:
                Note(fsmObject.Value, prefabs, owner);
                break;
            case FsmOwnerDefault { OwnerOption: OwnerDefaultOption.SpecifyGameObject } ownerDefault:
                Note(ownerDefault.GameObject?.Value, prefabs, owner);
                break;
            case GameObject gameObject:
                Note(gameObject, prefabs, owner);
                return gameObject.name;
            case FsmArray array:
                return $"{array.ElementType}[{array.Length}]";
            case Array array when array.Length > MaxArrayItems:
                return "[" + string.Join(",", array.Cast<object>().Take(MaxArrayItems)
                    .Select(v => Value(v, prefabs, owner))) + $",... ({array.Length})]";
            case Array array:
                return "[" + string.Join(",", array.Cast<object>().Select(v => Value(v, prefabs, owner))) + "]";
        }

        var described = TestDriver.DescribeValue(value);
        return described.Replace('\n', ' ').Replace('\r', ' ');
    }

    private static void Note(GameObject gameObject, Dictionary<GameObject, string> prefabs, string owner) {
        if (gameObject != null && !gameObject.scene.IsValid() && !prefabs.ContainsKey(gameObject) &&
            gameObject.GetComponentInChildren<PlayMakerFSM>(true) != null) {
            prefabs[gameObject] = owner;
        }
    }

    private static string _autoFolder;
    private static MonoBehaviour _autoHost;
    private static bool _autoHooked;

    /// <summary>
    /// Con carpeta, vuelca cada sala en cuanto se activa (un frame después, con sus salas añadidas ya cargadas) si aún no
    /// tiene archivo; con null, deja de hacerlo.
    /// </summary>
    public static void SetAuto(string folder, MonoBehaviour host) {
        _autoFolder = folder;
        _autoHost = host;
        if (_autoHooked) {
            return;
        }

        _autoHooked = true;
        UnitySceneManager.activeSceneChanged += (_, next) => {
            if (_autoFolder != null && _autoHost != null && next.buildIndex >= 0 &&
                !File.Exists(Path.Combine(_autoFolder, next.name + ".txt"))) {
                _autoHost.StartCoroutine(AutoDump(next.name));
            }
        };
    }

    private static System.Collections.IEnumerator AutoDump(string scene) {
        yield return null;
        if (_autoFolder == null || UnitySceneManager.GetActiveScene().name != scene) {
            yield break;
        }

        try {
            Util.Log.Info($"[TEST] fsmdumpall {DumpLoadedScenes(_autoFolder)}");
        } catch (Exception e) {
            Util.Log.Error($"[TEST] 'fsmdumpall {scene}' falló: {e}");
        }
    }

    /// <summary>Nombres de todas las salas del juego (por su ruta en la compilación), para recorrerlas.</summary>
    public static IEnumerable<string> BuildSceneNames() {
        for (var i = 0; i < UnitySceneManager.sceneCountInBuildSettings; i++) {
            yield return Path.GetFileNameWithoutExtension(
                UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i));
        }
    }
}
