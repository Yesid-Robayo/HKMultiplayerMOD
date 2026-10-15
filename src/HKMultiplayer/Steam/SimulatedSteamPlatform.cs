using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Steam;

/// <summary>
/// Steam simulado para el modo de prueba (orden <c>steamsim</c>): cada copia del juego hace de una cuenta distinta, que
/// el Steam de verdad no permite en un solo PC (todas las copias son la misma cuenta). Los mensajes van por UDP en
/// 127.0.0.1 y las salas e invitaciones son archivos en una carpeta temporal compartida por las copias. Así se prueba
/// con las rutas todo lo que hace el mod con Steam: crear la sala, unirse por ella o por invitación, la conexión por
/// cuenta y la reconexión; solo la llamada a Steamworks queda para una prueba entre dos cuentas de verdad.
/// </summary>
internal sealed class SimulatedSteamPlatform : ISteamPlatform {
    /// <summary>Cuentas inventadas: un número cualquiera más el de la copia.</summary>
    private const ulong AccountBase = 76561190000000000UL;

    private const ulong LobbyBase = 109775240000000000UL;
    private const int PortBase = 27600;

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "hkmp-steamsim");

    private readonly int _index;
    private readonly Socket _socket;
    private readonly Dictionary<ulong, IPEndPoint> _endPoints = new();
    private EndPoint _any = new IPEndPoint(IPAddress.Loopback, 0);
    private Action<ulong> _pendingCreated;

    /// <summary>Sala privada a la que invitaron a esta cuenta (la única privada en la que puede entrar).</summary>
    private ulong _invitedTo;
    private float _nextInviteCheck;

    public ulong LocalId { get; }
    public ulong Lobby { get; private set; }
    public event Action<ulong> JoinRequested;

    public SimulatedSteamPlatform(int index) {
        _index = index;
        LocalId = AccountBase + (ulong) index;
        Directory.CreateDirectory(Folder);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
        try {
            const int sioUdpConnReset = -1744830452;
            _socket.IOControl(sioUdpConnReset, new byte[] { 0 }, null);
        } catch (Exception) {
        }

        _socket.Bind(new IPEndPoint(IPAddress.Loopback, PortBase + index));
        // Una invitación vieja de una prueba anterior no cuenta
        TryDelete(InviteFile(index));
    }

    /// <summary>Id de la sala que abre la cuenta número <paramref name="index"/> (cada cuenta tiene como mucho una).</summary>
    public static ulong LobbyOf(int index) => LobbyBase + (ulong) index;

    private static string LobbyFile(ulong lobby) => Path.Combine(Folder, $"lobby-{lobby}.txt");
    private static string InviteFile(int index) => Path.Combine(Folder, $"invite-{index}.txt");

    public void Send(ulong peer, byte[] buffer, int offset, int length) {
        if (peer <= AccountBase || peer > AccountBase + 99) {
            return;
        }

        if (!_endPoints.TryGetValue(peer, out var to)) {
            to = new IPEndPoint(IPAddress.Loopback, PortBase + (int) (peer - AccountBase));
            _endPoints[peer] = to;
        }

        try {
            _socket.SendTo(buffer, offset, length, SocketFlags.None, to);
        } catch (SocketException) {
            // Como un paquete perdido
        }
    }

    public int Receive(byte[] buffer, out ulong from) {
        from = 0;
        try {
            if (_socket.Available == 0) {
                return -1;
            }

            var length = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref _any);
            from = AccountBase + (ulong) (((IPEndPoint) _any).Port - PortBase);
            return length;
        } catch (SocketException) {
            return -1;
        }
    }

    public void Expect(ulong peer) {
    }

    // =====================================================================================================
    // Salas: un archivo por sala con sus datos (clave=valor)
    // =====================================================================================================

    public void CreateLobby(bool friendsCanJoin, Action<ulong> done) {
        LeaveLobby();
        Lobby = LobbyOf(_index);
        WriteLobby(new Dictionary<string, string> { ["owner"] = LocalId.ToString(), ["private"] = friendsCanJoin ? "0" : "1" });
        // Como en Steam, la respuesta llega después (en el siguiente frame)
        _pendingCreated = done;
    }

    public void JoinLobby(ulong lobby, Action<LobbyJoinResult> done) {
        LeaveLobby();
        // Como en Steam: en una sala privada solo entra quien recibió una invitación
        var result = !File.Exists(LobbyFile(lobby))
            ? LobbyJoinResult.Gone
            : ReadLobby(lobby).TryGetValue("private", out var hidden) && hidden == "1" && _invitedTo != lobby
                ? LobbyJoinResult.NotAllowed
                : LobbyJoinResult.Ok;
        if (result == LobbyJoinResult.Ok) {
            Lobby = lobby;
        }

        done?.Invoke(result);
    }

    public void LeaveLobby() {
        if (Lobby == 0) {
            return;
        }

        if (Lobby == LobbyOf(_index)) {
            TryDelete(LobbyFile(Lobby));
        }

        Lobby = 0;
    }

    private static Dictionary<string, string> ReadLobby(ulong lobby) {
        var data = new Dictionary<string, string>();
        try {
            foreach (var line in File.ReadAllLines(LobbyFile(lobby))) {
                var at = line.IndexOf('=');
                if (at > 0) {
                    data[line.Substring(0, at)] = line.Substring(at + 1);
                }
            }
        } catch (IOException) {
        }

        return data;
    }

    private void WriteLobby(Dictionary<string, string> data) {
        try {
            File.WriteAllLines(LobbyFile(Lobby), data.Select(pair => $"{pair.Key}={pair.Value}").ToArray());
        } catch (IOException e) {
            Log.Warn($"Steam simulado: no se pudo escribir la sala: {e.Message}");
        }
    }

    public void SetLobbyData(string key, string value) {
        if (Lobby != LobbyOf(_index)) {
            return;
        }

        var data = ReadLobby(Lobby);
        data[key] = value;
        WriteLobby(data);
    }

    public string GetLobbyData(string key) =>
        Lobby != 0 && ReadLobby(Lobby).TryGetValue(key, out var value) ? value : string.Empty;

    public void OpenInviteDialog() => Log.Info("Steam simulado: se abriría el panel para invitar (prueba: steaminvite <cuenta>)");

    /// <summary>Prueba: la cuenta <paramref name="index"/> recibe una invitación a la sala de esta y la acepta.</summary>
    public void Invite(int index) {
        if (Lobby == 0) {
            Log.Info("Steam simulado: no hay sala a la que invitar");
            return;
        }

        File.WriteAllText(InviteFile(index), Lobby.ToString());
    }

    public List<SteamFriendGame> FriendGames() {
        var list = new List<SteamFriendGame>();
        foreach (var file in Directory.GetFiles(Folder, "lobby-*.txt")) {
            var name = Path.GetFileNameWithoutExtension(file).Substring("lobby-".Length);
            if (!ulong.TryParse(name, out var lobby) || lobby == LobbyOf(_index)) {
                continue;
            }

            var data = ReadLobby(lobby);
            // Las privadas no salen en la lista de amigos
            if (data.TryGetValue("private", out var hidden) && hidden == "1") {
                continue;
            }

            list.Add(new SteamFriendGame { Name = data.TryGetValue("name", out var host) ? host : $"Cuenta {lobby - LobbyBase}", Lobby = lobby });
        }

        return list;
    }

    public void Update() {
        if (_pendingCreated != null) {
            var done = _pendingCreated;
            _pendingCreated = null;
            done(Lobby);
        }

        // Invitaciones aceptadas: dos veces por segundo, como el aviso de Steam al pulsar "Unirse"
        if (Time.unscaledTime < _nextInviteCheck) {
            return;
        }

        _nextInviteCheck = Time.unscaledTime + 0.5f;
        var invite = InviteFile(_index);
        if (!File.Exists(invite)) {
            return;
        }

        string text;
        try {
            text = File.ReadAllText(invite).Trim();
        } catch (IOException) {
            return;
        }

        TryDelete(invite);
        if (ulong.TryParse(text, out var lobby)) {
            Log.Info("Steam simulado: invitación aceptada");
            _invitedTo = lobby;
            JoinRequested?.Invoke(lobby);
        }
    }

    private static void TryDelete(string file) {
        try {
            File.Delete(file);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }

    public void Dispose() {
        LeaveLobby();
        _socket.Close();
    }
}
