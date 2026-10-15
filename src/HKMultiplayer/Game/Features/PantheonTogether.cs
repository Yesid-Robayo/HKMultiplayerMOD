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
/// </summary>
internal sealed class PantheonTogether {
    /// <summary>Un jugador empieza un Panteón en esta sala (→ la sala): puerta y ataduras.</summary>
    public const MessageType PantheonStart = (MessageType) 113;

    /// <summary>La sala de un Panteón acabó en este PC (→ la sala): los demás pasan con él al siguiente jefe.</summary>
    public const MessageType PantheonSceneEnd = (MessageType) 114;

    /// <summary>Puerta por la que se entra en la primera sala de la secuencia (la de la FSM del desafío).</summary>
    private const string EntryGate = "door_dreamEnter";

    private static PantheonTogether _active;
    private static bool _hooked;

    private readonly MessageHandler _onStart;
    private readonly MessageHandler _onSceneEnd;
    private GameSession _session;

    /// <summary>Acabando la sala porque acabó en otro PC: no se vuelve a avisar.</summary>
    private static bool _endingFromRemote;

    static PantheonTogether() {
        MessageRoutes.Register(PantheonStart, Route.SameScene, reliable: true);
        MessageRoutes.Register(PantheonSceneEnd, Route.SameScene, reliable: true);
    }

    public PantheonTogether() {
        _onStart = OnStart;
        _onSceneEnd = OnSceneEnd;
    }

    public void Attach(GameSession session) {
        _session = session;
        _active = this;
        session.On(PantheonStart, _onStart);
        session.On(PantheonSceneEnd, _onSceneEnd);
    }

    public void Detach() {
        _session?.On(PantheonStart, null);
        _session?.On(PantheonSceneEnd, null);
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
            var exit = BossSceneController.Instance != null ? BossSceneController.Instance.customExitPoint : null;
            if (!_endingFromRemote && BossSequenceController.IsInSequence && exit != null && info != null &&
                info.SceneName == exit.targetScene) {
                _active?.SendSceneEnd();
            }

            orig(self, info);
        };
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

    private static readonly System.Reflection.FieldInfo DoorField =
        typeof(BossDoorChallengeUI).GetField("door", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private void OnLocalBegin(BossDoorChallengeUI ui) {
        if (DoorField?.GetValue(ui) is BossSequenceDoor door) {
            SendStart(door, (BossSequenceController.BoundNail ? BossSequenceController.ChallengeBindings.Nail : 0) |
                            (BossSequenceController.BoundShell ? BossSequenceController.ChallengeBindings.Shell : 0) |
                            (BossSequenceController.BoundCharms ? BossSequenceController.ChallengeBindings.Charms : 0) |
                            (BossSequenceController.BoundSoul ? BossSequenceController.ChallengeBindings.Soul : 0));
        }
    }

    /// <summary>Modo de prueba: empieza en este PC el Panteón de esa puerta, como su botón, y avisa a la sala.</summary>
    public bool TestStart(string doorName) {
        var door = FindDoor(GameManager.instance != null ? GameManager.instance.sceneName : string.Empty, doorName);
        if (door == null || door.bossSequence == null || door.bossSequence.Count == 0) {
            return false;
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
            : pd.atBench ? "sentado en un banco"
            : gm.isPaused ? "en pausa"
            : hero.controlReqlinquished ? "ocupado"
            : null;
        if (why != null) {
            Log.Info($"{from.Name} empezó un Panteón; este jugador no entra ({why})");
            Notice.Show(Lang.T($"{from.Name} entró en un Panteón", $"{from.Name} entered a Pantheon"), 4f);
            return;
        }

        Log.Info($"{from.Name} empezó el Panteón de {doorName}: este jugador entra con él (ataduras {bindings})");
        Enter(door, bindings);
        Notice.Show(Lang.T($"Panteón con {from.Name}", $"Pantheon with {from.Name}"), 4f);
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

    /// <summary>Lo mismo que el botón de empezar: la puerta a la que se vuelve, las ataduras, la secuencia y el viaje.</summary>
    private static void Enter(BossSequenceDoor door, BossSequenceController.ChallengeBindings bindings) {
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        var pd = PlayerData.instance;
        // La sala y la puerta a las que se vuelve al acabar (lo pone la FSM del desafío de la puerta: sin la sala, se
        // volvía a la del último sueño)
        pd.SetString("dreamReturnScene", door.gameObject.scene.name);
        pd.SetString("bossReturnEntryGate", door.dreamReturnGate.name);
        BossSequenceController.SetupNewSequence(door.bossSequence, bindings, door.playerDataString);
        hero.RelinquishControl();
        CoverWithWhite();
        gm.BeginSceneTransition(new GameManager.SceneLoadInfo {
            SceneName = door.bossSequence.GetSceneAt(0),
            EntryGateName = EntryGate,
            EntryDelay = 0f,
            Visualization = GameManager.SceneLoadVisualizations.GodsAndGlory,
            PreventCameraFadeOut = true,
            WaitForSceneTransitionCameraFade = true,
            AlwaysUnloadUnusedAssets = false
        });
    }
}
