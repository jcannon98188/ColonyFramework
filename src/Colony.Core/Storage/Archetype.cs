using System.Numerics;
using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// One archetype (ADR 0001): all Entities with the same set of fragmenting Components, stored
/// structure-of-arrays inside 64 KiB chunks. Column 0 is the <see cref="EntityId"/> column; then one
/// enable bitset per Component (spec section 4.1: frequent state flips are enable bits, not add/remove);
/// then the Components in id order. Every column starts on a 64-byte boundary and holds
/// <see cref="Capacity"/> elements, a multiple of 16, which is fixed by the layout and never by the CPU
/// vector width (spec section 4.2). Chunks stay dense: every chunk but the last is full, removal
/// swaps the archetype's last row into the hole. Each chunk slot has a structural version that changes
/// whenever a row is added, removed or moved in it; debug views check it. Flipping an enable bit is not
/// a structural change: the version does not move and views stay valid.
/// </summary>
internal sealed unsafe class Archetype
{
    public const int ColumnAlignment = 64;
    public const int ElementPadding = 16;
    public const int MinCapacity = ElementPadding;
    public const int EntityColumnElementSize = 8;

    /// <summary>Largest Component that still fits <see cref="MinCapacity"/> rows next to the Entity column and its enable bitset.</summary>
    public const int MaxComponentSize = (SlabAllocator.ChunkSize - (2 * ColumnAlignment)) / MinCapacity;

    private readonly int[] _offsetById;
    private readonly int[] _columnById;
    private nint[] _chunks = new nint[4];
    private int[] _counts = new int[4];
    private uint[] _versions = new uint[4];
    private uint[] _columnVersions;
    private int[] _enabledCounts;
    private bool[] _asleep = new bool[4];

    public Archetype(int id, ComponentMask signature, WorldSchema schema, BufferHeap heap)
    {
        Id = id;
        Signature = signature;
        Heap = heap;
        Schema = schema;
        var ids = new int[signature.Count];
        signature.CopyIds(ids);
        ComponentIds = ids;
        Sizes = new int[ids.Length];
        Offsets = new int[ids.Length];
        EnabledOffsets = new int[ids.Length];
        int buffers = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            Sizes[i] = schema.Sizes[ids[i]];
            if (schema.Components[ids[i]].IsBuffer)
            {
                buffers++;
            }
        }

        BufferColumns = new int[buffers];
        for (int i = 0, b = 0; i < ids.Length; i++)
        {
            if (schema.Components[ids[i]].IsBuffer)
            {
                BufferColumns[b++] = i;
            }
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
        EnabledBytes = Align((capacity + 7) / 8);
        _offsetById = new int[schema.ComponentCount];
        _columnById = new int[schema.ComponentCount];
        Array.Fill(_offsetById, -1);
        Array.Fill(_columnById, -1);
        int offset = Align(capacity * EntityColumnElementSize);
        for (int i = 0; i < ids.Length; i++)
        {
            EnabledOffsets[i] = offset;
            offset += EnabledBytes;
        }

        for (int i = 0; i < ids.Length; i++)
        {
            Offsets[i] = offset;
            _offsetById[ids[i]] = offset;
            _columnById[ids[i]] = i;
            offset += Align(capacity * Sizes[i]);
        }

        UsedBytesPerChunk = offset;
        _columnVersions = new uint[4 * Math.Max(1, ids.Length)];
        _enabledCounts = new int[4 * Math.Max(1, ids.Length)];
    }

    public int Id { get; }

    public ComponentMask Signature { get; }

    /// <summary>The World's buffer heap, for <see cref="ChunkView.Buffer{TElement}"/>.</summary>
    public BufferHeap Heap { get; }

    public WorldSchema Schema { get; }

    /// <summary>Column indices (into <see cref="ComponentIds"/>) of the buffer Components; their overflow blocks are freed with the row.</summary>
    public int[] BufferColumns { get; }

    /// <summary>Releases the overflow blocks of a row's buffer Components (before the row's bytes are dropped).</summary>
    public void FreeBuffers(int chunk, int row)
    {
        foreach (int column in BufferColumns)
        {
            var header = (BufferHeader*)((byte*)_chunks[chunk] + Offsets[column] + (long)row * Sizes[column]);
            Heap.Free(header->Handle);
            header->Handle = 0;
        }
    }

    /// <summary>Component ids in ascending order; parallel to <see cref="Sizes"/>, <see cref="Offsets"/> and <see cref="EnabledOffsets"/>.</summary>
    public int[] ComponentIds { get; }

    public int[] Sizes { get; }

    public int[] Offsets { get; }

    /// <summary>Byte offset of each Component's enable bitset inside a chunk (<see cref="EnabledBytes"/> each, bit r = row r).</summary>
    public int[] EnabledOffsets { get; }

    /// <summary>Bytes reserved per enable bitset: <see cref="Capacity"/> bits rounded up to the column alignment.</summary>
    public int EnabledBytes { get; }

    /// <summary>Rows per chunk: a multiple of 16 chosen from the layout alone.</summary>
    public int Capacity { get; }

    public int UsedBytesPerChunk { get; }

    public int ChunkCount { get; private set; }

    public int EntityCount { get; private set; }

    public bool Has(int componentId) => _offsetById[componentId] >= 0;

    /// <summary>Byte offset of the Component's column inside a chunk, or -1.</summary>
    public int OffsetOf(int componentId) => _offsetById[componentId];

    /// <summary>Index of the Component in <see cref="ComponentIds"/>, or -1.</summary>
    public int ColumnOf(int componentId) => _columnById[componentId];

    public byte* ChunkPointer(int chunk) => (byte*)_chunks[chunk];

    public int RowCount(int chunk) => _counts[chunk];

    public uint Version(int chunk) => _versions[chunk];

    // ---- Enable bits (spec section 4.1). Part of the World hash; never a structural change. ----

    private int Columns => Math.Max(1, ComponentIds.Length);

    public ulong* EnabledBits(int chunk, int column) => (ulong*)((byte*)_chunks[chunk] + EnabledOffsets[column]);

    public bool IsEnabled(int chunk, int column, int row)
        => (EnabledBits(chunk, column)[row >> 6] & (1UL << (row & 63))) != 0;

    /// <summary>Rows of the chunk whose Component in <paramref name="column"/> is enabled.</summary>
    public int EnabledCount(int chunk, int column) => _enabledCounts[(chunk * Columns) + column];

    public void SetEnabled(int chunk, int column, int row, bool enabled)
    {
        ulong* word = EnabledBits(chunk, column) + (row >> 6);
        ulong bit = 1UL << (row & 63);
        bool was = (*word & bit) != 0;
        if (was == enabled)
        {
            return;
        }

        *word = enabled ? *word | bit : *word & ~bit;
        _enabledCounts[(chunk * Columns) + column] += enabled ? 1 : -1;
    }

    /// <summary>True when the chunk has rows but some Component in <paramref name="query"/> is disabled on every one of them.</summary>
    public bool HasFullyDisabledColumn(int chunk, ComponentMask query)
    {
        if (_counts[chunk] == 0)
        {
            return false;
        }

        for (int i = 0; i < ComponentIds.Length; i++)
        {
            if (query.Contains(new ComponentType(ComponentIds[i])) && _enabledCounts[(chunk * Columns) + i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    // ---- Change versions and the asleep bit (spec section 4.3: chunk-level skipping). Not part of the World hash. ----

    /// <summary>Records that every column of the chunk changed (a structural change) and wakes it.</summary>
    public void MarkChanged(int chunk, uint changeCounter)
    {
        int columns = Columns;
        Array.Fill(_columnVersions, changeCounter, chunk * columns, columns);
        _asleep[chunk] = false;
    }

    /// <summary>Records that a System wrote the columns in <paramref name="writes"/> of one chunk.</summary>
    public void MarkWritten(int chunk, ComponentMask writes, uint changeCounter)
    {
        int columns = Columns;
        for (int i = 0; i < ComponentIds.Length; i++)
        {
            if (writes.Contains(new ComponentType(ComponentIds[i])))
            {
                _columnVersions[(chunk * columns) + i] = changeCounter;
            }
        }
    }

    /// <summary>Records that the host wrote one Component column of one chunk (no-op when the archetype lacks it).</summary>
    public void MarkColumnWritten(int chunk, int componentId, uint changeCounter)
    {
        int column = ColumnOf(componentId);
        if (column >= 0)
        {
            _columnVersions[(chunk * Columns) + column] = changeCounter;
        }
    }

    /// <summary>True when any column in <paramref name="filter"/> changed after <paramref name="since"/>.</summary>
    public bool HasChangedSince(int chunk, ComponentMask filter, uint since)
    {
        int columns = Columns;
        for (int i = 0; i < ComponentIds.Length; i++)
        {
            if (filter.Contains(new ComponentType(ComponentIds[i])) && _columnVersions[(chunk * columns) + i] > since)
            {
                return true;
            }
        }

        return false;
    }

    public bool IsAsleep(int chunk) => _asleep[chunk];

    public void SetAsleep(int chunk, bool asleep) => _asleep[chunk] = asleep;

    /// <summary>Appends a row at the end of the last chunk, renting a new chunk when it is full. The row's Component bytes are unspecified; every Component is enabled.</summary>
    public (int Chunk, int Row) AddRow(SlabAllocator allocator)
    {
        if (ChunkCount == 0 || _counts[ChunkCount - 1] == Capacity)
        {
            if (ChunkCount == _chunks.Length)
            {
                Array.Resize(ref _chunks, _chunks.Length * 2);
                Array.Resize(ref _counts, _counts.Length * 2);
                Array.Resize(ref _versions, _versions.Length * 2);
                Array.Resize(ref _asleep, _asleep.Length * 2);
                Array.Resize(ref _columnVersions, _columnVersions.Length * 2);
                Array.Resize(ref _enabledCounts, _enabledCounts.Length * 2);
            }

            byte* rented = allocator.Rent();
            _chunks[ChunkCount] = (nint)rented;
            _counts[ChunkCount] = 0;
            _asleep[ChunkCount] = false;
            if (ComponentIds.Length > 0)
            {
                // Bits past the live rows are always zero, so the hash can cover exactly the live bits.
                NativeMemory.Clear(rented + EnabledOffsets[0], (nuint)(EnabledBytes * ComponentIds.Length));
            }

            Array.Clear(_enabledCounts, ChunkCount * Columns, Columns);
            ChunkCount++;
        }

        int chunk = ChunkCount - 1;
        int row = _counts[chunk]++;
        for (int i = 0; i < ComponentIds.Length; i++)
        {
            EnabledBits(chunk, i)[row >> 6] |= 1UL << (row & 63);
            _enabledCounts[(chunk * Columns) + i]++;
        }

        _versions[chunk]++;
        EntityCount++;
        return (chunk, row);
    }

    /// <summary>
    /// Save/load seam (TASK-14): appends a full-size chunk holding <paramref name="rows"/> rows whose bytes
    /// the loader writes next (enable bits included; call <see cref="RecountEnabled"/> afterwards). Only
    /// valid while the last chunk is full, i.e. when restoring chunks in saved order.
    /// </summary>
    public int AppendChunk(SlabAllocator allocator, int rows)
    {
        if (rows <= 0 || rows > Capacity || (ChunkCount > 0 && _counts[ChunkCount - 1] != Capacity))
        {
            throw new ArgumentException($"A restored chunk holds 1..{Capacity} rows and follows only full chunks ({rows} rows requested).", nameof(rows));
        }

        if (ChunkCount == _chunks.Length)
        {
            Array.Resize(ref _chunks, _chunks.Length * 2);
            Array.Resize(ref _counts, _counts.Length * 2);
            Array.Resize(ref _versions, _versions.Length * 2);
            Array.Resize(ref _asleep, _asleep.Length * 2);
            Array.Resize(ref _columnVersions, _columnVersions.Length * 2);
            Array.Resize(ref _enabledCounts, _enabledCounts.Length * 2);
        }

        byte* rented = allocator.Rent();
        NativeMemory.Clear(rented, (nuint)UsedBytesPerChunk);
        int chunk = ChunkCount++;
        _chunks[chunk] = (nint)rented;
        _counts[chunk] = rows;
        _asleep[chunk] = false;
        _versions[chunk]++;
        Array.Clear(_enabledCounts, chunk * Columns, Columns);
        EntityCount += rows;
        return chunk;
    }

    /// <summary>Recomputes the per-column enabled counts of a chunk from its bits (after a loader wrote them).</summary>
    public void RecountEnabled(int chunk)
    {
        int rows = _counts[chunk];
        for (int i = 0; i < ComponentIds.Length; i++)
        {
            int count = 0;
            ulong* bits = EnabledBits(chunk, i);
            for (int w = 0; w < EnabledWords(rows); w++)
            {
                count += BitOperations.PopCount(bits[w]);
            }

            _enabledCounts[(chunk * Columns) + i] = count;
        }
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
                SetEnabled(chunk, i, row, IsEnabled(lastChunk, i, lastRow));
            }

            moved = new EntityId(((ulong*)destination)[row]);
            _versions[chunk]++;
        }

        for (int i = 0; i < Sizes.Length; i++)
        {
            SetEnabled(lastChunk, i, lastRow, false);
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

    /// <summary>Clears every Component of a row to zero (not the Entity column or the enable bits).</summary>
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

    /// <summary>Words of an enable bitset that cover the live rows of a chunk.</summary>
    public static int EnabledWords(int rows) => (rows + 63) >> 6;

    private int LayoutBytes(int capacity)
    {
        int bytes = Align(capacity * EntityColumnElementSize);
        int enabled = Align((capacity + 7) / 8);
        foreach (int size in Sizes)
        {
            bytes += Align(capacity * size) + enabled;
        }

        return bytes;
    }

    private static int Align(int bytes) => (bytes + ColumnAlignment - 1) & ~(ColumnAlignment - 1);

    /// <summary>Lowest set bit position helper for enumerators.</summary>
    public static int TrailingZeroCount(ulong word) => BitOperations.TrailingZeroCount(word);
}
