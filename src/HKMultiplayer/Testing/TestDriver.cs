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
