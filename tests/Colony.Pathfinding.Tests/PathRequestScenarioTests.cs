using Colony.Pathfinding.Tests.Scenarios;
using Colony.Persistence;
using Colony.Testing;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #5 (and the thread-count half of AC #1): the toggle and budget-exhaustion request scenarios in the determinism harness.</summary>
public sealed class PathRequestScenarioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_request_scenarios_hash_identically_on_1_2_4_and_N_workers_and_match_the_golden_hashes(bool starved)
    {
        var result = DeterminismHarness.Run(new PathRequestScenario { Starved = starved }, new DeterminismOptions { Ticks = 40 });

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(40, result.Hashes.ToArray().Distinct().Count());
        var golden = PathfindingGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void With_a_generous_budget_agents_reach_goals_find_pockets_unreachable_and_get_blocked_by_doors_but_never_miss_a_deadline()
    {
        var scenario = new PathRequestScenario();
        using var world = Run(scenario, workers: 4, ticks: 40);

        var (reached, unreachable, missed, blocked) = scenario.Totals(world);
        Assert.True(reached > 0, "agents arrive");
        Assert.True(unreachable > 0, "pocket goals come back Unreachable");
        Assert.True(blocked > 0, "door toggles break paths under walking agents");
        Assert.Equal(0, missed);
        var counters = world.Pathfinding().QueueCounters;
        Assert.True(counters.Submitted > scenario.Agents, "agents keep asking");
        Assert.Equal(counters.Submitted, counters.Delivered + world.Pathfinding().PendingRequestCount);
        Assert.Equal(0, counters.DeadlinesMissed);
    }

    [Fact]
    public void With_a_starved_budget_most_requests_miss_their_deadline_and_the_rest_are_still_answered_in_order()
    {
        var scenario = new PathRequestScenario { Starved = true };
        using var world = Run(scenario, workers: 4, ticks: 40);

        var (_, _, missed, _) = scenario.Totals(world);
        var counters = world.Pathfinding().QueueCounters;
        Assert.True(missed > 0, "the budget cannot serve every request");
        Assert.True(counters.Deferred > 0);
        Assert.True(counters.DeadlinesMissed * 2 > counters.Delivered, $"most results are deadline misses: {counters.DeadlinesMissed} of {counters.Delivered}");
        Assert.True(counters.Computed >= 40, "at least one request per Tick is still computed whatever the budget");
        Assert.Equal(missed, counters.DeadlinesMissed); // every miss reached its agent
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void The_request_scenario_allocates_nothing_in_steady_state(int workers)
    {
        var result = AllocationHarness.Run(new PathRequestScenario(), new AllocationOptions { Workers = workers, WarmupTicks = 30, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 4)]
    public void Saving_with_requests_in_flight_then_loading_and_continuing_hashes_like_the_run_that_never_saved(bool starved, int workers)
    {
        var scenario = new PathRequestScenario { Starved = starved };
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var schema = builder.Build();
        var options = new WorldOptions { Seed = scenario.Seed, WorkerThreads = workers, InitialEntityCapacity = scenario.InitialEntityCapacity };
        using var straight = schema.CreateWorld(options);
        scenario.Populate(straight);
        for (int t = 0; t < 10; t++)
        {
            scenario.Advance(straight);
        }

        Assert.True(straight.Pathfinding().PendingRequestCount > 0, "requests are in flight at the save");
        byte[] image = straight.SaveToBytes();
        Assert.Contains("ext/pathfinding/requests", WorldPersistence.SectionNames(image));
        using var resumed = WorldPersistence.Load(image, schema, new LoadOptions { WorldOptions = options });
        scenario.OnLoaded(resumed);
        Assert.Equal(straight.Pathfinding().PendingRequestCount, resumed.Pathfinding().PendingRequestCount);
        for (int t = 0; t < 15; t++)
        {
            scenario.Advance(straight);
            scenario.Advance(resumed);
            Assert.Equal(straight.Hash(), resumed.Hash());
        }
    }

    private static World Run(PathRequestScenario scenario, int workers, int ticks)
    {
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = workers, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        for (int t = 0; t < ticks; t++)
        {
            scenario.Advance(world);
        }

        return world;
    }
}
