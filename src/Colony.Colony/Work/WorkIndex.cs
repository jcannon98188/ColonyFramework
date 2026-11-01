using System.Runtime.InteropServices;
using Colony.Fields;
using Colony.Hashing;

namespace Colony.Jobs;

/// <summary>
/// Per-work-type, per-chunk lists of available work items (spec section 9.2), keyed by the Fields chunk their
/// <see cref="TilePos"/> stands in. The Late-Phase System <see cref="WorkIndexBuilder.SystemName"/> updates it
/// from the Component change feed: chunks whose WorkItem or TilePos column changed are collected in parallel and
/// applied serially in job order; a destroy or Component removal since the last update triggers one liveness sweep
/// over the indexed items. Queries write into caller-provided spans, allocate nothing, may run from parallel Systems
/// and return results ordered by squared tile distance then Entity id, so they are identical on any thread count.
/// Derived state: never hashed, never saved; a load calls <see cref="Rebuild"/>.
/// </summary>
public sealed class WorkIndex : IWorldExtension
{
    private const int NoEntry = -1;

    private readonly World _world;
    private readonly WorldSize _size;
    private readonly ComponentType _tilePos;
    private readonly ComponentType _workItem;
    private readonly int _typeCount;
    private readonly int _chunkCount;
    private FieldSet? _fields;

    // Buckets: an intrusive doubly linked list per (chunk ordinal, work type), threaded through the per-Entity arrays.
    private readonly int[] _head;
    private readonly int[] _bucketCount;
    private readonly int[] _countByType;

    // Per Entity slot (EntityId.Index): the id as indexed (Null = not indexed), tile, type, bucket and list links.
    private EntityId[] _ids;
    private TilePos[] _positions;
    private int[] _types;
    private int[] _bucket;
    private int[] _next;
    private int[] _prev;
    private int _highWater;

    // Per-job collection buffers of the Late update.
    private Entry[][] _jobEntries = [];
    private int[] _jobCounts = [];
    private int _jobCount;

    private long _seenDestroys;
    private long _seenRemoves;

    internal WorkIndex(World world, WorldSize size, ComponentType tilePos, ComponentType workItem, WorkIndexOptions options)
    {
        _world = world;
        _size = size;
        _tilePos = tilePos;
        _workItem = workItem;
        _typeCount = options.WorkTypeCount;
        _chunkCount = size.ChunkCount;
        _head = new int[_chunkCount * _typeCount];
        _bucketCount = new int[_chunkCount * _typeCount];
        _countByType = new int[_typeCount];
        Array.Fill(_head, NoEntry);
        int capacity = options.InitialCapacity;
        _ids = new EntityId[capacity];
        _positions = new TilePos[capacity];
        _types = new int[capacity];
        _bucket = new int[capacity];
        _next = new int[capacity];
        _prev = new int[capacity];
    }

    public World World => _world;

    public ComponentType TilePos => _tilePos;

    public ComponentType WorkItem => _workItem;

    /// <summary>Work types the index keeps lists for; item types must be below it.</summary>
    public int WorkTypeCount => _typeCount;

    /// <summary>Available items in a bucket (alive, available, inside the World, as of the last update).</summary>
    public int Count { get; private set; }

    /// <summary>Available items of one work type.</summary>
    public int CountOfType(int type) => _countByType[ValidateType(type)];

    /// <summary>Available items of one work type in one chunk ordinal.</summary>
    public int CountInChunk(int ordinal, int type) => _bucketCount[Bucket(ordinal, ValidateType(type))];

    /// <summary>(Entity, WorkItem, TilePos) rows the last update examined: every row of every chunk whose columns changed. Diagnostics.</summary>
    public int LastUpdateEntries { get; private set; }

    /// <summary>Items the last update dropped because they were destroyed or lost a Component. Diagnostics.</summary>
    public int LastUpdateRemoved { get; private set; }

    private FieldSet Fields => _fields ??= _world.Fields();

    // ---- Incremental update (Late Phase). ----

    internal void BeginUpdate(int jobCount)
    {
        if (_jobEntries.Length < jobCount)
        {
            int old = _jobEntries.Length;
            Array.Resize(ref _jobEntries, Math.Max(jobCount, old * 2));
            Array.Resize(ref _jobCounts, _jobEntries.Length);
            for (int i = old; i < _jobEntries.Length; i++)
            {
                _jobEntries[i] = [];
            }
        }

        _jobCount = jobCount;
        Array.Clear(_jobCounts, 0, jobCount);
    }

    /// <summary>Copies one changed chunk's rows into this job's buffer; runs on any worker.</summary>
    internal void Collect(int job, in ChunkView chunk)
    {
        var entities = chunk.Entities;
        var positions = chunk.Column<TilePos>(_tilePos);
        var items = chunk.Column<WorkItem>(_workItem);
        ref var entries = ref _jobEntries[job];
        if (entries.Length < entities.Length)
        {
            entries = new Entry[Math.Max(entities.Length, chunk.Capacity)];
        }

        for (int i = 0; i < entities.Length; i++)
        {
            entries[i] = new Entry(entities[i], positions[i], items[i]);
        }

        _jobCounts[job] = entities.Length;
    }

    /// <summary>Applies the collected chunks in job order, then sweeps dead items out if anything was destroyed or removed.</summary>
    internal void EndUpdate()
    {
        int examined = 0;
        for (int job = 0; job < _jobCount; job++)
        {
            var entries = _jobEntries[job];
            int count = _jobCounts[job];
            examined += count;
            for (int i = 0; i < count; i++)
            {
                Upsert(entries[i].Entity, entries[i].Position, entries[i].Item);
            }
        }

        LastUpdateEntries = examined;
        LastUpdateRemoved = 0;
        var counters = _world.Counters;
        if (counters.Destroys != _seenDestroys || counters.Removes != _seenRemoves)
        {
            LastUpdateRemoved = Sweep();
            _seenDestroys = counters.Destroys;
            _seenRemoves = counters.Removes;
        }
    }

    /// <summary>Empties the index; queries find nothing until the next update or <see cref="Rebuild"/>.</summary>
    public void Clear()
    {
        Array.Fill(_head, NoEntry);
        Array.Clear(_bucketCount);
        Array.Clear(_countByType);
        Array.Clear(_ids, 0, _highWater);
        Count = 0;
        _highWater = 0;
    }

    /// <summary>
    /// Drops everything and re-indexes every available item from the Entity storage. Call it between Ticks or from
    /// a serial System hook; the incremental update is equivalent, this is the oracle and the recovery path (a load
    /// runs it, since work-index lists are not saved).
    /// </summary>
    public void Rebuild()
    {
        Clear();
        int examined = 0;
        foreach (var chunk in _world.Query(ComponentMask.Of(_tilePos, _workItem), default, ignoreEnableBits: true))
        {
            var entities = chunk.Entities;
            var positions = chunk.Column<TilePos>(_tilePos);
            var items = chunk.Column<WorkItem>(_workItem);
            examined += entities.Length;
            for (int i = 0; i < entities.Length; i++)
            {
                Upsert(entities[i], positions[i], items[i]);
            }
        }

        LastUpdateEntries = examined;
        LastUpdateRemoved = 0;
        var counters = _world.Counters;
        _seenDestroys = counters.Destroys;
        _seenRemoves = counters.Removes;
    }

    private void Upsert(EntityId id, TilePos position, WorkItem item)
    {
        int slot = id.Index;
        EnsureSlots(slot + 1);
        bool indexed = _ids[slot] == id;
        if (!item.IsAvailable)
        {
            if (indexed)
            {
                Remove(slot);
            }

            return;
        }

        if (indexed && _positions[slot] == position && _types[slot] == item.Type)
        {
            return;
        }

        if (!_ids[slot].IsNull)
        {
            Unlink(slot);
        }

        _ids[slot] = id;
        _positions[slot] = position;
        _types[slot] = ValidateType(item.Type);
        Link(slot, OrdinalOf(position), item.Type);
    }

    private void Remove(int slot)
    {
        Unlink(slot);
        _ids[slot] = EntityId.Null;
    }

    private int Sweep()
    {
        int removed = 0;
        for (int slot = 0; slot < _highWater; slot++)
        {
            var id = _ids[slot];
            if (!id.IsNull && !(_world.IsAlive(id) && _world.Has(id, _tilePos) && _world.Has(id, _workItem)))
            {
                Remove(slot);
                removed++;
            }
        }

        return removed;
    }

    private int OrdinalOf(TilePos position)
        => _size.Contains(position) ? Fields.OrdinalOf(ChunkCoord.OfTile(position)) : NoEntry;

    private int Bucket(int ordinal, int type) => (ordinal * _typeCount) + type;

    private int ValidateType(int type)
    {
        if ((uint)type >= (uint)_typeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, $"Work type {type} is outside this index's {_typeCount} work types (WorkIndexOptions.WorkTypeCount).");
        }

        return type;
    }

    private void Link(int slot, int ordinal, int type)
    {
        _prev[slot] = NoEntry;
        _next[slot] = NoEntry;
        if (ordinal == NoEntry)
        {
            _bucket[slot] = NoEntry;
            return;
        }

        int bucket = Bucket(ordinal, type);
        _bucket[slot] = bucket;
        int head = _head[bucket];
        _next[slot] = head;
        if (head != NoEntry)
        {
            _prev[head] = slot;
        }

        _head[bucket] = slot;
        _bucketCount[bucket]++;
        _countByType[type]++;
        Count++;
    }

    private void Unlink(int slot)
    {
        int bucket = _bucket[slot];
        if (bucket == NoEntry)
        {
            return;
        }

        int next = _next[slot];
        int prev = _prev[slot];
        if (prev != NoEntry)
        {
            _next[prev] = next;
        }
        else
        {
            _head[bucket] = next;
        }

        if (next != NoEntry)
        {
            _prev[next] = prev;
        }

        _bucket[slot] = NoEntry;
        _bucketCount[bucket]--;
        _countByType[_types[slot]]--;
        Count--;
    }

    private void EnsureSlots(int needed)
    {
        if (needed > _ids.Length)
        {
            int length = Math.Max(needed, _ids.Length * 2);
            Array.Resize(ref _ids, length);
            Array.Resize(ref _positions, length);
            Array.Resize(ref _types, length);
            Array.Resize(ref _bucket, length);
            Array.Resize(ref _next, length);
            Array.Resize(ref _prev, length);
        }

        if (needed > _highWater)
        {
            _highWater = needed;
        }
    }

    // ---- Queries. ----

    /// <summary>The <c>results.Length</c> nearest available items of one work type within <paramref name="radius"/> tiles of <paramref name="origin"/>.</summary>
    public int QueryNearest(TilePos origin, int type, int radius, Span<WorkHit> results)
        => QueryNearest(origin, MemoryMarshal.CreateReadOnlySpan(ref type, 1), radius, results, default(AnyWorkItem));

    /// <summary>The <c>results.Length</c> nearest available items of one work type within <paramref name="radius"/> tiles of <paramref name="origin"/> that <paramref name="filter"/> accepts.</summary>
    public int QueryNearest<TFilter>(TilePos origin, int type, int radius, Span<WorkHit> results, TFilter filter)
        where TFilter : struct, IWorkFilter
        => QueryNearest(origin, MemoryMarshal.CreateReadOnlySpan(ref type, 1), radius, results, filter);

    /// <summary>The <c>results.Length</c> nearest available items of any of <paramref name="types"/> within <paramref name="radius"/> tiles of <paramref name="origin"/>.</summary>
    public int QueryNearest(TilePos origin, ReadOnlySpan<int> types, int radius, Span<WorkHit> results)
        => QueryNearest(origin, types, radius, results, default(AnyWorkItem));

    /// <summary>
    /// The <c>results.Length</c> nearest available items (spec section 9.2 "nearest N available of types") of any
    /// of <paramref name="types"/>, within <paramref name="radius"/> tiles (Euclidean, inclusive;
    /// <see cref="int.MaxValue"/> = unbounded) of <paramref name="origin"/>, that <paramref name="filter"/> accepts
    /// (reachability, for instance). Ordered by squared distance then Entity id; returns how many were written.
    /// Searches chunk shells outward and stops as soon as no unvisited tile can beat the worst result found.
    /// </summary>
    public int QueryNearest<TFilter>(TilePos origin, ReadOnlySpan<int> types, int radius, Span<WorkHit> results, TFilter filter)
        where TFilter : struct, IWorkFilter
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        foreach (int type in types)
        {
            ValidateType(type);
        }

        var set = new ResultSet(results);
        if (results.Length == 0 || types.Length == 0)
        {
            return 0;
        }

        long maxDistanceSquared = radius == int.MaxValue ? long.MaxValue : (long)radius * radius;
        int chunksX = _size.ChunksX;
        int chunksY = _size.ChunksY;
        int chunksZ = _size.ChunksZ;
        int cx = Math.Clamp(origin.X >> FieldChunk.Shift, 0, chunksX - 1);
        int cy = Math.Clamp(origin.Y >> FieldChunk.Shift, 0, chunksY - 1);
        int cz = Math.Clamp(origin.Z >> FieldChunk.Shift, 0, chunksZ - 1);
        int rings = Math.Max(Math.Max(Math.Max(cx, chunksX - 1 - cx), Math.Max(cy, chunksY - 1 - cy)), Math.Max(cz, chunksZ - 1 - cz));
        var fields = Fields;
        for (int r = 0; r <= rings; r++)
        {
            int zLo = Math.Max(cz - r, 0);
            int zHi = Math.Min(cz + r, chunksZ - 1);
            int yLo = Math.Max(cy - r, 0);
            int yHi = Math.Min(cy + r, chunksY - 1);
            int xLo = Math.Max(cx - r, 0);
            int xHi = Math.Min(cx + r, chunksX - 1);
            for (int z = zLo; z <= zHi; z++)
            {
                bool zFace = Math.Abs(z - cz) == r;
                for (int y = yLo; y <= yHi; y++)
                {
                    if (zFace || Math.Abs(y - cy) == r)
                    {
                        for (int x = xLo; x <= xHi; x++)
                        {
                            VisitChunk(ref set, fields.OrdinalOf(new ChunkCoord(x, y, z)), types, origin, maxDistanceSquared, filter);
                        }
                    }
                    else
                    {
                        if (cx - r >= 0)
                        {
                            VisitChunk(ref set, fields.OrdinalOf(new ChunkCoord(cx - r, y, z)), types, origin, maxDistanceSquared, filter);
                        }

                        if (cx + r < chunksX)
                        {
                            VisitChunk(ref set, fields.OrdinalOf(new ChunkCoord(cx + r, y, z)), types, origin, maxDistanceSquared, filter);
                        }
                    }
                }
            }

            // Every tile inside the cube of chunks [c - r, c + r] has been considered; the nearest unvisited tile is
            // at least the smallest gap from the origin to the cube's faces away.
            long gap = Math.Min(Math.Min(
                AxisGap(origin.X, (cx - r) << FieldChunk.Shift, ((cx + r + 1) << FieldChunk.Shift) - 1),
                AxisGap(origin.Y, (cy - r) << FieldChunk.Shift, ((cy + r + 1) << FieldChunk.Shift) - 1)),
                AxisGap(origin.Z, (cz - r) << FieldChunk.Shift, ((cz + r + 1) << FieldChunk.Shift) - 1));
            if (gap > radius || (set.IsFull && gap * gap > set.Bound))
            {
                break;
            }
        }

        return set.Count;
    }

    /// <summary>Distance from <paramref name="value"/> to the nearest integer outside <c>[lo, hi]</c>; 0 when it is already outside.</summary>
    private static long AxisGap(int value, int lo, int hi)
        => value < lo || value > hi ? 0 : Math.Min((long)value - lo + 1, (long)hi - value + 1);

    private void VisitChunk<TFilter>(ref ResultSet set, int ordinal, ReadOnlySpan<int> types, TilePos origin, long maxDistanceSquared, TFilter filter)
        where TFilter : struct, IWorkFilter
    {
        foreach (int type in types)
        {
            for (int slot = _head[Bucket(ordinal, type)]; slot != NoEntry; slot = _next[slot])
            {
                ref readonly var position = ref _positions[slot];
                long distance = SpatialIndex.DistanceSquared(origin, position);
                if (distance <= maxDistanceSquared && filter.Accepts(_ids[slot], in position, type))
                {
                    set.Consider(new WorkHit(_ids[slot], position, type, distance));
                }
            }
        }
    }

    private readonly record struct Entry(EntityId Entity, TilePos Position, WorkItem Item);

    /// <summary>A bounded result buffer kept sorted by (distance, Entity id): when full, a better hit evicts the worst.</summary>
    private ref struct ResultSet
    {
        private readonly Span<WorkHit> _hits;

        public ResultSet(Span<WorkHit> hits)
        {
            _hits = hits;
            Count = 0;
        }

        public int Count { get; private set; }

        public readonly bool IsFull => Count == _hits.Length;

        /// <summary>The worst distance still accepted when full; <see cref="long.MaxValue"/> otherwise.</summary>
        public readonly long Bound => IsFull && Count > 0 ? _hits[Count - 1].DistanceSquared : long.MaxValue;

        public void Consider(WorkHit hit)
        {
            if (_hits.Length == 0)
            {
                return;
            }

            if (IsFull)
            {
                if (!Precedes(hit, _hits[Count - 1]))
                {
                    return;
                }

                Count--;
            }

            int i = Count;
            while (i > 0 && Precedes(hit, _hits[i - 1]))
            {
                _hits[i] = _hits[i - 1];
                i--;
            }

            _hits[i] = hit;
            Count++;
        }

        private static bool Precedes(WorkHit a, WorkHit b)
            => a.DistanceSquared != b.DistanceSquared ? a.DistanceSquared < b.DistanceSquared : a.Entity.Value < b.Entity.Value;
    }

    // ---- IWorldExtension: nothing to hash, nothing per Tick beyond the System. ----

    void IWorldExtension.BeginTick(long tick)
    {
    }

    void IWorldExtension.EndPhase(Phase phase)
    {
    }

    void IWorldExtension.EndTick(long tick)
    {
    }

    /// <summary>Derived state: contributes nothing to the World hash (spec section 9.2: work-index lists are rebuilt on load).</summary>
    void IWorldExtension.Hash(ref Hash64State state)
    {
    }

    /// <summary>Not saved; a load rebuilds the lists from the loaded Entities.</summary>
    void IWorldExtension.Load(ExtensionStateReader reader) => Rebuild();

    public void Dispose()
    {
    }
}
