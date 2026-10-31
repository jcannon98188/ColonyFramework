using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony.Ecs;

/// <summary>64-bit Entity ID: low 32 bits index, high 32 bits generation. Value 0 is never alive (generations start at 1).</summary>
public readonly struct Entity : IEquatable<Entity>
{
    public readonly ulong Value;
    public Entity(ulong value) => Value = value;
    public Entity(int index, uint generation) => Value = (uint)index | ((ulong)generation << 32);
    public int Index => (int)(uint)Value;
    public uint Generation => (uint)(Value >> 32);
    public bool IsNull => Value == 0;
    public bool Equals(Entity other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Entity e && e.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => $"E{Index}v{Generation}";
}

/// <summary>Registry of unmanaged Component types (max 64, so a signature is a ulong bitmask).</summary>
public static class ComponentRegistry
{
    public const int MaxComponents = 64;
    internal static readonly int[] Sizes = new int[MaxComponents];
    internal static readonly string[] Names = new string[MaxComponents];
    private static int _count;
    private static readonly object Gate = new();
    public static int Count => _count;
    public static int SizeOf(int id) => Sizes[id];
    public static string NameOf(int id) => Names[id];

    internal static int Register(Type t, int size)
    {
        lock (Gate)
        {
            if (_count >= MaxComponents) throw new InvalidOperationException("Too many component types");
            int id = _count++;
            Sizes[id] = size;
            Names[id] = t.Name;
            return id;
        }
    }
}

public static class ComponentId<T> where T : unmanaged
{
    public static readonly int Value = ComponentRegistry.Register(typeof(T), Unsafe.SizeOf<T>());
    public static readonly ulong Bit = 1UL << Value;
}

public static class Mask
{
    public static ulong Of<T1>() where T1 : unmanaged => ComponentId<T1>.Bit;
    public static ulong Of<T1, T2>() where T1 : unmanaged where T2 : unmanaged => ComponentId<T1>.Bit | ComponentId<T2>.Bit;
    public static ulong Of<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
        => ComponentId<T1>.Bit | ComponentId<T2>.Bit | ComponentId<T3>.Bit;
    public static ulong Of<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        => ComponentId<T1>.Bit | ComponentId<T2>.Bit | ComponentId<T3>.Bit | ComponentId<T4>.Bit;
}

/// <summary>Resources are World-level managed objects (written only by Systems that declare it).</summary>
public static class ResourceRegistry
{
    private static int _count;
    internal static int Next() => Interlocked.Increment(ref _count) - 1;
}

public static class ResourceId<T> where T : class
{
    public static readonly int Value = ResourceRegistry.Next();
    public static readonly ulong Bit = 1UL << Value;
}

/// <summary>Slab allocator: 4 MiB slabs from NativeMemory.AlignedAlloc, carved into 64 KiB chunks, LIFO free list.</summary>
public sealed unsafe class ChunkAllocator : IDisposable
{
    public const int ChunkSize = 64 * 1024;
    public const int SlabSize = 4 * 1024 * 1024;
    public const int ChunksPerSlab = SlabSize / ChunkSize;

    private readonly List<nint> _slabs = new();
    private nint[] _free = new nint[ChunksPerSlab * 4];
    private int _freeCount;

    public int ChunksInUse { get; private set; }
    public int PeakChunksInUse { get; private set; }
    public long ReservedBytes => (long)_slabs.Count * SlabSize;
    public int SlabCount => _slabs.Count;

    public byte* Rent()
    {
        if (_freeCount == 0) AddSlab();
        ChunksInUse++;
        if (ChunksInUse > PeakChunksInUse) PeakChunksInUse = ChunksInUse;
        return (byte*)_free[--_freeCount];
    }

    public void Return(byte* chunk)
    {
        ChunksInUse--;
        _free[_freeCount++] = (nint)chunk;
    }

    private void AddSlab()
    {
        byte* slab = (byte*)NativeMemory.AlignedAlloc(SlabSize, 4096);
        NativeMemory.Clear(slab, SlabSize);
        _slabs.Add((nint)slab);
        if (_free.Length < _slabs.Count * ChunksPerSlab) Array.Resize(ref _free, _slabs.Count * ChunksPerSlab * 2);
        // push in reverse so the lowest address is rented first
        for (int i = ChunksPerSlab - 1; i >= 0; i--) _free[_freeCount++] = (nint)(slab + (long)i * ChunkSize);
    }

    public void Dispose()
    {
        foreach (var s in _slabs) NativeMemory.AlignedFree((void*)s);
        _slabs.Clear();
        _freeCount = 0;
    }
}

/// <summary>
/// An archetype: one signature, SoA columns inside 64 KiB chunks. Column 0 is the Entity column.
/// Each column starts on a 64-byte boundary and capacity is a multiple of 16 elements.
/// Chunks are kept dense: all full except the last (swap-back removal).
/// </summary>
public sealed unsafe class Archetype
{
    public readonly int Id;
    public readonly ulong Signature;
    public readonly int Capacity;
    public readonly int[] Types;     // component ids ascending
    public readonly int[] TypeSizes; // parallel to Types
    public readonly int[] TypeOffsets; // parallel to Types
    internal readonly int[] OffsetByType = new int[ComponentRegistry.MaxComponents];
    public readonly int UsedBytesPerChunk;

    internal nint[] Chunks = new nint[4];
    internal int[] Counts = new int[4];
    public int ChunkCount { get; private set; }
    public int EntityCount { get; private set; }

    public Archetype(int id, ulong signature)
    {
        Id = id;
        Signature = signature;
        var types = new List<int>();
        for (int t = 0; t < 64; t++) if ((signature & (1UL << t)) != 0) types.Add(t);
        Types = types.ToArray();
        TypeSizes = Types.Select(ComponentRegistry.SizeOf).ToArray();
        TypeOffsets = new int[Types.Length];

        int bytesPerEntity = 8 + TypeSizes.Sum();
        int cap = (ChunkAllocator.ChunkSize / bytesPerEntity) & ~15;
        while (LayoutSize(cap) > ChunkAllocator.ChunkSize) cap -= 16;
        if (cap < 16) throw new InvalidOperationException("Archetype too wide for a 64 KiB chunk");
        Capacity = cap;

        Array.Fill(OffsetByType, -1);
        int off = Align64(cap * 8);
        for (int i = 0; i < Types.Length; i++)
        {
            TypeOffsets[i] = off;
            OffsetByType[Types[i]] = off;
            off += Align64(cap * TypeSizes[i]);
        }
        UsedBytesPerChunk = off;
    }

    private int LayoutSize(int cap)
    {
        int off = Align64(cap * 8);
        foreach (var s in TypeSizes) off += Align64(cap * s);
        return off;
    }

    private static int Align64(int x) => (x + 63) & ~63;

    public bool Has(int type) => OffsetByType[type] >= 0;
    public int OffsetOf(int type) => OffsetByType[type];
    public byte* ChunkPtr(int chunk) => (byte*)Chunks[chunk];
    public int ChunkEntityCount(int chunk) => Counts[chunk];

    public ChunkView GetChunk(int chunk) => new(this, (byte*)Chunks[chunk], Counts[chunk], chunk);

    internal (int chunk, int row) AddRow(ChunkAllocator alloc)
    {
        if (ChunkCount == 0 || Counts[ChunkCount - 1] == Capacity)
        {
            if (ChunkCount == Chunks.Length)
            {
                Array.Resize(ref Chunks, Chunks.Length * 2);
                Array.Resize(ref Counts, Counts.Length * 2);
            }
            Chunks[ChunkCount] = (nint)alloc.Rent();
            Counts[ChunkCount] = 0;
            ChunkCount++;
        }
        int c = ChunkCount - 1;
        int r = Counts[c]++;
        EntityCount++;
        return (c, r);
    }

    /// <summary>Removes (c,r) by moving the archetype's last row into it. Returns the Entity that moved, or null.</summary>
    internal Entity RemoveRow(int c, int r, ChunkAllocator alloc)
    {
        int lc = ChunkCount - 1;
        int lr = Counts[lc] - 1;
        Entity moved = default;
        if (c != lc || r != lr)
        {
            byte* dst = (byte*)Chunks[c];
            byte* src = (byte*)Chunks[lc];
            ((ulong*)dst)[r] = ((ulong*)src)[lr];
            for (int i = 0; i < Types.Length; i++)
            {
                int s = TypeSizes[i], o = TypeOffsets[i];
                Buffer.MemoryCopy(src + o + (long)lr * s, dst + o + (long)r * s, s, s);
            }
            moved = new Entity(((ulong*)dst)[r]);
        }
        Counts[lc]--;
        EntityCount--;
        if (Counts[lc] == 0)
        {
            alloc.Return((byte*)Chunks[lc]);
            Chunks[lc] = 0;
            ChunkCount--;
        }
        return moved;
    }
}

/// <summary>A view of one chunk handed to a System job: per-column spans.</summary>
public readonly unsafe struct ChunkView
{
    public readonly Archetype Archetype;
    public readonly byte* Ptr;
    public readonly int Count;
    public readonly int Index;

    public ChunkView(Archetype a, byte* ptr, int count, int index)
    {
        Archetype = a; Ptr = ptr; Count = count; Index = index;
    }

    public bool Has<T>() where T : unmanaged => Archetype.Has(ComponentId<T>.Value);

    public ReadOnlySpan<Entity> Entities => new(Ptr, Count);

    public Span<T> Column<T>() where T : unmanaged
    {
        int off = Archetype.OffsetOf(ComponentId<T>.Value);
        if (off < 0) ThrowMissing(typeof(T));
        return new Span<T>(Ptr + off, Count);
    }

    /// <summary>Column span rounded up to a multiple of 16 elements (padding is garbage; safe to process with SIMD, never hashed).</summary>
    public Span<T> PaddedColumn<T>() where T : unmanaged
    {
        int off = Archetype.OffsetOf(ComponentId<T>.Value);
        if (off < 0) ThrowMissing(typeof(T));
        return new Span<T>(Ptr + off, (Count + 15) & ~15);
    }

    private static void ThrowMissing(Type t) => throw new InvalidOperationException($"Chunk has no column {t.Name}");
}
