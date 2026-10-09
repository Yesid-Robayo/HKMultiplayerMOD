using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using HKMultiplayer.Net.Transport;

namespace HKMultiplayer.Net;

/// <summary>Quien recibe los eventos de la red (las capas superiores).</summary>
public interface INetListener {
    /// <summary>Conexión aceptada y lista para enviar y recibir.</summary>
    void OnConnected(NetConnection connection);

    /// <summary>Conexión cerrada (por cualquiera de los dos lados, tiempo de espera o rechazo).</summary>
    void OnDisconnected(NetConnection connection, DisconnectReason reason);

    /// <summary>
    /// Mensaje recibido. El lector apunta al primer byte (el tipo) y solo es válido durante la llamada.
    /// </summary>
    void OnMessage(NetConnection connection, PacketReader reader);

    /// <summary>Transferencia grande recibida entera.</summary>
    void OnLargeMessage(NetConnection connection, byte kind, byte[] data);

    /// <summary>Registro (avisos y errores del transporte).</summary>
    void Log(string message);
}

/// <summary>
/// Base común del servidor y el cliente: recibe del transporte, despacha por dirección, envía el tick y vigila los
/// tiempos de espera. Todo se ejecuta en el hilo que llama a <see cref="Update"/> (el principal del juego).
/// </summary>
public abstract class NetPeer : IDisposable {
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    protected readonly INetListener Listener;
    protected readonly Dictionary<IPEndPoint, NetConnection> Connections = new();
    protected readonly BufferPool MessagePool = new(NetConstants.MaxMessageSize);

    // Búferes de trabajo reutilizados
    private readonly byte[] _plain = new byte[NetConstants.MaxDatagramSize];
    private readonly byte[] _datagram = new byte[NetConstants.MaxDatagramSize];
    private readonly PacketWriter _packetWriter = new();
    private readonly PacketReader _packetReader = new();
    private readonly PacketReader _appReader = new();
    private readonly List<NetConnection> _iteration = new();

    internal readonly PacketReader ScratchReader = new();

    private double _nextTick;

    internal ITransport Transport { get; }

    /// <summary>Segundos desde que arrancó el reloj de la red.</summary>
    public static double Now => Clock.Elapsed.TotalSeconds;

    protected NetPeer(ITransport transport, INetListener listener) {
        Transport = transport;
        Listener = listener;
    }

    /// <summary>
    /// Procesa lo recibido, vigila tiempos de espera y, si toca, envía el tick. Llamar una vez por frame.
    /// </summary>
    public void Update() {
        var now = Now;
        while (Transport.TryReceive(out var datagram)) {
            try {
                HandleDatagram(datagram.Buffer, datagram.Length, datagram.From, now);
            } catch (PacketException e) {
                LogWarning($"Datagrama descartado de {datagram.From}: {e.Message}");
            } finally {
                Transport.Return(datagram.Buffer);
            }
        }

        UpdateHandshakes(now);

        if (now < _nextTick) {
            return;
        }

        // Ritmo fijo; si el juego va más lento que el tick, un envío por frame
        _nextTick = System.Math.Max(_nextTick + 1.0 / NetConstants.TickRate, now - 0.5);

        _iteration.Clear();
        _iteration.AddRange(Connections.Values);
        foreach (var connection in _iteration) {
            if (connection.State == ConnectionState.Disconnected) {
                continue;
            }

            if (now - connection.LastReceive > NetConstants.TimeoutSeconds) {
                CloseConnection(connection, DisconnectReason.Timeout, false);
                continue;
            }

            connection.SendTick(now, _plain, _datagram, _packetWriter);
        }
    }

    private void HandleDatagram(byte[] buffer, int length, IPEndPoint from, double now) {
        if (length < 5) {
            return;
        }

        var magic = (uint) (buffer[0] | buffer[1] << 8 | buffer[2] << 16 | buffer[3] << 24);
        if (magic != NetConstants.Magic) {
            return;
        }

        var type = (DatagramType) buffer[4];
        if (type == DatagramType.Data) {
            if (Connections.TryGetValue(from, out var connection) && connection.State != ConnectionState.Disconnected) {
                if (!connection.ReceiveData(buffer, length, _plain, _packetReader, now)) {
                    OnUndecryptable(connection);
                }
            }

            return;
        }

        HandleHandshake(type, buffer, length, from, now);
    }

    /// <summary>Saludo (Hello / Challenge) según el lado.</summary>
    protected abstract void HandleHandshake(DatagramType type, byte[] buffer, int length, IPEndPoint from, double now);

    /// <summary>Reintentos del saludo (cliente) o limpieza de saludos abandonados (servidor).</summary>
    protected abstract void UpdateHandshakes(double now);

    /// <summary>Un paquete de datos no se pudo descifrar.</summary>
    protected virtual void OnUndecryptable(NetConnection connection) {
    }

    /// <summary>Mensajes de control del transporte; el resto va a las capas superiores.</summary>
    protected abstract bool HandleControl(NetConnection connection, byte type, PacketReader reader);

    internal void DeliverMessage(NetConnection connection, byte[] buffer, int offset, int length) {
        _appReader.Reset(buffer, offset, offset + length);
        var type = buffer[offset];
        if (type < ControlMessage.FirstUserType) {
            _appReader.Skip(1);
            if (type == ControlMessage.Disconnect) {
                var reason = _appReader.Remaining > 0 ? (DisconnectReason) _appReader.ReadByte() : DisconnectReason.Closed;
                CloseConnection(connection, reason, false);
                return;
            }

            HandleControl(connection, type, _appReader);
            return;
        }

        if (connection.State != ConnectionState.Connected) {
            return;
        }

        try {
            Listener.OnMessage(connection, _appReader);
        } catch (PacketException e) {
            LogWarning($"Mensaje {type} mal formado de {connection.EndPoint}: {e.Message}");
        }
    }

    internal void DeliverLarge(NetConnection connection, byte kind, byte[] data) {
        if (connection.State == ConnectionState.Connected) {
            Listener.OnLargeMessage(connection, kind, data);
        }
    }

    /// <summary>
    /// Cierra una conexión. Si <paramref name="notify"/>, se avisa al otro extremo (varias veces, por si se pierde).
    /// </summary>
    internal void CloseConnection(NetConnection connection, DisconnectReason reason, bool notify) {
        if (connection.State == ConnectionState.Disconnected) {
            return;
        }

        if (notify) {
            // Un mensaje que quedó a medio escribir (su escritor falló) ya no se enviará: no debe impedir el aviso
            connection.CancelMessage();
            for (var i = 0; i < 3; i++) {
                var writer = connection.BeginUnreliable();
                writer.WriteByte(ControlMessage.Disconnect);
                writer.WriteByte((byte) reason);
                connection.EndMessage();
                connection.SendTick(Now, _plain, _datagram, _packetWriter);
            }
        }

        var wasConnected = connection.State == ConnectionState.Connected;
        connection.State = ConnectionState.Disconnected;
        connection.DisconnectReason = reason;
        connection.Release();
        Connections.Remove(connection.EndPoint);
        OnConnectionClosed(connection, reason, wasConnected);
    }

    /// <summary>Tras cerrar una conexión (el cliente avisa siempre; el servidor solo si estaba aceptada).</summary>
    protected abstract void OnConnectionClosed(NetConnection connection, DisconnectReason reason, bool wasConnected);

    /// <summary>Envía un datagrama de saludo tal cual (sin cifrar).</summary>
    protected void SendRaw(byte[] buffer, int length, IPEndPoint to) {
        Transport.Send(buffer, 0, length, to);
    }

    internal void LogWarning(string message) => Listener.Log(message);

    public virtual void Dispose() {
        _iteration.Clear();
        _iteration.AddRange(Connections.Values);
        foreach (var connection in _iteration) {
            CloseConnection(connection, DisconnectReason.Closed, true);
        }

        Transport.Dispose();
    }

    /// <summary>Escribe la firma y el tipo de datagrama.</summary>
    protected static void WriteMagic(PacketWriter writer, DatagramType type) {
        writer.WriteUInt(NetConstants.Magic);
        writer.WriteByte((byte) type);
    }
}
