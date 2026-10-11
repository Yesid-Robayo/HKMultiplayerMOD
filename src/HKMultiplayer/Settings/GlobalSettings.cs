namespace HKMultiplayer.Settings;

/// <summary>
/// Ajustes que se guardan entre partidas (archivo de ajustes globales de la Modding API, en la carpeta de datos del
/// juego; nunca se sube al repositorio).
/// </summary>
public sealed class GlobalSettings {
    /// <summary>Puerto por defecto para hostear.</summary>
    public const int DefaultPort = 26950;

    /// <summary>Nombre con el que te ven los demás.</summary>
    public string PlayerName { get; set; } = "Caballero";

    /// <summary>Última dirección a la que te uniste.</summary>
    public string LastAddress { get; set; } = "127.0.0.1";

    /// <summary>Puerto del host (para hostear y para unirse).</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// Identifica a tu personaje en los mundos de otros (se crea sola la primera vez). No es una contraseña: solo
    /// sirve para que el host sepa qué personaje es el tuyo al volver a unirte.
    /// </summary>
    public string PlayerKey { get; set; } = string.Empty;

    /// <summary>Skin propia: nombre de su carpeta (vacío = el caballero normal).</summary>
    public string SkinName { get; set; } = string.Empty;

    /// <summary>Si en este PC se ven las skins de los demás (si no, se les ve con el caballero normal).</summary>
    public bool ShowOthersSkins { get; set; } = true;

    /// <summary>
    /// Si al abrir el juego se mira en GitHub si hay una versión nueva del mod (una consulta a la lista pública de
    /// versiones, sin datos del jugador).
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>La versión del mod de la última vez que se abrió el juego (para avisar de que se actualizó).</summary>
    public string LastRunVersion { get; set; } = string.Empty;
}
