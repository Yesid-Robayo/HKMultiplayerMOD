using HKMultiplayer.Session;
using HKMultiplayer.Steam;
using HKMultiplayer.UI;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// La presencia de Steam durante una partida coop: dónde está el jugador y con cuántos juega («Coop (2/4) · Ciudad de
/// Lágrimas»), en la ficha del juego que ven sus amigos, y los que juegan juntos agrupados en su lista de amigos. Se
/// quita al salir de la partida. Lo aprobó el usuario el 02/11/2026.
/// <para>
/// El grupo es una huella del código de la partida (igual para todos los de ella), nunca el código: con él cualquiera
/// podría unirse. No lleva nada del PC ni del jugador.
/// </para>
/// </summary>
internal sealed class SteamPresence {
    private const float Interval = 5f;

    private GameSession _session;
    private float _next;
    private string _sent;

    public void Attach(GameSession session) {
        _session = session;
        _sent = null;
        _next = 0f;
    }

    public void Detach() {
        _session = null;
        if (_sent != null) {
            SteamLink.Platform?.SetPresence(null, null, 0);
            _sent = null;
        }
    }

    /// <summary>Cada frame: si cambió la zona o el número de jugadores, se actualiza (como mucho cada 5 s).</summary>
    public void Update() {
        var now = Time.unscaledTime;
        var steam = SteamLink.Platform;
        if (_session == null || steam == null || now < _next) {
            return;
        }

        _next = now + Interval;
        var count = 0;
        foreach (var _ in _session.Players) {
            count++;
        }

        var zone = Zones.Name(Vitals.CurrentZone());
        var text = Lang.F("Coop ({0}/4)", "Co-op ({0}/4)", count) + (string.IsNullOrEmpty(zone) ? string.Empty : $" · {zone}");
        if (text == _sent) {
            return;
        }

        _sent = text;
        steam.SetPresence(text, Group(_session.RoomCode), count);
    }

    /// <summary>Huella corta del código de la partida (FNV-1a), igual en todos los PCs de la partida.</summary>
    private static string Group(string code) {
        var hash = 2166136261u;
        foreach (var c in code ?? string.Empty) {
            hash = (hash ^ c) * 16777619u;
        }

        return $"hkmp{hash:x8}";
    }
}
