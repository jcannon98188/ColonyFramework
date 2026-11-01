using System.Runtime.InteropServices;
using Colony.Fields;
using Colony.Hashing;

namespace Colony.Pathfinding;

/// <summary>Per-schema settings of the pathfinding service.</summary>
public sealed record PathfindingOptions
{
    /// <summary>Default per-World movement class limit (spec section 9.1).</summary>
    public const int DefaultMovementClassLimit = 32;

    /// <summary>Hard ceiling on movement classes per World (spec section 9.1).</summary>
    public const int MovementClassCeiling = 256;

    /// <summary>Movement classes a schema may register, 1..<see cref="MovementClassCeiling"/>.</summary>
    public int MaxMovementClasses { get; init; } = DefaultMovementClassLimit;

    /// <summary>
    /// Rebuild-vs-incremental threshold: when at least this fraction of the chunks changed in one Tick, every
    /// chunk is relabelled instead of the dirty set plus its neighbours (the labels are identical either way).
    /// </summary>
    public double FullRebuildFraction { get; init; } = 0.5;

    /// <summary>
    /// Work units (abstract nodes plus local cells expanded) the request queue may spend per Tick. A request is
    /// started only while the budget has room, so at least one runs per Tick; the rest wait for the next Tick.
    /// </summary>
    public long WorkBudgetPerTick { get; init; } = 200_000;

    /// <summary>Ticks between a request's submission Tick T and its delivery at the Begin Phase of T + N; at least 1.</summary>
    public int ResultDelayTicks { get; init; } = 2;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMovementClasses, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMovementClasses, MovementClassCeiling);
        ArgumentOutOfRangeException.ThrowIfLessThan(FullRebuildFraction, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FullRebuildFraction, 1.0);
        ArgumentOutOfRangeException.ThrowIfLessThan(WorkBudgetPerTick, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ResultDelayTicks, 1);
    }
}

/// <summary>
/// Registers the pathfinding service of a schema (spec section 9.1): the movement classes and their passability
/// rules. A <see cref="WorldExtensionDefinition"/> named "pathfinding" over the Fields chunk grid, plus the
/// Late-Phase System <see cref="SystemName"/> that applies the Tick's Field changes to every built class. Created by <see cref="PathfindingRegistration.AddPathfinding"/>.
/// </summary>
public sealed class PathfindingBuilder : WorldExtensionDefinition
{
    /// <summary>Layout hash seed ("PATH").</summary>
    private const ulong LayoutSeed = 0x5041_5448UL;

    /// <summary>Name of the Late-Phase System that applies the Tick's Field changes to every built class (spec section 4.3); order Late Systems that write Fields Before it.</summary>
    public const string SystemName = "pathfinding.regions";

    /// <summary>Name of the <see cref="PathRequest"/> event queue.</summary>
    public const string RequestQueueName = "pathfinding.requests";

    /// <summary>Name of the <see cref="PathResult"/> event queue.</summary>
    public const string ResultQueueName = "pathfinding.results";

    /// <summary>Name of the End-Phase System that takes the Tick's requests into the queue.</summary>
    public const string IntakeSystemName = "pathfinding.intake";

    /// <summary>Name of the Begin-Phase System that runs budgeted searches and emits the results due this Tick.</summary>
    public const string DeliverSystemName = "pathfinding.deliver";

    private readonly FieldsBuilder _fields;
    private readonly List<MovementClassDefinition> _definitions = [];
    private readonly List<MovementClassDescriptor> _descriptors = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private bool _frozen;
    private ulong _layoutHash;

    internal PathfindingBuilder(FieldsBuilder fields, PathfindingOptions options)
    {
        _fields = fields;
        Options = options;
    }

    public PathfindingOptions Options { get; }

    public override string Name => "pathfinding";

    public override ulong LayoutHash => _layoutHash;

    public int MovementClassCount => _definitions.Count;

    /// <summary>The <see cref="PathRequest"/> queue; declare it in <see cref="SystemDefinition.WritesResources"/> to emit requests.</summary>
    public ResourceType RequestQueue { get; internal set; }

    /// <summary>The <see cref="PathResult"/> queue; declare it in <see cref="SystemDefinition.ReadsResources"/> to read results.</summary>
    public ResourceType ResultQueue { get; internal set; }

    /// <summary>
    /// Registers a movement class; ids are registration order. The per-World limit
    /// (<see cref="PathfindingOptions.MaxMovementClasses"/>) is enforced at <see cref="WorldBuilder.Build"/>.
    /// </summary>
    public MovementClass AddMovementClass(MovementClassDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_frozen)
        {
            throw new InvalidOperationException("The schema has been built; movement class registration is closed.");
        }

        if (_byName.ContainsKey(definition.Name))
        {
            throw new ArgumentException($"A movement class named '{definition.Name}' is already registered.", nameof(definition));
        }

        int id = _definitions.Count;
        ulong hash = Hash64.Compute(MemoryMarshal.AsBytes(definition.Name.AsSpan()), LayoutSeed);
        foreach (int field in definition.FieldIds)
        {
            hash = Hash64.Combine(hash, (ulong)field);
        }

        _definitions.Add(definition);
        _descriptors.Add(new MovementClassDescriptor(new MovementClass(id), definition.Name, hash));
        _byName.Add(definition.Name, id);
        return new MovementClass(id);
    }

    /// <summary>The handle of an already-registered class, for System definitions written before <see cref="WorldBuilder.Build"/>.</summary>
    public MovementClass MovementClass(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out int id)
            ? new MovementClass(id)
            : throw new ArgumentException($"No movement class named '{name}' is registered; call AddMovementClass first.", nameof(name));
    }

    public override void Freeze()
    {
        if (_frozen)
        {
            return;
        }

        if (_definitions.Count > Options.MaxMovementClasses)
        {
            throw new InvalidOperationException($"{_definitions.Count} movement classes are registered but this World allows {Options.MaxMovementClasses} (PathfindingOptions.MaxMovementClasses, ceiling {PathfindingOptions.MovementClassCeiling}).");
        }

        _frozen = true;
        _fields.Freeze();
        ulong hash = Hash64.Combine(LayoutSeed, (ulong)_definitions.Count);
        foreach (var descriptor in _descriptors)
        {
            hash = Hash64.Combine(hash, descriptor.LayoutHash);
        }

        _layoutHash = hash;
    }

    public override IWorldExtension Create(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Freeze();
        return new PathfindingService(world, Options, _definitions, _descriptors);
    }

    internal static SystemDefinition DefineSystem() => new()
    {
        Name = SystemName,
        Phase = Phase.Late,
        Begin = static (in SystemContext context) => context.World.Pathfinding().SyncAll(parallel: true),
    };

    internal static SystemDefinition DefineIntake(ResourceType requests) => new()
    {
        Name = IntakeSystemName,
        Phase = Phase.End,
        ReadsResources = ResourceMask.Of(requests),
        Begin = (in SystemContext context) => context.World.Pathfinding().Queue.Intake(context.Events<PathRequest>(requests), context.Tick),
    };

    internal static SystemDefinition DefineDeliver(ResourceType results) => new()
    {
        Name = DeliverSystemName,
        Phase = Phase.Begin,
        WritesResources = ResourceMask.Of(results),
        Begin = (in SystemContext context) =>
        {
            var queue = context.World.Pathfinding().Queue;
            queue.Compute();
            queue.Deliver(in context, results, context.Tick);
        },
    };
}

/// <summary>Registration entry point of the pathfinding service on the Core builder.</summary>
public static class PathfindingRegistration
{
    /// <summary>
    /// Gives Worlds of this schema the pathfinding service over the Fields chunk grid. Call once per schema, after
    /// <see cref="WorldBuilderExtensions.AddFields"/>; register movement classes on the returned builder.
    /// </summary>
    public static PathfindingBuilder AddPathfinding(this WorldBuilder builder, FieldsBuilder fields, PathfindingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);
        options ??= new PathfindingOptions();
        options.Validate();
        var pathfinding = new PathfindingBuilder(fields, options);
        builder.AddExtension(pathfinding);
        builder.AddEvent<PathRequest>(PathfindingBuilder.RequestQueueName);
        builder.AddEvent<PathResult>(PathfindingBuilder.ResultQueueName);
        pathfinding.RequestQueue = builder.Resource(PathfindingBuilder.RequestQueueName);
        pathfinding.ResultQueue = builder.Resource(PathfindingBuilder.ResultQueueName);
        builder.AddSystem(PathfindingBuilder.DefineSystem());
        builder.AddSystem(PathfindingBuilder.DefineIntake(pathfinding.RequestQueue));
        builder.AddSystem(PathfindingBuilder.DefineDeliver(pathfinding.ResultQueue));
        return pathfinding;
    }

    /// <summary>The World's <see cref="PathfindingService"/>; throws when the schema registered none.</summary>
    public static PathfindingService Pathfinding(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Extension<PathfindingService>();
    }
}
