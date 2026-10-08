using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HKMultiplayer.Game;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Testing;

/// <summary>
/// Modo de prueba (solo con el argumento <c>-mptest</c>): ejecuta las órdenes que se añaden, una por línea, a
/// &lt;persistentDataPath&gt;/mp-test.txt y escribe los resultados en el ModLog con "[TEST]". Permite probar con
/// varias instancias del juego sin tocar nada: hostear, unirse, cargar partidas, moverse, medir suavidad y memoria.
/// </summary>
internal sealed class TestDriver : MonoBehaviour {
    public const string CommandLineFlag = "-mptest";

    private const string Tag = "[TEST] ";

    private string _commandFile;
    private int _processedLines;
    private float _nextPoll;

    public static bool IsEnabled() => Environment.GetCommandLineArgs().Contains(CommandLineFlag);

    private void Start() {
        _commandFile = Path.Combine(Application.persistentDataPath, "mp-test.txt");
        InputSimulator.Install();

        // Las órdenes de una ejecución anterior no se repiten
        File.WriteAllText(_commandFile, string.Empty);
        Log.Info($"{Tag}modo de prueba activo; órdenes en {_commandFile}");
    }

    private void Update() {
        if (Time.unscaledTime < _nextPoll) {
            return;
        }

        _nextPoll = Time.unscaledTime + 0.5f;
        string[] lines;
        try {
            using var stream = new FileStream(_commandFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        } catch (IOException) {
            return;
        }

        // La última línea puede estar a medio escribir si no termina en salto de línea
        for (; _processedLines < lines.Length - 1; _processedLines++) {
            var line = lines[_processedLines].Trim();
            if (line.Length == 0 || line.StartsWith("#")) {
                continue;
            }

            try {
                Log.Info($"{Tag}> {line}");
                Execute(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            } catch (Exception e) {
                Log.Error($"{Tag}'{line}' falló: {e}");
            }
        }
    }

    private void Execute(string[] args) {
        var runner = MultiplayerRunner.Instance;
        var hero = HeroController.instance;
        switch (args[0]) {
            case "host":
                // host <nombre> <puerto> [código]
                runner.Host(args[1], int.Parse(args[2]), args.Length > 3 ? args[3] : null);
                break;
            case "hostworld":
                // hostworld <mundo> <nombre> <puerto> [código]: hostea ese mundo coop (lo crea si no existe)
                var world = HKMultiplayer.Game.World.WorldStore.List().FirstOrDefault(w => w.Name == args[1]) ??
                            HKMultiplayer.Game.World.WorldStore.Create(args[1]);
                runner.HostWorld(world, args[2], int.Parse(args[3]), args.Length > 4 ? args[4] : null);
                break;
            case "mkworld":
                // mkworld <nombre>: crea un mundo coop sin abrirlo
                HKMultiplayer.Game.World.WorldStore.Create(string.Join(" ", args.Skip(1)));
                break;
            case "pdset":
                // pdset <campo> <true|false|entero|texto>: cambia un dato de la partida como lo haría el juego
                if (bool.TryParse(args[2], out var boolValue)) {
                    PlayerData.instance.SetBool(args[1], boolValue);
                } else if (int.TryParse(args[2], out var intValue)) {
                    PlayerData.instance.SetInt(args[1], intValue);
                } else {
                    PlayerData.instance.SetString(args[1], args[2]);
                }

                break;
            case "pdget":
                Log.Info($"{Tag}pd {args[1]}={typeof(PlayerData).GetField(args[1])?.GetValue(PlayerData.instance)}");
                break;
            case "sdset":
                // sdset <sala> <id>: marca un objeto de sala como activado (como al romper una pared)
                SceneData.instance.SaveMyState(new PersistentBoolData { sceneName = args[1], id = args[2], activated = true });
                break;
            case "sdget":
                var stored = SceneData.instance.FindMyState(new PersistentBoolData { sceneName = args[1], id = args[2] });
                Log.Info($"{Tag}sd {args[1]}/{args[2]}={(stored == null ? "ninguno" : stored.activated.ToString())}");
                break;
            case "objs":
                // Objetos de la sala que se sincronizan en directo
                foreach (var line in runner.SceneObjects.Describe()) {
                    Log.Info($"{Tag}obj {line}");
                }

                break;
            case "extras":
                foreach (var line in runner.SceneObjects.DescribeExtras()) {
                    Log.Info($"{Tag}extra {line}");
                }

                break;
            case "hitrock":
                // hitrock <inicio del id>: golpea una piedra de geo
                if (!runner.SceneObjects.TestHitRock(string.Join(" ", args.Skip(1)))) {
                    Log.Warn($"{Tag}no hay piedra sin romper '{string.Join(" ", args.Skip(1))}'");
                }

                break;
            case "dropstalactite":
                if (!runner.SceneObjects.TestDropStalactite()) {
                    Log.Warn($"{Tag}no hay estalactitas sin caer en la sala");
                }

                break;
            case "movers":
                foreach (var line in runner.MovingObjects.Describe()) {
                    Log.Info($"{Tag}movil {line}");
                }

                break;
            case "setstate":
                // setstate <inicio del nombre>|<estado>
                var stateArgs = string.Join(" ", args.Skip(1)).Split('|');
                if (!runner.MovingObjects.TestSetState(stateArgs[0], stateArgs[1])) {
                    Log.Warn($"{Tag}no hay objeto móvil '{stateArgs[0]}'");
                }

                break;
            case "gates":
                foreach (var gate in FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None)) {
                    Log.Info($"{Tag}puerta {gate.name} pos=({gate.transform.position.x:0.0},{gate.transform.position.y:0.0})");
                }

                break;
            case "activate":
                // activate <inicio del id>: activa un objeto de la sala como si lo hubiera hecho este jugador
                if (!runner.SceneObjects.TestActivate(string.Join(" ", args.Skip(1)))) {
                    Log.Warn($"{Tag}no hay objeto sin activar '{string.Join(" ", args.Skip(1))}'");
                }

                break;
            case "enemies":
                foreach (var line in runner.Enemies.Describe()) {
                    Log.Info($"{Tag}enemigo {line}");
                }

                break;
            case "hitenemy":
                // hitenemy <inicio del nombre> <daño>: golpe de aguijón de este jugador
                var enemyName = string.Join(" ", args.Skip(1).Take(args.Length - 2));
                if (!runner.Enemies.TestHit(enemyName, int.Parse(args[args.Length - 1]))) {
                    Log.Warn($"{Tag}no hay enemigo vivo '{enemyName}'");
                }

                break;
            case "tpenemy":
                // tpenemy <inicio del nombre>: este jugador va junto a ese enemigo
                if (runner.Enemies.TryGetPosition(string.Join(" ", args.Skip(1)), out var enemyPosition)) {
                    hero.transform.position = new Vector3(enemyPosition.x - 3f, enemyPosition.y + 1f, hero.transform.position.z);
                }

                break;
            case "rest":
                // Como descansar en un banco (fija la reaparición donde ya estaba)
                var pd = PlayerData.instance;
                pd.SetBenchRespawn(pd.respawnMarkerName, pd.respawnScene, pd.respawnType, pd.respawnFacingRight);
                break;
            case "fsmdump":
                // fsmdump <nombre del objeto>: estados, acciones y transiciones de sus FSM
                var dumpTarget = GameObject.Find(string.Join(" ", args.Skip(1)));
                if (dumpTarget == null) {
                    Log.Warn($"{Tag}no existe '{string.Join(" ", args.Skip(1))}'");
                    break;
                }

                foreach (var fsm in dumpTarget.GetComponents<PlayMakerFSM>()) {
                    Log.Info($"{Tag}fsm '{fsm.FsmName}' inicio={fsm.Fsm.StartState} actual={fsm.ActiveStateName}");
                    foreach (var variable in fsm.FsmVariables.IntVariables) {
                        Log.Info($"{Tag}  int {variable.Name}={variable.Value}");
                    }

                    foreach (var variable in fsm.FsmVariables.BoolVariables) {
                        Log.Info($"{Tag}  bool {variable.Name}={variable.Value}");
                    }

                    foreach (var state in fsm.FsmStates) {
                        var actions = string.Join(", ", state.Actions.Select(a => a.GetType().Name));
                        var transitions = string.Join(", ", state.Transitions.Select(tr => $"{tr.EventName}->{tr.ToState}"));
                        Log.Info($"{Tag}  [{state.Name}] {actions} | {transitions}");
                    }
                }

                break;
            case "leavegame":
                // Sale de la partida como desde la pausa (en el mundo del host: guarda y vuelve al menú)
                runner.LeaveFromGame();
                break;
            case "save":
                // Guarda como en un banco
                GameManager.instance.SaveGame();
                break;
            case "tomenu":
                StartCoroutine(GameManager.instance.ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes.SaveAndContinueOnFail));
                break;
            case "join":
                // join <dirección> <puerto> <código> <nombre>
                runner.Join(args[1], int.Parse(args[2]), args[3], args[4]);
                break;
            case "leave":
                runner.Leave();
                break;
            case "loadslot":
                GameManager.instance.LoadGameFromUI(int.Parse(args[1]));
                break;
            case "newgame":
                GameManager.instance.profileID = int.Parse(args[1]);
                GameManager.instance.StartNewGame(false, false);
                break;
            case "skip":
                var input = GameManager.instance.inputHandler;
                if (input.skipMode is not (GlobalEnums.SkipPromptMode.NOT_SKIPPABLE
                        or GlobalEnums.SkipPromptMode.NOT_SKIPPABLE_DUE_TO_LOADING) && !input.skippingCutscene) {
                    input.skippingCutscene = true;
                    GameManager.instance.SkipCutscene();
                }

                break;
            case "goscene":
                // goscene <sala> <puerta>: entra por esa puerta, como al cruzarla
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo {
                    SceneName = args[1],
                    EntryGateName = args[2],
                    EntryDelay = 0f,
                    PreventCameraFadeOut = false,
                    WaitForSceneTransitionCameraFade = true,
                    AlwaysUnloadUnusedAssets = false
                });
                break;
            case "tp":
                hero.transform.position = new Vector3(ParseFloat(args[1]), ParseFloat(args[2]), hero.transform.position.z);
                break;
            case "press":
                // press <botón> [segundos]: botones de HeroActions (left, right, up, down, jump, attack, dash...)
                if (!InputSimulator.Press(args[1], args.Length > 2 ? ParseFloat(args[2]) : 0.12f)) {
                    Log.Warn($"{Tag}no existe el botón '{args[1]}'");
                }

                break;
            case "status":
                Log.Info(Tag + "status " + DescribeStatus());
                break;
            case "shot":
                var path = Path.Combine(Application.persistentDataPath, args[1] + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Log.Info($"{Tag}captura -> {path}");
                break;
            case "fpscap":
                // fpscap <fps>: limita los FPS (-1 = sin límite)
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = int.Parse(args[1]);
                break;
            case "allocrate":
                StartCoroutine(MeasureAllocations(ParseFloat(args[1])));
                break;
            case "frametime":
                StartCoroutine(MeasureFrames(ParseFloat(args[1])));
                break;
            case "trackpartner":
                StartCoroutine(Track(ParseFloat(args[1]), self: false));
                break;
            case "trackself":
                // Lo mismo con el caballero local: sirve de referencia para comparar con lo que ve el otro
                StartCoroutine(Track(ParseFloat(args[1]), self: true));
                break;
            case "pause":
                // Abre o cierra el menú de pausa como la tecla Esc
                UIManager.instance.TogglePauseGame();
                break;
            case "mpmenu":
                // Abre el menú Multijugador (como su botón del menú principal)
                HKMultiplayer.UI.MultiplayerMenu.Instance?.TestOpen();
                break;
            case "pausemp":
                // Abre Multijugador desde el menú de pausa
                HKMultiplayer.UI.SessionMenu.Instance?.TestOpen();
                break;
            case "mpclick":
                // mpclick <texto del botón>
                if (VisibleMenu()?.TestClick(string.Join(" ", args.Skip(1))) != true) {
                    Log.Warn($"{Tag}no hay botón visible '{string.Join(" ", args.Skip(1))}'");
                }

                break;
            case "mptype":
                // mptype <etiqueta>|<texto>
                var typed = string.Join(" ", args.Skip(1)).Split('|');
                if (VisibleMenu()?.TestType(typed[0], typed.Length > 1 ? typed[1] : "") != true) {
                    Log.Warn($"{Tag}no hay campo visible '{typed[0]}'");
                }

                break;
            case "uidump":
                // uidump [profundidad]: jerarquía del menú principal con sus componentes
                // uidump [profundidad] [ruta bajo el canvas de la interfaz]
                var ui = UIManager.instance;
                var root = args.Length > 2 ? (args[2] == "/" ? ui.UICanvas.transform : args[2] == "@pause" ? ui.pauseMenuScreen.transform : ui.UICanvas.transform.Find(args[2])) : ui.mainMenuScreen.transform;
                Dump(root, 0, args.Length > 1 ? int.Parse(args[1]) : 4);
                break;
            default:
                Log.Warn($"{Tag}orden desconocida '{args[0]}'");
                break;
        }
    }

    private static string DescribeStatus() {
        var runner = MultiplayerRunner.Instance;
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        var text = new StringBuilder();
        text.Append($"scene={gm?.sceneName} ");
        if (hero != null) {
            var p = hero.transform.position;
            text.Append($"pos=({p.x:0.00},{p.y:0.00}) ");
        }

        var session = runner.Session;
        if (session == null) {
            text.Append("sesión=ninguna");
            return text.ToString();
        }

        text.Append($"sesión={(session.IsHost ? "host" : "cliente")} activa={session.IsActive} id={session.LocalId} ");
        text.Append($"rtt={session.Rtt * 1000:0}ms visibles={runner.Players.VisibleCount} jugadores=[");
        text.Append(string.Join(", ", session.Players.Select(p =>
            $"{p.Id}:{p.Name}@{(p.Scene.Length > 0 ? p.Scene : "-")}{(p.IsLocal ? "*" : "")}")));
        text.Append(']');
        var partner = runner.Players.FirstVisibleId();
        if (partner != 0 && runner.Players.TryGetAvatarPosition(partner, out var position) &&
            runner.Players.TryGetBufferStats(partner, out var delay, out var jitter)) {
            text.Append($" compañero=({position.x:0.00},{position.y:0.00}) retraso={delay:0.0}t variación={jitter:0.0}t");
        }

        return text.ToString();
    }

    /// <summary>Menú del mod que se está viendo.</summary>
    private static HKMultiplayer.UI.GameStyledMenu VisibleMenu() =>
        FindObjectsByType<HKMultiplayer.UI.GameStyledMenu>(FindObjectsSortMode.None).FirstOrDefault(m => m.isActiveAndEnabled);

    private static void Dump(Transform node, int depth, int maxDepth) {
        var components = string.Join(",", node.GetComponents<Component>().Select(c => c.GetType().Name));
        var text = node.GetComponent<UnityEngine.UI.Text>();
        var rect = node as RectTransform;
        Log.Info($"{Tag}ui {new string(' ', depth * 2)}{node.name} [{components}]" +
                 (rect != null ? $" pos={rect.anchoredPosition} size={rect.sizeDelta}" : "") +
                 (text != null ? $" texto='{text.text}' fuente={text.font?.name} tamaño={text.fontSize}" : "") +
                 (node.gameObject.activeSelf ? "" : " (inactivo)"));
        if (depth >= maxDepth) {
            return;
        }

        foreach (Transform child in node) {
            Dump(child, depth + 1, maxDepth);
        }
    }

    private static float ParseFloat(string value) => float.Parse(value, CultureInfo.InvariantCulture);

    // =====================================================================================================
    // Mediciones
    // =====================================================================================================

    /// <summary>Memoria administrada creada por segundo (suma de lo que crece el montón en cada frame).</summary>
    private static IEnumerator MeasureAllocations(float seconds) {
        var start = Time.unscaledTime;
        var end = start + seconds;
        long allocated = 0;
        var previous = GC.GetTotalMemory(false);
        yield return null;
        while (Time.unscaledTime < end) {
            var now = GC.GetTotalMemory(false);
            if (now > previous) {
                allocated += now - previous;
            }

            previous = now;
            yield return null;
        }

        Log.Info($"{Tag}memoria creada: {allocated / 1024f / (Time.unscaledTime - start):0} KB/s");
    }

    /// <summary>Tiempo por frame (medio y peor), tirones de más de 50 ms y recolecciones de memoria.</summary>
    private static IEnumerator MeasureFrames(float seconds) {
        var end = Time.unscaledTime + seconds;
        int frames = 0, spikes = 0;
        float total = 0f, worst = 0f;
        var collections = GC.CollectionCount(0);
        yield return null;
        while (Time.unscaledTime < end) {
            var dt = Time.unscaledDeltaTime;
            frames++;
            total += dt;
            worst = Mathf.Max(worst, dt);
            if (dt > 0.05f) {
                spikes++;
            }

            yield return null;
        }

        Log.Info($"{Tag}frames={frames} medio={total / Mathf.Max(1, frames) * 1000f:0.0} ms peor={worst * 1000f:0.0} ms " +
                 $"tirones(>50ms)={spikes} recolecciones={GC.CollectionCount(0) - collections}");
    }

    /// <summary>
    /// Suavidad del compañero en pantalla: cuánto se mueve en cada frame. Con un movimiento constante, la desviación
    /// de la velocidad por frame debe ser pequeña y no debe haber pasos hacia atrás.
    /// </summary>
    private static IEnumerator Track(float seconds, bool self) {
        var players = MultiplayerRunner.Instance.Players;
        var id = players.FirstVisibleId();

        bool TryGetPosition(out Vector3 position) {
            if (self) {
                var hero = HeroController.instance;
                position = hero != null ? hero.transform.position : default;
                return hero != null;
            }

            return players.TryGetAvatarPosition(id, out position);
        }

        if (!TryGetPosition(out var last)) {
            Log.Warn($"{Tag}no hay compañero en la sala");
            yield break;
        }

        // Se mide al final de cada frame: el avatar ya se movió (LateUpdate) y el paso corresponde a este frame
        var endOfFrame = new WaitForEndOfFrame();
        var end = Time.unscaledTime + seconds;
        var speeds = new List<float>();
        int frames = 0, still = 0, backwards = 0;
        var lastDx = 0f;
        var buffer = self ? null : players.BufferOf(id);
        var beyondStart = buffer?.BeyondNewest ?? 0;
        var snapsStart = buffer?.Snaps ?? 0;
        var stillTimes = new List<string>();
        var startTime = Time.unscaledTime;
        yield return endOfFrame;
        TryGetPosition(out last);
        while (Time.unscaledTime < end && TryGetPosition(out var now)) {
            var dt = Time.unscaledDeltaTime;
            var dx = now.x - last.x;
            var step = Vector2.Distance(now, last);
            last = now;
            frames++;
            if (step < 0.0001f) {
                still++;
                if (stillTimes.Count < 40) {
                    stillTimes.Add((Time.unscaledTime - startTime).ToString("0.000", CultureInfo.InvariantCulture));
                }
            } else if (dt > 0f) {
                speeds.Add(step / dt);
                if (lastDx != 0f && Mathf.Sign(dx) != Mathf.Sign(lastDx) && Mathf.Abs(dx) > 0.001f) {
                    backwards++;
                }
            }

            if (Mathf.Abs(dx) > 0.001f) {
                lastDx = dx;
            }

            yield return endOfFrame;
        }

        var mean = speeds.Count > 0 ? speeds.Average() : 0f;
        var deviation = speeds.Count > 0 ? Mathf.Sqrt(speeds.Average(s => (s - mean) * (s - mean))) : 0f;
        Log.Info($"{Tag}{(self ? "propio" : "compañero")}: frames={frames} quietos={still} velocidad media={mean:0.00} u/s " +
                 $"desviación={deviation:0.00} ({(mean > 0 ? deviation / mean * 100 : 0):0}%) cambios de sentido={backwards}" +
                 (buffer != null ? $" más allá del último={buffer.BeyondNewest - beyondStart} saltos={buffer.Snaps - snapsStart}" : "") +
                 $" quietos en t=[{string.Join(" ", stillTimes)}]");
    }
}
