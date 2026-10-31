namespace Colony.Fields;

/// <summary>Coordinate of a Field chunk in chunk units (tile coordinate divided by <see cref="FieldChunk.Size"/>).</summary>
public readonly record struct ChunkCoord(int X, int Y, int Z)
{
    /// <summary>The chunk containing a tile.</summary>
    public static ChunkCoord OfTile(int x, int y, int z) => new(x >> FieldChunk.Shift, y >> FieldChunk.Shift, z >> FieldChunk.Shift);

    public static ChunkCoord OfTile(TilePos tile) => OfTile(tile.X, tile.Y, tile.Z);

    /// <summary>Morton (Z-order) code of this coordinate: the canonical chunk order of iteration, hashing and saving.</summary>
    public ulong Morton => Fields.Morton.Encode(X, Y, Z);

    /// <summary>The tile at the chunk's minimum corner.</summary>
    public TilePos Origin => new(X << FieldChunk.Shift, Y << FieldChunk.Shift, Z << FieldChunk.Shift);

    public override string ToString() => $"({X}, {Y}, {Z})";
}
