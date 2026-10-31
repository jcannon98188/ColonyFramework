using Colony.Fields;
using Colony.Pathfinding.Tests.Scenarios;
using Colony.Testing;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #3: Reachable answers in constant time, and the unreachable-target scenario runs through the determinism harness.</summary>
public sealed class ReachableTests
{
    [Fact]
    public void The_unreachable_target_scenario_hashes_identically_on_1_2_4_and_N_workers_and_matches_the_golden_hashes()
    {
        var result = DeterminismHarness.Run(new UnreachableTargetScenario(), new DeterminismOptions { Ticks = 40 });

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(40, result.Hashes.ToArray().Distinct().Count());
        var golden = PathfindingGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void Pocket_goals_are_never_reached_and_door_traffic_makes_ordinary_goals_both_reachable_and_unreachable()
    {
        var scenario = new UnreachableTargetScenario();
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 4, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        for (int t = 0; t < 30; t++)
        {
            scenario.Advance(world);
        }

        var (reached, unreachable) = scenario.Totals(world);
        Assert.True(reached > 0, "some goals must be reachable");
        Assert.True(unreachable * 10 > (reached + unreachable), $"pockets and closed doors must make over a tenth unreachable: {reached} reached, {unreachable} not");
        foreach (var chunk in world.Query(ComponentMask.Of(scenario.AgentType)))
        {
            foreach (ref readonly var agent in chunk.Column<WanderAgent>(scenario.AgentType))
            {
                Assert.False(scenario.IsPocket(agent.X, agent.Y, agent.Z), "no agent ever enters a sealed pocket");
            }
        }

        var service = world.Pathfinding();
        Assert.True(service.Regions(scenario.Walker).Counters.IncrementalRelabels > 0, "door toggles relabel regions every Tick");
        long[] incremental = RegionOracle.Actual(world, scenario.Walker);
        service.Rebuild(scenario.Walker);
        Assert.Equal(incremental, RegionOracle.Actual(world, scenario.Walker));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void The_unreachable_target_scenario_allocates_nothing_in_steady_state(int workers)
    {
        var result = AllocationHarness.Run(new UnreachableTargetScenario(), new AllocationOptions { Workers = workers, WarmupTicks = 30, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void Reachable_does_no_search_and_allocates_nothing_whatever_the_distance()
    {
        using var world = TestWorlds.Create(new WorldSize(128, 128, 1), out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 64);
        service.EnsureBuilt(walker);
        var before = service.Regions(walker).Counters;
        _ = service.Reachable(new TilePos(0, 0, 0), new TilePos(1, 0, 0), walker); // warm the call path

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool near = service.Reachable(new TilePos(0, 0, 0), new TilePos(1, 0, 0), walker);
        bool far = service.Reachable(new TilePos(0, 0, 0), new TilePos(63, 127, 0), walker);
        bool across = service.Reachable(new TilePos(0, 0, 0), new TilePos(127, 127, 0), walker);
        bool wall = service.Reachable(new TilePos(0, 0, 0), new TilePos(64, 3, 0), walker);
        bool outside = service.Reachable(new TilePos(0, 0, 0), new TilePos(-1, 0, 0), walker);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.Equal((true, true, false, false, false), (near, far, across, wall, outside));
        Assert.Equal(0, allocated);
        Assert.Equal(before, service.Regions(walker).Counters); // no node visited, nothing relabelled: two lookups per endpoint
    }
}
