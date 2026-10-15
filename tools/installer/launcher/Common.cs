using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HKMultiplayer.Setup;

/// <summary>Textos en el idioma de Windows: español si es español; si no, inglés.</summary>
internal static class Lang {
    internal static readonly bool Spanish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

    internal static string L(string spanish, string english) => Spanish ? spanish : english;
}

/// <summary>Tipo de cada línea que escribe el instalador (en la ventana o en la consola, con su color).</summary>
internal enum LogKind {
    Step,
    Note,
    Warn,
    Ok,
    Error
}

/// <summary>Dónde escribe el instalador lo que va haciendo.</summary>
internal interface IOutput {
    void Write(LogKind kind, string text);
}

/// <summary>Error del instalador con un mensaje ya pensado para el jugador.</summary>
internal sealed class SetupException : Exception {
    internal SetupException(string message) : base(message) { }
}

/// <summary>Nombres, rutas y comprobaciones de la carpeta del juego que usan la instalación y la desinstalación.</summary>
internal static class GameFiles {
    internal const string ModName = "HKMultiplayer";

    /// <summary>Mod anterior: también abre la red, así que no puede estar a la vez que este.</summary>
    internal const string OldModName = "HollowKnightCoop";

    /// <summary>Carpeta (dentro de la del juego) con el respaldo de lo que cambió el instalador y la lista de cambios.</summary>
    internal const string BackupName = "HKMultiplayer-backup";

    internal const string ManifestName = "manifest.txt";
    internal const string ModsRelative = "hollow_knight_Data/Managed/Mods";

    /// <summary>Versión del juego para la que es la Modding API del paquete.</summary>
    internal const string GameVersion = "1.5.12620";

    private static readonly byte[] ModHooksBytes = Encoding.ASCII.GetBytes("ModHooks");

    /// <summary>La constante de versión del juego va en UTF-16: se busca byte a byte (vale con las dos alineaciones).</summary>
    private static readonly byte[] GameVersionBytes = Encoding.Unicode.GetBytes(GameVersion);

    internal static string Join(string gameDir, string relative) => Path.Combine(gameDir, relative.Replace('/', '\\'));

    internal static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Ruta que da el jugador: admite la del .exe, la de hollow_knight_Data o la de Managed, con o sin comillas. Devuelve la
    /// carpeta del juego (la que tiene hollow_knight.exe) o null.
    /// </summary>
    internal static string ToGameDir(string path) {
        try {
            if (string.IsNullOrWhiteSpace(path)) {
                return null;
            }

            path = path.Trim().Trim('"').Trim().TrimEnd('\\', '/');
            if (path.Length == 0) {
                return null;
            }

            // "C:" a secas es la carpeta actual de esa unidad, no su raíz
            if (path.EndsWith(":", StringComparison.Ordinal)) {
                path += "\\";
            }

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                path = Path.GetDirectoryName(path);
            }

            for (var i = 0; i < 3 && !string.IsNullOrEmpty(path); i++) {
                if (File.Exists(Path.Combine(path, "hollow_knight.exe"))) {
                    return Path.GetFullPath(path);
                }

                path = Path.GetDirectoryName(path);
            }
        } catch (ArgumentException) {
            // Caracteres que no valen en una ruta
        } catch (NotSupportedException) {
        } catch (IOException) {
        }

        return null;
    }

    /// <summary>Carpetas de Steam: la del registro, las bibliotecas de libraryfolders.vdf y las de siempre.</summary>
    private static List<string> SteamLibraries() {
        var roots = new List<string>();
        AddRegistryValue(roots, RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        AddRegistryValue(roots, RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        AddRegistryValue(roots, RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        var libraries = new List<string>();
        foreach (var root in roots) {
            libraries.Add(root);
            var vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
            try {
                if (!File.Exists(vdf)) {
                    continue;
                }

                foreach (var line in File.ReadAllLines(vdf, Encoding.UTF8)) {
                    var match = Regex.Match(line, "\"path\"\\s+\"([^\"]+)\"");
                    if (match.Success) {
                        libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                    }
                }
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        return libraries;
    }

    private static void AddRegistryValue(List<string> list, RegistryHive hive, string keyPath, string name) {
        try {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(keyPath);
            if (key?.GetValue(name) is string value && value.Length > 0) {
                list.Add(value.Replace('/', '\\'));
            }
        } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
        }
    }

    /// <summary>Todas las carpetas del juego que se encuentran (sin repetir): Steam, la desinstalación de Steam, GOG y rutas típicas.</summary>
    internal static List<string> FindGameDirs() {
        var candidates = SteamLibraries().Select(library => Path.Combine(library, @"steamapps\common\Hollow Knight")).ToList();
        AddRegistryValue(candidates, RegistryHive.LocalMachine,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 367520", "InstallLocation");
        AddRegistryValue(candidates, RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 367520", "InstallLocation");

        // Solo discos locales: una unidad de red desconectada haría esperar al instalador
        string[] relatives = {
            @"SteamLibrary\steamapps\common\Hollow Knight", @"Steam\steamapps\common\Hollow Knight",
            @"Games\Steam\steamapps\common\Hollow Knight", @"Juegos\Steam\steamapps\common\Hollow Knight",
            @"Program Files (x86)\Steam\steamapps\common\Hollow Knight", @"GOG Games\Hollow Knight",
            @"Program Files (x86)\GOG Galaxy\Games\Hollow Knight", @"Games\Hollow Knight", @"Juegos\Hollow Knight"
        };
        foreach (var drive in DriveInfo.GetDrives()) {
            try {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) {
                    continue;
                }
            } catch (IOException) {
                continue;
            }

            candidates.AddRange(relatives.Select(relative => Path.Combine(drive.RootDirectory.FullName, relative)));
        }

        var found = new List<string>();
        foreach (var candidate in candidates) {
            var game = ToGameDir(candidate);
            if (game != null && !found.Any(dir => string.Equals(dir, game, StringComparison.OrdinalIgnoreCase))) {
                found.Add(game);
            }
        }

        return found;
    }

    /// <summary>
    /// Copias del juego abiertas desde esa carpeta (otras copias en otras carpetas no molestan). Una de la que no se puede
    /// leer la ruta (abierta como administrador y el instalador no) cuenta: puede ser esta, y si no se decía, el bloqueo
    /// salía después como un error de archivo.
    /// </summary>
    private static int GameProcesses(string gameDir) {
        var prefix = gameDir.TrimEnd('\\') + "\\";
        var count = 0;
        foreach (var process in Process.GetProcessesByName("hollow_knight")) {
            using (process) {
                string path = null;
                try {
                    path = process.MainModule?.FileName;
                } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
                }

                if (string.IsNullOrEmpty(path) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    count++;
                }
            }
        }

        return count;
    }

    internal static void AssertGameClosed(string gameDir) {
        if (GameProcesses(gameDir) > 0) {
            throw new SetupException(Lang.L("Hollow Knight está abierto. Ciérralo y vuelve a intentarlo.",
                "Hollow Knight is running. Close it and try again."));
        }
    }

    /// <summary>
    /// Si un ensamblado lo parcheó la Modding API: los parcheados nombran ModHooks (Assembly-CSharp.dll) o MonoMod, con lo
    /// que se parchean (TeamCherry.Localization.dll no nombra ModHooks), y los del juego original ninguno de los dos. Así se
    /// sabe si un archivo que el instalador cambió sigue siendo el suyo o si Steam lo volvió a poner (al actualizar el juego
    /// o "Verificar integridad"), sin depender de huellas apuntadas (los manifiestos antiguos no las tienen). Hasta la 0.31.1
    /// solo se miraba ModHooks: al desinstalar se quedaba el TeamCherry.Localization.dll de la Modding API, tomado por uno
    /// nuevo del juego.
    /// </summary>
    internal static bool IsModded(string file) {
        if (!File.Exists(file)) {
            return false;
        }

        var data = File.ReadAllBytes(file);
        return Contains(data, ModHooksBytes) || Contains(data, MonoModBytes);
    }

    private static readonly byte[] MonoModBytes = Encoding.ASCII.GetBytes("MonoMod");

    /// <summary>Si el Assembly-CSharp.dll es de la versión del juego para la que es la Modding API del paquete.</summary>
    internal static bool IsGameVersion(string file) => Contains(File.ReadAllBytes(file), GameVersionBytes);

    private static bool Contains(byte[] data, byte[] pattern) {
        var last = data.Length - pattern.Length;
        for (var i = 0; i <= last; i++) {
            if (data[i] != pattern[0]) {
                continue;
            }

            var j = 1;
            while (j < pattern.Length && data[i + j] == pattern[j]) {
                j++;
            }

            if (j == pattern.Length) {
                return true;
            }
        }

        return false;
    }

    internal static string Sha256(string file) {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(file);
        return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
    }

    /// <summary>Versión de un HKMultiplayer.dll (la de su archivo, sin el ".0" del final): vacío si no está, "?" si no la tiene.</summary>
    internal static string DllVersion(string dll) {
        if (!File.Exists(dll)) {
            return string.Empty;
        }

        var version = FileVersionInfo.GetVersionInfo(dll).FileVersion;
        return string.IsNullOrEmpty(version) ? "?" : Regex.Replace(version, @"\.0$", string.Empty);
    }

    /// <summary>Versión del mod instalada en esa carpeta (vacío si no está).</summary>
    internal static string InstalledVersion(string gameDir) =>
        DllVersion(Join(gameDir, $"{ModsRelative}/{ModName}/{ModName}.dll"));

    /// <summary>Mensaje de un error para el jugador, con lo que hay que hacer si es de permisos o de un archivo en uso.</summary>
    internal static string ErrorText(Exception error) {
        var inner = error;
        while (inner.InnerException != null) {
            inner = inner.InnerException;
        }

        if (inner is UnauthorizedAccessException) {
            return Lang.L(
                $"Windows no deja escribir en la carpeta del juego. Cierra el instalador, haz clic derecho en 'HKMultiplayer Setup.exe' y elige 'Ejecutar como administrador'. ({inner.Message})",
                $"Windows won't let the installer write to the game folder. Close the installer, right-click 'HKMultiplayer Setup.exe' and choose 'Run as administrator'. ({inner.Message})");
        }

        // Archivo en uso (ERROR_SHARING_VIOLATION 32 o ERROR_LOCK_VIOLATION 33): por el código, porque el texto del
        // mensaje va en el idioma de Windows
        if (inner is IOException && (inner.HResult & 0xFFFF) is 32 or 33) {
            return Lang.L(
                $"Un archivo del juego está en uso. Cierra Hollow Knight (y Steam si sigue bloqueado) y vuelve a probar. ({inner.Message})",
                $"A game file is in use. Close Hollow Knight (and Steam if it is still locked) and try again. ({inner.Message})");
        }

        return error.Message;
    }

    /// <summary>Copia un archivo encima de otro aunque el de destino sea de solo lectura.</summary>
    internal static void CopyFile(string source, string destination) {
        if (File.Exists(destination)) {
            var attributes = File.GetAttributes(destination);
            if ((attributes & FileAttributes.ReadOnly) != 0) {
                File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
            }
        }

        File.Copy(source, destination, true);
    }

    internal static void DeleteFile(string path) {
        if (!File.Exists(path)) {
            return;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    /// <summary>Borra una carpeta con todo lo que tiene, también los archivos de solo lectura.</summary>
    internal static void DeleteTree(string path) {
        if (!Directory.Exists(path)) {
            return;
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }

    /// <summary>Mueve un archivo o una carpeta.</summary>
    internal static void Move(string from, string to) {
        if (Directory.Exists(from)) {
            Directory.Move(from, to);
        } else {
            File.Move(from, to);
        }
    }
}
