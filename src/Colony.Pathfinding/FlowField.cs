using Colony.Fields;

namespace Colony.Pathfinding;

/// <summary>Handle of a flow Field in its World; <see cref="PathfindingService.FlowField(FlowFieldId)"/> resolves it (null once evicted).</summary>
public readonly record struct FlowFieldId(int Id)
{
    public override string ToString() => $"FlowField({Id})";
}

/// <summary>An axis-aligned box of chunks, both corners inclusive.</summary>
public readonly record struct ChunkBox(ChunkCoord Min, ChunkCoord Max)
{
    public int SizeX => Max.X - Min.X + 1;

    public int SizeY => Max.Y - Min.Y + 1;

    public int SizeZ => Max.Z - Min.Z + 1;

    public int ChunkCount => SizeX * SizeY * SizeZ;

    public bool Contains(ChunkCoord chunk)
        => chunk.X >= Min.X && chunk.X <= Max.X && chunk.Y >= Min.Y && chunk.Y <= Max.Y && chunk.Z >= Min.Z && chunk.Z <= Max.Z;

    /// <summary>Dense index of a chunk inside the box (x fastest); the caller checks <see cref="Contains"/>.</summary>
    public int SlotOf(ChunkCoord chunk) => (chunk.X - Min.X) + (SizeX * ((chunk.Y - Min.Y) + (SizeY * (chunk.Z - Min.Z))));

    public ChunkCoord ChunkAt(int slot) => new(Min.X + (slot % SizeX), Min.Y + (slot / SizeX % SizeY), Min.Z + (slot / (SizeX * SizeY)));
}

/// <summary>What <see cref="FlowField.TryStep"/> found for a tile.</summary>
public enum FlowStepStatus : byte
{
    /// <summary>A face move toward the nearest goal is available.</summary>
    Step,

    /// <summary>The tile is a goal.</summary>
    AtGoal,

    /// <summary>The tile is impassable or no goal is reachable from it inside the bound.</summary>
    Unreachable,

    /// <summary>The tile lies outside the Field's bound: route to the bound with HPA* (<see cref="PathfindingService.TryNextStep(FlowField, TilePos, out TilePos)"/> does both).</summary>
    OutsideBound,

    /// <summary>The Field was evicted under the memory budget; ask the service for it again.</summary>
    Evicted,
}

/// <summary>
/// A bounded flow Field (spec section 9.1): the breadth-first distance to the nearest of a set of goal tiles for
/// every tile of a chunk box (the goal chunks plus <see cref="RadiusChunks"/> in every direction, clipped to
/// the World), for one movement class. Shared goals (a stockpile, a door, a fire) pay one build for any number
/// of movers; a mover inside the bound descends the distances one face move at a time, a mover outside it
/// takes HPA* to the bound. Built lazily by <see cref="PathfindingService.FlowField(ReadOnlySpan{TilePos}, MovementClass, int)"/>,
/// kept under the memory budget by least-recently-used eviction and repaired in layers when the terrain
/// changes (per-chunk dirty marks, a per-Tick budget, a staleness bound, rebuild when enough chunks changed).
/// </summary>
/// <remarks>
/// Distances are 16-bit: tiles farther than 65534 moves from every goal read as unreached. Reads
/// (<see cref="DistanceTo"/>, <see cref="TryStep"/>) are allocation free and safe from parallel chunk jobs:
/// the Field only changes at sync points on the Tick thread (the Begin repair pass, host repairs). Between a
/// terrain change and the repair, reads see the old distances but never step into a tile that is impassable
/// now. Nothing here is hashed or saved: a loaded World has no flow Fields until they are asked for again.
/// </remarks>
public sealed class FlowField
{
    internal const ushort Unreached = ushort.MaxValue;
    private const int FaceCells = FieldChunk.Size * FieldChunk.Size;

    private readonly RegionMap _map;
    private readonly int[] _ordinals;
    private readonly int[] _neighbourSlots;
    private readonly ushort[][] _distance;
    private readonly int[] _builtVersion;
    private readonly byte[] _dirty;
    private readonly TilePos[] _goals;
    private int _used;

    internal FlowField(RegionMap map, FlowFieldId id, ulong key, TilePos[] goals, int radiusChunks, ChunkBox bounds, long sequence, long tick)
    {
        _map = map;
        Id = id;
        Key = key;
        _goals = goals;
        RadiusChunks = radiusChunks;
        Bounds = bounds;
        Sequence = sequence;
        LastUsedTick = tick;
        DirtySinceTick = -1;
        int count = bounds.ChunkCount;
        _ordinals = new int[count];
        _neighbourSlots = new int[count * 6];
        _distance = new ushort[count][];
        _builtVersion = new int[count];
        _dirty = new byte[count];
        var fields = map.Fields;
        for (int slot = 0; slot < count; slot++)
        {
            var chunk = bounds.ChunkAt(slot);
            _ordinals[slot] = fields.OrdinalOf(chunk);
            _distance[slot] = new ushort[FieldChunk.CellCount];
            _neighbourSlots[(slot * 6) + 0] = SlotOrMinusOne(new ChunkCoord(chunk.X - 1, chunk.Y, chunk.Z));
            _neighbourSlots[(slot * 6) + 1] = SlotOrMinusOne(new ChunkCoord(chunk.X + 1, chunk.Y, chunk.Z));
            _neighbourSlots[(slot * 6) + 2] = SlotOrMinusOne(new ChunkCoord(chunk.X, chunk.Y - 1, chunk.Z));
            _neighbourSlots[(slot * 6) + 3] = SlotOrMinusOne(new ChunkCoord(chunk.X, chunk.Y + 1, chunk.Z));
            _neighbourSlots[(slot * 6) + 4] = SlotOrMinusOne(new ChunkCoord(chunk.X, chunk.Y, chunk.Z - 1));
            _neighbourSlots[(slot * 6) + 5] = SlotOrMinusOne(new ChunkCoord(chunk.X, chunk.Y, chunk.Z + 1));
        }

        Bytes = BytesFor(count, goals.Length);
    }

    public FlowFieldId Id { get; }

    /// <summary>Identity of the goal set: class, radius and the canonical goal list.</summary>
    internal ulong Key { get; }

    public MovementClass MovementClass => _map.Class;

    public int RadiusChunks { get; }

    /// <summary>The goals in canonical order (ascending z, y, x; duplicates dropped).</summary>
    public ReadOnlySpan<TilePos> Goals => _goals;

    public ChunkBox Bounds { get; }

    public int ChunkCount => _ordinals.Length;

    /// <summary>Memory this Field holds, as counted against <see cref="PathfindingOptions.FlowFieldMemoryBudgetBytes"/>.</summary>
    public long Bytes { get; }

    /// <summary>Set when the Field was evicted under the memory budget; its reads answer <see cref="FlowStepStatus.Evicted"/> / -1.</summary>
    public bool IsEvicted { get; private set; }

    /// <summary>Chunks whose passability changed since the Field was last brought up to date.</summary>
    public int DirtyChunkCount { get; private set; }

    public bool IsDirty => DirtyChunkCount > 0;

    /// <summary>The Tick at which the Field first became dirty since its last repair, -1 while clean.</summary>
    public long DirtySinceTick { get; private set; }

    /// <summary>The last Tick the Field was asked for or read in; eviction takes the least recently used first.</summary>
    public long LastUsedTick { get; internal set; }

    /// <summary>Creation order, the tie breaker of the eviction order.</summary>
    internal long Sequence { get; }

    internal RegionMap Map => _map;

    /// <summary>The memory a Field over <paramref name="chunks"/> chunks with <paramref name="goals"/> goals holds.</summary>
    internal static long BytesFor(int chunks, int goals)
        => ((long)chunks * ((FieldChunk.CellCount * sizeof(ushort)) + (6 * sizeof(int)) + sizeof(int) + sizeof(int) + 1 + 32)) + ((long)goals * 12) + 160;

    /// <summary>Breadth-first distance in face moves from <paramref name="tile"/> to the nearest goal; -1 outside the bound, impassable, unreached or evicted.</summary>
    public int DistanceTo(TilePos tile)
    {
        if (IsEvicted || !_map.Fields.Size.Contains(tile) || !Bounds.Contains(tile.Chunk))
        {
            return -1;
        }

        ushort d = _distance[Bounds.SlotOf(tile.Chunk)][tile.CellIndex];
        return d == Unreached ? -1 : d;
    }

    /// <summary>
    /// The face move from <paramref name="from"/> that descends the distances fastest (ties by neighbour index:
    /// -x, +x, -y, +y, -z, +z). Allocation free, safe from parallel chunk jobs. Never moves into a tile the class
    /// cannot stand on now, even while the Field is dirty.
    /// </summary>
    public FlowStepStatus TryStep(TilePos from, out TilePos next)
    {
        next = from;
        if (IsEvicted)
        {
            return FlowStepStatus.Evicted;
        }

        if (!_map.Fields.Size.Contains(from) || !Bounds.Contains(from.Chunk))
        {
            return FlowStepStatus.OutsideBound;
        }

        Volatile.Write(ref _used, 1);
        int slot = Bounds.SlotOf(from.Chunk);
        int cell = from.CellIndex;
        if (!ChunkGraph.Bit(_map.PassableOf(_ordinals[slot]), cell))
        {
            return FlowStepStatus.Unreachable;
        }

        ushort here = _distance[slot][cell];
        if (here == Unreached)
        {
            return FlowStepStatus.Unreachable;
        }

        if (here == 0)
        {
            return FlowStepStatus.AtGoal;
        }

        int best = -1;
        int bestSlot = 0;
        int bestCell = 0;
        ushort bestDistance = here;
        for (int d = 0; d < 6; d++)
        {
            if (TryNeighbour(slot, cell, d, out int nSlot, out int nCell))
            {
                ushort candidate = _distance[nSlot][nCell];
                if (candidate < bestDistance)
                {
                    bestDistance = candidate;
                    best = d;
                    bestSlot = nSlot;
                    bestCell = nCell;
                }
            }
        }

        if (best < 0)
        {
            return FlowStepStatus.Unreachable;
        }

        var origin = Bounds.ChunkAt(bestSlot).Origin;
        var (x, y, z) = FieldChunk.CellCoordinates(bestCell);
        next = new TilePos(origin.X + x, origin.Y + y, origin.Z + z);
        return FlowStepStatus.Step;
    }

    /// <summary>
    /// The passable face neighbour of a cell in <paramref name="direction"/> (0..5 = -x, +x, -y, +y, -z, +z) inside
    /// the bound, honouring the class's vertical rule; false when there is none.
    /// </summary>
    internal bool TryNeighbour(int slot, int cell, int direction, out int nSlot, out int nCell)
    {
        int x = cell & FieldChunk.Mask;
        int y = (cell >> FieldChunk.Shift) & FieldChunk.Mask;
        int z = cell >> (2 * FieldChunk.Shift);
        nSlot = slot;
        switch (direction)
        {
            case 0:
                nCell = x > 0 ? cell - 1 : Cross(slot, 0, cell + FieldChunk.Mask, out nSlot);
                break;
            case 1:
                nCell = x < FieldChunk.Mask ? cell + 1 : Cross(slot, 1, cell - FieldChunk.Mask, out nSlot);
                break;
            case 2:
                nCell = y > 0 ? cell - FieldChunk.Size : Cross(slot, 2, cell + (FieldChunk.Mask * FieldChunk.Size), out nSlot);
                break;
            case 3:
                nCell = y < FieldChunk.Mask ? cell + FieldChunk.Size : Cross(slot, 3, cell - (FieldChunk.Mask * FieldChunk.Size), out nSlot);
                break;
            case 4:
                nCell = z > 0 ? cell - FaceCells : Cross(slot, 4, cell + (FieldChunk.Mask * FaceCells), out nSlot);
                break;
            default:
                nCell = z < FieldChunk.Mask ? cell + FaceCells : Cross(slot, 5, cell - (FieldChunk.Mask * FaceCells), out nSlot);
                break;
        }

        if (nSlot < 0)
        {
            return false;
        }

        int nOrdinal = _ordinals[nSlot];
        if (!ChunkGraph.Bit(_map.PassableOf(nOrdinal), nCell))
        {
            return false;
        }

        // Climbing: the lower tile of the pair must allow it (the class's vertical rule, or passability without one).
        return direction switch
        {
            5 => ChunkGraph.Bit(_map.UpOf(_ordinals[slot]), cell),
            4 => ChunkGraph.Bit(_map.UpOf(nOrdinal), nCell),
            _ => true,
        };
    }

    private int Cross(int slot, int direction, int mirroredCell, out int nSlot)
    {
        nSlot = _neighbourSlots[(slot * 6) + direction];
        return mirroredCell;
    }

    private int SlotOrMinusOne(ChunkCoord chunk) => Bounds.Contains(chunk) ? Bounds.SlotOf(chunk) : -1;

    // ---- store-side access (Tick thread, under the service's flow lock) ----

    internal int OrdinalOf(int slot) => _ordinals[slot];

    /// <summary>The face neighbour slot inside the bound, -1 outside it.</summary>
    internal int NeighbourSlotOf(int slot, int direction) => _neighbourSlots[(slot * 6) + direction];

    internal ushort[] DistancesOf(int slot) => _distance[slot];

    internal bool IsDirtyChunk(int slot) => _dirty[slot] != 0;

    internal int BuiltVersionOf(int slot) => _builtVersion[slot];

    /// <summary>Records that the chunk's passability changed; returns true when it was clean before.</summary>
    internal bool MarkDirty(int slot, long tick)
    {
        if (_dirty[slot] != 0)
        {
            return false;
        }

        _dirty[slot] = 1;
        if (DirtyChunkCount++ == 0)
        {
            DirtySinceTick = tick;
        }

        return true;
    }

    /// <summary>Marks the whole bound up to date with the labels as they are now.</summary>
    internal void MarkClean()
    {
        for (int slot = 0; slot < _ordinals.Length; slot++)
        {
            _dirty[slot] = 0;
            _builtVersion[slot] = _map.LabelVersionOf(_ordinals[slot]);
        }

        DirtyChunkCount = 0;
        DirtySinceTick = -1;
    }

    internal void Evict() => IsEvicted = true;

    /// <summary>Whether a parallel read touched the Field since the last call (clears the flag).</summary>
    internal bool ConsumeUsed() => Interlocked.Exchange(ref _used, 0) != 0;
}
