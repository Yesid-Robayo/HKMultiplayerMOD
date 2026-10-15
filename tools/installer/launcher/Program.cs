using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using static HKMultiplayer.Setup.Lang;

namespace HKMultiplayer.Setup;

/// <summary>
/// Instalador de HKMultiplayer: con doble clic, la ventana para instalar, actualizar o desinstalar (se puede arrastrar la
/// carpeta del juego encima del .exe). En la consola, <c>/install</c> o <c>/uninstall</c> y la ruta del juego hacen el
/// trabajo ahí mismo y devuelven el código de salida (0 bien, 1 error, 2 desinstalado con algo sin devolver).
/// <para>
/// Todo el trabajo lo hace este programa. Hasta la 0.30.0 solo abría un instalador de PowerShell escondido con
/// <c>-ExecutionPolicy Bypass</c>, que es justo lo que vigilan los antivirus. Tampoco lleva dentro la versión del mod (la
/// lee del paquete): así es el mismo archivo en todas las versiones y SmartScreen le va sumando reputación.
/// </para>
/// </summary>
internal static class Program {
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    private const int HideWindow = 0;

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);

    private const uint Utf8CodePage = 65001;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint FileTypeDisk = 1;
    private const uint FileTypePipe = 3;

    [STAThread]
    private static int Main(string[] args) {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        var console = mode is "/install" or "/uninstall";

        if (!Installer.PackagePresent(root)) {
            // Abierto desde dentro del .zip (Windows solo saca este archivo) o con el paquete incompleto
            const string missing =
                "No encuentro los archivos del instalador. Descomprime primero el paquete entero (clic derecho, Extraer todo).\n\n" +
                "The installer files are missing. Extract the whole package first (right-click, Extract All).";
            if (console && AttachConsole(AttachParentProcess)) {
                Console.Error.WriteLine(missing);
            } else {
                MessageBox.Show(missing, "HKMultiplayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }

        var version = Installer.PackageVersion(root);
        if (console) {
            return RunInConsole(root, version, mode.Substring(1), args.Length > 1 ? args[1] : string.Empty);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try {
            Application.Run(new SetupForm(root, version, args.Length > 0 ? args[0] : string.Empty));
        } catch (Exception e) {
            MessageBox.Show(GameFiles.ErrorText(e), "HKMultiplayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        return 0;
    }

    /// <summary>El icono del instalador con todos sus tamaños (el de la barra de tareas es más grande que el del título).</summary>
    internal static Icon LoadIcon() {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("HKMultiplayer.Setup.setup.ico");
        return stream != null ? new Icon(stream) : null;
    }

    /// <summary>
    /// En la consola desde la que se llamó (o en una nueva, con una pausa al final para leerla): el instalador escribe ahí y
    /// su código de salida es el de este proceso.
    /// </summary>
    private static int RunInConsole(string root, string version, string mode, string given) {
        // Si la salida va a un archivo o a una tubería ("> registro.txt", la prueba del instalador), se escribe ahí: un
        // programa de ventana unido a la consola escribiría en la consola y el archivo se quedaba vacío
        var output = Redirected(StdOutput);
        var error = Redirected(StdError);
        var own = !AttachConsole(AttachParentProcess);
        if (own && output == null) {
            AllocConsole();
        } else {
            if (own) {
                // Salida a un archivo sin consola de la que colgarse: se crea una escondida para tener entrada
                AllocConsole();
                ShowWindow(GetConsoleWindow(), HideWindow);
            }

            own = false;
        }

        // En UTF-8: el instalador escribe tildes y eñes
        SetConsoleOutputCP(Utf8CodePage);
        Console.OutputEncoding = new UTF8Encoding(false);
        var writer = new ConsoleOutput(output ?? Console.Out, error ?? output ?? Console.Error, output == null);
        var code = new ConsoleRun(root, version, writer).Run(mode, given);
        writer.Flush();

        if (own) {
            Console.WriteLine();
            Console.WriteLine(L("Pulsa Intro para cerrar.", "Press Enter to close."));
            Console.ReadLine();
        }

        return code;
    }

    /// <summary>El archivo o la tubería al que va esa salida estándar desde que se abrió este proceso, o null.</summary>
    private static TextWriter Redirected(int which) {
        var handle = GetStdHandle(which);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) {
            return null;
        }

        var type = GetFileType(handle);
        if (type != FileTypeDisk && type != FileTypePipe) {
            return null;
        }

        var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(handle, false), FileAccess.Write);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }
}

/// <summary>Salida en la consola, con los colores de cada tipo de línea si es una consola de verdad.</summary>
internal sealed class ConsoleOutput : IOutput {
    private readonly TextWriter _out;
    private readonly TextWriter _error;
    private readonly bool _colors;

    internal ConsoleOutput(TextWriter output, TextWriter error, bool colors) {
        _out = output;
        _error = error;
        _colors = colors;
    }

    public void Write(LogKind kind, string text) {
        var (color, line) = kind switch {
            LogKind.Step => (ConsoleColor.Cyan, text),
            LogKind.Warn => (ConsoleColor.Yellow, "  " + text),
            LogKind.Ok => (ConsoleColor.Green, text),
            LogKind.Error => (ConsoleColor.Red, text),
            _ => (ConsoleColor.Gray, "  " + text)
        };
        var target = kind == LogKind.Error ? _error : _out;
        if (_colors && kind != LogKind.Note) {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            target.WriteLine(line);
            Console.ForegroundColor = previous;
        } else {
            target.WriteLine(line);
        }
    }

    /// <summary>Una línea sin formato (cabecera, preguntas, avisos finales).</summary>
    internal void Plain(string text) => _out.WriteLine(text);

    internal string Ask(string prompt) {
        _out.Write(prompt + ": ");
        return Console.ReadLine() ?? string.Empty;
    }

    internal void Flush() {
        _out.Flush();
        _error.Flush();
    }
}

/// <summary>Instalar o desinstalar desde la consola, sin ventana (para pruebas o para quien lo prefiera).</summary>
internal sealed class ConsoleRun {
    private readonly string _root;
    private readonly string _version;
    private readonly ConsoleOutput _console;

    internal ConsoleRun(string root, string version, ConsoleOutput console) {
        _root = root;
        _version = version;
        _console = console;
    }

    internal int Run(string mode, string given) {
        var install = mode == "install";
        _console.Write(LogKind.Step, $"=== HKMultiplayer {_version}: {(install ? L("instalación", "install") : L("desinstalación", "uninstall"))} ===");
        _console.Plain(string.Empty);
        var installer = new Installer(_root, _console);
        try {
            var game = ResolveGameDir(given);
            if (install) {
                installer.Install(game);
                _console.Plain(string.Empty);
                _console.Write(LogKind.Ok, L("Instalación completa.", "Installation complete."));
                _console.Plain(L("Abre Hollow Knight desde Steam: en el menú principal aparece \"Multijugador\".",
                    "Open Hollow Knight from Steam: \"Multiplayer\" appears in the main menu."));
                _console.Plain(L("Cómo hostear, unirse y abrir el puerto: LEEME.txt.", "How to host, join and open the port: README.txt."));
                return 0;
            }

            var clean = installer.Uninstall(game);
            _console.Plain(string.Empty);
            if (!clean) {
                return 2;
            }

            _console.Write(LogKind.Ok, L("Desinstalado: el juego vuelve a estar como antes de instalar HKMultiplayer.",
                "Uninstalled: the game is back to how it was before installing HKMultiplayer."));
            return 0;
        } catch (Exception e) {
            _console.Plain(string.Empty);
            var what = install ? L("No se pudo instalar: ", "Could not install: ") : L("No se pudo desinstalar: ", "Could not uninstall: ");
            _console.Write(LogKind.Error, what + GameFiles.ErrorText(e));
            if (installer.Changing) {
                _console.Write(LogKind.Error, L("Si se quedó a medias, la desinstalación deja el juego como estaba.",
                    "If it stopped halfway, uninstalling puts the game back as it was."));
            }

            return 1;
        }
    }

    /// <summary>
    /// Carpeta del juego: la que se pasa (o se arrastra sobre el .exe); si no, la que se encuentra; si hay varias o ninguna,
    /// se pregunta.
    /// </summary>
    private string ResolveGameDir(string given) {
        if (given.Length > 0) {
            return GameFiles.ToGameDir(given) ?? throw new SetupException(L(
                $"En '{given}' no está hollow_knight.exe. Indica la carpeta del juego (la que tiene hollow_knight.exe).",
                $"hollow_knight.exe is not in '{given}'. Give the game folder (the one with hollow_knight.exe)."));
        }

        var found = GameFiles.FindGameDirs();
        if (found.Count == 1) {
            return found[0];
        }

        if (found.Count > 1) {
            _console.Plain(L("Encontré Hollow Knight en varias carpetas:", "Hollow Knight was found in several folders:"));
            for (var i = 0; i < found.Count; i++) {
                _console.Plain($"  {i + 1}. {found[i]}");
            }

            var answer = _console.Ask(L($"Escribe el número de la carpeta que quieres usar (1-{found.Count})",
                $"Type the number of the folder to use (1-{found.Count})"));
            if (int.TryParse(answer.Trim(), out var index) && index >= 1 && index <= found.Count) {
                return found[index - 1];
            }

            throw new SetupException(L("No elegiste ninguna carpeta.", "No folder was chosen."));
        }

        _console.Plain(L("No encontré la carpeta del juego.", "The game folder wasn't found."));
        _console.Plain(L("En Steam: clic derecho en Hollow Knight > Administrar > Ver archivos locales, y copia la ruta de esa carpeta.",
            "In Steam: right-click Hollow Knight > Manage > Browse local files, and copy the path of that folder."));
        var path = _console.Ask(L("Pega aquí la ruta de la carpeta de Hollow Knight (la que tiene hollow_knight.exe)",
            "Paste the Hollow Knight folder path here (the one with hollow_knight.exe)"));
        return GameFiles.ToGameDir(path) ?? throw new SetupException(L($"En '{path}' no está hollow_knight.exe.",
            $"hollow_knight.exe is not in '{path}'."));
    }
}
