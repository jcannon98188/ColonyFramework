using Colony.Fields;

namespace Colony.Fields.Tests.Scenario;

/// <summary>AC #3: queries from parallel Systems allocate nothing and yield identical results across thread counts. AC #2: the update is incremental.</summary>
public sealed class SpatialIndexParallelTests
{
    [Fact]
    public void Parallel_queries_yield_identical_results_on_1_and_4_workers()
    {
        ulong[]? baseline = null;
        foreach (int workers in new[] { 1, 4 })
        {
            var scenario = SpatialScenario.Build(probe: true);
            using var world = scenario.CreateWorld(workers, seed: 2024);
            Assert.Equal(workers, world.WorkerCount);
            for (int i = 0; i < 25; i++)
            {
                world.Tick();
            }

            var hashes = scenario.ProbeHashes.ToArray();
            Assert.Equal(25, hashes.Length);
            Assert.True(hashes.Distinct().Count() > 20, "the probes see a changing World");
            if (baseline is null)
            {
                baseline = hashes;
            }
            else
            {
                Assert.Equal(baseline, hashes);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_steady_state_tick_with_index_update_and_parallel_queries_allocates_nothing(int workers)
    {
        var scenario = SpatialScenario.Build(probe: true);
        using var world = scenario.CreateWorld(workers, seed: 99);
        for (int i = 0; i < 30; i++)
        {
            world.Tick();
        }

        long mainBefore = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        for (int i = 0; i < 10; i++)
        {
            world.Tick();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - mainBefore);
        Assert.Equal(0, world.WorkerAllocatedBytes - workersBefore);
    }

    [Fact]
    public void The_update_touches_only_chunks_whose_tile_positions_changed()
    {
        var builder = new WorldBuilder().AddComponent<TilePos>("tile.pos").AddComponent<int>("tag").DebugValidation(false);
        var tilePos = builder.Component<TilePos>();
        var tag = builder.Component<int>();
        var fields = builder.AddFields(SpatialScenario.Size);
        builder.AddSpatialIndex(fields, tilePos);
        long nudge = -1;
        builder.AddSystem(new SystemDefinition
        {
            Name = "NudgeTagged",
            Writes = ComponentMask.Of(tilePos),
            All = ComponentMask.Of(tilePos, tag),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                if (context.Tick == nudge)
                {
                    chunk.Column<TilePos>(tilePos)[0] += new TilePos(1, 0, 0);
                }
            },
        });
        var schema = builder.Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 100; i++)
        {
            var e = buffer.Create(ComponentMask.Of(tilePos));
            buffer.Add(e, tilePos, new TilePos(i % 80, i % 48, 0));
        }

        for (int i = 0; i < 10; i++)
        {
            var e = buffer.Create(ComponentMask.Of(tilePos, tag));
            buffer.Add(e, tilePos, new TilePos(i, 0, 1));
        }

        world.Playback(buffer);
        world.ResetScratch();
        var index = world.SpatialIndex();

        world.Tick();
        Assert.Equal(110, index.Count);
        Assert.Equal(110, index.LastUpdateEntries);

        // The NudgeTagged System writes every Tick (stamping the tagged chunk) but changes nothing; the untagged chunk is never stamped.
        world.Tick();
        Assert.Equal(10, index.LastUpdateEntries);

        nudge = world.CurrentTick;
        world.Tick();
        Assert.Equal(10, index.LastUpdateEntries);
        Span<SpatialHit> hits = stackalloc SpatialHit[4];
        Assert.Equal(2, index.QueryTile(new TilePos(1, 0, 1), hits)); // the nudged Entity joined the one already there
        Assert.Equal(0, index.QueryTile(new TilePos(0, 0, 1), hits));

        // Destroying an untagged Entity stamps its chunk and triggers the sweep.
        EntityId victim = default;
        foreach (var chunk in world.Query(ComponentMask.Of(tilePos), ComponentMask.Of(tag)))
        {
            victim = chunk.Entities[5];
        }

        buffer.Destroy(victim);
        world.Playback(buffer);
        world.ResetScratch();
        world.Tick();
        Assert.Equal(109, index.Count);
        Assert.Equal(109, index.LastUpdateEntries);
        Assert.Equal(1, index.LastUpdateRemoved);
    }
}
