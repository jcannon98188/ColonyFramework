using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// One archetype (ADR 0001): all Entities with the same set of fragmenting Components, stored
/// structure-of-arrays inside 64 KiB chunks. Column 0 is the <see cref="EntityId"/> column; the
/// Components follow in id order. Every column starts on a 64-byte boundary and holds
/// <see cref="Capacity"/> elements, a multiple of 16, which is fixed by the layout and never by the CPU
/// vector width (spec section 4.2). Chunks stay dense: every chunk but the last is full, removal
/// swaps the archetype's last row into the hole. Each chunk slot has a structural version that changes
/// whenever a row is added, removed or moved in it; debug views check it.
/// </summary>
internal sealed unsafe class Archetype
{
    public const int ColumnAlignment = 64;
    public const int ElementPadding = 16;
    public const int MinCapacity = ElementPadding;
    public const int EntityColumnElementSize = 8;

    /// <summary>Largest Component that still fits <see cref="MinCapacity"/> rows next to the Entity column.</summary>
    public const int MaxComponentSize = (SlabAllocator.ChunkSize - ColumnAlignment) / MinCapacity;

    private readonly int[] _offsetById;
    private nint[] _chunks = new nint[4];
    private int[] _counts = new int[4];
    private uint[] _versions = new uint[4];

    public Archetype(int id, ComponentMask signature, WorldSchema schema)
    {
        Id = id;
        Signature = signature;
        var ids = new int[signature.Count];
        signature.CopyIds(ids);
        ComponentIds = ids;
        Sizes = new int[ids.Length];
        Offsets = new int[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            Sizes[i] = schema.Sizes[ids[i]];
        }

        long bytesPerEntity = EntityColumnElementSize;
        foreach (int size in Sizes)
        {
            bytesPerEntity += size;
        }

        int capacity = (int)(SlabAllocator.ChunkSize / bytesPerEntity) & ~(ElementPadding - 1);
        while (capacity >= MinCapacity && LayoutBytes(capacity) > SlabAllocator.ChunkSize)
        {
            capacity -= ElementPadding;
        }

        if (capacity < MinCapacity)
        {
            throw new InvalidOperationException(
                $"An archetype with {ids.Length} Components totalling {bytesPerEntity} bytes per Entity cannot fit {MinCapacity} rows in a {SlabAllocator.ChunkSize} byte chunk.");
        }

        Capacity = capacity;
        _offsetById = new int[schema.ComponentCount];
        Array.Fill(_offsetById, -1);
        int offset = Align(capacity * EntityColumnElementSize);
        for (int i = 0; i < ids.Length; i++)
        {
            Offsets[i] = offset;
            _offsetById[ids[i]] = offset;
            offset += Align(capacity * Sizes[i]);
        }

        UsedBytesPerChunk = offset;
    }

    public int Id { get; }

    public ComponentMask Signature { get; }

    /// <summary>Component ids in ascending order; parallel to <see cref="Sizes"/> and <see cref="Offsets"/>.</summary>
    public int[] ComponentIds { get; }

    public int[] Sizes { get; }

    public int[] Offsets { get; }

    /// <summary>Rows per chunk: a multiple of 16 chosen from the layout alone.</summary>
    public int Capacity { get; }

    public int UsedBytesPerChunk { get; }

    public int ChunkCount { get; private set; }

    public int EntityCount { get; private set; }

    public bool Has(int componentId) => _offsetById[componentId] >= 0;

    /// <summary>Byte offset of the Component's column inside a chunk, or -1.</summary>
    public int OffsetOf(int componentId) => _offsetById[componentId];

    public byte* ChunkPointer(int chunk) => (byte*)_chunks[chunk];

    public int RowCount(int chunk) => _counts[chunk];

    public uint Version(int chunk) => _versions[chunk];

    /// <summary>Appends a row at the end of the last chunk, renting a new chunk when it is full. The row's bytes are unspecified.</summary>
    public (int Chunk, int Row) AddRow(SlabAllocator allocator)
    {
        if (ChunkCount == 0 || _counts[ChunkCount - 1] == Capacity)
        {
            if (ChunkCount == _chunks.Length)
            {
                Array.Resize(ref _chunks, _chunks.Length * 2);
                Array.Resize(ref _counts, _counts.Length * 2);
                Array.Resize(ref _versions, _versions.Length * 2);
            }

            _chunks[ChunkCount] = (nint)allocator.Rent();
            _counts[ChunkCount] = 0;
            ChunkCount++;
        }

        int chunk = ChunkCount - 1;
        int row = _counts[chunk]++;
        _versions[chunk]++;
        EntityCount++;
        return (chunk, row);
    }

    /// <summary>
    /// Removes a row by moving the archetype's last row into it. Returns the id of the Entity that
    /// moved (its index record must be updated), or <see cref="EntityId.Null"/> when none moved.
    /// </summary>
    public EntityId RemoveRow(int chunk, int row, SlabAllocator allocator)
    {
        int lastChunk = ChunkCount - 1;
        int lastRow = _counts[lastChunk] - 1;
        EntityId moved = EntityId.Null;
        if (chunk != lastChunk || row != lastRow)
        {
            byte* destination = (byte*)_chunks[chunk];
            byte* source = (byte*)_chunks[lastChunk];
            ((ulong*)destination)[row] = ((ulong*)source)[lastRow];
            for (int i = 0; i < Sizes.Length; i++)
            {
                int size = Sizes[i];
                int offset = Offsets[i];
                Buffer.MemoryCopy(source + offset + (long)lastRow * size, destination + offset + (long)row * size, size, size);
            }

            moved = new EntityId(((ulong*)destination)[row]);
            _versions[chunk]++;
        }

        _counts[lastChunk]--;
        _versions[lastChunk]++;
        EntityCount--;
        if (_counts[lastChunk] == 0)
        {
            allocator.Return((byte*)_chunks[lastChunk]);
            _chunks[lastChunk] = 0;
            ChunkCount--;
        }

        return moved;
    }

    /// <summary>Clears every Component of a row to zero (not the Entity column).</summary>
    public void ClearRow(int chunk, int row)
    {
        byte* pointer = (byte*)_chunks[chunk];
        for (int i = 0; i < Sizes.Length; i++)
        {
            NativeMemory.Clear(pointer + Offsets[i] + (long)row * Sizes[i], (nuint)Sizes[i]);
        }
    }

    public void ReturnAllChunks(SlabAllocator allocator)
    {
        for (int i = 0; i < ChunkCount; i++)
        {
            allocator.Return((byte*)_chunks[i]);
            _chunks[i] = 0;
            _counts[i] = 0;
        }

        ChunkCount = 0;
        EntityCount = 0;
    }

    private int LayoutBytes(int capacity)
    {
        int bytes = Align(capacity * EntityColumnElementSize);
        foreach (int size in Sizes)
        {
            bytes += Align(capacity * size);
        }

        return bytes;
    }

    private static int Align(int bytes) => (bytes + ColumnAlignment - 1) & ~(ColumnAlignment - 1);
}
