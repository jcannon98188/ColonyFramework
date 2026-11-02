using Colony.Numerics;

namespace Colony.Fields;

/// <summary>
/// Position of a grid Entity in whole tiles (spec section 5.1): three ints, no floats. Register it as a
/// Component (<c>AddComponent&lt;TilePos&gt;("tile.pos")</c>); pair it with <see cref="SubTileOffset"/>
/// when an Entity moves smoothly inside its tile.
/// </summary>
public readonly record struct TilePos(int X, int Y, int Z)
{
    /// <summary>The chunk this tile lies in.</summary>
    public ChunkCoord Chunk => ChunkCoord.OfTile(X, Y, Z);

    /// <summary>Storage index of this tile's cell inside its chunk.</summary>
    public int CellIndex => FieldChunk.CellIndex(X & FieldChunk.Mask, Y & FieldChunk.Mask, Z & FieldChunk.Mask);

    public static TilePos operator +(TilePos a, TilePos b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static TilePos operator -(TilePos a, TilePos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static TilePos Add(TilePos a, TilePos b) => a + b;

    public static TilePos Subtract(TilePos a, TilePos b) => a - b;

    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>
/// Optional fixed-point offset of a grid Entity inside its tile, in tile units (so each component is
/// normally in <c>[0, 1)</c>). A separate Component, so Entities that snap to tiles do not pay for it.
/// </summary>
public readonly record struct SubTileOffset(Fixed32x3 Value)
{
    public SubTileOffset(Fixed32 x, Fixed32 y, Fixed32 z)
        : this(new Fixed32x3(x, y, z))
    {
    }

    public Fixed32 X => Value.X;

    public Fixed32 Y => Value.Y;

    public Fixed32 Z => Value.Z;
}
