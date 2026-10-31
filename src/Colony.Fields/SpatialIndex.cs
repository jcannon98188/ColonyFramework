using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// Per-chunk buckets of grid Entities (spec section 5.1), keyed by the Fields chunk their <see cref="TilePos"/>
/// stands in. The Late-Phase System <see cref="SpatialIndexBuilder.SystemName"/> updates it incrementally:
/// chunks whose TilePos column changed since the last update are collected in parallel and applied serially in
/// job order; a destroy or Component removal since the last update triggers one liveness sweep over the indexed
/// Entities (Core has no per-Entity removal feed yet). Queries (tile, box, radius, nearest-k, each with an
/// optional struct predicate) write into caller-provided spans, allocate nothing, may run from parallel Systems,
/// and return results ordered by squared tile distance then Entity id, so they are identical on any thread
/// count. The index is derived state: never hashed, never saved, <see cref="Rebuild"/> recreates it from the
/// Entity storage. Entities whose TilePos lies outside the World are tracked but sit in no bucket.
/// </summary>
public sealed class SpatialIndex : IWorldExtension
{
    private const int NoEntry = -1;

    private readonly World _world;
    private readonly ChunkLayout _layout;
    private readonly WorldSize _size;
    private readonly ComponentType _tilePos;

    // Buckets: an intrusive doubly linked list per chunk ordinal, threaded through the per-Entity arrays.
    private readonly int[] _head;
    private readonly int[] _bucketCount;

    // Per Entity slot (EntityId.Index): the id as indexed (Null = not indexed), its tile, its bucket (-1 = outside) and list links.
    private EntityId[] _ids = new EntityId[1024];
    private TilePos[] _positions = new TilePos[1024];
    private int[] _ordinal = new int[1024];
    private int[] _next = new int[1024];
    private int[] _prev = new int[1024];
    private int _highWater;

    // Per-job collection buffers of the Late update.
    private Entry[][] _jobEntries = [];
    private int[] _jobCounts = [];
    private int _jobCount;

    private long _seenDestroys;
    private long _seenRemoves;

    internal SpatialIndex(World world, ChunkLayout layout, ComponentType tilePos)
    {
        _world = world;
        _layout = layout;
        _size = layout.Size;
        _tilePos = tilePos;
        _head = new int[layout.ChunkCount];
        _bucketCount = new int[layout.ChunkCount];
        Array.Fill(_head, NoEntry);
    }

    public World World => _world;

    /// <summary>The Component the index follows.</summary>
    public ComponentType TilePos => _tilePos;

    /// <summary>Entities currently in a bucket (alive, carrying TilePos inside the World, as of the last update).</summary>
    public int Count { get; private set; }

    /// <summary>Entities in the bucket of one chunk ordinal.</summary>
    public int CountInChunk(int ordinal) => _bucketCount[ordinal];

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

    /// <summary>Copies one changed chunk's (Entity, TilePos) pairs into this job's buffer; runs on any worker.</summary>
    internal void Collect(int job, in ChunkView chunk)
    {
        var entities = chunk.Entities;
        var positions = chunk.Column<TilePos>(_tilePos);
        ref var entries = ref _jobEntries[job];
        if (entries.Length < entities.Length)
        {
            entries = new Entry[Math.Max(entities.Length, chunk.Capacity)];
        }

        for (int i = 0; i < entities.Length; i++)
        {
            entries[i] = new Entry(entities[i], positions[i]);
        }

        _jobCounts[job] = entities.Length;
    }

    /// <summary>Applies the collected chunks in job order, then sweeps dead Entities out if anything was destroyed or removed.</summary>
    internal void EndUpdate()
    {
        for (int job = 0; job < _jobCount; job++)
        {
            var entries = _jobEntries[job];
            int count = _jobCounts[job];
            for (int i = 0; i < count; i++)
            {
                Upsert(entries[i].Entity, entries[i].Position);
            }
        }

        var counters = _world.Counters;
        if (counters.Destroys != _seenDestroys || counters.Removes != _seenRemoves)
        {
            Sweep();
            _seenDestroys = counters.Destroys;
            _seenRemoves = counters.Removes;
        }
    }

    /// <summary>
    /// Drops everything and re-indexes every Entity carrying TilePos from the Entity storage. Call it between
    /// Ticks or from a serial System hook; the incremental update is equivalent, this is the oracle and the
    /// recovery path (after a load, for instance).
    /// </summary>
    public void Rebuild()
    {
        Array.Fill(_head, NoEntry);
        Array.Clear(_bucketCount);
        Array.Clear(_ids, 0, _highWater);
        Count = 0;
        _highWater = 0;
        foreach (var chunk in _world.Query(_tilePos))
        {
            var entities = chunk.Entities;
            var positions = chunk.Column<TilePos>(_tilePos);
            for (int i = 0; i < entities.Length; i++)
            {
                Upsert(entities[i], positions[i]);
            }
        }

        var counters = _world.Counters;
        _seenDestroys = counters.Destroys;
        _seenRemoves = counters.Removes;
    }

    private void Upsert(EntityId id, TilePos position)
    {
        int slot = id.Index;
        EnsureSlots(slot + 1);
        if (_ids[slot] == id && _positions[slot] == position)
        {
            return;
        }

        if (!_ids[slot].IsNull)
        {
            Unlink(slot);
        }

        _ids[slot] = id;
        _positions[slot] = position;
        Link(slot, OrdinalOf(position));
    }

    private void Remove(int slot)
    {
        Unlink(slot);
        _ids[slot] = EntityId.Null;
    }

    private void Sweep()
    {
        for (int slot = 0; slot < _highWater; slot++)
        {
            var id = _ids[slot];
            if (!id.IsNull && !(_world.IsAlive(id) && _world.Has(id, _tilePos)))
            {
                Remove(slot);
            }
        }
    }

    private int OrdinalOf(TilePos position)
        => _size.Contains(position)
            ? _layout.TryOrdinalOf(position.X >> FieldChunk.Shift, position.Y >> FieldChunk.Shift, position.Z >> FieldChunk.Shift)
            : NoEntry;

    private void Link(int slot, int ordinal)
    {
        _ordinal[slot] = ordinal;
        _prev[slot] = NoEntry;
        _next[slot] = NoEntry;
        if (ordinal == NoEntry)
        {
            return;
        }

        int head = _head[ordinal];
        _next[slot] = head;
        if (head != NoEntry)
        {
            _prev[head] = slot;
        }

        _head[ordinal] = slot;
        _bucketCount[ordinal]++;
        Count++;
    }

    private void Unlink(int slot)
    {
        int ordinal = _ordinal[slot];
        if (ordinal == NoEntry)
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
            _head[ordinal] = next;
        }

        if (next != NoEntry)
        {
            _prev[next] = prev;
        }

        _ordinal[slot] = NoEntry;
        _bucketCount[ordinal]--;
        Count--;
    }

    private void EnsureSlots(int needed)
    {
        if (needed > _ids.Length)
        {
            int length = Math.Max(needed, _ids.Length * 2);
            Array.Resize(ref _ids, length);
            Array.Resize(ref _positions, length);
            Array.Resize(ref _ordinal, length);
            Array.Resize(ref _next, length);
            Array.Resize(ref _prev, length);
        }

        if (needed > _highWater)
        {
            _highWater = needed;
        }
    }

    // ---- Queries. ----

    /// <summary>Every Entity standing exactly on <paramref name="tile"/>, by Entity id. Returns how many were written (the first by that order when <paramref name="results"/> is short).</summary>
    public int QueryTile(TilePos tile, Span<SpatialHit> results) => QueryTile(tile, results, default(AnyEntity));

    public int QueryTile<TPredicate>(TilePos tile, Span<SpatialHit> results, TPredicate predicate)
        where TPredicate : struct, ISpatialPredicate
    {
        var set = new ResultSet(results);
        int ordinal = OrdinalOf(tile);
        if (ordinal != NoEntry)
        {
            for (int slot = _head[ordinal]; slot != NoEntry; slot = _next[slot])
            {
                if (_positions[slot] == tile && predicate.Matches(_ids[slot], in _positions[slot]))
                {
                    set.Consider(new SpatialHit(_ids[slot], tile, 0));
                }
            }
        }

        return set.Count;
    }

    /// <summary>Every Entity inside <paramref name="box"/>, ordered by distance from <paramref name="origin"/> then Entity id. Returns how many were written; a short buffer keeps the nearest.</summary>
    public int QueryBox(in TileBox box, TilePos origin, Span<SpatialHit> results) => QueryBox(in box, origin, results, default(AnyEntity));

    public int QueryBox<TPredicate>(in TileBox box, TilePos origin, Span<SpatialHit> results, TPredicate predicate)
        where TPredicate : struct, ISpatialPredicate
    {
        var set = new ResultSet(results);
        Scan(ref set, in box, origin, long.MaxValue, predicate);
        return set.Count;
    }

    /// <summary>Every Entity within <paramref name="radius"/> tiles (Euclidean, inclusive) of <paramref name="center"/>, nearest first then by Entity id.</summary>
    public int QueryRadius(TilePos center, int radius, Span<SpatialHit> results) => QueryRadius(center, radius, results, default(AnyEntity));

    public int QueryRadius<TPredicate>(TilePos center, int radius, Span<SpatialHit> results, TPredicate predicate)
        where TPredicate : struct, ISpatialPredicate
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        var set = new ResultSet(results);
        var box = TileBox.Around(center, radius);
        Scan(ref set, in box, center, (long)radius * radius, predicate);
        return set.Count;
    }

    /// <summary>The <c>results.Length</c> Entities nearest to <paramref name="center"/> (ties by Entity id).</summary>
    public int QueryNearest(TilePos center, Span<SpatialHit> results) => QueryNearest(center, results, default(AnyEntity));

    /// <summary>
    /// The <c>results.Length</c> Entities nearest to <paramref name="center"/> (ties by Entity id) that satisfy
    /// <paramref name="predicate"/>, optionally within <paramref name="maxRadius"/> tiles. Searches chunk shells
    /// outward and stops as soon as no unvisited tile can beat the worst result found.
    /// </summary>
    public int QueryNearest<TPredicate>(TilePos center, Span<SpatialHit> results, TPredicate predicate, int maxRadius = int.MaxValue)
        where TPredicate : struct, ISpatialPredicate
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRadius);
        var set = new ResultSet(results);
        if (results.Length == 0)
        {
            return 0;
        }

        long maxDistanceSquared = maxRadius == int.MaxValue ? long.MaxValue : (long)maxRadius * maxRadius;
        int chunksX = _size.ChunksX;
        int chunksY = _size.ChunksY;
        int chunksZ = _size.ChunksZ;
        int cx = Math.Clamp(center.X >> FieldChunk.Shift, 0, chunksX - 1);
        int cy = Math.Clamp(center.Y >> FieldChunk.Shift, 0, chunksY - 1);
        int cz = Math.Clamp(center.Z >> FieldChunk.Shift, 0, chunksZ - 1);
        int rings = Math.Max(Math.Max(Math.Max(cx, chunksX - 1 - cx), Math.Max(cy, chunksY - 1 - cy)), Math.Max(cz, chunksZ - 1 - cz));
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
                            VisitChunk(ref set, _layout.TryOrdinalOf(x, y, z), center, maxDistanceSquared, predicate);
                        }
                    }
                    else
                    {
                        if (cx - r >= 0)
                        {
                            VisitChunk(ref set, _layout.TryOrdinalOf(cx - r, y, z), center, maxDistanceSquared, predicate);
                        }

                        if (cx + r < chunksX)
                        {
                            VisitChunk(ref set, _layout.TryOrdinalOf(cx + r, y, z), center, maxDistanceSquared, predicate);
                        }
                    }
                }
            }

            // Every tile inside the cube of chunks [c - r, c + r] has been considered; the nearest unvisited tile is
            // at least the smallest gap from the center to the cube's faces away.
            long gap = Math.Min(Math.Min(
                AxisGap(center.X, (cx - r) << FieldChunk.Shift, ((cx + r + 1) << FieldChunk.Shift) - 1),
                AxisGap(center.Y, (cy - r) << FieldChunk.Shift, ((cy + r + 1) << FieldChunk.Shift) - 1)),
                AxisGap(center.Z, (cz - r) << FieldChunk.Shift, ((cz + r + 1) << FieldChunk.Shift) - 1));
            if (gap > maxRadius || (set.IsFull && gap * gap > set.Bound))
            {
                break;
            }
        }

        return set.Count;
    }

    /// <summary>Distance from <paramref name="value"/> to the nearest integer outside <c>[lo, hi]</c>; 0 when it is already outside.</summary>
    private static long AxisGap(int value, int lo, int hi)
        => value < lo || value > hi ? 0 : Math.Min((long)value - lo + 1, (long)hi - value + 1);

    private void Scan<TPredicate>(ref ResultSet set, in TileBox box, TilePos origin, long maxDistanceSquared, TPredicate predicate)
        where TPredicate : struct, ISpatialPredicate
    {
        int xLo = Math.Max(box.Min.X >> FieldChunk.Shift, 0);
        int yLo = Math.Max(box.Min.Y >> FieldChunk.Shift, 0);
        int zLo = Math.Max(box.Min.Z >> FieldChunk.Shift, 0);
        int xHi = Math.Min(box.Max.X >> FieldChunk.Shift, _size.ChunksX - 1);
        int yHi = Math.Min(box.Max.Y >> FieldChunk.Shift, _size.ChunksY - 1);
        int zHi = Math.Min(box.Max.Z >> FieldChunk.Shift, _size.ChunksZ - 1);
        for (int z = zLo; z <= zHi; z++)
        {
            for (int y = yLo; y <= yHi; y++)
            {
                for (int x = xLo; x <= xHi; x++)
                {
                    int ordinal = _layout.TryOrdinalOf(x, y, z);
                    for (int slot = _head[ordinal]; slot != NoEntry; slot = _next[slot])
                    {
                        ref readonly var position = ref _positions[slot];
                        if (!box.Contains(position))
                        {
                            continue;
                        }

                        long distance = DistanceSquared(origin, position);
                        if (distance <= maxDistanceSquared && predicate.Matches(_ids[slot], in position))
                        {
                            set.Consider(new SpatialHit(_ids[slot], position, distance));
                        }
                    }
                }
            }
        }
    }

    private void VisitChunk<TPredicate>(ref ResultSet set, int ordinal, TilePos origin, long maxDistanceSquared, TPredicate predicate)
        where TPredicate : struct, ISpatialPredicate
    {
        for (int slot = _head[ordinal]; slot != NoEntry; slot = _next[slot])
        {
            ref readonly var position = ref _positions[slot];
            long distance = DistanceSquared(origin, position);
            if (distance <= maxDistanceSquared && predicate.Matches(_ids[slot], in position))
            {
                set.Consider(new SpatialHit(_ids[slot], position, distance));
            }
        }
    }

    /// <summary>Squared Euclidean distance between two tiles, exact in 64 bits.</summary>
    public static long DistanceSquared(TilePos a, TilePos b)
    {
        long dx = (long)a.X - b.X;
        long dy = (long)a.Y - b.Y;
        long dz = (long)a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private readonly record struct Entry(EntityId Entity, TilePos Position);

    /// <summary>
    /// A bounded result buffer kept sorted by (distance, Entity id): when full, a better hit evicts the worst.
    /// Insertion is linear in the buffer length, which is small for the queries Systems make.
    /// </summary>
    private ref struct ResultSet
    {
        private readonly Span<SpatialHit> _hits;

        public ResultSet(Span<SpatialHit> hits)
        {
            _hits = hits;
            Count = 0;
        }

        public int Count { get; private set; }

        public readonly bool IsFull => Count == _hits.Length;

        /// <summary>The worst distance still accepted when full; <see cref="long.MaxValue"/> otherwise.</summary>
        public readonly long Bound => IsFull && Count > 0 ? _hits[Count - 1].DistanceSquared : long.MaxValue;

        public void Consider(SpatialHit hit)
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

        private static bool Precedes(SpatialHit a, SpatialHit b)
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

    /// <summary>Derived state: contributes nothing to the World hash (spec section 5.1).</summary>
    void IWorldExtension.Hash(ref Hash64State state)
    {
    }

    public void Dispose()
    {
    }
}
