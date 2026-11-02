using System.Buffers.Binary;
using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// Per-World residency state of spec section 5.3. Focus points arrive as <see cref="FocusPoint"/> Input Commands and
/// are applied in the Input Phase; the Streaming-Phase System computes every chunk's desired state (Active within a
/// focus radius, Hibernating in the ring <see cref="ResidencyOptions.RingWidth"/> chunks further out, Unloaded
/// elsewhere; everything Active without focus points), evicts under the resident-chunk budget and applies the
/// transitions in canonical chunk order. A chunk-owned Entity (one carrying the TilePos Component) freezes in place
/// with its chunk: every Component's enable bit goes off and <see cref="Frozen"/> keeps the bits, so thawing restores
/// the Entity and the World hash exactly; registered catch-up functions then replay the frozen Ticks. An Unloaded
/// chunk's cells live on as their save section, or as nothing when the <see cref="IChunkSource"/> regenerates them
/// identically. <see cref="Pinned"/> Entities are never frozen.
/// </summary>
/// <remarks>
/// Loading with <see cref="ResidencyOptions.LoadDelayTicks"/> N &gt; 0: a chunk that becomes desired resident at the
/// Streaming Phase of Tick T is requested then (its cells generate on a thread-pool worker into a pooled buffer) and
/// applied at the Streaming Phase of Tick T + N whatever the generation took; a load not finished by then stalls the
/// Tick and is counted. The World state therefore depends only on the focus schedule, never on load timing. Pending
/// loads (ordinal, apply Tick) are state: hashed and saved. Prefetch hints only warm the buffers.
/// </remarks>
public sealed class ChunkResidencyManager : IWorldExtension
{
    private const string StateSection = "state";
    private const string LoadsSection = "loads";
    private const int FocusBytes = 20;
    private const int LoadBytes = 12;

    private readonly World _world;
    private readonly FieldSet _fields;
    private readonly ChunkLayout _layout;
    private readonly ResidencyBuilder _definition;
    private readonly ResidencyOptions _options;
    private readonly ComponentType _tilePos;
    private readonly ComponentType _frozen;
    private readonly ComponentType _pinned;
    private readonly ComponentType? _frozenAt;
    private readonly ComponentType[] _freezable;
    private readonly ResidencyBuilder.ICatchUp[] _catchUps;
    private readonly ChunkResidency[] _desired;
    private readonly byte[] _ringLevel;
    private readonly bool[] _generated;
    private readonly byte[] _hashScratch;
    private readonly long[] _evictKeys;
    private readonly CommandBuffer _commands;
    private readonly CommandBuffer _placements;
    private readonly List<int> _placedOrdinals = [];
    private readonly FocusPoint[] _focus;
    private readonly PendingLoad?[] _pending;
    private readonly PendingLoad?[] _prefetched;
    private readonly Stack<PendingLoad> _loadPool = new();
    private readonly List<PendingLoad> _retired = [];
    private int _focusCount;
    private int _pendingCount;
    private int _prefetchedCount;
    private int _catchUpWork;
    private List<EntityId>[] _movers = [];
    private ChunkCellBuffer? _cells;
    private int _cellsOrdinal = -1;
    private long _loads;
    private long _loadStalls;
    private long _loadsInFlight;
    private long _evictions;
    private long _catchUpEntities;
    private long _catchUpTicks;

    internal ChunkResidencyManager(World world, ResidencyBuilder definition)
    {
        _world = world;
        _definition = definition;
        _options = definition.Options;
        _fields = world.Fields();
        _layout = _fields.Layout;
        _tilePos = definition.TilePos;
        _frozen = definition.Frozen;
        _pinned = definition.Pinned;
        _frozenAt = definition.FrozenAt;
        _catchUps = [.. definition.CatchUps];
        _fields.Residency = this;

        var components = world.Schema.Components;
        var freezable = new List<ComponentType>(components.Length);
        foreach (ref readonly var component in components)
        {
            if (component.Type != _frozen && component.Type != _pinned && component.Type != _frozenAt)
            {
                freezable.Add(component.Type);
            }
        }

        _freezable = [.. freezable];
        int count = _layout.ChunkCount;
        _desired = new ChunkResidency[count];
        _ringLevel = new byte[count];
        _generated = new bool[count];
        _hashScratch = new byte[count * 2];
        _evictKeys = new long[count];
        _pending = new PendingLoad?[count];
        _prefetched = new PendingLoad?[count];
        _focus = new FocusPoint[_options.MaxFocusPoints];
        _commands = world.CreateCommandBuffer();
        _placements = world.CreateCommandBuffer();
        if (_options.InitialResidency == ChunkResidency.Unloaded)
        {
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                _fields.UnloadCells(ordinal, store: false);
                _fields.SetResidency(ordinal, ChunkResidency.Unloaded);
            }
        }
    }

    public World World => _world;

    public ResidencyOptions Options => _options;

    /// <summary>The focus points in Id order.</summary>
    public ReadOnlySpan<FocusPoint> FocusPoints => _focus.AsSpan(0, _focusCount);

    public ChunkResidency ResidencyOf(int ordinal) => _fields.ResidencyOf(ordinal);

    public ChunkResidency ResidencyOf(ChunkCoord coord) => _fields.ResidencyOf(_layout.OrdinalOf(coord));

    public ResidencyCounts Counts
    {
        get
        {
            int active = 0, hibernating = 0, unloaded = 0;
            for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
            {
                switch (_fields.ResidencyOf(ordinal))
                {
                    case ChunkResidency.Active: active++; break;
                    case ChunkResidency.Hibernating: hibernating++; break;
                    default: unloaded++; break;
                }
            }

            return new ResidencyCounts(active, hibernating, unloaded);
        }
    }

    /// <summary>Running totals of the Streaming Phase since the World was created (diagnostics only).</summary>
    public StreamingCounters Streaming => new(_loads, _loadStalls, _loadsInFlight, _evictions, _catchUpEntities, _catchUpTicks);

    /// <summary>Loads requested and not yet applied (state; bounded by <see cref="ResidencyOptions.MaxLoadsInFlight"/>).</summary>
    public int PendingLoadCount => _pendingCount;

    /// <summary>True while a delayed load of the chunk is requested and not yet applied.</summary>
    public bool IsLoadPending(int ordinal) => _pending[ordinal] is not null;

    /// <summary>True when the Entity is frozen with its chunk (carries <see cref="Frozen"/>).</summary>
    public bool IsFrozen(EntityId id) => _world.Has(id, _frozen);

    /// <summary>True when an Unloaded chunk's cells are kept as their save section (false: resident, or regenerable).</summary>
    public bool HasStoredCells(int ordinal) => _fields.HasStoredCells(ordinal);

    /// <summary>True once the chunk's initial Entities were placed from the chunk source (or the host marked it generated).</summary>
    public bool HasBeenGenerated(int ordinal) => _generated[ordinal];

    /// <summary>The chunk source of this World (<see cref="FieldSet.ChunkSource"/>), or null when it has none.</summary>
    public IChunkSource? Source => _fields.ChunkSource;

    /// <summary>
    /// Records that the chunk's cells and Entities were written from the chunk source by the host (for example a
    /// pipeline's initial GenerateAll), so a later reload does not place its Entities again.
    /// </summary>
    public void MarkGenerated(int ordinal) => _generated[ordinal] = true;

    /// <summary>Every Active chunk's in-grid neighbours (26-neighbourhood) are Active or Hibernating: no Active chunk borders unloaded space.</summary>
    public bool RingInvariantHolds()
    {
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            if (_fields.ResidencyOf(ordinal) == ChunkResidency.Active && !NeighboursResident(ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Host-side transition between Ticks (tests, editors, a forced evict/reload): applies the same transition the
    /// Streaming Phase would (synchronously, whatever <see cref="ResidencyOptions.LoadDelayTicks"/> says) and plays
    /// its commands back at once. A pending load of the chunk is dropped. The next Streaming Phase reconciles the
    /// chunk with the focus points again.
    /// </summary>
    public void ForceResidency(int ordinal, ChunkResidency state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, _layout.ChunkCount);
        if (_pending[ordinal] is { } pending)
        {
            Cancel(ordinal, pending);
        }

        Transition(ordinal, _fields.ResidencyOf(ordinal), state, _commands, _world.CurrentTick);
        Flush(_commands);
    }

    /// <summary>
    /// A prefetch hint (spec section 5.3): starts generating the chunk's cells on a background worker now, so a later
    /// load request finds them ready. Hints never change simulation state (the request still applies exactly
    /// <see cref="ResidencyOptions.LoadDelayTicks"/> Ticks after it is made and does not count against the in-flight
    /// bound until then); they only avoid stalls. Ignored for resident chunks, chunks already pending or prefetched,
    /// Worlds without a chunk source or delayed loading, and beyond <see cref="ResidencyOptions.MaxLoadsInFlight"/> hints.
    /// Call from the Tick thread (host code between Ticks, or a serial System hook).
    /// </summary>
    public void Prefetch(ChunkCoord coord)
    {
        int ordinal = _layout.OrdinalOf(coord);
        if (_options.LoadDelayTicks == 0 || Source is null || _fields.ResidencyOf(ordinal) != ChunkResidency.Unloaded
            || _pending[ordinal] is not null || _prefetched[ordinal] is not null || _prefetchedCount >= _options.MaxLoadsInFlight
            || _fields.HasStoredCells(ordinal))
        {
            return;
        }

        var load = Rent(ordinal, long.MaxValue);
        _prefetched[ordinal] = load;
        _prefetchedCount++;
        Dispatch(load);
    }

    // ---- Systems ----

    internal void ApplyFocusCommands(in SystemContext context)
    {
        foreach (var entry in context.Inputs<FocusPoint>(_definition.FocusCommand))
        {
            var point = entry.Command;
            int index = IndexOfFocus(point.Id);
            if (point.Radius < 0)
            {
                if (index >= 0)
                {
                    Array.Copy(_focus, index + 1, _focus, index, _focusCount - index - 1);
                    _focusCount--;
                }

                continue;
            }

            if (index >= 0)
            {
                _focus[index] = point;
                continue;
            }

            if (_focusCount == _focus.Length)
            {
                context.Reject(in entry, ResidencyBuilder.RejectTooManyFocusPoints);
                continue;
            }

            int at = ~index;
            Array.Copy(_focus, at, _focus, at + 1, _focusCount - at);
            _focus[at] = point;
            _focusCount++;
        }
    }

    /// <summary>Binary search by Id: the index, or the bitwise complement of the insertion point.</summary>
    private int IndexOfFocus(uint id)
    {
        int low = 0, high = _focusCount - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            uint candidate = _focus[mid].Id;
            if (candidate == id)
            {
                return mid;
            }

            if (candidate < id)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }

    internal void BeginStreaming(in SystemContext context)
    {
        if (_movers.Length < context.JobCount)
        {
            int old = _movers.Length;
            Array.Resize(ref _movers, Math.Max(context.JobCount, old * 2));
            for (int i = old; i < _movers.Length; i++)
            {
                _movers[i] = [];
            }
        }

        long tick = context.Tick;
        var commands = context.Commands;
        ReclaimRetired();
        ComputeDesired();
        if (_options.ResidentChunkBudget > 0)
        {
            ApplyBudget(in context);
        }

        // Pass 1 (canonical order): unloads, freezes, load requests and the loads due this Tick. Every load lands as
        // Hibernating; pass 2 activates, so no chunk is Active before its neighbours are resident.
        int delay = _options.LoadDelayTicks;
        long loads = 0, stalls = 0;
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            var current = _fields.ResidencyOf(ordinal);
            var desired = _desired[ordinal];
            if (current != ChunkResidency.Unloaded)
            {
                if (desired == ChunkResidency.Unloaded || (current == ChunkResidency.Active && desired == ChunkResidency.Hibernating))
                {
                    Transition(ordinal, current, desired, commands, tick);
                }

                continue;
            }

            var pending = _pending[ordinal];
            if (desired == ChunkResidency.Unloaded)
            {
                if (pending is not null)
                {
                    Cancel(ordinal, pending);
                }

                continue;
            }

            if (delay == 0)
            {
                LoadCells(ordinal);
                loads++;
                continue;
            }

            if (pending is null)
            {
                if (_pendingCount < _options.MaxLoadsInFlight)
                {
                    Request(ordinal, tick + delay);
                }

                continue;
            }

            if (!pending.Dispatched)
            {
                Dispatch(pending); // restored from a save; the chunk source is set by now
            }

            if (pending.ApplyTick > tick)
            {
                continue;
            }

            if (!pending.IsDone)
            {
                stalls++;
                context.Log(_definition.StallMessage, ordinal, pending.ApplyTick, _pendingCount);
                pending.WaitDone();
            }

            ApplyLoad(ordinal, pending);
            loads++;
        }

        // Pass 2: Hibernating -> Active, gated on resident neighbours (ring invariant) and the catch-up work budget.
        _catchUpWork = 0;
        bool thawedAny = false;
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            if (_desired[ordinal] != ChunkResidency.Active || _fields.ResidencyOf(ordinal) != ChunkResidency.Hibernating || !NeighboursResident(ordinal))
            {
                continue;
            }

            if (_catchUps.Length > 0 && thawedAny && _catchUpWork >= _options.CatchUpWorkBudget)
            {
                continue;
            }

            ThawChunk(ordinal, commands, tick);
            _fields.SetResidency(ordinal, ChunkResidency.Active);
            thawedAny = true;
        }

        _loads += loads;
        _loadStalls += stalls;
        _loadsInFlight += _pendingCount;
        var ids = _definition.Counters;
        if (loads != 0)
        {
            context.Count(ids.Loads, loads);
        }

        if (stalls != 0)
        {
            context.Count(ids.LoadStalls, stalls);
        }

        if (_pendingCount != 0)
        {
            context.Count(ids.LoadsInFlight, _pendingCount);
        }

        if (_catchUpWork != 0)
        {
            context.Count(ids.CatchUps, _catchUpWork);
        }
    }

    internal void CollectMovers(in SystemContext context, in ChunkView chunk)
    {
        var positions = chunk.ReadColumn<TilePos>(_tilePos);
        var ids = chunk.Entities;
        var movers = _movers[context.JobIndex];
        foreach (int row in chunk.EnabledRows(_tilePos))
        {
            var pos = positions[row];
            int ordinal = _layout.TryOrdinalOf(pos.X >> FieldChunk.Shift, pos.Y >> FieldChunk.Shift, pos.Z >> FieldChunk.Shift);
            if (ordinal < 0 || _fields.ResidencyOf(ordinal) == ChunkResidency.Active || _world.Has(ids[row], _pinned))
            {
                continue;
            }

            movers.Add(ids[row]);
        }
    }

    internal void EndStreaming(in SystemContext context)
    {
        for (int job = 0; job < context.JobCount; job++)
        {
            var movers = _movers[job];
            foreach (var id in movers)
            {
                FreezeEntity(id, context.Commands, context.Tick);
            }

            movers.Clear();
        }
    }

    private void ComputeDesired()
    {
        var options = _options;
        var far = options.HibernateInsteadOfUnload ? ChunkResidency.Hibernating : ChunkResidency.Unloaded;
        if (_focusCount == 0)
        {
            Array.Fill(_desired, options.InitialResidency == ChunkResidency.Active ? ChunkResidency.Active : far);
            return;
        }

        Array.Fill(_desired, far);
        Array.Fill(_ringLevel, byte.MaxValue);
        for (int i = 0; i < _focusCount; i++)
        {
            var focus = _focus[i];
            var centre = ChunkCoord.OfTile(focus.Center);
            int r = focus.Radius;
            for (int z = centre.Z - r; z <= centre.Z + r; z++)
            {
                for (int y = centre.Y - r; y <= centre.Y + r; y++)
                {
                    for (int x = centre.X - r; x <= centre.X + r; x++)
                    {
                        int ordinal = _layout.TryOrdinalOf(x, y, z);
                        if (ordinal >= 0)
                        {
                            _desired[ordinal] = ChunkResidency.Active;
                            _ringLevel[ordinal] = 0;
                        }
                    }
                }
            }
        }

        // The Hibernating ring, one chunk at a time: every unmarked chunk touching (26-neighbourhood) a chunk of the previous level.
        for (byte level = 1; level <= options.RingWidth; level++)
        {
            for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
            {
                if (_ringLevel[ordinal] == byte.MaxValue && TouchesLevel(ordinal, (byte)(level - 1)))
                {
                    _ringLevel[ordinal] = level;
                    _desired[ordinal] = ChunkResidency.Hibernating;
                }
            }
        }
    }

    private bool TouchesLevel(int ordinal, byte level)
    {
        var c = _layout.CoordOf(ordinal);
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int neighbour = _layout.TryOrdinalOf(c.X + dx, c.Y + dy, c.Z + dz);
                    if (neighbour >= 0 && _ringLevel[neighbour] == level)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Every in-grid neighbour (26-neighbourhood) of the chunk is resident now.</summary>
    private bool NeighboursResident(int ordinal)
    {
        var c = _layout.CoordOf(ordinal);
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int neighbour = _layout.TryOrdinalOf(c.X + dx, c.Y + dy, c.Z + dz);
                    if (neighbour >= 0 && _fields.ResidencyOf(neighbour) == ChunkResidency.Unloaded)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private bool HasDesiredUnloadedNeighbour(int ordinal)
    {
        var c = _layout.CoordOf(ordinal);
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int neighbour = _layout.TryOrdinalOf(c.X + dx, c.Y + dy, c.Z + dz);
                    if (neighbour >= 0 && _desired[neighbour] == ChunkResidency.Unloaded)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Chebyshev distance in chunks from the chunk to the nearest focus centre; 0 without focus points.</summary>
    private int FocusDistance(int ordinal)
    {
        if (_focusCount == 0)
        {
            return 0;
        }

        var c = _layout.CoordOf(ordinal);
        int best = int.MaxValue;
        for (int i = 0; i < _focusCount; i++)
        {
            var centre = ChunkCoord.OfTile(_focus[i].Center);
            int d = Math.Max(Math.Abs(c.X - centre.X), Math.Max(Math.Abs(c.Y - centre.Y), Math.Abs(c.Z - centre.Z)));
            best = Math.Min(best, d);
        }

        return best;
    }

    /// <summary>
    /// Trims the desired set to the resident-chunk budget: evicts farthest-from-focus first, Hibernating before Active at
    /// equal distance, then ascending ordinal; demotes Active chunks left next to unloaded space; emits the pressure event.
    /// </summary>
    private void ApplyBudget(in SystemContext context)
    {
        int budget = _options.ResidentChunkBudget;
        int demanded = 0;
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            if (_desired[ordinal] != ChunkResidency.Unloaded)
            {
                // Key: larger distance first, Hibernating (0) before Active (1), then ordinal; sorts ascending.
                int distance = FocusDistance(ordinal);
                long state = _desired[ordinal] == ChunkResidency.Active ? 1L : 0L;
                _evictKeys[demanded++] = ((long)(int.MaxValue - distance) << 32) | (state << 31) | (uint)ordinal;
            }
        }

        if (demanded <= budget)
        {
            return;
        }

        var keys = _evictKeys.AsSpan(0, demanded);
        keys.Sort();
        int evicted = demanded - budget;
        for (int i = 0; i < evicted; i++)
        {
            _desired[(int)(keys[i] & 0x7FFF_FFFF)] = ChunkResidency.Unloaded;
        }

        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            if (_desired[ordinal] == ChunkResidency.Active && HasDesiredUnloadedNeighbour(ordinal))
            {
                _desired[ordinal] = ChunkResidency.Hibernating;
            }
        }

        _evictions += evicted;
        context.Count(_definition.Counters.Evictions, evicted);
        context.Log(_definition.PressureMessage, demanded, budget, evicted);
        if (_definition.BudgetPressureQueue is { } queue)
        {
            context.Emit(queue, new BudgetPressure(demanded, budget, evicted));
        }
    }

    // ---- Transitions ----

    private void Transition(int ordinal, ChunkResidency from, ChunkResidency to, CommandBuffer commands, long tick)
    {
        if (from == to)
        {
            return;
        }

        switch (from, to)
        {
            case (ChunkResidency.Active, ChunkResidency.Hibernating):
                FreezeChunk(ordinal, commands, tick);
                _fields.SetResidency(ordinal, ChunkResidency.Hibernating);
                break;
            case (ChunkResidency.Active, ChunkResidency.Unloaded):
                FreezeChunk(ordinal, commands, tick);
                UnloadCells(ordinal);
                _fields.SetResidency(ordinal, ChunkResidency.Unloaded);
                break;
            case (ChunkResidency.Hibernating, ChunkResidency.Active):
                ThawChunk(ordinal, commands, tick);
                _fields.SetResidency(ordinal, ChunkResidency.Active);
                break;
            case (ChunkResidency.Hibernating, ChunkResidency.Unloaded):
                UnloadCells(ordinal);
                _fields.SetResidency(ordinal, ChunkResidency.Unloaded);
                break;
            case (ChunkResidency.Unloaded, ChunkResidency.Hibernating):
                LoadCells(ordinal);
                break;
            case (ChunkResidency.Unloaded, ChunkResidency.Active):
                LoadCells(ordinal);
                ThawChunk(ordinal, commands, tick);
                _fields.SetResidency(ordinal, ChunkResidency.Active);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(to), to, "Unknown residency state.");
        }
    }

    /// <summary>Takes the chunk's cells out of the storage: dropped when the source regenerates them identically, otherwise kept as their save section.</summary>
    private void UnloadCells(int ordinal)
    {
        bool store = true;
        if (Source is { } source)
        {
            var cells = Regenerate(source, ordinal);
            store = false;
            for (int f = 0; f < _fields.FieldCount; f++)
            {
                if (!_fields.StorageAt(f).CellsEqual(ordinal, cells.Cells(f)))
                {
                    store = true;
                    break;
                }
            }
        }

        _fields.UnloadCells(ordinal, store);
    }

    /// <summary>Makes the chunk Hibernating with its cells back, synchronously: from the stored section, else regenerated (placing its Entities the first time).</summary>
    private void LoadCells(int ordinal)
    {
        if (_prefetched[ordinal] is { } prefetched)
        {
            Retire(prefetched);
            _prefetched[ordinal] = null;
            _prefetchedCount--;
        }

        _fields.SetResidency(ordinal, ChunkResidency.Hibernating);
        if (_fields.ReloadStoredCells(ordinal) || Source is not { } source)
        {
            return;
        }

        var cells = Regenerate(source, ordinal);
        WriteGenerated(ordinal, source, cells);
    }

    private void WriteGenerated(int ordinal, IChunkSource source, ChunkCellBuffer cells)
    {
        for (int f = 0; f < _fields.FieldCount; f++)
        {
            _fields.WriteChunkCells(f, ordinal, cells.Cells(f));
        }

        if (!_generated[ordinal])
        {
            _generated[ordinal] = true;
            source.PlaceEntities(_layout.CoordOf(ordinal), _placements);
            _placedOrdinals.Add(ordinal);
        }
    }

    private ChunkCellBuffer Regenerate(IChunkSource source, int ordinal)
    {
        _cells ??= new ChunkCellBuffer(_fields);
        if (_cellsOrdinal != ordinal)
        {
            _cellsOrdinal = -1;
            _cells.Reset();
            source.GenerateCells(_layout.CoordOf(ordinal), _cells);
            _cellsOrdinal = ordinal;
        }

        return _cells;
    }

    /// <summary>Hashes an unstored Unloaded chunk's cells of one Field as the regenerated dense chunk; false without a source.</summary>
    internal bool TryHashRegenerated(int fieldId, int ordinal, ref Hash64State state)
    {
        if (Source is not { } source)
        {
            return false;
        }

        _fields.AppendCells(fieldId, Regenerate(source, ordinal).Cells(fieldId), ref state);
        return true;
    }

    // ---- Delayed loads ----

    private PendingLoad Rent(int ordinal, long applyTick)
    {
        if (!_loadPool.TryPop(out var load))
        {
            load = new PendingLoad(new ChunkCellBuffer(_fields));
        }

        load.Prepare(ordinal, _layout.CoordOf(ordinal), applyTick);
        return load;
    }

    /// <summary>Requests the chunk's load for <paramref name="applyTick"/>, taking over a prefetched buffer when there is one.</summary>
    private void Request(int ordinal, long applyTick)
    {
        PendingLoad load;
        if (_prefetched[ordinal] is { } prefetched)
        {
            _prefetched[ordinal] = null;
            _prefetchedCount--;
            load = prefetched;
            load.ApplyTick = applyTick;
        }
        else
        {
            load = Rent(ordinal, applyTick);
            Dispatch(load);
        }

        _pending[ordinal] = load;
        _pendingCount++;
    }

    /// <summary>Starts the generation on the thread pool; stored chunks and Worlds without a source have nothing to generate.</summary>
    private void Dispatch(PendingLoad load)
    {
        load.Dispatched = true;
        if (_fields.HasStoredCells(load.Ordinal) || Source is not { } source)
        {
            load.Finish(generate: false);
            return;
        }

        load.Start(source);
    }

    private void ApplyLoad(int ordinal, PendingLoad load)
    {
        if (load.Error is { } error)
        {
            throw new InvalidOperationException($"The chunk source failed to generate chunk {_layout.CoordOf(ordinal)}.", error);
        }

        _fields.SetResidency(ordinal, ChunkResidency.Hibernating);
        if (!_fields.ReloadStoredCells(ordinal) && load.Generated && Source is { } source)
        {
            WriteGenerated(ordinal, source, load.Cells);
        }

        _pending[ordinal] = null;
        _pendingCount--;
        _loadPool.Push(load);
    }

    private void Cancel(int ordinal, PendingLoad load)
    {
        _pending[ordinal] = null;
        _pendingCount--;
        Retire(load);
    }

    /// <summary>Returns a load to the pool, or parks it until its worker is done with the buffer.</summary>
    private void Retire(PendingLoad load)
    {
        if (load.IsDone)
        {
            _loadPool.Push(load);
        }
        else
        {
            _retired.Add(load);
        }
    }

    private void ReclaimRetired()
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].IsDone)
            {
                _loadPool.Push(_retired[i]);
                _retired.RemoveAt(i);
            }
        }
    }

    // ---- Entities ----

    /// <summary>Plays back pending commands and freezes the Entities just placed into chunks that are not Active.</summary>
    private void Flush(CommandBuffer commands)
    {
        if (!commands.IsEmpty)
        {
            _world.Playback(commands);
        }

        if (_placements.IsEmpty && _placedOrdinals.Count == 0)
        {
            return;
        }

        _world.Playback(_placements);
        long tick = _world.CurrentTick;
        foreach (int ordinal in _placedOrdinals)
        {
            if (_fields.ResidencyOf(ordinal) != ChunkResidency.Active)
            {
                FreezeChunk(ordinal, _placements, tick);
            }
        }

        _placedOrdinals.Clear();
        if (!_placements.IsEmpty)
        {
            _world.Playback(_placements);
        }
    }

    private void FreezeChunk(int ordinal, CommandBuffer commands, long tick)
    {
        var coord = _layout.CoordOf(ordinal);
        foreach (var chunk in _world.Query(ComponentMask.Of(_tilePos), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(_tilePos);
            var ids = chunk.Entities;
            for (int row = 0; row < positions.Length; row++)
            {
                if (ChunkCoord.OfTile(positions[row]) == coord)
                {
                    FreezeEntity(ids[row], commands, tick);
                }
            }
        }
    }

    private void ThawChunk(int ordinal, CommandBuffer commands, long tick)
    {
        var coord = _layout.CoordOf(ordinal);
        foreach (var chunk in _world.Query(ComponentMask.Of(_tilePos), default, ignoreEnableBits: true))
        {
            var positions = chunk.Column<TilePos>(_tilePos);
            var ids = chunk.Entities;
            for (int row = 0; row < positions.Length; row++)
            {
                if (ChunkCoord.OfTile(positions[row]) == coord)
                {
                    ThawEntity(ids[row], commands, tick);
                }
            }
        }
    }

    private void FreezeEntity(EntityId id, CommandBuffer commands, long tick)
    {
        if (_world.Has(id, _frozen) || _world.Has(id, _pinned))
        {
            return;
        }

        var enabled = ComponentMask.Empty;
        foreach (var type in _freezable)
        {
            if (!_world.Has(id, type))
            {
                continue;
            }

            if (_world.IsEnabled(id, type))
            {
                enabled = enabled.With(type);
                _world.SetEnabled(id, type, false);
            }
        }

        commands.Add(id, _frozen, new Frozen(enabled));
        if (_frozenAt is { } frozenAt)
        {
            commands.Add(id, frozenAt, new FrozenAt(tick));
        }
    }

    private void ThawEntity(EntityId id, CommandBuffer commands, long tick)
    {
        if (!_world.Has(id, _frozen))
        {
            return;
        }

        var enabled = _world.Get<Frozen>(id, _frozen).Enabled;
        foreach (var type in _freezable)
        {
            if (enabled.Contains(type) && _world.Has(id, type))
            {
                _world.SetEnabled(id, type, true);
            }
        }

        commands.Remove(id, _frozen);
        if (_frozenAt is { } frozenAt && _world.Has(id, frozenAt))
        {
            long elapsed = Math.Clamp(tick - _world.Get<FrozenAt>(id, frozenAt).Tick, 0, _options.CatchUpMaxTicks);
            foreach (var catchUp in _catchUps)
            {
                catchUp.Apply(_world, id, elapsed);
            }

            commands.Remove(id, frozenAt);
            _catchUpWork++;
            _catchUpEntities++;
            _catchUpTicks += elapsed;
        }
    }

    // ---- IWorldExtension ----

    public void BeginTick(long tick)
    {
    }

    public void EndPhase(Phase phase)
    {
        // The Streaming Phase's buffers were merged; Entities a chunk source placed this Phase are created now, still
        // inside the Streaming boundary, and frozen when their chunk is Hibernating. (Jobs of the Phase were gathered
        // before the Begin hook ran, so the placement cannot be played back from the hook itself.)
        if (phase == Phase.Streaming)
        {
            Flush(_placements);
        }
    }

    public void EndTick(long tick)
    {
    }

    /// <summary>Focus points, every chunk's residency and generated flag (canonical order) and, with delayed loading, the pending loads. Entities and cells are hashed by their owners.</summary>
    public void Hash(ref Hash64State state)
    {
        state.Append(in _focusCount);
        state.Append<FocusPoint>(_focus.AsSpan(0, _focusCount));
        state.Append(ResidencyAndGeneratedBytes());
        if (_options.LoadDelayTicks > 0)
        {
            state.Append(in _pendingCount);
            for (int ordinal = 0; ordinal < _pending.Length; ordinal++)
            {
                if (_pending[ordinal] is { } load)
                {
                    long applyTick = load.ApplyTick;
                    state.Append(in ordinal);
                    state.Append(in applyTick);
                }
            }
        }
    }

    private ReadOnlySpan<byte> ResidencyAndGeneratedBytes()
    {
        int n = _layout.ChunkCount;
        for (int ordinal = 0; ordinal < n; ordinal++)
        {
            _hashScratch[ordinal] = (byte)_fields.ResidencyOf(ordinal);
            _hashScratch[n + ordinal] = _generated[ordinal] ? (byte)1 : (byte)0;
        }

        return _hashScratch;
    }

    /// <summary>
    /// Section <c>state</c>: focus count, the focus points (Id, X, Y, Z, Radius as little-endian 32-bit values) and one
    /// generated byte per chunk. Section <c>loads</c>, only while loads are pending: count, then (ordinal int32,
    /// apply Tick int64) per load in ordinal order. Residency is saved by the Fields.
    /// </summary>
    public void Save(ExtensionStateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var payload = new byte[4 + (_focusCount * FocusBytes) + _layout.ChunkCount];
        BinaryPrimitives.WriteInt32LittleEndian(payload, _focusCount);
        int offset = 4;
        for (int i = 0; i < _focusCount; i++)
        {
            var f = _focus[i];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), f.Id);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4), f.Center.X);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 8), f.Center.Y);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 12), f.Center.Z);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 16), f.Radius);
            offset += FocusBytes;
        }

        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            payload[offset + ordinal] = _generated[ordinal] ? (byte)1 : (byte)0;
        }

        writer.WriteSection(StateSection, payload);
        if (_pendingCount == 0)
        {
            return;
        }

        var loads = new byte[4 + (_pendingCount * LoadBytes)];
        BinaryPrimitives.WriteInt32LittleEndian(loads, _pendingCount);
        offset = 4;
        for (int ordinal = 0; ordinal < _pending.Length; ordinal++)
        {
            if (_pending[ordinal] is { } load)
            {
                BinaryPrimitives.WriteInt32LittleEndian(loads.AsSpan(offset), ordinal);
                BinaryPrimitives.WriteInt64LittleEndian(loads.AsSpan(offset + 4), load.ApplyTick);
                offset += LoadBytes;
            }
        }

        writer.WriteSection(LoadsSection, loads);
    }

    public void Load(ExtensionStateReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var payload = reader.Section(StateSection);
        if (payload.Length < 4)
        {
            throw new InvalidDataException($"Section '{StateSection}' of extension '{reader.ExtensionName}' is {payload.Length} bytes; expected at least 4.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0 || count > _focus.Length || payload.Length != 4 + (count * FocusBytes) + _layout.ChunkCount)
        {
            throw new InvalidDataException($"Section '{StateSection}' of extension '{reader.ExtensionName}' holds {count} focus points in {payload.Length} bytes; expected {4 + (Math.Max(count, 0) * FocusBytes) + _layout.ChunkCount} bytes for at most {_focus.Length} points.");
        }

        int offset = 4;
        uint previous = 0;
        for (int i = 0; i < count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            if (i > 0 && id <= previous)
            {
                throw new InvalidDataException($"Section '{StateSection}' of extension '{reader.ExtensionName}': focus points are not in ascending Id order.");
            }

            previous = id;
            var centre = new TilePos(
                BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 8)..]),
                BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 12)..]));
            _focus[i] = new FocusPoint(id, centre, BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 16)..]));
            offset += FocusBytes;
        }

        _focusCount = count;
        for (int ordinal = 0; ordinal < _layout.ChunkCount; ordinal++)
        {
            _generated[ordinal] = payload[offset + ordinal] != 0;
        }

        _cellsOrdinal = -1;
        for (int ordinal = 0; ordinal < _pending.Length; ordinal++)
        {
            if (_pending[ordinal] is { } stale)
            {
                Cancel(ordinal, stale);
            }
        }

        if (!reader.TryGetSection(LoadsSection, out var loads))
        {
            return;
        }

        int pending = loads.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(loads) : -1;
        if (pending < 0 || pending > _options.MaxLoadsInFlight || loads.Length != 4 + (pending * LoadBytes))
        {
            throw new InvalidDataException($"Section '{LoadsSection}' of extension '{reader.ExtensionName}' holds {pending} loads in {loads.Length} bytes.");
        }

        offset = 4;
        for (int i = 0; i < pending; i++)
        {
            int ordinal = BinaryPrimitives.ReadInt32LittleEndian(loads[offset..]);
            long applyTick = BinaryPrimitives.ReadInt64LittleEndian(loads[(offset + 4)..]);
            offset += LoadBytes;
            if ((uint)ordinal >= (uint)_pending.Length || _pending[ordinal] is not null || _fields.ResidencyOf(ordinal) != ChunkResidency.Unloaded)
            {
                throw new InvalidDataException($"Section '{LoadsSection}' of extension '{reader.ExtensionName}' names chunk {ordinal}, which is not an Unloaded chunk without a pending load.");
            }

            // Dispatched at the next Streaming Phase: the host may set the chunk source after loading.
            _pending[ordinal] = Rent(ordinal, applyTick);
            _pendingCount++;
        }
    }

    /// <summary>Waits for every background generation still running, so no worker touches a buffer after the World is gone.</summary>
    public void Dispose()
    {
        foreach (var load in _pending)
        {
            load?.WaitDone();
        }

        foreach (var load in _prefetched)
        {
            load?.WaitDone();
        }

        foreach (var load in _retired)
        {
            load.WaitDone();
        }
    }

    /// <summary>One background load: the chunk, the Tick it applies at and the buffer its cells generate into.</summary>
    private sealed class PendingLoad(ChunkCellBuffer cells) : IThreadPoolWorkItem
    {
        private IChunkSource? _source;
        private int _done = 1;

        public ChunkCellBuffer Cells => cells;

        public int Ordinal { get; private set; }

        public ChunkCoord Coord { get; private set; }

        public long ApplyTick { get; set; }

        /// <summary>False for a load restored from a save until the next Streaming Phase starts its generation.</summary>
        public bool Dispatched { get; set; }

        /// <summary>True when <see cref="Cells"/> holds generated cells (false: stored or initial cells apply).</summary>
        public bool Generated { get; private set; }

        public Exception? Error { get; private set; }

        public bool IsDone => Volatile.Read(ref _done) != 0;

        public void Prepare(int ordinal, ChunkCoord coord, long applyTick)
        {
            Ordinal = ordinal;
            Coord = coord;
            ApplyTick = applyTick;
            Dispatched = false;
            Generated = false;
            Error = null;
            _source = null;
            _done = 1;
        }

        public void Finish(bool generate)
        {
            Generated = generate;
            Volatile.Write(ref _done, 1);
        }

        public void Start(IChunkSource source)
        {
            _source = source;
            Generated = true;
            Volatile.Write(ref _done, 0);
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        /// <summary>Spins (yielding) until the worker is done; only a load not ready at its Tick gets here.</summary>
        public void WaitDone()
        {
            var spin = new SpinWait();
            while (!IsDone)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            try
            {
                cells.Reset();
                _source!.GenerateCells(Coord, cells);
            }
#pragma warning disable CA1031 // The exception belongs to the Tick thread, which rethrows it when the load applies.
            catch (Exception e)
#pragma warning restore CA1031
            {
                Error = e;
            }
            finally
            {
                Volatile.Write(ref _done, 1);
            }
        }
    }
}
