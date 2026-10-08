using System;
using System.Text;

namespace HKMultiplayer.Net;

/// <summary>
/// Lee valores de un búfer (little-endian). Los datos que llegan de la red no son de fiar: cualquier lectura fuera de
/// los límites lanza <see cref="PacketException"/> y el paquete se descarta.
/// </summary>
public sealed class PacketReader {
    private byte[] _buffer;
    private int _position;
    private int _limit;

    public byte[] Buffer => _buffer;

    /// <summary>Posición actual (índice absoluto en el búfer).</summary>
    public int Position => _position;

    /// <summary>Bytes que quedan por leer.</summary>
    public int Remaining => _limit - _position;

    public void Reset(byte[] buffer, int offset, int limit) {
        _buffer = buffer;
        _position = offset;
        _limit = limit;
    }

    public void Skip(int count) {
        Ensure(count);
        _position += count;
    }

    private void Ensure(int count) {
        if (count < 0 || _position + count > _limit) {
            throw new PacketException("Paquete más corto de lo que indica su contenido");
        }
    }

    public byte ReadByte() {
        Ensure(1);
        return _buffer[_position++];
    }

    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUShort() {
        Ensure(2);
        var value = (ushort) (_buffer[_position] | _buffer[_position + 1] << 8);
        _position += 2;
        return value;
    }

    public short ReadShort() => (short) ReadUShort();

    public uint ReadUInt() {
        Ensure(4);
        var value = (uint) (_buffer[_position] | _buffer[_position + 1] << 8 | _buffer[_position + 2] << 16 |
                            _buffer[_position + 3] << 24);
        _position += 4;
        return value;
    }

    public int ReadInt() => (int) ReadUInt();

    public ulong ReadULong() {
        ulong low = ReadUInt();
        ulong high = ReadUInt();
        return low | high << 32;
    }

    public float ReadFloat() => new PacketWriter.FloatBits { Bits = ReadUInt() }.Float;

    public uint ReadVarUInt() {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7) {
            var b = ReadByte();
            value |= (uint) (b & 0x7F) << shift;
            if ((b & 0x80) == 0) {
                return value;
            }
        }

        throw new PacketException("Entero variable demasiado largo");
    }

    /// <summary>Lee una cadena UTF-8 de como mucho <paramref name="maxBytes"/> bytes.</summary>
    public string ReadString(int maxBytes = 256) {
        var length = (int) ReadVarUInt();
        if (length > maxBytes) {
            throw new PacketException($"Cadena demasiado larga ({length} bytes)");
        }

        Ensure(length);
        var value = length == 0 ? string.Empty : Encoding.UTF8.GetString(_buffer, _position, length);
        _position += length;
        return value;
    }

    public void ReadBytes(byte[] destination, int offset, int count) {
        Ensure(count);
        System.Buffer.BlockCopy(_buffer, _position, destination, offset, count);
        _position += count;
    }
}

/// <summary>Datos recibidos mal formados.</summary>
public sealed class PacketException : Exception {
    public PacketException(string message) : base(message) {
    }
}
