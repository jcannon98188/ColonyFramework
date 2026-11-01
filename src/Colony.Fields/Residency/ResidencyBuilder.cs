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

    /// <summary><see cref="CommandRejected.Reason"/> of a <see cref="FocusPoint"/> that would exceed <see cref="ResidencyOptions.MaxFocusPoints"/>.</summary>
    public const int RejectTooManyFocusPoints = 1;

    private readonly FieldsBuilder _fields;

    internal ResidencyBuilder(WorldBuilder builder, FieldsBuilder fields, ComponentType tilePos, ResidencyOptions options)
    {
        _fields = fields;
        TilePos = tilePos;
        Options = options;
        FocusCommand = builder.AddInputCommand<FocusPoint>(FocusCommandName);
        Frozen = builder.AddComponent<Frozen>(FrozenComponentName, ComponentOptions.NonFragmenting).Component<Frozen>();
        Pinned = builder.AddComponent<Pinned>(PinnedComponentName, ComponentOptions.NonFragmenting).Component<Pinned>();
    }

    /// <summary>The Component that makes an Entity chunk-owned.</summary>
    public ComponentType TilePos { get; }

    public ComponentType Frozen { get; }

    public ComponentType Pinned { get; }

    public InputCommandType FocusCommand { get; }

    public ResidencyOptions Options { get; }

    /// <summary>
    /// Where Unloaded chunks without stored bytes come from, resolved once per World. ProcGen's <c>AddGeneration</c>
    /// sets it when the residency manager is registered first; a Game without generation leaves it null, and every
    /// unloaded chunk is then kept as its save section.
    /// </summary>
    public Func<World, IChunkSource>? ChunkSource { get; set; }

    public override string Name => ExtensionName;

    public override ulong LayoutHash
    {
        get
        {
            ulong hash = Hash64.Combine(LayoutSeed, (ulong)TilePos.Id);
            hash = Hash64.Combine(hash, (ulong)Options.MaxFocusPoints);
            return Hash64.Combine(hash, Options.HibernateInsteadOfUnload ? 1UL : 0UL);
        }
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
        Begin = static (in SystemContext context) => context.World.Residency().BeginStreaming(in context),
        ForEachChunk = static (in SystemContext context, in ChunkView chunk) => context.World.Residency().CollectMovers(in context, in chunk),
        End = static (in SystemContext context) => context.World.Residency().EndStreaming(in context),
    };
}

/// <summary>Registration entry point of the residency manager on the Core builder.</summary>
public static class ResidencyRegistration
{
    /// <summary>
    /// Gives Worlds of this schema streaming residency over the chunks of <paramref name="fields"/> (spec section 5.3).
    /// Entities carrying <paramref name="tilePos"/> (a <see cref="TilePos"/> Component) belong to the chunk they stand
    /// in and freeze with it. Call once per schema, after <see cref="WorldBuilderExtensions.AddFields"/> and before
    /// a chunk source such as ProcGen's <c>AddGeneration</c>.
    /// </summary>
    public static ResidencyBuilder AddResidency(this WorldBuilder builder, FieldsBuilder fields, ComponentType tilePos, ResidencyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);
        options ??= new ResidencyOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFocusPoints, 1);
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
