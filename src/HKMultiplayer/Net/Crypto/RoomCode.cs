using System.Text;
using Org.BouncyCastle.Security;

namespace HKMultiplayer.Net.Crypto;

/// <summary>
/// Código de la partida: lo genera el host y su compañero lo escribe para unirse. Sin él no se pueden derivar las
/// claves de la conexión (ver <see cref="KeyExchange"/>), así que nadie más puede entrar ni hacerse pasar por el host.
/// </summary>
public static class RoomCode {
    /// <summary>Sin letras ni números que se confundan (O/0, I/1).</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Caracteres del código (sin contar el guion).</summary>
    public const int Length = 8;

    /// <summary>Código nuevo al azar, con formato "ABCD-EFGH".</summary>
    public static string Generate() {
        var random = new SecureRandom();
        var builder = new StringBuilder(Length + 1);
        for (var i = 0; i < Length; i++) {
            if (i == Length / 2) {
                builder.Append('-');
            }

            builder.Append(Alphabet[random.Next(Alphabet.Length)]);
        }

        return builder.ToString();
    }

    /// <summary>Forma canónica de lo que escribió el jugador: mayúsculas y sin espacios ni guiones.</summary>
    public static string Normalize(string input) {
        if (string.IsNullOrEmpty(input)) {
            return string.Empty;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var c in input) {
            if (c == '-' || char.IsWhiteSpace(c)) {
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>Si el código tiene el formato correcto (longitud y caracteres).</summary>
    public static bool IsValid(string normalized) {
        if (normalized == null || normalized.Length != Length) {
            return false;
        }

        foreach (var c in normalized) {
            if (Alphabet.IndexOf(c) < 0) {
                return false;
            }
        }

        return true;
    }
}
