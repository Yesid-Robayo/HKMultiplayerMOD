using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HKMultiplayer.Net;

/// <summary>
/// Escribe valores directamente en un búfer (little-endian), sin crear arrays intermedios. Se reutiliza: <see cref="Reset"/>
/// lo apunta a otro búfer. Si se intenta escribir más de lo que cabe, lanza una excepción: es un error de programación.
/// </summary>
public sealed class PacketWriter {
    private byte[] _buffer;
    private int _start;
    private int _position;
    private int _limit;

    /// <summary>Búfer en el que se escribe.</summary>
    public byte[] Buffer => _buffer;

    /// <summary>Posición actual (índice absoluto en el búfer).</summary>
    public int Position => _position;

    /// <summary>Bytes escritos desde el inicio.</summary>
    public int Length => _position - _start;

    /// <summary>Bytes que aún caben.</summary>
    public int Remaining => _limit - _position;

    /// <summary>Apunta el escritor a <paramref name="buffer"/>, desde <paramref name="offset"/> y sin pasar de <paramref name="limit"/>.</summary>
    public void Reset(byte[] buffer, int offset, int limit) {
        _buffer = buffer;
        _start = offset;
        _position = offset;
        _limit = limit;
    }

    /// <summary>Mueve la posición (para rellenar después un hueco reservado, como una longitud).</summary>
    public void Seek(int position) {
        _position = position;
    }

    private void Ensure(int count) {
        if (_position + count > _limit) {
            throw new InvalidOperationException($"No caben {count} bytes más en el paquete ({Remaining} libres)");
        }
    }

    public void WriteByte(byte value) {
        Ensure(1);
        _buffer[_position++] = value;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte) 1 : (byte) 0);

    public void WriteUShort(ushort value) {
        Ensure(2);
        _buffer[_position++] = (byte) value;
        _buffer[_position++] = (byte) (value >> 8);
    }

    public void WriteShort(short value) => WriteUShort((ushort) value);

    public void WriteUInt(uint value) {
        Ensure(4);
        _buffer[_position++] = (byte) value;
        _buffer[_position++] = (byte) (value >> 8);
        _buffer[_position++] = (byte) (value >> 16);
        _buffer[_position++] = (byte) (value >> 24);
    }

    public void WriteInt(int value) => WriteUInt((uint) value);

    public void WriteULong(ulong value) {
        WriteUInt((uint) value);
        WriteUInt((uint) (value >> 32));
    }

    public void WriteFloat(float value) => WriteUInt(new FloatBits { Float = value }.Bits);

    /// <summary>Entero sin signo de longitud variable (1 byte hasta 127, 2 hasta 16383...).</summary>
    public void WriteVarUInt(uint value) {
        while (value >= 0x80) {
            WriteByte((byte) (value | 0x80));
            value >>= 7;
        }

        WriteByte((byte) value);
    }

    /// <summary>Cadena UTF-8 con su longitud delante, codificada directamente en el búfer.</summary>
    public void WriteString(string value) {
        value ??= string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt((uint) byteCount);
        Ensure(byteCount);
        Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _position);
        _position += byteCount;
    }

    public void WriteBytes(byte[] source, int offset, int count) {
        Ensure(count);
        System.Buffer.BlockCopy(source, offset, _buffer, _position, count);
        _position += count;
    }

    /// <summary>Para pasar entre float y sus bits sin crear arrays (net472 no tiene SingleToInt32Bits).</summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint Bits;
    }
}
