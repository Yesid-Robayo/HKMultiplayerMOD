using System;
using System.Collections.Generic;
using System.Net;

namespace HKMultiplayer.Net.Transport;

/// <summary>
/// Red en la que cada par se identifica con un número de 64 bits en vez de una dirección IP (Steam: su SteamID). Envía y
/// recibe datagramas sin garantía de entrega, como UDP: la fiabilidad y el cifrado los pone la capa de encima.
/// </summary>
public interface IPeerNetwork {
    /// <summary>Envía <paramref name="length"/> bytes de <paramref name="buffer"/> a <paramref name="peer"/>.</summary>
    void Send(ulong peer, byte[] buffer, int offset, int length);

    /// <summary>
    /// Copia en <paramref name="buffer"/> el siguiente datagrama recibido y devuelve su longitud, o -1 si no hay ninguno
    /// (o no cabía y se descartó). Se llama desde el hilo principal.
    /// </summary>
    int Receive(byte[] buffer, out ulong from);
}

/// <summary>
/// Transporte sobre una <see cref="IPeerNetwork"/>. El resto de la red identifica a cada par por un
/// <see cref="IPEndPoint"/>: a cada uno se le da una dirección IPv6 local inventada (prefijo fd48:4b4d:5053:544d, "HKMPSTM",
/// y un número de orden), siempre la misma mientras dure el transporte. El número de orden, y no el SteamID, va en la
/// dirección: así el registro, que escribe las direcciones, no lleva nada que identifique al jugador.
/// </summary>
public sealed class PeerTransport : ITransport {
    private static readonly byte[] Prefix = { 0xfd, 0x48, 0x4b, 0x4d, 0x50, 0x53, 0x54, 0x4d };

    private readonly IPeerNetwork _network;
    private readonly BufferPool _pool;
    private readonly Dictionary<ulong, IPEndPoint> _byPeer = new();
    private readonly Dictionary<IPEndPoint, ulong> _byEndPoint = new();

    /// <param name="network">La red de pares (no es de este transporte: no se cierra al cerrarlo).</param>
    /// <param name="pool">Reserva de búferes; compartida con otro transporte en un <see cref="CompositeTransport"/>.</param>
    public PeerTransport(IPeerNetwork network, BufferPool pool = null) {
        _network = network;
        _pool = pool ?? new BufferPool(NetConstants.MaxDatagramSize);
    }

    /// <summary>No escucha en ningún puerto.</summary>
    public int LocalPort => 0;

    /// <summary>La dirección con la que el resto de la red conoce a ese par (se le da una si aún no tenía).</summary>
    public IPEndPoint EndPointOf(ulong peer) {
        if (_byPeer.TryGetValue(peer, out var endPoint)) {
            return endPoint;
        }

        var bytes = new byte[16];
        Array.Copy(Prefix, bytes, Prefix.Length);
        var index = (ulong) _byPeer.Count + 1;
        for (var i = 0; i < 8; i++) {
            bytes[15 - i] = (byte) (index >> (i * 8));
        }

        endPoint = new IPEndPoint(new IPAddress(bytes), 1);
        _byPeer[peer] = endPoint;
        _byEndPoint[endPoint] = peer;
        return endPoint;
    }

    /// <summary>El par de esa dirección, si es una de las de este transporte.</summary>
    public bool TryGetPeer(IPEndPoint endPoint, out ulong peer) => _byEndPoint.TryGetValue(endPoint, out peer);

    /// <summary>Si esa dirección es de un par de este transporte.</summary>
    public bool Owns(IPEndPoint endPoint) => _byEndPoint.ContainsKey(endPoint);

    public bool TryReceive(out Datagram datagram) {
        var buffer = _pool.Rent();
        var length = _network.Receive(buffer, out var from);
        if (length < 0) {
            _pool.Return(buffer);
            datagram = default;
            return false;
        }

        datagram = new Datagram { Buffer = buffer, Length = length, From = EndPointOf(from) };
        return true;
    }

    public void Return(byte[] buffer) => _pool.Return(buffer);

    public void Send(byte[] buffer, int offset, int length, IPEndPoint to) {
        // Una dirección que no es de un par (no debería llegar aquí) se ignora, como un paquete perdido
        if (_byEndPoint.TryGetValue(to, out var peer)) {
            _network.Send(peer, buffer, offset, length);
        }
    }

    public void Dispose() {
        _byPeer.Clear();
        _byEndPoint.Clear();
    }
}

/// <summary>
/// Dos transportes a la vez: el host escucha por IP (UDP) y por Steam, y cada paquete sale por el del par al que va.
/// Los dos tienen que compartir la misma <see cref="BufferPool"/>: un búfer se devuelve al primero, venga de cuál venga.
/// </summary>
public sealed class CompositeTransport : ITransport {
    private readonly ITransport _primary;
    private readonly PeerTransport _peers;
    private bool _peersFirst;

    public CompositeTransport(ITransport primary, PeerTransport peers) {
        _primary = primary;
        _peers = peers;
    }

    /// <summary>El puerto del transporte por IP.</summary>
    public int LocalPort => _primary.LocalPort;

    public bool TryReceive(out Datagram datagram) {
        // Se alterna cuál se mira primero: con mucho tráfico por uno, el otro no se queda esperando
        _peersFirst = !_peersFirst;
        return _peersFirst
            ? _peers.TryReceive(out datagram) || _primary.TryReceive(out datagram)
            : _primary.TryReceive(out datagram) || _peers.TryReceive(out datagram);
    }

    public void Return(byte[] buffer) => _primary.Return(buffer);

    public void Send(byte[] buffer, int offset, int length, IPEndPoint to) {
        if (_peers.Owns(to)) {
            _peers.Send(buffer, offset, length, to);
        } else {
            _primary.Send(buffer, offset, length, to);
        }
    }

    public void Dispose() {
        _primary.Dispose();
        _peers.Dispose();
    }
}
