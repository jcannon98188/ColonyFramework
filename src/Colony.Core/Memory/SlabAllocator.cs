using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// Native slab allocator (spec section 4.2): reserves 4 MiB slabs and carves them into 64 KiB chunks
/// handed out from a LIFO free list, so chunk memory never moves and the layout inside a chunk never
/// depends on the host. Slabs are 4096-byte aligned, which makes every chunk (and every 64-byte column
/// boundary inside it) aligned as well. With poisoning on, returned chunks are filled with
/// <see cref="PoisonByte"/> so use-after-free reads are recognisable.
/// </summary>
internal sealed unsafe class SlabAllocator : IDisposable
{
    public const int ChunkSize = 64 * 1024;
    public const int SlabSize = 4 * 1024 * 1024;
    public const int ChunksPerSlab = SlabSize / ChunkSize;
    public const int SlabAlignment = 4096;
    public const byte PoisonByte = 0xDD;

    private readonly bool _poison;
    private nint[] _slabs = new nint[4];
    private int _slabCount;
    private nint[] _free = new nint[ChunksPerSlab];
    private int _freeCount;
    private bool _disposed;

    public SlabAllocator(bool poison)
    {
        _poison = poison;
    }

    public int ChunksInUse { get; private set; }

    public int PeakChunksInUse { get; private set; }

    public int SlabCount => _slabCount;

    public long ReservedBytes => (long)_slabCount * SlabSize;

    /// <summary>Rents a 64 KiB chunk. Contents are unspecified (poisoned in debug, zero when fresh).</summary>
    public byte* Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_freeCount == 0)
        {
            AddSlab();
        }

        ChunksInUse++;
        if (ChunksInUse > PeakChunksInUse)
        {
            PeakChunksInUse = ChunksInUse;
        }

        return (byte*)_free[--_freeCount];
    }

    public void Return(byte* chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (chunk is null || !Owns(chunk))
        {
            throw new InvalidOperationException("The chunk being returned does not belong to this allocator.");
        }

        if (_poison)
        {
            NativeMemory.Fill(chunk, ChunkSize, PoisonByte);
        }

        ChunksInUse--;
        _free[_freeCount++] = (nint)chunk;
    }

    /// <summary>True when <paramref name="pointer"/> is a chunk boundary inside one of the slabs.</summary>
    public bool Owns(byte* pointer)
    {
        for (int i = 0; i < _slabCount; i++)
        {
            long offset = (long)(pointer - (byte*)_slabs[i]);
            if (offset >= 0 && offset < SlabSize)
            {
                return offset % ChunkSize == 0;
            }
        }

        return false;
    }

    private void AddSlab()
    {
        byte* slab = (byte*)NativeMemory.AlignedAlloc(SlabSize, SlabAlignment);
        NativeMemory.Clear(slab, SlabSize);
        if (_slabCount == _slabs.Length)
        {
            Array.Resize(ref _slabs, _slabs.Length * 2);
        }

        _slabs[_slabCount++] = (nint)slab;
        int needed = _slabCount * ChunksPerSlab;
        if (_free.Length < needed)
        {
            Array.Resize(ref _free, needed);
        }

        // Pushed high to low so the lowest address is rented first: chunks fill a slab front to back.
        for (int i = ChunksPerSlab - 1; i >= 0; i--)
        {
            _free[_freeCount++] = (nint)(slab + (long)i * ChunkSize);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _slabCount; i++)
        {
            NativeMemory.AlignedFree((void*)_slabs[i]);
        }

        _slabCount = 0;
        _freeCount = 0;
    }
}
