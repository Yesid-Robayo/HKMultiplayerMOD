using System;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Búfer de interpolación de un jugador remoto (sin Unity, se prueba fuera del juego). Guarda los últimos estados
/// ordenados por el tick del remitente y dibuja al jugador un poco en el pasado, entre dos estados reales, para que
/// el movimiento sea continuo aunque los paquetes lleguen a destiempo o se pierda alguno.
/// <para>
/// Reloj: cada llegada da una medida de "tick local − tick del remitente"; la menor es la de los paquetes que llegaron
/// sin retraso extra. El retraso de dibujo se adapta a la variación observada (más variación, más margen) y el reloj de
/// dibujo se acerca a su objetivo acelerando o frenando un poco, nunca a saltos, para no dar tirones.
/// </para>
/// </summary>
internal sealed class SnapshotBuffer {
    private const int Capacity = 64;

    /// <summary>Ticks por segundo del remitente.</summary>
    private const double TickRate = 60;

    /// <summary>Retraso mínimo y máximo de dibujo, en ticks.</summary>
    private const double MinDelay = 1.5;

    private const double MaxDelay = 12;

    /// <summary>Más allá de esta diferencia, el reloj de dibujo salta en vez de acercarse poco a poco.</summary>
    private const double SnapThreshold = 30;

    /// <summary>Distancia entre dos estados seguidos a partir de la cual es un salto (no se interpola).</summary>
    private const float TeleportDistance = 4f;

    /// <summary>Ticks que se puede adelantar el dibujo al último estado conocido, siguiendo su velocidad.</summary>
    private const double MaxExtrapolation = 3;

    private readonly PlayerSnapshot[] _items = new PlayerSnapshot[Capacity];
    private int _count;

    private bool _hasClock;
    private double _offset;
    private double _jitterPeak;
    private double _delay = 3;
    private double _renderTick;
    private double _lastTime;

    /// <summary>Retraso de dibujo actual, en ticks.</summary>
    public double Delay => _delay;

    /// <summary>Variación observada de las llegadas, en ticks.</summary>
    public double Jitter => _jitterPeak;

    public int Count => _count;

    /// <summary>Diagnóstico: veces que se dibujó más allá del último estado (siguiendo su velocidad o parado).</summary>
    public int BeyondNewest { get; private set; }

    /// <summary>Diagnóstico: veces que el reloj de dibujo saltó.</summary>
    public int Snaps { get; private set; }

    public void Clear() {
        _count = 0;
        _hasClock = false;
        _jitterPeak = 0;
        _delay = 3;
    }

    /// <summary>
    /// Añade un estado recibido en el instante local <paramref name="time"/> (segundos). Con
    /// <paramref name="timingReliable"/> en false (el frame local fue un parón, p. ej. al cargar una sala) la llegada no
    /// cuenta como variación de la red: el retraso lo causó este PC, no la conexión.
    /// </summary>
    public void Add(in PlayerSnapshot snapshot, double time, bool timingReliable = true) {
        // El remitente volvió a empezar (reinició el juego): se descarta lo anterior
        if (_count > 0 && snapshot.Tick + 600 < _items[_count - 1].Tick) {
            Clear();
        }

        UpdateClock(snapshot.Tick, time, timingReliable);

        // Inserción ordenada (los no fiables pueden llegar desordenados); los repetidos se ignoran
        var index = _count;
        while (index > 0 && _items[index - 1].Tick > snapshot.Tick) {
            index--;
        }

        if (index > 0 && _items[index - 1].Tick == snapshot.Tick) {
            return;
        }

        if (_count == Capacity) {
            if (index == 0) {
                return;
            }

            Array.Copy(_items, 1, _items, 0, Capacity - 1);
            _count--;
            index--;
        }

        if (index < _count) {
            Array.Copy(_items, index, _items, index + 1, _count - index);
        }

        _items[index] = snapshot;
        _count++;
    }

    private void UpdateClock(uint tick, double time, bool timingReliable) {
        var sample = time * TickRate - tick;
        if (!_hasClock) {
            _hasClock = true;
            _offset = sample;
            _renderTick = tick - _delay;
            _lastTime = time;
            return;
        }

        if (sample < _offset) {
            // Llegó antes que nunca: esa es la latencia real
            _offset = sample;
        } else if (timingReliable) {
            // Deriva lenta hacia arriba (relojes que no van igual, la ruta se volvió más lenta)
            _offset += (sample - _offset) * 0.002;
        } else {
            return;
        }

        // Pico de variación con olvido lento
        var late = sample - _offset;
        _jitterPeak = late > _jitterPeak ? late : _jitterPeak * 0.995;
        var targetDelay = Math.Min(MaxDelay, Math.Max(MinDelay, 1 + _jitterPeak * 1.25));
        _delay += (targetDelay - _delay) * 0.05;
    }

    /// <summary>
    /// Avanza el reloj de dibujo hasta el instante <paramref name="time"/> y calcula cómo dibujar al jugador.
    /// </summary>
    /// <returns>false si aún no hay estados.</returns>
    public bool Sample(double time, out PlayerSnapshot result) {
        result = default;
        if (_count == 0) {
            return false;
        }

        var dt = Math.Max(0, time - _lastTime);
        _lastTime = time;
        var target = time * TickRate - _offset - _delay;
        var error = target - _renderTick;
        if (Math.Abs(error) > SnapThreshold) {
            _renderTick = target;
            Snaps++;
        } else {
            // Acercarse acelerando o frenando como mucho un 10%
            var rate = 1 + Math.Max(-0.1, Math.Min(0.1, error * 0.05));
            _renderTick += dt * TickRate * rate;
        }

        var render = _renderTick;

        // Se descartan los estados que ya no hacen falta: queda uno anterior al de dibujo, y siempre dos para poder
        // seguir la velocidad del último si no llegan más
        var drop = 0;
        while (drop + 2 < _count && _items[drop + 1].Tick <= render) {
            drop++;
        }

        if (drop > 0) {
            Array.Copy(_items, drop, _items, 0, _count - drop);
            _count -= drop;
        }

        if (_count == 1 || render <= _items[0].Tick) {
            result = _items[0];
            return true;
        }

        ref var a = ref _items[0];
        ref var b = ref _items[1];
        result = a;
        if (Distance(a, b) > TeleportDistance) {
            return true;
        }

        var span = (double) (b.Tick - a.Tick);
        var t = (render - a.Tick) / span;
        if (t > 1) {
            // Sin estados más nuevos: seguir un poco su velocidad si venían seguidos, y luego quedarse ahí (volver
            // atrás al último estado real sería un tirón)
            result = b;
            BeyondNewest++;
            if (span <= 3) {
                var ahead = Math.Min(t - 1, MaxExtrapolation / span);
                result.X = (float) (b.X + (b.X - a.X) * ahead);
                result.Y = (float) (b.Y + (b.Y - a.Y) * ahead);
            }

            return true;
        }

        result.X = (float) (a.X + (b.X - a.X) * t);
        result.Y = (float) (a.Y + (b.Y - a.Y) * t);
        return true;
    }

    private static float Distance(in PlayerSnapshot a, in PlayerSnapshot b) {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return (float) Math.Sqrt(dx * dx + dy * dy);
    }
}
