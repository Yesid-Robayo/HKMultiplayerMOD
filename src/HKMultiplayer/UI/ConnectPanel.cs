using System.Collections.Generic;
using HKMultiplayer.Game;
using HKMultiplayer.Game.World;
using HKMultiplayer.Settings;
using UnityEngine;

namespace HKMultiplayer.UI;

/// <summary>
/// Panel para hostear o unirse (se abre y cierra con F5). Solo está activo mientras se ve: IMGUI crea memoria en cada
/// frame en que se dibuja, así que cerrado no cuesta nada. Mientras está abierto el juego no recibe teclas, para que
/// escribir el nombre o el código no mueva al caballero.
/// </summary>
internal sealed class ConnectPanel : MonoBehaviour {
    private const int WindowId = 0x484B4D50;

    private Rect _window = new(40, 40, 380, 0);
    private string _name;
    private string _address;
    private string _port;
    private string _code = string.Empty;
    private string _newWorldName = string.Empty;
    private List<CoopWorldInfo> _worlds = new();
    private string _selectedWorld;

    private bool _inputBlocked;
    private bool _previousAccepting;
    private bool _previousCursor;

    private void OnEnable() {
        var settings = HKMultiplayerMod.Settings;
        _name = settings.PlayerName;
        _address = settings.LastAddress;
        _port = settings.Port.ToString();
        _worlds = WorldStore.List();
        if (_selectedWorld == null && _worlds.Count > 0) {
            _selectedWorld = _worlds[0].Id;
        }

        _previousCursor = Cursor.visible;
        Cursor.visible = true;
        var input = InputHandler.Instance;
        if (input != null) {
            _previousAccepting = input.acceptingInput;
            input.acceptingInput = false;
            _inputBlocked = true;
        }
    }

    private void OnDisable() {
        Cursor.visible = _previousCursor;
        if (_inputBlocked && InputHandler.Instance != null) {
            InputHandler.Instance.acceptingInput = _previousAccepting;
        }

        _inputBlocked = false;
    }

    private void OnGUI() {
        _window = GUILayout.Window(WindowId, _window, DrawWindow, "HKMultiplayer  (F5 para cerrar)");
    }

    private void DrawWindow(int id) {
        var runner = MultiplayerRunner.Instance;
        var session = runner.Session;

        if (session == null) {
            DrawField("Tu nombre", ref _name);
            GUILayout.Space(6);
            DrawHostSection(runner);

            GUILayout.Space(10);
            GUILayout.Label("Unirse a la partida de otro:");
            DrawField("Dirección del host", ref _address);
            DrawField("Código", ref _code);
            if (GUILayout.Button("Unirse") && TryPort(out var joinPort)) {
                Remember(joinPort);
                runner.Join(_address, joinPort, _code, _name);
            }
        } else {
            if (session.IsHost) {
                GUILayout.Label($"Hosteando en el puerto {session.Port}");
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Código: {MultiplayerRunner.FormatCode(session.RoomCode)}");
                if (GUILayout.Button("Copiar", GUILayout.Width(80))) {
                    GUIUtility.systemCopyBuffer = MultiplayerRunner.FormatCode(session.RoomCode);
                }

                GUILayout.EndHorizontal();
            } else {
                GUILayout.Label(session.IsActive ? $"Conectado (ping {session.Rtt * 1000:0} ms)" : "Conectando...");
            }

            GUILayout.Space(6);
            GUILayout.Label("Jugadores:");
            foreach (var player in session.Players) {
                var where = player.Scene.Length > 0 ? player.Scene : "fuera de la partida";
                GUILayout.Label($"  {player.Name}{(player.IsLocal ? " (tú)" : "")} — {where}");
            }

            GUILayout.Space(6);
            if (GUILayout.Button(session.IsHost ? "Cerrar partida" : "Salir de la partida")) {
                runner.Leave();
            }
        }

        if (runner.StatusMessage.Length > 0) {
            GUILayout.Space(6);
            GUILayout.Label(runner.StatusMessage);
        }

        GUI.DragWindow();
    }

    private void DrawHostSection(MultiplayerRunner runner) {
        GUILayout.Label("Hostear un mundo coop (aparte de tus partidas normales, que no se tocan):");
        foreach (var world in _worlds) {
            var selected = world.Id == _selectedWorld;
            var text = world.IsNew
                ? $"{world.Name} — nuevo"
                : $"{world.Name} — {FormatTime(world.PlayTime)} — {world.Area}";
            if (GUILayout.Toggle(selected, text) && !selected) {
                _selectedWorld = world.Id;
            }
        }

        GUILayout.BeginHorizontal();
        _newWorldName = GUILayout.TextField(_newWorldName, 40);
        if (GUILayout.Button("Crear mundo", GUILayout.Width(110))) {
            var created = WorldStore.Create(_newWorldName);
            _newWorldName = string.Empty;
            _worlds = WorldStore.List();
            _selectedWorld = created.Id;
        }

        GUILayout.EndHorizontal();
        DrawField("Puerto", ref _port);
        var world0 = _worlds.Find(w => w.Id == _selectedWorld);
        if (!CoopSaves.CanStart) {
            GUILayout.Label("Vuelve al menú principal para abrir un mundo coop.");
        } else if (world0 == null) {
            GUILayout.Label("Crea un mundo o elige uno de la lista.");
        } else if (GUILayout.Button($"Hostear «{world0.Name}»") && TryPort(out var hostPort)) {
            Remember(hostPort);
            runner.HostWorld(world0, _name, hostPort);
        }
    }

    private static string FormatTime(float seconds) {
        var minutes = (int) (seconds / 60);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    private static void DrawField(string label, ref string value) {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130));
        value = GUILayout.TextField(value ?? string.Empty, 64);
        GUILayout.EndHorizontal();
    }

    private bool TryPort(out int port) {
        return int.TryParse(_port, out port) && port is > 0 and < 65536;
    }

    private void Remember(int port) {
        var settings = HKMultiplayerMod.Settings;
        settings.PlayerName = _name;
        settings.LastAddress = _address;
        settings.Port = port == 0 ? GlobalSettings.DefaultPort : port;
        HKMultiplayerMod.SaveSettings();
    }
}
