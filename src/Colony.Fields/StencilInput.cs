using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony.Fields;

/// <summary>A stencil body: computes every cell of <paramref name="output"/> (storage order) from the neighbourhood in <paramref name="input"/>.</summary>
public delegate void StencilKernel<T>(in StencilInput<T> input, Span<T> output)
    where T : unmanaged;

/// <summary>
/// What a stencil kernel reads (spec section 5.1): the chunk being computed plus its 26 neighbours, through
/// <see cref="Read"/> with local coordinates in <c>[-16, 32)</c>. Reads outside the World return the
/// boundary value. In double-buffered mode every read sees the previous state; in in-place ordered mode the
/// centre chunk aliases the output and lower-coloured neighbours are already updated.
/// </summary>
public readonly ref struct StencilInput<T>
    where T : unmanaged
{
    private const int Side = 3;
    private const int Cells = Side * Side * Side;

    private readonly Pointers _pointers;
    private readonly Values _uniform;
    private readonly WorldSize _size;
    private readonly bool _checkBounds;

    internal unsafe StencilInput(ChunkLayout layout, FieldStorage storage, ChunkCoord chunk, T boundary)
    {
        Chunk = chunk;
        Boundary = boundary;
        _size = layout.Size;
        var origin = chunk.Origin;
        _checkBounds = origin.X + FieldChunk.Size > _size.X || origin.Y + FieldChunk.Size > _size.Y || origin.Z + FieldChunk.Size > _size.Z;
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int slot = (dx + 1) + (Side * (dy + 1)) + (Side * Side * (dz + 1));
                    int ordinal = layout.TryOrdinalOf(chunk.X + dx, chunk.Y + dy, chunk.Z + dz);
                    if (ordinal < 0)
                    {
                        _pointers[slot] = Outside;
                        _uniform[slot] = boundary;
                    }
                    else if (storage.IsUniform(ordinal))
                    {
                        _pointers[slot] = Uniform;
                        _uniform[slot] = MemoryMarshal.Read<T>(storage.UniformValue(ordinal));
                    }
                    else
                    {
                        // Cell storage is stable native memory for the chunk's lifetime (FieldStorage contract).
                        _pointers[slot] = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(storage.ReadCells(ordinal)));
                    }
                }
            }
        }
    }

    private const nint Outside = -1;
    private const nint Uniform = 0;

    /// <summary>The chunk whose cells the kernel is computing.</summary>
    public ChunkCoord Chunk { get; }

    /// <summary>Value returned for reads outside the World.</summary>
    public T Boundary { get; }

    /// <summary>The cell at local coordinates relative to the centre chunk's origin, each in <c>[-16, 32)</c>.</summary>
    public unsafe T Read(int x, int y, int z)
    {
        if (_checkBounds)
        {
            var origin = Chunk.Origin;
            if (!_size.Contains(origin.X + x, origin.Y + y, origin.Z + z))
            {
                return Boundary;
            }
        }

        int nx = (x + FieldChunk.Size) >> FieldChunk.Shift;
        int ny = (y + FieldChunk.Size) >> FieldChunk.Shift;
        int nz = (z + FieldChunk.Size) >> FieldChunk.Shift;
        int slot = nx + (Side * ny) + (Side * Side * nz);
        nint pointer = _pointers[slot];
        if (pointer <= Uniform)
        {
            return _uniform[slot];
        }

        return ((T*)pointer)[FieldChunk.CellIndex(x & FieldChunk.Mask, y & FieldChunk.Mask, z & FieldChunk.Mask)];
    }

    /// <summary>The centre chunk's cells in storage order, or empty when it is uniform (then every cell is <see cref="CentreUniformValue"/>).</summary>
    public unsafe ReadOnlySpan<T> Centre
    {
        get
        {
            nint pointer = _pointers[CentreSlot];
            return pointer <= Uniform ? ReadOnlySpan<T>.Empty : new ReadOnlySpan<T>((T*)pointer, FieldChunk.CellCount);
        }
    }

    public bool IsCentreUniform => _pointers[CentreSlot] <= Uniform;

    public T CentreUniformValue => _uniform[CentreSlot];

    private const int CentreSlot = 1 + Side + (Side * Side);

    [InlineArray(Cells)]
    private struct Pointers
    {
        private nint _element0;
    }

    [InlineArray(Cells)]
    private struct Values
    {
        private T _element0;
    }
}
