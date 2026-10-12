using System;
using System.Collections;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace HKMultiplayer.Game;

/// <summary>
/// Versiones del mod: al abrir el juego se mira en GitHub cuál es la más nueva publicada y, si es más nueva que la
/// instalada, se avisa en el menú principal y Multijugador ofrece descargarla (todos tienen que llevar la misma versión
/// para jugar juntos). También se avisa la primera vez que se abre una versión recién instalada, con sus novedades.
/// <para>Una sola consulta por sesión, sin datos del jugador (solo la lista pública de versiones); se puede desactivar en
/// Multijugador (<see cref="Settings.GlobalSettings.CheckForUpdates"/>).</para>
/// </summary>
internal static class UpdateCheck {
    /// <summary>Página de las versiones (para descargar a mano).</summary>
    public const string ReleasesPage = "https://github.com/Yesid-Robayo/HKMultiplayerMOD/releases";

    // Todas se publican como pre-release, así que no sirve "latest": se mira la lista
    private const string ReleasesApi = "https://api.github.com/repos/Yesid-Robayo/HKMultiplayerMOD/releases?per_page=20";

    private const int TimeoutSeconds = 10;

    /// <summary>Tras llegar al menú principal, lo que se espera para avisar (el título aún está apareciendo).</summary>
    private const float AnnounceDelay = 2f;

    /// <summary>La versión publicada más nueva (sin la "v"), o null si no se sabe (sin consultar, sin red...).</summary>
    public static string Latest {
        get => _latest;
        private set {
            _latest = value;
            // Se compara una vez al saberla: comparar versiones crea memoria y Update lo preguntaba cada frame
            _newerAvailable = value != null && IsNewer(value, HKMultiplayerMod.Version);
        }
    }

    private static string _latest;
    private static bool _newerAvailable;

    /// <summary>La página de esa versión.</summary>
    public static string LatestUrl { get; private set; }

    /// <summary>Si hay una versión publicada más nueva que la instalada.</summary>
    public static bool NewerAvailable => _newerAvailable;

    /// <summary>Si es la primera vez que se abre esta versión (se acaba de instalar o actualizar).</summary>
    public static bool JustUpdated { get; private set; }

    /// <summary>La versión que había antes (vacía si se actualizó desde una que no la apuntaba).</summary>
    public static string PreviousVersion { get; private set; } = string.Empty;

    /// <summary>La página con las novedades de la versión instalada.</summary>
    public static string CurrentNotesUrl => $"{ReleasesPage}/tag/v{HKMultiplayerMod.Version}";

    /// <summary>Cambió algo de lo de arriba (llegó la respuesta).</summary>
    public static event Action Changed;

    private static bool _begun;
    private static bool _checking;
    private static bool _announcedUpdate;
    private static bool _announcedNewer;
    private static float _atMenuSince = -1f;

    /// <summary>Al cargar el mod: apunta la versión y, si está activado, consulta GitHub.</summary>
    public static void Begin(MonoBehaviour host) {
        if (_begun) {
            return;
        }

        _begun = true;
        var settings = HKMultiplayerMod.Settings;
        var previous = settings.LastRunVersion ?? string.Empty;
        // Sin versión apuntada pero con ajustes de antes: viene de una versión anterior a esto (0.23.0 o menos)
        if (previous != HKMultiplayerMod.Version && (previous.Length > 0 || HKMultiplayerMod.SettingsFromFile)) {
            JustUpdated = true;
            PreviousVersion = previous;
        }

        if (previous != HKMultiplayerMod.Version) {
            settings.LastRunVersion = HKMultiplayerMod.Version;
            HKMultiplayerMod.SaveSettings();
        }

        if (JustUpdated) {
            Log.Info($"HKMultiplayer actualizado a {HKMultiplayerMod.Version}" +
                     (PreviousVersion.Length > 0 ? $" (antes {PreviousVersion})" : string.Empty));
        }

        if (settings.CheckForUpdates) {
            host.StartCoroutine(Fetch());
        }
    }

    /// <summary>Consulta otra vez (al activar la opción en Multijugador).</summary>
    public static void CheckAgain(MonoBehaviour host) {
        if (!_checking && HKMultiplayerMod.Settings.CheckForUpdates) {
            host.StartCoroutine(Fetch());
        }
    }

    private static IEnumerator Fetch() {
        _checking = true;
        using var request = UnityWebRequest.Get(ReleasesApi);
        request.SetRequestHeader("User-Agent", "HKMultiplayer/" + HKMultiplayerMod.Version);
        request.SetRequestHeader("Accept", "application/vnd.github+json");
        request.timeout = TimeoutSeconds;
        yield return request.SendWebRequest();
        _checking = false;

        if (request.result != UnityWebRequest.Result.Success) {
            Log.Info($"No se pudo buscar actualizaciones: {request.error}");
            yield break;
        }

        try {
            string best = null;
            string bestUrl = null;
            foreach (var release in JArray.Parse(request.downloadHandler.text)) {
                if (release["draft"]?.Value<bool>() == true) {
                    continue;
                }

                var tag = release["tag_name"]?.Value<string>()?.TrimStart('v', 'V');
                if (tag != null && Version.TryParse(tag, out _) && (best == null || IsNewer(tag, best))) {
                    best = tag;
                    bestUrl = release["html_url"]?.Value<string>();
                }
            }

            Latest = best;
            LatestUrl = string.IsNullOrEmpty(bestUrl) ? ReleasesPage : bestUrl;
            Log.Info(best == null ? "Sin versiones publicadas en GitHub"
                : NewerAvailable ? $"Hay una versión nueva de HKMultiplayer: {best} (instalada {HKMultiplayerMod.Version})"
                : $"HKMultiplayer {HKMultiplayerMod.Version} es la versión más nueva");
        } catch (Exception e) when (e is JsonException or InvalidCastException or FormatException or ArgumentException) {
            Log.Info($"No se pudo leer la lista de versiones: {e.Message}");
        }

        Changed?.Invoke();
    }

    /// <summary>Cada frame: en el menú principal, avisa de la actualización recién hecha o de la versión nueva.</summary>
    public static void Update() {
        if (_announcedUpdate && (_announcedNewer || !NewerAvailable)) {
            return;
        }

        var gm = GameManager.instance;
        var atMenu = gm != null && gm.gameState == GlobalEnums.GameState.MAIN_MENU && HeroController.instance == null &&
                     UIManager.instance != null && UIManager.instance.menuState == GlobalEnums.MainMenuState.MAIN_MENU;
        if (!atMenu) {
            _atMenuSince = -1f;
            return;
        }

        if (_atMenuSince < 0f) {
            _atMenuSince = Time.unscaledTime;
        }

        if (Time.unscaledTime - _atMenuSince < AnnounceDelay) {
            return;
        }

        if (!_announcedUpdate) {
            _announcedUpdate = true;
            if (JustUpdated) {
                Notice.Show(Lang.T($"HKMultiplayer se actualizó a la {HKMultiplayerMod.Version}. Novedades en Multijugador.",
                    $"HKMultiplayer was updated to {HKMultiplayerMod.Version}. What's new: in Multiplayer."), 8f);
                // El de la versión nueva, si lo hay, cuando acabe este
                _atMenuSince = Time.unscaledTime + 8f;
                return;
            }
        }

        if (!_announcedNewer && NewerAvailable) {
            _announcedNewer = true;
            Notice.Show(Lang.T($"Hay una versión nueva de HKMultiplayer: {Latest} (tienes la {HKMultiplayerMod.Version}). Descárgala en Multijugador.",
                $"A new HKMultiplayer version is out: {Latest} (you have {HKMultiplayerMod.Version}). Get it in Multiplayer."), 10f);
        }
    }

    /// <summary>Modo de prueba: como si GitHub hubiera dicho que la más nueva es esa.</summary>
    public static void TestSetLatest(string version) {
        Latest = version;
        LatestUrl = $"{ReleasesPage}/tag/v{version}";
        _announcedNewer = false;
        _atMenuSince = -1f;
        Changed?.Invoke();
    }

    /// <summary>Si la versión <paramref name="a"/> es más nueva que <paramref name="b"/> ("0.25.0" &gt; "0.24.1").</summary>
    public static bool IsNewer(string a, string b) =>
        Version.TryParse(a, out var left) && Version.TryParse(b, out var right) && left > right;
}
