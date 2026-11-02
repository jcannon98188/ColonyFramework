namespace Colony.Fields;

/// <summary>
/// The canonical chunk order of a World size: chunk ordinal = rank of the chunk's Morton code among all
/// chunks of the grid. Every per-chunk array in the Fields package is indexed by ordinal, so iterating
/// ordinals 0..N-1 is iterating in Morton order (spec section 5.1 and 6). Built once per schema and
/// shared by its Worlds.
/// </summary>
internal sealed class ChunkLayout
{
    private readonly ChunkCoord[] _coords;
    private readonly int[] _ordinalByLinear;
    private readonly int[] _colourOrdinals;
    private readonly int[] _colourStarts;

    public ChunkLayout(WorldSize size)
    {
        Size = size;
        int count = size.ChunkCount;
        var codes = new ulong[count];
        var coords = new ChunkCoord[count];
        int i = 0;
        for (int z = 0; z < size.ChunksZ; z++)
        {
            for (int y = 0; y < size.ChunksY; y++)
            {
                for (int x = 0; x < size.ChunksX; x++)
                {
                    coords[i] = new ChunkCoord(x, y, z);
                    codes[i] = coords[i].Morton;
                    i++;
                }
            }
        }

        Array.Sort(codes, coords);
        _coords = coords;
        _ordinalByLinear = new int[count];
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            _ordinalByLinear[Linear(coords[ordinal])] = ordinal;
        }

        // Chunk colouring for in-place stencils: colour = parity bits of (x, y, z), so two chunks of one
        // colour are never face-, edge- or corner-adjacent. Ordinals grouped by colour, Morton order within.
        _colourStarts = new int[ColourCount + 1];
        _colourOrdinals = new int[count];
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            _colourStarts[ColourOf(coords[ordinal]) + 1]++;
        }

        for (int c = 0; c < ColourCount; c++)
        {
            _colourStarts[c + 1] += _colourStarts[c];
        }

        Span<int> fill = stackalloc int[ColourCount];
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            int colour = ColourOf(coords[ordinal]);
            _colourOrdinals[_colourStarts[colour] + fill[colour]++] = ordinal;
        }
    }

    /// <summary>Eight colours: one per parity combination of the three chunk coordinates.</summary>
    public const int ColourCount = 8;

    public WorldSize Size { get; }

    public int ChunkCount => _coords.Length;

    public ChunkCoord CoordOf(int ordinal) => _coords[ordinal];

    public int OrdinalOf(ChunkCoord coord)
    {
        if ((uint)coord.X >= (uint)Size.ChunksX || (uint)coord.Y >= (uint)Size.ChunksY || (uint)coord.Z >= (uint)Size.ChunksZ)
        {
            throw new ArgumentOutOfRangeException(nameof(coord), coord, $"The chunk lies outside the {Size.ChunksX}x{Size.ChunksY}x{Size.ChunksZ} chunk grid.");
        }

        return _ordinalByLinear[Linear(coord)];
    }

    /// <summary>Ordinal of the chunk at chunk coordinates, or -1 when outside the grid.</summary>
    public int TryOrdinalOf(int cx, int cy, int cz)
        => (uint)cx < (uint)Size.ChunksX && (uint)cy < (uint)Size.ChunksY && (uint)cz < (uint)Size.ChunksZ
            ? _ordinalByLinear[cx + (Size.ChunksX * (cy + (Size.ChunksY * cz)))]
            : -1;

    public static int ColourOf(ChunkCoord coord) => (coord.X & 1) | ((coord.Y & 1) << 1) | ((coord.Z & 1) << 2);

    /// <summary>Ordinals of the chunks of one colour, in Morton order.</summary>
    public ReadOnlySpan<int> OrdinalsOfColour(int colour)
        => _colourOrdinals.AsSpan(_colourStarts[colour], _colourStarts[colour + 1] - _colourStarts[colour]);

    private int Linear(ChunkCoord coord) => coord.X + (Size.ChunksX * (coord.Y + (Size.ChunksY * coord.Z)));
}
