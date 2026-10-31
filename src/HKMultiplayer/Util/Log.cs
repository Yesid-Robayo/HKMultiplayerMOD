namespace HKMultiplayer.Util;

/// <summary>
/// Registro en el ModLog del juego, con el prefijo del mod y la hora: el ModLog de la API no la lleva, y sin ella no se
/// pueden cruzar los logs de dos jugadores.
/// </summary>
internal static class Log {
    private const string Prefix = "[HKMultiplayer] ";

    public static void Info(string message) => Modding.Logger.Log(Line(message));

    public static void Warn(string message) => Modding.Logger.LogWarn(Line(message));

    public static void Error(string message) => Modding.Logger.LogError(Line(message));

    private static string Line(string message) => $"{Prefix}{System.DateTime.Now:HH:mm:ss.fff} {message}";
}
