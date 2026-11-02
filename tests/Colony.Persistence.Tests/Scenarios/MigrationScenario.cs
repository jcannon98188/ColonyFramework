using System.Runtime.InteropServices;
using Colony.Persistence;
namespace Colony.Persistence.Tests;

public struct Cell
{
    public int Energy;
}

/// <summary>
/// TASK-37 AC #4: a World whose persisted shapes exist in two generations. <see cref="Version"/> 1 is the shape
/// the committed golden save was made with (Resource <see cref="TallyV1"/>, <see cref="LedgerDefinition"/>
/// format 1); version 2 is today's shape (<see cref="TallyV2"/>, ledger format 2). Cells gain energy every Tick
/// in parallel chunk jobs; the Late System reduces the energy into the Tally Resource in job order and the
/// ledger extension appends an entry per Tick, so every persisted shape is World state that the hash covers and
/// the continuation after a migrated load must reproduce on any worker count.
/// </summary>
public sealed class MigrationScenario : Testing.Scenario
{
    public const string TallyName = "m.tally";
    public const string CellName = "m.cell";

    public override string Name => Version == 1 ? "migration-v1" : "migration-v2";

    public override ulong Seed { get; init; } = 41;

    /// <summary>1 = the golden's shapes, 2 = the current shapes.</summary>
    public int Version { get; init; } = 2;

    public int Cells { get; init; } = 300;

    public override int InitialEntityCapacity => Cells * 2;

    /// <summary>The Resource and extension migrations from version 1 to version 2.</summary>
    public static MigrationRegistry MigrationsFromV1(WorldSchema v1, WorldSchema v2)
    {
        ArgumentNullException.ThrowIfNull(v1);
        ArgumentNullException.ThrowIfNull(v2);
        return new MigrationRegistry()
            .AddResource<TallyV1, TallyV2>(TallyName, v1.DescribeResource(v1.Resource(TallyName)).LayoutHash, v2.DescribeResource(v2.Resource(TallyName)).LayoutHash, TallyV1ToV2)
            .AddExtension(LedgerDefinition.ExtensionName, 1, 2, Ledger.MigrateV1ToV2);
    }

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddComponent<Cell>(CellName);
        var cell = builder.Component<Cell>();
        ResourceType tally = Version == 1 ? builder.AddResource<TallyV1>(TallyName).Resource<TallyV1>() : builder.AddResource<TallyV2>(TallyName).Resource<TallyV2>();
        builder.AddExtension(new LedgerDefinition(Version));
        var state = new State(Version);
        builder
            .AddSystem(new SystemDefinition
            {
                Name = "m.charge",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(cell),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => Charge(in chunk, cell),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "m.tally",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(cell),
                WritesResources = ResourceMask.Of(tally),
                Begin = (in SystemContext context) => state.Prepare(context.JobCount),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) => state.Sum(in context, in chunk, cell),
                End = (in SystemContext context) => state.Finish(in context, tally),
            });
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var cell = world.Schema.Component<Cell>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Cells; i++)
        {
            var e = buffer.Create(ComponentMask.Of(cell));
            buffer.Add(e, cell, new Cell { Energy = i % 7 });
        }

        world.Playback(buffer);
        world.ResetScratch();
    }

    private static void Charge(in ChunkView chunk, ComponentType cell)
    {
        var cells = chunk.Column<Cell>(cell);
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].Energy += 1 + (i & 3);
        }
    }

    private static void TallyV1ToV2(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var from = MemoryMarshal.Read<TallyV1>(source);
        MemoryMarshal.Write(destination, new TallyV2 { Total = from.Total, Samples = 0 });
    }

    private sealed class State(int version)
    {
        private long[] _sums = [];

        public void Prepare(int jobCount)
        {
            if (_sums.Length < jobCount)
            {
                _sums = new long[jobCount * 2];
            }
        }

        public void Sum(in SystemContext context, in ChunkView chunk, ComponentType cell)
        {
            long sum = 0;
            foreach (ref readonly var c in chunk.Column<Cell>(cell))
            {
                sum += c.Energy;
            }

            _sums[context.JobIndex] = sum;
        }

        public void Finish(in SystemContext context, ResourceType tally)
        {
            long sum = 0;
            for (int j = 0; j < context.JobCount; j++)
            {
                sum += _sums[j];
            }

            if (version == 1)
            {
                context.Resource<TallyV1>(tally).Total += sum;
            }
            else
            {
                ref var v2 = ref context.Resource<TallyV2>(tally);
                v2.Total += sum;
                v2.Samples++;
            }
        }
    }
}
