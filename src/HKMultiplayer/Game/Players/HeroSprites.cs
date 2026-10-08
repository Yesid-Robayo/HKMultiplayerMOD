using System.Collections.Generic;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Numeración de las colecciones de sprites del caballero, igual en todos los PC: se recorren los clips de su
/// biblioteca de animaciones en orden y cada colección nueva recibe el siguiente número. Así el estado del jugador
/// envía el sprite exacto que se ve (colección + id) en 3 bytes, sea cual sea la animación o quien lo haya puesto.
/// </summary>
internal sealed class HeroSprites {
    /// <summary>Colección desconocida (no se cambia el sprite del avatar).</summary>
    public const byte Unknown = 255;

    private readonly List<tk2dSpriteCollectionData> _collections = new();
    private readonly Dictionary<tk2dSpriteCollectionData, byte> _indexByCollection = new();
    private readonly Dictionary<string, byte> _indexByName = new();

    public tk2dSpriteAnimation Library { get; }

    public HeroSprites(tk2dSpriteAnimation library) {
        Library = library;
        foreach (var clip in library.clips) {
            if (clip?.frames == null) {
                continue;
            }

            foreach (var frame in clip.frames) {
                var collection = frame?.spriteCollection;
                if (collection == null || _indexByCollection.ContainsKey(collection) || _collections.Count >= Unknown) {
                    continue;
                }

                var index = (byte) _collections.Count;
                _collections.Add(collection);
                _indexByCollection[collection] = index;
                _indexByName[collection.spriteCollectionName ?? string.Empty] = index;
            }
        }
    }

    public int Count => _collections.Count;

    /// <summary>Número de una colección (o <see cref="Unknown"/>).</summary>
    public byte IndexOf(tk2dSpriteCollectionData collection) {
        if (collection == null) {
            return Unknown;
        }

        if (_indexByCollection.TryGetValue(collection, out var index)) {
            return index;
        }

        // Otra instancia de la misma colección (se apunta para no buscar por nombre otra vez)
        if (!_indexByName.TryGetValue(collection.spriteCollectionName ?? string.Empty, out index)) {
            index = Unknown;
        }

        _indexByCollection[collection] = index;
        return index;
    }

    public tk2dSpriteCollectionData Get(byte index) => index < _collections.Count ? _collections[index] : null;
}
