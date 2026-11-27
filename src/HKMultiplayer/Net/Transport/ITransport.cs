using System;
using System.Net;

namespace HKMultiplayer.Net.Transport;

/// <summary>Datagrama recibido. El búfer es de la reserva del transporte: hay que devolverlo con <see cref="ITransport.Return"/>.</summary>
public struct Datagram {
    public byte[] Buffer;
    public int Length;
    public IPEndPoint From;
}

/// <summary>
/// Envío y recepción de datagramas. Hay una implementación UDP para el juego y otra simulada (con pérdidas, retraso y
/// desorden) para las pruebas.
/// </summary>
public interface ITransport : IDisposable {
    /// <summary>Puerto local en el que escucha.</summary>
    int LocalPort { get; }

    /// <summary>Saca el siguiente datagrama recibido, si hay. Se llama desde el hilo de red (o el principal en modo síncrono).</summary>
    bool TryReceive(out Datagram datagram);

    /// <summary>Devuelve a la reserva el búfer de un datagrama ya procesado.</summary>
    void Return(byte[] buffer);

    /// <summary>Envía <paramref name="length"/> bytes de <paramref name="buffer"/> a <paramref name="to"/>.</summary>
    void Send(byte[] buffer, int offset, int length, IPEndPoint to);
}

/// <summary>
/// Transporte capaz de despertar al hilo dedicado de red en cuanto entra un datagrama, sin esperar al siguiente tick.
/// </summary>
public interface IWakeableTransport {
    /// <summary>Si parte de este transporte necesita sondeo periódico (p. ej. SteamNetworkingMessages).</summary>
    bool NeedsPolling { get; }

    /// <summary>Registra la acción que despierta al hilo de red cuando entra un datagrama (null para quitarla).</summary>
    void SetWakeCallback(Action wake);
}
