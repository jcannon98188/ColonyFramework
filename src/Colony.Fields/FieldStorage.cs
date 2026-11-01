using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// Where one Field's cells live: the seam between the Field API and its storage (spec section 5.2 and 12,
/// ADR 0003). Systems never see it; <see cref="FieldAccessor{T}"/> and the stencils go through it, so a
/// GPU-resident or compressed implementation can replace <see cref="CpuFieldStorage"/> without touching
/// Systems. The CPU implementation is the oracle: any other must hash identically for the same cells.
/// Chunks are addressed by ordinal (Morton rank) and cells by storage index (<see cref="FieldChunk.CellIndex"/>).
/// A chunk is either uniform (one value, no cell storage) or dense (<see cref="FieldChunk.CellCount"/> cells).
/// Contract: the memory behind <see cref="ReadCells"/>/<see cref="WriteCells"/> is stable (native or pinned)
/// for as long as the chunk is dense; stencils cache references to it for the duration of a job.
/// </summary>
public abstract class FieldStorage : IDisposable
{
    protected FieldStorage(int cellSize, int chunkCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);
        CellSize = cellSize;
        ChunkCount = chunkCount;
    }

    public int CellSize { get; }

    public int ChunkCount { get; }

    /// <summary>Bytes of dense cell storage currently allocated (uniform chunks contribute nothing).</summary>
    public abstract long CellBytesAllocated { get; }

    public abstract bool IsUniform(int ordinal);

    /// <summary>The single value of a uniform chunk (<see cref="CellSize"/> bytes); undefined for a dense chunk.</summary>
    public abstract ReadOnlySpan<byte> UniformValue(int ordinal);

    /// <summary>All cells of a dense chunk in storage order (<see cref="FieldChunk.CellCount"/> x <see cref="CellSize"/> bytes); empty for a uniform chunk.</summary>
    public abstract ReadOnlySpan<byte> ReadCells(int ordinal);

    /// <summary>All cells of the chunk for writing; a uniform chunk is expanded to dense storage first.</summary>
    public abstract Span<byte> WriteCells(int ordinal);

    /// <summary>Makes the chunk uniform with <paramref name="value"/>. Dense storage already allocated is kept for reuse.</summary>
    public abstract void Fill(int ordinal, ReadOnlySpan<byte> value);

    /// <summary>
    /// Makes the chunk uniform with <paramref name="value"/> and gives its dense storage back (streaming unload,
    /// spec section 5.3): unlike <see cref="Fill"/>, <see cref="CellBytesAllocated"/> drops. The default keeps the block.
    /// </summary>
    public virtual void Release(int ordinal, ReadOnlySpan<byte> value) => Fill(ordinal, value);

    /// <summary>True when the chunk's cells (uniform or dense) equal <paramref name="cells"/>, a full chunk in storage order.</summary>
    public abstract bool CellsEqual(int ordinal, ReadOnlySpan<byte> cells);

    /// <summary>Appends the chunk's cells in storage order, exactly as a dense chunk with the same values would hash.</summary>
    public abstract void HashChunk(int ordinal, ref Hash64State state);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);
}
