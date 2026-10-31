using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Colony.Ecs;

internal struct EntityRecord
{
    public int Archetype; // -1 = dead
    public int Chunk;
    public int Row;
    public uint Generation;
}

public struct StructuralStats
{
    public long Creates, Destroys, Adds, Removes, StaleSkipped;
    public readonly long Total => Creates + Destroys + Adds + Removes;
}

public sealed unsafe partial class World : IDisposable
{
    public readonly ChunkAllocator Allocator = new();
    private readonly List<Archetype> _archetypes = new();
    private readonly Dictionary<ulong, Archetype> _bySignature = new();
    private EntityRecord[] _records = new EntityRecord[1 << 16];
    private int _recordCount;
    private int[] _freeIndices = new int[1 << 12];
    private int _freeCount;
    private readonly object?[] _resources = new object?[64];

    public ulong Seed { get; }
    public long CurrentTick { get; private set; }
    public StructuralStats Structural;

    public World(ulong seed, int threadCount)
    {
        Seed = seed;
        Scheduler = new Scheduler(this, threadCount);
    }

    public Scheduler Scheduler { get; }
    public IReadOnlyList<Archetype> Archetypes => _archetypes;
    public int AliveCount => _recordCount - _freeCount;

    // ---------------- Resources ----------------
    public void SetResource<T>(T value) where T : class => _resources[ResourceId<T>.Value] = value;
    public T GetResource<T>() where T : class => Unsafe.As<T>(_resources[ResourceId<T>.Value]!);

    // ---------------- Archetypes ----------------
    public Archetype GetOrCreateArchetype(ulong signature)
    {
        if (_bySignature.TryGetValue(signature, out var a)) return a;
        a = new Archetype(_archetypes.Count, signature);
        _archetypes.Add(a);
        _bySignature.Add(signature, a);
        return a;
    }

    // ---------------- Entities (direct API: only outside Tick or from command playback) ----------------
    public Entity CreateEntity(Archetype a)
    {
        int index;
        uint gen;
        if (_freeCount > 0)
        {
            index = _freeIndices[--_freeCount];
            gen = _records[index].Generation;
        }
        else
        {
            if (_recordCount == _records.Length) Array.Resize(ref _records, _records.Length * 2);
            index = _recordCount++;
            gen = 1;
        }
        var e = new Entity(index, gen);
        var (c, r) = a.AddRow(Allocator);
        byte* chunk = a.ChunkPtr(c);
        ((ulong*)chunk)[r] = e.Value;
        for (int i = 0; i < a.Types.Length; i++)
        {
            int s = a.TypeSizes[i];
            NativeMemory.Clear(chunk + a.TypeOffsets[i] + (long)r * s, (nuint)s);
        }
        _records[index] = new EntityRecord { Archetype = a.Id, Chunk = c, Row = r, Generation = gen };
        return e;
    }

    public Entity Create<T1, T2>(in T1 a, in T2 b) where T1 : unmanaged where T2 : unmanaged
    {
        var e = CreateEntity(GetOrCreateArchetype(Mask.Of<T1, T2>()));
        Set(e, a); Set(e, b);
        return e;
    }

    public Entity Create<T1, T2, T3, T4>(in T1 a, in T2 b, in T3 c, in T4 d)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
    {
        var e = CreateEntity(GetOrCreateArchetype(Mask.Of<T1, T2, T3, T4>()));
        Set(e, a); Set(e, b); Set(e, c); Set(e, d);
        return e;
    }

    public bool IsAlive(Entity e)
    {
        int i = e.Index;
        return (uint)i < (uint)_recordCount && _records[i].Generation == e.Generation && _records[i].Archetype >= 0;
    }

    public ref T Get<T>(Entity e) where T : unmanaged
    {
        ref var rec = ref _records[e.Index];
        var a = _archetypes[rec.Archetype];
        int off = a.OffsetOf(ComponentId<T>.Value);
        if (off < 0) throw new InvalidOperationException("missing component");
        return ref ((T*)(a.ChunkPtr(rec.Chunk) + off))[rec.Row];
    }

    public void Set<T>(Entity e, in T value) where T : unmanaged => Get<T>(e) = value;

    internal void SetRaw(Entity e, int type, byte* src)
    {
        ref var rec = ref _records[e.Index];
        var a = _archetypes[rec.Archetype];
        int s = ComponentRegistry.Sizes[type];
        Buffer.MemoryCopy(src, a.ChunkPtr(rec.Chunk) + a.OffsetOf(type) + (long)rec.Row * s, s, s);
    }

    public bool Destroy(Entity e)
    {
        if (!IsAlive(e)) return false;
        ref var rec = ref _records[e.Index];
        RemoveFromArchetype(_archetypes[rec.Archetype], rec.Chunk, rec.Row);
        rec.Archetype = -1;
        rec.Generation++;
        if (_freeCount == _freeIndices.Length) Array.Resize(ref _freeIndices, _freeIndices.Length * 2);
        _freeIndices[_freeCount++] = e.Index;
        return true;
    }

    private void RemoveFromArchetype(Archetype a, int c, int r)
    {
        var moved = a.RemoveRow(c, r, Allocator);
        if (!moved.IsNull)
        {
            ref var mr = ref _records[moved.Index];
            mr.Chunk = c;
            mr.Row = r;
        }
    }

    internal bool AddComponentRaw(Entity e, int type, byte* data)
    {
        if (!IsAlive(e)) return false;
        ref var rec = ref _records[e.Index];
        var src = _archetypes[rec.Archetype];
        if (!src.Has(type))
            MoveTo(e, ref rec, src, GetOrCreateArchetype(src.Signature | (1UL << type)));
        SetRaw(e, type, data);
        return true;
    }

    internal bool RemoveComponentRaw(Entity e, int type)
    {
        if (!IsAlive(e)) return false;
        ref var rec = ref _records[e.Index];
        var src = _archetypes[rec.Archetype];
        if (src.Has(type))
            MoveTo(e, ref rec, src, GetOrCreateArchetype(src.Signature & ~(1UL << type)));
        return true;
    }

    private void MoveTo(Entity e, ref EntityRecord rec, Archetype src, Archetype dst)
    {
        var (dc, dr) = dst.AddRow(Allocator);
        byte* d = dst.ChunkPtr(dc);
        byte* s = src.ChunkPtr(rec.Chunk);
        ((ulong*)d)[dr] = e.Value;
        for (int i = 0; i < dst.Types.Length; i++)
        {
            int t = dst.Types[i], size = dst.TypeSizes[i];
            byte* to = d + dst.TypeOffsets[i] + (long)dr * size;
            int so = src.OffsetOf(t);
            if (so >= 0) Buffer.MemoryCopy(s + so + (long)rec.Row * size, to, size, size);
            else NativeMemory.Clear(to, (nuint)size);
        }
        RemoveFromArchetype(src, rec.Chunk, rec.Row);
        rec.Archetype = dst.Id;
        rec.Chunk = dc;
        rec.Row = dr;
    }

    public int CountMatching(ulong all, ulong none = 0)
    {
        int n = 0;
        foreach (var a in _archetypes)
            if ((a.Signature & all) == all && (a.Signature & none) == 0) n += a.EntityCount;
        return n;
    }

    // ---------------- Tick ----------------
    public void AddSystem(SystemBase system) => Scheduler.Add(system);

    public void Tick()
    {
        Scheduler.RunTick(CurrentTick);
        CurrentTick++;
    }

    // ---------------- Hash ----------------
    /// <summary>xxHash64 chained over chunk contents in archetype-ID then chunk order, live rows only (padding excluded).</summary>
    public ulong ComputeHash()
    {
        ulong h = XxHash64.Hash(null, 0, (ulong)CurrentTick);
        foreach (var a in _archetypes)
        {
            ulong sig = a.Signature;
            h = XxHash64.Hash((byte*)&sig, 8, h);
            for (int c = 0; c < a.ChunkCount; c++)
            {
                byte* p = a.ChunkPtr(c);
                int n = a.ChunkEntityCount(c);
                h = XxHash64.Hash(p, n * 8, h);
                for (int i = 0; i < a.Types.Length; i++)
                    h = XxHash64.Hash(p + a.TypeOffsets[i], n * a.TypeSizes[i], h);
            }
        }
        return h;
    }

    public void Dispose()
    {
        Scheduler.Dispose();
        Allocator.Dispose();
    }
}
