using System.Collections.Generic;
using HKMultiplayer.UI;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Barras de vida pequeñas sobre los enemigos heridos (ajuste del host "Barras de enemigos", desactivado por defecto).
/// Los jefes no: tienen la suya abajo (<see cref="BossBar"/>). Como la barra del jefe, los enemigos se apuntan al
/// encenderse y la vida máxima es la mayor vista; en el PC que no mueve al enemigo se lee la vida de la marioneta, que se
/// sincroniza. Las barras se crean una vez y cada frame solo se mueven, se encienden o se apagan.
/// </summary>
internal sealed class EnemyBars {
    private const int MaxBars = 16;

    /// <summary>Vida a partir de la cual no se dibuja barra (objetos que el juego hace prácticamente invencibles).</summary>
    private const int MaxSensibleHealth = 5000;

    /// <summary>Más de esto de vida máxima para tener barra (los de un golpe no la necesitan).</summary>
    private const int MinMaxHealth = 2;

    /// <summary>Altura de la barra sobre lo alto del enemigo, en unidades del juego.</summary>
    private const float HeightAbove = 0.35f;

    private static readonly Vector2 BarSize = new(110f, 14f);

    private sealed class Tracked {
        public HealthManager Health;
        public Renderer Renderer;
        public int MaxHealth;
    }

    private sealed class Bar {
        public RectTransform Root;
        public Image Fill;
        public Tracked Shown;
        public int Hp = -1;
    }

    private static EnemyBars _active;
    private static bool _hooked;

    private readonly List<Tracked> _tracked = new();
    private readonly Bar[] _bars = new Bar[MaxBars];
    private RectTransform _root;
    private bool _visible;

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
        if (health == null || Difficulty.IsBoss(health) || ShadeSync.IsRemoteShade(health.gameObject)) {
            return;
        }

        foreach (var tracked in _tracked) {
            if (tracked.Health == health) {
                return;
            }
        }

        _tracked.Add(new Tracked { Health = health, Renderer = health.GetComponent<Renderer>(), MaxHealth = health.hp });
    }

    private void Build() {
        _root = HudCanvas.NewGroup("Enemigos");
        for (var i = 0; i < MaxBars; i++) {
            var bar = new Bar();
            bar.Fill = HudCanvas.NewBar($"Enemigo {i + 1}", _root, BarSize, new Color(0.78f, 0.1f, 0.1f, 1f), out bar.Root);
            bar.Root.gameObject.SetActive(false);
            _bars[i] = bar;
        }

        _root.gameObject.SetActive(false);
    }

    private void SetVisible(bool visible) {
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
        if (scene.Length == 0 || gm == null || gm.isPaused || camera == null || !HostSettings.Current.ShowEnemyBars ||
            gm.gameState != GlobalEnums.GameState.PLAYING) {
            SetVisible(false);
            return;
        }

        for (var i = _tracked.Count - 1; i >= 0; i--) {
            if (_tracked[i].Health == null) {
                _tracked.RemoveAt(i);
            }
        }

        if (_root == null) {
            Build();
        }

        SetVisible(true);
        var used = 0;
        foreach (var tracked in _tracked) {
            var health = tracked.Health;
            if (used >= MaxBars || !health.gameObject.activeInHierarchy || health.isDead || health.hp <= 0) {
                continue;
            }

            if (health.hp > tracked.MaxHealth) {
                tracked.MaxHealth = health.hp;
            }

            // Solo heridos, a la vista y con una vida con sentido
            if (health.hp >= tracked.MaxHealth || tracked.MaxHealth < MinMaxHealth ||
                tracked.MaxHealth >= MaxSensibleHealth || !TryPlace(camera, tracked, out var point)) {
                continue;
            }

            var bar = _bars[used++];
            if (!bar.Root.gameObject.activeSelf) {
                bar.Root.gameObject.SetActive(true);
            }

            if (bar.Shown != tracked) {
                bar.Shown = tracked;
                bar.Hp = -1;
            }

            bar.Root.anchoredPosition = point;
            if (bar.Hp != health.hp) {
                bar.Hp = health.hp;
                HudCanvas.SetFill(bar.Fill, (float) health.hp / tracked.MaxHealth);
            }
        }

        for (var i = used; i < MaxBars; i++) {
            if (_bars[i].Root.gameObject.activeSelf) {
                _bars[i].Root.gameObject.SetActive(false);
                _bars[i].Shown = null;
            }
        }
    }

    /// <summary>Punto del lienzo sobre el enemigo; false si no se ve (oculto o fuera de la pantalla).</summary>
    private static bool TryPlace(Camera camera, Tracked tracked, out Vector2 point) {
        point = default;
        var renderer = tracked.Renderer;
        if (renderer != null && !renderer.enabled) {
            return false;
        }

        Vector3 top;
        if (renderer != null) {
            var bounds = renderer.bounds;
            top = new Vector3(bounds.center.x, bounds.max.y + HeightAbove, bounds.center.z);
        } else {
            top = tracked.Health.transform.position + new Vector3(0f, 1.5f, 0f);
        }

        var viewport = camera.WorldToViewportPoint(top);
        if (viewport.z <= 0f || viewport.x < -0.05f || viewport.x > 1.05f || viewport.y < -0.05f || viewport.y > 1.1f) {
            return false;
        }

        return HudCanvas.WorldToCanvas(camera, top, out point);
    }

    /// <summary>Resumen para el modo de prueba: cuántos se siguen y cuántas barras se ven.</summary>
    public string Describe() {
        var shown = 0;
        foreach (var bar in _bars) {
            if (bar != null && bar.Root.gameObject.activeSelf) {
                shown++;
            }
        }

        var hurt = 0;
        foreach (var tracked in _tracked) {
            if (tracked.Health != null && tracked.Health.hp < tracked.MaxHealth && !tracked.Health.isDead) {
                hurt++;
            }
        }

        return $"seguidos={_tracked.Count} heridos={hurt} barras={shown} activo={HostSettings.Current.ShowEnemyBars}";
    }
}
