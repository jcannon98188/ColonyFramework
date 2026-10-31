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

        // Spawn ran 2 of 8 Ticks, Heal 3, MovePosition and Watch 8 each; one chunk; Watch skipped it 4 times.
        Assert.Equal(
            new SchedulerCounters(SystemRuns: 21, SystemsSkippedByRate: 11, ChunksVisited: 15, ChunksSkippedAsleep: 0, ChunksSkippedByChangeFilter: 4, ChunksSkippedDisabled: 0),
            world.SchedulerCounters);
    }

    [Fact]
    public void An_every_n_ticks_system_runs_only_on_ticks_matching_its_offset()
    {
        var explicitTicks = new List<long>();
        var derivedTicks = new List<long>();
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition { Name = "Fifth", Every = 5, Offset = 3, Begin = (in SystemContext c) => explicitTicks.Add(c.Tick) })
            .AddSystem(new SystemDefinition { Name = "Derived", Every = 4, Begin = (in SystemContext c) => derivedTicks.Add(c.Tick) })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });

        for (int i = 0; i < 13; i++)
        {
            world.Tick();
        }

        Assert.Equal([3L, 8L], explicitTicks);
        int offset = schema.System("Derived").Offset;
        Assert.InRange(offset, 0, 3);
        Assert.Equal(Enumerable.Range(0, 13).Where(t => t % 4 == offset).Select(t => (long)t), derivedTicks);
        Assert.Equal(2 + derivedTicks.Count, world.SchedulerCounters.SystemRuns);
        Assert.Equal(26 - 2 - derivedTicks.Count, world.SchedulerCounters.SystemsSkippedByRate);
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

        Assert.Equal(
            new SchedulerCounters(SystemRuns: 9, SystemsSkippedByRate: 5, ChunksVisited: 3, ChunksSkippedAsleep: 4, ChunksSkippedByChangeFilter: 0, ChunksSkippedDisabled: 0),
            world.SchedulerCounters);
    }

    [Fact]
    public void A_chunk_whose_queried_component_is_disabled_on_every_row_is_skipped_unless_the_system_ignores_enable_bits()
    {
        var watched = new List<long>();
        var revived = new List<long>();
        var health = new ComponentType(0);
        var schema = new WorldBuilder().AddComponent<Health>("health")
            .AddSystem(new SystemDefinition
            {
                Name = "Disabler",
                Phase = Phase.Begin,
                Every = 4,
                Offset = 0,
                Writes = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        chunk.SetEnabled(health, i, false);
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Watcher",
                Reads = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext c, in ChunkView _) => watched.Add(c.Tick),
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Reviver",
                Phase = Phase.Late,
                Every = 4,
                Offset = 2,
                Writes = ComponentMask.Of(health),
                IgnoreEnableBits = true,
                ForEachChunk = (in SystemContext c, in ChunkView chunk) =>
                {
                    revived.Add(c.Tick);
                    Assert.Equal(0, chunk.EnabledCount(health));
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        chunk.SetEnabled(health, i, true);
                    }
                },
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        buffer.Create(ComponentMask.Of(health));
        buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);
        world.ResetScratch();

        for (int i = 0; i < 8; i++)
        {
            world.Tick();
        }

        // Disabled in Begin at 0 and 4, re-enabled in Late at 2 and 6: the Watcher sees the chunk only at 3 and 7.
        Assert.Equal([3L, 7L], watched);
        Assert.Equal([2L, 6L], revived);
        Assert.Equal(
            new SchedulerCounters(SystemRuns: 12, SystemsSkippedByRate: 12, ChunksVisited: 6, ChunksSkippedAsleep: 0, ChunksSkippedByChangeFilter: 0, ChunksSkippedDisabled: 6),
            world.SchedulerCounters);
        // Flipping bits caused no structural change.
        Assert.Equal(2, world.EntityCount);
        Assert.Equal(0, world.Counters.Adds + world.Counters.Removes);
    }
}
