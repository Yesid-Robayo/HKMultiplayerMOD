namespace HKMultiplayer.Net;

/// <summary>
/// Constantes del protocolo de red.
/// </summary>
public static class NetConstants {
    /// <summary>Firma al principio de cada datagrama ("HKMU"): descarta tráfico que no es del mod.</summary>
    public const uint Magic = 0x554D4B48;

    /// <summary>Versión del protocolo: si no coincide, no se puede conectar.</summary>
    public const ushort ProtocolVersion = 2;

    /// <summary>
    /// Tamaño máximo de un datagrama. 1200 bytes caben sin fragmentarse en casi cualquier red (también por VPN).
    /// </summary>
    public const int MaxDatagramSize = 1200;

    /// <summary>Tamaño máximo de un mensaje normal; los mayores van por la transferencia de datos grandes.</summary>
    public const int MaxMessageSize = 1000;

    /// <summary>Bytes de la etiqueta de autenticación de ChaCha20-Poly1305.</summary>
    public const int TagSize = 16;

    /// <summary>Cabecera en claro de un paquete de datos: firma (4) + tipo (1) + secuencia (4).</summary>
    public const int DataHeaderSize = 9;

    /// <summary>Paquetes que se envían por segundo a cada conexión.</summary>
    public const int TickRate = 60;

    /// <summary>
    /// Sin recibir nada durante este tiempo, la conexión se da por perdida. Con 10 s, un microcorte de la red (wifi, Steam
    /// cambiando de relé) echaba al invitado y tenía que volver a entrar en el mundo entero (issue #29): ahora aguanta como
    /// los juegos en línea, con el aviso en pantalla (<c>ConnectionHud</c>), y al volver la red los mensajes fiables que
    /// quedaron pendientes llegan en orden (no se pierde nada). Con 20 s, un corte de unos 25 s de Steam echaba al
    /// invitado justo antes de volver la red: ahora son 30.
    /// </summary>
    public const float TimeoutSeconds = 30f;

    /// <summary>Mensajes fiables que pueden estar enviados sin confirmar a la vez.</summary>
    public const int ReliableWindow = 1024;

    /// <summary>
    /// Paquetes por tick como mucho cuando hay mensajes fiables pendientes (transferencias grandes). No pasa de los 64
    /// que cubre el campo de acuses, para que la respuesta del otro lado confirme la ráfaga entera.
    /// </summary>
    public const int MaxPacketsPerTick = 56;

    /// <summary>Conexiones a medio abrir (sin completar el saludo) que se aceptan a la vez.</summary>
    public const int MaxPendingConnections = 8;
}

/// <summary>Tipo de datagrama (byte después de la firma).</summary>
public enum DatagramType : byte {
    /// <summary>Cliente → servidor: quiere conectarse (claves públicas efímeras).</summary>
    Hello = 1,

    /// <summary>Servidor → cliente: su clave pública y una prueba cifrada de que conoce el código.</summary>
    Challenge = 2,

    /// <summary>Paquete de datos cifrado.</summary>
    Data = 3
}

/// <summary>Por qué terminó una conexión.</summary>
public enum DisconnectReason : byte {
    /// <summary>Uno de los dos la cerró.</summary>
    Closed = 0,

    /// <summary>Dejaron de llegar paquetes.</summary>
    Timeout = 1,

    /// <summary>El código de la partida no es el correcto.</summary>
    WrongCode = 2,

    /// <summary>Versión del mod distinta.</summary>
    VersionMismatch = 3,

    /// <summary>La partida está llena.</summary>
    Full = 4,

    /// <summary>El host rechazó la conexión por otro motivo.</summary>
    Rejected = 5,

    /// <summary>El host expulsó a este jugador de la partida.</summary>
    Kicked = 6
}
