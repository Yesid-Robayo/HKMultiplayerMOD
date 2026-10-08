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
/// </summary>
internal sealed class MultiplayerRunner : MonoBehaviour {
    public static MultiplayerRunner Instance { get; private set; }

    public GameSession Session { get; private set; }

    public PlayerSync Players { get; } = new();

    public WorldSync World { get; } = new();

    public SharedProgress Progress { get; } = new();

    public Scene.SceneObjectSync SceneObjects { get; } = new();

    public Scene.EnemySync Enemies { get; } = new();

    public Scene.FsmStateSync MovingObjects { get; } = new();

    public Scene.BreakableSync Breakables { get; } = new();

    public Scene.HazardSync Hazards { get; } = new();

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

    private void Awake() {
        Instance = this;
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
        CoopSaves.ExitedWorld += () => {
            // El host salió de su mundo (los personajes ya se recogieron al volver al menú): la partida se cierra
            if (_leaveOnMenu || Session is { IsHost: true }) {
                _leaveOnMenu = false;
                Leave();
            }
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
        CoopSaves.Update();
        MultiplayerMenu.EnsureInstalled();
        SessionMenu.UpdateInstalled(Session != null);
        World.Update();
        Progress.Update();
    }

    private void LateUpdate() {
        var session = Session;
        if (session == null) {
            return;
        }

        var scene = _sceneReloaded ? string.Empty : CurrentScene();
        _sceneReloaded = false;
        if (_announceCode && scene.Length > 0 && session.IsHost) {
            _announceCode = false;
            Notice.Show($"Partida abierta  ·  Código {FormatCode(session.RoomCode)}\n" +
                        "Lo tienes en Pausa › Multijugador", 12f);
        }

        session.SetLocalScene(scene);
        Players.SetLocalScene(scene);
        SceneObjects.Update(scene);
        MovingObjects.Update(scene);
        Breakables.Update(scene);
        Hazards.Update(scene);

        try {
            Players.SendLocalState(Time.unscaledTimeAsDouble);
            session.Update();
            Players.UpdateAvatars();
            Enemies.Update(scene);
        } catch (Exception e) {
            Log.Error($"Error en la partida: {e}");
        }

        if (_closing != null) {
            var closing = _closing;
            _closing = null;
            if (Session == closing) {
                Session = null;
                Players.Detach();
        World.Detach();
        Progress.Detach();
        SceneObjects.Detach();
        Enemies.Detach();
        MovingObjects.Detach();
        Breakables.Detach();
        Hazards.Detach();
                closing.Dispose();
            }
        }
    }

    private void OnApplicationQuit() => Leave();

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
        Leave();
        UdpTransport transport;
        try {
            transport = new UdpTransport(port);
        } catch (SocketException e) {
            SetStatus($"No se pudo abrir el puerto {port}: {e.Message}");
            return false;
        }

        transport.Error += message => Log.Warn($"Red: {message}");
        var code = roomCode != null ? RoomCode.Normalize(roomCode) : RoomCode.Generate();
        Session = GameSession.Host(WithTestNetwork(transport), code, SanitizeName(name), CurrentScene(), HKMultiplayerMod.Version,
            Log.Info);
        Wire(Session);
        _announceCode = true;
        SetStatus($"Partida abierta en el puerto {port}. Código: {FormatCode(code)}");
        return true;
    }

    /// <summary>Hostea un mundo coop: abre la partida y carga el mundo (desde el menú principal).</summary>
    public bool HostWorld(CoopWorldInfo world, string name, int port, string roomCode = null) {
        if (world == null) {
            SetStatus("No hay mundo que abrir");
            return false;
        }

        if (!CoopSaves.CanStart) {
            SetStatus("Vuelve al menú principal para abrir un mundo coop");
            return false;
        }

        if (!Host(name, port, roomCode)) {
            return false;
        }

        if (!CoopSaves.Start(world)) {
            Leave();
            SetStatus($"No se pudo abrir el mundo {world.Name}");
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
        Leave();
        var code = RoomCode.Normalize(roomCode ?? string.Empty);
        if (!RoomCode.IsValid(code)) {
            SetStatus("El código no es válido: son 8 letras o números, como ABCD-EFGH");
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
            SetStatus($"No se encontró la dirección '{address}'");
            return false;
        }

        UdpTransport transport;
        try {
            transport = new UdpTransport(0);
        } catch (SocketException e) {
            SetStatus($"No se pudo abrir la red: {e.Message}");
            return false;
        }

        transport.Error += message => Log.Warn($"Red: {message}");
        Session = GameSession.Join(WithTestNetwork(transport), new IPEndPoint(ip, port), code, SanitizeName(name),
            HKMultiplayerMod.Version, Log.Info);
        Wire(Session);
        SetStatus($"Conectando con {ip}:{port}...");
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

    private void ReturnToMenu(bool save) {
        var ui = UIManager.instance;
        if (ui != null && ui.uiState == GlobalEnums.UIState.PAUSED) {
            ui.UIReturnToMainMenu();
            return;
        }

        StartCoroutine(GameManager.instance.ReturnToMainMenu(save
            ? GameManager.ReturnToMainMenuSaveModes.SaveAndContinueOnFail
            : GameManager.ReturnToMainMenuSaveModes.DontSave));
    }

        /// <summary>Sale de la partida (o la cierra si es el host).</summary>
    public void Leave() {
        if (Session == null) {
            return;
        }

        var session = Session;
        Session = null;
        Players.Detach();
        World.Detach();
        Progress.Detach();
        SceneObjects.Detach();
        Enemies.Detach();
        MovingObjects.Detach();
        Breakables.Detach();
        Hazards.Detach();
        session.Dispose();
        SetStatus(session.IsHost ? "Partida cerrada" : "Desconectado");
    }

    private void Wire(GameSession session) {
        Players.Attach(session);
        World.Attach(session);
        Progress.Attach(session);
        SceneObjects.Attach(session);
        Enemies.Attach(session, Players);
        MovingObjects.Attach(session, Players);
        Breakables.Attach(session);
        Hazards.Attach(session);
        session.Connected += () => SetStatus($"Conectado como jugador {session.LocalId}");
        session.Disconnected += reason => {
            // Al salir uno mismo (Leave) la sesión ya no es la actual: nada que avisar
            if (Session != session) {
                return;
            }

            // Llega desde dentro de session.Update(): se cierra al terminar el frame de red
            _closing = session;
            SetStatus(DescribeDisconnect(reason));

            // Jugando el mundo del host sin host: de vuelta al menú (aquí no hay nada que guardar)
            if (CoopSaves.ActiveWorld != null && !CoopSaves.IsHostWorld && HeroController.instance != null) {
                Notice.Show(DescribeDisconnect(reason), 6f);
                ReturnToMenu(save: false);
            }
        };
        session.PlayerJoined += player => {
            Log.Info($"{player.Name} se unió a la partida");
            Notice.Show($"{player.Name} se unió a la partida");
        };
        session.PlayerLeft += player => {
            Log.Info($"{player.Name} salió de la partida");
            Notice.Show($"{player.Name} salió de la partida");
        };
    }

    private void SetStatus(string message) {
        StatusMessage = message;
        Log.Info(message);
    }

    private static string DescribeDisconnect(DisconnectReason reason) => reason switch {
        DisconnectReason.Closed => "El host cerró la partida",
        DisconnectReason.Timeout => "Se perdió la conexión con el host",
        DisconnectReason.WrongCode => "Código incorrecto o host no encontrado",
        DisconnectReason.VersionMismatch => "El host tiene otra versión del mod",
        DisconnectReason.Full => "La partida está llena",
        _ => "El host rechazó la conexión"
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
        name = (name ?? string.Empty).Trim();
        return name.Length == 0 ? "Caballero" : name.Length > 24 ? name.Substring(0, 24) : name;
    }
}
