using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HKMultiplayer.Net;
using HKMultiplayer.Util;
using Steamworks;

namespace HKMultiplayer.Steam;

/// <summary>
/// Steam de verdad, con el Steamworks.NET que trae el juego. El juego ya inicia Steam (SteamOnlineSubsystem) y llama a
/// SteamAPI.RunCallbacks en cada frame, que es lo que entrega aquí los avisos (salas, invitaciones, peticiones de
/// conexión).
/// <para>
/// La conexión usa SteamNetworkingMessages: mensajes sin garantía de entrega entre cuentas, como UDP, que Steam lleva
/// directo o por sus relés si el router no deja (así nadie tiene que abrir puertos). La fiabilidad y el cifrado con el
/// código de la partida siguen siendo los del mod.
/// </para>
/// </summary>
internal sealed class SteamworksPlatform : ISteamPlatform {
    private const uint HollowKnightAppId = 367520;

    /// <summary>Sin agrupar mensajes (cada tick sale al momento) y reabriendo la sesión si Steam la cerró.</summary>
    private const int SendFlags = Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoNagle |
                                  Steamworks.Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

    private const int Channel = 0;
    private const int MaxLobbyMembers = 4;

    private readonly IntPtr[] _messages = new IntPtr[1];

    /// <summary>Búfer fijo fuera del recolector para enviar sin crear nada por paquete.</summary>
    private readonly IntPtr _sendBuffer = Marshal.AllocHGlobal(NetConstants.MaxDatagramSize);

    private readonly HashSet<ulong> _expected = new();
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
    private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
    private readonly Callback<GameRichPresenceJoinRequested_t> _presenceJoinRequested;
    private readonly CallResult<LobbyCreated_t> _lobbyCreated;
    private readonly CallResult<LobbyEnter_t> _lobbyEntered;
    private Action<ulong> _onCreated;
    private Action<bool> _onEntered;
    private bool _friendsCanJoin;
    private bool _connectShown;
    private bool _disposed;

    public ulong LocalId { get; }
    public ulong Lobby { get; private set; }
    public event Action<ulong> JoinRequested;

    private SteamworksPlatform(ulong localId) {
        LocalId = localId;
        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(request => {
            Log.Info("Steam: invitación o \"Unirse\" a una partida de un amigo");
            JoinRequested?.Invoke(request.m_steamIDLobby.m_SteamID);
        });
        // "Unirse a la partida" o "Invitar a jugar" desde la lista de amigos de Steam: llega la presencia "connect" del host
        _presenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(request => {
            if (TryParseConnect(request.m_rgchConnect, out var lobby)) {
                Log.Info("Steam: \"Unirse a la partida\" desde la lista de amigos");
                JoinRequested?.Invoke(lobby);
            }
        });
        _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
    }

    /// <summary>La conexión con Steam si el juego lo inició (el de Steam); si no, null.</summary>
    public static ISteamPlatform TryCreate() {
        try {
            if (!SteamAPI.IsSteamRunning()) {
                Log.Info("Steam no está abierto: solo conexión por IP");
                return null;
            }

            var id = SteamUser.GetSteamID();
            if (!id.IsValid()) {
                return null;
            }

            Log.Info("Steam disponible: salas e invitaciones de Steam activas");
            // Prepara ya los relés de Steam: si no, la primera conexión espera a que se preparen
            SteamNetworkingUtils.InitRelayNetworkAccess();
            AllowLargeTransfers();
            return new SteamworksPlatform(id.m_SteamID);
        } catch (InvalidOperationException) {
            // El juego no inició Steam (p. ej. abierto sin Steam)
            Log.Info("Steam no se inició en el juego: solo conexión por IP");
            return null;
        }
    }

    /// <summary>Ritmo de envío más alto que deja Steam por conexión (por defecto, 1 MB/s).</summary>
    private const int MaxSendRate = 4 * 1024 * 1024;

    /// <summary>Lo que Steam guarda para enviar antes de descartar (por defecto, 512 KB).</summary>
    private const int SendBuffer = 2 * 1024 * 1024;

    /// <summary>
    /// Deja que Steam envíe tan rápido como la red del mod: el mundo al unirse y las skins salen a unos 2-2,6 MB/s, y
    /// con el tope de Steam por defecto se quedaban en 1 MB/s y, con su búfer lleno, Steam descartaba paquetes que luego
    /// había que repetir. Steam sigue bajando el ritmo solo si la conexión no da para más.
    /// </summary>
    private static void AllowLargeTransfers() {
        SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, MaxSendRate);
        SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, SendBuffer);
    }

    private static void SetGlobal(ESteamNetworkingConfigValue setting, int value) {
        var argument = Marshal.AllocHGlobal(sizeof(int));
        try {
            Marshal.WriteInt32(argument, value);
            if (!SteamNetworkingUtils.SetConfigValue(setting, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, argument)) {
                Log.Warn($"Steam no aceptó el ajuste de red {setting} = {value}");
            }
        } finally {
            Marshal.FreeHGlobal(argument);
        }
    }

    // =====================================================================================================
    // Mensajes
    // =====================================================================================================

    private static SteamNetworkingIdentity Identity(ulong peer) {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(new CSteamID(peer));
        return identity;
    }

    public void Send(ulong peer, byte[] buffer, int offset, int length) {
        if (_disposed || length > NetConstants.MaxDatagramSize) {
            return;
        }

        Marshal.Copy(buffer, offset, _sendBuffer, length);
        var identity = Identity(peer);
        SteamNetworkingMessages.SendMessageToUser(ref identity, _sendBuffer, (uint) length, SendFlags, Channel);
    }

    public int Receive(byte[] buffer, out ulong from) {
        from = 0;
        while (!_disposed && SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _messages, 1) > 0) {
            var pointer = _messages[0];
            try {
                var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(pointer);
                var length = message.m_cbSize;
                if (length <= 0 || length > buffer.Length) {
                    // No cabe en un datagrama del mod: no es nuestro o viene mal hecho
                    continue;
                }

                from = message.m_identityPeer.GetSteamID().m_SteamID;
                Marshal.Copy(message.m_pData, buffer, 0, length);
                return length;
            } finally {
                SteamNetworkingMessage_t.Release(pointer);
            }
        }

        return -1;
    }

    /// <summary>
    /// Alguien quiere hablar con este jugador. Solo se acepta a amigos, a los de la sala y al host al que se une: un
    /// desconocido ni siquiera llega al saludo de la conexión (que además pide el código).
    /// </summary>
    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request) {
        var identity = request.m_identityRemote;
        var peer = identity.GetSteamID();
        if (_expected.Contains(peer.m_SteamID) || InLobby(peer) ||
            SteamFriends.GetFriendRelationship(peer) == EFriendRelationship.k_EFriendRelationshipFriend) {
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        } else {
            Log.Info("Steam: petición de conexión de alguien que no es amigo ni está en la sala, ignorada");
        }
    }

    private bool InLobby(CSteamID peer) {
        if (Lobby == 0) {
            return false;
        }

        var lobby = new CSteamID(Lobby);
        var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (var i = 0; i < count; i++) {
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == peer) {
                return true;
            }
        }

        return false;
    }

    public void Expect(ulong peer) => _expected.Add(peer);

    /// <summary>La sala de un "+connect_lobby &lt;id&gt;" (la presencia "connect" o la línea de órdenes del juego).</summary>
    public static bool TryParseConnect(string text, out ulong lobby) {
        lobby = 0;
        var parts = (text ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(parts, "+connect_lobby");
        return at >= 0 && at + 1 < parts.Length && ulong.TryParse(parts[at + 1], out lobby) && lobby != 0;
    }

    // =====================================================================================================
    // Salas
    // =====================================================================================================

    public void CreateLobby(bool friendsCanJoin, Action<ulong> done) {
        LeaveLobby();
        _onCreated = done;
        _friendsCanJoin = friendsCanJoin;
        var type = friendsCanJoin ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePrivate;
        _lobbyCreated.Set(SteamMatchmaking.CreateLobby(type, MaxLobbyMembers));
    }

    private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure) {
        var done = _onCreated;
        _onCreated = null;
        if (ioFailure || result.m_eResult != EResult.k_EResultOK) {
            Log.Warn($"Steam: no se pudo crear la sala ({(ioFailure ? "sin conexión con Steam" : result.m_eResult.ToString())})");
            done?.Invoke(0);
            return;
        }

        Lobby = result.m_ulSteamIDLobby;
        // Con la presencia "connect", Steam enseña "Unirse a la partida" e "Invitar a jugar" en la lista de amigos (sin
        // ella solo sale "Invitar a ver"). En una sala privada no se pone: solo se entra con invitación
        if (_friendsCanJoin) {
            SteamFriends.SetRichPresence("connect", $"+connect_lobby {Lobby}");
            _connectShown = true;
        }

        done?.Invoke(Lobby);
    }

    public void JoinLobby(ulong lobby, Action<bool> done) {
        LeaveLobby();
        _onEntered = done;
        _lobbyEntered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby)));
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure) {
        var done = _onEntered;
        _onEntered = null;
        // 1 = k_EChatRoomEnterResponseSuccess
        var ok = !ioFailure && result.m_EChatRoomEnterResponse == 1;
        if (ok) {
            Lobby = result.m_ulSteamIDLobby;
        } else {
            Log.Warn($"Steam: no se pudo entrar en la sala (respuesta {result.m_EChatRoomEnterResponse})");
        }

        done?.Invoke(ok);
    }

    public void LeaveLobby() {
        if (Lobby == 0) {
            return;
        }

        SteamMatchmaking.LeaveLobby(new CSteamID(Lobby));
        Lobby = 0;
        if (_connectShown) {
            SteamFriends.SetRichPresence("connect", string.Empty);
            _connectShown = false;
        }
    }

    public void SetLobbyData(string key, string value) {
        if (Lobby != 0) {
            SteamMatchmaking.SetLobbyData(new CSteamID(Lobby), key, value);
        }
    }

    public string GetLobbyData(string key) =>
        Lobby != 0 ? SteamMatchmaking.GetLobbyData(new CSteamID(Lobby), key) ?? string.Empty : string.Empty;

    public void OpenInviteDialog() {
        if (Lobby != 0) {
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(Lobby));
        }
    }

    public List<SteamFriendGame> FriendGames() {
        var list = new List<SteamFriendGame>();
        var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (var i = 0; i < count; i++) {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (SteamFriends.GetFriendGamePlayed(friend, out var game) && game.m_gameID.AppID().m_AppId == HollowKnightAppId &&
                game.m_steamIDLobby.IsValid()) {
                list.Add(new SteamFriendGame { Name = SteamFriends.GetFriendPersonaName(friend), Lobby = game.m_steamIDLobby.m_SteamID });
            }
        }

        return list;
    }

    public void Update() {
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        LeaveLobby();
        _sessionRequest.Dispose();
        _joinRequested.Dispose();
        _presenceJoinRequested.Dispose();
        _lobbyCreated.Dispose();
        _lobbyEntered.Dispose();
        Marshal.FreeHGlobal(_sendBuffer);
    }
}
