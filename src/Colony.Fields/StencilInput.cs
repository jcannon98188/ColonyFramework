using System.Diagnostics.CodeAnalysis;
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
/// <remarks>
/// Performance (TASK-38): the constructor gathers the one-cell halo around the chunk (18x18x18 cells, boundary
/// values baked in) into a per-thread scratch block, so a <see cref="Read"/> within <c>[-1, 17)</c> is one
/// indexed load; in in-place mode the centre chunk is read live from the output instead, so a kernel still sees
/// its own earlier cells updated. Reads further out go through the 27-chunk pointer table as before. Edge chunks
/// that stick out past the World bound take the pointer-table path for every read in in-place mode (their
/// centre cells past the bound must read as the boundary, not as the live output).
/// </remarks>
public readonly ref struct StencilInput<T>
    where T : unmanaged
{
    private const int Side = 3;
    private const int Cells = Side * Side * Side;

    /// <summary>Halo edge: the chunk plus one cell on each side.</summary>
    private const int HaloSide = FieldChunk.Size + 2;
    private const int HaloArea = HaloSide * HaloSide;
    private const int HaloCells = HaloSide * HaloSide * HaloSide;

    private readonly Pointers _pointers;
    private readonly Values _uniform;
    private readonly unsafe T* _halo;
    private readonly unsafe T* _centre;
    private readonly WorldSize _size;
    private readonly int _originX;
    private readonly int _originY;
    private readonly int _originZ;
    private readonly bool _checkBounds;

    internal unsafe StencilInput(ChunkLayout layout, FieldStorage storage, ChunkCoord chunk, T boundary, bool aliasCentre, void* scratch)
    {
        Chunk = chunk;
        Boundary = boundary;
        _size = layout.Size;
        var origin = chunk.Origin;
        _originX = origin.X;
        _originY = origin.Y;
        _originZ = origin.Z;
        // Only a chunk that sticks out past the World bound needs per-read bounds checks; the common interior
        // chunk pays nothing. The 26 neighbours' presence is encoded in the pointer table.
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

        if (aliasCentre && _checkBounds)
        {
            // In-place on an edge chunk: live centre reads must still turn into the boundary past the World bound,
            // which only the slow path does. Rare (at most the last chunk along each axis).
            _halo = null;
            _centre = null;
            return;
        }

        _centre = aliasCentre ? (T*)_pointers[CentreSlot] : null;
        _halo = (T*)scratch; // at least HaloBytes, rented by the caller from the FieldSet's StencilScratchPool
        if (_checkBounds)
        {
            FillHaloChecked();
        }
        else
        {
            FillHalo();
        }
    }

    private const nint Outside = -1;
    private const nint Uniform = 0;

    /// <summary>Scratch bytes the constructor needs for the halo.</summary>
    internal static unsafe nuint HaloBytes => (nuint)HaloCells * (nuint)sizeof(T);

    /// <summary>The gathered 18x18x18 halo (boundary baked in), for <see cref="StencilHalo{T}"/>; always present when the centre is not aliased.</summary>
    internal unsafe T* HaloPointer => _halo;

    /// <summary>The chunk whose cells the kernel is computing.</summary>
    public ChunkCoord Chunk { get; }

    /// <summary>Value returned for reads outside the World.</summary>
    public T Boundary { get; }

    /// <summary>
    /// The cell at local coordinates relative to the centre chunk's origin, each in <c>[-16, 32)</c>.
    /// Kernels call this 7 to 27 times per cell, so it is forced inline: a live centre load (in-place mode), a
    /// halo load for the one-cell ring, else the 27-chunk pointer table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe T Read(int x, int y, int z)
    {
        if (_centre is not null && ((x | y | z) & ~FieldChunk.Mask) == 0)
        {
            return _centre[FieldChunk.CellIndex(x, y, z)];
        }

        if (_halo is not null && (uint)(x + 1) < HaloSide && (uint)(y + 1) < HaloSide && (uint)(z + 1) < HaloSide)
        {
            return _halo[(x + 1) + (HaloSide * (y + 1)) + (HaloArea * (z + 1))];
        }

        return ReadFar(x, y, z);
    }

    /// <summary>The pointer-table read: any neighbourhood coordinate, World bounds honoured for edge chunks.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe T ReadFar(int x, int y, int z)
    {
        if (_checkBounds && !_size.Contains(_originX + x, _originY + y, _originZ + z))
        {
            return Boundary;
        }

        int slot = ((x + FieldChunk.Size) >> FieldChunk.Shift)
                 + (Side * ((y + FieldChunk.Size) >> FieldChunk.Shift))
                 + (Side * Side * ((z + FieldChunk.Size) >> FieldChunk.Shift));
        if ((uint)slot >= Cells)
        {
            ThrowOutOfNeighbourhood(x, y, z);
        }

        nint pointer = _pointers[slot];
        if (pointer <= Uniform)
        {
            return _uniform[slot];
        }

        return ((T*)pointer)[FieldChunk.CellIndex(x & FieldChunk.Mask, y & FieldChunk.Mask, z & FieldChunk.Mask)];
    }

    /// <summary>Gathers the 18x18x18 halo of an interior chunk: 324 rows of (one cell, sixteen contiguous cells, one cell).</summary>
    private unsafe void FillHalo()
    {
        T* halo = _halo;
        for (int hz = 0; hz < HaloSide; hz++)
        {
            int gz = hz - 1;
            int nz = (gz + FieldChunk.Size) >> FieldChunk.Shift;
            int lz = gz & FieldChunk.Mask;
            for (int hy = 0; hy < HaloSide; hy++)
            {
                int gy = hy - 1;
                int ny = (gy + FieldChunk.Size) >> FieldChunk.Shift;
                int ly = gy & FieldChunk.Mask;
                T* row = halo + (HaloSide * hy) + (HaloArea * hz);
                int rowBase = (Side * ny) + (Side * Side * nz);
                row[0] = CellOf(rowBase, FieldChunk.CellIndex(FieldChunk.Mask, ly, lz));
                CopyRun(rowBase + 1, FieldChunk.CellIndex(0, ly, lz), row + 1);
                row[HaloSide - 1] = CellOf(rowBase + 2, FieldChunk.CellIndex(0, ly, lz));
            }
        }
    }

    /// <summary>Gathers the halo of a chunk that sticks out past the World bound, one cell at a time through the checked path.</summary>
    private unsafe void FillHaloChecked()
    {
        T* halo = _halo;
        for (int z = -1; z <= FieldChunk.Size; z++)
        {
            for (int y = -1; y <= FieldChunk.Size; y++)
            {
                for (int x = -1; x <= FieldChunk.Size; x++)
                {
                    halo[(x + 1) + (HaloSide * (y + 1)) + (HaloArea * (z + 1))] = ReadFar(x, y, z);
                }
            }
        }
    }

    private unsafe T CellOf(int slot, int cellIndex)
    {
        nint pointer = _pointers[slot];
        return pointer <= Uniform ? _uniform[slot] : ((T*)pointer)[cellIndex];
    }

    /// <summary>Copies one 16-cell x row of the chunk in <paramref name="slot"/> (or repeats its uniform value) to <paramref name="destination"/>.</summary>
    private unsafe void CopyRun(int slot, int cellIndex, T* destination)
    {
        nint pointer = _pointers[slot];
        if (pointer <= Uniform)
        {
            new Span<T>(destination, FieldChunk.Size).Fill(_uniform[slot]);
        }
        else
        {
            new ReadOnlySpan<T>((T*)pointer + cellIndex, FieldChunk.Size).CopyTo(new Span<T>(destination, FieldChunk.Size));
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfNeighbourhood(int x, int y, int z)
        => throw new ArgumentOutOfRangeException(nameof(x), $"Stencil read ({x}, {y}, {z}) lies outside the 3x3x3 chunk neighbourhood; local coordinates must be in [-{FieldChunk.Size}, {2 * FieldChunk.Size}).");

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

/// <summary>
/// Native scratch for stencil halos, owned by one World's <see cref="FieldSet"/> (TASK-38, reworked in TASK-43).
/// A job rents a slot before gathering its halo and returns it after the kernel; there are as many slots as the
/// World has workers, so a rent always finds one free. Which slot a job gets depends on timing, but a slot is
/// pure scratch (fully rewritten by every gather), so results never depend on it. Blocks grow only when a wider cell
/// type shows up and are freed with the World, so steady-state Ticks allocate nothing and nothing outlives the World
/// (the TASK-38 per-thread blocks leaked one block per worker thread of every disposed World).
/// </summary>
internal sealed unsafe class StencilScratchPool : IDisposable
{
    private const int Alignment = 64;

    private readonly nint[] _blocks;
    private readonly nuint[] _bytes;
    private readonly int[] _taken;

    public StencilScratchPool(int slots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        _blocks = new nint[slots];
        _bytes = new nuint[slots];
        _taken = new int[slots];
    }

    /// <summary>Native blocks currently allocated; test seam.</summary>
    public int LiveBlocks
    {
        get
        {
            int count = 0;
            foreach (nint block in _blocks)
            {
                count += block != 0 ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>Takes a free slot holding at least <paramref name="bytes"/>; pass the returned slot to <see cref="Return"/>.</summary>
    public int Rent(nuint bytes, out void* block)
    {
        var spin = default(SpinWait);
        while (true)
        {
            for (int slot = 0; slot < _taken.Length; slot++)
            {
                if (Volatile.Read(ref _taken[slot]) == 0 && Interlocked.CompareExchange(ref _taken[slot], 1, 0) == 0)
                {
                    if (_bytes[slot] < bytes)
                    {
                        if (_blocks[slot] != 0)
                        {
                            NativeMemory.AlignedFree((void*)_blocks[slot]);
                        }

                        _blocks[slot] = (nint)NativeMemory.AlignedAlloc(bytes, Alignment);
                        _bytes[slot] = bytes;
                    }

                    block = (void*)_blocks[slot];
                    return slot;
                }
            }

            spin.SpinOnce();
        }
    }

    public void Return(int slot) => Volatile.Write(ref _taken[slot], 0);

    public void Dispose()
    {
        for (int slot = 0; slot < _blocks.Length; slot++)
        {
            if (_blocks[slot] != 0)
            {
                NativeMemory.AlignedFree((void*)_blocks[slot]);
                _blocks[slot] = 0;
                _bytes[slot] = 0;
            }
        }
    }
}