using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// Per-Tick bump arena (spec section 4.2) for command buffers and temporaries. It rents chunks from the
/// World's <see cref="SlabAllocator"/> on demand, hands out aligned slices by bumping a pointer, and
/// <see cref="Reset"/> rewinds everything at once while keeping the chunks, so after warm-up a Tick
/// allocates nothing. One allocation cannot exceed a chunk. Each reset bumps <see cref="Generation"/>,
/// which lets owners of arena memory detect that their slices are gone.
/// </summary>
internal sealed unsafe class ScratchArena : IDisposable
{
    public const int MaxAllocation = SlabAllocator.ChunkSize;
    public const byte PoisonByte = 0xCD;

    private readonly SlabAllocator _allocator;
    private readonly bool _poison;
    private nint[] _chunks = new nint[4];
    private int _chunkCount;
    private int _current = -1;
    private int _offset;
    private bool _disposed;

    public ScratchArena(SlabAllocator allocator, bool poison)
    {
        _allocator = allocator;
        _poison = poison;
    }

    /// <summary>Incremented by every <see cref="Reset"/>.</summary>
    public uint Generation { get; private set; } = 1;

    public int ChunkCount => _chunkCount;

    /// <summary>Bytes handed out since the last reset, including alignment padding.</summary>
    public long UsedBytes => _current < 0 ? 0 : (long)_current * SlabAllocator.ChunkSize + _offset;

    public byte* Alloc(int bytes, int alignment = 16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes <= 0 || bytes > MaxAllocation)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, $"A scratch allocation must be between 1 and {MaxAllocation} bytes.");
        }

        if (_current < 0)
        {
            Advance();
        }

        int aligned = (_offset + alignment - 1) & ~(alignment - 1);
        if (aligned + bytes > SlabAllocator.ChunkSize)
        {
            Advance();
            aligned = 0;
        }

        byte* result = (byte*)_chunks[_current] + aligned;
        _offset = aligned + bytes;
        return result;
    }

    /// <summary>Rewinds the arena, keeping its chunks. Poisons the used region in debug mode.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poison)
        {
            for (int i = 0; i <= _current; i++)
            {
                int used = i == _current ? _offset : SlabAllocator.ChunkSize;
                NativeMemory.Fill((void*)_chunks[i], (nuint)used, PoisonByte);
            }
        }

        _current = _chunkCount > 0 ? 0 : -1;
        _offset = 0;
        Generation++;
    }

    private void Advance()
    {
        _current++;
        if (_current == _chunkCount)
        {
            if (_chunkCount == _chunks.Length)
            {
                Array.Resize(ref _chunks, _chunks.Length * 2);
            }

            _chunks[_chunkCount++] = (nint)_allocator.Rent();
        }

        _offset = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _chunkCount; i++)
        {
            _allocator.Return((byte*)_chunks[i]);
        }

        _chunkCount = 0;
        _current = -1;
    }
}
