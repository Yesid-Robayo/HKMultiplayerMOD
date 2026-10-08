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

    /// <summary>Tecla que abre y cierra el panel de conexión (nombre de <see cref="UnityEngine.KeyCode"/>).</summary>
    public string PanelKey { get; set; } = "F5";
}
