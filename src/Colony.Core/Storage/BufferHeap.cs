using System.Numerics;
using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// The World-owned size-class allocator behind buffer Component overflow (spec section 4.1): power-of-two
/// blocks from 16 bytes to one 64 KiB chunk, carved out of chunks rented from the <see cref="SlabAllocator"/>,
/// with one intrusive free list per class. Handles are 32-bit (class, chunk ordinal, 16-byte offset; 0 is
/// none) so a Component stores no pointer. Allocation and release happen from any worker (buffers grow
/// inside chunk jobs) under one lock; the handle values therefore depend on thread timing and are never
/// hashed or saved, only the elements are. Blocks never move: a buffer that grows gets a new block and
/// copies.
/// </summary>
internal sealed unsafe class BufferHeap : IDisposable
{
    public const int MinBlockSize = 16;
    public const int MaxBlockSize = SlabAllocator.ChunkSize;
    private const int MinShift = 4;
    private const int ClassCount = 17 - MinShift; // 16 bytes .. 64 KiB
    private const int OffsetBits = 12;             // 64 KiB / 16-byte units
    private const int ChunkBits = 16;
    private const uint None = 0;

    private readonly SlabAllocator _allocator;
    private readonly Lock _lock = new();
    private readonly uint[] _freeHead = new uint[ClassCount];
    private readonly int[] _bumpChunk = new int[ClassCount];
    private readonly int[] _bumpOffset = new int[ClassCount];
    private nint[] _chunks = new nint[4];
    private int _chunkCount;
    private bool _disposed;
    private long _overflows;
    private long _grows;
    private long _frees;
    private long _blocksInUse;
    private long _bytesInUse;

    public BufferHeap(SlabAllocator allocator)
    {
        _allocator = allocator;
        Array.Fill(_bumpChunk, -1);
    }

    public BufferHeapCounters Counters
    {
        get
        {
            lock (_lock)
            {
                return new BufferHeapCounters(_overflows, _grows, _frees, _blocksInUse, _bytesInUse, _chunkCount);
            }
        }
    }

    /// <summary>Bytes a handle's block holds.</summary>
    public static int BlockSize(uint handle) => 1 << (MinShift + (int)((handle - 1) >> (ChunkBits + OffsetBits)));

    /// <summary>Smallest class that holds <paramref name="bytes"/>.</summary>
    public static int ClassFor(int bytes)
    {
        int size = Math.Max(MinBlockSize, bytes);
        return BitOperations.Log2((uint)size - 1) + 1 - MinShift;
    }

    public byte* Pointer(uint handle)
    {
        uint v = handle - 1;
        int chunk = (int)((v >> OffsetBits) & ((1u << ChunkBits) - 1));
        int offset = (int)(v & ((1u << OffsetBits) - 1)) << MinShift;
        return (byte*)_chunks[chunk] + offset;
    }

    /// <summary>
    /// Allocates a block of at least <paramref name="bytes"/>. <paramref name="fromInline"/> says whether the
    /// buffer is leaving its inline storage (counted as an overflow) or growing an existing block.
    /// </summary>
    public uint Alloc(int bytes, bool fromInline)
    {
        if (bytes > MaxBlockSize)
        {
            throw new InvalidOperationException($"A buffer Component cannot exceed {MaxBlockSize} bytes of elements ({bytes} requested).");
        }

        int cls = ClassFor(bytes);
        int size = 1 << (cls + MinShift);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            uint handle = _freeHead[cls];
            if (handle != None)
            {
                _freeHead[cls] = *(uint*)Pointer(handle);
            }
            else
            {
                if (_bumpChunk[cls] < 0 || _bumpOffset[cls] + size > SlabAllocator.ChunkSize)
                {
                    _bumpChunk[cls] = RentChunk();
                    _bumpOffset[cls] = 0;
                }

                handle = Handle(cls, _bumpChunk[cls], _bumpOffset[cls]);
                _bumpOffset[cls] += size;
            }

            if (fromInline)
            {
                _overflows++;
            }
            else
            {
                _grows++;
            }

            _blocksInUse++;
            _bytesInUse += size;
            return handle;
        }
    }

    public void Free(uint handle)
    {
        if (handle == None)
        {
            return;
        }

        int cls = (int)((handle - 1) >> (ChunkBits + OffsetBits));
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            *(uint*)Pointer(handle) = _freeHead[cls];
            _freeHead[cls] = handle;
            _frees++;
            _blocksInUse--;
            _bytesInUse -= 1 << (cls + MinShift);
        }
    }

    private static uint Handle(int cls, int chunk, int offset)
        => ((uint)cls << (ChunkBits + OffsetBits)) | ((uint)chunk << OffsetBits) | (uint)(offset >> MinShift) | 0u + 1u;

    private int RentChunk()
    {
        if (_chunkCount == (1 << ChunkBits))
        {
            throw new InvalidOperationException("The buffer heap has reached its 4 GiB limit.");
        }

        if (_chunkCount == _chunks.Length)
        {
            Array.Resize(ref _chunks, _chunks.Length * 2);
        }

        _chunks[_chunkCount] = (nint)_allocator.Rent();
        return _chunkCount++;
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
            _chunks[i] = 0;
        }

        _chunkCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CapacityOf(uint handle, int elementSize) => BlockSize(handle) / elementSize;
}

/// <summary>Running totals of the buffer heap (<see cref="World.BufferCounters"/>), for diagnostics.</summary>
/// <param name="Overflows">Buffers that left their inline storage.</param>
/// <param name="Grows">Overflowed buffers that moved to a larger block.</param>
/// <param name="Frees">Blocks released (Entity destroyed, Component removed or overwritten, buffer regrown).</param>
/// <param name="BlocksInUse">Blocks currently owned by live buffers.</param>
/// <param name="BytesInUse">Bytes of those blocks (size-class sizes, not element counts).</param>
/// <param name="ChunksRented">64 KiB chunks the heap holds from the World's slab allocator.</param>
public readonly record struct BufferHeapCounters(long Overflows, long Grows, long Frees, long BlocksInUse, long BytesInUse, int ChunksRented);
