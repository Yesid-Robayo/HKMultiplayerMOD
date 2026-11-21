using System.Collections.Generic;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Resumen al acabar una partida coop: cuánto se jugó, los jefes vencidos, el geo conseguido, las veces que este jugador
/// cayó y las muertes de verdad. Se cuenta en cada PC durante la sesión (sin red) y se enseña al volver al menú, y queda
/// en el registro.
/// </summary>
internal sealed class SessionSummary {
    private static bool _hooked;
    private static SessionSummary _active;

    private GameSession _session;
    private float _startedAt;
    private int _bosses;
    private int _geo;
    private int _downs;
    private int _deaths;
    private int _lastGeo = -1;
    private bool _wasDown;
    private bool _wasDead;

    /// <summary>Jefes ya contados (un mismo jefe puede «morir» dos veces: falsas muertes, cambios de fase).</summary>
    private readonly HashSet<int> _counted = new();

    /// <summary>
    /// Jefes que acaban de morir y desde cuándo. Solo cuentan si siguen muertos <see cref="DeadSeconds"/> (o desaparecen
    /// así): el Falso Caballero, por ejemplo, «muere» en cada cambio de fase y vuelve con toda su vida.
    /// </summary>
    private readonly Dictionary<HealthManager, float> _dying = new();

    private readonly List<HealthManager> _done = new();

    private const float DeadSeconds = 3f;

    /// <summary>Cuándo se contó el último jefe (el final de una sala de jefe no vuelve a contar el mismo).</summary>
    private float _lastBossAt = -100f;

    private void CountBoss(int id) {
        if (_counted.Add(id)) {
            _bosses++;
            _lastBossAt = Time.unscaledTime;
        }
    }

    /// <summary>El último resumen, para enseñarlo en el menú (vacío si no hay).</summary>
    public static string Last { get; private set; } = string.Empty;

    public void Attach(GameSession session) {
        _session = session;
        _active = this;
        _startedAt = Time.unscaledTime;
        _bosses = _geo = _downs = _deaths = 0;
        _lastGeo = -1;
        _wasDown = _wasDead = false;
        _counted.Clear();
        _dying.Clear();
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.HealthManager.Die += (orig, self, direction, attackType, ignoreEvasion) => {
            var wasDead = self != null && self.GetIsDead();
            orig(self, direction, attackType, ignoreEvasion);
            if (!wasDead && self != null && _active != null && self.GetIsDead() && Difficulty.IsBoss(self)) {
                _active._dying[self] = Time.unscaledTime;
            }
        };

        // Las salas de jefe de Dios Hogar (estatuas y Panteones) acaban cuando caen sus jefes: cuenta uno si no se contó
        // ninguno justo antes (los jefes con fases o cabezas aparte no siempre pasan por la muerte de su cuerpo)
        On.BossSceneController.EndBossScene += (orig, self) => {
            orig(self);
            var active = _active;
            if (active != null && Time.unscaledTime - active._lastBossAt > 10f) {
                active.CountBoss(self.GetInstanceID());
            }
        };
    }

    /// <summary>Al dejar la partida: el resumen, en el registro y en un aviso.</summary>
    public void Detach() {
        if (_session == null) {
            return;
        }

        // Los que ya estaban muertos al salir también cuentan
        foreach (var pair in _dying) {
            if (pair.Key == null || pair.Key.GetIsDead()) {
                CountBoss(pair.Key != null ? pair.Key.GetInstanceID() : pair.GetHashCode());
            }
        }

        _dying.Clear();
        var minutes = Mathf.Max(0, Mathf.RoundToInt((Time.unscaledTime - _startedAt) / 60f));
        var time = minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
        Last = Lang.F("Resumen de la partida: {0} · {1} · {2} geo · caíste {3} · {4}",
            "Session summary: {0} · {1} · {2} geo · went down {3} · {4}", time,
            Lang.F(_bosses == 1 ? "{0} jefe" : "{0} jefes", _bosses == 1 ? "{0} boss" : "{0} bosses", _bosses), _geo,
            Lang.F(_downs == 1 ? "{0} vez" : "{0} veces", _downs == 1 ? "{0} time" : "{0} times", _downs),
            Lang.F(_deaths == 1 ? "{0} muerte" : "{0} muertes", _deaths == 1 ? "{0} death" : "{0} deaths", _deaths));
        Log.Info($"Resumen de la sesión: {minutes} min, jefes={_bosses}, geo={_geo}, caídas={_downs}, muertes={_deaths}");
        // Solo si se jugó algo: al fallar la conexión no hay nada que resumir
        if (minutes > 0 || _bosses > 0 || _geo > 0) {
            Notice.Show(Last, 12f);
        }

        _session = null;
        if (_active == this) {
            _active = null;
        }
    }

    /// <summary>Cada frame: el geo que entra y las caídas y muertes del jugador local.</summary>
    public void Update() {
        var pd = PlayerData.instance;
        var hero = HeroController.instance;
        if (_session == null || pd == null || hero == null) {
            return;
        }

        // Solo lo que sube (gastar en tiendas no resta); un salto enorme es cargar otra partida, no geo ganado
        if (_lastGeo >= 0 && pd.geo > _lastGeo && pd.geo - _lastGeo < 100000) {
            _geo += pd.geo - _lastGeo;
        }

        _lastGeo = pd.geo;
        if (_dying.Count > 0) {
            var now = Time.unscaledTime;
            _done.Clear();
            foreach (var pair in _dying) {
                var health = pair.Key;
                if (health == null) {
                    // Desapareció estando muerto (la sala lo quitó): cuenta
                    CountBoss(pair.GetHashCode());
                    _done.Add(health);
                } else if (!health.GetIsDead()) {
                    // Volvió a la vida: era un cambio de fase
                    _done.Add(health);
                } else if (now - pair.Value >= DeadSeconds) {
                    CountBoss(health.GetInstanceID());
                    _done.Add(health);
                }
            }

            foreach (var health in _done) {
                _dying.Remove(health);
            }
        }

        var down = PartyState.IsDown(_session.LocalId);
        if (down && !_wasDown) {
            _downs++;
        }

        _wasDown = down;
        var dead = hero.cState.dead;
        if (dead && !_wasDead && !down) {
            _deaths++;
        }

        _wasDead = dead;
    }
}
