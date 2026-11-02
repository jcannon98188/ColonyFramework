using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

public struct Counter
{
    public ulong Value;
}

/// <summary>
/// The smallest scenario that evolves every Tick: 50k Entities (a dozen chunks, so jobs land on worker
/// threads) whose counters advance by a per-Entity random step. Knobs make it misbehave on purpose, all
/// numbered like the harness (Tick 1 is the first Advance; Core's <c>context.Tick</c> is one less):
/// <see cref="DivergeFromTick"/> keys the step by the worker count from that Tick on (the determinism
/// negative control), <see cref="ThrowAtTick"/> throws from the System, <see cref="AllocateFromTick"/>
/// allocates on whichever thread runs the chunk job.
/// </summary>
public sealed class CounterScenario : Scenario
{
    public const int Entities = 50_000;

    public override string Name => "counter";

    public override int InitialEntityCapacity => Entities * 2;

    public override ulong Seed { get; init; } = 42;

    public long DivergeFromTick { get; init; } = long.MaxValue;

    public long ThrowAtTick { get; init; } = long.MaxValue;

    public long AllocateFromTick { get; init; } = long.MaxValue;

    public long StopAllocatingAtTick { get; init; } = long.MaxValue;

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var counter = builder.AddComponent<Counter>("counter").DebugValidation(false).Component<Counter>();
        builder.AddSystem(new SystemDefinition
        {
            Name = "Count",
            Phase = Phase.Simulate,
            Writes = ComponentMask.Of(counter),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                if (context.Tick == ThrowAtTick - 1)
                {
                    throw new InvalidOperationException("on purpose");
                }

                if (context.Tick >= AllocateFromTick - 1 && context.Tick < StopAllocatingAtTick - 1)
                {
                    Sink = new byte[64];
                }

                var random = context.Tick >= DivergeFromTick - 1
                    ? context.Random.Derive((ulong)context.World.WorkerCount)
                    : context.Random;
                var values = chunk.Column<Counter>(counter);
                var entities = chunk.Entities;
                for (int i = 0; i < values.Length; i++)
                {
                    values[i].Value += random.UInt32At(entities[i].Value);
                }
            },
        });
    }

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var counter = world.Schema.Component<Counter>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Entities; i++)
        {
            var e = buffer.Create(ComponentMask.Of(counter));
            buffer.Add(e, counter, new Counter { Value = (ulong)i });
        }

        world.Playback(buffer);
        world.ResetScratch();
    }

    public static object? Sink { get; private set; }
}
