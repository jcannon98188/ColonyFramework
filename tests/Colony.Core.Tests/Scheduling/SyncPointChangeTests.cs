namespace Colony.Core.Tests.Scheduling;

/// <summary>
/// Structural changes made at a sync point (host playback, Phase-boundary playback) must be visible to a
/// change-filtered System even when that System ran in the wave just before the sync point: the Late-Phase
/// spatial index (spec section 5.1) consumes TilePos changes this way.
/// </summary>
public sealed class SyncPointChangeTests
{
    [Fact]
    public void A_change_filtered_late_system_sees_entities_created_by_host_playbacks_between_ticks()
    {
        var visited = new List<long>();
        var health = new ComponentType(0);
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition
            {
                Name = "Watch",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(health),
                ChangeFilter = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext c, in ChunkView _) => visited.Add(c.Tick),
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);
        world.ResetScratch();

        world.Tick(); // 0: the chunk was created before the first Tick
        world.Tick(); // 1: nothing changed
        buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);
        world.ResetScratch();
        world.Tick(); // 2: a row was added between Ticks
        world.Tick(); // 3: nothing

        Assert.Equal([0L, 2L], visited);
    }

    [Fact]
    public void A_change_filtered_system_in_the_last_wave_of_a_phase_sees_that_phase_boundary_playback()
    {
        var visited = new List<long>();
        var health = new ComponentType(0);
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition
            {
                Name = "Spawn",
                Phase = Phase.Late,
                Every = 4,
                Offset = 1,
                Begin = (in SystemContext c) => c.Commands.Create(ComponentMask.Of(health)),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Watch",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(health),
                ChangeFilter = ComponentMask.Of(health),
                After = ["Spawn"],
                ForEachChunk = (in SystemContext c, in ChunkView _) => visited.Add(c.Tick),
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        for (int i = 0; i < 8; i++)
        {
            world.Tick();
        }

        // Spawn records a create at Ticks 1 and 5; the Late boundary plays it back after Watch ran, so Watch sees it next Tick.
        Assert.Equal([2L, 6L], visited);
    }
}
