namespace HKMultiplayer.Net;

/// <summary>
/// Canales fiables ordenados e independientes de una conexión: perder un paquete en uno no retiene a los demás
/// (sin bloqueo de cabeza de línea). Se empaquetan por orden de prioridad en cada tick:
/// Combate → Mundo → Social → Datos grandes.
/// </summary>
public enum ReliableChannel : byte {
    /// <summary>Golpes, vida y eventos de enemigos, ataques del jugador, revivir y sombras.</summary>
    Combat = 0,

    /// <summary>Saludo, sesión, cambios de sala, objetos del escenario y progreso del mundo.</summary>
    World = 1,

    /// <summary>Chat, mensajes rápidos, pings, marcas del mapa y metadatos de skins.</summary>
    Social = 2,

    /// <summary>Transferencias grandes troceadas (mundo al unirse, personaje, skins).</summary>
    Bulk = 3
}

internal static class ReliableChannels {
    public const int Count = 4;
}
