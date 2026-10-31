namespace Colony.Fields;

/// <summary>
/// The build-time chunk geometry of every Field (spec section 5.1): 16x16x16 tiles, cells row-major inside
/// the chunk with x fastest so a row of 16 cells is one SIMD run. Chunks are addressed by
/// <see cref="ChunkCoord"/> and iterated, hashed and saved in Morton order of that coordinate.
/// </summary>
public static class FieldChunk
{
    /// <summary>Tiles per chunk edge.</summary>
    public const int Size = 16;

    /// <summary><c>log2(Size)</c>.</summary>
    public const int Shift = 4;

    /// <summary><c>Size - 1</c>: masks a tile coordinate down to its position inside the chunk.</summary>
    public const int Mask = Size - 1;

    /// <summary>Cells per chunk (4096).</summary>
    public const int CellCount = Size * Size * Size;

    /// <summary>Storage index of the cell at local coordinates <c>(x, y, z)</c>, each in <c>[0, Size)</c>: <c>x + 16 y + 256 z</c>.</summary>
    public static int CellIndex(int x, int y, int z) => x | (y << Shift) | (z << (2 * Shift));

    /// <summary>Local coordinates of a storage index.</summary>
    public static (int X, int Y, int Z) CellCoordinates(int index)
        => (index & Mask, (index >> Shift) & Mask, (index >> (2 * Shift)) & Mask);
}
