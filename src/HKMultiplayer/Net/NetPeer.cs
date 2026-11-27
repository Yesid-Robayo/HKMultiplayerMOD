using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
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
/// tiempos de espera.
/// <para>
/// Puede funcionar de dos formas: en modo síncrono (todo en <see cref="Update"/>, útil para pruebas deterministas) o
/// con un hilo dedicado (<see cref="StartNetworkThread"/>), donde cifrado, acuses, reenvíos, keepalives y envío corren
/// fuera del hilo principal del juego y <see cref="Update"/> solo entrega los eventos acumulados.
/// </para>
/// </summary>
public abstract class NetPeer : IDisposable {
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    protected readonly INetListener Listener;
    protected readonly Dictionary<IPEndPoint, NetConnection> Connections = new();
    protected readonly BufferPool MessagePool = new(NetConstants.MaxMessageSize);

    // Búferes de trabajo reutilizados (exclusivos del hilo de red, o del principal en modo síncrono)
    private readonly byte[] _plain = new byte[NetConstants.MaxDatagramSize];
    private readonly byte[] _datagram = new byte[NetConstants.MaxDatagramSize];
    private readonly PacketWriter _packetWriter = new();
    private readonly PacketReader _packetReader = new();
    private readonly PacketReader _controlReader = new();
    private readonly List<NetConnection> _iteration = new();
    private readonly byte[] _disconnectPayload = new byte[2];

    // Lector exclusivo del hilo principal para entregar mensajes a INetListener
    private readonly PacketReader _appReader = new();

    internal readonly PacketReader ScratchReader = new();

    // Hilo dedicado de red y cola de eventos hacia el hilo principal (intercambio de doble cola bajo bloqueo corto)
    private readonly object _inboundLock = new();
    private Queue<NetEvent> _inbound = new();
    private Queue<NetEvent> _inboundDrain = new();
    private Thread _thread;
    private AutoResetEvent _wake;
    private bool _needsPolling;
    private bool _stopping;
    private bool _disposed;

    private double _nextTick;
    private int _handlerErrors;

    internal ITransport Transport { get; }

    /// <summary>Si el procesamiento de red corre en un hilo dedicado.</summary>
    public bool IsThreaded => Volatile.Read(ref _thread) != null;

    /// <summary>Segundos desde que arrancó el reloj de la red.</summary>
    public static double Now => Clock.Elapsed.TotalSeconds;

    protected NetPeer(ITransport transport, INetListener listener) {
        Transport = transport;
        Listener = listener;
    }

    /// <summary>
    /// Arranca el hilo dedicado de red (<c>HKMultiplayer Net</c>). A partir de aquí el transporte, el cifrado, los
    /// acuses, los reenvíos y los keepalives siguen vivos aunque el hilo principal de Unity se pare cargando una sala.
    /// </summary>
    public void StartNetworkThread() {
        if (_disposed || Volatile.Read(ref _thread) != null) {
            return;
        }

        _wake = new AutoResetEvent(false);
        if (Transport is IWakeableTransport wakeable) {
            _needsPolling = wakeable.NeedsPolling;
            wakeable.SetWakeCallback(Wake);
        } else {
            _needsPolling = true;
        }

        var thread = new Thread(NetworkLoop) {
            IsBackground = true,
            Name = "HKMultiplayer Net"
        };
        Volatile.Write(ref _thread, thread);
        thread.Start();
    }

    /// <summary>Despierta al hilo dedicado de red para procesar envíos o llegadas sin esperar al temporizador.</summary>
    internal void Wake() {
        if (Volatile.Read(ref _stopping)) {
            return;
        }

        try {
            _wake?.Set();
        } catch (ObjectDisposedException) {
            // El hilo se está cerrando
        }
    }

    /// <summary>
    /// En modo síncrono, procesa la red y el tick. En modo hilo dedicado, solo entrega en el hilo principal los eventos
    /// y mensajes ya descifrados por el hilo de red. Llamar una vez por frame.
    /// </summary>
    public void Update() {
        if (IsThreaded) {
            DrainEvents();
            return;
        }

        Step(Now);
    }

    private void NetworkLoop() {
        try {
            while (!Volatile.Read(ref _stopping)) {
                Step(Now);
                if (Volatile.Read(ref _stopping)) {
                    break;
                }

                var remainingMs = (_nextTick - Now) * 1000.0;
                if (remainingMs <= 0) {
                    // Ya toca el siguiente tick: no dormimos, pero limpiamos una señal vieja si la había
                    continue;
                }

                var maxWait = _needsPolling ? 4 : 16;
                var waitMs = (int) Math.Min(maxWait, Math.Ceiling(remainingMs));
                if (waitMs < 1) {
                    waitMs = 1;
                }

                _wake.WaitOne(waitMs);
            }
        } catch (Exception e) {
            LogWarning($"Fallo inesperado en el hilo de red: {e}");
        } finally {
            CloseAllConnections();
        }
    }

    private void Step(double now) {
        ProcessPendingCommands(now);
        DrainOutboundAll();

        while (Transport.TryReceive(out var datagram)) {
            try {
                HandleDatagram(datagram.Buffer, datagram.Length, datagram.From, now);
            } catch (PacketException e) {
                LogWarning($"Datagrama descartado de {datagram.From}: {e.Message}");
            } catch (Exception e) {
                // Uno que falla no corta los demás datagramas ni el tick de este frame
                LogWarning($"Fallo con un datagrama de {datagram.From}: {e}");
            } finally {
                Transport.Return(datagram.Buffer);
            }
        }

        UpdateHandshakes(now);

        if (now < _nextTick) {
            return;
        }

        // Ritmo fijo de TickRate Hz; en el primer tick o tras una pausa larga se reancla para no lanzar una ráfaga
        var step = 1.0 / NetConstants.TickRate;
        _nextTick = _nextTick == 0 || now - _nextTick > 0.25 ? now + step : _nextTick + step;

        _iteration.Clear();
        _iteration.AddRange(Connections.Values);
        foreach (var connection in _iteration) {
            if (connection.State == ConnectionState.Disconnected) {
                continue;
            }

            try {
                if (IsThreaded && !connection.DrainOutbound()) {
                    continue;
                }

                if (now - connection.LastReceive > NetConstants.TimeoutSeconds) {
                    LogWarning($"Se corta por tiempo la conexión con {Describe(connection)}: {connection.DescribeStats(now)}");
                    CloseConnection(connection, DisconnectReason.Timeout, false);
                    continue;
                }

                // Un silencio largo queda en el registro aunque luego no se corte: dice cuándo y con qué estado de red
                if (!connection.SilenceReported && connection.State == ConnectionState.Connected &&
                    now - connection.LastReceive > SilenceWarningSeconds) {
                    connection.SilenceReported = true;
                    LogWarning($"No llega nada de {Describe(connection)}: {connection.DescribeStats(now)}");
                }

                if (CanSend(connection)) {
                    connection.SendTick(now, _plain, _datagram, _packetWriter);
                }
            } catch (Exception e) {
                // Un fallo con una conexión (o en quien escucha su desconexión) no deja sin tick a las demás
                LogWarning($"Fallo en el tick de {connection.EndPoint}: {e}");
            }
        }
    }

    private void DrainOutboundAll() {
        if (!IsThreaded || Connections.Count == 0) {
            return;
        }

        _iteration.Clear();
        _iteration.AddRange(Connections.Values);
        foreach (var connection in _iteration) {
            if (connection.State != ConnectionState.Disconnected) {
                connection.DrainOutbound();
            }
        }
    }

    /// <summary>Procesa órdenes enviadas desde el hilo principal hacia el hilo de red (p. ej. conectar en el cliente).</summary>
    protected virtual void ProcessPendingCommands(double now) {
    }

    /// <summary>Drena en el hilo principal todos los eventos encolados por el hilo de red.</summary>
    private void DrainEvents() {
        Queue<NetEvent> batch;
        lock (_inboundLock) {
            if (_inbound.Count == 0) {
                return;
            }

            batch = _inbound;
            _inbound = _inboundDrain;
            _inboundDrain = batch;
        }

        while (batch.Count > 0) {
            var ev = batch.Dequeue();
            switch (ev.Kind) {
                case NetEventKind.Connected:
                    DispatchConnected(ev.Connection);
                    break;
                case NetEventKind.Disconnected:
                    DispatchDisconnected(ev.Connection, ev.Reason);
                    break;
                case NetEventKind.Message:
                    try {
                        DispatchUserMessage(ev.Connection, ev.Buffer, 0, ev.Length);
                    } finally {
                        MessagePool.Return(ev.Buffer);
                    }

                    break;
                case NetEventKind.LargeMessage:
                    DispatchLarge(ev.Connection, ev.LargeKind, ev.Buffer);
                    break;
                case NetEventKind.Log:
                    try {
                        Listener.Log(ev.Text);
                    } catch {
                        // Nunca tumba el drenado
                    }

                    break;
            }
        }
    }

    private void EnqueueEvent(NetEvent ev) {
        lock (_inboundLock) {
            _inbound.Enqueue(ev);
        }
    }

    /// <summary>Avisa a quien escucha de una conexión nueva; un fallo suyo no sale del transporte.</summary>
    protected void NotifyConnected(NetConnection connection) {
        if (IsThreaded) {
            EnqueueEvent(new NetEvent { Kind = NetEventKind.Connected, Connection = connection });
            return;
        }

        DispatchConnected(connection);
    }

    private void DispatchConnected(NetConnection connection) {
        try {
            Listener.OnConnected(connection);
        } catch (Exception e) {
            LogWarning($"Fallo al avisar de la conexión de {connection.EndPoint}: {e}");
        }
    }

    /// <summary>Avisa a quien escucha de una desconexión; un fallo suyo no sale del transporte.</summary>
    protected void NotifyDisconnected(NetConnection connection, DisconnectReason reason) {
        if (IsThreaded) {
            EnqueueEvent(new NetEvent { Kind = NetEventKind.Disconnected, Connection = connection, Reason = reason });
            return;
        }

        DispatchDisconnected(connection, reason);
    }

    private void DispatchDisconnected(NetConnection connection, DisconnectReason reason) {
        try {
            Listener.OnDisconnected(connection, reason);
        } catch (Exception e) {
            LogWarning($"Fallo al avisar de la desconexión de {connection?.EndPoint}: {e}");
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

    /// <summary>Si a esta conexión se le envían paquetes en cada tick.</summary>
    protected virtual bool CanSend(NetConnection connection) => true;

    /// <summary>Un paquete de datos no se pudo descifrar.</summary>
    protected virtual void OnUndecryptable(NetConnection connection) {
    }

    /// <summary>Mensajes de control del transporte; el resto va a las capas superiores.</summary>
    protected abstract bool HandleControl(NetConnection connection, byte type, PacketReader reader);

    /// <summary>Procesa un mensaje de control del transporte directamente en el hilo de red.</summary>
    internal void DeliverControl(NetConnection connection, byte[] buffer, int offset, int length) {
        _controlReader.Reset(buffer, offset, offset + length);
        var type = buffer[offset];
        _controlReader.Skip(1);
        if (type == ControlMessage.Disconnect) {
            var reason = _controlReader.Remaining > 0 ? (DisconnectReason) _controlReader.ReadByte() : DisconnectReason.Closed;
            CloseConnection(connection, reason, false);
            return;
        }

        HandleControl(connection, type, _controlReader);
    }

    /// <summary>Encola un mensaje de usuario (con búfer prestado de <see cref="MessagePool"/>) hacia el hilo principal.</summary>
    internal void EnqueueUserMessage(NetConnection connection, byte[] pooledBuffer, int length) {
        if (connection.State != ConnectionState.Connected) {
            MessagePool.Return(pooledBuffer);
            return;
        }

        EnqueueEvent(new NetEvent {
            Kind = NetEventKind.Message,
            Connection = connection,
            Buffer = pooledBuffer,
            Length = length
        });
    }

    /// <summary>Entrega un mensaje de usuario en modo síncrono.</summary>
    internal void DeliverUserMessage(NetConnection connection, byte[] buffer, int offset, int length) {
        if (connection.State != ConnectionState.Connected) {
            return;
        }

        DispatchUserMessage(connection, buffer, offset, length);
    }

    private void DispatchUserMessage(NetConnection connection, byte[] buffer, int offset, int length) {
        _appReader.Reset(buffer, offset, offset + length);
        var type = buffer[offset];
        try {
            Listener.OnMessage(connection, _appReader);
        } catch (PacketException e) {
            LogWarning($"Mensaje {type} mal formado de {connection.EndPoint}: {e.Message}");
        } catch (Exception e) {
            // Un fallo de quien procesa el mensaje no puede salir de aquí: cortaría el datagrama a medias y, en el canal
            // fiable, el mensaje quedaría sin entregar con su paquete ya confirmado (nunca se reenvía) y todo lo
            // siguiente de esa conexión esperaría detrás para siempre
            LogHandlerError($"el mensaje {type} de {connection.EndPoint}", e);
        }
    }

    internal void DeliverLarge(NetConnection connection, byte kind, byte[] data) {
        if (connection.State != ConnectionState.Connected) {
            return;
        }

        if (IsThreaded) {
            EnqueueEvent(new NetEvent {
                Kind = NetEventKind.LargeMessage,
                Connection = connection,
                LargeKind = kind,
                Buffer = data
            });
            return;
        }

        DispatchLarge(connection, kind, data);
    }

    private void DispatchLarge(NetConnection connection, byte kind, byte[] data) {
        try {
            Listener.OnLargeMessage(connection, kind, data);
        } catch (Exception e) {
            LogHandlerError($"la transferencia {kind} de {connection.EndPoint}", e);
        }
    }

    /// <summary>Registra un fallo al procesar algo recibido; si se repite mucho, solo de vez en cuando.</summary>
    private void LogHandlerError(string what, Exception e) {
        _handlerErrors++;
        if (_handlerErrors <= 20 || _handlerErrors % 500 == 0) {
            LogWarning($"Error al procesar {what} (van {_handlerErrors}): {e}");
        }
    }

    /// <summary>
    /// Cierra una conexión (siempre llamado desde el hilo de red, o desde el principal en modo síncrono). Si
    /// <paramref name="notify"/>, se avisa al otro extremo (varias veces, por si se pierde).
    /// </summary>
    internal void CloseConnection(NetConnection connection, DisconnectReason reason, bool notify) {
        if (connection.State == ConnectionState.Disconnected) {
            return;
        }

        if (notify) {
            _disconnectPayload[0] = ControlMessage.Disconnect;
            _disconnectPayload[1] = (byte) reason;
            for (var i = 0; i < 3; i++) {
                connection.EnqueueControlUnreliable(_disconnectPayload, 2);
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

    private void CloseAllConnections() {
        _iteration.Clear();
        _iteration.AddRange(Connections.Values);
        foreach (var connection in _iteration) {
            CloseConnection(connection, DisconnectReason.Closed, true);
        }
    }

    /// <summary>Tras cerrar una conexión (el cliente avisa siempre; el servidor solo si estaba aceptada).</summary>
    protected abstract void OnConnectionClosed(NetConnection connection, DisconnectReason reason, bool wasConnected);

    /// <summary>Envía un datagrama de saludo tal cual (sin cifrar).</summary>
    protected void SendRaw(byte[] buffer, int length, IPEndPoint to) {
        Transport.Send(buffer, 0, length, to);
    }

    internal void LogWarning(string message) {
        var thread = Volatile.Read(ref _thread);
        if (thread != null && Thread.CurrentThread == thread) {
            EnqueueEvent(new NetEvent { Kind = NetEventKind.Log, Text = message });
            return;
        }

        Listener.Log(message);
    }

    /// <summary>Segundos sin recibir nada a partir de los que se avisa en el registro (el corte llega con <see cref="NetConstants.TimeoutSeconds"/>).</summary>
    private const double SilenceWarningSeconds = 3;

    /// <summary>Quién es el otro extremo, para el registro, sin su dirección.</summary>
    internal string Describe(NetConnection connection) =>
        this is NetServer ? $"el jugador {connection.Id}" : "el host";

    public virtual void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        var thread = Volatile.Read(ref _thread);
        if (thread != null) {
            if (Transport is IWakeableTransport wakeable) {
                wakeable.SetWakeCallback(null);
            }

            Volatile.Write(ref _stopping, true);
            try {
                _wake?.Set();
            } catch (ObjectDisposedException) {
            }

            if (Thread.CurrentThread != thread) {
                thread.Join(2000);
            }

            Volatile.Write(ref _thread, null);
            // Igual que en modo síncrono: quien escucha se entera de los cierres hechos al parar y de los últimos avisos
            // del registro (antes de esto se descartaban y la sesión no veía su propia desconexión)
            DrainEvents();
            _wake?.Dispose();
            _wake = null;
            Transport.Dispose();
            return;
        }

        CloseAllConnections();
        Transport.Dispose();
    }

    /// <summary>Escribe la firma y el tipo de datagrama.</summary>
    protected static void WriteMagic(PacketWriter writer, DatagramType type) {
        writer.WriteUInt(NetConstants.Magic);
        writer.WriteByte((byte) type);
    }

    private enum NetEventKind : byte {
        Connected,
        Disconnected,
        Message,
        LargeMessage,
        Log
    }

    private struct NetEvent {
        public NetEventKind Kind;
        public NetConnection Connection;
        public DisconnectReason Reason;
        public byte LargeKind;
        public byte[] Buffer;
        public int Length;
        public string Text;
    }
}
