using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Las plataformas de los sueños (<see cref="DreamPlatform"/>) se encienden también bajo los compañeros, sin red. En el
/// juego aparecen cuando el caballero local entra en su zona interior y se apagan cuando sale de la exterior; el avatar
/// de un compañero no tiene colisión, así que en los demás PCs se le veía de pie en el aire.
/// <para>
/// En cada PC: si la caja de un compañero entra en la zona interior de una plataforma apagada, se enciende; si el
/// caballero local sale de la exterior con un compañero aún dentro, el juego no la apaga. Solo se apagan aquí las que
/// se encendieron o se dejaron encendidas por un compañero, y solo cuando ya no queda nadie en su zona exterior: las que
/// enciende el propio juego por su cuenta (<c>showOnEnable</c> o sus FSM) no se tocan.
/// </para>
/// </summary>
internal sealed class PartnerDreamPlatforms {
    /// <summary>Espera tras cambiar de sala antes de buscar sus plataformas (las crea al cargar).</summary>
    private const float ScanDelay = 0.5f;

    /// <summary>Cada cuánto se miran las zonas (unas 20 veces por segundo).</summary>
    private const float CheckInterval = 0.05f;

    private static readonly FieldInfo VisibleField =
        typeof(DreamPlatform).GetField("visible", BindingFlags.Instance | BindingFlags.NonPublic);

    private static bool _hooked;
    private static PartnerDreamPlatforms _active;

    private readonly List<DreamPlatform> _platforms = new();

    /// <summary>Plataformas encendidas (o que se dejaron encendidas) por estar dentro un compañero.</summary>
    private readonly HashSet<DreamPlatform> _held = new();

    private List<Vector3> _partners;
    private string _scene = string.Empty;
    private float _scanAt = -1f;
    private float _nextCheck;
    private Vector2 _size = new(0.5f, 1.3f);
    private Vector2 _offset;

    /// <summary>Plataformas de la sala que reaccionan a quien pasa.</summary>
    public int PlatformCount => _platforms.Count;

    /// <summary>Veces que un compañero encendió una plataforma o evitó que se apagara.</summary>
    public int Shown { get; private set; }

    public void Attach() {
        _active = this;
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.DreamPlatform.Hide += (orig, self) => {
            // El caballero local salió de su zona, pero sigue un compañero dentro: se queda encendida
            if (_active != null && _active.PartnerInside(self, outer: true)) {
                if (_active._held.Add(self)) {
                    _active.Shown++;
                }

                return;
            }

            orig(self);
        };
    }

    public void Detach() {
        if (_active == this) {
            _active = null;
        }

        _platforms.Clear();
        _held.Clear();
        _partners = null;
    }

    /// <summary>La sala local cambió: sus plataformas se buscan un poco después.</summary>
    public void SetScene(string scene) {
        if (scene == _scene) {
            return;
        }

        _scene = scene;
        _platforms.Clear();
        _held.Clear();
        _scanAt = scene.Length > 0 ? Time.unscaledTime + ScanDelay : -1f;
    }

    /// <summary>
    /// Enciende o apaga las plataformas según dónde están los compañeros (<paramref name="partners"/>: las posiciones
    /// de los que se ven en la sala). Llamar cada frame, después de mover los avatares.
    /// </summary>
    public void Update(HeroController hero, List<Vector3> partners) {
        _partners = partners;
        var now = Time.unscaledTime;
        if (_scanAt >= 0f && now >= _scanAt) {
            Scan(hero);
        }

        if (_platforms.Count == 0 || now < _nextCheck) {
            return;
        }

        _nextCheck = now + CheckInterval;
        for (var i = _platforms.Count - 1; i >= 0; i--) {
            var platform = _platforms[i];
            if (platform == null) {
                _platforms.RemoveAt(i);
                continue;
            }

            if (!platform.isActiveAndEnabled) {
                continue;
            }

            if (!IsVisible(platform)) {
                _held.Remove(platform);
                if (PartnerInside(platform, outer: false)) {
                    platform.Show();
                    _held.Add(platform);
                    Shown++;
                }

                continue;
            }

            // Encendida por un compañero: se apaga cuando ya no queda nadie en su zona exterior, como en el juego
            if (_held.Contains(platform) && !PartnerInside(platform, outer: true) && !HeroInside(hero, platform)) {
                _held.Remove(platform);
                platform.Hide();
            }
        }
    }

    private static bool IsVisible(DreamPlatform platform) =>
        VisibleField == null || (bool) VisibleField.GetValue(platform);

    /// <summary>Si la caja de algún compañero toca la zona interior o exterior de la plataforma.</summary>
    private bool PartnerInside(DreamPlatform platform, bool outer) {
        var zone = Zone(platform, outer);
        if (zone == null || _partners == null) {
            return false;
        }

        var bounds = zone.bounds;
        foreach (var position in _partners) {
            if (Bounds2D.Overlaps(bounds, new Bounds((Vector2) position + _offset, _size))) {
                return true;
            }
        }

        return false;
    }

    private static bool HeroInside(HeroController hero, DreamPlatform platform) {
        var zone = Zone(platform, outer: true);
        var box = hero != null ? hero.GetComponent<BoxCollider2D>() : null;
        return zone != null && box != null && Bounds2D.Overlaps(zone.bounds, box.bounds);
    }

    private static Collider2D Zone(DreamPlatform platform, bool outer) {
        var trigger = outer ? platform.outerCollider : platform.innerCollider;
        return trigger != null ? trigger.GetComponent<Collider2D>() : null;
    }

    /// <summary>Las plataformas de la sala que se encienden al paso (las de <c>showOnEnable</c> no dependen de nadie).</summary>
    private void Scan(HeroController hero) {
        _scanAt = -1f;
        _platforms.Clear();
        var box = hero != null ? hero.GetComponent<BoxCollider2D>() : null;
        if (box != null) {
            _size = box.size;
            _offset = box.offset;
        }

        foreach (var platform in Object.FindObjectsByType<DreamPlatform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
            if (!platform.showOnEnable && platform.innerCollider != null) {
                _platforms.Add(platform);
            }
        }

        if (_platforms.Count > 0) {
            Util.Log.Info($"Plataformas de sueño en {_scene}: {_platforms.Count}");
        }
    }
}
