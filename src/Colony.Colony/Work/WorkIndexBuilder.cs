using Colony.Fields;
using Colony.Hashing;

namespace Colony.Jobs;

/// <summary>Registration options for <see cref="WorkIndexRegistration.AddWorkIndex"/>.</summary>
public sealed class WorkIndexOptions
{
    /// <summary>
    /// Number of work types the index keeps lists for (item types are 0..count-1). Memory is one list head per
    /// (type, chunk): 4 bytes x types x Fields chunks, so size it to the Game's work types, not generously.
    /// </summary>
    public int WorkTypeCount { get; init; } = 16;

    /// <summary>Entity slots the per-Entity tables start with (grow-only; size it for the load so steady state allocates nothing).</summary>
    public int InitialCapacity { get; init; } = 1024;
}

/// <summary>
/// Registers the work index (spec section 9.2): the <see cref="WorkItem"/> Component, a
/// <see cref="WorldExtensionDefinition"/> named "work-index" sized to the Fields chunk grid, and the Late-Phase
/// System <see cref="SystemName"/> that keeps it current from WorkItem and TilePos changes. Created by
/// <see cref="WorkIndexRegistration.AddWorkIndex"/>.
/// </summary>
public sealed class WorkIndexBuilder : WorldExtensionDefinition
{
    /// <summary>Layout hash seed ("WKIX"). The index is derived state, so the hash covers only what it follows.</summary>
    private const ulong LayoutSeed = 0x574B_4958UL;

    public const string WorkItemName = "colony.work.item";

    /// <summary>Name of the Late-Phase System that updates the index; order Late Systems that query it After it.</summary>
    public const string SystemName = "colony.work.index";

    private readonly FieldsBuilder _fields;

    internal WorkIndexBuilder(FieldsBuilder fields, ComponentType tilePos, ComponentType workItem, WorkIndexOptions options)
    {
        _fields = fields;
        TilePos = tilePos;
        WorkItem = workItem;
        Options = options;
    }

    /// <summary>The grid position Component the index follows.</summary>
    public ComponentType TilePos { get; }

    /// <summary>The <see cref="Jobs.WorkItem"/> Component; add it (with TilePos) to every work item Entity.</summary>
    public ComponentType WorkItem { get; }

    public WorkIndexOptions Options { get; }

    /// <summary>Both Components: the signature of a work item Entity.</summary>
    public ComponentMask ItemComponents => ComponentMask.Of(TilePos, WorkItem);

    public override string Name => "work-index";

    public override ulong LayoutHash => Hash64.Combine(Hash64.Combine(LayoutSeed, (ulong)TilePos.Id), (ulong)WorkItem.Id);

    public override IWorldExtension Create(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _fields.Freeze();
        return new WorkIndex(world, _fields.Size, TilePos, WorkItem, Options);
    }

    internal SystemDefinition DefineSystem() => new()
    {
        Name = SystemName,
        Phase = Phase.Late,
        All = ItemComponents,
        Reads = ItemComponents,
        ChangeFilter = ItemComponents,
        Begin = static (in SystemContext context) => context.World.WorkIndex().BeginUpdate(context.JobCount),
        ForEachChunk = static (in SystemContext context, in ChunkView chunk) => context.World.WorkIndex().Collect(context.JobIndex, in chunk),
        End = static (in SystemContext context) => context.World.WorkIndex().EndUpdate(),
    };
}

/// <summary>Registration entry point of the work index on the Core builder.</summary>
public static class WorkIndexRegistration
{
    /// <summary>
    /// Registers the <see cref="WorkItem"/> Component and indexes every available item by work type and the Fields
    /// chunk its <paramref name="tilePos"/> stands in, updated in the Late Phase. Call once per schema, after
    /// <see cref="WorldBuilderExtensions.AddFields"/>.
    /// </summary>
    public static WorkIndexBuilder AddWorkIndex(this WorldBuilder builder, FieldsBuilder fields, ComponentType tilePos, WorkIndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);
        options ??= new WorkIndexOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WorkTypeCount, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InitialCapacity, 1, nameof(options));

        builder.AddComponent<WorkItem>(WorkIndexBuilder.WorkItemName);
        var index = new WorkIndexBuilder(fields, tilePos, builder.Component<WorkItem>(), options);
        builder.AddExtension(index);
        builder.AddSystem(index.DefineSystem());
        return index;
    }

    /// <summary>The World's <see cref="WorkIndex"/>; throws when the schema registered none.</summary>
    public static WorkIndex WorkIndex(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Extension<WorkIndex>();
    }
}
