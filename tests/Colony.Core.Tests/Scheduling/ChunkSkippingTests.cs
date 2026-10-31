namespace Colony.Core.Tests.Scheduling;

/// <summary>Chunk-level skipping via change versions and the asleep bit (spec section 4.3, minimal form).</summary>
public sealed class ChunkSkippingTests
{
    [Fact]
    public void A_change_filtered_system_visits_only_chunks_whose_columns_changed_since_it_last_ran()
    {
        var visited = new List<int>();
        var builder = new WorldBuilder().AddComponent<Health>("health").AddComponent<Position>("position");
        var health = new ComponentType(0);
        var position = new ComponentType(1);
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Spawn",
                Phase = Phase.Begin,
                Every = 4,
                Offset = 2,
                Begin = (in SystemContext c) => c.Commands.Create(ComponentMask.Of(health, position)),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Heal",
                Every = 3,
                Offset = 0,
                Writes = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) => chunk.Column<Health>(health)[0].Value++,
            })
            .AddSystem(new SystemDefinition
            {
                Name = "MovePosition",
                Every = 1,
                Writes = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) => chunk.Column<Position>(position)[0].X++,
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Watch",
                Reads = ComponentMask.Of(health),
                ChangeFilter = ComponentMask.Of(health),
                After = ["Heal"],
                ForEachChunk = (in SystemContext c, in ChunkView _) => visited.Add((int)c.Tick),
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(health, position));
        world.Playback(buffer);
        world.ResetScratch();

        for (int i = 0; i < 8; i++)
        {
            world.Tick();
        }

        // Tick 0: Heal wrote health; 1: only position changed (not in the filter); 2: Spawn added a row (structural);
        // 3: Heal; 4, 5: nothing; 6: Heal and Spawn; 7: nothing.
        Assert.Equal([0, 2, 3, 6], visited);
    }

    [Fact]
    public void Asleep_chunks_are_skipped_until_a_structural_change_wakes_them()
    {
        var visited = new List<long>();
        var health = new ComponentType(0);
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition
            {
                Name = "Sleeper",
                Reads = ComponentMask.Of(health),
                SkipAsleepChunks = true,
                ForEachChunk = (in SystemContext c, in ChunkView chunk) =>
                {
                    visited.Add(c.Tick);
                    chunk.SetAsleep(true);
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Spawn",
                Phase = Phase.Late,
                Every = 3,
                Offset = 2,
                Begin = (in SystemContext c) => c.Commands.Create(ComponentMask.Of(health)),
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);
        world.ResetScratch();

        for (int i = 0; i < 7; i++)
        {
            world.Tick();
        }

        // Visited at 0 (then asleep), woken by the spawn at the end of Tick 2 -> visited at 3, woken at 5 -> visited at 6.
        Assert.Equal([0L, 3L, 6L], visited);
        foreach (var chunk in world.Query(health))
        {
            Assert.True(chunk.IsAsleep);
        }
    }
}
