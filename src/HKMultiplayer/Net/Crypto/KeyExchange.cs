using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace HKMultiplayer.Net.Crypto;

/// <summary>
/// Intercambio de claves de una conexión: cada lado crea un par X25519 efímero (nuevo en cada conexión, así que una
/// clave robada no sirve para descifrar conexiones pasadas) y de ahí se derivan dos claves de 32 bytes, una por
/// dirección, con HKDF-SHA256. El código de la partida entra en la derivación: sin él las claves salen distintas y nada
/// se descifra.
/// </summary>
public sealed class KeyExchange {
    public const int PublicKeySize = 32;
    public const int RandomSize = 16;

    private static readonly SecureRandom Random = new();

    private readonly X25519PrivateKeyParameters _privateKey;

    /// <summary>Clave pública que se envía al otro lado.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Valor aleatorio que se envía al otro lado (hace únicas las claves de cada conexión).</summary>
    public byte[] RandomBytes { get; }

    public KeyExchange() {
        _privateKey = new X25519PrivateKeyParameters(Random);
        PublicKey = _privateKey.GeneratePublicKey().GetEncoded();
        RandomBytes = new byte[RandomSize];
        Random.NextBytes(RandomBytes);
    }

    /// <summary>
    /// Deriva las claves de la conexión. Los dos lados llaman con los mismos datos de cliente y servidor, cada uno con su
    /// clave privada y la pública del otro.
    /// </summary>
    /// <returns>null si la clave pública recibida no es válida.</returns>
    public SessionKeys Derive(
        byte[] remotePublicKey,
        string roomCode,
        byte[] clientPublicKey,
        byte[] serverPublicKey,
        byte[] clientRandom,
        byte[] serverRandom
    ) {
        if (remotePublicKey == null || remotePublicKey.Length != PublicKeySize) {
            return null;
        }

        var shared = new byte[32];
        var agreement = new X25519Agreement();
        agreement.Init(_privateKey);
        try {
            agreement.CalculateAgreement(new X25519PublicKeyParameters(remotePublicKey, 0), shared, 0);
        } catch (System.Exception) {
            // Clave pública degenerada: se rechaza la conexión
            return null;
        }

        // Sal: huella del código de la partida (normalizado)
        var salt = Sha256(Encoding.UTF8.GetBytes("HKMU-room:" + RoomCode.Normalize(roomCode)));

        // Información de contexto: versión del protocolo y todo lo intercambiado en el saludo
        var info = new byte[8 + PublicKeySize * 2 + RandomSize * 2];
        Encoding.ASCII.GetBytes("HKMU-v1:", 0, 8, info, 0);
        System.Buffer.BlockCopy(clientPublicKey, 0, info, 8, PublicKeySize);
        System.Buffer.BlockCopy(serverPublicKey, 0, info, 8 + PublicKeySize, PublicKeySize);
        System.Buffer.BlockCopy(clientRandom, 0, info, 8 + PublicKeySize * 2, RandomSize);
        System.Buffer.BlockCopy(serverRandom, 0, info, 8 + PublicKeySize * 2 + RandomSize, RandomSize);

        var hkdf = new HkdfBytesGenerator(new Sha256Digest());
        hkdf.Init(new HkdfParameters(shared, salt, info));
        var output = new byte[64];
        hkdf.GenerateBytes(output, 0, output.Length);

        var keys = new SessionKeys(new byte[32], new byte[32]);
        System.Buffer.BlockCopy(output, 0, keys.ClientToServer, 0, 32);
        System.Buffer.BlockCopy(output, 32, keys.ServerToClient, 0, 32);
        System.Array.Clear(shared, 0, shared.Length);
        System.Array.Clear(output, 0, output.Length);
        return keys;
    }

    private static byte[] Sha256(byte[] data) {
        var digest = new Sha256Digest();
        digest.BlockUpdate(data, 0, data.Length);
        var result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>Claves de una conexión, una por dirección.</summary>
public sealed class SessionKeys {
    public byte[] ClientToServer { get; }
    public byte[] ServerToClient { get; }

    public SessionKeys(byte[] clientToServer, byte[] serverToClient) {
        ClientToServer = clientToServer;
        ServerToClient = serverToClient;
    }
}
