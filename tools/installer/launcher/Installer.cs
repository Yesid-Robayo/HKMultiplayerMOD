using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static HKMultiplayer.Setup.GameFiles;
using static HKMultiplayer.Setup.Lang;

namespace HKMultiplayer.Setup;

/// <summary>
/// Instalación y desinstalación de HKMultiplayer en la carpeta del juego. La instalación comprueba el paquete, guarda una
/// copia de lo que va a cambiar y lo apunta en HKMultiplayer-backup\manifest.txt (así la desinstalación deja el juego como
/// estaba), quita el mod anterior HollowKnightCoop, aparta los demás mods a Mods\Disabled y copia la Modding API y el mod.
/// La desinstalación deshace, en orden inverso, lo que apuntó la instalación. Los mundos coop y las partidas (en
/// AppData\LocalLow) no se tocan; las skins que haya en Mods\HKMultiplayer\Skins se quedan.
/// <para>
/// Líneas del manifiesto (una por cambio, en el orden en que se hicieron; el mismo formato que el instalador de PowerShell
/// de las versiones anteriores, así que una instalación de entonces se desinstala con este):
/// <list type="bullet">
/// <item><c>added-dir|&lt;carpeta&gt;</c>: carpeta que no existía.</item>
/// <item><c>added|&lt;archivo&gt;</c>: archivo que no existía.</item>
/// <item><c>replaced|&lt;archivo&gt;</c>: archivo que existía; el original está en HKMultiplayer-backup\files\&lt;archivo&gt;.</item>
/// <item><c>old-mod|&lt;ruta&gt;|&lt;copia&gt;</c>: mod anterior quitado; está en HKMultiplayer-backup\&lt;copia&gt;.</item>
/// <item><c>moved|&lt;desde&gt;|&lt;hasta&gt;</c>: otro mod movido a Mods\Disabled.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class Installer {
    private static readonly Encoding ManifestEncoding = new UTF8Encoding(true);
    private static readonly Encoding AppendEncoding = new UTF8Encoding(false);

    private readonly string _packageRoot;
    private readonly IOutput _output;

    internal Installer(string packageRoot, IOutput output) {
        _packageRoot = packageRoot;
        _output = output;
    }

    /// <summary>Si ya se había empezado a cambiar el juego cuando falló la instalación (entonces la desinstalación lo deshace).</summary>
    internal bool Changing { get; private set; }

    private string Source => Path.Combine(_packageRoot, "files");

    /// <summary>
    /// Versión del paquete: la del HKMultiplayer.dll que trae. No va escrita en el .exe, que así es el mismo archivo en
    /// todas las versiones (SmartScreen le guarda la reputación por archivo exacto).
    /// </summary>
    internal static string PackageVersion(string packageRoot) =>
        DllVersion(Join(Path.Combine(packageRoot, "files"), $"{ModsRelative}/{ModName}/{ModName}.dll"));

    /// <summary>Si junto al .exe están los archivos del paquete (no si se abrió desde dentro del .zip).</summary>
    internal static bool PackagePresent(string packageRoot) =>
        File.Exists(Path.Combine(packageRoot, "SHA256SUMS.txt")) && Directory.Exists(Path.Combine(packageRoot, "files"));

    private void Step(string text) => _output.Write(LogKind.Step, text);
    private void Note(string text) => _output.Write(LogKind.Note, text);
    private void Warn(string text) => _output.Write(LogKind.Warn, text);

    private sealed class Entry {
        internal string Hash;
        internal string Path;
    }

    /// <summary>Archivos del paquete con su hash (SHA256SUMS.txt, generado por tools/package.sh).</summary>
    private List<Entry> ReadPackageEntries() {
        var sums = Path.Combine(_packageRoot, "SHA256SUMS.txt");
        if (!File.Exists(sums)) {
            throw new SetupException(L("Falta SHA256SUMS.txt: el paquete está incompleto. Vuelve a descomprimirlo.",
                "SHA256SUMS.txt is missing: the package is incomplete. Extract it again."));
        }

        var entries = new List<Entry>();
        foreach (var line in File.ReadAllLines(sums)) {
            var match = Regex.Match(line, @"^([0-9a-f]{64})\s+\*?\./files/(.+)$");
            if (match.Success) {
                entries.Add(new Entry { Hash = match.Groups[1].Value, Path = match.Groups[2].Value });
            }
        }

        if (entries.Count == 0) {
            throw new SetupException(L("SHA256SUMS.txt no tiene archivos: el paquete está incompleto.",
                "SHA256SUMS.txt lists no files: the package is incomplete."));
        }

        return entries;
    }

    /// <summary>
    /// La Modding API del paquete es para Hollow Knight 1.5.12620 (Unity 6, con los ensamblados TeamCherry.*). En otra
    /// versión del juego dejaría el juego sin arrancar.
    /// </summary>
    private static void AssertCompatibleGame(string game) {
        var managed = Join(game, "hollow_knight_Data/Managed");
        if (!Directory.Exists(managed)) {
            throw new SetupException(L($"En '{game}' no está hollow_knight_Data\\Managed: no parece la carpeta del juego.",
                $"'{game}' has no hollow_knight_Data\\Managed: it doesn't look like the game folder."));
        }

        var player = Path.Combine(game, "UnityPlayer.dll");
        var unity = File.Exists(player) ? FileVersionInfo.GetVersionInfo(player).FileVersion ?? string.Empty : string.Empty;
        // La versión del juego va en su Assembly-CSharp.dll (Constants.GAME_VERSION, en UTF-16): un parche futuro con el
        // mismo Unity pasaba la comprobación y se le copiaba un Assembly-CSharp.dll hecho para otra versión
        var assembly = Path.Combine(managed, "Assembly-CSharp.dll");
        if (File.Exists(assembly) && !IsGameVersion(assembly)) {
            throw new SetupException(L(
                $"Este mod es para Hollow Knight {GameVersion} y tu juego es de otra versión. Actualízalo en Steam " +
                "(Propiedades > Betas: ninguna) o, si Steam ya tiene otra más nueva, espera a una versión del mod para ella.",
                $"This mod is for Hollow Knight {GameVersion} and your game is a different version. Update it in Steam " +
                "(Properties > Betas: none) or, if Steam already has a newer one, wait for a mod version for it."));
        }

        if (!unity.StartsWith("6000.", StringComparison.Ordinal) || !File.Exists(Path.Combine(managed, "TeamCherry.TK2D.dll"))) {
            throw new SetupException(L(
                $"Este mod es para Hollow Knight {GameVersion} (la versión actual de Steam). Tu juego es de otra versión " +
                $"(motor Unity '{unity}'). Actualízalo en Steam (Propiedades > Betas: ninguna) y vuelve a instalar.",
                $"This mod is for Hollow Knight {GameVersion} (the current Steam version). Your game is a different version " +
                $"(Unity engine '{unity}'). Update it in Steam (Properties > Betas: none) and install again."));
        }
    }

    /// <summary>Otros mods que la instalación aparta a Mods\Disabled (carpetas y .dll sueltos de Mods, salvo este y Disabled).</summary>
    internal static List<string> OtherMods(string game) {
        var mods = Join(game, ModsRelative);
        if (!Directory.Exists(mods)) {
            return new List<string>();
        }

        var others = new List<string>();
        foreach (var dir in Directory.GetDirectories(mods)) {
            var name = Path.GetFileName(dir);
            if (!Same(name, ModName) && !Same(name, "Disabled") && !Same(name, OldModName)) {
                others.Add(name);
            }
        }

        foreach (var file in Directory.GetFiles(mods)) {
            var name = Path.GetFileName(file);
            if (Same(Path.GetExtension(name), ".dll") && !Same(name, OldModName + ".dll")) {
                others.Add(name);
            }
        }

        others.Sort(StringComparer.OrdinalIgnoreCase);
        return others;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Instala en la carpeta del juego (ya comprobada). Lanza una excepción si algo falla.</summary>
    internal void Install(string game) {
        var source = Source;
        Changing = false;

        Note(L("Carpeta del juego: ", "Game folder: ") + game);
        AssertCompatibleGame(game);
        AssertGameClosed(game);

        // 1. El paquete está completo y sin cambios
        Step(L("Comprobando los archivos del paquete...", "Checking the package files..."));
        var entries = ReadPackageEntries();
        foreach (var entry in entries) {
            var file = Path.Combine(source, entry.Path.Replace('/', '\\'));
            if (!File.Exists(file)) {
                throw new SetupException(L($"Falta en el paquete: {entry.Path}. Vuelve a descomprimirlo entero.",
                    $"Missing from the package: {entry.Path}. Extract the whole package again."));
            }

            if (Sha256(file) != entry.Hash) {
                throw new SetupException(L($"Archivo dañado en el paquete: {entry.Path}. Vuelve a descargarlo.",
                    $"Damaged file in the package: {entry.Path}. Download it again."));
            }
        }

        Note(L($"{entries.Count} archivos correctos", $"{entries.Count} files OK"));

        // 2. Copia de lo que se va a cambiar. Si ya se instaló antes (actualización), se conserva la copia de entonces (la
        //    del juego como estaba) y solo se añade lo nuevo de este paquete
        var backupDir = Path.Combine(game, BackupName);
        var manifest = Path.Combine(backupDir, ManifestName);
        var update = File.Exists(manifest);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacedBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (update) {
            foreach (var line in File.ReadAllLines(manifest)) {
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) {
                    continue;
                }

                var parts = line.Split('|');
                if (parts.Length < 2) {
                    continue;
                }

                known.Add(parts[1]);
                if (parts[0] == "replaced") {
                    replacedBefore.Add(parts[1]);
                }
            }
        } else {
            Directory.CreateDirectory(backupDir);
            File.WriteAllLines(manifest, new[] {
                "# HKMultiplayer: cambios del instalador en la carpeta del juego. La desinstalación los deshace.",
                "# HKMultiplayer: installer changes in the game folder. Uninstalling undoes them.",
                "# No borres esta carpeta mientras el mod esté instalado. / Do not delete this folder while the mod is installed."
            }, ManifestEncoding);
        }

        void Record(string line) => File.AppendAllText(manifest, line + "\r\n", AppendEncoding);

        // Desde aquí sí se cambia el juego (la copia y el manifiesto ya están): un fallo antes no deja nada a medias
        Changing = true;

        Step(L("Guardando una copia de lo que se va a cambiar...", "Backing up what will change..."));
        var replaced = 0;
        var targets = entries.Select(entry => entry.Path).Concat(new[] { "steam_appid.txt" });
        foreach (var relative in targets) {
            // Carpetas que hay que crear, de fuera hacia dentro
            var segments = relative.Split('/');
            for (var i = 1; i < segments.Length; i++) {
                var parentRelative = string.Join("/", segments, 0, i);
                var parentPath = Join(game, parentRelative);
                if (!Directory.Exists(parentPath)) {
                    Record($"added-dir|{parentRelative}");
                    known.Add(parentRelative);
                    Directory.CreateDirectory(parentPath);
                }
            }

            var target = Join(game, relative);
            var copy = Path.Combine(backupDir, "files", relative.Replace('/', '\\'));
            var patched = relative != "steam_appid.txt" && IsModded(Join(source, relative));
            if (known.Contains(relative)) {
                // Un ensamblado del juego que ya no es el de la Modding API: Steam lo cambió después de instalar (el juego
                // se actualizó o se verificó). Ese es ahora el original: si se dejaba la copia de la primera instalación, al
                // desinstalar volvía un Assembly-CSharp.dll de una versión anterior y el juego no arrancaba
                if (patched && replacedBefore.Contains(relative) && File.Exists(target) && !IsModded(target)) {
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    CopyFile(target, copy);
                    Warn(L($"El juego cambió {relative} desde la última instalación (¿se actualizó?): se guarda esa versión como la original.",
                        $"The game changed {relative} since the last install (was it updated?): that version is kept as the original."));
                }

                continue;
            }

            if (File.Exists(target)) {
                if (relative == "steam_appid.txt") {
                    // No se toca: se deja el del juego
                    continue;
                }

                // Ya había una Modding API (puesta a mano o con otro instalador): la copia no es el juego original
                if (patched && IsModded(target)) {
                    Warn(L($"{relative} ya tenía una Modding API: al desinstalar volverá esa, no el juego original. Para el original: Steam > Propiedades > Archivos instalados > Verificar integridad.",
                        $"{relative} already had a Modding API: uninstalling will put that one back, not the original game. For the original: Steam > Properties > Installed Files > Verify integrity."));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                CopyFile(target, copy);
                Record($"replaced|{relative}");
                replaced++;
            } else {
                Record($"added|{relative}");
            }

            known.Add(relative);
        }

        Note(update
            ? L("Ya estaba instalado: se conserva la copia del juego de la primera instalación.",
                "Already installed: the game backup from the first install is kept.")
            : L($"{replaced} archivos del juego guardados en {backupDir}", $"{replaced} game files backed up to {backupDir}"));

        // 3. Mod anterior (HollowKnightCoop): también abre la red, no pueden estar los dos. Se guarda en la copia y la
        //    desinstalación lo devuelve
        var mods = Join(game, ModsRelative);
        foreach (var name in new[] { OldModName, OldModName + ".dll" }) {
            var path = Path.Combine(mods, name);
            if (!Exists(path)) {
                continue;
            }

            var stored = $"old-mod/{name}";
            var suffix = 2;
            while (Exists(Join(backupDir, stored))) {
                stored = $"old-mod/{name}.{suffix}";
                suffix++;
            }

            Directory.CreateDirectory(Path.Combine(backupDir, "old-mod"));
            Move(path, Join(backupDir, stored));
            Record($"old-mod|{ModsRelative}/{name}|{stored}");
            Warn(L($"Quitado el mod anterior {OldModName} (los dos usan la red a la vez). Su copia queda en {BackupName}.",
                $"Removed the old mod {OldModName} (both use the network at the same time). Its copy stays in {BackupName}."));
        }

        // 4. Otros mods: los hechos para otra versión de la Modding API no cargan con esta y pueden romper el juego. Se
        //    apartan a Mods\Disabled (la Modding API no carga esa carpeta) y la desinstalación los devuelve
        if (Directory.Exists(mods)) {
            var others = OtherMods(game);
            if (others.Count > 0) {
                var disabled = Path.Combine(mods, "Disabled");
                if (!Directory.Exists(disabled)) {
                    Record($"added-dir|{ModsRelative}/Disabled");
                    Directory.CreateDirectory(disabled);
                }

                foreach (var name in others) {
                    var destination = Path.Combine(disabled, name);
                    if (Exists(destination)) {
                        Warn(L($"No se movió {name}: ya hay uno con ese nombre en Mods\\Disabled. Si el juego falla al cargarlo, quítalo a mano.",
                            $"{name} was not moved: Mods\\Disabled already has one with that name. If the game fails to load it, remove it by hand."));
                        continue;
                    }

                    Move(Path.Combine(mods, name), destination);
                    Record($"moved|{ModsRelative}/{name}|{ModsRelative}/Disabled/{name}");
                    Warn(L("Otro mod apartado a Mods\\Disabled: ", "Another mod moved to Mods\\Disabled: ") + name);
                }
            }
        }

        // 5. Copiar la Modding API y el mod
        Step(L("Copiando la Modding API y el mod...", "Copying the Modding API and the mod..."));
        foreach (var entry in entries) {
            CopyFile(Join(source, entry.Path), Join(game, entry.Path));
        }

        // Sin este archivo, abrir hollow_knight.exe directamente (sin Steam) no arranca el juego
        var appId = Path.Combine(game, "steam_appid.txt");
        if (!File.Exists(appId)) {
            File.WriteAllText(appId, "367520\r\n", Encoding.ASCII);
        }

        // 6. Comprobar lo copiado
        foreach (var entry in entries) {
            if (Sha256(Join(game, entry.Path)) != entry.Hash) {
                throw new SetupException(L($"No se copió bien: {entry.Path}", $"Not copied correctly: {entry.Path}"));
            }
        }

        Note(L("Copiado y comprobado", "Copied and verified"));
        Changing = false;
    }

    /// <summary>Borra las subcarpetas vacías y luego la carpeta si quedó vacía. Devuelve true si la borró.</summary>
    private static bool RemoveEmptyTree(string path) {
        if (!Directory.Exists(path)) {
            return true;
        }

        foreach (var child in Directory.GetDirectories(path)) {
            RemoveEmptyTree(child);
        }

        if (Directory.EnumerateFileSystemEntries(path).Any()) {
            return false;
        }

        Directory.Delete(path);
        return true;
    }

    /// <summary>
    /// Carpeta del mod. Los archivos del paquete ya los borró su línea "added" del manifiesto; aquí se borra la caché de
    /// skins de los compañeros (SkinCache, la crea el mod) y, con <paramref name="all"/> (instalado a mano: no hay lista de
    /// lo que se puso), también los .dll, .pdb y .xml. Lo demás se queda: las skins del jugador y cualquier archivo que haya
    /// puesto ahí. Devuelve true si la carpeta se queda.
    /// </summary>
    private bool RemoveModFolder(string modDir, bool all = false) {
        if (!Directory.Exists(modDir)) {
            return false;
        }

        DeleteTree(Path.Combine(modDir, "SkinCache"));
        if (all) {
            foreach (var file in Directory.GetFiles(modDir)) {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is ".dll" or ".pdb" or ".xml") {
                    DeleteFile(file);
                }
            }
        }

        if (RemoveEmptyTree(modDir)) {
            return false;
        }

        var left = Directory.EnumerateFileSystemEntries(modDir).Select(Path.GetFileName);
        Note(L("Se deja la carpeta del mod con lo que no puso el instalador (tus skins y tus archivos): ",
            "The mod folder is kept with what the installer did not put there (your skins and files): ") +
            $"{modDir} ({string.Join(", ", left)})");
        return true;
    }

    /// <summary>Desinstala de la carpeta del juego. Devuelve true si el juego quedó como estaba; lanza una excepción si algo falla.</summary>
    internal bool Uninstall(string game) {
        Note(L("Carpeta del juego: ", "Game folder: ") + game);
        AssertGameClosed(game);

        var backupDir = Path.Combine(game, BackupName);
        var manifest = Path.Combine(backupDir, ManifestName);
        var modDir = Join(game, $"{ModsRelative}/{ModName}");

        if (!File.Exists(manifest)) {
            // Instalado a mano (sin el instalador): solo se puede quitar el mod
            if (!Directory.Exists(modDir)) {
                Warn(L($"No encontré HKMultiplayer instalado en esta carpeta (ni la copia {BackupName}).",
                    $"HKMultiplayer isn't installed in this folder (and there is no {BackupName} copy)."));
                return false;
            }

            RemoveModFolder(modDir, true);
            Warn(L("Mod quitado. No había copia del instalador, así que la Modding API sigue instalada.",
                "Mod removed. There was no installer backup, so the Modding API is still installed."));
            Warn(L("Para dejar el juego original: Steam > clic derecho en Hollow Knight > Propiedades > Archivos instalados > Verificar integridad.",
                "To get the original game back: Steam > right-click Hollow Knight > Properties > Installed Files > Verify integrity."));
            return false;
        }

        Step(L("Deshaciendo los cambios de la instalación...", "Undoing the install changes..."));
        var lines = File.ReadAllLines(manifest).Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Reverse().ToList();
        var problems = new List<string>();
        var keptSkins = false;
        var restoredOldMod = false;
        var restoredMods = 0;
        foreach (var line in lines) {
            var parts = line.Split('|');
            var kind = parts[0];
            if (parts.Length < 2) {
                problems.Add(L($"línea desconocida en el manifiesto: {line}", $"unknown manifest line: {line}"));
                continue;
            }

            var target = Join(game, parts[1]);
            switch (kind) {
                case "added":
                    DeleteFile(target);
                    break;
                case "replaced": {
                    var copy = Path.Combine(backupDir, "files", parts[1].Replace('/', '\\'));
                    // Un ensamblado que ya no es el de la Modding API lo volvió a poner Steam (actualización o "Verificar
                    // integridad") y es más nuevo que la copia: se deja. Si no, volvía uno de una versión anterior del juego.
                    // Solo los archivos del juego: los de Mods (un mod puesto a mano antes de instalar) se devuelven siempre
                    if (!parts[1].StartsWith(ModsRelative + "/", StringComparison.Ordinal) && File.Exists(target) &&
                        File.Exists(copy) && !IsModded(copy) && !IsModded(target) && Sha256(target) != Sha256(copy)) {
                        Note(L($"Se deja {parts[1]}: el juego lo cambió después de instalar (es su versión original más nueva).",
                            $"{parts[1]} is kept: the game changed it after the install (it's its newer original version)."));
                        break;
                    }

                    if (File.Exists(copy)) {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        CopyFile(copy, target);
                    } else {
                        problems.Add(L($"falta la copia de {parts[1]}", $"the backup of {parts[1]} is missing"));
                    }

                    break;
                }
                case "added-dir":
                    if (Same(target, modDir)) {
                        keptSkins = RemoveModFolder(modDir);
                    } else if (!RemoveEmptyTree(target) && !keptSkins) {
                        Warn(L($"Se deja {parts[1]}: tiene archivos que no puso el instalador.",
                            $"{parts[1]} is kept: it has files the installer didn't put there."));
                    }

                    break;
                case "moved" when parts.Length >= 3: {
                    var from = Join(game, parts[2]);
                    if (Exists(from) && !Exists(target)) {
                        Move(from, target);
                        restoredMods++;
                    } else if (!Exists(target)) {
                        problems.Add(L($"no está {parts[2]} para devolverlo a su sitio", $"{parts[2]} is not there to move it back"));
                    }

                    break;
                }
                case "old-mod" when parts.Length >= 3: {
                    var stored = Join(backupDir, parts[2]);
                    if (Exists(stored) && !Exists(target)) {
                        Move(stored, target);
                        restoredOldMod = true;
                    } else if (!Exists(target)) {
                        problems.Add(L($"falta la copia del mod anterior ({parts[2]})", $"the backup of the old mod is missing ({parts[2]})"));
                    }

                    break;
                }
                default:
                    problems.Add(L($"línea desconocida en el manifiesto: {line}", $"unknown manifest line: {line}"));
                    break;
            }
        }

        // La caché de skins de los compañeros la crea el mod: se borra también si la carpeta del mod ya existía antes de
        // instalar (entonces no es una carpeta añadida y RemoveModFolder no se llama)
        DeleteTree(Path.Combine(modDir, "SkinCache"));

        var clean = problems.Count == 0;
        if (clean) {
            DeleteTree(backupDir);
        } else {
            Warn(L("Algo no se pudo devolver:", "Something could not be restored:"));
            foreach (var problem in problems) {
                Warn("- " + problem);
            }

            Warn(L($"Se conserva la copia en {backupDir}. Para dejar el juego original del todo: Steam > Hollow Knight > Propiedades > Archivos instalados > Verificar integridad.",
                $"The backup in {backupDir} is kept. To get the original game back completely: Steam > Hollow Knight > Properties > Installed Files > Verify integrity."));
        }

        if (restoredOldMod) {
            Note(L($"El mod anterior ({OldModName}) vuelve a estar instalado, como antes.", $"The old mod ({OldModName}) is installed again, as before."));
        }

        if (restoredMods > 0) {
            Note(L($"Mods apartados a Mods\\Disabled que vuelven a su sitio: {restoredMods}.", $"Mods moved back from Mods\\Disabled: {restoredMods}."));
        }

        Note(L("Tus mundos coop y tus partidas no se han tocado.", "Your coop worlds and saves were not touched."));
        return clean;
    }
}
