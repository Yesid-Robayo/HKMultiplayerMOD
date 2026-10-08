namespace HKMultiplayer.Net.Crypto;

/// <summary>
/// ChaCha20-Poly1305 (RFC 8439) sin crear memoria: todo el estado vive en arrays reservados al crear el objeto. Se
/// escribió porque la implementación de BouncyCastle crea varios objetos en cada paquete (unos 600 bytes), lo que con
/// 60 paquetes por segundo en cada sentido volvía a llenar la memoria. Se verifica contra el vector del RFC y contra
/// BouncyCastle en las pruebas (tests/NetTests). Un objeto por clave y dirección; no es seguro entre hilos.
/// </summary>
public sealed class ChaCha20Poly1305Aead {
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private readonly uint[] _key = new uint[8];
    private readonly uint[] _state = new uint[16];
    private readonly uint[] _working = new uint[16];
    private readonly byte[] _block = new byte[64];
    private readonly byte[] _polyKey = new byte[32];
    private readonly byte[] _lengths = new byte[16];
    private readonly byte[] _tag = new byte[16];
    private readonly Poly1305 _poly = new();

    public ChaCha20Poly1305Aead(byte[] key) {
        for (var i = 0; i < 8; i++) {
            _key[i] = ReadUInt(key, i * 4);
        }
    }

    /// <summary>
    /// Cifra <paramref name="length"/> bytes de <paramref name="input"/> en <paramref name="output"/> y escribe la etiqueta
    /// a continuación. Devuelve <paramref name="length"/> + 16. Entrada y salida pueden ser el mismo búfer.
    /// </summary>
    public int Seal(byte[] nonce, byte[] aad, int aadOffset, int aadLength,
        byte[] input, int inputOffset, int length, byte[] output, int outputOffset) {
        PolyKey(nonce);
        Xor(nonce, 1, input, inputOffset, length, output, outputOffset);
        ComputeTag(aad, aadOffset, aadLength, output, outputOffset, length);
        System.Buffer.BlockCopy(_tag, 0, output, outputOffset + length, TagSize);
        return length + TagSize;
    }

    /// <summary>
    /// Comprueba la etiqueta y descifra. Devuelve la longitud del texto en claro, o -1 si no es auténtico (en ese caso no
    /// se escribe nada en <paramref name="output"/>).
    /// </summary>
    public int Open(byte[] nonce, byte[] aad, int aadOffset, int aadLength,
        byte[] input, int inputOffset, int length, byte[] output, int outputOffset) {
        if (length < TagSize) {
            return -1;
        }

        var cipherLength = length - TagSize;
        PolyKey(nonce);
        ComputeTag(aad, aadOffset, aadLength, input, inputOffset, cipherLength);

        // Comparación en tiempo constante
        var diff = 0;
        for (var i = 0; i < TagSize; i++) {
            diff |= _tag[i] ^ input[inputOffset + cipherLength + i];
        }

        if (diff != 0) {
            return -1;
        }

        Xor(nonce, 1, input, inputOffset, cipherLength, output, outputOffset);
        return cipherLength;
    }

    /// <summary>Clave de Poly1305: los primeros 32 bytes del bloque ChaCha20 con contador 0.</summary>
    private void PolyKey(byte[] nonce) {
        Block(nonce, 0);
        System.Buffer.BlockCopy(_block, 0, _polyKey, 0, 32);
    }

    private void ComputeTag(byte[] aad, int aadOffset, int aadLength, byte[] cipher, int cipherOffset, int cipherLength) {
        _poly.Init(_polyKey);
        _poly.Update(aad, aadOffset, aadLength);
        _poly.Pad16(aadLength);
        _poly.Update(cipher, cipherOffset, cipherLength);
        _poly.Pad16(cipherLength);
        WriteULong(_lengths, 0, (ulong) aadLength);
        WriteULong(_lengths, 8, (ulong) cipherLength);
        _poly.Update(_lengths, 0, 16);
        _poly.Finish(_tag);
    }

    private void Xor(byte[] nonce, uint counter, byte[] input, int inputOffset, int length, byte[] output, int outputOffset) {
        for (var done = 0; done < length; done += 64, counter++) {
            Block(nonce, counter);
            var count = System.Math.Min(64, length - done);
            for (var i = 0; i < count; i++) {
                output[outputOffset + done + i] = (byte) (input[inputOffset + done + i] ^ _block[i]);
            }
        }
    }

    /// <summary>Bloque de 64 bytes de ChaCha20 (20 rondas) para el contador dado.</summary>
    private void Block(byte[] nonce, uint counter) {
        var s = _state;
        s[0] = 0x61707865;
        s[1] = 0x3320646e;
        s[2] = 0x79622d32;
        s[3] = 0x6b206574;
        for (var i = 0; i < 8; i++) {
            s[4 + i] = _key[i];
        }

        s[12] = counter;
        s[13] = ReadUInt(nonce, 0);
        s[14] = ReadUInt(nonce, 4);
        s[15] = ReadUInt(nonce, 8);

        var x = _working;
        System.Array.Copy(s, x, 16);
        for (var round = 0; round < 10; round++) {
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 1, 5, 9, 13);
            QuarterRound(x, 2, 6, 10, 14);
            QuarterRound(x, 3, 7, 11, 15);
            QuarterRound(x, 0, 5, 10, 15);
            QuarterRound(x, 1, 6, 11, 12);
            QuarterRound(x, 2, 7, 8, 13);
            QuarterRound(x, 3, 4, 9, 14);
        }

        for (var i = 0; i < 16; i++) {
            WriteUInt(_block, i * 4, x[i] + s[i]);
        }
    }

    private static void QuarterRound(uint[] x, int a, int b, int c, int d) {
        x[a] += x[b];
        x[d] = Rotl(x[d] ^ x[a], 16);
        x[c] += x[d];
        x[b] = Rotl(x[b] ^ x[c], 12);
        x[a] += x[b];
        x[d] = Rotl(x[d] ^ x[a], 8);
        x[c] += x[d];
        x[b] = Rotl(x[b] ^ x[c], 7);
    }

    private static uint Rotl(uint value, int bits) => value << bits | value >> (32 - bits);

    private static uint ReadUInt(byte[] b, int o) => (uint) (b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    private static void WriteUInt(byte[] b, int o, uint v) {
        b[o] = (byte) v;
        b[o + 1] = (byte) (v >> 8);
        b[o + 2] = (byte) (v >> 16);
        b[o + 3] = (byte) (v >> 24);
    }

    private static void WriteULong(byte[] b, int o, ulong v) {
        WriteUInt(b, o, (uint) v);
        WriteUInt(b, o + 4, (uint) (v >> 32));
    }

    /// <summary>
    /// Poly1305 con limbs de 26 bits (como poly1305-donna de 32 bits). Admite datos en trozos: los bloques incompletos se
    /// acumulan hasta tener 16 bytes.
    /// </summary>
    private sealed class Poly1305 {
        private uint _r0, _r1, _r2, _r3, _r4;
        private uint _s1, _s2, _s3, _s4;
        private uint _h0, _h1, _h2, _h3, _h4;
        private uint _pad0, _pad1, _pad2, _pad3;
        private readonly byte[] _buffer = new byte[16];
        private int _buffered;

        public void Init(byte[] key) {
            // r con los bits fijados a 0 que exige el algoritmo ("clamp")
            var t0 = ReadUInt(key, 0);
            var t1 = ReadUInt(key, 4);
            var t2 = ReadUInt(key, 8);
            var t3 = ReadUInt(key, 12);
            _r0 = t0 & 0x3ffffff;
            _r1 = (t0 >> 26 | t1 << 6) & 0x3ffff03;
            _r2 = (t1 >> 20 | t2 << 12) & 0x3ffc0ff;
            _r3 = (t2 >> 14 | t3 << 18) & 0x3f03fff;
            _r4 = t3 >> 8 & 0x00fffff;
            _s1 = _r1 * 5;
            _s2 = _r2 * 5;
            _s3 = _r3 * 5;
            _s4 = _r4 * 5;
            _h0 = _h1 = _h2 = _h3 = _h4 = 0;
            _pad0 = ReadUInt(key, 16);
            _pad1 = ReadUInt(key, 20);
            _pad2 = ReadUInt(key, 24);
            _pad3 = ReadUInt(key, 28);
            _buffered = 0;
        }

        public void Update(byte[] data, int offset, int length) {
            while (length > 0) {
                if (_buffered == 0 && length >= 16) {
                    ProcessBlock(data, offset, 1u << 24);
                    offset += 16;
                    length -= 16;
                    continue;
                }

                var take = System.Math.Min(16 - _buffered, length);
                System.Buffer.BlockCopy(data, offset, _buffer, _buffered, take);
                _buffered += take;
                offset += take;
                length -= take;
                if (_buffered == 16) {
                    ProcessBlock(_buffer, 0, 1u << 24);
                    _buffered = 0;
                }
            }
        }

        /// <summary>Relleno con ceros hasta múltiplo de 16 de lo escrito (<paramref name="length"/> bytes).</summary>
        public void Pad16(int length) {
            var remainder = length % 16;
            if (remainder == 0) {
                return;
            }

            for (var i = 0; i < 16 - remainder; i++) {
                _buffer[_buffered++] = 0;
            }

            if (_buffered == 16) {
                ProcessBlock(_buffer, 0, 1u << 24);
                _buffered = 0;
            }
        }

        private void ProcessBlock(byte[] m, int o, uint hibit) {
            var t0 = ReadUInt(m, o);
            var t1 = ReadUInt(m, o + 4);
            var t2 = ReadUInt(m, o + 8);
            var t3 = ReadUInt(m, o + 12);

            var h0 = _h0 + (t0 & 0x3ffffff);
            var h1 = _h1 + ((t0 >> 26 | t1 << 6) & 0x3ffffff);
            var h2 = _h2 + ((t1 >> 20 | t2 << 12) & 0x3ffffff);
            var h3 = _h3 + ((t2 >> 14 | t3 << 18) & 0x3ffffff);
            var h4 = _h4 + (t3 >> 8 | hibit);

            ulong d0 = (ulong) h0 * _r0 + (ulong) h1 * _s4 + (ulong) h2 * _s3 + (ulong) h3 * _s2 + (ulong) h4 * _s1;
            ulong d1 = (ulong) h0 * _r1 + (ulong) h1 * _r0 + (ulong) h2 * _s4 + (ulong) h3 * _s3 + (ulong) h4 * _s2;
            ulong d2 = (ulong) h0 * _r2 + (ulong) h1 * _r1 + (ulong) h2 * _r0 + (ulong) h3 * _s4 + (ulong) h4 * _s3;
            ulong d3 = (ulong) h0 * _r3 + (ulong) h1 * _r2 + (ulong) h2 * _r1 + (ulong) h3 * _r0 + (ulong) h4 * _s4;
            ulong d4 = (ulong) h0 * _r4 + (ulong) h1 * _r3 + (ulong) h2 * _r2 + (ulong) h3 * _r1 + (ulong) h4 * _r0;

            uint c;
            c = (uint) (d0 >> 26);
            h0 = (uint) d0 & 0x3ffffff;
            d1 += c;
            c = (uint) (d1 >> 26);
            h1 = (uint) d1 & 0x3ffffff;
            d2 += c;
            c = (uint) (d2 >> 26);
            h2 = (uint) d2 & 0x3ffffff;
            d3 += c;
            c = (uint) (d3 >> 26);
            h3 = (uint) d3 & 0x3ffffff;
            d4 += c;
            c = (uint) (d4 >> 26);
            h4 = (uint) d4 & 0x3ffffff;
            h0 += c * 5;
            c = h0 >> 26;
            h0 &= 0x3ffffff;
            h1 += c;

            _h0 = h0;
            _h1 = h1;
            _h2 = h2;
            _h3 = h3;
            _h4 = h4;
        }

        public void Finish(byte[] tag) {
            if (_buffered > 0) {
                // Último bloque incompleto: se marca con un 1 tras los datos y sin el bit alto
                _buffer[_buffered++] = 1;
                while (_buffered < 16) {
                    _buffer[_buffered++] = 0;
                }

                ProcessBlock(_buffer, 0, 0);
                _buffered = 0;
            }

            uint h0 = _h0, h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;
            uint c;
            c = h1 >> 26;
            h1 &= 0x3ffffff;
            h2 += c;
            c = h2 >> 26;
            h2 &= 0x3ffffff;
            h3 += c;
            c = h3 >> 26;
            h3 &= 0x3ffffff;
            h4 += c;
            c = h4 >> 26;
            h4 &= 0x3ffffff;
            h0 += c * 5;
            c = h0 >> 26;
            h0 &= 0x3ffffff;
            h1 += c;

            // h - p, y se elige el resultado sin ramas
            var g0 = h0 + 5;
            c = g0 >> 26;
            g0 &= 0x3ffffff;
            var g1 = h1 + c;
            c = g1 >> 26;
            g1 &= 0x3ffffff;
            var g2 = h2 + c;
            c = g2 >> 26;
            g2 &= 0x3ffffff;
            var g3 = h3 + c;
            c = g3 >> 26;
            g3 &= 0x3ffffff;
            var g4 = h4 + c - (1u << 26);

            var mask = (g4 >> 31) - 1;
            g0 &= mask;
            g1 &= mask;
            g2 &= mask;
            g3 &= mask;
            g4 &= mask;
            mask = ~mask;
            h0 = (h0 & mask) | g0;
            h1 = (h1 & mask) | g1;
            h2 = (h2 & mask) | g2;
            h3 = (h3 & mask) | g3;
            h4 = (h4 & mask) | g4;

            // h % 2^128 en 4 palabras, + pad
            h0 = (h0 | h1 << 26) & 0xffffffff;
            h1 = (h1 >> 6 | h2 << 20) & 0xffffffff;
            h2 = (h2 >> 12 | h3 << 14) & 0xffffffff;
            h3 = (h3 >> 18 | h4 << 8) & 0xffffffff;

            ulong f = (ulong) h0 + _pad0;
            h0 = (uint) f;
            f = (ulong) h1 + _pad1 + (f >> 32);
            h1 = (uint) f;
            f = (ulong) h2 + _pad2 + (f >> 32);
            h2 = (uint) f;
            f = (ulong) h3 + _pad3 + (f >> 32);
            h3 = (uint) f;

            WriteUInt(tag, 0, h0);
            WriteUInt(tag, 4, h1);
            WriteUInt(tag, 8, h2);
            WriteUInt(tag, 12, h3);
        }
    }
}
