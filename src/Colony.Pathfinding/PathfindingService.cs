using Colony.Fields;
using Colony.Hashing;

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

    private readonly MovementClassDefinition[] _definitions;
    private readonly MovementClassDescriptor[] _descriptors;
    private readonly RegionMap?[] _maps;
    private readonly Lock _buildLock = new();
    private FieldSet? _fields;
    private bool _inTick;
    private bool _disposed;

    internal PathfindingService(World world, PathfindingOptions options, List<MovementClassDefinition> definitions, List<MovementClassDescriptor> descriptors)
    {
        World = world;
        Options = options;
        _definitions = [.. definitions];
        _descriptors = [.. descriptors];
        _maps = new RegionMap?[_definitions.Length];
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
    public void Rebuild(MovementClass movementClass) => Regions(movementClass).RebuildAll(parallel: !_inTick);

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

    private FieldSet Fields => _fields ??= World.Fields();

    private RegionMap Build(int id)
    {
        lock (_buildLock)
        {
            var existing = _maps[id];
            if (existing is not null)
            {
                return existing;
            }

            var map = new RegionMap(World, Fields, _definitions[id], _descriptors[id].Class, Options);
            map.RebuildAll(parallel: !_inTick);
            Volatile.Write(ref _maps[id], map);
            return map;
        }
    }

    internal void SyncAll(bool parallel)
    {
        foreach (var map in _maps)
        {
            map?.Sync(parallel);
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
        SyncAll(parallel: true);
        _inTick = true;
    }

    void IWorldExtension.EndPhase(Phase phase)
    {
    }

    void IWorldExtension.EndTick(long tick) => _inTick = false;

    /// <summary>Region data is derived from the Fields (spec section 6: caches are excluded from the hash).</summary>
    void IWorldExtension.Hash(ref Hash64State state)
    {
    }

    /// <summary>Region data is not saved (spec section 9.1); only the ids of the built classes, so loading rebuilds the same set.</summary>
    void IWorldExtension.Save(ExtensionStateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> built = stackalloc byte[(_maps.Length + 7) >> 3];
        built.Clear();
        for (int i = 0; i < _maps.Length; i++)
        {
            if (_maps[i] is not null)
            {
                built[i >> 3] |= (byte)(1 << (i & 7));
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
                _ = Build(i);
            }
        }
    }

    public void Dispose() => _disposed = true;

    internal bool IsDisposed => _disposed;
}
