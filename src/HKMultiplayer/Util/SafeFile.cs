using System.IO;
using System.Text;

namespace HKMultiplayer.Util;

/// <summary>
/// Guardado que aguanta un cierre de golpe o un corte de luz (issue #19). El texto se escribe en un <c>.tmp</c>, se fuerza
/// al disco y solo entonces sustituye al archivo, cuya versión anterior queda como <c>.bak</c>. Sin forzarlo, Windows
/// puede renombrar el archivo antes de escribir su contenido, y tras un apagón queda vacío o lleno de ceros.
/// </summary>
internal static class SafeFile {
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void Write(string path, string text) {
        var temp = path + ".tmp";
        var bytes = Utf8.GetBytes(text);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                   FileOptions.WriteThrough)) {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        if (File.Exists(path)) {
            if (File.Exists(path + ".bak")) {
                File.Delete(path + ".bak");
            }

            File.Move(path, path + ".bak");
        }

        File.Move(temp, path);
    }

    /// <summary>
    /// Dónde buscar lo guardado, en orden: el último, su copia anterior y el <c>.tmp</c> que pudo quedar si el juego se
    /// cerró entre mover uno y otro.
    /// </summary>
    public static string[] Candidates(string path) => new[] { path, path + ".bak", path + ".tmp" };
}
