namespace Colony.Fields;

/// <summary>
/// Registers the spatial index over grid Entities (spec section 5.1): a <see cref="WorldExtensionDefinition"/>
/// sized to the Fields chunk grid plus the Late-Phase System <see cref="SystemName"/> that keeps it current
/// from TilePos changes. Created by <see cref="SpatialIndexRegistration.AddSpatialIndex"/>.
/// </summary>
public sealed class SpatialIndexBuilder : WorldExtensionDefinition
{
    /// <summary>Layout hash seed ("SPIX"). The index holds derived state only, so the hash covers nothing but the TilePos Component.</summary>
    private const ulong LayoutSeed = 0x5350_4958UL;

    /// <summary>Name of the Late-Phase System that updates the index; order Game Systems that query it with After/Before.</summary>
    public const string SystemName = "spatial.index";

    private readonly FieldsBuilder _fields;

    internal SpatialIndexBuilder(FieldsBuilder fields, ComponentType tilePos)
    {
        _fields = fields;
        TilePos = tilePos;
    }

    /// <summary>The Component the index follows.</summary>
    public ComponentType TilePos { get; }

    public override string Name => "spatial-index";

    public override ulong LayoutHash => Hashing.Hash64.Combine(LayoutSeed, (ulong)TilePos.Id);

    public override IWorldExtension Create(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _fields.Freeze();
        return new SpatialIndex(world, _fields.Layout, TilePos);
    }

    internal SystemDefinition DefineSystem() => new()
    {
        Name = SystemName,
        Phase = Phase.Late,
        Reads = ComponentMask.Of(TilePos),
        ChangeFilter = ComponentMask.Of(TilePos),
        Begin = static (in SystemContext context) => context.World.SpatialIndex().BeginUpdate(context.JobCount),
        ForEachChunk = static (in SystemContext context, in ChunkView chunk) => context.World.SpatialIndex().Collect(context.JobIndex, in chunk),
        End = static (in SystemContext context) => context.World.SpatialIndex().EndUpdate(),
    };
}

/// <summary>Registration entry point of the spatial index on the Core builder.</summary>
public static class SpatialIndexRegistration
{
    /// <summary>
    /// Indexes every Entity carrying <paramref name="tilePos"/> (a <see cref="TilePos"/> Component) by the Fields
    /// chunk it stands in, updated in the Late Phase. Call once per schema, after <see cref="WorldBuilderExtensions.AddFields"/>.
    /// </summary>
    public static SpatialIndexBuilder AddSpatialIndex(this WorldBuilder builder, FieldsBuilder fields, ComponentType tilePos)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fields);
        var index = new SpatialIndexBuilder(fields, tilePos);
        builder.AddExtension(index);
        builder.AddSystem(index.DefineSystem());
        return index;
    }

    /// <summary>The World's <see cref="SpatialIndex"/>; throws when the schema registered none.</summary>
    public static SpatialIndex SpatialIndex(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Extension<SpatialIndex>();
    }
}
