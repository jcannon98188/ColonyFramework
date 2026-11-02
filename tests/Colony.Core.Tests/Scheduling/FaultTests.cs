namespace Colony.Core.Tests.Scheduling;

/// <summary>AC #6: a System exception aborts the Tick and marks the World faulted, naming the System.</summary>
public sealed class FaultTests
{
    [Fact]
    public void A_throwing_serial_system_aborts_the_tick_and_faults_the_world()
    {
        bool lateRan = false;
        var schema = new WorldBuilder()
            .AddSystem(new SystemDefinition { Name = "Fine", Phase = Phase.Begin, Begin = (in SystemContext _) => { } })
            .AddSystem(new SystemDefinition { Name = "Broken", Phase = Phase.Simulate, Begin = (in SystemContext _) => throw new InvalidDataException("boom") })
            .AddSystem(new SystemDefinition { Name = "Late", Phase = Phase.Late, Begin = (in SystemContext _) => lateRan = true })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });

        var fault = Assert.Throws<SystemFaultException>(() => world.Tick());

        Assert.Equal("Broken", fault.SystemName);
        Assert.Equal(0, fault.Tick);
        Assert.IsType<InvalidDataException>(fault.InnerException);
        Assert.Contains("Broken", fault.Message, StringComparison.Ordinal);
        Assert.False(lateRan);
        Assert.True(world.IsFaulted);
        Assert.Equal("Broken", world.Fault!.Value.SystemName);
        Assert.Equal(Phase.Simulate, world.Fault.Value.Phase);
        Assert.Equal(0, world.CurrentTick);
        Assert.Throws<InvalidOperationException>(() => world.Tick());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_throwing_chunk_job_on_any_worker_names_its_system(int workers)
    {
        var health = new ComponentType(0);
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition
            {
                Name = "Harmless",
                Writes = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext _, in ChunkView _) => { },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Explodes",
                Reads = ComponentMask.Of(health),
                After = ["Harmless"],
                ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
                {
                    if (context.JobIndex == 3)
                    {
                        throw new InvalidDataException("chunk 3");
                    }
                },
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = workers, InitialEntityCapacity = 1 << 16 });
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 40_000; i++)
        {
            buffer.Create(ComponentMask.Of(health));
        }

        world.Playback(buffer);
        world.ResetScratch();

        var fault = Assert.Throws<SystemFaultException>(() => world.Tick());

        Assert.Equal("Explodes", fault.SystemName);
        Assert.Equal("chunk 3", fault.InnerException!.Message);
        Assert.True(world.IsFaulted);
    }
}
