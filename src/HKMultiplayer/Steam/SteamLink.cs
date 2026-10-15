using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HKMultiplayer.Net.Transport;
using HKMultiplayer.Testing;
using HKMultiplayer.Util;

namespace HKMultiplayer.Steam;

/// <summary>Cómo acabó el intento de entrar en una sala de Steam.</summary>
internal enum LobbyJoinResult {
    Ok,

    /// <summary>La sala ya no existe (el host cerró la partida).</summary>
    Gone,

    /// <summary>Es privada y no hay invitación a la sala ("Solo invitados").</summary>
    NotAllowed,

    Full,
    Failed
}

/// <summary>Un amigo de Steam con una partida de HKMultiplayer abierta (en una sala de Steam).</summary>
internal sealed class SteamFriendGame {
    public string Name;
    public ulong Lobby;
}

/// <summary>
/// Lo que el mod usa de Steam: mensajes entre cuentas (para la conexión), salas solo para amigos (para encontrar la
/// partida y unirse) e invitaciones. Hay una implementación con el Steamworks del juego y otra simulada para el modo de
/// prueba, en la que cada copia del juego hace de una cuenta distinta.
/// </summary>
internal interface ISteamPlatform : IPeerNetwork, IDisposable {
    /// <summary>La cuenta de este jugador (su SteamID).</summary>
    ulong LocalId { get; }

    /// <summary>La sala de Steam en la que está (0: ninguna).</summary>
    ulong Lobby { get; }

    /// <summary>
    /// Crea una sala de hasta 4: solo para amigos (la ven y entran con "Unirse") o privada (solo entra quien recibe una
    /// invitación). <paramref name="done"/> recibe su id (0 si falló).
    /// </summary>
    void CreateLobby(bool friendsCanJoin, Action<ulong> done);

    /// <summary>Entra en esa sala. <paramref name="done"/> dice si se pudo, y si no, por qué.</summary>
    void JoinLobby(ulong lobby, Action<LobbyJoinResult> done);

    void LeaveLobby();

    /// <summary>Guarda un dato en la sala (solo su dueño). Los que entran lo leen con <see cref="GetLobbyData"/>.</summary>
    void SetLobbyData(string key, string value);

    /// <summary>Un dato de la sala en la que está (vacío si no lo tiene).</summary>
    string GetLobbyData(string key);

    /// <summary>Abre el panel de Steam para invitar a amigos a la sala.</summary>
    void OpenInviteDialog();

    /// <summary>Amigos que juegan a Hollow Knight con una sala abierta.</summary>
    List<SteamFriendGame> FriendGames();

    /// <summary>Acepta los mensajes de esa cuenta aunque no sea amiga ni esté en la sala (el host al que se une).</summary>
    void Expect(ulong peer);

    /// <summary>El jugador aceptó una invitación o pulsó "Unirse" en Steam: id de la sala.</summary>
    event Action<ulong> JoinRequested;

    /// <summary>Una vez por frame.</summary>
    void Update();
}

/// <summary>
/// Acceso a Steam. Con el juego de Steam se usa el Steamworks que trae el juego (ya iniciado por él); en la versión de
/// GOG no hay Steam y la conexión es solo por IP. En el modo de prueba no se usa el Steam de verdad (todas las copias
/// son la misma cuenta): se simula con <c>steamsim</c>.
/// </summary>
internal static class SteamLink {
    private static ISteamPlatform _platform;
    private static bool _tried;

    /// <summary>La conexión con Steam, o null si no hay.</summary>
    public static ISteamPlatform Platform {
        get {
            if (!_tried) {
                _tried = true;
                if (!TestDriver.IsEnabled()) {
                    _platform = TryReal();
                }
            }

            return _platform;
        }
    }

    public static bool Available => Platform != null;

    /// <summary>Modo de prueba: esta copia del juego hace de la cuenta de Steam número <paramref name="index"/>.</summary>
    public static void UseSimulated(int index) {
        _platform?.Dispose();
        _platform = new SimulatedSteamPlatform(index);
        _tried = true;
        Log.Info($"Steam simulado: cuenta {index}");
    }

    private static ISteamPlatform TryReal() {
        try {
            return CreateReal();
        } catch (Exception e) {
            // Sin el ensamblado de Steamworks (versión de GOG) falla aquí, al compilar CreateReal
            Log.Info($"Steam no disponible ({e.GetType().Name}): solo conexión por IP");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISteamPlatform CreateReal() => SteamworksPlatform.TryCreate();
}
