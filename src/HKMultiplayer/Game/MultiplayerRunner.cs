using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using HKMultiplayer.Game.Players;
using HKMultiplayer.Game.World;
using HKMultiplayer.Net;
using HKMultiplayer.Net.Crypto;
using HKMultiplayer.Net.Transport;
using HKMultiplayer.Session;
using HKMultiplayer.Testing;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game;

/// <summary>
/// Objeto persistente del mod: abre y cierra la partida, mueve la red cada frame, sigue la sala del jugador local y
/// sincroniza a los jugadores. Todo en LateUpdate, en este orden: estado local → red (recibir y enviar) → dibujar a los
/// remotos; así lo que se dibuja es lo más reciente y lo que se envía sale en el mismo frame.
/// <para>Orden tardío: su LateUpdate va después del de tk2d, que avanza ahí la animación; si no, cuando un cuadro cambiaba
/// de colección, la skin se ponía sobre la hoja anterior y se veía un frame con la otra.</para>
/// </summary>
[DefaultExecutionOrder(10000)]
internal sealed class MultiplayerRunner : MonoBehaviour {
    public static MultiplayerRunner Instance { get; private set; }

    public GameSession Session { get; private set; }

    public PlayerSync Players { get; } = new();

    public WorldSync World { get; } = new();

    public SharedProgress Progress { get; } = new();

    public Scene.SceneObjectSync SceneObjects { get; } = new();

    public Scene.EnemySync Enemies { get; } = new();

    public Scene.FsmStateSync MovingObjects { get; } = new();

    /// <summary>Personajes de misión: se van o cambian también aquí cuando otro avanza su misión.</summary>
    public QuestNpcSync QuestNpcs { get; } = new();

    public Scene.BreakableSync Breakables { get; } = new();

    public Scene.HazardSync Hazards { get; } = new();

    // ---- Funciones del coop (M5) ----

    /// <summary>Estado de cada jugador (en pie, caído...): lo decide el host.</summary>
    public Features.PartyState Party { get; } = new();

    public Features.Vitals Vitals { get; } = new();

    public Features.Revive Revive { get; } = new();

    public Features.ShadeSync Shades { get; } = new();

    public Features.PartnerHud Hud { get; } = new();

    public Features.BossBar BossBar { get; } = new();

    /// <summary>Barras de vida sobre los enemigos heridos (si el host las activa).</summary>
    public Features.EnemyBars EnemyBars { get; } = new();

    public Features.PartyActions Actions { get; } = new();

    /// <summary>"Ir con ..." del panel: aparecer junto a un compañero.</summary>
    public Features.PartnerTravel Travel { get; } = new();

    /// <summary>Finales, créditos y Mr. Mushroom a la vez para todos.</summary>
    public Features.SharedCutscenes Cutscenes { get; } = new();

    /// <summary>Presentaciones de jefe: los dos caballeros quietos a la vez.</summary>
    public Features.BossPresentation Presentations { get; } = new();

    /// <summary>Sueños compartidos: entrar y salir con los compañeros de la sala.</summary>
    public Features.DreamSync Dreams { get; } = new();

    /// <summary>Grimmchild para todos si uno lo recibe.</summary>
    public Features.GrimmchildGift Grimmchild { get; } = new();

    /// <summary>Viajar juntos en ciervo desde la misma estación.</summary>
    public Features.StagTogether Stag { get; } = new();

    /// <summary>Entrar juntos en un Panteón desde su puerta del Atrio.</summary>
    public Features.PantheonTogether Pantheons { get; } = new();

    /// <summary>Skins: la propia y las de los demás.</summary>
    public Features.Skins Skins { get; } = new();

    /// <summary>Compañeros en el mapa abierto.</summary>
    public Features.PartnerMap PartnerMap { get; } = new();

    public PartyAbilities PartyAbilities { get; } = new();

    /// <summary>El jugador salió de un mundo ajeno: al llegar al menú se deja la partida.</summary>
    private bool _leaveOnMenu;

    /// <summary>Último mensaje para el panel (errores al conectar, desconexiones...).</summary>
    public string StatusMessage { get; private set; } = string.Empty;

    /// <summary>Al hostear, el código se anuncia en pantalla en cuanto el jugador entra a jugar.</summary>
    private bool _announceCode;

    /// <summary>Sesión que terminó durante su propia actualización (se cierra al acabar).</summary>
    private GameSession _closing;

    /// <summary>Sala de la última carga terminada.</summary>
    private string _lastLoadedScene = string.Empty;

    /// <summary>
    /// Se volvió a cargar la misma sala (morir y reaparecer en ella, por ejemplo): sus objetos son otros aunque el nombre
    /// sea el mismo. Durante un frame se juega "fuera de sala": así cada sincronización vuelve a buscar los objetos y el
    /// servidor ve salir y entrar al jugador, de modo que el dueño de la sala le envía lo que ya pasó en ella.
    /// </summary>
    private bool _sceneReloaded;

    /// <summary>Ya se recogieron los personajes y se guardó: el juego puede cerrarse.</summary>
    private bool _quitReady;

    /// <summary>Lo más que espera el host los personajes de los invitados antes de cerrar.</summary>
    private const float CollectTimeout = 4f;

    /// <summary>Partida a la que se unió el invitado por última vez (a la que se reconecta).</summary>
    private sealed class JoinTarget {
        public string Address;
        public int Port;
        public string RoomCode;
        public string Name;
    }

    private JoinTarget _lastJoin;

    /// <summary>
    /// Reconexión automática del invitado que perdió la conexión con el host: cuántos intentos van (0: no se está
    /// reconectando). Cada intento espera a estar en el menú principal, donde se puede volver a cargar el mundo.
    /// </summary>
    private int _reconnectAttempts;

    private float _nextReconnectAt;

    private const int MaxReconnectAttempts = 12;

    /// <summary>Pausa entre un intento fallido y el siguiente (cada intento espera además hasta 10 s al host).</summary>
    private const float ReconnectDelay = 5f;

    /// <summary>Si se está intentando volver a la partida del host.</summary>
    public bool IsReconnecting => _reconnectAttempts > 0;

    private void Awake() {
        Instance = this;
        UpdateCheck.Begin(this);
        // Llamado cuando la sala nueva ya está lista y la anterior descargada
        On.GameManager.OnNextLevelReady += (orig, self) => {
            orig(self);
            var scene = self.sceneName ?? string.Empty;
            if (scene.Length > 0 && scene == _lastLoadedScene) {
                _sceneReloaded = true;
            }

            _lastLoadedScene = scene;
        };
        CoopSaves.Install();
        Progress.RemoteFieldChanged = QuestNpcs.OnRemoteChange;
        // Ganchos de las funciones del coop ya al cargar el mod (después, el juego ya podría haber compilado lo que los
        // llama con el método enganchado copiado dentro)
        Features.Revive.InstallHooks();
        Features.ShadeSync.InstallHooks();
        Features.BossBar.InstallHooks();
        Features.EnemyBars.InstallHooks();
        Features.Difficulty.Install();
        Scene.SceneLoads.Install();
        HKMultiplayer.Game.World.PartyAbilities.InstallHooks();
        Features.SharedCutscenes.InstallHooks();
        Features.BossPresentation.InstallHooks();
        Features.DreamSync.InstallHooks();
        Features.PantheonTogether.InstallHooks();
        Scene.DropSeeds.InstallHooks();
        Scene.ScenePaths.InstallHooks();
        QuestNpcSync.InstallHooks();
        CoopSaves.ExitedWorld += () => {
            // Se salió del mundo coop al menú principal (los personajes ya se recogieron o se enviaron al guardar): la
            // partida se cierra. También el invitado que sale con "Salir al menú" del juego (no con el botón del mod):
            // antes seguía conectado en el menú, contaba como "en pie" (el host caía en vez de morir y nadie lo revivía)
            // y, si luego cargaba una partida suya, el mod seguía actuando sobre ella
            if (_leaveOnMenu || Session != null) {
                _leaveOnMenu = false;
                Leave();
            }
        };
        World.WorldRejected += reason => {
            Log.Warn($"No se pudo entrar en el mundo del host: {reason}");
            Notice.Show(reason, 6f);
            Leave();
        };

        // El host vuelve al menú: antes recoge el personaje de cada invitado
        On.GameManager.ReturnToMainMenu += (orig, self, saveMode, callback) =>
            CollectGuestsThen(orig(self, saveMode, callback));

        // Cerrar el juego siendo host con invitados: se recogen sus personajes, se guarda el mundo y entonces se cierra
        Application.wantsToQuit += () => {
            if (_quitReady || !World.HasGuestsToSave) {
                return true;
            }

            StartCoroutine(CollectGuestsAndQuit());
            return false;
        };
        if (TestDriver.IsEnabled()) {
            gameObject.AddComponent<TestDriver>();
        }
    }

    private void Update() {
        if (_backToMenuWhenReady) {
            if (CoopSaves.CanStart) {
                _backToMenuWhenReady = false;
            } else if (HeroController.instance != null && !CoopSaves.IntroPlaying &&
                       GameManager.instance is { IsInSceneTransition: false, gameState: GlobalEnums.GameState.PLAYING }) {
                _backToMenuWhenReady = false;
                ReturnToMenu(save: false);
            }
        }

        UpdateReconnect();
        CoopSaves.Update();
        MultiplayerMenu.EnsureInstalled();
        SessionMenu.UpdateInstalled(Session != null);
        UpdateCheck.Update();
        World.Update();
        Progress.Update();
        Skins.Update();
        if (Session != null) {
            Features.HostSettings.Current.Update();
            Actions.Update();
            Travel.Update();
        }
    }

    private void LateUpdate() {
        var session = Session;
        if (session == null) {
            // La skin propia se ve también jugando solo
            try {
                Skins.LateUpdate(string.Empty);
            } catch (Exception e) {
                Log.Error($"Error en las skins: {e}");
            }

            return;
        }

        var scene = _sceneReloaded ? string.Empty : CurrentScene();
        _sceneReloaded = false;
        if (_announceCode && scene.Length > 0 && session.IsHost) {
            _announceCode = false;
            Notice.Show(Lang.T($"Partida abierta  ·  Código {FormatCode(session.RoomCode)}\n", $"Game open  ·  Code {FormatCode(session.RoomCode)}\n") +
                        Lang.T("Lo tienes en Pausa › Multijugador", "It's in Pause › Multiplayer"), 12f);
        }

        // Un fallo en la sincronización de la sala no debe dejar sin enviar los paquetes de este frame (con varios
        // seguidos el otro extremo cortaría la conexión por tiempo)
        try {
            session.SetLocalScene(scene);
            Players.SetLocalScene(scene);
            SceneObjects.Update(scene);
            MovingObjects.Update(scene);
            QuestNpcs.Update(scene);
            Breakables.Update(scene);
            Hazards.Update(scene);
        } catch (Exception e) {
            Log.Error($"Error en la sala: {e}");
        }

        // En bloques: un fallo en una parte no salta las demás (antes uno solo para todo: si fallaba el estado local, ni
        // siquiera se enviaba ni recibía nada ese frame)
        try {
            Players.SendLocalState(Time.unscaledTimeAsDouble);
        } catch (Exception e) {
            ReportFailure("el estado local", e);
        }

        try {
            session.Update();
        } catch (Exception e) {
            ReportFailure("la red", e);
        }

        try {
            Players.UpdateAvatars();
            Enemies.Update(scene);
        } catch (Exception e) {
            ReportFailure("los jugadores y enemigos", e);
        }

        try {
            Vitals.Update();
            UI.HudCanvas.Sync();
            Revive.Update(scene);
            Shades.Update(scene);
        } catch (Exception e) {
            ReportFailure("revivir y las sombras", e);
        }

        try {
            Hud.Update(scene);
            BossBar.Update(scene);
            EnemyBars.Update(scene);
            Skins.LateUpdate(scene);
            Skins.ApplyToBodies(Revive.Bodies);
        } catch (Exception e) {
            ReportFailure("el HUD y las skins", e);
        }

        try {
            Cutscenes.Update();
            Presentations.Update();
            Dreams.Update(scene);
            Grimmchild.Update(scene);
            Stag.Update(scene);
            PartnerMap.Update(scene);
            PartyAbilities.Update();
            Party.ReportLocal(Cutscenes.Watching ? Features.PlayerStatus.Cutscene
                : Dreams.InDream ? Features.PlayerStatus.Dream
                : InStagTravel() ? Features.PlayerStatus.StagTravel
                : HeroController.instance == null || !session.IsHost && CoopSaves.ActiveWorld == null || CoopSaves.IntroPlaying
                    ? Features.PlayerStatus.Joining
                : Features.PlayerStatus.Standing,
                GameManager.instance != null && GameManager.instance.isPaused);
        } catch (Exception e) {
            ReportFailure("las funciones del coop", e);
        }

        if (_closing != null) {
            var closing = _closing;
            _closing = null;
            if (Session == closing) {
                Session = null;
                DetachAll();
                closing.Dispose();
            }
        }
    }

    private void OnApplicationQuit() => Leave();

    /// <summary>Sala del viaje en ciervo (no es de juego: mientras se viaja la sala local queda vacía).</summary>
    private const string StagTravelScene = "Cinematic_Stag_travel";

    private static bool InStagTravel() => GameManager.instance != null && GameManager.instance.sceneName == StagTravelScene;

    /// <summary>Sala en la que se está jugando, o vacía (menú, cinemática, cargando).</summary>
    private static string CurrentScene() {
        var gm = GameManager.instance;
        if (gm == null || HeroController.instance == null || !gm.IsGameplayScene()) {
            return string.Empty;
        }

        return gm.sceneName ?? string.Empty;
    }

    // =====================================================================================================
    // Abrir y cerrar
    // =====================================================================================================

    /// <summary>Hostea en <paramref name="port"/>. Con <paramref name="roomCode"/> nulo se genera uno al azar.</summary>
    public bool Host(string name, int port, string roomCode = null) {
        CancelReconnect();
        CloseSession();
        UdpTransport transport;
        try {
            transport = new UdpTransport(port);
        } catch (SocketException e) {
            SetStatus(Lang.T($"No se pudo abrir el puerto {port}: {e.Message}", $"Couldn't open port {port}: {e.Message}"));
            return false;
        }

        transport.Error += message => Log.Warn($"Red: {message}");
        var code = roomCode != null ? RoomCode.Normalize(roomCode) : RoomCode.Generate();
        Session = GameSession.Host(WithTestNetwork(transport), code, SanitizeName(name), CurrentScene(), HKMultiplayerMod.Version,
            Log.Info);
        Wire(Session);
        _announceCode = true;
        SetStatus(Lang.T($"Partida abierta en el puerto {port}. Código: {FormatCode(code)}", $"Game open on port {port}. Code: {FormatCode(code)}"));
        return true;
    }

    /// <summary>Hostea un mundo coop: abre la partida y carga el mundo (desde el menú principal).</summary>
    public bool HostWorld(CoopWorldInfo world, string name, int port, string roomCode = null) {
        if (world == null) {
            SetStatus(Lang.T("No hay mundo que abrir", "There is no world to open"));
            return false;
        }

        if (!CoopSaves.CanStart) {
            SetStatus(Lang.T("Vuelve al menú principal para abrir un mundo coop", "Go back to the main menu to open a coop world"));
            return false;
        }

        if (!Host(name, port, roomCode)) {
            return false;
        }

        if (!CoopSaves.Start(world)) {
            Leave();
            SetStatus(Lang.T($"No se pudo abrir el mundo {world.Name}", $"Couldn't open the world {world.Name}"));
            return false;
        }

        return true;
    }

    /// <summary>En el modo de prueba, si se pidió (netsim), la conexión se estropea como una mala conexión.</summary>
    private static ITransport WithTestNetwork(ITransport transport) {
        if (!TestDriver.IsEnabled() || !LaggyTransport.Configured) {
            return transport;
        }

        Log.Info($"Red de prueba: {LaggyTransport.NextLatency * 1000:0} ms ± {LaggyTransport.NextJitter * 1000:0} ms, " +
                 $"{LaggyTransport.NextLoss * 100:0}% de pérdida");
        return new LaggyTransport(transport, LaggyTransport.NextLatency, LaggyTransport.NextJitter, LaggyTransport.NextLoss);
    }

    /// <summary>Se une a la partida de <paramref name="address"/>.</summary>
    public bool Join(string address, int port, string roomCode, string name) {
        CancelReconnect();
        return StartJoin(address, port, roomCode, name);
    }

    private bool StartJoin(string address, int port, string roomCode, string name) {
        CloseSession();
        var code = RoomCode.Normalize(roomCode ?? string.Empty);
        if (!RoomCode.IsValid(code)) {
            SetStatus(Lang.T("El código no es válido: son 8 letras o números, como ABCD-EFGH", "The code is not valid: it is 8 letters or numbers, like ABCD-EFGH"));
            return false;
        }

        IPAddress ip;
        try {
            ip = IPAddress.TryParse(address.Trim(), out var parsed)
                ? parsed
                : Dns.GetHostAddresses(address.Trim()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        } catch (SocketException) {
            ip = null;
        }

        if (ip == null) {
            SetStatus(Lang.T($"No se encontró la dirección '{address}'", $"Address '{address}' not found"));
            return false;
        }

        UdpTransport transport;
        try {
            transport = new UdpTransport(0);
        } catch (SocketException e) {
            SetStatus(Lang.T($"No se pudo abrir la red: {e.Message}", $"Couldn't open the network: {e.Message}"));
            return false;
        }

        transport.Error += message => Log.Warn($"Red: {message}");
        _lastJoin = new JoinTarget { Address = address, Port = port, RoomCode = code, Name = name };
        Session = GameSession.Join(WithTestNetwork(transport), new IPEndPoint(ip, port), code, SanitizeName(name),
            HKMultiplayerMod.Version, Log.Info);
        Wire(Session);
        SetStatus(Lang.T($"Conectando con {ip}:{port}...", $"Connecting to {ip}:{port}..."));
        return true;
    }

    /// <summary>
    /// Sale de la partida. Si se está jugando el mundo del host, primero se envía el personaje (guardando como al salir
    /// al menú) y se vuelve al menú principal; la conexión se cierra al llegar.
    /// </summary>
    public void LeaveFromGame() {
        var session = Session;
        if (session != null && !session.IsHost && CoopSaves.ActiveWorld != null && !CoopSaves.IsHostWorld) {
            _leaveOnMenu = true;
            ReturnToMenu(save: true);
            return;
        }

        CloseGame();
    }

    /// <summary>
    /// Cierra la partida. Si es el host con invitados en su mundo, antes recoge el personaje de cada uno (unos segundos
    /// como mucho), para que nadie pierda lo hecho desde su último guardado.
    /// </summary>
    public void CloseGame() {
        if (!World.HasGuestsToSave) {
            Leave();
            return;
        }

        StartCoroutine(CloseAfterCollecting(Session));
    }

    private System.Collections.IEnumerator CloseAfterCollecting(GameSession session) {
        yield return World.CollectGuests(CollectTimeout);
        if (Session == session) {
            Leave();
        }
    }

    private System.Collections.IEnumerator CollectGuestsThen(System.Collections.IEnumerator next) {
        if (World.HasGuestsToSave) {
            yield return World.CollectGuests(CollectTimeout);
        }

        yield return next;
    }

    private System.Collections.IEnumerator CollectGuestsAndQuit() {
        yield return World.CollectGuests(CollectTimeout);
        if (CoopSaves.IsHostWorld && HeroController.instance != null) {
            GameManager.instance.SaveGame();
        }

        _quitReady = true;
        Application.Quit();
    }

    /// <summary>Se perdió la conexión mientras se cargaba el mundo del host: se vuelve al menú en cuanto se pueda.</summary>
    private bool _backToMenuWhenReady;

    private float _returningSince = -100f;

    private void ReturnToMenu(bool save) {
        // Una sola vuelta a la vez: el caballero sigue durante el fundido de 2 s y una segunda empezaba otra encima
        if (Time.unscaledTime - _returningSince < 10f || CoopSaves.AtMainMenu) {
            return;
        }

        _returningSince = Time.unscaledTime;
        var ui = UIManager.instance;
        if (ui != null && ui.uiState == GlobalEnums.UIState.PAUSED) {
            ui.UIReturnToMainMenu();
            return;
        }

        StartCoroutine(GameManager.instance.ReturnToMainMenu(save
            ? GameManager.ReturnToMainMenuSaveModes.SaveAndContinueOnFail
            : GameManager.ReturnToMainMenuSaveModes.DontSave));
    }

    /// <summary>Sale de la partida (o la cierra si es el host). También deja de intentar reconectar.</summary>
    public void Leave() {
        CancelReconnect();
        CloseSession();
    }

    private void CloseSession() {
        if (Session == null) {
            return;
        }

        var session = Session;
        Session = null;
        DetachAll();
        session.Dispose();
        SetStatus(session.IsHost ? Lang.T("Partida cerrada", "Game closed") : Lang.T("Desconectado", "Disconnected"));
    }

    /// <summary>Cada capa deja la partida (las funciones del coop primero: usan las demás).</summary>
    private void DetachAll() {
        // Cada parte por separado: si una fallaba, las de detrás no se soltaban y session.Dispose() no llegaba (el socket,
        // su hilo y el puerto se quedaban abiertos)
        Safely(() => { PartnerMap.Detach(); }, "PartnerMap.Detach");
        Safely(() => { PartyAbilities.Detach(); }, "PartyAbilities.Detach");
        Safely(() => { Skins.Detach(); }, "Skins.Detach");
        Safely(() => { Grimmchild.Detach(); }, "Grimmchild.Detach");
        Safely(() => { Stag.Detach(); }, "Stag.Detach");
        Safely(() => { Pantheons.Detach(); }, "Pantheons.Detach");
        Safely(() => { Scene.DropSeeds.Enabled = false; }, "Scene.DropSeeds.Enabled");
        Safely(() => { Scene.ScenePaths.Enabled = false; }, "Scene.ScenePaths.Enabled");
        Safely(() => { Scene.ScenePaths.Clear(); }, "Scene.ScenePaths.Clear");
        Safely(() => { Dreams.Detach(); }, "Dreams.Detach");
        Safely(() => { Presentations.Detach(); }, "Presentations.Detach");
        Safely(() => { Cutscenes.Detach(); }, "Cutscenes.Detach");
        Safely(() => { Travel.Detach(); }, "Travel.Detach");
        Safely(() => { Actions.Detach(); }, "Actions.Detach");
        Safely(() => { EnemyBars.Detach(); }, "EnemyBars.Detach");
        Safely(() => { BossBar.Detach(); }, "BossBar.Detach");
        Safely(() => { Hud.Detach(); }, "Hud.Detach");
        Safely(() => { Shades.Detach(); }, "Shades.Detach");
        Safely(() => { Revive.Detach(); }, "Revive.Detach");
        Safely(() => { Vitals.Detach(); }, "Vitals.Detach");
        Safely(() => { Features.HostSettings.Current.Detach(); }, "Features.HostSettings.Current.Detach");
        Safely(() => { Party.Detach(); }, "Party.Detach");
        Safely(() => { Players.Detach(); }, "Players.Detach");
        Safely(() => { World.Detach(); }, "World.Detach");
        Safely(() => { Progress.Detach(); }, "Progress.Detach");
        Safely(() => { SceneObjects.Detach(); }, "SceneObjects.Detach");
        Safely(() => { Enemies.Detach(); }, "Enemies.Detach");
        Safely(() => { MovingObjects.Detach(); }, "MovingObjects.Detach");
        Safely(() => { QuestNpcs.Detach(); }, "QuestNpcs.Detach");
        Safely(() => { Breakables.Detach(); }, "Breakables.Detach");
        Safely(() => { Hazards.Detach(); }, "Hazards.Detach");
    }

    private static void Safely(Action action, string what) {
        try {
            action();
        } catch (Exception e) {
            Log.Error($"Error al cerrar {what}: {e}");
        }
    }


    // =====================================================================================================
    // Reconexión automática
    // =====================================================================================================

    /// <summary>
    /// El invitado perdió la conexión: si se cortó por tiempo (el host no responde, no porque cerrara la partida), se
    /// intenta volver a entrar. Un intento que falla por tiempo deja programado el siguiente; cualquier otro motivo
    /// (código, versión, partida llena o cerrada) termina los intentos.
    /// </summary>
    private void OnGuestDisconnected(DisconnectReason reason, bool wasConnected) {
        if (reason != DisconnectReason.Timeout || _lastJoin == null) {
            if (_reconnectAttempts > 0) {
                GiveUpReconnect(DescribeDisconnect(reason));
            }

            return;
        }

        // Un primer intento de unirse que no llegó al host no se repite solo
        if (!wasConnected && _reconnectAttempts == 0) {
            return;
        }

        if (_reconnectAttempts >= MaxReconnectAttempts) {
            GiveUpReconnect(Lang.T("el host no responde", "the host isn't responding"));
            return;
        }

        if (_reconnectAttempts == 0) {
            Notice.Show(Lang.T("Se perdió la conexión con el host: reconectando...\nEsc para cancelar",
                "Lost connection to the host: reconnecting...\nEsc to cancel"), 6f);
        }

        _reconnectAttempts++;
        _nextReconnectAt = Time.unscaledTime + ReconnectDelay;
    }

    private void UpdateReconnect() {
        if (_reconnectAttempts == 0) {
            return;
        }

        // Esc cancela en el menú principal (en la partida es la pausa)
        if (CoopSaves.CanStart && Input.GetKeyDown(KeyCode.Escape)) {
            CancelReconnect();
            // El intento en curso (aún sin conectar) también se corta: si no, entraría si el host vuelve en esos segundos
            if (Session is { IsHost: false, CanSend: false }) {
                CloseSession();
            }

            Notice.Show(Lang.T("Reconexión cancelada", "Reconnection cancelled"), 4f);
            return;
        }

        // Se intenta desde el menú principal (el mundo del host solo se puede cargar ahí) y sin otra partida en marcha
        if (Session != null || !CoopSaves.CanStart || Time.unscaledTime < _nextReconnectAt) {
            return;
        }

        var target = _lastJoin;
        _nextReconnectAt = float.MaxValue;
        Log.Info($"Reconectando con el host (intento {_reconnectAttempts} de {MaxReconnectAttempts})");
        Notice.Show(Lang.T($"Reconectando con el host (intento {_reconnectAttempts} de {MaxReconnectAttempts})...\nEsc para cancelar",
            $"Reconnecting to the host (attempt {_reconnectAttempts} of {MaxReconnectAttempts})...\nEsc to cancel"), 12f);
        if (!StartJoin(target.Address, target.Port, target.RoomCode, target.Name)) {
            GiveUpReconnect(StatusMessage);
        }
    }

    /// <summary>Se deja de intentar volver a la partida, con el motivo en pantalla y en el registro.</summary>
    private void GiveUpReconnect(string reason) {
        _reconnectAttempts = 0;
        Log.Info($"No se pudo volver a la partida: {reason}");
        Notice.Show(Lang.T("No se pudo volver a la partida: ", "Couldn't get back into the game: ") + reason, 8f);
    }

    private void CancelReconnect() {
        if (_reconnectAttempts > 0) {
            Log.Info("Se deja de intentar reconectar");
        }

        _reconnectAttempts = 0;
    }

    private void Wire(GameSession session) {
        // El estado de cada jugador primero: las demás funciones lo consultan al entrar alguien
        Party.Attach(session);
        Players.Attach(session);
        World.Attach(session);
        Progress.Attach(session);
        SceneObjects.Attach(session);
        Enemies.Attach(session, Players);
        MovingObjects.Attach(session, Players);
        QuestNpcs.Attach(session);
        Breakables.Attach(session);
        Hazards.Attach(session);
        Features.HostSettings.Current.Attach(session);
        Vitals.Attach(session);
        Revive.Attach(session, Party, Players, Vitals);
        Shades.Attach(session);
        Hud.Attach(session, Party, Vitals, Players, Revive);
        BossBar.Attach();
        EnemyBars.Attach();
        Actions.Attach(session, Revive);
        Cutscenes.Attach(session, Revive);
        Presentations.Attach(session, Revive);
        Dreams.Attach(session, Revive);
        Travel.Attach(session, Party, Revive, Dreams, Cutscenes, Players);
        Grimmchild.Attach(session);
        Stag.Attach(session);
        Pantheons.Attach(session);
        Scene.DropSeeds.Enabled = true;
        Scene.ScenePaths.Enabled = true;
        Scene.ScenePaths.RememberLoaded();
        Skins.Attach(session, Players);
        PartnerMap.Attach(session, Revive);
        PartyAbilities.Attach(session);
        var connected = false;
        session.Connected += () => {
            connected = true;
            SetStatus(Lang.T($"Conectado como jugador {session.LocalId}", $"Connected as player {session.LocalId}"));
            if (_reconnectAttempts > 0) {
                _reconnectAttempts = 0;
                Notice.Show(Lang.T("Reconectado con el host: entrando en el mundo...", "Reconnected to the host: entering the world..."), 6f);
            }
        };
        session.Disconnected += reason => {
            // Al salir uno mismo (Leave) la sesión ya no es la actual: nada que avisar
            if (Session != session) {
                return;
            }

            // Llega desde dentro de session.Update(): se cierra al terminar el frame de red
            _closing = session;
            SetStatus(DescribeDisconnect(reason));

            // Jugando el mundo del host sin host: de vuelta al menú (aquí no hay nada que guardar). Si aún está cargándolo o
            // viendo la introducción, en cuanto haya caballero: antes la carga seguía y se jugaba una copia sin conexión del
            // mundo del host, con todo lo que se guardaba tirado
            if (CoopSaves.ActiveWorld != null && !CoopSaves.IsHostWorld) {
                Notice.Show(DescribeDisconnect(reason), 6f);
                if (HeroController.instance != null && !CoopSaves.IntroPlaying &&
                    GameManager.instance is { IsInSceneTransition: false }) {
                    ReturnToMenu(save: false);
                } else {
                    _backToMenuWhenReady = true;
                }
            }

            // Después del aviso de desconexión: si se va a reconectar, su aviso lo sustituye
            if (!session.IsHost) {
                OnGuestDisconnected(reason, connected);
            }
        };
        session.PlayerJoined += player => {
            Log.Info($"{player.Name} se unió a la partida");
            if (Features.HostSettings.Current.ShowNotices) {
                Notice.Show(Lang.T($"{player.Name} se unió a la partida", $"{player.Name} joined the game"));
            }
        };
        session.PlayerLeft += player => {
            // Al cerrar la partida propia (la sesión ya no es la actual) no se avisa de cada uno que "sale"
            if (Session != session) {
                return;
            }

            Log.Info($"{player.Name} salió de la partida");
            if (Features.HostSettings.Current.ShowNotices) {
                Notice.Show(Lang.T($"{player.Name} salió de la partida", $"{player.Name} left the game"));
            }
        };
    }

    private int _failures;

    /// <summary>Un fallo en una parte del frame: al registro (los 20 primeros y luego uno de cada 500).</summary>
    private void ReportFailure(string part, Exception e) {
        if (++_failures <= 20 || _failures % 500 == 0) {
            Log.Error($"Error en {part} (van {_failures}): {e}");
        }
    }

    private void SetStatus(string message) {
        StatusMessage = message;
        Log.Info(message);
    }

    private static string DescribeDisconnect(DisconnectReason reason) => reason switch {
        DisconnectReason.Closed => Lang.T("El host cerró la partida", "The host closed the game"),
        DisconnectReason.Timeout => Lang.T("Se perdió la conexión con el host", "Lost connection to the host"),
        DisconnectReason.WrongCode => Lang.T("Código incorrecto o host no encontrado", "Wrong code or host not found"),
        DisconnectReason.VersionMismatch => Lang.T("El host tiene otra versión del mod", "The host has a different version of the mod"),
        DisconnectReason.Full => Lang.T("La partida está llena", "The game is full"),
        _ => Lang.T("El host rechazó la conexión", "The host refused the connection")
    };

    /// <summary>Direcciones IPv4 de este PC en la red local (las que debe usar el compañero para unirse).</summary>
    public static string LocalAddresses() {
        try {
            var addresses = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToList();
            return addresses.Count > 0 ? string.Join(" / ", addresses) : "127.0.0.1";
        } catch (SocketException) {
            return "?";
        }
    }

    public static string FormatCode(string normalized) =>
        normalized.Length == RoomCode.Length ? normalized.Substring(0, 4) + "-" + normalized.Substring(4) : normalized;

    private static string SanitizeName(string name) {
        name = GameSession.CleanName(name);
        return name == "?" ? "Caballero" : name;
    }
}
