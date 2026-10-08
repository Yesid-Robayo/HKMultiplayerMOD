using HKMultiplayer.Net;

namespace HKMultiplayer.Game.Players;

/// <summary>Estado del jugador en un tick: lo que hace falta para dibujarlo en otro PC.</summary>
internal struct PlayerSnapshot {
    /// <summary>Tick del remitente (60 por segundo): marca el momento exacto del estado.</summary>
    public uint Tick;

    /// <summary>Huella de la sala en la que está (se descartan los estados de otra sala).</summary>
    public ushort SceneHash;

    public float X;
    public float Y;
    public bool FacingRight;
    public bool Visible;

    /// <summary>Sprite que se ve: colección (<see cref="HeroSprites"/>) e id dentro de ella.</summary>
    public byte Collection;

    public ushort SpriteId;

    private const byte FacingRightFlag = 1;
    private const byte VisibleFlag = 2;

    /// <summary>Tamaño en bytes.</summary>
    public const int Size = 4 + 2 + 4 + 4 + 1 + 1 + 2;

    public void Write(PacketWriter writer) {
        writer.WriteUInt(Tick);
        writer.WriteUShort(SceneHash);
        writer.WriteFloat(X);
        writer.WriteFloat(Y);
        writer.WriteByte((byte) ((FacingRight ? FacingRightFlag : 0) | (Visible ? VisibleFlag : 0)));
        writer.WriteByte(Collection);
        writer.WriteUShort(SpriteId);
    }

    public void Read(PacketReader reader) {
        Tick = reader.ReadUInt();
        SceneHash = reader.ReadUShort();
        X = reader.ReadFloat();
        Y = reader.ReadFloat();
        var flags = reader.ReadByte();
        FacingRight = (flags & FacingRightFlag) != 0;
        Visible = (flags & VisibleFlag) != 0;
        Collection = reader.ReadByte();
        SpriteId = reader.ReadUShort();
    }

    /// <summary>Si dibujarían lo mismo (para no repetir estados de un jugador quieto).</summary>
    public bool SameLook(in PlayerSnapshot other) =>
        X == other.X && Y == other.Y && FacingRight == other.FacingRight && Visible == other.Visible &&
        Collection == other.Collection && SpriteId == other.SpriteId && SceneHash == other.SceneHash;

    /// <summary>Huella de 16 bits del nombre de una sala (FNV-1a).</summary>
    public static ushort HashScene(string scene) {
        if (string.IsNullOrEmpty(scene)) {
            return 0;
        }

        var hash = 2166136261u;
        foreach (var c in scene) {
            hash = (hash ^ c) * 16777619u;
        }

        var folded = (ushort) (hash ^ (hash >> 16));
        return folded == 0 ? (ushort) 1 : folded;
    }
}
