namespace HKMultiplayer.Net.Crypto;

/// <summary>
/// Cifrado autenticado de los paquetes de una dirección con ChaCha20-Poly1305 (<see cref="ChaCha20Poly1305Aead"/>, sin
/// crear memoria por paquete). El nonce es el número de secuencia del paquete: nunca se repite en una dirección y cada
/// dirección tiene su clave. La cabecera en claro va como datos asociados: si alguien la cambia, el paquete no se acepta.
/// </summary>
public sealed class PacketCipher {
    private readonly ChaCha20Poly1305Aead _aead;
    private readonly byte[] _nonce = new byte[ChaCha20Poly1305Aead.NonceSize];

    public PacketCipher(byte[] key) {
        _aead = new ChaCha20Poly1305Aead(key);
    }

    private byte[] Nonce(ulong sequence) {
        for (var i = 0; i < 8; i++) {
            _nonce[4 + i] = (byte) (sequence >> (8 * i));
        }

        return _nonce;
    }

    /// <summary>
    /// Cifra <paramref name="length"/> bytes de <paramref name="input"/> en <paramref name="output"/> y añade la etiqueta.
    /// </summary>
    /// <returns>Bytes escritos (longitud + <see cref="NetConstants.TagSize"/>).</returns>
    public int Encrypt(ulong sequence, byte[] header, int headerOffset, int headerLength,
        byte[] input, int inputOffset, int length, byte[] output, int outputOffset) {
        return _aead.Seal(Nonce(sequence), header, headerOffset, headerLength, input, inputOffset, length, output,
            outputOffset);
    }

    /// <summary>Descifra y comprueba la etiqueta. Devuelve la longitud del texto en claro, o -1 si no es auténtico.</summary>
    public int Decrypt(ulong sequence, byte[] header, int headerOffset, int headerLength,
        byte[] input, int inputOffset, int length, byte[] output, int outputOffset) {
        return _aead.Open(Nonce(sequence), header, headerOffset, headerLength, input, inputOffset, length, output,
            outputOffset);
    }
}
