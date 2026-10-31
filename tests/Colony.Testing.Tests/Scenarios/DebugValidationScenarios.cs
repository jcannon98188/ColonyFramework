using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

/// <summary>Shared shape of the failing-on-purpose scenarios: a handful of Entities with one Component and one serial System that misbehaves.</summary>
public abstract class MisbehavingScenario : Scenario
{
    public const int Entities = 40;

    public override ulong Seed { get; init; } = 7;

    protected abstract void Misbehave(World world, ComponentType counter);

    public override void Compose(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var counter = builder.AddComponent<Counter>("counter").Component<Counter>();
        builder.AddSystem(new SystemDefinition
        {
            Name = Name,
            Phase = Phase.Simulate,
            Reads = ComponentMask.Of(counter),
            End = (in SystemContext context) => Misbehave(context.World, counter),
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
}

/// <summary>
/// Stale ref check: the System keeps a <see cref="ChunkView"/> across a structural change (it destroys an
/// Entity of that chunk and plays the command back mid-Tick), then touches the view. Debug validation
/// throws from the view; release reads whatever the chunk now holds.
/// </summary>
public sealed class StaleViewScenario : MisbehavingScenario
{
    public override string Name => "debug-stale-view";

    protected override void Misbehave(World world, ComponentType counter)
    {
        foreach (var chunk in world.Query(counter))
        {
            if (chunk.Entities.Length < 2)
            {
                return;
            }

            var buffer = world.CreateCommandBuffer();
            buffer.Destroy(chunk.Entities[0]);
            world.Playback(buffer);
            _ = chunk.Column<Counter>(counter); // stale: the chunk's structural version moved on
            return;
        }
    }
}

/// <summary>
/// Poisoning: the System writes a marker into a rented chunk (or a scratch block), frees it, then reads
/// it back as if it were still valid. With poisoning on, the freed memory reads as the poison byte and
/// the System throws (the use-after-free is caught); off, the stale marker is still there and nothing notices.
/// </summary>
public sealed class UseAfterFreeScenario : MisbehavingScenario
{
    public const byte Marker = 42;

    public enum Memory
    {
        Chunk,
        Scratch,
    }

    public Memory Kind { get; init; } = Memory.Chunk;

    public override string Name => Kind == Memory.Chunk ? "debug-use-after-free-chunk" : "debug-use-after-free-scratch";

    /// <summary>What the last read of freed memory returned.</summary>
    public byte LastRead { get; private set; }

    private nint _scratch;

    public override void Populate(World world)
    {
        _scratch = 0; // per World: the harness runs one World per worker count
        base.Populate(world);
    }

    protected override unsafe void Misbehave(World world, ComponentType counter)
    {
        byte read;
        if (Kind == Memory.Chunk)
        {
            byte* chunk = world.Allocator.Rent();
            chunk[0] = Marker;
            world.Allocator.Return(chunk);
            read = chunk[0];
        }
        else
        {
            // The block written in the previous Tick was freed by that Tick's scratch reset.
            byte* previous = (byte*)_scratch;
            read = previous is null ? Marker : previous[0];
            byte* block = world.Scratch.Alloc(64);
            block[0] = Marker;
            _scratch = (nint)block;
        }

        LastRead = read;
        if (read != Marker)
        {
            throw new InvalidOperationException($"use-after-free: freed memory read back as 0x{read:X2} instead of the marker {Marker}.");
        }
    }
}

/// <summary>Leak check: the System rents a chunk on the first Tick and never returns it. Debug validation reports it when the World is disposed.</summary>
public sealed class LeakingScenario : MisbehavingScenario
{
    public override string Name => "debug-leak";

    protected override unsafe void Misbehave(World world, ComponentType counter)
    {
        if (world.CurrentTick == 0)
        {
            _ = world.Allocator.Rent();
        }
    }
}
