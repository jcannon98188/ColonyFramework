using System.Runtime.InteropServices;

namespace Colony.Fields;

/// <summary>
/// Where the residency manager gets a chunk it has no bytes for (spec section 5.3): the generation pipeline
/// (ProcGen's <c>WorldGeneration</c> implements it) or any other deterministic function of (seed, coordinate).
/// A chunk whose cells equal what the source regenerates is dropped on unload and regenerated on reload, so the
/// source must be pure: the same coordinate always yields the same cells and placements.
/// </summary>
public interface IChunkSource
{
    /// <summary>Writes the chunk's cells for every Field into <paramref name="cells"/>, which arrives filled with each Field's initial value.</summary>
    public void GenerateCells(ChunkCoord coord, ChunkCellBuffer cells);

    /// <summary>Records the chunk's initial Entities; called once per chunk, the first time it becomes resident.</summary>
    public void PlaceEntities(ChunkCoord coord, CommandBuffer commands);
}

/// <summary>One chunk's cells for every Field of a <see cref="FieldSet"/>, in storage order (<see cref="FieldChunk.CellIndex"/>).</summary>
public sealed class ChunkCellBuffer
{
    private readonly FieldSet _fields;
    private readonly byte[][] _cells;

    internal ChunkCellBuffer(FieldSet fields)
    {
        _fields = fields;
        _cells = new byte[fields.FieldCount][];
        for (int f = 0; f < _cells.Length; f++)
        {
            _cells[f] = new byte[fields.Fields[f].CellSize * FieldChunk.CellCount];
        }

        Reset();
    }

    public int FieldCount => _cells.Length;

    /// <summary>The raw cells of one Field.</summary>
    public Span<byte> Cells(int fieldId) => _cells[fieldId];

    /// <summary>The typed cells of one Field.</summary>
    public Span<T> Cells<T>(Field<T> field)
        where T : unmanaged
    {
        _ = _fields.Describe(field);
        return MemoryMarshal.Cast<byte, T>(_cells[field.Id]);
    }

    /// <summary>Refills every Field with its initial value.</summary>
    public void Reset()
    {
        for (int f = 0; f < _cells.Length; f++)
        {
            Expand(_fields.InitialValueOf(f), _cells[f]);
        }
    }

    private static void Expand(ReadOnlySpan<byte> value, Span<byte> cells)
    {
        value.CopyTo(cells);
        int filled = value.Length;
        while (filled < cells.Length)
        {
            int copy = Math.Min(filled, cells.Length - filled);
            cells[..copy].CopyTo(cells[filled..]);
            filled += copy;
        }
    }
}
