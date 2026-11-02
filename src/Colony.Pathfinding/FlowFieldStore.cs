using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Colony.Fields;
using Colony.Hashing;

namespace Colony.Pathfinding;

/// <summary>Running totals of the World's flow Fields; diagnostics only, never hashed.</summary>
/// <param name="Built">Fields built from a new goal set.</param>
/// <param name="Evicted">Fields evicted under the memory budget.</param>
/// <param name="Rebuilds">Repairs that recomputed a whole Field (including the initial builds).</param>
/// <param name="IncrementalRepairs">Repairs that recomputed only the affected chunks.</param>
/// <param name="RepairWorkUnits">Cells popped by builds and repairs.</param>
/// <param name="StalenessForced">Repairs run past the per-Tick budget because the Field hit the staleness bound.</param>
/// <param name="RepairsDeferred">Field-Ticks a dirty Field was left for a later Tick because the budget was spent.</param>
/// <param name="Bytes">Memory the live Fields hold now.</param>
/// <param name="BudgetBytes">The configured budget.</param>
public readonly record struct FlowFieldCounters(
    long Built,
    long Evicted,
    long Rebuilds,
    long IncrementalRepairs,
    long RepairWorkUnits,
    long StalenessForced,
    long RepairsDeferred,
    long Bytes,
    long BudgetBytes);

/// <summary>
/// The World's flow Fields (spec section 9.1): lookup by goal set, least-recently-used eviction under the memory
/// budget, dirty marking from region upkeep and the layered repair pass. Everything runs on the Tick thread
/// under the service's flow lock; the Fields themselves are read from anywhere.
/// </summary>
internal sealed class FlowFieldStore
{
    /// <summary>Name of the saved section holding the Fields (absent when there are none).</summary>
    internal const string SectionName = "flows";

    private const ulong KeySeed = 0x464C_4F57UL; // "FLOW"
    private const int StackGoals = 256;

    private readonly PathfindingService _service;
    private readonly PathfindingOptions _options;
    private readonly List<FlowField> _fields = [];
    private readonly FlowBuilder _builder = new();
    private int[] _order = new int[16];
    private int _nextId;
    private long _sequence;
    private long _bytes;
    private long _built;
    private long _evicted;
    private long _rebuilds;
    private long _incremental;
    private long _workUnits;
    private long _forced;
    private long _deferred;
    private long _workUnitsThisTick;

    public FlowFieldStore(PathfindingService service, PathfindingOptions options)
    {
        _service = service;
        _options = options;
    }

    public int Count => _fields.Count;

    public long Bytes => _bytes;

    public FlowFieldCounters Counters => new(_built, _evicted, _rebuilds, _incremental, _workUnits, _forced, _deferred, _bytes, _options.FlowFieldMemoryBudgetBytes);

    /// <summary>Work units spent by the repair pass this Tick (read by the deliver System for its counters).</summary>
    public long WorkUnitsThisTick => _workUnitsThisTick;

    /// <summary>Dirty chunks over every live Field: the repair backlog.</summary>
    public int Backlog
    {
        get
        {
            int backlog = 0;
            foreach (var flow in _fields)
            {
                backlog += flow.DirtyChunkCount;
            }

            return backlog;
        }
    }

    public FlowField? Find(FlowFieldId id)
    {
        foreach (var field in _fields)
        {
            if (field.Id == id)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// The Field of a goal set, built now when none exists (evicting the least recently used ones to make room).
    /// In a Tick an existing one is touched (recency). Between Ticks an existing one is returned as it is: a host
    /// read (a renderer, an inspector) must not change what the next Tick sees (spec section 6), so it neither
    /// repairs the Field nor touches its recency; <see cref="Repair"/> is the explicit, simulation-affecting call.
    /// </summary>
    public FlowField GetOrCreate(ReadOnlySpan<TilePos> goals, RegionMap map, int radiusChunks, long tick, bool inTick)
    {
        Span<long> packed = goals.Length <= StackGoals ? stackalloc long[goals.Length] : new long[goals.Length];
        for (int i = 0; i < goals.Length; i++)
        {
            packed[i] = Pack(goals[i]);
        }

        packed.Sort();
        int unique = Unique(packed);
        packed = packed[..unique];
        ulong key = KeyOf(packed, map.Class.Id, radiusChunks);

        foreach (var field in _fields)
        {
            if (field.Key == key && field.MovementClass == map.Class && field.RadiusChunks == radiusChunks && SameGoals(field.Goals, packed))
            {
                if (inTick)
                {
                    field.LastUsedTick = tick;
                }

                return field;
            }
        }

        var canonical = new TilePos[unique];
        for (int i = 0; i < unique; i++)
        {
            canonical[i] = Unpack(packed[i]);
        }

        var bounds = BoundsOf(canonical, radiusChunks, map.Fields.Size);
        long bytes = FlowField.BytesFor(bounds.ChunkCount, canonical.Length);
        FoldUsed(inTick ? tick : tick - 1);
        while (_fields.Count > 0 && _bytes + bytes > _options.FlowFieldMemoryBudgetBytes)
        {
            Evict(LeastRecentlyUsed());
        }

        var created = new FlowField(map, new FlowFieldId(_nextId++), key, canonical, radiusChunks, bounds, _sequence++, tick, _service.ResidencyFunc);
        _fields.Add(created);
        _bytes += created.Bytes;
        _built++;
        _rebuilds++;
        _workUnits += _builder.Rebuild(created);
        created.MarkClean();
        return created;
    }

    /// <summary>Brings a Field up to date now, whatever the budget: rebuild or incremental by the dirty fraction.</summary>
    public void Repair(FlowField field)
    {
        if (!field.IsEvicted)
        {
            field.RefreshResidency(field.LastUsedTick);
        }

        if (!field.IsDirty || field.IsEvicted)
        {
            return;
        }

        int threshold = Math.Max(1, (int)(_options.FlowRebuildFraction * field.ChunkCount));
        long units;
        if (field.DirtyChunkCount >= threshold)
        {
            _rebuilds++;
            units = _builder.Rebuild(field);
        }
        else
        {
            units = _builder.RepairIncremental(field, threshold, out bool fellBackToRebuild);
            if (fellBackToRebuild)
            {
                _rebuilds++;
            }
            else
            {
                _incremental++;
            }
        }

        _workUnits += units;
        _workUnitsThisTick += units;
        field.MarkClean();
    }

    /// <summary>Marks the chunks a class relabelled dirty in every Field of that class.</summary>
    public void MarkDirty(RegionMap map, ReadOnlySpan<int> relabelled, long tick)
    {
        if (relabelled.IsEmpty)
        {
            return;
        }

        var fields = map.Fields;
        foreach (var field in _fields)
        {
            if (field.MovementClass != map.Class)
            {
                continue;
            }

            if (relabelled.Length >= field.ChunkCount)
            {
                for (int slot = 0; slot < field.ChunkCount; slot++)
                {
                    if (field.BuiltVersionOf(slot) != map.LabelVersionOf(field.OrdinalOf(slot)))
                    {
                        field.MarkDirty(slot, tick);
                    }
                }

                continue;
            }

            foreach (int ordinal in relabelled)
            {
                var chunk = fields.ChunkAt(ordinal);
                if (field.Bounds.Contains(chunk))
                {
                    field.MarkDirty(field.Bounds.SlotOf(chunk), tick);
                }
            }
        }
    }

    /// <summary>Start of Tick: fold the parallel-read flags of the previous Tick into the recency order.</summary>
    public void BeginTick(long tick)
    {
        _workUnitsThisTick = 0;
        FoldUsed(tick - 1);
    }

    /// <summary>Parallel reads only raise a flag (order independent); the Tick thread turns it into a recency stamp here.</summary>
    private void FoldUsed(long tick)
    {
        foreach (var field in _fields)
        {
            if (field.ConsumeUsed())
            {
                field.LastUsedTick = Math.Max(field.LastUsedTick, tick);
            }
        }
    }

    /// <summary>
    /// The Begin-Phase repair pass: dirty Fields past the staleness bound are repaired whatever the budget; the
    /// rest, most recently used first, while the per-Tick repair budget has room.
    /// </summary>
    public void RepairPass(long tick)
    {
        int count = _fields.Count;
        if (count == 0)
        {
            return;
        }

        if (_order.Length < count)
        {
            _order = new int[(int)BitOperations.RoundUpToPowerOf2((uint)count)];
        }

        // Insertion sort by (LastUsedTick descending, Sequence ascending): tiny lists, no allocation.
        for (int i = 0; i < count; i++)
        {
            int j = i;
            while (j > 0 && Before(_fields[i], _fields[_order[j - 1]]))
            {
                _order[j] = _order[j - 1];
                j--;
            }

            _order[j] = i;
        }

        // Chunks that were loaded or unloaded since the last pass (the Streaming Phase runs first) are dirty too.
        for (int i = 0; i < count; i++)
        {
            _fields[i].RefreshResidency(tick);
        }

        long budget = _options.FlowRepairBudgetPerTick;
        long used = 0;
        for (int i = 0; i < count; i++)
        {
            var field = _fields[_order[i]];
            if (!field.IsDirty)
            {
                continue;
            }

            bool forced = tick - field.DirtySinceTick >= _options.FlowStalenessTicks;
            if (!forced && used >= budget)
            {
                _deferred++;
                continue;
            }

            if (forced && used >= budget)
            {
                _forced++;
            }

            long before = _workUnitsThisTick;
            Repair(field);
            used += _workUnitsThisTick - before;
        }
    }

    /// <summary>Drops every Field (a load: Fields are derived state and are asked for again).</summary>
    public void Clear()
    {
        foreach (var field in _fields)
        {
            field.Evict();
        }

        _fields.Clear();
        _bytes = 0;
    }

    /// <summary>
    /// Writes the Fields Systems can observe (spec section 6, load-then-continue): ids, goal sets, recency, the
    /// parallel-read flags not folded yet, dirty and residency marks, and the old distances of dirty Fields.
    /// Clean Fields are rebuilt on load (a repair matches a rebuild). Nothing when there are no Fields.
    /// </summary>
    public void Save(ExtensionStateWriter writer)
    {
        if (_fields.Count == 0)
        {
            return;
        }

        long size = 16;
        foreach (var field in _fields)
        {
            size += 53 + (12L * field.Goals.Length) + (2L * field.ChunkCount);
            if (field.IsDirty)
            {
                size += (long)field.ChunkCount * FieldChunk.CellCount * sizeof(ushort);
            }
        }

        var span = new byte[size].AsSpan();
        int at = 0;
        WriteInt(span, ref at, _fields.Count);
        WriteInt(span, ref at, _nextId);
        WriteLong(span, ref at, _sequence);
        foreach (var field in _fields)
        {
            WriteInt(span, ref at, field.Id.Id);
            WriteInt(span, ref at, field.MovementClass.Id);
            WriteInt(span, ref at, field.RadiusChunks);
            WriteInt(span, ref at, field.Goals.Length);
            foreach (var goal in field.Goals)
            {
                WriteInt(span, ref at, goal.X);
                WriteInt(span, ref at, goal.Y);
                WriteInt(span, ref at, goal.Z);
            }

            WriteLong(span, ref at, field.Sequence);
            WriteLong(span, ref at, field.LastUsedTick);
            span[at++] = field.Used ? (byte)1 : (byte)0;
            WriteLong(span, ref at, field.DirtySinceTick);
            WriteInt(span, ref at, field.MissingSlot);
            WriteInt(span, ref at, field.ChunkCount);
            field.DirtyMarks.CopyTo(span[at..]);
            at += field.ChunkCount;
            field.UnloadedMarks.CopyTo(span[at..]);
            at += field.ChunkCount;
            if (field.IsDirty)
            {
                for (int slot = 0; slot < field.ChunkCount; slot++)
                {
                    foreach (ushort d in field.DistancesOf(slot))
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(span[at..], d);
                        at += 2;
                    }
                }
            }
        }

        writer.WriteSection(SectionName, span[..at]);
    }

    /// <summary>Restores the Fields of a save over the rebuilt region maps (<paramref name="regions"/> resolves a class id).</summary>
    public void Load(ReadOnlySpan<byte> payload, Func<int, RegionMap> regions, string extensionName)
    {
        Clear();
        int at = 0;
        int count = ReadInt(payload, ref at, extensionName);
        _nextId = ReadInt(payload, ref at, extensionName);
        _sequence = ReadLong(payload, ref at, extensionName);
        if (count < 0)
        {
            throw Corrupt(extensionName, "a negative Field count");
        }

        for (int i = 0; i < count; i++)
        {
            int id = ReadInt(payload, ref at, extensionName);
            var map = regions(ReadInt(payload, ref at, extensionName));
            int radius = ReadInt(payload, ref at, extensionName);
            int goalCount = ReadInt(payload, ref at, extensionName);
            if (goalCount <= 0 || goalCount > (payload.Length - at) / 12)
            {
                throw Corrupt(extensionName, $"a bad goal count at Field {i}");
            }

            var goals = new TilePos[goalCount];
            var packed = new long[goalCount];
            for (int g = 0; g < goalCount; g++)
            {
                int x = ReadInt(payload, ref at, extensionName);
                int y = ReadInt(payload, ref at, extensionName);
                int z = ReadInt(payload, ref at, extensionName);
                goals[g] = new TilePos(x, y, z);
                packed[g] = Pack(goals[g]);
            }

            long sequence = ReadLong(payload, ref at, extensionName);
            long lastUsed = ReadLong(payload, ref at, extensionName);
            bool used = ReadByte(payload, ref at, extensionName) != 0;
            long dirtySince = ReadLong(payload, ref at, extensionName);
            int missingSlot = ReadInt(payload, ref at, extensionName);
            int chunks = ReadInt(payload, ref at, extensionName);
            var bounds = BoundsOf(goals, radius, map.Fields.Size);
            if (chunks != bounds.ChunkCount || payload.Length - at < 2L * chunks)
            {
                throw Corrupt(extensionName, $"Field {i} spanning {chunks} chunks where its goals and radius give {bounds.ChunkCount}");
            }

            var dirty = payload.Slice(at, chunks);
            at += chunks;
            var unloaded = payload.Slice(at, chunks);
            at += chunks;
            var field = new FlowField(map, new FlowFieldId(id), KeyOf(packed, map.Class.Id, radius), goals, radius, bounds, sequence, lastUsed, _service.ResidencyFunc);
            field.RestoreUnloaded(unloaded);
            field.RestoreUsed(used);
            if (dirty.ContainsAnyExcept((byte)0))
            {
                if (payload.Length - at < (long)chunks * FieldChunk.CellCount * sizeof(ushort))
                {
                    throw Corrupt(extensionName, $"truncated distances in Field {i}");
                }

                for (int slot = 0; slot < chunks; slot++)
                {
                    var distances = field.DistancesOf(slot);
                    for (int c = 0; c < distances.Length; c++)
                    {
                        distances[c] = BinaryPrimitives.ReadUInt16LittleEndian(payload[at..]);
                        at += 2;
                    }
                }

                field.RestoreDirty(dirty, dirtySince, missingSlot);
            }
            else
            {
                _ = _builder.Rebuild(field);
                field.MarkClean();
            }

            _fields.Add(field);
            _bytes += field.Bytes;
        }

        if (at != payload.Length)
        {
            throw Corrupt(extensionName, $"{payload.Length - at} trailing bytes");
        }
    }

    private static ulong KeyOf(ReadOnlySpan<long> packed, int classId, int radiusChunks)
    {
        ulong key = Hash64.Combine(Hash64.Combine(KeySeed, (ulong)classId), (ulong)radiusChunks);
        return Hash64.Combine(key, Hash64.Compute(MemoryMarshal.AsBytes(packed), KeySeed));
    }

    private static void WriteInt(Span<byte> span, ref int at, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], value);
        at += 4;
    }

    private static void WriteLong(Span<byte> span, ref int at, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(span[at..], value);
        at += 8;
    }

    private static int ReadInt(ReadOnlySpan<byte> payload, ref int at, string extensionName)
    {
        if (payload.Length - at < 4)
        {
            throw Corrupt(extensionName, "truncated data");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(payload[at..]);
        at += 4;
        return value;
    }

    private static long ReadLong(ReadOnlySpan<byte> payload, ref int at, string extensionName)
    {
        if (payload.Length - at < 8)
        {
            throw Corrupt(extensionName, "truncated data");
        }

        long value = BinaryPrimitives.ReadInt64LittleEndian(payload[at..]);
        at += 8;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> payload, ref int at, string extensionName)
    {
        if (payload.Length - at < 1)
        {
            throw Corrupt(extensionName, "truncated data");
        }

        return payload[at++];
    }

    private static InvalidDataException Corrupt(string extensionName, string what)
        => new($"Extension '{extensionName}': section '{SectionName}' holds {what}.");

    private static bool Before(FlowField a, FlowField b)
        => a.LastUsedTick != b.LastUsedTick ? a.LastUsedTick > b.LastUsedTick : a.Sequence < b.Sequence;

    private int LeastRecentlyUsed()
    {
        int best = 0;
        for (int i = 1; i < _fields.Count; i++)
        {
            var a = _fields[i];
            var b = _fields[best];
            if (a.LastUsedTick < b.LastUsedTick || (a.LastUsedTick == b.LastUsedTick && a.Sequence < b.Sequence))
            {
                best = i;
            }
        }

        return best;
    }

    private void Evict(int index)
    {
        var field = _fields[index];
        field.Evict();
        _bytes -= field.Bytes;
        _evicted++;
        _fields.RemoveAt(index);
    }

    private static ChunkBox BoundsOf(TilePos[] goals, int radius, WorldSize size)
    {
        var first = goals[0].Chunk;
        int minX = first.X, minY = first.Y, minZ = first.Z, maxX = first.X, maxY = first.Y, maxZ = first.Z;
        foreach (var goal in goals)
        {
            var c = goal.Chunk;
            minX = Math.Min(minX, c.X);
            minY = Math.Min(minY, c.Y);
            minZ = Math.Min(minZ, c.Z);
            maxX = Math.Max(maxX, c.X);
            maxY = Math.Max(maxY, c.Y);
            maxZ = Math.Max(maxZ, c.Z);
        }

        return new ChunkBox(
            new ChunkCoord(Math.Max(0, minX - radius), Math.Max(0, minY - radius), Math.Max(0, minZ - radius)),
            new ChunkCoord(Math.Min(size.ChunksX - 1, maxX + radius), Math.Min(size.ChunksY - 1, maxY + radius), Math.Min(size.ChunksZ - 1, maxZ + radius)));
    }

    private static bool SameGoals(ReadOnlySpan<TilePos> goals, ReadOnlySpan<long> packed)
    {
        if (goals.Length != packed.Length)
        {
            return false;
        }

        for (int i = 0; i < goals.Length; i++)
        {
            if (Pack(goals[i]) != packed[i])
            {
                return false;
            }
        }

        return true;
    }

    private static long Pack(TilePos tile) => ((long)(uint)tile.Z << 42) | ((long)(uint)tile.Y << 21) | (uint)tile.X;

    private static TilePos Unpack(long packed) => new((int)(packed & 0x1F_FFFF), (int)((packed >> 21) & 0x1F_FFFF), (int)((packed >> 42) & 0x1F_FFFF));

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
}
