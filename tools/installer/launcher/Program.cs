using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace HKMultiplayer.Setup;

/// <summary>
/// Abre el instalador del paquete (<c>scripts\setup.ps1</c>), como antes "HKMultiplayer Setup.bat", pero sin que se vea
/// una consola: con doble clic, la ventana para instalar, actualizar o desinstalar (se puede arrastrar la carpeta del
/// juego encima del .exe). En la consola, <c>/install</c> o <c>/uninstall</c> y la ruta del juego hacen el trabajo ahí
/// mismo y devuelven el código de salida del instalador.
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
        var script = Path.Combine(root, "scripts", "setup.ps1");
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        var console = mode is "/install" or "/uninstall";

        if (!File.Exists(script)) {
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

        return console ? RunInConsole(script, mode.Substring(1), args.Length > 1 ? args[1] : string.Empty)
            : OpenWindow(script, args.Length > 0 ? args[0] : string.Empty);
    }

    /// <summary>La ventana del instalador, sin consola. Este proceso acaba en seguida (como el .bat con "start").</summary>
    private static int OpenWindow(string script, string gameDir) {
        var start = PowerShell(script, $"-GameDir {Quote(gameDir)}");
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.CreateNoWindow = true;
        try {
            Process.Start(start);
            return 0;
        } catch (Exception e) {
            MessageBox.Show(Text("No se pudo abrir el instalador: ", "Could not open the installer: ") + e.Message,
                "HKMultiplayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>
    /// En la consola desde la que se llamó (o en una nueva, con una pausa al final para leerla): el instalador escribe ahí
    /// y su código de salida es el de este proceso.
    /// </summary>
    private static int RunInConsole(string script, string mode, string gameDir) {
        // Si la salida va a un archivo o a una tubería ("> registro.txt", la prueba del instalador), se escribe ahí: un
        // programa de ventana unido a la consola escribiría en la consola y el archivo se quedaba vacío
        var output = Redirected(StdOutput);
        var error = Redirected(StdError);
        var own = !AttachConsole(AttachParentProcess);
        if (own && output == null) {
            AllocConsole();
        } else {
            if (own) {
                // Salida a un archivo sin consola de la que colgarse: PowerShell abría la suya (visible y en la página de
                // códigos OEM: las tildes llegaban mal). Se crea una escondida en UTF-8 y PowerShell la hereda
                AllocConsole();
                ShowWindow(GetConsoleWindow(), HideWindow);
            }

            own = false;
        }

        // En UTF-8 (como el "chcp 65001" del .bat): el instalador escribe tildes y eñes. Su salida se recoge y se escribe
        // aquí
        SetConsoleOutputCP(Utf8CodePage);
        Console.OutputEncoding = new UTF8Encoding(false);
        var outWriter = output ?? Console.Out;
        var errorWriter = error ?? output ?? Console.Error;
        var start = PowerShell(script, $"-Mode {mode} -GameDir {Quote(gameDir)}");
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        int code;
        try {
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, line) => {
                if (line.Data != null) {
                    lock (outWriter) {
                        outWriter.WriteLine(line.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, line) => {
                if (line.Data != null) {
                    lock (outWriter) {
                        errorWriter.WriteLine(line.Data);
                    }
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            code = process.ExitCode;
        } catch (Exception e) {
            errorWriter.WriteLine(Text("No se pudo abrir el instalador: ", "Could not open the installer: ") + e.Message);
            code = 1;
        }

        outWriter.Flush();
        errorWriter.Flush();

        if (own) {
            Console.WriteLine();
            Console.WriteLine(Text("Pulsa Intro para cerrar.", "Press Enter to close."));
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

    private static ProcessStartInfo PowerShell(string script, string arguments) => new() {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script)} {arguments}",
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(script) ?? string.Empty
    };

    /// <summary>Entre comillas para la línea de órdenes (una ruta con espacios o vacía sigue siendo un solo argumento).</summary>
    private static string Quote(string value) {
        // Las barras justo antes de la comilla de cierre se duplican: si no, la última la escaparía ("C:\Juegos\" se leía
        // como C:\Juegos" y lo que sigue)
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value.Replace("\"", "\\\"") + new string('\\', trailing) + "\"";
    }

    private static string Text(string spanish, string english) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? spanish : english;
}
