using System.Numerics;
using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>Running totals of one class's region upkeep; diagnostics only, never hashed.</summary>
public readonly record struct RegionCounters(
    long Builds,
    long Updates,
    long IncrementalRelabels,
    long FullRelabels,
    long ChunksEvaluated,
    long ChunksRelabelled,
    long NodesVisited);

/// <summary>
/// The region ids of one movement class over the Fields chunk grid (spec section 9.1). Each chunk is split
/// into local connected components by flood fill (face adjacency; vertical moves as the class allows); each
/// component is a node of a small chunk graph whose edges join touching components of face-adjacent chunks.
/// A region is a connected component of that graph and its id is canonical: the smallest
/// <c>(ordinal &lt;&lt; 12) | cellIndex</c> over its tiles. Incremental upkeep and a from-scratch rebuild
/// therefore produce identical ids, and so do any worker counts.
/// </summary>
/// <remarks>
/// Upkeep from a set of changed chunks: re-evaluate the passability rule; chunks whose passable and vertical
/// bits did not change are dropped. The rest are relabelled locally (parallel, chunk keyed), edges are rebuilt
/// for them and their face neighbours. Region ids live in a union-find of slots (TASK-39): a merge links the
/// slots met through the changed chunks without any search, and a split runs one breadth-first search from every
/// node touching the changed chunks at once, stopping as soon as at most one side can still grow, so the cost
/// follows the smaller sides, not the colony (untouched regions keep their ids). When at least
/// <see cref="PathfindingOptions.FullRebuildFraction"/> of the chunks changed, the whole graph is relabelled
/// instead (the chunk-set rebuild fallback). Lookups are two array reads: constant time, no allocation.
/// Reads are safe from any thread while no upkeep runs; upkeep runs only at sync points on the Tick thread.
/// </remarks>
public sealed class RegionMap
{
    private const int CompBits = 11;
    private const int Words = FieldChunk.CellCount / 64;

    private readonly World _world;
    private readonly FieldSet _fields;
    private readonly MovementClassDefinition _definition;
    private readonly PathfindingOptions _options;
    private readonly bool[]? _dependsOn;
    private readonly ChunkRegions[] _chunks;
    private readonly int[] _neighbours;
    private readonly ulong[]?[] _inWorld;
    private readonly ChunkJob _job;
    private readonly ChunkGraphPool _pool;
    private ChunkGraph? _graph;

    private int[] _dirty = new int[64];
    private int _dirtyCount;
    private int[] _changed = new int[64];
    private int _changedCount;
    private int[] _touched = new int[64];
    private int _touchedCount;
    private readonly int[] _chunkMark;
    private readonly bool[] _summaryValid;
    private int _chunkStamp;
    private long[] _queue = new long[256];

    // Regions are slots in a union-find (TASK-39): a node holds a slot, the region id is the root's minimum.
    private int[] _slotParent = [];
    private byte[] _slotRank = [];
    private long[] _slotMin = [];
    private int _slotCount;

    // Groups of one incremental update: new components and the outside nodes touching the changed chunks.
    private int[] _groupParent = new int[64];
    private int[] _groupPending = new int[64];
    private long[] _groupMin = new long[64];
    private int[] _groupSlot = new int[64];
    private int _groupCount;
    private int _stamp;
    private uint _syncedVersion;

    private long _builds;
    private long _updates;
    private long _incremental;
    private long _full;
    private long _evaluated;
    private long _relabelled;
    private long _visited;

    internal RegionMap(World world, FieldSet fields, MovementClassDefinition definition, MovementClass movementClass, PathfindingOptions options, ChunkGraphPool pool)
    {
        _world = world;
        _pool = pool;
        _fields = fields;
        _definition = definition;
        _options = options;
        Class = movementClass;
        if (definition.FieldIds.Count > 0)
        {
            _dependsOn = new bool[fields.FieldCount];
            foreach (int id in definition.FieldIds)
            {
                if ((uint)id >= (uint)fields.FieldCount)
                {
                    throw new InvalidOperationException($"Movement class '{definition.Name}' depends on Field id {id}, which this World does not have.");
                }

                _dependsOn[id] = true;
            }
        }

        int count = fields.ChunkCount;
        _chunks = new ChunkRegions[count];
        _neighbours = new int[count * 6];
        _inWorld = new ulong[]?[count];
        _chunkMark = new int[count];
        _summaryValid = new bool[count];
        var size = fields.Size;
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            _chunks[ordinal] = new ChunkRegions(definition.Vertical is not null);
            var c = fields.ChunkAt(ordinal);
            _neighbours[(ordinal * 6) + 0] = TryOrdinal(c.X - 1, c.Y, c.Z);
            _neighbours[(ordinal * 6) + 1] = TryOrdinal(c.X + 1, c.Y, c.Z);
            _neighbours[(ordinal * 6) + 2] = TryOrdinal(c.X, c.Y - 1, c.Z);
            _neighbours[(ordinal * 6) + 3] = TryOrdinal(c.X, c.Y + 1, c.Z);
            _neighbours[(ordinal * 6) + 4] = TryOrdinal(c.X, c.Y, c.Z - 1);
            _neighbours[(ordinal * 6) + 5] = TryOrdinal(c.X, c.Y, c.Z + 1);
            var origin = c.Origin;
            if (origin.X + FieldChunk.Size > size.X || origin.Y + FieldChunk.Size > size.Y || origin.Z + FieldChunk.Size > size.Z)
            {
                var mask = new ulong[Words];
                for (int i = 0; i < FieldChunk.CellCount; i++)
                {
                    var (x, y, z) = FieldChunk.CellCoordinates(i);
                    if (size.Contains(origin.X + x, origin.Y + y, origin.Z + z))
                    {
                        mask[i >> 6] |= 1UL << (i & 63);
                    }
                }

                _inWorld[ordinal] = mask;
            }
        }

        _job = new ChunkJob(this);
    }

    public MovementClass Class { get; }

    public RegionCounters Counters => new(_builds, _updates, _incremental, _full, _evaluated, _relabelled, _visited);

    /// <summary>The region of a tile, <see cref="RegionId.None"/> when impassable or outside the World. Constant time.</summary>
    public RegionId RegionOf(TilePos tile)
    {
        if (!_fields.Size.Contains(tile))
        {
            return RegionId.None;
        }

        var chunk = _chunks[_fields.OrdinalOf(tile.Chunk)];
        if (chunk.Count == 0)
        {
            return RegionId.None;
        }

        int label = chunk.Labels![tile.CellIndex];
        return label == 0 ? RegionId.None : new RegionId(_slotMin[RootOf(chunk.Slot[label - 1])]);
    }

    /// <summary>Local components of a chunk (nodes of the chunk graph); for diagnostics and tests.</summary>
    public int ComponentCount(int ordinal) => _chunks[ordinal].Count;

    /// <summary>The HPA* chunk graph of this class (TASK-20), built lazily per chunk from the labels into the service's shared pool.</summary>
    internal ChunkGraph Graph => _graph ??= new ChunkGraph(this, _pool);

    internal FieldSet Fields => _fields;

    internal int ChunkCount => _chunks.Length;

    /// <summary>The face neighbour of a chunk (direction 0..5 = -x, +x, -y, +y, -z, +z), -1 outside the World.</summary>
    internal int NeighbourOf(int ordinal, int direction) => _neighbours[(ordinal * 6) + direction];

    /// <summary>Bumps every time the chunk's local labels are rebuilt; consumers rebuild derived data when it moves.</summary>
    internal int LabelVersionOf(int ordinal) => _chunks[ordinal].Version;

    /// <summary>The chunks whose passability changed in the last <see cref="Sync"/> or <see cref="RebuildAll"/> (ascending ordinals; every chunk after a rebuild).</summary>
    internal ReadOnlySpan<int> LastRelabelled => _changed.AsSpan(0, _changedCount);

    /// <summary>Local component label per cell (0 = impassable); null while the chunk has no passable tile.</summary>
    internal ushort[]? LabelsOf(int ordinal) => _chunks[ordinal].Count == 0 ? null : _chunks[ordinal].Labels;

    internal ulong[] PassableOf(int ordinal) => _chunks[ordinal].Passable;

    /// <summary>Vertical bits (climb from the cell to z + 1 allowed); the passable bits when the class has no vertical rule.</summary>
    internal ulong[] UpOf(int ordinal) => _chunks[ordinal].Up ?? _chunks[ordinal].Passable;

    /// <summary>Whether the class can stand on the tile (inside the World and passable).</summary>
    internal bool IsPassable(TilePos tile)
        => _fields.Size.Contains(tile) && Bit(_chunks[_fields.OrdinalOf(tile.Chunk)].Passable, tile.CellIndex);

    private int TryOrdinal(int x, int y, int z)
    {
        var size = _fields.Size;
        return (uint)x < (uint)size.ChunksX && (uint)y < (uint)size.ChunksY && (uint)z < (uint)size.ChunksZ
            ? _fields.OrdinalOf(new ChunkCoord(x, y, z))
            : -1;
    }

    /// <summary>Bytes of one chunk's summary record: the ordinal then the passable bits and, for a class with a vertical rule, the up bits.</summary>
    private int SummaryRecordBytes => 4 + (Words * 8 * (_definition.Vertical is null ? 1 : 2));

    /// <summary>
    /// The pathfinding summary of every Unloaded chunk evaluated while it was resident (spec section 5.3), as a
    /// section payload: per chunk its ordinal (little-endian int32), 64 passable words and, when the class has a
    /// vertical rule, 64 up words. Empty when no chunk is Unloaded, so saves without streaming are unchanged.
    /// </summary>
    internal byte[] WriteSummary()
    {
        int count = 0;
        for (int ordinal = 0; ordinal < _chunks.Length; ordinal++)
        {
            if (_summaryValid[ordinal] && _fields.ResidencyOf(ordinal) == ChunkResidency.Unloaded)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return [];
        }

        var payload = new byte[count * SummaryRecordBytes];
        int offset = 0;
        for (int ordinal = 0; ordinal < _chunks.Length; ordinal++)
        {
            if (!_summaryValid[ordinal] || _fields.ResidencyOf(ordinal) != ChunkResidency.Unloaded)
            {
                continue;
            }

            var chunk = _chunks[ordinal];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), ordinal);
            offset += 4;
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Passable.AsSpan()).CopyTo(payload.AsSpan(offset));
            offset += Words * 8;
            if (chunk.Up is not null)
            {
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Up.AsSpan()).CopyTo(payload.AsSpan(offset));
                offset += Words * 8;
            }
        }

        return payload;
    }

    /// <summary>Restores a <see cref="WriteSummary"/> payload before the first <see cref="RebuildAll"/>: those chunks are labelled from the summary, not from their (absent) cells.</summary>
    internal void RestoreSummary(ReadOnlySpan<byte> payload, string extensionName)
    {
        int record = SummaryRecordBytes;
        if (payload.Length % record != 0)
        {
            throw new InvalidDataException($"Extension '{extensionName}': the pathfinding summary of class '{_definition.Name}' holds {payload.Length} bytes, not a multiple of {record}.");
        }

        for (int offset = 0; offset < payload.Length; offset += record)
        {
            int ordinal = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
            if ((uint)ordinal >= (uint)_chunks.Length)
            {
                throw new InvalidDataException($"Extension '{extensionName}': the pathfinding summary of class '{_definition.Name}' names chunk {ordinal} of {_chunks.Length}.");
            }

            if (_fields.ResidencyOf(ordinal) != ChunkResidency.Unloaded)
            {
                continue; // resident now: its cells are the source
            }

            var chunk = _chunks[ordinal];
            payload.Slice(offset + 4, Words * 8).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Passable.AsSpan()));
            if (chunk.Up is not null)
            {
                payload.Slice(offset + 4 + (Words * 8), Words * 8).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Up.AsSpan()));
            }

            _summaryValid[ordinal] = true;
        }
    }

    /// <summary>Relabels every chunk and the whole graph from the current Field state.</summary>
    internal void RebuildAll(bool parallel)
    {
        _builds++;
        _syncedVersion = _fields.CurrentVersion - 1;
        int count = _chunks.Length;
        EnsureCapacity(ref _changed, count);
        for (int i = 0; i < count; i++)
        {
            _changed[i] = i;
        }

        _changedCount = count;
        Run(ChunkJob.Mode.EvaluateAll, _changed, count, parallel);
        Run(ChunkJob.Mode.Relabel, _changed, count, parallel);
        Run(ChunkJob.Mode.Edges, _changed, count, parallel);
        _evaluated += count;
        _relabelled += count;
        LabelAll();
    }

    /// <summary>Applies every chunk written since the last sync (by the class's dependent Fields).</summary>
    internal void Sync(bool parallel)
    {
        _dirtyCount = 0;
        _changedCount = 0;
        _chunkStamp++;
        foreach (var change in _fields.ChangedSince(_syncedVersion))
        {
            if (_dependsOn is not null && !_dependsOn[change.FieldId])
            {
                continue;
            }

            if (_chunkMark[change.Ordinal] != _chunkStamp)
            {
                _chunkMark[change.Ordinal] = _chunkStamp;
                Append(ref _dirty, ref _dirtyCount, change.Ordinal);
            }
        }

        // Chunks stamped with the running version may be written again before it completes: keep them for next time.
        _syncedVersion = _fields.CurrentVersion - 1;
        if (_dirtyCount == 0)
        {
            return;
        }

        Array.Sort(_dirty, 0, _dirtyCount);
        Update(parallel);
    }

    /// <summary>Re-evaluates the dirty chunks (ascending ordinals) and relabels what changed.</summary>
    private void Update(bool parallel)
    {
        _updates++;
        Run(ChunkJob.Mode.Evaluate, _dirty, _dirtyCount, parallel);
        _evaluated += _dirtyCount;
        _changedCount = 0;
        for (int i = 0; i < _dirtyCount; i++)
        {
            if (_chunks[_dirty[i]].PendingChanged)
            {
                Append(ref _changed, ref _changedCount, _dirty[i]);
            }
        }

        if (_changedCount == 0)
        {
            return;
        }

        bool full = _changedCount >= Math.Max(1, (int)(_options.FullRebuildFraction * _chunks.Length));

        Run(ChunkJob.Mode.Relabel, _changed, _changedCount, parallel);
        _relabelled += _changedCount;

        // Edges change for the changed chunks and their face neighbours.
        _chunkStamp++;
        int changedStamp = _chunkStamp;
        for (int i = 0; i < _changedCount; i++)
        {
            _chunkMark[_changed[i]] = changedStamp;
        }

        _touchedCount = 0;
        for (int i = 0; i < _changedCount; i++)
        {
            Append(ref _touched, ref _touchedCount, _changed[i]);
        }

        _chunkStamp++;
        int touchedStamp = _chunkStamp;
        for (int i = 0; i < _changedCount; i++)
        {
            int ordinal = _changed[i];
            for (int d = 0; d < 6; d++)
            {
                int n = _neighbours[(ordinal * 6) + d];
                if (n >= 0 && _chunkMark[n] != changedStamp && _chunkMark[n] != touchedStamp)
                {
                    _chunkMark[n] = touchedStamp;
                    Append(ref _touched, ref _touchedCount, n);
                }
            }
        }

        Array.Sort(_touched, 0, _touchedCount);
        if (full)
        {
            Run(ChunkJob.Mode.Edges, _touched, _touchedCount, parallel);
            _full++;
            LabelAll();
            return;
        }

        _incremental++;
        _stamp++;
        _groupCount = 0;

        // Every new component of a changed chunk starts a group; so does every outside node that touched a changed
        // chunk before (a region may split there) or touches one now (regions may merge there).
        for (int i = 0; i < _changedCount; i++)
        {
            int ordinal = _changed[i];
            var chunk = _chunks[ordinal];
            for (int c = 0; c < chunk.Count; c++)
            {
                chunk.Visit[c] = _stamp;
                chunk.Group[c] = NewGroup(Key(ordinal, chunk.MinCell[c]), 0);
            }
        }

        int tail = 0;
        CollectPortals(changedStamp, ref tail, unite: false);
        Run(ChunkJob.Mode.Edges, _touched, _touchedCount, parallel);
        CollectPortals(changedStamp, ref tail, unite: true);
        for (int i = 0; i < _changedCount; i++)
        {
            int ordinal = _changed[i];
            var chunk = _chunks[ordinal];
            for (int e = 0; e < chunk.EdgeCount; e++)
            {
                int edge = chunk.Edges[e];
                int n = _neighbours[(ordinal * 6) + ((edge >> 12) & 7)];
                if (_chunkMark[n] == changedStamp)
                {
                    LinkGroups(FindGroup(chunk.Group[edge >> 15]), FindGroup(_chunks[n].Group[edge & 0xFFF]));
                }
            }
        }

        if (_slotCount + _groupCount > _slotParent.Length)
        {
            LabelAll(); // slots exhausted: compact (the same ids, fresh dense slots)
            return;
        }

        // Merge fast path: the groups linked through the changed chunks are their merged regions, no search. Then a
        // bounded split search: one breadth-first search over the unchanged nodes, all groups interleaved, which
        // stops as soon as at most one group can still grow. Every finished group is a whole region; the one left
        // (usually the big side of a split) keeps the old slots it reached, so its unvisited part is never walked.
        int active = 0;
        for (int g = 0; g < _groupCount; g++)
        {
            if (_groupParent[g] == g && _groupPending[g] > 0)
            {
                active++;
            }
        }

        int head = 0;
        while (head < tail && active > 1)
        {
            long node = _queue[head++];
            int o = (int)(node >> CompBits);
            int c = (int)(node & ((1 << CompBits) - 1));
            var chunk = _chunks[o];
            int g = FindGroup(chunk.Group[c]);
            int end = chunk.EdgeStart[c + 1];
            for (int e = chunk.EdgeStart[c]; e < end; e++)
            {
                int edge = chunk.Edges[e];
                int n = _neighbours[(o * 6) + ((edge >> 12) & 7)];
                if (_chunkMark[n] == changedStamp)
                {
                    continue; // linked above
                }

                int their = edge & 0xFFF;
                var other = _chunks[n];
                if (other.Visit[their] != _stamp)
                {
                    other.Visit[their] = _stamp;
                    other.Group[their] = g;
                    _groupPending[g]++;
                    _groupMin[g] = Math.Min(_groupMin[g], Key(n, other.MinCell[their]));
                    Push(ref tail, ((long)n << CompBits) | (uint)their);
                }
                else
                {
                    int h = FindGroup(other.Group[their]);
                    if (h != g)
                    {
                        if (_groupPending[h] > 0)
                        {
                            active--;
                        }

                        g = LinkGroups(g, h);
                    }
                }
            }

            if (--_groupPending[g] == 0)
            {
                active--;
            }
        }

        _visited += tail;
        int left = head < tail ? FindGroup(_chunks[(int)(_queue[head] >> CompBits)].Group[(int)(_queue[head] & ((1 << CompBits) - 1))]) : -1;
        for (int g = 0; g < _groupCount; g++)
        {
            _groupSlot[g] = -1;
        }

        // Finished groups get fresh slots; the old slots the remaining group reached are joined into one.
        int slot = -1;
        long oldMin = long.MaxValue;
        for (int i = 0; i < tail; i++)
        {
            long node = _queue[i];
            var chunk = _chunks[(int)(node >> CompBits)];
            int c = (int)(node & ((1 << CompBits) - 1));
            int g = FindGroup(chunk.Group[c]);
            if (g != left)
            {
                chunk.Slot[c] = SlotOf(g);
                continue;
            }

            int old = FindSlot(chunk.Slot[c]);
            if (old != slot)
            {
                oldMin = Math.Min(oldMin, _slotMin[old]);
                slot = slot < 0 ? old : LinkSlots(slot, old);
            }
        }

        for (int i = 0; i < _changedCount; i++)
        {
            var chunk = _chunks[_changed[i]];
            _visited += chunk.Count;
            for (int c = 0; c < chunk.Count; c++)
            {
                int g = FindGroup(chunk.Group[c]);
                chunk.Slot[c] = g != left ? SlotOf(g) : slot;
            }
        }

        if (left < 0)
        {
            return;
        }

        // The remaining group's id: the smallest key over what was visited, or over the unvisited rest, whose
        // smallest key is at least the smallest old id among the joined slots (that node may have left the region).
        slot = FindSlot(slot);
        long id = _groupMin[left];
        if (oldMin < id)
        {
            id = Holds(oldMin, slot, changedStamp) ? oldMin : SmallestFrom(oldMin, id, slot, changedStamp);
        }

        _slotMin[slot] = id;
    }

    /// <summary>Starts a group for every outside node with an edge into a changed chunk; with <paramref name="unite"/>, links it to the component it touches.</summary>
    private void CollectPortals(int changedStamp, ref int tail, bool unite)
    {
        for (int i = 0; i < _touchedCount; i++)
        {
            int ordinal = _touched[i];
            if (_chunkMark[ordinal] == changedStamp)
            {
                continue;
            }

            var chunk = _chunks[ordinal];
            for (int e = 0; e < chunk.EdgeCount; e++)
            {
                int edge = chunk.Edges[e];
                int n = _neighbours[(ordinal * 6) + ((edge >> 12) & 7)];
                if (_chunkMark[n] != changedStamp)
                {
                    continue;
                }

                int c = edge >> 15;
                if (chunk.Visit[c] != _stamp)
                {
                    chunk.Visit[c] = _stamp;
                    chunk.Group[c] = NewGroup(Key(ordinal, chunk.MinCell[c]), 1);
                    Push(ref tail, ((long)ordinal << CompBits) | (uint)c);
                }

                if (unite)
                {
                    LinkGroups(FindGroup(chunk.Group[c]), FindGroup(_chunks[n].Group[edge & 0xFFF]));
                }
            }
        }
    }

    /// <summary>Whether the node with smallest cell <paramref name="key"/> still exists outside the changed chunks and lies in <paramref name="root"/>.</summary>
    private bool Holds(long key, int root, int changedStamp)
    {
        int ordinal = (int)(key >> 12);
        int cell = (int)(key & 0xFFF);
        var chunk = _chunks[ordinal];
        if (_chunkMark[ordinal] == changedStamp || chunk.Count == 0)
        {
            return false;
        }

        int label = chunk.Labels![cell];
        return label != 0 && chunk.MinCell[label - 1] == cell && FindSlot(chunk.Slot[label - 1]) == root;
    }

    /// <summary>The smallest key in <c>[from, below)</c> of a node in <paramref name="root"/>, or <paramref name="below"/>.</summary>
    private long SmallestFrom(long from, long below, int root, int changedStamp)
    {
        for (int ordinal = (int)(from >> 12); ordinal <= (int)(below >> 12); ordinal++)
        {
            if (_chunkMark[ordinal] == changedStamp)
            {
                continue; // its components are in the visited part
            }

            var chunk = _chunks[ordinal];
            for (int c = 0; c < chunk.Count; c++)
            {
                long key = Key(ordinal, chunk.MinCell[c]);
                _visited++;
                if (key >= below)
                {
                    return below;
                }

                if (key >= from && FindSlot(chunk.Slot[c]) == root)
                {
                    return key;
                }
            }
        }

        return below;
    }

    private int NewGroup(long key, int pending)
    {
        if (_groupCount == _groupParent.Length)
        {
            int capacity = _groupParent.Length * 2;
            Array.Resize(ref _groupParent, capacity);
            Array.Resize(ref _groupPending, capacity);
            Array.Resize(ref _groupMin, capacity);
            Array.Resize(ref _groupSlot, capacity);
        }

        int g = _groupCount++;
        _groupParent[g] = g;
        _groupPending[g] = pending;
        _groupMin[g] = key;
        return g;
    }

    private int FindGroup(int g)
    {
        while (_groupParent[g] != g)
        {
            _groupParent[g] = _groupParent[_groupParent[g]];
            g = _groupParent[g];
        }

        return g;
    }

    /// <summary>Links two group roots (the lower index stays root); returns the root.</summary>
    private int LinkGroups(int a, int b)
    {
        if (a == b)
        {
            return a;
        }

        if (b < a)
        {
            (a, b) = (b, a);
        }

        _groupParent[b] = a;
        _groupPending[a] += _groupPending[b];
        _groupMin[a] = Math.Min(_groupMin[a], _groupMin[b]);
        return a;
    }

    /// <summary>The fresh slot of a finished group root, allocated on first use.</summary>
    private int SlotOf(int group)
    {
        int slot = _groupSlot[group];
        if (slot < 0)
        {
            slot = _slotCount++;
            _slotParent[slot] = slot;
            _slotRank[slot] = 0;
            _slotMin[slot] = _groupMin[group];
            _groupSlot[group] = slot;
        }

        return slot;
    }

    /// <summary>The root of a slot, halving the path (upkeep only, on the Tick thread).</summary>
    private int FindSlot(int slot)
    {
        while (_slotParent[slot] != slot)
        {
            _slotParent[slot] = _slotParent[_slotParent[slot]];
            slot = _slotParent[slot];
        }

        return slot;
    }

    /// <summary>The root of a slot without writing (reads may run on any thread).</summary>
    private int RootOf(int slot)
    {
        int parent;
        while ((parent = _slotParent[slot]) != slot)
        {
            slot = parent;
        }

        return slot;
    }

    /// <summary>Unites two slot roots by rank; the root keeps the smaller id.</summary>
    private int LinkSlots(int a, int b)
    {
        if (a == b)
        {
            return a;
        }

        if (_slotRank[a] < _slotRank[b])
        {
            (a, b) = (b, a);
        }
        else if (_slotRank[a] == _slotRank[b])
        {
            _slotRank[a]++;
        }

        _slotParent[b] = a;
        _slotMin[a] = Math.Min(_slotMin[a], _slotMin[b]);
        return a;
    }

    /// <summary>Labels the whole graph from scratch: one dense slot per region.</summary>
    private void LabelAll()
    {
        int nodes = 0;
        for (int ordinal = 0; ordinal < _chunks.Length; ordinal++)
        {
            nodes += _chunks[ordinal].Count;
        }

        // Room for twice the nodes: incremental updates spend slots until the next compaction.
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, nodes * 2));
        if (_slotParent.Length < capacity)
        {
            _slotParent = new int[capacity];
            _slotRank = new byte[capacity];
            _slotMin = new long[capacity];
        }

        // An update has at most one group per node: size them now, not on a later Tick.
        if (_groupParent.Length < capacity / 2)
        {
            _groupParent = new int[capacity / 2];
            _groupPending = new int[capacity / 2];
            _groupMin = new long[capacity / 2];
            _groupSlot = new int[capacity / 2];
        }

        _slotCount = 0;
        _stamp++;
        for (int ordinal = 0; ordinal < _chunks.Length; ordinal++)
        {
            var chunk = _chunks[ordinal];
            for (int c = 0; c < chunk.Count; c++)
            {
                if (chunk.Visit[c] == _stamp)
                {
                    continue;
                }

                // Ascending (ordinal, component) is ascending key: the first unvisited node is its region's minimum.
                int slot = _slotCount++;
                _slotParent[slot] = slot;
                _slotRank[slot] = 0;
                _slotMin[slot] = Key(ordinal, chunk.MinCell[c]);
                int tail = Search(ordinal, c);
                for (int i = 0; i < tail; i++)
                {
                    long node = _queue[i];
                    _chunks[(int)(node >> CompBits)].Slot[(int)(node & ((1 << CompBits) - 1))] = slot;
                }
            }
        }
    }

    /// <summary>Marks and enqueues the component of a node; returns the member count (members in <see cref="_queue"/>).</summary>
    private int Search(int ordinal, int comp)
    {
        int head = 0;
        int tail = 0;
        _chunks[ordinal].Visit[comp] = _stamp;
        Push(ref tail, ((long)ordinal << CompBits) | (uint)comp);
        while (head < tail)
        {
            long node = _queue[head++];
            int o = (int)(node >> CompBits);
            int c = (int)(node & ((1 << CompBits) - 1));
            var chunk = _chunks[o];
            int end = chunk.EdgeStart[c + 1];
            for (int e = chunk.EdgeStart[c]; e < end; e++)
            {
                int edge = chunk.Edges[e];
                int n = _neighbours[(o * 6) + ((edge >> 12) & 7)];
                int their = edge & 0xFFF;
                var other = _chunks[n];
                if (other.Visit[their] != _stamp)
                {
                    other.Visit[their] = _stamp;
                    Push(ref tail, ((long)n << CompBits) | (uint)their);
                }
            }
        }

        _visited += tail;
        return tail;
    }

    private void Push(ref int tail, long node)
    {
        if (tail == _queue.Length)
        {
            Array.Resize(ref _queue, _queue.Length * 2);
        }

        _queue[tail++] = node;
    }

    private static long Key(int ordinal, int minCell) => ((long)ordinal << 12) | (uint)minCell;

    private static void Append(ref int[] array, ref int count, int value)
    {
        if (count == array.Length)
        {
            Array.Resize(ref array, array.Length * 2);
        }

        array[count++] = value;
    }

    private static void EnsureCapacity(ref int[] array, int count)
    {
        if (array.Length < count)
        {
            Array.Resize(ref array, (int)BitOperations.RoundUpToPowerOf2((uint)count));
        }
    }

    private void Run(ChunkJob.Mode mode, int[] ordinals, int count, bool parallel)
    {
        _job.Set(mode, ordinals);
        if (parallel && count > 1)
        {
            _world.RunParallel(count, _job);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                _job.Execute(i);
            }
        }
    }

    // ---- per-chunk work (runs on workers, keyed by chunk) ----

    private void Evaluate(int ordinal, bool force)
    {
        var chunk = _chunks[ordinal];
        if (_fields.ResidencyOf(ordinal) == ChunkResidency.Unloaded && _summaryValid[ordinal])
        {
            // Spec section 5.3: an Unloaded chunk keeps its pathfinding summary. Its cells are gone (the storage
            // holds the initial value), so the bits evaluated while it was resident stay as they are.
            chunk.Passable.CopyTo(chunk.NextPassable.AsSpan());
            if (chunk.Up is not null)
            {
                chunk.Up.CopyTo(chunk.NextUp!.AsSpan());
            }

            chunk.PendingChanged = force;
            return;
        }

        _summaryValid[ordinal] = true;
        var passable = chunk.NextPassable.AsSpan();
        passable.Clear();
        _definition.Rule(new PassabilityInput(_fields, ordinal, _fields.ChunkAt(ordinal)), passable);
        var mask = _inWorld[ordinal];
        if (mask is not null)
        {
            for (int w = 0; w < Words; w++)
            {
                passable[w] &= mask[w];
            }
        }

        bool changed = force || !passable.SequenceEqual(chunk.Passable);
        if (chunk.NextUp is not null)
        {
            var up = chunk.NextUp.AsSpan();
            up.Clear();
            _definition.Vertical!(new PassabilityInput(_fields, ordinal, _fields.ChunkAt(ordinal)), up);
            for (int w = 0; w < Words; w++)
            {
                up[w] &= passable[w];
            }

            changed |= !up.SequenceEqual(chunk.Up);
        }

        chunk.PendingChanged = changed;
    }

    private void Relabel(int ordinal)
    {
        var chunk = _chunks[ordinal];
        (chunk.Passable, chunk.NextPassable) = (chunk.NextPassable, chunk.Passable);
        if (chunk.NextUp is not null)
        {
            (chunk.Up, chunk.NextUp) = (chunk.NextUp, chunk.Up);
        }

        chunk.PendingChanged = false;
        chunk.Version++;
        var passable = chunk.Passable;
        bool any = false;
        for (int w = 0; w < Words; w++)
        {
            any |= passable[w] != 0;
        }

        if (!any)
        {
            chunk.Count = 0;
            chunk.EdgeCount = 0;
            return;
        }

        chunk.Labels ??= new ushort[FieldChunk.CellCount];
        var labels = chunk.Labels.AsSpan();
        labels.Clear();
        var up = chunk.Up ?? passable;
        Span<ushort> stack = stackalloc ushort[FieldChunk.CellCount];
        int count = 0;
        for (int w = 0; w < Words; w++)
        {
            ulong bits = passable[w];
            while (bits != 0)
            {
                int cell = (w << 6) | BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (labels[cell] != 0)
                {
                    continue;
                }

                count++;
                chunk.EnsureComponents(count);
                chunk.MinCell[count - 1] = (ushort)cell;
                var label = (ushort)count;
                labels[cell] = label;
                int top = 0;
                stack[top++] = (ushort)cell;
                while (top > 0)
                {
                    int c = stack[--top];
                    int x = c & 15;
                    int y = (c >> 4) & 15;
                    int z = c >> 8;
                    if (x > 0)
                    {
                        Visit(c - 1, passable, labels, label, stack, ref top);
                    }

                    if (x < 15)
                    {
                        Visit(c + 1, passable, labels, label, stack, ref top);
                    }

                    if (y > 0)
                    {
                        Visit(c - 16, passable, labels, label, stack, ref top);
                    }

                    if (y < 15)
                    {
                        Visit(c + 16, passable, labels, label, stack, ref top);
                    }

                    if (z < 15 && Bit(up, c))
                    {
                        Visit(c + 256, passable, labels, label, stack, ref top);
                    }

                    if (z > 0 && Bit(up, c - 256))
                    {
                        Visit(c - 256, passable, labels, label, stack, ref top);
                    }
                }
            }
        }

        chunk.Count = count;
        for (int c = 0; c < count; c++)
        {
            chunk.Slot[c] = -1;
        }
    }

    private static void Visit(int cell, ulong[] passable, Span<ushort> labels, ushort label, Span<ushort> stack, ref int top)
    {
        if (labels[cell] == 0 && Bit(passable, cell))
        {
            labels[cell] = label;
            stack[top++] = (ushort)cell;
        }
    }

    private static bool Bit(ulong[] bits, int cell) => (bits[cell >> 6] & (1UL << (cell & 63))) != 0;

    /// <summary>Rebuilds a chunk's edge list over all six faces from its and its neighbours' labels.</summary>
    private void BuildEdges(int ordinal)
    {
        var chunk = _chunks[ordinal];
        chunk.EdgeCount = 0;
        if (chunk.Count == 0)
        {
            return;
        }

        var mine = chunk.Labels!;
        for (int d = 0; d < 6; d++)
        {
            int n = _neighbours[(ordinal * 6) + d];
            if (n < 0 || _chunks[n].Count == 0)
            {
                continue;
            }

            var other = _chunks[n];
            var theirs = other.Labels!;
            for (int a = 0; a < 16; a++)
            {
                for (int b = 0; b < 16; b++)
                {
                    int my;
                    int their;
                    bool vertical = true;
                    switch (d)
                    {
                        case 0: my = FieldChunk.CellIndex(0, a, b); their = FieldChunk.CellIndex(15, a, b); break;
                        case 1: my = FieldChunk.CellIndex(15, a, b); their = FieldChunk.CellIndex(0, a, b); break;
                        case 2: my = FieldChunk.CellIndex(a, 0, b); their = FieldChunk.CellIndex(a, 15, b); break;
                        case 3: my = FieldChunk.CellIndex(a, 15, b); their = FieldChunk.CellIndex(a, 0, b); break;
                        case 4:
                            my = FieldChunk.CellIndex(a, b, 0);
                            their = FieldChunk.CellIndex(a, b, 15);
                            vertical = Bit(other.Up ?? other.Passable, their);
                            break;
                        default:
                            my = FieldChunk.CellIndex(a, b, 15);
                            their = FieldChunk.CellIndex(a, b, 0);
                            vertical = Bit(chunk.Up ?? chunk.Passable, my);
                            break;
                    }

                    int la = mine[my];
                    int lb = theirs[their];
                    if (la != 0 && lb != 0 && vertical)
                    {
                        chunk.AddEdge(((la - 1) << 15) | (d << 12) | (lb - 1));
                    }
                }
            }
        }

        chunk.FinishEdges();
    }

    /// <summary>Per-chunk state of one class: passable and vertical bits, local labels, components and edges.</summary>
    private sealed class ChunkRegions
    {
        public ChunkRegions(bool vertical)
        {
            if (vertical)
            {
                Up = new ulong[Words];
                NextUp = new ulong[Words];
            }
        }

        public ulong[] Passable = new ulong[Words];
        public ulong[] NextPassable = new ulong[Words];
        public ulong[]? Up;
        public ulong[]? NextUp;
        public bool PendingChanged;
        public int Version;
        public ushort[]? Labels;
        public int Count;
        public ushort[] MinCell = [];
        public int[] Slot = [];
        public int[] Visit = [];
        public int[] Group = [];
        public int[] Edges = [];
        public int EdgeCount;
        public int[] EdgeStart = [0];

        public void EnsureComponents(int count)
        {
            if (MinCell.Length >= count)
            {
                return;
            }

            int capacity = Math.Max(8, (int)BitOperations.RoundUpToPowerOf2((uint)count));
            Array.Resize(ref MinCell, capacity);
            Array.Resize(ref Slot, capacity);
            Array.Resize(ref Visit, capacity);
            Array.Resize(ref Group, capacity);
            Array.Resize(ref EdgeStart, capacity + 1);
        }

        public void AddEdge(int edge)
        {
            if (EdgeCount == Edges.Length)
            {
                Array.Resize(ref Edges, Math.Max(64, Edges.Length * 2));
            }

            Edges[EdgeCount++] = edge;
        }

        /// <summary>Sorts and dedupes the edges (grouped by own component) and fills <see cref="EdgeStart"/>.</summary>
        public void FinishEdges()
        {
            var edges = Edges.AsSpan(0, EdgeCount);
            edges.Sort();
            int w = 0;
            for (int i = 0; i < edges.Length; i++)
            {
                if (w == 0 || edges[i] != edges[w - 1])
                {
                    edges[w++] = edges[i];
                }
            }

            EdgeCount = w;
            int e = 0;
            for (int c = 0; c <= Count; c++)
            {
                while (e < w && (edges[e] >> 15) < c)
                {
                    e++;
                }

                EdgeStart[c] = e;
            }
        }
    }

    private sealed class ChunkJob(RegionMap map) : IIndexedJob
    {
        public enum Mode
        {
            Evaluate,
            EvaluateAll,
            Relabel,
            Edges,
        }

        private Mode _mode;
        private int[] _ordinals = [];

        public void Set(Mode mode, int[] ordinals)
        {
            _mode = mode;
            _ordinals = ordinals;
        }

        public void Execute(int index)
        {
            int ordinal = _ordinals[index];
            switch (_mode)
            {
                case Mode.Evaluate: map.Evaluate(ordinal, force: false); break;
                case Mode.EvaluateAll: map.Evaluate(ordinal, force: true); break;
                case Mode.Relabel: map.Relabel(ordinal); break;
                default: map.BuildEdges(ordinal); break;
            }
        }
    }
}
