using Colony.Fields;
using Colony.Hashing;
using Colony.Numerics;

namespace Colony.Pathfinding;

/// <summary>
/// A World's pathfinding service (spec section 9.1): one <see cref="RegionMap"/> per movement class, built on
/// first use and kept current from the World change feed. Fetch it with <see cref="PathfindingRegistration.Pathfinding"/>.
/// </summary>
/// <remarks>
/// Sync points (each applies every dependent Field chunk written since the previous one, allocation free):
/// the Late-Phase System <see cref="PathfindingBuilder.SystemName"/> (spec section 4.3, region upkeep), the
/// start of every Tick (catches End-Phase and host writes, so every Tick starts from the Fields as they are),
/// and <see cref="Sync"/> from host code. Between sync points region ids do not move, so every System of a
/// Phase sees the same answer whatever the worker count. Region data is derived state: never hashed, never
/// saved. A save records only which classes were built (one small section), and loading rebuilds exactly
/// those from the loaded Fields, so save-load-continue equals never-saved.
/// </remarks>
public sealed class PathfindingService : IWorldExtension
{
    /// <summary>Name of the one saved section: the ids of the built classes.</summary>
    internal const string BuiltSectionName = "built";

    /// <summary>Prefix of the per-class sections holding the pathfinding summary of Unloaded chunks (spec section 5.3), written only when some chunk is Unloaded.</summary>
    internal const string SummarySectionPrefix = "summary/";

    private readonly MovementClassDefinition[] _definitions;
    private readonly MovementClassDescriptor[] _descriptors;
    private readonly RegionMap?[] _maps;
    private readonly Lock _buildLock = new();
    private readonly Lock _searchLock = new();
    private readonly PathSearch _search = new();
    private readonly Func<int, ChunkResidency> _residency;
    private readonly Lock _flowLock = new();
    private readonly FlowFieldStore _flows;
    private readonly PathfindingCounterIds _counters;
    private FieldSet? _fields;
    private PathRequestQueue? _queue;
    private bool _inTick;
    private bool _disposed;

    internal PathfindingService(World world, PathfindingOptions options, List<MovementClassDefinition> definitions, List<MovementClassDescriptor> descriptors, PathfindingCounterIds counters)
    {
        World = world;
        Options = options;
        _definitions = [.. definitions];
        _descriptors = [.. descriptors];
        _maps = new RegionMap?[_definitions.Length];
        _residency = Residency;
        _flows = new FlowFieldStore(this, options);
        _counters = counters;
    }

    public World World { get; }

    public PathfindingOptions Options { get; }

    public int MovementClassCount => _definitions.Length;

    public ReadOnlySpan<MovementClassDescriptor> MovementClasses => _descriptors;

    public MovementClassDescriptor Describe(MovementClass movementClass) => _descriptors[Validate(movementClass)];

    public MovementClass MovementClass(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < _descriptors.Length; i++)
        {
            if (string.Equals(_descriptors[i].Name, name, StringComparison.Ordinal))
            {
                return new MovementClass(i);
            }
        }

        throw new ArgumentException($"No movement class named '{name}' is registered in this World.", nameof(name));
    }

    /// <summary>Whether the class's region structures exist yet (they are built on first use).</summary>
    public bool IsBuilt(MovementClass movementClass) => Volatile.Read(ref _maps[Validate(movementClass)]) is not null;

    /// <summary>
    /// The region map of a class, building it on first use from the Fields as they are now. Between Ticks the
    /// build labels chunks on the worker pool; during a Tick it labels them serially under a lock (from a chunk
    /// job the pool is busy), so prefer <see cref="EnsureBuilt"/> from host code before the first Tick.
    /// </summary>
    public RegionMap Regions(MovementClass movementClass)
    {
        int id = Validate(movementClass);
        return Volatile.Read(ref _maps[id]) ?? Build(id);
    }

    /// <summary>Builds the class's structures now if they do not exist yet.</summary>
    public void EnsureBuilt(MovementClass movementClass) => _ = Regions(movementClass);

    /// <summary>Relabels every chunk of a class from scratch; the oracle for the incremental path. Call between Ticks or from a Begin/End hook.</summary>
    public void Rebuild(MovementClass movementClass)
    {
        var map = Regions(movementClass);
        map.RebuildAll(parallel: !_inTick);
        lock (_flowLock)
        {
            _flows.MarkDirty(map, map.LastRelabelled, World.CurrentTick);
        }
    }

    /// <summary>
    /// The flow Field of a goal set for a class (spec section 9.1): the breadth-first distances to the nearest
    /// goal over the goal chunks plus <paramref name="radiusChunks"/> (default <see cref="PathfindingOptions.DefaultFlowRadiusChunks"/>),
    /// built now when no Field with the same goals exists, evicting the least recently used Fields past
    /// <see cref="PathfindingOptions.FlowFieldMemoryBudgetBytes"/>. Goal order and duplicates do not matter.
    /// Call it from serial code (host, Begin/End hooks); read the result from anywhere. Between Ticks a dirty
    /// Field is repaired before it is returned (lazy repair on read); during a Tick the Begin repair pass and the
    /// staleness bound apply.
    /// </summary>
    public FlowField FlowField(ReadOnlySpan<TilePos> goals, MovementClass movementClass, int radiusChunks = -1)
    {
        if (goals.IsEmpty)
        {
            throw new ArgumentException("A flow Field needs at least one goal tile.", nameof(goals));
        }

        if (radiusChunks < 0)
        {
            radiusChunks = Options.DefaultFlowRadiusChunks;
        }

        var map = Regions(movementClass);
        lock (_flowLock)
        {
            return _flows.GetOrCreate(goals, map, radiusChunks, World.CurrentTick, _inTick);
        }
    }

    /// <summary>The flow Field with this id, null once it was evicted (or never existed).</summary>
    public FlowField? FlowField(FlowFieldId id)
    {
        lock (_flowLock)
        {
            return _flows.Find(id);
        }
    }

    /// <summary>Brings a flow Field up to date now whatever the budget (serial code only: host, Begin/End hooks).</summary>
    public void Repair(FlowField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        lock (_flowLock)
        {
            _flows.Repair(field);
        }
    }

    /// <summary>
    /// One face move toward the goals of <paramref name="field"/> from <paramref name="from"/>: the flow Field's
    /// step inside its bound, otherwise HPA* toward the nearest goal (which enters the bound on the way), as spec
    /// section 9.1 routes units outside the bound. <see cref="FlowStepStatus.OutsideBound"/> is returned only when
    /// HPA* finds no path to any goal either.
    /// </summary>
    public FlowStepStatus TryNextStep(FlowField field, TilePos from, out TilePos next)
    {
        ArgumentNullException.ThrowIfNull(field);
        var status = field.TryStep(from, out next);
        if (status != FlowStepStatus.OutsideBound)
        {
            return status;
        }

        var goals = field.Goals;
        int best = -1;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < goals.Length; i++)
        {
            int distance = Math.Abs(goals[i].X - from.X) + Math.Abs(goals[i].Y - from.Y) + Math.Abs(goals[i].Z - from.Z);
            if (distance < bestDistance && Reachable(from, goals[i], field.MovementClass))
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best >= 0 && TryNextStep(from, goals[best], field.MovementClass, out next) ? FlowStepStatus.Step : FlowStepStatus.OutsideBound;
    }

    public int FlowFieldCount
    {
        get
        {
            lock (_flowLock)
            {
                return _flows.Count;
            }
        }
    }

    /// <summary>Memory the live flow Fields hold now.</summary>
    public long FlowFieldBytes
    {
        get
        {
            lock (_flowLock)
            {
                return _flows.Bytes;
            }
        }
    }

    public FlowFieldCounters FlowCounters
    {
        get
        {
            lock (_flowLock)
            {
                return _flows.Counters;
            }
        }
    }

    /// <summary>Begin Phase, after delivery: the layered repair pass, then the Tick's counters.</summary>
    internal void RepairFlowFields(in SystemContext context)
    {
        int backlog;
        long bytes;
        long repairUnits;
        lock (_flowLock)
        {
            _flows.RepairPass(context.Tick);
            backlog = _flows.Backlog;
            bytes = _flows.Bytes;
            repairUnits = _flows.WorkUnitsThisTick;
        }

        context.Count(_counters.QueueDepth, Queue.Count);
        context.Count(_counters.WorkUnits, Queue.WorkUnitsThisTick + repairUnits);
        context.Count(_counters.RepairBacklog, backlog);
        context.Count(_counters.FlowFieldBytes, bytes);
        context.Count(_counters.FlowFieldBudgetBytes, Options.FlowFieldMemoryBudgetBytes);
    }

    /// <summary>Applies Field writes made since the last sync point to every built class now (host code between Ticks, or a Begin/End hook).</summary>
    public void Sync() => SyncAll(parallel: true);

    /// <summary>The region of a tile for a class, <see cref="RegionId.None"/> when impassable or outside the World. Constant time.</summary>
    public RegionId RegionOf(TilePos tile, MovementClass movementClass) => Regions(movementClass).RegionOf(tile);

    /// <summary>
    /// Whether <paramref name="to"/> can be reached from <paramref name="from"/> by a mover of <paramref name="movementClass"/>
    /// (spec section 9.1): both tiles passable, inside the World and in one region. Constant time (two array
    /// lookups per endpoint) and allocation free; false for any impassable or out-of-World endpoint.
    /// </summary>
    public bool Reachable(TilePos from, TilePos to, MovementClass movementClass)
    {
        var map = Regions(movementClass);
        var a = map.RegionOf(from);
        return !a.IsNone && a == map.RegionOf(to);
    }

    /// <summary>
    /// Finds a path now with two-level HPA* (spec section 9.1): the result's waypoints are the start, the chunk
    /// transitions crossed and the goal, copied into <paramref name="waypoints"/> (truncated when it is short;
    /// <see cref="PathQuery.WaypointCount"/> is the full count). Consecutive waypoints lie in one chunk or are
    /// face-adjacent across a border; refine between them with the next-step query or the path-follow
    /// helper. Serialised on one search scratch, so prefer the deferred request queue from Systems; allocation
    /// free once warm. The cost is in tiles (one per face move) and within a few percent of optimal.
    /// </summary>
    public PathQuery FindPath(TilePos from, TilePos to, MovementClass movementClass, Span<TilePos> waypoints)
    {
        var map = Regions(movementClass);
        lock (_searchLock)
        {
            var status = _search.Run(map, from, to, _residency);
            if (status == PathStatus.Found)
            {
                var found = _search.Waypoints;
                found[..Math.Min(found.Length, waypoints.Length)].CopyTo(waypoints);
            }

            return new PathQuery(status, _search.Cost, _search.WaypointCount, _search.MissingChunk, _search.WorkUnits);
        }
    }

    /// <summary>
    /// The HPA* cost in tiles from <paramref name="from"/> to <paramref name="to"/> (within a few percent of
    /// optimal), -1 when unreachable or when an Unloaded chunk is in the way. Allocation free; serialised with the
    /// other searches.
    /// </summary>
    public int ApproximateCost(TilePos from, TilePos to, MovementClass movementClass)
    {
        var map = Regions(movementClass);
        lock (_searchLock)
        {
            return _search.Run(map, from, to, _residency) == PathStatus.Found ? _search.Cost : -1;
        }
    }

    /// <summary>
    /// The one face move that takes a unit at <paramref name="from"/> toward <paramref name="to"/> along the HPA*
    /// path, refined by local A* inside the unit's chunk (spec section 9.1). False at the goal or when no path
    /// exists. Allocation free; serialised with the other searches.
    /// </summary>
    public bool TryNextStep(TilePos from, TilePos to, MovementClass movementClass, out TilePos next)
    {
        next = from;
        if (from == to)
        {
            return false;
        }

        var map = Regions(movementClass);
        lock (_searchLock)
        {
            if (_search.Run(map, from, to, _residency) != PathStatus.Found)
            {
                return false;
            }

            // Waypoint 0 is the start itself; the next one is in this chunk or face-adjacent across a border.
            var waypoints = _search.Waypoints;
            return waypoints.Length > 1 && LocalStep.Toward(map, from, waypoints[1], _search.LocalDistanceScratch, _search.LocalQueueScratch, out next);
        }
    }

    /// <summary>
    /// The path-follow helper (spec section 9.1): moves <paramref name="state"/> by <paramref name="distance"/> tiles
    /// along <paramref name="waypoints"/> (a <see cref="FindPath"/> or request result), choosing each face move by
    /// local A* inside the unit's chunk and keeping a fixed-point sub-tile offset. Safe from parallel chunk jobs
    /// (reads only the class's passability; the state is the caller's) and allocation free. Games own movement:
    /// write the resulting tile and offset back to the Entity's Components.
    /// </summary>
    public PathFollowStatus Follow(ref PathFollowState state, ReadOnlySpan<TilePos> waypoints, MovementClass movementClass, Fixed32 distance)
        => PathFollower.Advance(Regions(movementClass), ref state, waypoints, distance);

    /// <summary>The <see cref="PathRequest"/> event queue (emit requests on it; from host code through a command buffer).</summary>
    public ResourceType RequestQueue => World.Schema.Resource(PathfindingBuilder.RequestQueueName);

    /// <summary>The <see cref="PathResult"/> event queue, filled at the Begin Phase of each delivery Tick.</summary>
    public ResourceType ResultQueue => World.Schema.Resource(PathfindingBuilder.ResultQueueName);

    /// <summary>The waypoints of a result delivered this Tick (start, chunk transitions, goal); empty unless found.</summary>
    public ReadOnlySpan<TilePos> Waypoints(in PathResult result) => Queue.Waypoints(in result);

    /// <summary>The results emitted at the last delivery (the most recent Begin Phase), in canonical order; for hosts and tests, Systems read <see cref="ResultQueue"/>.</summary>
    public ReadOnlySpan<PathResult> Delivered => Queue.Delivered;

    /// <summary>Requests taken in and not yet delivered.</summary>
    public int PendingRequestCount => Queue.Count;

    public PathQueueCounters QueueCounters => Queue.Counters;

    internal PathRequestQueue Queue => _queue ??= new PathRequestQueue(this);

    internal Lock SearchLock => _searchLock;

    internal PathSearch Search => _search;

    internal Func<int, ChunkResidency> ResidencyFunc => _residency;

    /// <summary>Residency of a chunk as the search sees it; tests override it until the streaming manager (TASK-25) reports Unloaded chunks.</summary>
    internal Func<int, ChunkResidency>? ResidencyOverride { get; set; }

    private ChunkResidency Residency(int ordinal) => ResidencyOverride is { } f ? f(ordinal) : Fields.ResidencyOf(ordinal);

    private FieldSet Fields => _fields ??= World.Fields();

    private RegionMap Build(int id) => Build(id, default, string.Empty);

    private RegionMap Build(int id, ReadOnlySpan<byte> summary, string extensionName)
    {
        lock (_buildLock)
        {
            var existing = _maps[id];
            if (existing is not null)
            {
                return existing;
            }

            var map = new RegionMap(World, Fields, _definitions[id], _descriptors[id].Class, Options);
            if (!summary.IsEmpty)
            {
                map.RestoreSummary(summary, extensionName);
            }

            map.RebuildAll(parallel: !_inTick);
            Volatile.Write(ref _maps[id], map);
            return map;
        }
    }

    private static string SummarySectionName(int id) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{SummarySectionPrefix}{id}");

    internal void SyncAll(bool parallel)
    {
        foreach (var map in _maps)
        {
            if (map is null)
            {
                continue;
            }

            map.Sync(parallel);
            if (!map.LastRelabelled.IsEmpty)
            {
                lock (_flowLock)
                {
                    _flows.MarkDirty(map, map.LastRelabelled, World.CurrentTick);
                }
            }
        }
    }

    private int Validate(MovementClass movementClass)
    {
        if ((uint)movementClass.Id >= (uint)_definitions.Length)
        {
            throw new ArgumentException($"{movementClass} does not belong to this World.", nameof(movementClass));
        }

        return movementClass.Id;
    }

    /// <summary>The start-of-Tick sync point: End-Phase writes of the previous Tick and host writes since.</summary>
    void IWorldExtension.BeginTick(long tick)
    {
        lock (_flowLock)
        {
            _flows.BeginTick(tick);
        }

        SyncAll(parallel: true);
        _inTick = true;
    }

    void IWorldExtension.EndPhase(Phase phase)
    {
    }

    void IWorldExtension.EndTick(long tick) => _inTick = false;

    /// <summary>Region data is derived from the Fields (spec section 6: caches are excluded from the hash); the pending request queue is state and is hashed.</summary>
    void IWorldExtension.Hash(ref Hash64State state) => Queue.Hash(ref state);

    /// <summary>Region data is not saved (spec section 9.1); the ids of the built classes (so loading rebuilds the same set), the pathfinding summary of Unloaded chunks (spec section 5.3) and, once used, the request queue are.</summary>
    void IWorldExtension.Save(ExtensionStateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Queue.Save(writer);
        Span<byte> built = stackalloc byte[(_maps.Length + 7) >> 3];
        built.Clear();
        for (int i = 0; i < _maps.Length; i++)
        {
            if (_maps[i] is { } map)
            {
                built[i >> 3] |= (byte)(1 << (i & 7));
                var summary = map.WriteSummary();
                if (summary.Length > 0)
                {
                    writer.WriteSection(SummarySectionName(i), summary);
                }
            }
        }

        writer.WriteSection(BuiltSectionName, built);
    }

    /// <summary>Rebuilds every class the save had built, from the loaded Fields (the Fields extension loads first).</summary>
    void IWorldExtension.Load(ExtensionStateReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_buildLock)
        {
            Array.Clear(_maps);
        }

        lock (_flowLock)
        {
            _flows.Clear();
        }

        if (reader.TryGetSection(PathRequestQueue.SectionName, out var requests))
        {
            Queue.Load(requests, reader.ExtensionName);
        }

        if (!reader.TryGetSection(BuiltSectionName, out var built))
        {
            return;
        }

        if (built.Length != (_maps.Length + 7) >> 3)
        {
            throw new InvalidDataException($"Extension '{reader.ExtensionName}': section '{BuiltSectionName}' holds {built.Length} bytes for {_maps.Length} movement classes.");
        }

        for (int i = 0; i < _maps.Length; i++)
        {
            if ((built[i >> 3] & (1 << (i & 7))) != 0)
            {
                _ = reader.TryGetSection(SummarySectionName(i), out var summary) ? Build(i, summary, reader.ExtensionName) : Build(i);
            }
        }
    }

    public void Dispose() => _disposed = true;

    internal bool IsDisposed => _disposed;
}
