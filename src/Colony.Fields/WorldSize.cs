namespace Colony.Fields;

/// <summary>
/// The bounded dimensions of a World in tiles, fixed at creation (spec section 5.1). Tile coordinates are
/// ints in <c>[0, X) x [0, Y) x [0, Z)</c>; depth 1 means a 2D World. Fields are stored in whole
/// <see cref="FieldChunk.Size"/>-cubed chunks, so the chunk grid rounds each dimension up.
/// </summary>
public readonly record struct WorldSize
{
    /// <summary>Documented maximum width in tiles.</summary>
    public const int MaxX = 4096;

    /// <summary>Documented maximum height (y) in tiles.</summary>
    public const int MaxY = 4096;

    /// <summary>Documented maximum depth in tiles.</summary>
    public const int MaxZ = 512;

    public WorldSize(int x, int y, int z)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(x, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(y, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(z, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(x, MaxX);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(y, MaxY);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(z, MaxZ);
        X = x;
        Y = y;
        Z = z;
    }

    public int X { get; }

    public int Y { get; }

    public int Z { get; }

    /// <summary>Depth 1: a single layer of tiles.</summary>
    public bool Is2D => Z == 1;

    public int ChunksX => (X + FieldChunk.Size - 1) >> FieldChunk.Shift;

    public int ChunksY => (Y + FieldChunk.Size - 1) >> FieldChunk.Shift;

    public int ChunksZ => (Z + FieldChunk.Size - 1) >> FieldChunk.Shift;

    /// <summary>Total chunks per Field.</summary>
    public int ChunkCount => ChunksX * ChunksY * ChunksZ;

    public bool Contains(int x, int y, int z)
        => (uint)x < (uint)X && (uint)y < (uint)Y && (uint)z < (uint)Z;

    public bool Contains(TilePos tile) => Contains(tile.X, tile.Y, tile.Z);

    public override string ToString() => $"{X}x{Y}x{Z}";
}
