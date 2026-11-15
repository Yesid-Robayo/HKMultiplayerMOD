using System;
using System.IO;
using System.Linq;
using HKMultiplayer.Util;

namespace HKMultiplayer.Tests;

/// <summary>Guardado seguro de los mundos (issue #19): el anterior queda como copia y no quedan restos.</summary>
public static partial class Program {
    private static void RunSafeFileTests() {
        Run("Guardado seguro: el nuevo en su sitio, el anterior como .bak y sin .tmp", SafeFileKeepsBackup);
        Run("Guardado seguro: sustituye un archivo dañado y lo deja como .bak", SafeFileReplacesDamaged);
    }

    private static string TempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "hkm-safefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeFileKeepsBackup() {
        var dir = TempDir();
        try {
            var path = Path.Combine(dir, "info.json");
            SafeFile.Write(path, "{\"Name\":\"uno\"}");
            Assert(File.ReadAllText(path) == "{\"Name\":\"uno\"}", "el primer guardado no quedó");
            Assert(!File.Exists(path + ".bak"), "el primer guardado dejó una copia sin haber anterior");

            SafeFile.Write(path, "{\"Name\":\"dos\"}");
            SafeFile.Write(path, "{\"Name\":\"tres\"}");
            Assert(File.ReadAllText(path) == "{\"Name\":\"tres\"}", "el último guardado no quedó en su sitio");
            Assert(File.ReadAllText(path + ".bak") == "{\"Name\":\"dos\"}", "la copia no es el guardado anterior");
            Assert(!File.Exists(path + ".tmp"), "quedó el .tmp");

            var bytes = File.ReadAllBytes(path);
            Assert(bytes[0] == (byte) '{', "el archivo empieza con una marca de UTF-8 u otra cosa");
            Assert(SafeFile.Candidates(path).SequenceEqual(new[] { path, path + ".bak", path + ".tmp" }),
                "el orden de lectura no es último, copia y .tmp");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    private static void SafeFileReplacesDamaged() {
        var dir = TempDir();
        try {
            var path = Path.Combine(dir, "world.json");
            // Como lo deja un apagón: el tamaño correcto, pero todo ceros
            File.WriteAllBytes(path, new byte[64]);
            File.WriteAllText(path + ".tmp", "{\"resto\":");
            SafeFile.Write(path, "{\"ok\":true}");
            Assert(File.ReadAllText(path) == "{\"ok\":true}", "no sustituyó el archivo dañado");
            Assert(File.ReadAllBytes(path + ".bak").All(b => b == 0), "el dañado no quedó como copia");
            Assert(!File.Exists(path + ".tmp"), "quedó el .tmp viejo");
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
