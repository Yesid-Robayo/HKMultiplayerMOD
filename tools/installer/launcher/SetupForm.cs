using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using static HKMultiplayer.Setup.Lang;

namespace HKMultiplayer.Setup;

/// <summary>
/// Ventana del instalador: elegir la carpeta del juego, instalar o actualizar, desinstalar, abrir las instrucciones y ver
/// las novedades. En español o en inglés según el idioma de Windows, con los colores de los menús del juego.
/// </summary>
internal sealed class SetupForm : Form, IOutput {
    // Colores de los menús del juego: fondo casi negro azulado, texto blanco, el azul del alma para lo importante
    private static readonly Color Back = ColorTranslator.FromHtml("#0f121a");
    private static readonly Color Panel = ColorTranslator.FromHtml("#181d29");
    private static readonly Color Line = ColorTranslator.FromHtml("#2a3142");
    private static readonly Color TextColor = ColorTranslator.FromHtml("#e8ecf3");
    private static readonly Color Dim = ColorTranslator.FromHtml("#8c96a8");
    private static readonly Color Accent = ColorTranslator.FromHtml("#7fb2ff");
    private static readonly Color AccentDark = ColorTranslator.FromHtml("#2f5fb3");
    private static readonly Color OkColor = ColorTranslator.FromHtml("#7ddc8f");
    private static readonly Color WarnColor = ColorTranslator.FromHtml("#f0c86a");
    private static readonly Color ErrorColor = ColorTranslator.FromHtml("#ff7b7b");
    private static readonly Color LogBack = ColorTranslator.FromHtml("#0a0c12");

    private readonly Font _fontTitle = new("Georgia", 26, FontStyle.Bold);
    private readonly Font _fontText = new("Segoe UI", 10);
    private readonly Font _fontBold = new("Segoe UI Semibold", 10);
    private readonly Font _fontButton = new("Segoe UI Semibold", 11);
    private readonly Font _fontLog = new("Segoe UI", 9.5f);

    private readonly string _packageRoot;
    private readonly string _version;
    private readonly string _givenDir;

    private readonly Panel _banner;
    private readonly Label _bannerVersion;
    private readonly LinkLabel _bannerLink;
    private readonly ComboBox _folder;
    private readonly Button _browse;
    private readonly Label _status;
    private readonly Button _install;
    private readonly Button _uninstall;
    private readonly Button _readme;
    private readonly ProgressBar _progress;
    private readonly RichTextBox _log;

    private List<Release> _releases;
    private Release _newest;

    internal SetupForm(string packageRoot, string version, string givenDir) {
        _packageRoot = packageRoot;
        _version = version;
        _givenDir = givenDir;

        Text = L("HKMultiplayer: instalador", "HKMultiplayer setup");
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Back;
        ForeColor = TextColor;
        Font = _fontText;
        Icon = Program.LoadIcon();

        // Cabecera
        Controls.Add(NewLabel("HKMultiplayer", 24, 16, 330, 48, _fontTitle, TextColor));
        var subtitle = L("Mod cooperativo para Hollow Knight", "Co-op mod for Hollow Knight") +
                       (version.Length > 0 ? "   ·   " + L("versión ", "version ") + version : string.Empty);
        Controls.Add(NewLabel(subtitle, 28, 66, 350, 22, _fontText, Dim));

        // Cartel de versión nueva (arriba a la derecha): sale solo si GitHub tiene una más nueva que este paquete
        _banner = new Panel {
            Location = new Point(392, 14), Size = new Size(304, 76), BackColor = ColorTranslator.FromHtml("#1c2b4a"),
            Visible = false
        };
        _banner.Paint += (_, e) => {
            using var pen = new Pen(Accent);
            e.Graphics.DrawRectangle(pen, 0, 0, _banner.Width - 1, _banner.Height - 1);
        };
        _bannerVersion = NewLabel(string.Empty, 12, 29, 284, 20, _fontText, TextColor);
        _bannerLink = new LinkLabel {
            Location = new Point(12, 50), Size = new Size(284, 20), Font = _fontText, ForeColor = TextColor,
            LinkColor = Accent, ActiveLinkColor = Color.White, BackColor = Color.Transparent
        };
        _bannerLink.LinkClicked += (_, _) => {
            if (_newest != null) {
                Open(_newest.Url);
            }
        };
        _banner.Controls.AddRange(new Control[] {
            NewLabel(L("¡Hay una versión nueva!", "A new version is out!"), 12, 7, 284, 22, _fontBold, Accent), _bannerVersion,
            _bannerLink
        });
        Controls.Add(_banner);
        Controls.Add(new Panel { Location = new Point(24, 98), Size = new Size(672, 1), BackColor = Line });

        // Carpeta del juego
        Controls.Add(NewLabel(L("Carpeta de Hollow Knight", "Hollow Knight folder"), 24, 114, 400, 22, _fontBold, TextColor));
        _folder = new ComboBox {
            Location = new Point(24, 140), Size = new Size(560, 28), DropDownStyle = ComboBoxStyle.DropDown,
            FlatStyle = FlatStyle.Flat, BackColor = Panel, ForeColor = TextColor
        };
        Controls.Add(_folder);
        _browse = NewButton(L("Buscar…", "Browse…"), 594, 136, 102, false);
        _browse.Height = 32;
        _browse.Font = _fontText;
        Controls.Add(_browse);
        _status = NewLabel(string.Empty, 24, 176, 672, 22, _fontText, Dim);
        Controls.Add(_status);

        // Botones
        _install = NewButton(L("Instalar", "Install"), 24, 212, 220, true);
        _uninstall = NewButton(L("Desinstalar", "Uninstall"), 256, 212, 200, false);
        _readme = NewButton(L("Instrucciones", "Instructions"), 468, 212, 228, false);
        Controls.AddRange(new Control[] { _install, _uninstall, _readme });

        _progress = new ProgressBar {
            Location = new Point(24, 266), Size = new Size(672, 4), Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25, Visible = false
        };
        Controls.Add(_progress);

        // Registro de lo que se hace
        _log = new RichTextBox {
            Location = new Point(24, 278), Size = new Size(672, 222), BackColor = LogBack, ForeColor = TextColor,
            Font = _fontLog, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical
        };
        Controls.Add(_log);

        Controls.Add(NewLabel(L("Tus partidas normales no se tocan. Al desinstalar, el juego queda como estaba.",
            "Your normal saves are not touched. Uninstalling puts the game back as it was."), 24, 514, 520, 36, _fontText, Dim));
        var notes = NewButton(L("Novedades", "What's new"), 556, 510, 140, false);
        notes.Height = 34;
        notes.Font = _fontBold;
        Controls.Add(notes);

        _browse.Click += (_, _) => Browse();
        _folder.TextChanged += (_, _) => UpdateStatus();
        _folder.SelectedIndexChanged += (_, _) => UpdateStatus();
        _install.Click += async (_, _) => await RunInstall();
        _uninstall.Click += async (_, _) => await RunUninstall();
        _readme.Click += (_, _) => OpenReadme();
        notes.Click += async (_, _) => await ShowNotes();
        Shown += (_, _) => OnFirstShown();
    }

    private static Label NewLabel(string text, int x, int y, int width, int height, Font font, Color color) => new() {
        Text = text, Location = new Point(x, y), Size = new Size(width, height), Font = font, ForeColor = color,
        BackColor = Color.Transparent
    };

    private Button NewButton(string text, int x, int y, int width, bool primary) {
        var button = new Button {
            Text = text, Location = new Point(x, y), Size = new Size(width, 42), Font = _fontButton, FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, BackColor = primary ? AccentDark : Panel, ForeColor = primary ? Color.White : TextColor,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.MouseOverBackColor = Accent;
        button.FlatAppearance.MouseDownBackColor = AccentDark;
        // Desactivado, Windows pinta el texto en un gris oscuro que casi no se lee sobre el fondo: se vuelve a dibujar el
        // botón con el texto en blanco (ForeColor no cuenta en un botón desactivado)
        button.Paint += (_, e) => {
            if (button.Enabled) {
                return;
            }

            var bounds = button.ClientRectangle;
            using (var back = new SolidBrush(button.BackColor)) {
                e.Graphics.FillRectangle(back, bounds);
            }

            using (var pen = new Pen(button.FlatAppearance.BorderColor)) {
                e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
            }

            TextRenderer.DrawText(e.Graphics, button.Text, button.Font, bounds, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        return button;
    }

    /// <summary>Escribe una línea en el registro de la ventana (desde cualquier hilo: la instalación va en otro).</summary>
    public void Write(LogKind kind, string text) {
        if (InvokeRequired) {
            Invoke(new Action(() => Write(kind, text)));
            return;
        }

        var (color, prefix) = kind switch {
            LogKind.Step => (Accent, "›  "),
            LogKind.Warn => (WarnColor, "!  "),
            LogKind.Ok => (OkColor, "✔  "),
            LogKind.Error => (ErrorColor, "✖  "),
            _ => (TextColor, "    ")
        };
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText(prefix + text + "\r\n");
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    /// <summary>Estado de la carpeta elegida: si es la del juego y si el mod ya está.</summary>
    private void UpdateStatus() {
        var game = GameFiles.ToGameDir(_folder.Text);
        if (game == null) {
            _status.Text = "●  " + L("Elige la carpeta del juego (la que tiene hollow_knight.exe).",
                "Choose the game folder (the one with hollow_knight.exe).");
            _status.ForeColor = WarnColor;
            _install.Text = L("Instalar", "Install");
            _install.Enabled = false;
            _uninstall.Enabled = false;
            return;
        }

        var installed = GameFiles.InstalledVersion(game);
        if (installed.Length > 0) {
            _status.Text = "●  " + L($"HKMultiplayer {installed} está instalado en esta carpeta.",
                $"HKMultiplayer {installed} is installed in this folder.");
            _status.ForeColor = OkColor;
            _install.Text = L("Actualizar", "Update");
        } else {
            _status.Text = "●  " + L("HKMultiplayer no está instalado en esta carpeta.", "HKMultiplayer isn't installed in this folder.");
            _status.ForeColor = Dim;
            _install.Text = L("Instalar", "Install");
        }

        _install.Enabled = true;
        _uninstall.Enabled = installed.Length > 0 || Directory.Exists(Path.Combine(game, GameFiles.BackupName));
    }

    private void SetBusy(bool busy) {
        _progress.Visible = busy;
        foreach (var control in new Control[] { _install, _uninstall, _readme, _browse, _folder }) {
            control.Enabled = !busy;
        }

        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void ShowMessage(string text, MessageBoxIcon icon) =>
        MessageBox.Show(this, text, "HKMultiplayer", MessageBoxButtons.OK, icon);

    private bool Ask(string text) =>
        MessageBox.Show(this, text, "HKMultiplayer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void Browse() {
        using var dialog = new FolderBrowserDialog {
            Description = L(
                "Elige la carpeta de Hollow Knight (la que tiene hollow_knight.exe). En Steam: clic derecho en el juego > Administrar > Ver archivos locales.",
                "Choose the Hollow Knight folder (the one with hollow_knight.exe). In Steam: right-click the game > Manage > Browse local files."),
            ShowNewFolderButton = false
        };
        if (_folder.Text.Length > 0 && Directory.Exists(_folder.Text)) {
            dialog.SelectedPath = _folder.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK) {
            _folder.Text = GameFiles.ToGameDir(dialog.SelectedPath) ?? dialog.SelectedPath;
            UpdateStatus();
        }
    }

    private async Task RunInstall() {
        var game = GameFiles.ToGameDir(_folder.Text);
        if (game == null) {
            return;
        }

        // Se dice antes qué otros mods se apartan: también uno que el jugador volvió a activar después de otra instalación
        var others = Installer.OtherMods(game);
        if (others.Count > 0 && !Ask(L(
                "Estos mods se apartarán a Mods\\Disabled (no funcionan con la Modding API de HKMultiplayer; al desinstalar vuelven):\n\n" +
                string.Join("\n", others) + "\n\n¿Seguir?",
                "These mods will be moved to Mods\\Disabled (they don't work with HKMultiplayer's Modding API; uninstalling brings them back):\n\n" +
                string.Join("\n", others) + "\n\nContinue?"))) {
            return;
        }

        _log.Clear();
        SetBusy(true);
        var installer = new Installer(_packageRoot, this);
        try {
            await Task.Run(() => installer.Install(game));
            Write(LogKind.Ok, L("Instalación completa.", "Installation complete."));
            Write(LogKind.Note, L("Abre Hollow Knight desde Steam: en el menú principal aparece \"Multijugador\".",
                "Open Hollow Knight from Steam: \"Multiplayer\" appears in the main menu."));
            SetBusy(false);
            UpdateStatus();
            ShowMessage(L(
                $"¡Listo! HKMultiplayer {_version} está instalado.\n\nAbre Hollow Knight desde Steam: en el menú principal aparece \"Multijugador\". Cómo hostear y unirse: botón \"Instrucciones\".",
                $"Done! HKMultiplayer {_version} is installed.\n\nOpen Hollow Knight from Steam: \"Multiplayer\" appears in the main menu. How to host and join: \"Instructions\" button."),
                MessageBoxIcon.Information);
        } catch (Exception e) {
            var text = GameFiles.ErrorText(e);
            Write(LogKind.Error, L("No se pudo instalar: ", "Could not install: ") + text);
            if (installer.Changing) {
                Write(LogKind.Error, L("Si se quedó a medias, \"Desinstalar\" deja el juego como estaba.",
                    "If it stopped halfway, \"Uninstall\" puts the game back as it was."));
            }

            SetBusy(false);
            UpdateStatus();
            ShowMessage(L("No se pudo instalar:", "Could not install:") + "\n\n" + text, MessageBoxIcon.Error);
        }
    }

    private async Task RunUninstall() {
        var game = GameFiles.ToGameDir(_folder.Text);
        if (game == null || !Ask(L(
                "¿Quitar HKMultiplayer y dejar el juego como estaba?\n\nTus mundos coop, tus partidas y tus skins no se borran.",
                "Remove HKMultiplayer and put the game back as it was?\n\nYour coop worlds, saves and skins are not deleted."))) {
            return;
        }

        _log.Clear();
        SetBusy(true);
        try {
            var clean = await Task.Run(() => new Installer(_packageRoot, this).Uninstall(game));
            if (clean) {
                Write(LogKind.Ok, L("Desinstalado: el juego vuelve a estar como antes de instalar HKMultiplayer.",
                    "Uninstalled: the game is back to how it was before installing HKMultiplayer."));
            }

            SetBusy(false);
            UpdateStatus();
            if (clean) {
                ShowMessage(L("HKMultiplayer se desinstaló. El juego vuelve a estar como antes.",
                    "HKMultiplayer was uninstalled. The game is back to how it was."), MessageBoxIcon.Information);
            }
        } catch (Exception e) {
            var text = GameFiles.ErrorText(e);
            Write(LogKind.Error, L("No se pudo desinstalar: ", "Could not uninstall: ") + text);
            SetBusy(false);
            UpdateStatus();
            ShowMessage(L("No se pudo desinstalar:", "Could not uninstall:") + "\n\n" + text, MessageBoxIcon.Error);
        }
    }

    private void OpenReadme() {
        var file = Path.Combine(_packageRoot, L("LEEME.txt", "README.txt"));
        if (!File.Exists(file)) {
            file = Path.Combine(_packageRoot, "README.txt");
        }

        if (File.Exists(file)) {
            Open(file);
        }
    }

    private static void Open(string target) {
        try {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException) {
        }
    }

    private void SetReleases(string json) {
        _releases = Releases.Parse(json);
        _newest = _releases.OrderByDescending(release => release.Version).FirstOrDefault();
        if (_newest != null && Version.TryParse(_version, out var current) && _newest.Version > current) {
            var newestText = _newest.Tag.TrimStart('v', 'V');
            _bannerVersion.Text = L($"La {newestText} (este paquete es la {_version})", $"{newestText} (this package is {_version})");
            _bannerLink.Text = L("Descargarla", "Download it");
            _banner.Visible = true;
        }
    }

    /// <summary>Pide a GitHub las versiones publicadas sin parar la ventana. Devuelve si llegaron.</summary>
    private async Task<bool> FetchReleases(TimeSpan timeout) {
        using var client = Releases.NewClient(_version);
        var request = client.DownloadStringTaskAsync(new Uri(Releases.ApiUrl));
        var finished = await Task.WhenAny(request, Task.Delay(timeout));
        if (finished != request) {
            client.CancelAsync();
            return false;
        }

        try {
            SetReleases(await request);
            return true;
        } catch (Exception e) when (e is WebException or ArgumentException or InvalidOperationException) {
            return false;
        }
    }

    /// <summary>
    /// "Novedades": lo que trae la versión nueva o, si no la hay, la de este paquete (el apartado "Novedades de la ..." de
    /// sus notas).
    /// </summary>
    private async Task ShowNotes() {
        if (_releases == null) {
            // Aún no llegó (o falló al abrir): se pide ya, esperando como mucho 10 s
            Cursor = Cursors.WaitCursor;
            await FetchReleases(TimeSpan.FromSeconds(10));
            Cursor = Cursors.Default;
        }

        Version.TryParse(_version, out var current);
        var release = _newest != null && current != null && _newest.Version > current
            ? _newest
            : _releases?.FirstOrDefault(item => current != null && item.Version == current);
        if (release == null) {
            ShowMessage(_releases != null
                ? L($"Las novedades de la {_version} aún no están publicadas. Todas las versiones: {Releases.PageUrl}",
                    $"What's new in {_version} isn't published yet. All versions: {Releases.PageUrl}")
                : L($"No se pudieron cargar las novedades (hace falta internet). Están en {Releases.PageUrl}",
                    $"Couldn't load what's new (an internet connection is needed). It's at {Releases.PageUrl}"),
                MessageBoxIcon.Information);
            return;
        }

        var tag = release.Tag.TrimStart('v', 'V');
        using var dialog = new Form {
            Text = L($"Novedades de la {tag}", $"What's new in {tag}"), AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(640, 460), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            BackColor = Back, ForeColor = TextColor, Font = _fontText, Icon = Icon
        };
        dialog.Controls.Add(NewLabel(L("Novedades de la ", "What's new in ") + tag, 24, 16, 592, 30, _fontButton, TextColor));
        dialog.Controls.Add(new RichTextBox {
            Location = new Point(24, 54), Size = new Size(592, 336), BackColor = LogBack, ForeColor = TextColor, Font = _fontLog,
            ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical,
            Text = Releases.Notes(release)
        });
        var web = NewButton(L("Ver en GitHub", "Open on GitHub"), 24, 404, 200, false);
        web.Click += (_, _) => Open(release.Url);
        var close = NewButton(L("Cerrar", "Close"), 476, 404, 140, true);
        close.Click += (_, _) => dialog.Close();
        dialog.Controls.AddRange(new Control[] { web, close });
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    /// <summary>Carpeta inicial: la que se arrastró sobre el .exe, o la que se encuentra. Luego, las versiones publicadas.</summary>
    private async void OnFirstShown() {
        Activate();
        Write(LogKind.Step, L("Buscando Hollow Knight...", "Looking for Hollow Knight..."));
        List<string> found;
        try {
            found = await Task.Run(GameFiles.FindGameDirs);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            found = new List<string>();
        }

        foreach (var dir in found) {
            _folder.Items.Add(dir);
        }

        var given = GameFiles.ToGameDir(_givenDir);
        if (given != null) {
            _folder.Text = given;
        } else if (found.Count > 0) {
            _folder.SelectedIndex = 0;
        }

        _log.Clear();
        // Primero el estado: el botón dice "Instalar" o "Actualizar" y el aviso nombra el mismo
        UpdateStatus();
        if (found.Count > 1 && given == null) {
            Write(LogKind.Note, L($"Encontré el juego en {found.Count} carpetas: elige la tuya en la lista.",
                $"The game was found in {found.Count} folders: pick yours from the list."));
        } else if (_folder.Text.Length == 0) {
            Write(LogKind.Warn, L("No encontré la carpeta del juego. Pulsa \"Buscar…\" y elígela.",
                "The game folder was not found. Click \"Browse…\" and choose it."));
        } else {
            Write(LogKind.Note, L($"Todo listo. Cierra Hollow Knight y pulsa \"{_install.Text}\".",
                $"All set. Close Hollow Knight and click \"{_install.Text}\"."));
        }

        _folder.SelectionLength = 0;
        if (_install.Enabled) {
            _install.Focus();
        }

        // Si GitHub tiene una versión más nueva que este paquete sale el cartel de arriba (sin internet no pasa nada)
        await FetchReleases(TimeSpan.FromSeconds(30));
    }
}
