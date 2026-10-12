using System;
using System.Collections.Generic;
using System.Reflection;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HKMultiplayer.Game.World;

/// <summary>
/// Personajes de misión que aparecen en varios sitios, cambian o se van al hablarles (Mr. Mushroom, Quirrel, Zote,
/// Bretta...). El avance de su misión es un dato del mundo y llega al otro PC por el progreso compartido, pero el
/// personaje en la sala es de cada PC: el juego solo mira ese dato al cargar la sala. Aquí:
/// <list type="bullet">
/// <item>Cuando llega de otro PC un cambio de un dato del mundo, las FSM de la sala que lo miran al empezar (su comprobación
/// al cargar la sala: "¿estoy en este sitio?", "¿ya me rescataron?") vuelven a empezar con el valor nuevo, como si se
/// cargara la sala. El personaje desaparece o cambia también en este PC. Si este jugador está en una conversación o
/// una cinemática (su caballero sin control), se espera a que acabe.</item>
/// <item>Salvo si el dato es un sí/no y lo cambió un compañero que está en esta misma sala (habló con el personaje
/// delante de este jugador): en su PC el personaje se queda hasta volver a entrar (Bretta tras rescatarla), y aquí
/// también. Antes se iba en este PC a mitad de la conversación del otro. Un número no (Mr. Mushroom): hablar otra vez
/// con la copia que se quedara avanzaría la misión dos veces.</item>
/// <item>Una misión no avanza dos veces por la misma conversación: si los dos hablan a la vez con su copia del personaje
/// y el otro avanza la misión mientras este jugador habla, la lógica de su personaje no cambia otra vez ese número del
/// mundo, y lo lee con el valor que tenía al empezar la conversación (dice lo mismo que le dijo al otro). Si no, la
/// misión avanzaba dos pasos y se saltaba un sitio.</item>
/// </list>
/// El diálogo sigue siendo de cada uno: cada jugador lee su texto.
/// </summary>
internal sealed class QuestNpcSync {
    /// <summary>Estados como mucho que se recorren desde el inicio de una FSM buscando su comprobación.</summary>
    private const int MaxStartStates = 24;

    private static QuestNpcSync _active;
    private static bool _hooked;

    private GameSession _session;

    private string _scene = string.Empty;

    /// <summary>Por dato del mundo, las FSM de la sala que lo miran al empezar.</summary>
    private readonly Dictionary<string, List<PlayMakerFSM>> _startReaders = new();

    /// <summary>
    /// Por dato del mundo, los objetos de la sala que el juego enciende o apaga según él al encenderse o al empezar
    /// (<c>DeactivateIfPlayerdataTrue</c>, <c>DeactivateIfPlayerdataFalse</c>, <c>ActivateIfPlayerdataTrue</c>): el
    /// Forjaguijones, el farol de la Pesadilla...
    /// </summary>
    private readonly Dictionary<string, List<MonoBehaviour>> _toggles = new();

    /// <summary>Datos del mundo que cambiaron en otro PC desde que se cargó esta sala, y cuándo (el último cambio).</summary>
    private readonly Dictionary<string, float> _remoteChangedAt = new();

    /// <summary>Cuándo empezó la conversación (o cinemática) de este jugador: su caballero perdió el control (-1: con control).</summary>
    private float _conversationStart = -1f;

    /// <summary>Valor que tenían los números del mundo que cambiaron en otro PC durante la conversación de este jugador.</summary>
    private readonly Dictionary<string, int> _beforeRemote = new();

    /// <summary>Datos cambiados en otro PC cuyas FSM aún hay que volver a comprobar (se espera a que haya control).</summary>
    private readonly List<string> _pending = new();

    /// <summary>
    /// Sí/no que cambió un compañero que estaba en esta sala: los personajes que lo miran se quedan como en su PC (ver
    /// <see cref="KeptInRoom"/>).
    /// </summary>
    private readonly HashSet<string> _changedHere = new();

    /// <summary>
    /// Componentes <c>DeactivateIfPlayerdata*</c> que apagaron su objeto al encenderse (al cargar la sala): solo esos se
    /// vuelven a encender si el dato cambia. Se apuntan antes de que esta clase vea la sala nueva, así que no se vacía al
    /// cambiar de sala (los de salas descargadas se quitan al buscar los de la nueva).
    /// </summary>
    private static readonly HashSet<MonoBehaviour> TurnedOffAtLoad = new();

    /// <summary>Campos de texto de cada tipo de acción que lee datos de la partida (se calculan una vez por tipo).</summary>
    private static readonly Dictionary<Type, FieldInfo[]> ReadFields = new();

    public void Attach(GameSession session) {
        _active = this;
        _session = session;
        InstallHooks();
        Clear();
    }

    public void Detach() {
        if (_active == this) {
            _active = null;
        }

        _session = null;
        Clear();
    }

    private void Clear() {
        _scene = string.Empty;
        _startReaders.Clear();
        _toggles.Clear();
        _remoteChangedAt.Clear();
        _pending.Clear();
        _changedHere.Clear();
        _conversationStart = -1f;
        _beforeRemote.Clear();
    }

    // =====================================================================================================
    // Cambios del mundo llegados de otro PC
    // =====================================================================================================

    /// <summary>
    /// Otro jugador pagó el ascensor de Cruces a la Ciudad (`Toll Machine Lift` pone `cityLift1` y manda TOLL LIFT PAY solo
    /// en su PC): el ascensor de esta sala que lo espera (`elev_main`, FSM `Lift lower`, estado "Up") baja también aquí.
    /// Antes el compañero lo veía arriba hasta volver a entrar.
    /// </summary>
    private static void CallPaidLift() {
        foreach (var fsm in PlayMakerFSM.FsmList.ToArray()) {
            if (fsm == null || fsm.Fsm?.ActiveState == null || fsm.gameObject.scene.buildIndex < 0) {
                continue;
            }

            foreach (var transition in fsm.Fsm.ActiveState.Transitions) {
                if (transition.EventName == "TOLL LIFT PAY") {
                    Log.Info($"{fsm.gameObject.name}/{fsm.FsmName}: otro jugador pagó el ascensor, baja también aquí");
                    fsm.SendEvent("TOLL LIFT PAY");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// El progreso compartido acaba de poner un dato del mundo con lo que llegó de otro PC (<paramref name="before"/>: el
    /// valor que tenía; en los sí/no, 0 o 1).
    /// </summary>
    public void OnRemoteChange(string field, int before, bool isBool) {
        if (_scene.Length == 0) {
            return;
        }

        if (isBool && before == 0 && field == "cityLift1") {
            CallPaidLift();
        }

        _remoteChangedAt[field] = Time.unscaledTime;
        if (isBool && PartnerInRoom()) {
            _changedHere.Add(field);
        } else {
            _changedHere.Remove(field);
        }

        if (_conversationStart >= 0f && !_beforeRemote.ContainsKey(field)) {
            _beforeRemote[field] = before;
        }

        if ((_startReaders.ContainsKey(field) || _toggles.ContainsKey(field)) && !_pending.Contains(field)) {
            _pending.Add(field);
        }
    }

    /// <summary>Llamar cada frame con la sala actual (vacía fuera de una sala de juego).</summary>
    public void Update(string scene) {
        if (scene != _scene) {
            Clear();
            _scene = scene;
            if (scene.Length > 0) {
                Scan();
            }

            return;
        }

        // La conversación de este jugador empieza cuando su caballero pierde el control y acaba cuando lo recupera
        // Sin control por otra cosa no es una conversación: la presentación de jefe de otro PC (hasta 30 s), el banco. Antes
        // contaba igual y, mientras, una FSM que leía ese dato podía leer el valor viejo o perder una suma de verdad
        var hero = HeroController.instance;
        if (hero != null && hero.controlReqlinquished && !Features.BossPresentation.PartnerLocked &&
            PlayerData.instance is { atBench: false }) {
            if (_conversationStart < 0f) {
                _conversationStart = Time.unscaledTime;
            }
        } else if (_conversationStart >= 0f) {
            _conversationStart = -1f;
            _beforeRemote.Clear();
        }

        if (_pending.Count == 0 || !CanRecheck()) {
            return;
        }

        foreach (var field in _pending) {
            Recheck(field);
        }

        _pending.Clear();
    }

    /// <summary>
    /// Solo con el caballero con control: en una conversación o cinemática de este jugador su personaje no se toca (se
    /// quedaría sin control a mitad del diálogo).
    /// </summary>
    private static bool CanRecheck() {
        var hero = HeroController.instance;
        var gm = GameManager.instance;
        return hero != null && gm != null && !hero.controlReqlinquished && !gm.IsInSceneTransition;
    }

    /// <summary>
    /// Las FSM que miran el dato al empezar vuelven a su estado inicial, como al cargar la sala: con el valor nuevo se van
    /// (Mr. Mushroom se destruye si ya no es su sitio), se apagan o cambian.
    /// </summary>
    private void Recheck(string field) {
        RecheckToggles(field);
        if (!_startReaders.TryGetValue(field, out var fsms)) {
            return;
        }

        foreach (var fsm in fsms) {
            if (fsm == null || !fsm.enabled || !fsm.gameObject.activeInHierarchy || fsm.Fsm == null) {
                continue;
            }

            if (KeptInRoom(field, fsm)) {
                Log.Info($"{fsm.gameObject.name}/{fsm.FsmName}: {field} lo cambió un compañero en esta sala; se queda " +
                         "como en su PC hasta volver a entrar");
                continue;
            }

            var start = fsm.Fsm.StartState;
            Log.Info($"{fsm.gameObject.name}/{fsm.FsmName}: {field} cambió en otro PC, se comprueba otra vez como al " +
                     $"cargar la sala ({fsm.ActiveStateName} → {start})");
            var inner = fsm.Fsm;
            Scene.FsmStateSync.ApplyingSavedState = true;
            FsmExecutionStack.PushFsm(inner);
            try {
                inner.SetState(start);
                inner.UpdateStateChanges();
            } catch (Exception e) {
                Log.Warn($"{fsm.gameObject.name}/{fsm.FsmName}: no se pudo volver a comprobar: {e.Message}");
            } finally {
                FsmExecutionStack.PopFsm();
                Scene.FsmStateSync.ApplyingSavedState = false;
            }
        }
    }

    /// <summary>Los objetos que el juego enciende o apaga según el dato se ponen como al cargar la sala con el valor nuevo.</summary>
    private void RecheckToggles(string field) {
        if (!_toggles.TryGetValue(field, out var toggles) || PlayerData.instance == null) {
            return;
        }

        var value = PlayerData.instance.GetBool(field);
        foreach (var toggle in toggles) {
            if (toggle == null) {
                continue;
            }

            // Lo que el componente apagó al cargar la sala vuelve si con el valor nuevo no lo apagaría (la Myla
            // infectada, apagada sin el Corazón de Cristal); lo que estaba apagado por otra cosa no se toca
            var wasOff = TurnedOffAtLoad.Contains(toggle);
            var target = toggle switch {
                DeactivateIfPlayerdataTrue => value ? false : wasOff ? true : null,
                DeactivateIfPlayerdataFalse => value ? wasOff ? true : null : false,
                ActivateIfPlayerdataTrue => value ? true : (bool?) null,
                _ => null
            };
            var go = toggle.gameObject;
            if (target == null || go.activeSelf == target.Value) {
                continue;
            }

            // Los enemigos solo se encienden (la Myla infectada al conseguir el Corazón de Cristal): su muerte va por la
            // sincronización de enemigos y apagarlo aquí cortaría la del jefe que acaba de morir en el otro PC
            if (!target.Value && Scene.EnemySync.IsEnemyObject(toggle.transform)) {
                continue;
            }

            // Un personaje no se apaga delante de este jugador si el otro habló con él aquí (ver KeptInRoom)
            if (!target.Value && _changedHere.Contains(field) && IsNpcObject(go)) {
                continue;
            }

            Log.Info($"{go.name}: {field} cambió en otro PC, se {(target.Value ? "enciende" : "apaga")} como al cargar la sala");
            TurnedOffAtLoad.Remove(toggle);
            go.SetActive(target.Value);
        }
    }

    /// <summary>
    /// Si el personaje se queda sin volver a comprobarse: el sí/no lo cambió un compañero en esta sala, hablando con su
    /// copia delante de este jugador. En su PC el juego no lo vuelve a mirar hasta cargar la sala (Bretta sigue ahí tras
    /// rescatarla); aquí tampoco, o se iba a mitad de su conversación. La campana del ciervo sí: es lo que el otro abrió.
    /// </summary>
    private bool KeptInRoom(string field, PlayMakerFSM fsm) =>
        _changedHere.Contains(field) && Array.IndexOf(RecheckAlways, fsm.FsmName) < 0 && IsNpc(fsm);

    /// <summary>Si otro jugador está en la misma sala que este.</summary>
    private bool PartnerInRoom() {
        if (_session == null) {
            return false;
        }

        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == _scene) {
                return true;
            }
        }

        return false;
    }

    // =====================================================================================================
    // Qué FSM miran cada dato al empezar
    // =====================================================================================================

    private void Scan() {
        ScanToggles();
        var hero = HeroController.instance;
        var reads = new List<string>();
        foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            if (fsm.Fsm == null || fsm.gameObject.scene.buildIndex < 0 ||
                hero != null && fsm.transform.IsChildOf(hero.transform) ||
                Scene.EnemySync.IsEnemyObject(fsm.transform) || Scene.FsmStateSync.IsSharedController(fsm.Fsm) ||
                Scene.FsmStateSync.CopiesChain(fsm, out _, out _)) {
                // Lo que ya va igual en todos (enemigos, controladores, palancas y lo que copia su cadena) llega de allí
                continue;
            }

            reads.Clear();
            var onlyShows = ReadStart(fsm, reads);
            if (reads.Count == 0 || !onlyShows && !IsNpc(fsm) && Array.IndexOf(RecheckAlways, fsm.FsmName) < 0) {
                continue;
            }

            foreach (var field in reads) {
                // Solo los datos del mundo: los del personaje no llegan de otro PC
                if (CharacterData.IsCharacterField(field)) {
                    continue;
                }

                if (!_startReaders.TryGetValue(field, out var list)) {
                    _startReaders[field] = list = new List<PlayMakerFSM>();
                }

                if (!list.Contains(fsm)) {
                    list.Add(fsm);
                }
            }
        }
    }

    private void ScanToggles() {
        TurnedOffAtLoad.RemoveWhere(toggle => toggle == null);
        foreach (var toggle in UnityEngine.Object.FindObjectsByType<DeactivateIfPlayerdataTrue>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            AddToggle(toggle.boolName, toggle);
        }

        foreach (var toggle in UnityEngine.Object.FindObjectsByType<DeactivateIfPlayerdataFalse>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            AddToggle(toggle.boolName, toggle);
        }

        foreach (var toggle in UnityEngine.Object.FindObjectsByType<ActivateIfPlayerdataTrue>(FindObjectsInactive.Include,
                     FindObjectsSortMode.None)) {
            AddToggle(toggle.boolName, toggle);
        }
    }

    private void AddToggle(string field, MonoBehaviour toggle) {
        // Los enemigos también, pero solo para encenderlos (ver RecheckToggles)
        if (string.IsNullOrEmpty(field) || toggle.gameObject.scene.buildIndex < 0 || CharacterData.IsCharacterField(field)) {
            return;
        }

        if (!_toggles.TryGetValue(field, out var list)) {
            _toggles[field] = list = new List<MonoBehaviour>();
        }

        list.Add(toggle);
    }

    /// <summary>
    /// FSM que no son de un personaje pero se vuelven a comprobar como si lo fueran. La campana de las estaciones del
    /// ciervo (`Stag Bell`) solo se enciende al cargar la sala si la estación está abierta, y al empezar avisa a su cartel
    /// (por eso no contaba como "solo enseña"): abierta por otro jugador, aquí no había campana hasta volver a entrar.
    /// </summary>
    private static readonly string[] RecheckAlways = { "Stag Bell" };

    /// <summary>FSM de un personaje con el que se habla: el suyo, el de algo suyo o el que lo enciende.</summary>
    private static readonly string[] NpcFsms = { "npc_control", "Conversation Control" };

    /// <summary>
    /// Si la FSM es de un personaje (su objeto, uno de sus hijos o su padre directo tienen la FSM de hablar): se vuelve a
    /// comprobar haga lo que haga al empezar.
    /// </summary>
    private static bool IsNpc(PlayMakerFSM fsm) {
        foreach (var other in fsm.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (Array.IndexOf(NpcFsms, other.FsmName) >= 0) {
                return true;
            }
        }

        var parent = fsm.transform.parent;
        if (parent == null) {
            return false;
        }

        foreach (var other in parent.GetComponents<PlayMakerFSM>()) {
            if (Array.IndexOf(NpcFsms, other.FsmName) >= 0) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Acciones que no se repiten al volver a empezar una FSM que no es de un personaje: crean o sueltan objetos, cambian
    /// datos de la partida, avisan a otros objetos, suenan, mueven la cámara o al caballero, guardan o cambian de sala.
    /// Las arenas y puertas que miran datos del mundo al empezar los tienen (volver a empezarlas a mitad de un combate o de
    /// su apertura lo repetiría); el farol de la Compañía o una tienda que aparece, no: solo se enseñan o se esconden.
    /// </summary>
    private static readonly string[] NotOnRecheck = {
        "Spawn", "Create", "Instantiate", "SetPlayerData", "IncrementPlayerData", "DecrementPlayerData", "PlayerDataIntAdd",
        "SendEvent", "SendMessage", "Broadcast", "CallMethod", "Audio", "Music", "Camera", "Hero", "Relinquish",
        "Scene", "Battle", "Save", "Fsm"
    };

    /// <summary>
    /// Datos de la partida que la FSM lee al empezar: en los estados a los que llega sola desde el inicial (con
    /// "FINISHED" o con lo que deciden sus propias acciones), hasta un estado que espera al caballero (una zona, un botón).
    /// Devuelve si en ese camino solo se enseña, esconde o anima (nada de <see cref="NotOnRecheck"/>).
    /// </summary>
    private static bool ReadStart(PlayMakerFSM fsm, List<string> reads) {
        var start = fsm.Fsm.GetState(fsm.Fsm.StartState);
        if (start == null) {
            return false;
        }

        var onlyShows = true;
        var visited = new HashSet<FsmState> { start };
        var queue = new Queue<FsmState>();
        queue.Enqueue(start);
        while (queue.Count > 0 && visited.Count <= MaxStartStates) {
            var state = queue.Dequeue();
            var waits = false;
            foreach (var action in state.Actions) {
                if (action == null) {
                    continue;
                }

                var type = action.GetType().Name;
                waits |= type.StartsWith("Trigger", StringComparison.Ordinal) ||
                         type.StartsWith("ListenFor", StringComparison.Ordinal) ||
                         type.StartsWith("CheckCollision", StringComparison.Ordinal) ||
                         type.StartsWith("CheckAlertRange", StringComparison.Ordinal);
                foreach (var part in NotOnRecheck) {
                    if (type.IndexOf(part, StringComparison.Ordinal) >= 0) {
                        onlyShows = false;
                        break;
                    }
                }

                foreach (var field in PlayerDataReads(action)) {
                    reads.Add(field);
                }
            }

            if (waits) {
                continue;
            }

            foreach (var transition in state.Transitions) {
                var next = fsm.Fsm.GetState(transition.ToState);
                if (next == null || visited.Contains(next) ||
                    transition.EventName != "FINISHED" && !FiresEvent(state, transition.EventName)) {
                    continue;
                }

                visited.Add(next);
                queue.Enqueue(next);
            }
        }

        return onlyShows;
    }

    /// <summary>Si alguna acción del estado decide ese evento (p. ej. el "DESTROY" de una comparación).</summary>
    private static bool FiresEvent(FsmState state, string eventName) {
        foreach (var action in state.Actions) {
            if (action == null) {
                continue;
            }

            foreach (var field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                if (field.FieldType == typeof(FsmEvent) && field.GetValue(action) is FsmEvent fsmEvent &&
                    fsmEvent.Name == eventName) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Nombres de datos de la partida que lee una acción (las que comparan o leen, no las que cambian).</summary>
    private static IEnumerable<string> PlayerDataReads(FsmStateAction action) {
        var fields = ReadFieldsOf(action.GetType());
        foreach (var field in fields) {
            switch (field.GetValue(action)) {
                case FsmString { Value: { Length: > 0 } name }:
                    yield return name;
                    break;
                case string { Length: > 0 } plain:
                    yield return plain;
                    break;
                case FsmString[] names:
                    foreach (var item in names) {
                        if (item?.Value is { Length: > 0 } value) {
                            yield return value;
                        }
                    }

                    break;
            }
        }
    }

    private static FieldInfo[] ReadFieldsOf(Type type) {
        if (ReadFields.TryGetValue(type, out var fields)) {
            return fields;
        }

        var name = type.Name;
        var reads = name.Contains("PlayerData") && !name.StartsWith("Set", StringComparison.Ordinal) &&
                    !name.StartsWith("Increment", StringComparison.Ordinal) &&
                    !name.StartsWith("Decrement", StringComparison.Ordinal) && !name.EndsWith("Add", StringComparison.Ordinal);
        fields = reads
            ? Array.FindAll(type.GetFields(BindingFlags.Public | BindingFlags.Instance),
                f => f.FieldType == typeof(FsmString) || f.FieldType == typeof(string) || f.FieldType == typeof(FsmString[]))
            : Array.Empty<FieldInfo>();
        ReadFields[type] = fields;
        return fields;
    }

    // =====================================================================================================
    // La misma conversación no avanza la misión dos veces
    // =====================================================================================================

    /// <summary>
    /// Se llama al cargar el mod (y en Attach por si acaso): `PlayerData.GetBool`, `GetInt` y `SetInt` son de una línea, y si
    /// quien los llama ya se compiló (tras una partida normal en el mismo proceso) con el método copiado dentro, el gancho
    /// puesto después no se ve: volvían los fallos de misiones que avanzan dos veces, solo en ese caso.
    /// </summary>
    internal static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        // En el registro: un personaje de misión que su comprobación quita, y por qué dato. En la partida real a uno le
        // salía Quirrel y al otro no, y el registro no decía nada
        On.HutongGames.PlayMaker.Actions.PlayerDataBoolTest.OnEnter += (orig, self) => {
            orig(self);
            if (_active != null) {
                NoteRemoval(self);
            }
        };
        On.DeactivateIfPlayerdataTrue.OnEnable += (orig, self) => {
            orig(self);
            NoteTurnedOff(self);
        };
        On.DeactivateIfPlayerdataFalse.OnEnable += (orig, self) => {
            orig(self);
            NoteTurnedOff(self);
        };
        On.PlayerData.IncrementInt += (orig, self, name) => {
            if (_active == null || !_active.AlreadyAdvanced(name)) {
                orig(self, name);
            }
        };
        On.PlayerData.DecrementInt += (orig, self, name) => {
            if (_active == null || !_active.AlreadyAdvanced(name)) {
                orig(self, name);
            }
        };
        On.PlayerData.IntAdd += (orig, self, name, amount) => {
            if (_active == null || amount == 0 || !_active.AlreadyAdvanced(name)) {
                orig(self, name, amount);
            }
        };
        On.PlayerData.SetInt += (orig, self, name, value) => {
            // Poner el mismo valor no avanza nada
            if (_active == null || !_active.ChangedDuringConversation(name) || self.GetIntInternal(name) == value ||
                !_active.AlreadyAdvanced(name)) {
                orig(self, name, value);
            }
        };
        On.PlayerData.GetInt += (orig, self, name) =>
            _active != null && _active.StaleRead(name, out var before) ? before : orig(self, name);

        // Igual los sí/no: con los dos en la escena de Hornet en la fuente, el que acababa segundo leía a mitad de la
        // suya "ya encontrada" (hornetFountainEncounter, puesto por el otro), tomaba la rama de después y se quedaba sin
        // control
        On.PlayerData.GetBool += (orig, self, name) =>
            _active != null && _active.StaleRead(name, out var before) ? before != 0 : orig(self, name);
    }

    /// <summary>El componente se encendió con su objeto y lo apagó: se apunta (ver <see cref="TurnedOffAtLoad"/>).</summary>
    private static void NoteTurnedOff(MonoBehaviour toggle) {
        if (_active != null && toggle != null && !toggle.gameObject.activeSelf) {
            TurnedOffAtLoad.Add(toggle);
            var field = toggle switch {
                DeactivateIfPlayerdataTrue t => t.boolName,
                DeactivateIfPlayerdataFalse f => f.boolName,
                _ => null
            };
            if (!string.IsNullOrEmpty(field) && IsNpcObject(toggle.gameObject)) {
                LogRemoval(toggle.gameObject.name, toggle.GetType().Name, field);
            }
        }
    }

    /// <summary>Si la comprobación (un sí/no de la partida) manda a la FSM de un personaje a quitarse, se apunta.</summary>
    private static void NoteRemoval(HutongGames.PlayMaker.Actions.PlayerDataBoolTest action) {
        var field = action.boolName?.Value;
        var fsm = action.Fsm;
        if (string.IsNullOrEmpty(field) || PlayerData.instance == null || fsm?.Owner is not PlayMakerFSM owner ||
            action.State == null) {
            return;
        }

        var sent = PlayerData.instance.GetBool(field) ? action.isTrue : action.isFalse;
        if (sent == null || !LeadsToRemoval(action.State, sent) || !IsNpc(owner)) {
            return;
        }

        LogRemoval(owner.gameObject.name, owner.FsmName, field);
    }

    private static void LogRemoval(string name, string by, string field) {
        var value = PlayerData.instance != null && PlayerData.instance.GetBool(field);
        var whose = CharacterData.IsCharacterField(field) ? "de este jugador" : "del mundo";
        Log.Info($"{name}/{by}: el personaje se quita en esta sala por {field}={value} (dato {whose})");
    }

    /// <summary>Si ese aviso lleva a destruir o apagar el objeto (el estado "Destroy" de los personajes).</summary>
    private static bool LeadsToRemoval(FsmState state, FsmEvent sent) {
        if (sent.Name.IndexOf("DESTROY", StringComparison.OrdinalIgnoreCase) >= 0) {
            return true;
        }

        foreach (var transition in state.Transitions) {
            if (transition.FsmEvent == sent && transition.ToState != null &&
                (transition.ToState.IndexOf("Destroy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 transition.ToState.IndexOf("Deactivate", StringComparison.OrdinalIgnoreCase) >= 0)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Si el objeto es de un personaje con el que se habla (él, un hijo o su padre tienen la FSM de hablar).</summary>
    private static bool IsNpcObject(GameObject go) {
        foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (Array.IndexOf(NpcFsms, fsm.FsmName) >= 0) {
                return true;
            }
        }

        var parent = go.transform.parent;
        return parent != null && Array.Exists(parent.GetComponents<PlayMakerFSM>(), f => Array.IndexOf(NpcFsms, f.FsmName) >= 0);
    }

    /// <summary>
    /// La conversación de este jugador con su copia del personaje lee el número como estaba al empezarla, antes del avance
    /// que hizo el otro jugador mientras tanto: así dice lo mismo que le dijo al otro (Mr. Mushroom, la pista de su
    /// siguiente sitio y no la del sitio de después).
    /// </summary>
    private bool StaleRead(string field, out int before) {
        before = 0;
        if (_beforeRemote.Count == 0 || field == null || !_beforeRemote.TryGetValue(field, out before)) {
            return false;
        }

        var fsm = FsmExecutionStack.ExecutingFsm;
        return HeroController.instance is { controlReqlinquished: true } && fsm?.Owner != null && Concerns(fsm, field);
    }

    /// <summary>
    /// Si un cambio de un número del mundo que hace ahora la lógica de un personaje ya lo hizo otro jugador: este jugador
    /// está en una conversación (sin control), el número cambió en otro PC después de que empezara y lo mira la FSM que lo
    /// cambia (o una del mismo personaje al empezar). Es la misma conversación con su copia del personaje, que ya avanzó
    /// la misión allí. Un cambio de otro PC de antes de la conversación no cuenta: hablar otra vez con un personaje que se
    /// queda (los preceptos de Zote) sigue avanzando.
    /// </summary>
    private bool AlreadyAdvanced(string field) {
        if (!ChangedDuringConversation(field)) {
            return false;
        }

        var fsm = FsmExecutionStack.ExecutingFsm;
        if (fsm?.Owner == null || !Concerns(fsm, field)) {
            return false;
        }

        Log.Info($"{fsm.GameObjectName}/{fsm.Name}: {field} ya lo avanzó otro jugador mientras este hablaba; no se " +
                 "avanza otra vez");
        return true;
    }

    /// <summary>Si el dato cambió en otro PC durante la conversación de este jugador (sin control).</summary>
    private bool ChangedDuringConversation(string field) =>
        _conversationStart >= 0f && field != null && _remoteChangedAt.TryGetValue(field, out var at) &&
        at >= _conversationStart && HeroController.instance is { controlReqlinquished: true };

    /// <summary>Si la FSM lee el dato en algún estado, o lo mira al empezar otra FSM del mismo objeto.</summary>
    private bool Concerns(Fsm fsm, string field) {
        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                if (action == null) {
                    continue;
                }

                foreach (var name in PlayerDataReads(action)) {
                    if (name == field) {
                        return true;
                    }
                }
            }
        }

        if (!_startReaders.TryGetValue(field, out var fsms)) {
            return false;
        }

        var owner = fsm.GameObject;
        foreach (var reader in fsms) {
            if (reader != null && reader.gameObject == owner) {
                return true;
            }
        }

        return false;
    }

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    public IEnumerable<string> Describe() {
        foreach (var pair in _startReaders) {
            var names = new List<string>();
            foreach (var fsm in pair.Value) {
                if (fsm != null) {
                    names.Add($"{fsm.gameObject.name}/{fsm.FsmName}:{fsm.ActiveStateName}");
                }
            }

            yield return $"{pair.Key}{(_remoteChangedAt.ContainsKey(pair.Key) ? " (cambió en otro PC)" : "")} -> " +
                         string.Join(", ", names);
        }

        foreach (var pair in _toggles) {
            var names = new List<string>();
            foreach (var toggle in pair.Value) {
                if (toggle != null) {
                    names.Add($"{toggle.gameObject.name}[{toggle.GetType().Name}]{(toggle.gameObject.activeSelf ? "" : " (apagado)")}");
                }
            }

            yield return $"{pair.Key} -> " + string.Join(", ", names);
        }
    }
}
