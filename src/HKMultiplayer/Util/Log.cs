namespace HKMultiplayer.Util;

/// <summary>Registro en el ModLog del juego, con el prefijo del mod.</summary>
internal static class Log {
    private const string Prefix = "[HKMultiplayer] ";

    public static void Info(string message) => Modding.Logger.Log(Prefix + message);

    public static void Warn(string message) => Modding.Logger.LogWarn(Prefix + message);

    public static void Error(string message) => Modding.Logger.LogError(Prefix + message);
}
