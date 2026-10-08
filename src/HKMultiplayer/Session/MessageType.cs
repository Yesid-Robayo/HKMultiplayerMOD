namespace HKMultiplayer.Session;

/// <summary>
/// Tipos de mensaje de la sesión y de las capas de juego (primer byte de cada mensaje, desde 16). Cómo se envía cada
/// uno (fiable o no) y a quién lo reenvía el servidor está en <see cref="MessageRoutes"/>.
/// </summary>
public enum MessageType : byte {
    // ---- Sesión (servidor → clientes, sin remitente) ----

    /// <summary>Al unirse: id propio y lista de jugadores ya conectados con su sala.</summary>
    Welcome = 16,

    /// <summary>Un jugador se unió.</summary>
    PlayerJoined = 17,

    /// <summary>Un jugador se fue.</summary>
    PlayerLeft = 18,

    /// <summary>Un jugador cambió de sala.</summary>
    PlayerScene = 19,

    /// <summary>Cambió quién manda en una sala.</summary>
    SceneOwner = 20,

    /// <summary>Latencia de cada jugador con el host (cada segundo).</summary>
    Pings = 21,

    // ---- Sesión (cliente → servidor) ----

    /// <summary>El jugador local entró en otra sala.</summary>
    EnterScene = 24,

    // ---- Juego (con remitente al reenviarse) ----

    /// <summary>Estado continuo del jugador (posición, animación...).</summary>
    PlayerState = 32,

    /// <summary>Evento del jugador (efectos, golpes recibidos...).</summary>
    PlayerEvent = 33,

    /// <summary>Primer tipo libre para las capas de juego.</summary>
    FirstGameType = 40
}

/// <summary>A quién reenvía el servidor un mensaje que le llega de un jugador.</summary>
public enum Route : byte {
    /// <summary>Solo lo procesa el servidor.</summary>
    Server,

    /// <summary>A los demás jugadores que están en la misma sala que quien lo envió.</summary>
    SameScene,

    /// <summary>A todos los demás jugadores.</summary>
    Everyone,

    /// <summary>Al jugador que manda en la sala de quien lo envió (p. ej. golpes a enemigos).</summary>
    SceneOwner
}

/// <summary>
/// Cómo viaja cada tipo de mensaje: fiable o no y a quién se reenvía. Las capas de juego registran sus tipos al
/// iniciar; los no registrados solo los procesa el servidor y van por el canal fiable.
/// </summary>
public static class MessageRoutes {
    private static readonly Route[] Routes = new Route[256];
    private static readonly bool[] Unreliable = new bool[256];

    static MessageRoutes() {
        Register(MessageType.PlayerState, Route.SameScene, reliable: false);
        Register(MessageType.PlayerEvent, Route.SameScene, reliable: true);
    }

    public static void Register(MessageType type, Route route, bool reliable) {
        Routes[(byte) type] = route;
        Unreliable[(byte) type] = !reliable;
    }

    public static Route RouteOf(byte type) => Routes[type];

    public static bool IsReliable(byte type) => !Unreliable[type];
}
