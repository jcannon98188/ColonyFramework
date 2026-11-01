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
/// for them and their face neighbours, and only the regions that touched them are relabelled by a search over
/// the chunk graph (merges and splits alike; untouched regions keep their ids). When at least
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
    private long[] _affected = new long[64];
    private int _affectedCount;
    private long[] _queue = new long[256];
    private int _stamp;
    private uint _syncedVersion;

    private long _builds;
    private long _updates;
    private long _incremental;
    private long _full;
    private long _evaluated;
    private long _relabelled;
    private long _visited;

    internal RegionMap(World world, FieldSet fields, MovementClassDefinition definition, MovementClass movementClass, PathfindingOptions options)
    {
        _world = world;
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
        return label == 0 ? RegionId.None : new RegionId(chunk.Region[label - 1]);
    }

    /// <summary>Local components of a chunk (nodes of the chunk graph); for diagnostics and tests.</summary>
    public int ComponentCount(int ordinal) => _chunks[ordinal].Count;

    /// <summary>The HPA* chunk graph of this class (TASK-20), built lazily per chunk from the labels.</summary>
    internal ChunkGraph Graph => _graph ??= new ChunkGraph(this);

    internal FieldSet Fields => _fields;

    internal int ChunkCount => _chunks.Length;

    /// <summary>The face neighbour of a chunk (direction 0..5 = -x, +x, -y, +y, -z, +z), -1 outside the World.</summary>
    internal int NeighbourOf(int ordinal, int direction) => _neighbours[(ordinal * 6) + direction];

    /// <summary>Bumps every time the chunk's local labels are rebuilt; consumers rebuild derived data when it moves.</summary>
    internal int LabelVersionOf(int ordinal) => _chunks[ordinal].Version;

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

        // Old regions of the changed chunks, captured before their labels go.
        _affectedCount = 0;
        if (!full)
        {
            for (int i = 0; i < _changedCount; i++)
            {
                var chunk = _chunks[_changed[i]];
                for (int c = 0; c < chunk.Count; c++)
                {
                    Append(ref _affected, ref _affectedCount, chunk.Region[c]);
                }
            }
        }

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
        Run(ChunkJob.Mode.Edges, _touched, _touchedCount, parallel);

        if (full)
        {
            _full++;
            LabelAll();
            return;
        }

        _incremental++;

        // Regions outside the changed chunks that now touch them may merge.
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
                if (_chunkMark[n] == changedStamp)
                {
                    Append(ref _affected, ref _affectedCount, chunk.Region[edge >> 15]);
                }
            }
        }

        Array.Sort(_affected, 0, _affectedCount);
        _affectedCount = Unique(_affected.AsSpan(0, _affectedCount));
        var affected = _affected.AsSpan(0, _affectedCount);

        _stamp++;
        for (int i = 0; i < _touchedCount; i++)
        {
            int ordinal = _touched[i];
            var chunk = _chunks[ordinal];
            bool changed = _chunkMark[ordinal] == changedStamp;
            for (int c = 0; c < chunk.Count; c++)
            {
                if (chunk.Visit[c] == _stamp)
                {
                    continue;
                }

                // Unvisited nodes still carry their old region id.
                if (changed || affected.BinarySearch(chunk.Region[c]) >= 0)
                {
                    RelabelComponent(ordinal, c);
                }
            }
        }
    }

    /// <summary>Breadth-first search from one node; every member gets the smallest key found.</summary>
    private void RelabelComponent(int ordinal, int comp)
    {
        int tail = Search(ordinal, comp);
        long min = long.MaxValue;
        for (int i = 0; i < tail; i++)
        {
            long node = _queue[i];
            int o = (int)(node >> CompBits);
            int c = (int)(node & ((1 << CompBits) - 1));
            long key = Key(o, _chunks[o].MinCell[c]);
            min = Math.Min(min, key);
        }

        for (int i = 0; i < tail; i++)
        {
            long node = _queue[i];
            _chunks[(int)(node >> CompBits)].Region[(int)(node & ((1 << CompBits) - 1))] = min;
        }
    }

    private void LabelAll()
    {
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
                long key = Key(ordinal, chunk.MinCell[c]);
                int tail = Search(ordinal, c);
                for (int i = 0; i < tail; i++)
                {
                    long node = _queue[i];
                    _chunks[(int)(node >> CompBits)].Region[(int)(node & ((1 << CompBits) - 1))] = key;
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

    private static int Unique(Span<long> sorted)
    {
        if (sorted.IsEmpty)
        {
            return 0;
        }

        int w = 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] != sorted[w - 1])
            {
                sorted[w++] = sorted[i];
            }
        }

        return w;
    }

    private static void Append(ref int[] array, ref int count, int value)
    {
        if (count == array.Length)
        {
            Array.Resize(ref array, array.Length * 2);
        }

        array[count++] = value;
    }

    private static void Append(ref long[] array, ref int count, long value)
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
            chunk.Region[c] = -1;
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
        public long[] Region = [];
        public int[] Visit = [];
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
            Array.Resize(ref Region, capacity);
            Array.Resize(ref Visit, capacity);
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
