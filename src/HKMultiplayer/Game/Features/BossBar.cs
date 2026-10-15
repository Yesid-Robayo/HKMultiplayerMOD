using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HKMultiplayer.UI;
using UnityEngine;
using UnityEngine.UI;
using GameText = TeamCherry.Localization.Language;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Barra de vida de los jefes, abajo en el centro y con su nombre (el de su entrada del diario, en el idioma del juego).
/// Funciona igual en el PC que no mueve al jefe, porque la vida de las marionetas se sincroniza. Los jefes se apuntan al
/// encenderse (sin buscar por la sala) y la vida máxima es la mayor vista (algunos la suben al empezar).
/// </summary>
internal sealed class BossBar {
    private const int MaxBars = 3;

    /// <summary>Distancia entre una barra y la siguiente (hacia arriba).</summary>
    private const float BarStep = 78f;

    /// <summary>Cuánto suben los avisos con una barra visible (hasta encima de su nombre).</summary>
    private const float NoticeLift = 125f;

    /// <summary>Vida a partir de la cual no se dibuja barra (objetos que el juego hace prácticamente invencibles).</summary>
    private const int MaxSensibleHealth = 5000;

    private static readonly FieldInfo PlayerDataNameField =
        typeof(EnemyDeathEffects).GetField("playerDataName", BindingFlags.NonPublic | BindingFlags.Instance);

    private sealed class Tracked {
        public HealthManager Health;
        public Renderer Renderer;
        public int MaxHealth;
        public string Name;
        public Vector3 StartPosition;

        /// <summary>Ya se movió o recibió daño: está peleando (ver <see cref="Engaged"/>).</summary>
        public bool Engaged;
    }

    /// <summary>Lo que tiene que moverse un jefe desde donde apareció para darlo por despierto.</summary>
    private const float WakeDistance = 0.5f;

    private sealed class Bar {
        public RectTransform Root;
        public Text Name;
        public Image Fill;
        public Tracked Shown;
        public int Hp = -1;
    }

    private static BossBar _active;
    private static bool _hooked;

    private readonly List<Tracked> _tracked = new();
    private readonly Bar[] _bars = new Bar[MaxBars];
    private RectTransform _root;
    private bool _visible;

    /// <summary>Nombre del diario de cada enemigo (clave: su playerDataName), leído del propio diario una vez.</summary>
    private static Dictionary<string, string> _journalNames;

    /// <summary>Se llama al cargar el mod.</summary>
    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.HealthManager.OnEnable += (orig, self) => {
            orig(self);
            _active?.Track(self);
        };
    }

    public void Attach() {
        _active = this;
        _tracked.Clear();

        // Los que ya estaban encendidos al empezar la partida (una vez)
        foreach (var health in Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None)) {
            Track(health);
        }
    }

    public void Detach() {
        if (_active == this) {
            _active = null;
        }

        _tracked.Clear();
        SetVisible(false);
    }

    private void Track(HealthManager health) {
        if (health == null || !Difficulty.IsBoss(health) || ShadeSync.IsRemoteShade(health.gameObject)) {
            return;
        }

        // Solo la vida que reciben los golpes (la primera del objeto): los Señores Mantis de la segunda fase llevan dos, y
        // la segunda no baja nunca. Cada uno tenía una barra de más, siempre llena
        if (health.GetComponent<HealthManager>() != health) {
            return;
        }

        foreach (var tracked in _tracked) {
            if (tracked.Health == health) {
                return;
            }
        }

        _tracked.Add(new Tracked {
            Health = health, Renderer = health.GetComponent<Renderer>(), MaxHealth = health.hp, Name = BossName(health),
            StartPosition = health.transform.position
        });
    }

    private void Build() {
        _root = HudCanvas.NewGroup("Jefes");
        _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0f);
        for (var i = 0; i < MaxBars; i++) {
            var bar = new Bar();
            bar.Root = HudCanvas.NewGroup($"Jefe {i + 1}", _root);
            bar.Root.anchoredPosition = new Vector2(0f, 90f + i * BarStep);
            bar.Fill = HudCanvas.NewBar("Vida", bar.Root, new Vector2(900f, 22f), new Color(0.78f, 0.1f, 0.1f, 1f), out _);
            bar.Name = HudCanvas.NewText("Nombre", bar.Root, 30, TextAnchor.MiddleCenter, title: true);
            bar.Name.rectTransform.anchoredPosition = new Vector2(0f, 32f);
            bar.Root.gameObject.SetActive(false);
            _bars[i] = bar;
        }

        _root.gameObject.SetActive(false);
    }

    private void SetVisible(bool visible) {
        if (!visible) {
            Notice.SetLift(0f);
        }

        if (_visible == visible) {
            return;
        }

        _visible = visible;
        if (_root != null) {
            _root.gameObject.SetActive(visible);
        }
    }

    /// <summary>Llamar en LateUpdate.</summary>
    public void Update(string scene) {
        var gm = GameManager.instance;
        var camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
        if (scene.Length == 0 || gm == null || gm.isPaused || camera == null || !HostSettings.Current.ShowBossBar ||
            UI.HudCanvas.GameHudHidden() ||
            gm.gameState != GlobalEnums.GameState.PLAYING) {
            SetVisible(false);
            return;
        }

        // Los destruidos (al salir de la sala) se olvidan
        for (var i = _tracked.Count - 1; i >= 0; i--) {
            if (_tracked[i].Health == null) {
                _tracked.RemoveAt(i);
            }
        }

        if (_tracked.Count == 0) {
            SetVisible(false);
            return;
        }

        if (_root == null) {
            Build();
        }

        SetVisible(true);
        var used = 0;
        foreach (var tracked in _tracked) {
            var health = tracked.Health;
            // También el que ya está muriendo: el cuerpo del Falso Caballero vuelve a tener vida y su barra se quedaba
            if (used >= MaxBars || !health.gameObject.activeInHierarchy || health.isDead || health.hp <= 0 ||
                Scene.EnemySync.IsDying(health) ||
                health.hp >= MaxSensibleHealth || !OnScreen(camera, tracked)) {
                continue;
            }

            if (health.hp > tracked.MaxHealth) {
                tracked.MaxHealth = health.hp;
            }

            if (!Engaged(tracked)) {
                continue;
            }

            var bar = _bars[used++];
            if (!bar.Root.gameObject.activeSelf) {
                bar.Root.gameObject.SetActive(true);
            }

            if (bar.Shown != tracked) {
                bar.Shown = tracked;
                bar.Name.text = tracked.Name;
                bar.Hp = -1;
            }

            if (bar.Hp != health.hp) {
                bar.Hp = health.hp;
                HudCanvas.SetFill(bar.Fill, tracked.MaxHealth > 0 ? (float) health.hp / tracked.MaxHealth : 0f);
            }
        }

        for (var i = used; i < MaxBars; i++) {
            if (_bars[i].Root.gameObject.activeSelf) {
                _bars[i].Root.gameObject.SetActive(false);
                _bars[i].Shown = null;
            }
        }

        // Los avisos (abajo a la derecha) suben por encima de las barras para no pisarlas
        Notice.SetLift(used > 0 ? NoticeLift + (used - 1) * BarStep : 0f);
    }

    /// <summary>
    /// Si ya está peleando: se movió desde donde apareció o recibió daño. Los que esperan quietos su turno no tienen barra
    /// (los seis Caballeros Vigía duermen a la vista en el techo y salían todas las barras a la vez). Se mira así y no por
    /// su lógica porque en los PCs que no lo mueven está parada.
    /// </summary>
    private static bool Engaged(Tracked tracked) {
        if (!tracked.Engaged) {
            var health = tracked.Health;
            tracked.Engaged = health.hp < tracked.MaxHealth ||
                              (health.transform.position - tracked.StartPosition).sqrMagnitude > WakeDistance * WakeDistance;
        }

        return tracked.Engaged;
    }

    /// <summary>Si el jefe está a la vista (o cerca del borde): antes de que aparezca no se muestra su barra.</summary>
    private static bool OnScreen(Camera camera, Tracked tracked) {
        if (tracked.Renderer != null && !tracked.Renderer.enabled) {
            return false;
        }

        var viewport = camera.WorldToViewportPoint(tracked.Health.transform.position);
        return viewport.z > 0f && viewport.x > -0.2f && viewport.x < 1.2f && viewport.y > -0.2f && viewport.y < 1.2f;
    }

    /// <summary>
    /// Nombre del jefe en el idioma del juego: el título con el que el juego lo presenta (hoja "Titles"); si no tiene,
    /// el de su entrada del diario (hay jefes con la entrada de otro: el Rey de las Pesadillas lleva la de Hornet); y si
    /// tampoco, el del objeto limpio.
    /// </summary>
    private static string BossName(HealthManager health) {
        var title = TitleName(health);
        if (title != null) {
            return title;
        }

        var deathEffects = health.GetComponent<EnemyDeathEffects>();
        var dataName = deathEffects != null ? PlayerDataNameField?.GetValue(deathEffects) as string : null;
        if (!string.IsNullOrEmpty(dataName)) {
            if (_journalNames == null || _journalNames.Count == 0) {
                _journalNames = new Dictionary<string, string>();
                // Las entradas del diario viven en el inventario (cargado aunque esté cerrado)
                foreach (var entry in Resources.FindObjectsOfTypeAll<JournalEntryStats>()) {
                    if (!string.IsNullOrEmpty(entry.playerDataName) && !string.IsNullOrEmpty(entry.convoName)) {
                        _journalNames[entry.playerDataName] = entry.convoName;
                    }
                }
            }

            if (_journalNames.TryGetValue(dataName, out var convoName) && GameText.Has("NAME_" + convoName, "Journal")) {
                var localized = GameText.Get("NAME_" + convoName, "Journal");
                if (!string.IsNullOrEmpty(localized) && !localized.Contains("#!#")) {
                    return localized.Replace("<br>", " ").Trim();
                }
            }
        }

        var name = Regex.Replace(health.gameObject.name, @"\(Clone\)|\(\d+\)|\bNew\b|\d+$", string.Empty).Trim();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) {
            builder.Append(c == '_' ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private const string TitlesSheet = "Titles";

    /// <summary>
    /// Nombre del jefe en el idioma del juego tal como sale en su presentación: la lógica del jefe (o lo que cuelga de él)
    /// lleva la clave de su título ("FALSE_KNIGHT", "FAILED_CHAMPION"...) en un texto de sus FSM; el título es esa clave
    /// con "_SUPER" (la parte pequeña de encima), "_MAIN" (la grande) y "_SUB" (la de debajo) en la hoja "Titles". Null
    /// si no se encuentra.
    /// </summary>
    private static string TitleName(HealthManager health) {
        foreach (var fsm in health.GetComponentsInChildren<PlayMakerFSM>(true)) {
            foreach (var variable in fsm.FsmVariables.StringVariables) {
                var found = TitleFor(variable.Value);
                if (found != null) {
                    return found;
                }
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    foreach (var field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                        if (field.FieldType == typeof(HutongGames.PlayMaker.FsmString) &&
                            field.GetValue(action) is HutongGames.PlayMaker.FsmString text) {
                            var found = TitleFor(text.Value);
                            if (found != null) {
                                return found;
                            }
                        }
                    }
                }
            }
        }

        // Si su lógica no la lleva, la clave sale a veces de su nombre ("Grimm Boss" → "GRIMM"): las palabras en
        // mayúsculas unidas con "_", de la más larga a la más corta
        var words = Regex.Replace(health.gameObject.name, @"\(Clone\)|\(\d+\)|\d+", " ").ToUpperInvariant()
            .Split(new[] { ' ', '_', '-' }, System.StringSplitOptions.RemoveEmptyEntries);
        for (var length = words.Length; length > 0; length--) {
            for (var start = 0; start + length <= words.Length; start++) {
                var found = TitleFor(string.Join("_", words, start, length));
                if (found != null) {
                    return found;
                }
            }
        }

        return null;
    }

    private static string TitleFor(string key) {
        if (string.IsNullOrEmpty(key) || key.Length > 40 || key.IndexOf(' ') >= 0 || !GameText.Has(key + "_MAIN", TitlesSheet)) {
            return null;
        }

        // El título va en partes: la pequeña de encima, la grande y la pequeña de debajo ("Campeón" / "Fallido")
        var main = GameText.Get(key + "_MAIN", TitlesSheet);
        if (string.IsNullOrEmpty(main) || main.Contains("#!#")) {
            return null;
        }

        return (TitlePart(key + "_SUPER") + " " + main + " " + TitlePart(key + "_SUB")).Replace("<br>", " ").Trim();
    }

    /// <summary>Parte pequeña del título (encima o debajo de la grande), vacía si no tiene.</summary>
    private static string TitlePart(string key) {
        var part = GameText.Has(key, TitlesSheet) ? GameText.Get(key, TitlesSheet) : string.Empty;
        return string.IsNullOrEmpty(part) || part.Contains("#!#") ? string.Empty : part;
    }

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() {
        var text = new StringBuilder();
        foreach (var tracked in _tracked) {
            if (tracked.Health != null) {
                var health = tracked.Health;
                var camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
                var path = health.transform.parent != null ? $"{health.transform.parent.name}/{health.name}" : health.name;
                text.Append($"{tracked.Name} {health.hp}/{tracked.MaxHealth} [{path} id={health.GetInstanceID()} " +
                            $"activo={health.gameObject.activeInHierarchy} muerto={health.isDead} " +
                            $"visible={(camera != null && OnScreen(camera, tracked))} " +
                            $"pos=({health.transform.position.x:0.0},{health.transform.position.y:0.0})]; ");
            }
        }

        return text.Length == 0 ? "ninguno" : text.ToString();
    }
}
