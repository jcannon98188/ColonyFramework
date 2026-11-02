namespace Colony.Core.Tests.Buffers;

/// <summary>
/// AC #3 under the scheduler: chunk jobs grow and shrink buffers from worker threads through the shared heap,
/// the World hash is the same on 1 and 4 workers (handles never reach the hash), and a steady-state Tick
/// allocates nothing on any Tick thread.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class BufferSystemTests
{
    private const int Entities = 4_000;
    private const int Ticks = 24;

    private static WorldSchema BuildSchema(out ComponentType inventory)
    {
        var builder = new WorldBuilder().AddComponent<Position>("position").DebugValidation(false);
        builder.AddBufferComponent<int, Inline4<int>>("inventory");
        var type = builder.Component("inventory");
        inventory = type;
        return builder
            .AddSystem(new SystemDefinition
            {
                Name = "Stock",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(type),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
                {
                    int tick = (int)context.World.CurrentTick;
                    for (int row = 0; row < chunk.Count; row++)
                    {
                        var items = chunk.Buffer<int>(type, row);
                        items.Add(tick * 31 + row);
                        if (items.Length > 12)
                        {
                            items.Clear(); // back inline: the overflow block goes back to the heap
                        }
                    }
                },
            })
            .Build();
    }

    private static World CreateWorld(WorldSchema schema, ComponentType inventory, int workers)
    {
        var world = schema.CreateWorld(new WorldOptions { WorkerThreads = workers, InitialEntityCapacity = Entities * 2 });
        var position = schema.Component<Position>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Entities; i++)
        {
            buffer.Create(ComponentMask.Of(position, inventory));
        }

        world.Playback(buffer);
        return world;
    }

    [Fact]
    public void Buffers_grown_from_chunk_jobs_hash_identically_on_1_and_4_workers()
    {
        var schema = BuildSchema(out var inventory);
        using var one = CreateWorld(schema, inventory, 1);
        using var four = CreateWorld(schema, inventory, 4);

        for (int tick = 0; tick < Ticks; tick++)
        {
            one.Tick();
            four.Tick();
            Assert.Equal(one.Hash(), four.Hash());
        }

        Assert.True(one.BufferCounters.Overflows >= Entities, "every buffer must have overflowed at least once");
        Assert.True(one.BufferCounters.Frees > 0, "clearing must have released blocks");
        Assert.Equal(one.BufferCounters.Overflows, four.BufferCounters.Overflows);
        Assert.Equal(one.BufferCounters.BlocksInUse, four.BufferCounters.BlocksInUse);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_steady_state_tick_with_buffer_churn_allocates_nothing(int workers)
    {
        var schema = BuildSchema(out var inventory);
        using var world = CreateWorld(schema, inventory, workers);
        for (int i = 0; i < 2 * Ticks; i++)
        {
            world.Tick();
        }

        world.Hash();
        long mainBefore = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        for (int i = 0; i < Ticks; i++)
        {
            world.Tick();
        }

        world.Hash();
        long mainAllocated = GC.GetAllocatedBytesForCurrentThread() - mainBefore;
        long workersAllocated = world.WorkerAllocatedBytes - workersBefore;

        Assert.Equal(0, mainAllocated);
        Assert.Equal(0, workersAllocated);
    }
}
