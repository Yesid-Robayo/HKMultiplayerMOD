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

    /// <summary>Último mensaje para el panel (errores al conectar, desconexiones...).</summary>
    public string StatusMessage { get; private set; } = string.Empty;

    /// <summary>Sesión que terminó durante su propia actualización (se cierra al acabar).</summary>
    private GameSession _closing;

    private ConnectPanel _panel;
    private KeyCode _panelKey = KeyCode.F5;

    private void Awake() {
        Instance = this;
        CoopSaves.Install();
        if (Enum.TryParse(HKMultiplayerMod.Settings.PanelKey, true, out KeyCode key)) {
            _panelKey = key;
        }

        if (TestDriver.IsEnabled()) {
            gameObject.AddComponent<TestDriver>();
        }
    }

    private void Update() {
        CoopSaves.Update();

        if (Input.GetKeyDown(_panelKey)) {
            // Se crea al abrirlo la primera vez: al añadirse, Unity ya lo activa (y bloquearía las teclas)
            if (_panel == null) {
                _panel = gameObject.AddComponent<ConnectPanel>();
            } else {
                _panel.enabled = !_panel.enabled;
            }
        }
    }

    private void LateUpdate() {
        var session = Session;
        if (session == null) {
            return;
        }

        var scene = CurrentScene();
        session.SetLocalScene(scene);
        Players.SetLocalScene(scene);

        try {
            Players.SendLocalState(Time.unscaledTimeAsDouble);
            session.Update();
            Players.UpdateAvatars();
        } catch (Exception e) {
            Log.Error($"Error en la partida: {e}");
        }

        if (_closing != null) {
            var closing = _closing;
            _closing = null;
            if (Session == closing) {
                Session = null;
                Players.Detach();
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
        Session = GameSession.Host(transport, code, SanitizeName(name), CurrentScene(), HKMultiplayerMod.Version,
            Log.Info);
        Wire(Session);
        SetStatus($"Partida abierta en el puerto {port}. Código: {FormatCode(code)}");
        return true;
    }

    /// <summary>Hostea un mundo coop: abre la partida y carga el mundo (desde el menú principal).</summary>
    public bool HostWorld(CoopWorldInfo world, string name, int port, string roomCode = null) {
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
        Session = GameSession.Join(transport, new IPEndPoint(ip, port), code, SanitizeName(name),
            HKMultiplayerMod.Version, Log.Info);
        Wire(Session);
        SetStatus($"Conectando con {ip}:{port}...");
        return true;
    }

    /// <summary>Sale de la partida (o la cierra si es el host).</summary>
    public void Leave() {
        if (Session == null) {
            return;
        }

        var session = Session;
        Session = null;
        Players.Detach();
        session.Dispose();
        SetStatus(session.IsHost ? "Partida cerrada" : "Desconectado");
    }

    private void Wire(GameSession session) {
        Players.Attach(session);
        session.Connected += () => SetStatus($"Conectado como jugador {session.LocalId}");
        session.Disconnected += reason => {
            // Al salir uno mismo (Leave) la sesión ya no es la actual: nada que avisar
            if (Session != session) {
                return;
            }

            // Llega desde dentro de session.Update(): se cierra al terminar el frame de red
            _closing = session;
            SetStatus(DescribeDisconnect(reason));
        };
        session.PlayerJoined += player => Log.Info($"{player.Name} se unió a la partida");
        session.PlayerLeft += player => Log.Info($"{player.Name} salió de la partida");
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

    public static string FormatCode(string normalized) =>
        normalized.Length == RoomCode.Length ? normalized.Substring(0, 4) + "-" + normalized.Substring(4) : normalized;

    private static string SanitizeName(string name) {
        name = (name ?? string.Empty).Trim();
        return name.Length == 0 ? "Caballero" : name.Length > 24 ? name.Substring(0, 24) : name;
    }
}
