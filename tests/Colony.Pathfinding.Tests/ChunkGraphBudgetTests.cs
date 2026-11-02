using Colony.Fields;
using Colony.Pathfinding.Tests.Scenarios;
using Colony.Testing;

namespace Colony.Pathfinding.Tests;

/// <summary>
/// TASK-39 AC #1: the HPA* chunk graph holds at most <see cref="PathfindingOptions.ChunkGraphMemoryBudgetBytes"/>
/// (past it the least recently used chunks are evicted and rebuilt on the next read, never below
/// <see cref="PathfindingOptions.MinimumChunkGraphChunks"/> resident chunks), its memory is in the Tick record,
/// and the budget never changes a result.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class ChunkGraphBudgetTests
{
    [Fact]
    public void A_tiny_budget_evicts_chunks_but_every_path_equals_the_unbounded_graph()
    {
        var size = new WorldSize(128, 128, 2);
        PrototypeTerrainWorld.Create(size, out var unboundedWorld, out var walker);
        PrototypeTerrainWorld.Create(size, out var tinyWorld, out _, options: new PathfindingOptions { ChunkGraphMemoryBudgetBytes = 0 });
        using (unboundedWorld)
        using (tinyWorld)
        {
            var unbounded = unboundedWorld.Pathfinding();
            var tiny = tinyWorld.Pathfinding();
            var random = unboundedWorld.Random.Derive("budget");
            var a = new TilePos[256];
            var b = new TilePos[256];
            int found = 0;
            for (int i = 0; i < 300; i++)
            {
                ulong k = (ulong)i * 6;
                var from = new TilePos(random.Int32At(k, 0, size.X), random.Int32At(k + 1, 0, size.Y), random.Int32At(k + 2, 0, size.Z));
                var to = new TilePos(random.Int32At(k + 3, 0, size.X), random.Int32At(k + 4, 0, size.Y), random.Int32At(k + 5, 0, size.Z));
                var expected = unbounded.FindPath(from, to, walker, a);
                var actual = tiny.FindPath(from, to, walker, b);
                Assert.Equal(expected.Status, actual.Status);
                Assert.Equal(expected.Cost, actual.Cost);
                Assert.Equal(expected.WaypointCount, actual.WaypointCount);
                Assert.Equal(a.AsSpan(0, Math.Min(a.Length, expected.WaypointCount)), b.AsSpan(0, Math.Min(b.Length, actual.WaypointCount)));
                found += expected.Status == PathStatus.Found ? 1 : 0;
            }

            Assert.True(found > 100, $"only {found} paths found");
            Assert.True(tiny.ChunkGraphEvictions > 0, "the tiny budget evicted chunks");
            Assert.Equal(0, unbounded.ChunkGraphEvictions);
            Assert.True(tiny.ChunkGraphBytes < unbounded.ChunkGraphBytes, $"{tiny.ChunkGraphBytes} vs {unbounded.ChunkGraphBytes}");
            Assert.True(tiny.ChunkGraphBytes <= PathfindingOptions.MinimumChunkGraphChunks * 9 * 1024, $"floor only: {tiny.ChunkGraphBytes} bytes");
        }
    }

    [Fact]
    public void Past_the_budget_the_graph_stays_within_it()
    {
        var size = new WorldSize(128, 128, 2);
        PrototypeTerrainWorld.Create(size, out var world, out var walker, options: new PathfindingOptions { ChunkGraphMemoryBudgetBytes = 100_000 });
        using (world)
        {
            var service = world.Pathfinding();
            Span<TilePos> waypoints = stackalloc TilePos[64];
            for (int i = 0; i < 40; i++)
            {
                _ = service.FindPath(new TilePos(1 + i, 1, 0), new TilePos(126 - i, 126, 1), walker, waypoints);
                Assert.True(service.ChunkGraphBytes <= 100_000, $"{service.ChunkGraphBytes} bytes");
            }

            Assert.True(service.ChunkGraphEvictions > 0);
        }
    }

    [Fact]
    public void The_tick_record_carries_graph_memory_budget_and_evictions()
    {
        var scenario = new PathRequestScenario { ChunkGraphBudgetBytes = 0 };
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var schema = builder.Build();
        var names = schema.Counters.ToArray();
        int bytes = Array.IndexOf(names, PathfindingCounterIds.ChunkGraphBytesName);
        int budget = Array.IndexOf(names, PathfindingCounterIds.ChunkGraphBudgetBytesName);
        int evictions = Array.IndexOf(names, PathfindingCounterIds.ChunkGraphEvictionsName);
        Assert.True(bytes >= 0 && budget >= 0 && evictions >= 0, "the graph counters are registered");

        using var world = schema.CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 2, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        for (int t = 0; t < 6; t++)
        {
            scenario.Advance(world);
        }

        var service = world.Pathfinding();
        var record = world.Diagnostics.Latest.Counters;
        Assert.Equal(0, record[budget]);
        Assert.True(record[bytes] > 0, "searches built chunks");
        Assert.True(record[evictions] > 0, "the zero budget evicted chunks");
        Assert.True(record[evictions] <= service.ChunkGraphEvictions);
    }

    [Fact]
    public void A_tiny_budget_changes_no_hash_of_the_request_scenario()
    {
        var options = new DeterminismOptions { Ticks = 25 };
        var unbounded = DeterminismHarness.Run(new PathRequestScenario(), options);
        var tiny = DeterminismHarness.Run(new PathRequestScenario { ChunkGraphBudgetBytes = 0 }, options);

        Assert.True(tiny.Passed, tiny.Describe());
        Assert.Equal(unbounded.Hashes.ToArray(), tiny.Hashes.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Evicting_and_rebuilding_chunks_allocates_nothing_in_steady_state(int workers)
    {
        var result = AllocationHarness.Run(new PathRequestScenario { ChunkGraphBudgetBytes = 0 }, new AllocationOptions { Workers = workers, WarmupTicks = 30, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }
}
