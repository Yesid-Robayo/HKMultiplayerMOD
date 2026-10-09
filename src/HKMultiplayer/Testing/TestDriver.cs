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

        // Cada daño que recibe el caballero y qué lo hizo (para comprobar que los ataques dañan a los dos jugadores)
        On.HeroController.TakeDamage += (orig, self, source, side, amount, hazard) => {
            var health = PlayerData.instance != null ? PlayerData.instance.health : -1;
            orig(self, source, side, amount, hazard);
            var after = PlayerData.instance != null ? PlayerData.instance.health : -1;
            if (after != health) {
                Log.Info($"{Tag}daño {health - after} de {(source != null ? source.name : "?")}");
            }
        };

        // Cada enemigo que muere en este PC y desde dónde se llamó (para encontrar muertes que no deberían pasar)
        On.HealthManager.Die += (orig, self, direction, attackType, ignoreEvasion) => {
            if (!self.isDead) {
                var caller = string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames()?.Skip(2).Take(6)
                    .Select(f => f.GetMethod()?.Name) ?? Array.Empty<string>());
                Log.Info($"{Tag}muere {self.name} hp={self.hp} por {caller}");
            }

            // En arenas: si el enemigo tenía su batalla apuntada y cómo cambia su contador de enemigos
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance;
            var battle = typeof(HealthManager).GetField("battleScene", flags)?.GetValue(self) as GameObject;
            var notified = typeof(HealthManager).GetField("notifiedBattleScene", flags)?.GetValue(self);
            var killedTo = (typeof(HealthManager).GetField("sendKilledTo", flags)?.GetValue(self) as GameObject) ??
                           typeof(HealthManager).GetField("sendKilledToObject", flags)?.GetValue(self) as GameObject;
            var counter = battle != null ? FSMUtility.LocateFSM(battle, "Battle Control")?.FsmVariables.GetFsmInt("Battle Enemies") : null;
            var before = counter?.Value;
            orig(self, direction, attackType, ignoreEvasion);
            if (battle != null || killedTo != null) {
                Log.Info($"{Tag}arena {self.name}: batalla={(battle != null ? battle.name : "-")} avisado={notified} " +
                         $"contador {before}→{counter?.Value} avisa a={(killedTo != null ? killedTo.name : "-")}");
            }
        };

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
                SceneData.instance.SaveMyState(new PersistentBoolData {
                    sceneName = args[1], id = string.Join(" ", args.Skip(2)), activated = true
                });
                break;
            case "sdclear":
                // sdclear <sala> <id>: vuelve a dejar sin activar un objeto de sala (p. ej. revivir un jefe para probarlo)
                var cleared = SceneData.instance.FindMyState(new PersistentBoolData {
                    sceneName = args[1], id = string.Join(" ", args.Skip(2))
                });
                if (cleared != null) {
                    cleared.activated = false;
                }

                Log.Info($"{Tag}sd {args[1]}/{string.Join(" ", args.Skip(2))} sin activar: {(cleared != null ? "sí" : "no existía")}");
                break;
            case "sdlist":
                // sdlist <sala>: lo guardado de esa sala (objetos activados, números, piedras)
                foreach (var item in SceneData.instance.persistentBoolItems.Where(i => i.sceneName == args[1])) {
                    Log.Info($"{Tag}sd bool {item.id}={item.activated}");
                }

                foreach (var item in SceneData.instance.persistentIntItems.Where(i => i.sceneName == args[1])) {
                    Log.Info($"{Tag}sd int {item.id}={item.value}");
                }

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
            case "moving":
                // moving <segundos>: objetos sólidos de la sala que se mueven o giran solos (plataformas, sierras...)
                StartCoroutine(FindMoving(ParseFloat(args[1])));
                break;
            case "wake":
                // wake <inicio del nombre>: enciende el primer enemigo apagado con ese nombre (los de las jaulas del
                // Coliseo) y lo pone junto al caballero
                var sleeping = FindObjectsByType<HealthManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(h => !h.gameObject.activeInHierarchy && !h.isDead &&
                                         h.name.StartsWith(string.Join(" ", args.Skip(1)), StringComparison.Ordinal));
                if (sleeping == null) {
                    Log.Warn($"{Tag}no hay enemigo apagado '{string.Join(" ", args.Skip(1))}'");
                } else {
                    sleeping.transform.position = hero.transform.position + new Vector3(4f, 2f, 0f);
                    sleeping.gameObject.SetActive(true);
                    Log.Info($"{Tag}encendido {sleeping.name}");
                }

                break;
            case "hazards":
                // hazards: peligros animados que se alinean con el dueño de la sala
                Log.Info($"{Tag}{runner.Hazards.Describe()}");
                break;
            case "healthlist":
                // healthlist: todos los objetos con vida de la sala, con su padre y su huella (para ver repetidos)
                foreach (var hm in FindObjectsByType<HealthManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    var t = hm.transform;
                    Log.Info($"{Tag}vida {hm.name} padre={(t.parent != null ? t.parent.name : "-")} " +
                             $"activo={hm.gameObject.activeInHierarchy} hermano={t.GetSiblingIndex()} " +
                             $"huella={HKMultiplayer.Game.Scene.ScenePaths.Hash(t):x8}");
                }

                break;
            case "arenas":
                // arenas: objetos que no son enemigos pero les dan órdenes o los encienden (controladores de combate).
                // Marcas: O=órdenes a enemigos, A=enciende enemigos, R=decide al azar, H=reacciona al caballero local
                // (zona o botón), T=a todos. Los que ya siguen a un controlador salen con *
                foreach (var line in DescribeArenas()) {
                    Log.Info($"{Tag}arena {line}");
                }

                break;
            case "fsmevent":
                // fsmevent <inicio del nombre>|<evento>: orden a un objeto móvil como desde su palanca o botón
                var eventArgs = string.Join(" ", args.Skip(1)).Split('|');
                if (!runner.MovingObjects.TestSendEvent(eventArgs[0], eventArgs[1])) {
                    Log.Warn($"{Tag}no hay objeto móvil '{eventArgs[0]}'");
                }

                break;
            case "fsmgo":
                // fsmgo <inicio del nombre>[/<fsm>]|<estado>: como fsmevent, pero pone ese estado (saltar oleadas del Coliseo)
                var goArgs = string.Join(" ", args.Skip(1)).Split('|');
                if (!runner.MovingObjects.TestSetState(goArgs[0], goArgs[1])) {
                    Log.Warn($"{Tag}no hay objeto móvil '{goArgs[0]}'");
                }

                break;
            case "fsmstate":
                // fsmstate <objeto>|<estado>: pone la FSM del objeto en ese estado (p. ej. el golpe de una palanca)
                var fsmArgs = string.Join(" ", args.Skip(1)).Split('|');
                var fsmObject = GameObject.Find(fsmArgs[0]);
                var targetFsm = fsmObject != null ? fsmObject.GetComponent<PlayMakerFSM>() : null;
                if (targetFsm == null) {
                    Log.Warn($"{Tag}no existe '{fsmArgs[0]}'");
                } else {
                    targetFsm.SetState(fsmArgs[1]);
                }

                break;
            case "hitnearest":
                // hitnearest <objeto>[|evento]: el objeto con ese nombre más cercano al caballero recibe el evento (por
                // defecto NAIL HIT), como si lo golpeara de verdad
                var hitArgs = string.Join(" ", args.Skip(1)).Split('|');
                var hitEvent = hitArgs.Length > 1 ? hitArgs[1] : "NAIL HIT";
                var nearest = FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)
                    .Where(f => f.gameObject.name == hitArgs[0])
                    .OrderBy(f => (f.transform.position - hero.transform.position).sqrMagnitude)
                    .FirstOrDefault();
                if (nearest == null) {
                    Log.Warn($"{Tag}no existe '{hitArgs[0]}'");
                } else {
                    // A todas las FSM del objeto (los jefes tienen varias; cada una atiende sus eventos)
                    foreach (var hitFsm in nearest.GetComponents<PlayMakerFSM>()) {
                        Log.Info($"{Tag}{hitEvent} a {hitFsm.gameObject.name}/{hitFsm.FsmName} en {hitFsm.ActiveStateName}");
                        hitFsm.SendEvent(hitEvent);
                    }
                }

                break;
            case "findobj":
                // findobj <texto>: objetos de la sala cuyo nombre lo contiene, con sus componentes
                var needle = string.Join(" ", args.Skip(1));
                var shown = 0;
                foreach (var found in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    if (found.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 || shown++ >= 15) {
                        continue;
                    }

                    var components = string.Join(",", found.GetComponents<Component>().Select(c => c.GetType().Name));
                    var fsms = string.Join(",", found.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName));
                    Log.Info($"{Tag}obj {found.name} padre={(found.parent != null ? found.parent.name : "-")}{(found.gameObject.activeInHierarchy ? "" : " (inactivo)")} [{components}] fsm=[{fsms}] " +
                             $"pos=({found.position.x:0.0},{found.position.y:0.0})" +
                             (found.GetComponent<Collider2D>() is { } box ? $" caja={box.bounds.min:F1}-{box.bounds.max:F1}" : string.Empty));
                }

                break;
            case "findactive":
                // findactive <texto>: solo los objetos activos cuyo nombre lo contiene, con su posición
                var activeNeedle = string.Join(" ", args.Skip(1));
                foreach (var found in FindObjectsByType<Transform>(FindObjectsSortMode.None)) {
                    if (found.name.IndexOf(activeNeedle, StringComparison.OrdinalIgnoreCase) >= 0) {
                        var fsms = string.Join(",", found.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName));
                        Log.Info($"{Tag}activo {found.name} padre={(found.parent != null ? found.parent.name : "-")} " +
                                 $"pos=({found.position.x:0.0},{found.position.y:0.0}) fsm=[{fsms}]");
                    }
                }

                break;
            case "methods":
                // methods <tipo>: métodos y campos de un tipo del juego (para saber cómo engancharse a él)
                var type = typeof(HeroController).Assembly.GetType(args[1]);
                if (type == null) {
                    Log.Warn($"{Tag}no existe el tipo '{args[1]}'");
                    break;
                }

                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
                foreach (var method in type.GetMethods(all)) {
                    Log.Info($"{Tag}método {method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(x => x.ParameterType.Name))})");
                }

                foreach (var field in type.GetFields(all)) {
                    Log.Info($"{Tag}campo {field.FieldType.Name} {field.Name}");
                }

                break;
            case "fsmnames":
                // Nombres de FSM de la sala y cuántas hay de cada uno
                foreach (var group in FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                             .GroupBy(f => f.FsmName).OrderByDescending(g => g.Count())) {
                    Log.Info($"{Tag}fsmname {group.Key} x{group.Count()} ej={group.First().gameObject.name}");
                }

                break;
            case "tpobj":
                // tpobj <objeto> [altura]: el caballero aparece encima del objeto
                var tpArgs = string.Join(" ", args.Skip(1)).Split('|');
                var tpTarget = GameObject.Find(tpArgs[0]);
                if (tpTarget == null) {
                    Log.Warn($"{Tag}no existe '{tpArgs[0]}'");
                } else {
                    var above = tpArgs.Length > 1 ? ParseFloat(tpArgs[1]) : 1.5f;
                    var p = tpTarget.transform.position;
                    hero.transform.position = new Vector3(p.x, p.y + above, hero.transform.position.z);
                }

                break;
            case "say":
                // say <segundos> <texto>: cartel grande arriba (para la demostración); | separa líneas
                HKMultiplayer.UI.Banner.Show(string.Join(" ", args.Skip(2)).Replace('|', '\n'), ParseFloat(args[1]));
                break;
            case "breakables":
                Log.Info($"{Tag}rompibles: {runner.Breakables.Count} en la sala, {runner.Breakables.BrokenCount()} rotos");
                break;
            case "breaklist":
                // breaklist: cada rompible de la sala con su tipo, id, estado y posición
                foreach (var line in runner.Breakables.Describe()) {
                    Log.Info($"{Tag}rompible {line}");
                }

                break;
            case "breakname":
                // breakname <texto>: rompe los rompibles enteros cuyo nombre lo contiene
                Log.Info($"{Tag}rotos {runner.Breakables.TestBreakNamed(string.Join(" ", args.Skip(1)))}");
                break;
            case "breaknear":
                // breaknear <cantidad>: rompe los rompibles enteros más cercanos al jugador
                Log.Info($"{Tag}rotos {runner.Breakables.TestBreakNearest(int.Parse(args[1]))}");
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
            case "bosses":
                foreach (var line in runner.Enemies.DescribeBosses()) {
                    Log.Info($"{Tag}{line}");
                }

                break;
            case "netsim":
                // netsim <retraso ms> <variación ms> <pérdida %>: la próxima partida (host o unirse) va como una mala conexión
                HKMultiplayer.Net.Transport.LaggyTransport.NextLatency = ParseFloat(args[1]) / 1000f;
                HKMultiplayer.Net.Transport.LaggyTransport.NextJitter = ParseFloat(args[2]) / 1000f;
                HKMultiplayer.Net.Transport.LaggyTransport.NextLoss = ParseFloat(args[3]) / 100f;
                break;
            case "hitall":
                // hitall <daño>: golpea a todos los enemigos vivos de la sala, como el caballero
                Log.Info($"{Tag}golpeados {runner.Enemies.TestHitAll(int.Parse(args[1]))}");
                break;
            case "parts":
                // parts <enemigo>: piezas que se mueven aparte de ese enemigo
                foreach (var line in runner.Enemies.DescribeParts(string.Join(" ", args.Skip(1)))) {
                    Log.Info($"{Tag}pieza {line}");
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

                    var objectVariables = fsm.FsmVariables.GameObjectVariables;
                    for (var v = 0; v < objectVariables.Length; v++) {
                        var value = objectVariables[v].Value;
                        Log.Info($"{Tag}  objeto {v} {objectVariables[v].Name}={(value != null ? value.name : "-")}");
                    }

                    var globals = string.Join(", ", fsm.FsmGlobalTransitions.Select(tr => $"{tr.EventName}->{tr.ToState}"));
                    Log.Info($"{Tag}  globales: {globals}");

                    foreach (var state in fsm.FsmStates) {
                        var actions = string.Join(", ", state.Actions.Select(a => a.GetType().Name));
                        var transitions = string.Join(", ", state.Transitions.Select(tr => $"{tr.EventName}->{tr.ToState}"));
                        Log.Info($"{Tag}  [{state.Name}] {actions} | {transitions}");
                    }
                }

                break;
            case "fsmactions":
                // fsmactions <objeto>|<estado>: parámetros de cada acción de ese estado (qué variable o FSM tocan)
                var actionArgs = string.Join(" ", args.Skip(1)).Split('|');
                var actionObject = GameObject.Find(actionArgs[0]);
                if (actionObject == null) {
                    Log.Warn($"{Tag}no existe '{actionArgs[0]}'");
                    break;
                }

                // Con "*" como estado, todos los estados
                foreach (var fsm in actionObject.GetComponents<PlayMakerFSM>())
                foreach (var state in fsm.FsmStates.Where(s => actionArgs[1] == "*" || s.Name == actionArgs[1])) {
                    foreach (var action in state.Actions) {
                        var fields = action.GetType().GetFields(System.Reflection.BindingFlags.Public |
                                                                System.Reflection.BindingFlags.Instance)
                            .Select(f => $"{f.Name}={DescribeValue(f.GetValue(action))}");
                        Log.Info($"{Tag}  {fsm.FsmName}/{state.Name} {action.GetType().Name}: {string.Join(" ", fields)}");
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
            case "die":
                // die: el caballero recibe un golpe mortal, como en el juego (muerte, sombra y vuelta al banco)
                PlayerData.instance.SetInt("health", 1);
                hero.TakeDamage(hero.gameObject, GlobalEnums.CollisionSide.top, 99, 1);
                break;
            case "hurt":
                // hurt: el caballero recibe un golpe de 1 (para ver el efecto del golpe en el otro PC)
                hero.TakeDamage(hero.gameObject, GlobalEnums.CollisionSide.left, 1, 1);
                break;
            case "tp":
                // tp <x> <y> [respawn]: con "respawn", si pisa pinchos vuelve aquí y no a la entrada de la sala. Solo en
                // un sitio seguro: sobre un suelo que luego se llena de pinchos (el Coliseo) reaparecería en ellos sin fin
                hero.transform.position = new Vector3(ParseFloat(args[1]), ParseFloat(args[2]), hero.transform.position.z);
                if (args.Length > 3 && args[3] == "respawn") {
                    hero.SetHazardRespawn(hero.transform.position, hero.cState.facingRight);
                }

                break;
            case "press":
                // press <botón> [segundos]: botones de HeroActions (left, right, up, down, jump, attack, dash...)
                if (!InputSimulator.Press(args[1], args.Length > 2 ? ParseFloat(args[2]) : 0.12f)) {
                    Log.Warn($"{Tag}no existe el botón '{args[1]}'");
                }

                break;
            case "slashes":
                // slashes <cuántos> <segundos entre uno y otro> [up|down]: ráfaga de aguijonazos seguidos (las órdenes se
                // leen cada medio segundo: varios "press attack" seguidos serían uno solo), con arriba o abajo pulsado
                StartCoroutine(Slashes(int.Parse(args[1]), ParseFloat(args[2]), args.Length > 3 ? args[3] : null));
                break;
            case "status":
                Log.Info(Tag + "status " + DescribeStatus());
                break;
            case "hero":
                // Por qué el caballero no responde: control, estados y pausa
                Log.Info($"{Tag}hero acceptingInput={hero.acceptingInput} controlReqlinquished={hero.controlReqlinquished} " +
                         $"dead={hero.cState.dead} transitioning={hero.cState.transitioning} hazardDeath={hero.cState.hazardDeath} " +
                         $"hazardRespawning={hero.cState.hazardRespawning} recoiling={hero.cState.recoiling} " +
                         $"invulnerable={hero.cState.invulnerable} focusing={hero.cState.focusing} " +
                         $"isPaused={GameManager.instance.isPaused} gameState={GameManager.instance.gameState} " +
                         $"timeScale={Time.timeScale} heroState={hero.hero_state} " +
                         $"anim={hero.GetComponent<tk2dSpriteAnimator>()?.CurrentClip?.name}");
                break;
            case "regain":
                // Devuelve el control al caballero (para desatascar una prueba)
                hero.RegainControl();
                hero.AcceptInput();
                hero.StartAnimationControl();
                break;
            case "shot":
                var path = Path.Combine(Application.persistentDataPath, args[1] + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Log.Info($"{Tag}captura -> {path}");
                break;
            case "burst":
                // burst <nombre> <cuántas> <segundos entre una y otra>: capturas seguidas (nombre-1, nombre-2...), para
                // ver un efecto mientras pasa (las órdenes se leen cada medio segundo)
                StartCoroutine(Burst(args[1], int.Parse(args[2]), ParseFloat(args[3])));
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
            case "mppage":
                // mppage <partida|ajustes|ajustes2|compañeros>: página del panel Multijugador de la pausa
                if (HKMultiplayer.UI.SessionMenu.Instance?.TestPage(args[1]) != true) {
                    Log.Warn($"{Tag}no hay página '{args[1]}'");
                }

                break;
            case "mpoption":
                // mpoption <inicio de la etiqueta>[|pasos]: avanza esa opción del menú visible como la flecha derecha
                var optionArgs = string.Join(" ", args.Skip(1)).Split('|');
                var optionValue = VisibleMenu()?.TestOption(optionArgs[0], optionArgs.Length > 1 ? int.Parse(optionArgs[1]) : 1);
                Log.Info(optionValue != null ? $"{Tag}opción {optionArgs[0]} = {optionValue}" : $"{Tag}no hay opción '{optionArgs[0]}'");
                break;
            case "mpoptions":
                // Opciones del menú visible con su valor
                foreach (var line in VisibleMenu()?.TestDescribeOptions() ?? Enumerable.Empty<string>()) {
                    Log.Info($"{Tag}opción {line}");
                }

                break;
            case "settings":
                // Ajustes de la partida que rigen en este PC
                Log.Info($"{Tag}ajustes {HKMultiplayer.Game.Features.HostSettings.Current.Describe()}");
                break;
            case "party":
                // Estado de revivir: caído, cuerpo, viaje, progreso y estado de cada jugador
                Log.Info($"{Tag}revivir {runner.Revive.Describe()}");
                break;
            case "partystate":
                // Estado de cada jugador según el host (en pie, caído, en cinemática, en sueño)
                Log.Info($"{Tag}estado {runner.Party.DescribeAll()}");
                break;
            case "cutscenes":
                // Cinemática compartida en curso, votos, quién fue llevado y (en el host) quién ve cada una
                Log.Info($"{Tag}cinemáticas {runner.Cutscenes.Describe()}");
                break;
            case "dreams":
                // Sueños: si está en uno y las entradas a sueños de la sala con su estado
                Log.Info($"{Tag}sueños {runner.Dreams.Describe()}");
                break;
            case "dreamhit":
                // dreamhit [encender]: como golpear con el aguijón onírico la entrada a un sueño de la sala; con
                // "encender", si ninguna está encendida se enciende la primera (luego otro dreamhit la golpea)
                Log.Info($"{Tag}golpe onírico: {(runner.Dreams.TestDreamHit(args.Length > 1) ? "sí" : "no hay entrada")}");
                break;
            case "presentation":
                // Presentación de jefe: si este PC avisó, quién tiene quieto al caballero local
                Log.Info($"{Tag}presentación {runner.Presentations.Describe()}");
                break;
            case "cinematic":
                // Reproductores de vídeo de la escena: si su vídeo va, dónde se dibuja y cómo está el fundido de la cámara
                var flagsAll = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                foreach (var player in FindObjectsByType<CinematicPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    var video = player.GetComponents<Component>().FirstOrDefault(c => c.GetType().Name == "VideoPlayer");
                    var meshRenderer = player.GetComponent<MeshRenderer>();
                    Log.Info($"{Tag}vídeo {player.name} tipo={player.videoType} activo={player.isActiveAndEnabled} " +
                             $"disparado={typeof(CinematicPlayer).GetField("videoTriggered", flagsAll)?.GetValue(player)} " +
                             $"reproduciendo={typeof(CinematicPlayer).GetField("isPlaying", flagsAll)?.GetValue(player)} " +
                             $"malla={(meshRenderer != null && meshRenderer.enabled)} unity={(video != null ? $"{video.GetType().GetProperty("isPlaying")?.GetValue(video)} t={video.GetType().GetProperty("time")?.GetValue(video)}" : "-")} " +
                             $"pos={player.transform.position} capa={player.gameObject.layer}");
                }

                var cams = GameCameras.instance;
                Log.Info($"{Tag}cámara pos={cams?.mainCamera?.transform.position} fundido={cams?.cameraFadeFSM?.ActiveStateName} " +
                         $"hud={cams?.hudCamera?.isActiveAndEnabled} estado={GameManager.instance.gameState} " +
                         $"ui={UIManager.instance?.uiState} omitir={GameManager.instance.inputHandler.skipMode}");
                break;
            case "skins":
                // Skin propia, las cargadas, la que lleva cada uno y qué textura dibuja cada caballero
                Log.Info($"{Tag}skins {runner.Skins.Describe()}");
                break;
            case "skin":
                // skin <nombre de la carpeta>|normal: elige la skin propia (como en el panel)
                var skinName = string.Join(" ", args.Skip(1));
                HKMultiplayer.Game.Features.Skins.SelectLocalSkin(skinName == "normal" ? string.Empty : skinName);
                break;
            case "skinshow":
                // skinshow si|no: ver o no las skins de los demás en este PC
                HKMultiplayer.Game.Features.Skins.SetShowOthers(args.Length < 2 || args[1] != "no");
                break;
            case "charmupdate":
                // Como cambiar de amuletos en un banco: el caballero aplica los equipados (p. ej. sale Grimmchild)
                hero.CharmUpdate();
                PlayMakerFSM.BroadcastEvent("CHARM EQUIP CHECK");
                PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK");
                break;
            case "senders":
                // senders <evento>: FSM de la sala (también inactivas) que mandan ese aviso, con su estado y su destino
                var wanted = string.Join(" ", args.Skip(1));
                foreach (var fsm in FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    foreach (var state in fsm.FsmStates) {
                        foreach (var action in state.Actions) {
                            var sent = action switch {
                                HutongGames.PlayMaker.Actions.SendEventByName byName => byName.sendEvent?.Value,
                                HutongGames.PlayMaker.Actions.SendEvent plain => plain.sendEvent?.Name,
                                _ => null
                            };
                            if (sent == wanted) {
                                Log.Info($"{Tag}manda {fsm.gameObject.name}/{fsm.FsmName} [{state.Name}] {action.GetType().Name} " +
                                         $"actual={fsm.ActiveStateName} activo={fsm.gameObject.activeInHierarchy}");
                            }
                        }
                    }
                }

                break;
            case "langget":
                // langget <hoja> <clave>: texto del juego en su idioma (p. ej. "Titles FALSE_KNIGHT_MAIN")
                Log.Info($"{Tag}texto {args[1]}/{args[2]}=" + (TeamCherry.Localization.Language.Has(args[2], args[1])
                    ? TeamCherry.Localization.Language.Get(args[2], args[1]) : "(no existe)"));
                break;
            case "covers":
                // Lo que se dibuja delante de la cámara y la tapa (grande, sobre su centro): máscaras de zonas ocultas...
                var view = GameCameras.instance.mainCamera.transform.position;
                foreach (var drawn in FindObjectsByType<Renderer>(FindObjectsSortMode.None)) {
                    var area = drawn.bounds;
                    if (!drawn.enabled || area.size.x < 4f || area.size.y < 3f || view.x < area.min.x || view.x > area.max.x ||
                        view.y < area.min.y || view.y > area.max.y) {
                        continue;
                    }

                    var fsms = string.Join(",", drawn.GetComponents<PlayMakerFSM>().Select(f => f.FsmName + ":" + f.ActiveStateName));
                    Log.Info($"{Tag}tapa {drawn.name} padre={(drawn.transform.parent != null ? drawn.transform.parent.name : "-")} " +
                             $"tipo={drawn.GetType().Name} z={drawn.transform.position.z:0.0} caja={area.min:F1}-{area.max:F1} " +
                             $"color={(drawn is SpriteRenderer sprite ? sprite.color.ToString() : "-")} fsm=[{fsms}] " +
                             $"comp=[{string.Join(",", drawn.GetComponents<Component>().Select(c => c.GetType().Name))}]");
                }

                break;
            case "transitions":
                // FSM de la sala con un cambio de sala (BeginSceneTransition): a qué sala, por qué puerta y cómo se ve
                foreach (var fsm in FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    foreach (var state in fsm.FsmStates) {
                        foreach (var action in state.Actions) {
                            if (action is HutongGames.PlayMaker.Actions.BeginSceneTransition transition) {
                                Log.Info($"{Tag}cambio {fsm.gameObject.name}/{fsm.FsmName} [{state.Name}] -> " +
                                         $"{transition.sceneName.Value}/{transition.entryGateName.Value} " +
                                         $"{transition.visualization.Value} actual={fsm.ActiveStateName} " +
                                         $"activo={fsm.gameObject.activeInHierarchy}");
                            }
                        }
                    }
                }

                break;
            case "focus":
                // focus <segundos>: como mantener Focus (para revivir)
                runner.Revive.TestFocus(ParseFloat(args[1]));
                break;
            case "tpbody":
                // El caballero va junto al cuerpo de un caído de la sala
                if (runner.Revive.TestBodyPosition(out var bodyPosition)) {
                    hero.transform.position = new Vector3(bodyPosition.x, bodyPosition.y + 0.2f, hero.transform.position.z);
                } else {
                    Log.Warn($"{Tag}no hay cuerpos en la sala");
                }

                break;
            case "heal":
                // Vida llena como al curarse (el HUD del juego se actualiza, a diferencia de pdset)
                hero.AddHealth(PlayerData.instance.maxHealth - PlayerData.instance.health);
                break;
            case "soul":
                // soul <cantidad>: alma para el caballero (como al golpear)
                hero.AddMPCharge(int.Parse(args[1]));
                break;
            case "shades":
                Log.Info($"{Tag}sombras {runner.Shades.Describe()}");
                break;
            case "killshade":
                // Mata la copia de la sombra de un compañero en la sala, como al golpearla
                Log.Info($"{Tag}sombra de compañero derrotada: {(runner.Shades.TestKillCopy() ? "sí" : "no había")}");
                break;
            case "bossbar":
                Log.Info($"{Tag}jefes {runner.BossBar.Describe()}");
                break;
            case "vitals":
                // Vida y alma recibidas de cada compañero
                foreach (var member in runner.Party.Members) {
                    var vitals = runner.Vitals.Get(member.Id);
                    Log.Info($"{Tag}vida {member.Name}: " + (vitals == null
                        ? "sin datos"
                        : $"{vitals.Health}/{vitals.MaxHealth} azules={vitals.Blue} alma={vitals.Soul}/{vitals.MaxSoul} " +
                          $"zona={HKMultiplayer.Game.Features.Zones.Name(vitals.Zone)}"));
                }

                break;
            case "spriteof":
                // spriteof <nombre>: sprite que muestra ahora cada objeto con ese nombre (p. ej. "Health 1" del HUD)
                foreach (var drawn in Resources.FindObjectsOfTypeAll<tk2dSprite>()
                             .Where(t => t.gameObject.scene.IsValid() && t.name == string.Join(" ", args.Skip(1)))) {
                    var definition = drawn.CurrentSprite;
                    Log.Info($"{Tag}muestra {drawn.name}: {drawn.Collection?.name}/{definition?.name} " +
                             $"escala=({drawn.transform.lossyScale.x:0.00},{drawn.transform.lossyScale.y:0.00})");
                }

                break;
            case "collections":
                // collections <texto>: colecciones de sprites cargadas cuyo nombre lo contiene
                foreach (var collection in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()
                             .Where(c => c.name.IndexOf(string.Join(" ", args.Skip(1)), StringComparison.OrdinalIgnoreCase) >= 0)) {
                    Log.Info($"{Tag}colección {collection.name} ({collection.spriteDefinitions.Length} sprites)");
                }

                break;
            case "hudsprite":
                // hudsprite <full|empty|blue> <sprite>: cambia el sprite de las máscaras del HUD del compañero
                var kind = args[1] switch {
                    "empty" => HKMultiplayer.UI.HudSprites.Kind.Empty,
                    "blue" => HKMultiplayer.UI.HudSprites.Kind.Blue,
                    _ => HKMultiplayer.UI.HudSprites.Kind.Full
                };
                Log.Info($"{Tag}sprite {args[2]}: {(HKMultiplayer.UI.HudSprites.Override(kind, args[2]) ? "puesto" : "no existe")}");
                runner.Hud.RefreshSprites();
                break;
            case "hudrot":
                // hudrot <grados>: giro de los sprites guardados girados en el atlas
                HKMultiplayer.UI.HudSprites.RotatedAngle = ParseFloat(args[1]);
                runner.Hud.RefreshSprites();
                break;
            case "sprites":
                // sprites <colección>[|texto]: sprites de una colección de tk2d (p. ej. "HUD Cln") con su tamaño
                var spriteArgs = string.Join(" ", args.Skip(1)).Split('|');
                foreach (var collection in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()
                             .Where(c => c.name == spriteArgs[0] || c.spriteCollectionName == spriteArgs[0]).Take(1)) {
                    foreach (var definition in collection.spriteDefinitions) {
                        if (spriteArgs.Length > 1 &&
                            definition.name.IndexOf(spriteArgs[1], StringComparison.OrdinalIgnoreCase) < 0) {
                            continue;
                        }

                        var tex = definition.material != null ? definition.material.mainTexture : null;
                        Log.Info($"{Tag}sprite {definition.name} girado={definition.flipped} " +
                                 $"uv=[{string.Join(" ", definition.uvs.Select(uv => $"{uv.x:0.000},{uv.y:0.000}"))}] " +
                                 $"textura={(tex != null ? $"{tex.name} {tex.width}x{tex.height}" : "-")}");
                    }
                }

                break;
            case "clips":
                // clips [texto]: animaciones del caballero con sus fotogramas
                foreach (var clip in hero.GetComponent<tk2dSpriteAnimator>().Library.clips) {
                    if (clip == null || args.Length > 1 && clip.name.IndexOf(args[1], StringComparison.OrdinalIgnoreCase) < 0) {
                        continue;
                    }

                    Log.Info($"{Tag}clip {clip.name} modo={clip.wrapMode} fps={clip.fps} fotogramas=" +
                             string.Join(",", clip.frames.Select(f => f.spriteCollection.spriteDefinitions[f.spriteId].name)));
                }

                break;
            case "effects":
                // Efectos del otro jugador enviados, recibidos y reproducidos
                Log.Info($"{Tag}{runner.Players.DescribeEffects()}");
                break;
            case "tree":
                // tree <profundidad> <ruta del objeto>: jerarquía de un objeto con sus componentes (p. ej. "Knight")
                var treeRoot = GameObject.Find(string.Join(" ", args.Skip(2)));
                if (treeRoot == null) {
                    Log.Warn($"{Tag}no existe '{string.Join(" ", args.Skip(2))}'");
                    break;
                }

                Dump(treeRoot.transform, 0, int.Parse(args[1]));
                break;
            case "fsmall":
                // fsmall <ruta del objeto>: acciones de todas las FSM del objeto y sus hijos, también inactivos. Si no
                // está en la sala, se busca por nombre entre todo lo cargado (prefabs del pool, como "Fireball Top"):
                // antes una copia ya creada (sus acciones tienen los valores con los que funciona) que el prefab
                var fsmName = string.Join(" ", args.Skip(1));
                var loaded = Resources.FindObjectsOfTypeAll<GameObject>();
                var fsmRoot = GameObject.Find(fsmName) ??
                              loaded.FirstOrDefault(g => g.scene.IsValid() && (g.name == fsmName || g.name == fsmName + "(Clone)")) ??
                              loaded.FirstOrDefault(g => g.name == fsmName);
                if (fsmRoot == null) {
                    Log.Warn($"{Tag}no existe '{string.Join(" ", args.Skip(1))}'");
                    break;
                }

                foreach (var fsm in fsmRoot.GetComponentsInChildren<PlayMakerFSM>(true)) {
                    Log.Info($"{Tag}fsmall {fsm.gameObject.name}/{fsm.FsmName} inicio={fsm.Fsm.StartState}");
                    foreach (var state in fsm.FsmStates) {
                        var transitions = string.Join(", ", state.Transitions.Select(tr => $"{tr.EventName}->{tr.ToState}"));
                        Log.Info($"{Tag}fsmall   [{state.Name}] | {transitions}");
                        foreach (var action in state.Actions) {
                            var fields = action.GetType().GetFields(System.Reflection.BindingFlags.Public |
                                                                    System.Reflection.BindingFlags.Instance)
                                .Select(f => $"{f.Name}={DescribeValue(f.GetValue(action))}");
                            Log.Info($"{Tag}fsmall     {action.GetType().Name}: {string.Join(" ", fields)}");
                        }
                    }
                }

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

    private static IEnumerator Burst(string name, int count, float interval) {
        for (var i = 1; i <= count; i++) {
            ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, $"{name}-{i}.png"));
            yield return new WaitForSecondsRealtime(interval);
        }

        Log.Info($"{Tag}ráfaga {name} terminada ({count} capturas)");
    }

    private static IEnumerator Slashes(int count, float interval, string direction) {
        if (direction != null) {
            InputSimulator.Press(direction, count * (interval + 0.08f) + 0.1f);
        }

        for (var i = 0; i < count; i++) {
            InputSimulator.Press("attack", 0.06f);
            yield return new WaitForSecondsRealtime(interval + 0.06f);
        }
    }

    private static IEnumerator FindMoving(float seconds) {
        var hero = HeroController.instance;
        var start = new Dictionary<Transform, (Vector3 Position, float Angle)>();
        foreach (var collider in FindObjectsByType<Collider2D>(FindObjectsSortMode.None)) {
            var t = collider.transform;
            if (collider.gameObject.scene.buildIndex < 0 || HKMultiplayer.Game.Scene.EnemySync.IsEnemyObject(t) ||
                hero != null && t.IsChildOf(hero.transform) || start.ContainsKey(t)) {
                continue;
            }

            start[t] = (t.position, t.eulerAngles.z);
        }

        yield return new WaitForSeconds(seconds);
        var shown = 0;
        foreach (var pair in start) {
            var t = pair.Key;
            if (t == null) {
                continue;
            }

            var moved = (t.position - pair.Value.Position).magnitude;
            var turned = Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.z, pair.Value.Angle));
            if (moved < 0.05f && turned < 1f || shown++ >= 40) {
                continue;
            }

            var collider = t.GetComponent<Collider2D>();
            var fsms = string.Join(",", t.GetComponentsInParent<PlayMakerFSM>(true).Select(f => f.gameObject.name + "/" + f.FsmName + ":" + f.ActiveStateName));
            var parts = string.Join(",", t.GetComponents<Component>().Select(c => c.GetType().Name)
                .Where(n => n != "Transform" && !n.StartsWith("PlayMaker", StringComparison.Ordinal)));
            Log.Info($"{Tag}mueve {t.name} padre={(t.parent != null ? t.parent.name : "-")} recorrido={moved:0.0} giro={turned:0} " +
                     $"sólido={(collider != null && !collider.isTrigger ? "sí" : "no")} daño={(t.GetComponent<DamageHero>() != null ? "sí" : "no")} " +
                     $"[{parts}] fsm=[{fsms}]");
        }

        Log.Info($"{Tag}movidos {shown} de {start.Count}");
    }

    private static IEnumerable<string> DescribeArenas() {
        var hero = HeroController.instance;
        foreach (var fsm in FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            if (fsm.gameObject.scene.buildIndex < 0 || HKMultiplayer.Game.Scene.EnemySync.IsEnemyObject(fsm.transform) ||
                hero != null && fsm.transform.IsChildOf(hero.transform)) {
                continue;
            }

            bool orders = false, activates = false, random = false, heroTrigger = false, broadcast = false;
            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    var name = action.GetType().Name;
                    random |= name.Contains("Random");
                    heroTrigger |= name.StartsWith("Trigger2d", StringComparison.Ordinal) || name.StartsWith("ListenFor", StringComparison.Ordinal) ||
                                   name == "CheckAlertRange" || name == "CheckAlertRangeByName";
                    try {
                        CheckArenaAction(action, ref orders, ref activates, ref broadcast);
                    } catch (Exception) {
                        // Acción con campos vacíos: no dice nada
                    }
                }
            }

            if (!orders && !activates) {
                continue;
            }

            var flags = (orders ? "O" : "") + (activates ? "A" : "") + (random ? "R" : "") + (heroTrigger ? "H" : "") +
                        (broadcast ? "T" : "");
            var shared = HKMultiplayer.Game.Scene.FsmStateSync.IsSharedController(fsm.Fsm) ? "*" : "";
            yield return $"{shared}{fsm.gameObject.name}/{fsm.FsmName} [{flags}] estado={fsm.ActiveStateName}";
        }
    }

    private static void CheckArenaAction(HutongGames.PlayMaker.FsmStateAction action, ref bool orders, ref bool activates,
        ref bool broadcast) {
        switch (action) {
            case HutongGames.PlayMaker.Actions.SendEventByName send:
                var target = send.eventTarget;
                if (target.target == HutongGames.PlayMaker.FsmEventTarget.EventTarget.BroadcastAll) {
                    broadcast = true;
                } else if (target.target is HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObject
                           or HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObjectFSM) {
                    var receiver = action.Fsm.GetOwnerDefaultTarget(target.gameObject);
                    orders |= receiver != null && HKMultiplayer.Game.Scene.EnemySync.IsEnemyObject(receiver.transform);
                }

                break;
            case HutongGames.PlayMaker.Actions.ActivateGameObject activate:
                var activated = action.Fsm.GetOwnerDefaultTarget(activate.gameObject);
                activates |= activated != null && activated.GetComponentsInChildren<HealthManager>(true).Length > 0;
                break;
            case HutongGames.PlayMaker.Actions.ActivateAllChildren children:
                var parent = children.gameObject.Value;
                activates |= parent != null && parent.GetComponentsInChildren<HealthManager>(true).Length > 0;
                break;
        }
    }

    private static float ParseFloat(string value) => float.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>Valor legible de un parámetro de acción: las variables con nombre se muestran por su nombre.</summary>
    private static string DescribeValue(object value) {
        switch (value) {
            case null:
                return "null";
            case HutongGames.PlayMaker.NamedVariable { UseVariable: true } named:
                return $"{{{named.Name}}}";
            case HutongGames.PlayMaker.NamedVariable named:
                return named.ToString();
            case HutongGames.PlayMaker.FsmEvent fsmEvent:
                return $"'{fsmEvent.Name}'";
            case HutongGames.PlayMaker.FsmOwnerDefault owner:
                return owner.OwnerOption == HutongGames.PlayMaker.OwnerDefaultOption.UseOwner
                    ? "owner"
                    : DescribeValue(owner.GameObject);
            case HutongGames.PlayMaker.FsmEventTarget target:
                return $"{target.target}:{DescribeValue(target.gameObject)}:{DescribeValue(target.fsmName)}";
            case Array array:
                return "[" + string.Join(",", array.Cast<object>().Select(DescribeValue)) + "]";
            default:
                return value.ToString();
        }
    }

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
