using Colony.Diagnostics;
using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// Registers the residency manager of spec section 5.3: the <see cref="FocusPoint"/> Input Command, the built-in
/// <see cref="Frozen"/> and <see cref="Pinned"/> Components, the Input-Phase System that keeps the focus table and
/// the Streaming-Phase System that moves chunks between Active, Hibernating and Unloaded. Created by
/// <see cref="ResidencyRegistration.AddResidency"/>; each World gets a <see cref="ChunkResidencyManager"/>.
/// </summary>
public sealed class ResidencyBuilder : WorldExtensionDefinition
{
    /// <summary>Layout hash seed ("RSDY").</summary>
    private const ulong LayoutSeed = 0x5253_4459UL;

    public const string ExtensionName = "residency";

    /// <summary>Name of the Streaming-Phase System that applies residency transitions.</summary>
    public const string StreamingSystemName = "residency.streaming";

    /// <summary>Name of the Input-Phase System that applies <see cref="FocusPoint"/> commands.</summary>
    public const string FocusSystemName = "residency.focus";

    public const string FocusCommandName = "colony.focus";

    public const string FrozenComponentName = "colony.frozen";

    public const string PinnedComponentName = "colony.pinned";

    /// <summary>Name of the <see cref="FrozenAt"/> Component, registered by the first <see cref="AddCatchUp{T}"/>.</summary>
    public const string FrozenAtComponentName = "colony.frozen-at";

    /// <summary>Name of the <see cref="BudgetPressure"/> event queue, registered when <see cref="ResidencyOptions.ResidentChunkBudget"/> is set.</summary>
    public const string BudgetPressureQueueName = "colony.budget-pressure";

    /// <summary><see cref="CommandRejected.Reason"/> of a <see cref="FocusPoint"/> that would exceed <see cref="ResidencyOptions.MaxFocusPoints"/>.</summary>
    public const int RejectTooManyFocusPoints = 1;

    private readonly WorldBuilder _builder;
    private readonly FieldsBuilder _fields;
    private readonly List<ICatchUp> _catchUps = [];
    private ComponentType? _frozenAt;

    internal ResidencyBuilder(WorldBuilder builder, FieldsBuilder fields, ComponentType tilePos, ResidencyOptions options)
    {
        _builder = builder;
        _fields = fields;
        TilePos = tilePos;
        Options = options;
        FocusCommand = builder.AddInputCommand<FocusPoint>(FocusCommandName);
        Frozen = builder.AddComponent<Frozen>(FrozenComponentName, ComponentOptions.NonFragmenting).Component<Frozen>();
        Pinned = builder.AddComponent<Pinned>(PinnedComponentName, ComponentOptions.NonFragmenting).Component<Pinned>();
        if (options.ResidentChunkBudget > 0)
        {
            BudgetPressureQueue = builder.AddEvent<BudgetPressure>(BudgetPressureQueueName).Resource(BudgetPressureQueueName);
        }

        Counters = new StreamingCounterIds(
            builder.AddCounter("residency.loads"),
            builder.AddCounter("residency.load-stalls"),
            builder.AddCounter("residency.loads-in-flight"),
            builder.AddCounter("residency.evictions"),
            builder.AddCounter("residency.catch-ups"),
            builder.AddCounter("residency.catch-up-ticks"));
        StallMessage = builder.AddLogMessage("Streaming: the load of chunk {0} due at Tick {1} was not ready and stalled the Tick ({2} loads in flight)");
        PressureMessage = builder.AddLogMessage("Streaming: focus points demanded {0} resident chunks against a budget of {1}; {2} evicted");
    }

    /// <summary>The Component that makes an Entity chunk-owned.</summary>
    public ComponentType TilePos { get; }

    public ComponentType Frozen { get; }

    public ComponentType Pinned { get; }

    /// <summary>The <see cref="FrozenAt"/> Component, null until a catch-up function is registered.</summary>
    public ComponentType? FrozenAt => _frozenAt;

    public InputCommandType FocusCommand { get; }

    /// <summary>The <see cref="BudgetPressure"/> queue, null without a <see cref="ResidencyOptions.ResidentChunkBudget"/>.</summary>
    public ResourceType? BudgetPressureQueue { get; }

    public ResidencyOptions Options { get; }

    /// <summary>The <c>residency.*</c> diagnostics counters (indices into <c>TickRecordView.Counters</c>).</summary>
    public StreamingCounterIds Counters { get; }

    internal LogMessageId StallMessage { get; }

    internal LogMessageId PressureMessage { get; }

    internal IReadOnlyList<ICatchUp> CatchUps => _catchUps;

    public override string Name => ExtensionName;

    public override ulong LayoutHash
    {
        get
        {
            var options = Options;
            ulong hash = Hash64.Combine(LayoutSeed, (ulong)TilePos.Id);
            hash = Hash64.Combine(hash, (ulong)options.MaxFocusPoints);
            hash = Hash64.Combine(hash, options.HibernateInsteadOfUnload ? 1UL : 0UL);
            hash = Hash64.Combine(hash, (ulong)options.InitialResidency);
            if (options.RingWidth != 1 || options.LoadDelayTicks != 0 || options.ResidentChunkBudget != 0 || _catchUps.Count != 0)
            {
                // Folded only when set, so schemas with TASK-26 options keep their layout hash.
                hash = Hash64.Combine(hash, (ulong)options.RingWidth);
                hash = Hash64.Combine(hash, (ulong)options.LoadDelayTicks);
                hash = Hash64.Combine(hash, (ulong)options.MaxLoadsInFlight);
                hash = Hash64.Combine(hash, (ulong)options.ResidentChunkBudget);
                hash = Hash64.Combine(hash, (ulong)options.CatchUpMaxTicks);
                hash = Hash64.Combine(hash, (ulong)options.CatchUpWorkBudget);
                hash = Hash64.Combine(hash, (ulong)_catchUps.Count);
                foreach (var catchUp in _catchUps)
                {
                    hash = Hash64.Combine(hash, (ulong)catchUp.Component.Id);
                }
            }

            return hash;
        }
    }

    /// <summary>
    /// Opts a Component into catch-up (spec section 5.3): when a frozen Entity carrying <paramref name="component"/> thaws,
    /// <paramref name="action"/> runs once with the Ticks it was frozen (capped by <see cref="ResidencyOptions.CatchUpMaxTicks"/>)
    /// in the Streaming Phase, before the Tick's Simulate Phase. The function must be a deterministic function of
    /// (state, elapsed) alone. The first registration adds the <see cref="FrozenAt"/> Component to the schema.
    /// </summary>
    public ResidencyBuilder AddCatchUp<T>(ComponentType component, CatchUpAction<T> action)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        foreach (var existing in _catchUps)
        {
            if (existing.Component == component)
            {
                throw new ArgumentException($"Component {component} already has a catch-up function.", nameof(component));
            }
        }

        _frozenAt ??= _builder.AddComponent<FrozenAt>(FrozenAtComponentName, ComponentOptions.NonFragmenting).Component<FrozenAt>();
        _catchUps.Add(new CatchUp<T>(component, action));
        return this;
    }

    public override IWorldExtension Create(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _fields.Freeze();
        return new ChunkResidencyManager(world, this);
    }

    internal static SystemDefinition DefineFocusSystem() => new()
    {
        Name = FocusSystemName,
        Phase = Phase.Input,
        Begin = static (in SystemContext context) => context.World.Residency().ApplyFocusCommands(in context),
    };

    internal SystemDefinition DefineStreamingSystem() => new()
    {
        Name = StreamingSystemName,
        Phase = Phase.Streaming,
        Reads = ComponentMask.Of(TilePos),
        ChangeFilter = ComponentMask.Of(TilePos),
        WritesResources = BudgetPressureQueue is { } queue ? ResourceMask.Of(queue) : default,
        Begin = static (in SystemContext context) => context.World.Residency().BeginStreaming(in context),
        ForEachChunk = static (in SystemContext context, in ChunkView chunk) => context.World.Residency().CollectMovers(in context, in chunk),
        End = static (in SystemContext context) => context.World.Residency().EndStreaming(in context),
    };

    /// <summary>A registered catch-up function, applied to one Entity by the manager.</summary>
    internal interface ICatchUp
    {
        public ComponentType Component { get; }

        public void Apply(World world, EntityId id, long elapsedTicks);
    }

    private sealed class CatchUp<T>(ComponentType component, CatchUpAction<T> action) : ICatchUp
        where T : unmanaged
    {
        public ComponentType Component => component;

        public void Apply(World world, EntityId id, long elapsedTicks)
        {
            if (world.Has(id, component))
            {
                action(ref world.Get<T>(id, component), elapsedTicks);
            }
        }
    }
}

/// <summary>The <c>residency.*</c> counter ids of a schema with residency (see <see cref="StreamingCounters"/> for their meaning).</summary>
public readonly record struct StreamingCounterIds(
    CounterId Loads,
    CounterId LoadStalls,
    CounterId LoadsInFlight,
    CounterId Evictions,
    CounterId CatchUps,
    CounterId CatchUpTicks);

/// <summary>Registration entry point of the residency manager on the Core builder.</summary>
public static class ResidencyRegistration
{
    /// <summary>
    /// Gives Worlds of this schema streaming residency over the chunks of <paramref name="fields"/> (spec section 5.3).
    /// Entities carrying <paramref name="tilePos"/> (a <see cref="TilePos"/> Component) belong to the chunk they stand
    /// in and freeze with it. Call once per schema, after <see cref="WorldBuilderExtensions.AddFields"/>. Chunks the
    /// manager holds no bytes for come from <see cref="FieldSet.ChunkSource"/> (ProcGen's generation sets it).
    /// </summary>
    public static ResidencyBuilder AddResidency(this WorldBuilder builder, FieldsBuilder fields, ComponentType tilePos, ResidencyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);
        options ??= new ResidencyOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFocusPoints, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RingWidth, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.LoadDelayTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLoadsInFlight, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ResidentChunkBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CatchUpMaxTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CatchUpWorkBudget, 1);
        if (options.InitialResidency is not (ChunkResidency.Active or ChunkResidency.Unloaded))
        {
            throw new ArgumentException("InitialResidency must be Active or Unloaded.", nameof(options));
        }

        var residency = new ResidencyBuilder(builder, fields, tilePos, options);
        builder.AddExtension(residency);
        builder.AddSystem(ResidencyBuilder.DefineFocusSystem());
        builder.AddSystem(residency.DefineStreamingSystem());
        return residency;
    }

    /// <summary>The World's <see cref="ChunkResidencyManager"/>; throws when the schema registered none.</summary>
    public static ChunkResidencyManager Residency(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Extension<ChunkResidencyManager>();
    }
}
