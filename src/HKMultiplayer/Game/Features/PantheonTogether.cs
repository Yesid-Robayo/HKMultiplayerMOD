using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Entrar juntos en un Panteón: cuando un jugador empieza uno en su puerta del Atrio de Godhome, los demás de la sala
/// empiezan la misma secuencia de jefes, con las mismas ataduras, y entran con él. Antes cada uno entraba por su cuenta
/// (el viaje es de tipo "GodsAndGlory", que no es un sueño y <see cref="DreamSync"/> no lo sigue): el que empezaba
/// peleaba solo y los demás se quedaban en el Atrio.
/// <para>
/// Dentro, cada PC lleva su propia secuencia y pasa al siguiente jefe al acabar su sala (el juego). Para que sigan
/// juntos, la sala que acaba en un PC acaba también en los demás que están en ella: la de descanso acaba al cruzar su
/// estatua, y el que no la cruzaba se quedaba allí mientras el otro peleaba solo el resto del Panteón. Lo que se gana
/// (la puerta completada, las ataduras) es de cada jugador.
/// </para>
/// <para>
/// Lo mismo con las estatuas del Salón de los Dioses: quien elige un nivel en una estatua (<c>BossChallengeUI.LoadBoss</c>)
/// avisa a la sala, y cada compañero libre entra en la misma sala del jefe como si lo hubiera elegido en su estatua: vuelve
/// a ella al acabar y el nivel completado se apunta en la suya.
/// </para>
/// </summary>
internal sealed class PantheonTogether {
    /// <summary>Un jugador empieza un Panteón en esta sala (→ la sala): puerta y ataduras.</summary>
    public const MessageType PantheonStart = (MessageType) 113;

    /// <summary>La sala de un Panteón acabó en este PC (→ la sala): los demás pasan con él al siguiente jefe.</summary>
    public const MessageType PantheonSceneEnd = (MessageType) 114;

    /// <summary>Un jugador empieza el combate de una estatua de esta sala (→ la sala): estatua, nivel, sala del jefe.</summary>
    public const MessageType StatueStart = (MessageType) 121;

    /// <summary>
    /// Este jugador terminó la sala de entrada de un Panteón (→ todos): ya puede pasar al primer jefe. Va a todos porque los
    /// que esperan están en esa sala y el que lo envía puede estar en otra.
    /// </summary>
    public const MessageType EntranceReady = (MessageType) 131;

    /// <summary>Puerta por la que se entra en la primera sala de la secuencia (la de la FSM del desafío).</summary>
    private const string EntryGate = "door_dreamEnter";

    /// <summary>
    /// La sala de entrada de los Panteones (<c>GG_Boss_Door_Entrance</c>): la puerta lleva siempre allí, con el primer jefe
    /// en la variable <c>firstBossScene</c>; esa sala pasa de largo o, la primera vez tras completar Panteones, enseña el
    /// texto del Buscador de Dioses, y luego viaja al primer jefe.
    /// </summary>
    private const string EntranceScene = "GG_Boss_Door_Entrance";

    private const string FirstBossVariable = "firstBossScene";

    /// <summary>Lo más que se espera a los compañeros en la sala de entrada (uno que se desconectó no la deja colgada).</summary>
    private const float EntranceMaxWait = 60f;

    /// <summary>Quién empezó el Panteón en el que se entra ahora (0: ninguno).</summary>
    private ushort _starter;

    /// <summary>Los jugadores que ya acabaron su sala de entrada en este Panteón.</summary>
    private readonly System.Collections.Generic.HashSet<ushort> _entranceReady = new();

    /// <summary>Pasando ya al primer jefe tras la espera: el viaje no se retiene otra vez.</summary>
    private static bool _leavingEntrance;

    private static PantheonTogether _active;
    private static bool _hooked;

    private readonly MessageHandler _onStart;
    private readonly MessageHandler _onSceneEnd;
    private readonly MessageHandler _onStatue;
    private readonly MessageHandler _onEntranceReady;

    /// <summary>Entrando en una estatua porque la eligió otro jugador: no se vuelve a avisar.</summary>
    private static bool _statueFromRemote;
    private GameSession _session;

    /// <summary>Acabando la sala porque acabó en otro PC: no se vuelve a avisar.</summary>
    private static bool _endingFromRemote;

    static PantheonTogether() {
        MessageRoutes.Register(PantheonStart, Route.SameScene, reliable: true);
        MessageRoutes.Register(PantheonSceneEnd, Route.SameScene, reliable: true);
        MessageRoutes.Register(StatueStart, Route.SameScene, reliable: true);
        MessageRoutes.Register(EntranceReady, Route.Everyone, reliable: true);
    }

    public PantheonTogether() {
        _onStart = OnStart;
        _onSceneEnd = OnSceneEnd;
        _onStatue = OnStatue;
        _onEntranceReady = OnEntranceReady;
    }

    public void Attach(GameSession session) {
        _session = session;
        _active = this;
        session.On(PantheonStart, _onStart);
        session.On(PantheonSceneEnd, _onSceneEnd);
        session.On(StatueStart, _onStatue);
        session.On(EntranceReady, _onEntranceReady);
    }

    public void Detach() {
        _session?.On(PantheonStart, null);
        _session?.On(PantheonSceneEnd, null);
        _session?.On(StatueStart, null);
        _session?.On(EntranceReady, null);
        _session = null;
        if (_active == this) {
            _active = null;
        }
    }

    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        // Volver al Atrio por la puerta de un Panteón (al morir dentro): el juego entra por "door_dreamReturn" y luego la
        // cambia por la de esa puerta (bossReturnEntryGate), pero la puerta solo enciende su lado abierto (donde está la
        // vuelta) si el Panteón está desbloqueado o si se entra por ella, y lo mira antes del cambio. Jugando solo siempre
        // lo está; aquí se entra con el compañero que lo empezó, y quien no lo tenía desbloqueado no encontraba la puerta:
        // se quedaba tumbado entrando en la sala para siempre y el juego lo seguía dando por dentro del Panteón
        On.BossSequenceDoor.IsEnteringFromGate += (orig, self) => {
            if (orig(self)) {
                return true;
            }

            var gm = GameManager.instance;
            var pd = PlayerData.instance;
            return _active != null && gm != null && pd != null && self.dreamReturnGate != null &&
                   gm.GetEntryGateName() == "door_dreamReturn" && pd.GetString("bossReturnEntryGate") == self.dreamReturnGate.name;
        };

        // El botón de empezar del cuadro de la puerta: la secuencia ya está preparada al volver
        On.BossDoorChallengeUI.Begin += (orig, self) => {
            orig(self);
            _active?.OnLocalBegin(self);
        };

        // Elegir un nivel en una estatua del Salón de los Dioses
        On.BossChallengeUI.LoadBoss_int_bool += (orig, self, level, doHideAnim) => {
            orig(self, level, doHideAnim);
            if (!_statueFromRemote) {
                _active?.OnLocalStatue(self, level);
            }
        };

        // Fin de la sala de un jefe (todos muertos)
        On.BossSceneController.EndBossScene += (orig, self) => {
            orig(self);
            if (!_endingFromRemote && BossSequenceController.IsInSequence) {
                _active?.SendSceneEnd();
            }
        };

        // La sala de descanso no acaba así: se sale por su puerta propia (customExitPoint, junto a la estatua), que el
        // juego apunta al siguiente jefe
        On.GameManager.BeginSceneTransition += (orig, self, info) => {
            // Salir de la sala de entrada hacia el primer jefe: se espera a los compañeros que aún no acabaron la suya
            if (!_leavingEntrance && _active != null && info != null && self.sceneName == EntranceScene &&
                info.SceneName == StaticVariableList.GetValue<string>(FirstBossVariable) && _active.HoldEntrance(self, info)) {
                return;
            }

            var exit = BossSceneController.Instance != null ? BossSceneController.Instance.customExitPoint : null;
            if (!_endingFromRemote && BossSequenceController.IsInSequence && exit != null && info != null &&
                info.SceneName == exit.targetScene) {
                _active?.SendSceneEnd();
            }

            orig(self, info);
        };
    }

    /// <summary>
    /// Este PC acabó su sala de entrada y va al primer jefe: avisa a todos y, si algún compañero aún no acabó la suya (lee
    /// el texto del Buscador, o es quien empezó y todavía está en la animación de la puerta), espera en esa sala a que
    /// acaben. Antes el compañero llegaba solo al primer jefe y peleaba sin el otro hasta que este llegaba.
    /// </summary>
    private bool HoldEntrance(GameManager gm, GameManager.SceneLoadInfo info) {
        if (_session == null || !_session.CanSend) {
            return false;
        }

        _entranceReady.Add(_session.LocalId);
        var writer = _session.Begin(EntranceReady);
        writer.WriteString(info.SceneName);
        _session.End();
        if (WaitingFor(info.SceneName) == null) {
            return false;
        }

        gm.StartCoroutine(WaitAtEntrance(gm, info));
        return true;
    }

    /// <summary>El compañero al que aún hay que esperar en la sala de entrada, o null.</summary>
    private SessionPlayer WaitingFor(string firstBoss) {
        if (_session == null) {
            return null;
        }

        foreach (var player in _session.Players) {
            if (player.IsLocal || _entranceReady.Contains(player.Id) || player.Scene == firstBoss) {
                continue;
            }

            // Uno que está en su sala de entrada, o quien empezó el Panteón y aún no llegó a ella (animación de la puerta)
            if (player.Scene == EntranceScene || player.Id == _starter) {
                return player;
            }
        }

        return null;
    }

    private System.Collections.IEnumerator WaitAtEntrance(GameManager gm, GameManager.SceneLoadInfo info) {
        var since = Time.unscaledTime;
        var shown = false;
        SessionPlayer waiting;
        while ((waiting = WaitingFor(info.SceneName)) != null && Time.unscaledTime - since < EntranceMaxWait &&
               gm.sceneName == EntranceScene) {
            if (!shown) {
                shown = true;
                Log.Info($"Sala de entrada del Panteón: se espera a {waiting.Name} para ir juntos al primer jefe");
                Notice.Show(Lang.F("Esperando a {0} para empezar el combate", "Waiting for {0} to start the fight", waiting.Name), 4f);
            }

            yield return null;
        }

        if (gm.sceneName != EntranceScene) {
            yield break;
        }

        if (waiting != null) {
            Log.Warn($"Sala de entrada del Panteón: {waiting.Name} no llegó en {EntranceMaxWait:0} s; se pasa al primer jefe");
        }

        _leavingEntrance = true;
        try {
            gm.BeginSceneTransition(info);
        } finally {
            _leavingEntrance = false;
        }
    }

    private void OnEntranceReady(SessionPlayer from, PacketReader reader) {
        reader.ReadString(128);
        _entranceReady.Add(from.Id);
    }

    private void SendSceneEnd() {
        var scene = GameManager.instance != null ? GameManager.instance.sceneName : null;
        if (_session == null || !_session.CanSend || scene == null || !AnyoneElseHere()) {
            return;
        }

        var writer = _session.Begin(PantheonSceneEnd);
        writer.WriteString(scene);
        _session.End();
    }

    private void OnSceneEnd(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var gm = GameManager.instance;
        var controller = BossSceneController.Instance;
        if (gm == null || gm.sceneName != scene || !BossSequenceController.IsInSequence || controller == null ||
            HeroController.instance == null || HeroController.instance.cState.dead) {
            return;
        }

        // El final del Panteón de Hallownest lo sigue cada PC por su cuenta: cada uno acaba la sala al terminarlo
        if (Scene.EnemySync.FinaleRunning) {
            Log.Info($"{from.Name} acabó {scene} del Panteón: este PC sigue con su final");
            return;
        }

        Log.Info($"{from.Name} acabó {scene} del Panteón: este PC pasa con él al siguiente");
        var exit = controller.customExitPoint;
        if (exit != null) {
            WalkInto(exit);
            return;
        }

        _endingFromRemote = true;
        try {
            controller.EndBossScene();
        } finally {
            _endingFromRemote = false;
        }
    }

    /// <summary>
    /// Pone al caballero dentro de la puerta de salida mirando hacia ella: el juego la cruza como si llegara andando (la
    /// puerta empuja hacia atrás al que la toca de espaldas).
    /// </summary>
    private static void WalkInto(TransitionPoint exit) {
        var hero = HeroController.instance;
        var area = exit.GetComponent<Collider2D>();
        if (hero == null || area == null || hero.cState.transitioning) {
            return;
        }

        if (exit.GetGatePosition() == GlobalEnums.GatePosition.left) {
            hero.FaceLeft();
        } else {
            hero.FaceRight();
        }

        var center = area.bounds.center;
        hero.transform.position = new Vector3(center.x, center.y, hero.transform.position.z);
    }

    private static readonly System.Reflection.FieldInfo StatueField =
        typeof(BossChallengeUI).GetField("bossStatue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private void OnLocalStatue(BossChallengeUI ui, int level) {
        if (StatueField?.GetValue(ui) is not BossStatue statue || _session == null || !_session.CanSend || !AnyoneElseHere()) {
            return;
        }

        var bossScene = StaticVariableList.GetValue<string>("bossSceneToLoad");
        if (string.IsNullOrEmpty(bossScene)) {
            return;
        }

        Log.Info($"Estatua {statue.name} (nivel {level}, {bossScene}): los compañeros de la sala entran también");
        var writer = _session.Begin(StatueStart);
        writer.WriteString(statue.gameObject.scene.name);
        writer.WriteString(statue.name);
        writer.WriteByte((byte) level);
        writer.WriteBool(statue.UsingDreamVersion);
        writer.WriteString(bossScene);
        _session.End();
    }

    private void OnStatue(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var statueName = reader.ReadString(128);
        var level = reader.ReadByte();
        var dream = reader.ReadBool();
        var bossSceneName = reader.ReadString(128);
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        if (gm == null || hero == null || pd == null || gm.sceneName != scene) {
            return;
        }

        var statue = FindStatue(scene, statueName);
        var bossScene = statue != null ? dream ? statue.dreamBossScene : statue.bossScene : null;
        var why = statue == null || bossScene == null || statue.dreamReturnGate == null ? "aquí no está esa estatua"
            : level > 2 || !IsSceneOf(bossScene, bossSceneName) ? "sala de jefe que no es de esa estatua"
            : !Application.CanStreamedLevelBeLoaded(bossSceneName) ? "no existe esa sala"
            : BossSequenceController.IsInSequence ? "está en un Panteón"
            : PartyState.IsDown(_session.LocalId) ? "caído"
            : hero.cState.dead || hero.cState.transitioning || gm.IsInSceneTransition ? "cambiando de sala"
            : pd.atBench && hero.controlReqlinquished ? "sentado en un banco"
            : gm.isPaused ? "en pausa"
            : hero.controlReqlinquished ? "ocupado"
            : null;
        if (why != null) {
            Log.Info($"{from.Name} empezó la estatua {statueName}; este jugador no entra ({why})");
            Notice.Show(Lang.F("{0} se enfrenta a una estatua", "{0} is fighting a statue", from.Name), 4f);
            return;
        }

        Log.Info($"{from.Name} empezó la estatua {statueName} (nivel {level}): este jugador entra con él");
        EnterStatue(statue, bossScene, bossSceneName, level, dream);
        Notice.Show(Lang.F("Estatua con {0}", "Statue with {0}", from.Name), 4f);
    }

    private static bool IsSceneOf(BossScene bossScene, string sceneName) =>
        sceneName == bossScene.sceneName || sceneName == bossScene.Tier1Scene || sceneName == bossScene.Tier2Scene ||
        sceneName == bossScene.Tier3Scene;

    private static BossStatue FindStatue(string scene, string statueName) {
        foreach (var candidate in Object.FindObjectsByType<BossStatue>(FindObjectsSortMode.None)) {
            if (candidate.name == statueName && candidate.gameObject.scene.name == scene) {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Lo mismo que elegir el nivel en la estatua (<c>BossChallengeUI.LoadBoss</c>) y lo que hace después su FSM
    /// <c>GG Boss UI</c>: la sala a la que se vuelve, la puerta de la estatua, el nivel completado al ganar y el viaje.
    /// </summary>
    private static void EnterStatue(BossStatue statue, BossScene bossScene, string sceneName, int level, bool dream) {
        var gm = GameManager.instance;
        var pd = PlayerData.instance;
        StaticVariableList.SetValue("bossSceneToLoad", sceneName);
        BossStatueLoadManager.RecordBossScene(bossScene);
        pd.SetString("bossReturnEntryGate", statue.dreamReturnGate.name);
        pd.SetString("dreamReturnScene", statue.gameObject.scene.name);
        var field = dream ? statue.dreamStatueStatePD : statue.statueStatePD;
        BossSceneController.SetupEvent = self => {
            self.BossLevel = level;
            self.DreamReturnEvent = "DREAM RETURN";
            self.OnBossesDead += () => {
                var completion = GameManager.instance.GetPlayerDataVariable<BossStatue.Completion>(field);
                switch (level) {
                    case 0:
                        completion.completedTier1 = true;
                        break;
                    case 1:
                        completion.completedTier2 = true;
                        break;
                    case 2:
                        completion.completedTier3 = true;
                        break;
                }

                GameManager.instance.SetPlayerDataVariable(field, completion);
                GameManager.instance.playerData.SetString("currentBossStatueCompletionKey", field);
                GameManager.instance.playerData.SetInt("bossStatueTargetLevel", level);
            };
            self.OnBossSceneComplete += () => self.DoDreamReturn();
        };

        _statueFromRemote = true;
        try {
            HeroController.instance.RelinquishControl();
            CoverWithWhite();
            gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
                SceneName = sceneName,
                EntryGateName = EntryGate,
                EntryDelay = 0f,
                Visualization = GameManager.SceneLoadVisualizations.GodsAndGlory,
                PreventCameraFadeOut = true,
                WaitForSceneTransitionCameraFade = true,
                AlwaysUnloadUnusedAssets = false
            });
        } finally {
            _statueFromRemote = false;
        }
    }

    private static readonly System.Reflection.FieldInfo DoorField =
        typeof(BossDoorChallengeUI).GetField("door", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Empieza un Panteón (lo empezó ese jugador): se olvida quién acabó la sala de entrada del anterior.</summary>
    private void BeginRun(ushort starter) {
        _starter = starter;
        _entranceReady.Clear();
    }

    private void OnLocalBegin(BossDoorChallengeUI ui) {
        if (_session != null) {
            BeginRun(_session.LocalId);
        }

        if (DoorField?.GetValue(ui) is BossSequenceDoor door) {
            SendStart(door, (BossSequenceController.BoundNail ? BossSequenceController.ChallengeBindings.Nail : 0) |
                            (BossSequenceController.BoundShell ? BossSequenceController.ChallengeBindings.Shell : 0) |
                            (BossSequenceController.BoundCharms ? BossSequenceController.ChallengeBindings.Charms : 0) |
                            (BossSequenceController.BoundSoul ? BossSequenceController.ChallengeBindings.Soul : 0));
        }
    }

    /// <summary>Empieza en este PC el Panteón de esa puerta, como su botón, y avisa a la sala.</summary>
    public bool TestStart(string doorName) {
        var door = FindDoor(GameManager.instance != null ? GameManager.instance.sceneName : string.Empty, doorName);
        if (door == null || door.bossSequence == null || door.bossSequence.Count == 0) {
            return false;
        }

        if (_session != null) {
            BeginRun(_session.LocalId);
        }

        SendStart(door, BossSequenceController.ChallengeBindings.None);
        Enter(door, BossSequenceController.ChallengeBindings.None);
        return true;
    }

    private void SendStart(BossSequenceDoor door, BossSequenceController.ChallengeBindings bindings) {
        if (_session == null || !_session.CanSend || !AnyoneElseHere()) {
            return;
        }

        Log.Info($"Panteón de {door.name} (ataduras {bindings}): los compañeros de la sala entran también");
        var writer = _session.Begin(PantheonStart);
        writer.WriteString(door.gameObject.scene.name);
        writer.WriteString(door.name);
        writer.WriteByte((byte) bindings);
        _session.End();
    }

    private bool AnyoneElseHere() {
        var scene = GameManager.instance != null ? GameManager.instance.sceneName : null;
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == scene) {
                return true;
            }
        }

        return false;
    }

    private void OnStart(SessionPlayer from, PacketReader reader) {
        var scene = reader.ReadString(128);
        var doorName = reader.ReadString(128);
        var bindings = (BossSequenceController.ChallengeBindings) reader.ReadByte();
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        if (gm == null || hero == null || pd == null || gm.sceneName != scene) {
            return;
        }

        var door = FindDoor(scene, doorName);
        // Como el ciervo y los sueños: no se lleva a quien está ocupado
        var why = door == null || door.bossSequence == null || door.bossSequence.Count == 0 ? "aquí no está esa puerta"
            : BossSequenceController.IsInSequence ? "ya está en un Panteón"
            : PartyState.IsDown(_session.LocalId) ? "caído"
            : hero.cState.dead || hero.cState.transitioning || gm.IsInSceneTransition ? "cambiando de sala"
            : pd.atBench && hero.controlReqlinquished ? "sentado en un banco"
            : gm.isPaused ? "en pausa"
            : hero.controlReqlinquished ? "ocupado"
            : null;
        if (why != null) {
            Log.Info($"{from.Name} empezó un Panteón; este jugador no entra ({why})");
            Notice.Show(Lang.F("{0} entró en un Panteón", "{0} entered a Pantheon", from.Name), 4f);
            return;
        }

        Log.Info($"{from.Name} empezó el Panteón de {doorName}: este jugador entra con él (ataduras {bindings})");
        BeginRun(from.Id);
        Enter(door, bindings);
        Notice.Show(Lang.F("Panteón con {0}", "Pantheon with {0}", from.Name), 4f);
    }

    private const string WhiteBlanker = "HUD Blanker White";

    /// <summary>
    /// Como el juego entre los jefes del Panteón (la salida de cada sala): la pantalla se cubre de blanco al momento y el
    /// viaje no funde la cámara a negro; en la sala del jefe se destapa el blanco. Sin esto la cámara fundía a negro y se
    /// quedaba esperando su "FADE OUT COMPLETE", que en ese viaje no llega: los compañeros que entraban con el que empezó
    /// se quedaban con la pantalla negra.
    /// </summary>
    private static void CoverWithWhite() {
        var blanker = HutongGames.PlayMaker.FsmVariables.GlobalVariables.FindFsmGameObject(WhiteBlanker)?.Value;
        var control = blanker != null ? PlayMakerFSM.FindFsmOnGameObject(blanker, "Blanker Control") : null;
        if (control != null) {
            var fadeTime = control.FsmVariables.FindFsmFloat("Fade Time");
            if (fadeTime != null) {
                fadeTime.Value = 0f;
            }

            control.SendEvent("FADE IN");
        }

        var cameraFade = GameCameras.instance != null ? GameCameras.instance.cameraFadeFSM : null;
        var noFade = cameraFade != null ? cameraFade.FsmVariables.FindFsmBool("No Fade") : null;
        if (noFade != null) {
            noFade.Value = true;
        }
    }

    private static BossSequenceDoor FindDoor(string scene, string doorName) {
        foreach (var candidate in Object.FindObjectsByType<BossSequenceDoor>(FindObjectsSortMode.None)) {
            if (candidate.name == doorName && candidate.gameObject.scene.name == scene) {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Lo mismo que el botón de empezar: la puerta a la que se vuelve, las ataduras, la secuencia y el viaje, por la sala de
    /// entrada como la FSM de la puerta (deja el primer jefe en <c>firstBossScene</c>). Antes se iba directo al primer
    /// jefe: se llegaba solo, antes que quien lo empezó, y sin el texto del Buscador de Dioses.
    /// </summary>
    private static void Enter(BossSequenceDoor door, BossSequenceController.ChallengeBindings bindings) {
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        // La sala y la puerta a las que se vuelve al acabar (lo pone la FSM del desafío de la puerta: sin la sala, se
        // volvía a la del último sueño)
        pd.SetString("dreamReturnScene", door.gameObject.scene.name);
        pd.SetString("bossReturnEntryGate", door.dreamReturnGate.name);
        BossSequenceController.SetupNewSequence(door.bossSequence, bindings, door.playerDataString);
        StaticVariableList.SetValue(FirstBossVariable, door.bossSequence.GetSceneAt(0));
        hero.RelinquishControl();
        CoverWithWhite();
        gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = EntranceScene,
            EntryGateName = EntryGate,
            EntryDelay = 0f,
            Visualization = GameManager.SceneLoadVisualizations.GodsAndGlory,
            PreventCameraFadeOut = true,
            WaitForSceneTransitionCameraFade = true,
            AlwaysUnloadUnusedAssets = false
        });
    }
}
