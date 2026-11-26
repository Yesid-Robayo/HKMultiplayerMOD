using HKMultiplayer.Net;

namespace HKMultiplayer.Tests;

/// <summary>
/// Microcortes de la red (issue #29: echaba al invitado a menudo). La conexión aguanta un corte corto y al volver se pone al
/// día sola; solo un silencio de más de <see cref="NetConstants.TimeoutSeconds"/> la corta. Van en tiempo real (unos 35 s).
/// </summary>
public static partial class Program {
    private static void RunOutageTests() {
        Run("Microcorte de 12 s: no se corta y lo enviado durante el corte llega entero y en orden", SurvivesShortOutage);
        Run("Silencio más largo que el tiempo de espera: se corta en los dos lados", TimesOutAfterLongSilence);
    }

    private static void SurvivesShortOutage() {
        var pair = new Pair();
        pair.ConnectOrFail();
        // Se pierde todo en los dos sentidos durante 12 s (antes se cortaba a los 10) mientras el cliente sigue enviando
        pair.Network.LossRate = 1;
        const int count = 240;
        for (uint i = 0; i < count; i++) {
            var writer = pair.Client.Connection.BeginReliable();
            writer.WriteByte(20);
            writer.WriteUInt(i);
            pair.Client.Connection.EndMessage();
            pair.Pump(() => false, 0.05);
        }

        Assert(pair.Client.Connection.SilentSeconds >= 11, $"el corte duró {pair.Client.Connection.SilentSeconds:0.0} s");
        Assert(pair.Client.IsConnected && pair.ServerListener.Disconnects.Count == 0, "se cortó durante el microcorte");
        Assert(pair.ServerListener.Reliable.Count == 0, "llegó algo con la red cortada");

        // Vuelve la red: todo lo pendiente llega, una vez y en orden, sin reconectar
        pair.Network.LossRate = 0;
        Assert(pair.Pump(() => pair.ServerListener.Reliable.Count >= count, 10),
            $"tras el corte llegaron {pair.ServerListener.Reliable.Count} de {count}");
        for (var i = 0; i < count; i++) {
            Assert(pair.ServerListener.Reliable[i] == i, $"fuera de orden en {i}");
        }

        Assert(pair.Client.Connection.SilentSeconds < 1, "el cliente no volvió a recibir del servidor");
        Assert(pair.Client.IsConnected && pair.ServerListener.Disconnects.Count == 0 &&
               pair.ClientListener.Disconnects.Count == 0, "se cortó al volver la red");
    }

    private static void TimesOutAfterLongSilence() {
        var pair = new Pair();
        pair.ConnectOrFail();
        pair.Network.LossRate = 1;
        Assert(pair.Pump(() => pair.ServerListener.Disconnects.Count == 1 && pair.ClientListener.Disconnects.Count == 1,
            NetConstants.TimeoutSeconds + 3), "no se cortó tras el tiempo de espera");
        Assert(pair.ServerListener.Disconnects[0] == DisconnectReason.Timeout, "motivo incorrecto en el servidor");
        Assert(pair.ClientListener.Disconnects[0] == DisconnectReason.Timeout, "motivo incorrecto en el cliente");
    }
}
