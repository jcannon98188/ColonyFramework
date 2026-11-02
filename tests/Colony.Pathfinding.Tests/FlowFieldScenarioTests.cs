using Colony.Pathfinding.Tests.Scenarios;
using Colony.Persistence;
using Colony.Testing;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #3: flow Field results are identical across thread counts; Fields are not hashed, and saved only so load-then-continue is exact (FlowDeterminismTests).</summary>
public sealed class FlowFieldScenarioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_flow_scenarios_hash_identically_on_1_2_4_and_N_workers_and_match_the_golden_hashes(bool tightBudget)
    {
        var result = DeterminismHarness.Run(new FlowFieldScenario { TightBudget = tightBudget }, new DeterminismOptions { Ticks = 40 });

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(40, result.Hashes.ToArray().Distinct().Count());
        var golden = PathfindingGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void Agents_walk_the_fields_arrive_get_lost_behind_doors_and_the_fields_are_repaired_every_tick()
    {
        var scenario = new FlowFieldScenario();
        using var world = Run(scenario, workers: 4, ticks: 40);

        var (steps, reached, lost) = scenario.Totals(world);
        Assert.True(steps > 10_000, $"agents walk ({steps})");
        Assert.True(reached > 0, "agents arrive");
        Assert.True(lost > 0, "doors close on agents");
        var service = world.Pathfinding();
        Assert.Equal(scenario.GoalSets, service.FlowFieldCount);
        var counters = service.FlowCounters;
        Assert.Equal(scenario.GoalSets, counters.Built);
        Assert.Equal(0, counters.Evicted);
        Assert.True(counters.IncrementalRepairs + counters.Rebuilds > scenario.GoalSets, "door toggles dirtied Fields that were repaired");
        Assert.Equal(0, counters.RepairsDeferred);
    }

    [Fact]
    public void With_a_tight_budget_a_field_is_evicted_and_rebuilt_every_tick()
    {
        var scenario = new FlowFieldScenario { TightBudget = true };
        using var world = Run(scenario, workers: 4, ticks: 40);

        var service = world.Pathfinding();
        Assert.Equal(scenario.GoalSets - 1, service.FlowFieldCount);
        var counters = service.FlowCounters;
        Assert.True(counters.Evicted >= 40, $"evictions: {counters.Evicted}");
        Assert.True(service.FlowFieldBytes <= counters.BudgetBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void The_flow_scenario_allocates_nothing_in_steady_state(int workers)
    {
        var result = AllocationHarness.Run(new FlowFieldScenario(), new AllocationOptions { Workers = workers, WarmupTicks = 30, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void Flow_fields_are_not_hashed_but_saved_so_a_load_continues_with_them()
    {
        var scenario = new FlowFieldScenario();
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var schema = builder.Build();
        var options = new WorldOptions { Seed = scenario.Seed, WorkerThreads = 2, InitialEntityCapacity = scenario.InitialEntityCapacity };
        using var straight = schema.CreateWorld(options);
        scenario.Populate(straight);
        ulong before = straight.Hash();
        _ = straight.Pathfinding().FlowField([new(5, 5, 0), new(50, 40, 1)], scenario.Walker, radiusChunks: 2);
        Assert.Equal(before, straight.Hash());

        for (int t = 0; t < 10; t++)
        {
            scenario.Advance(straight);
        }

        Assert.True(straight.Pathfinding().FlowFieldCount > 0);
        byte[] image = straight.SaveToBytes();
        var sections = WorldPersistence.SectionNames(image).ToArray();
        Assert.Equal(["ext/pathfinding/flows", "ext/pathfinding/built"], sections.Where(n => n.Contains("pathfinding", StringComparison.Ordinal)));

        using var resumed = WorldPersistence.Load(image, schema, new LoadOptions { WorldOptions = options });
        scenario.OnLoaded(resumed);
        Assert.Equal(straight.Pathfinding().FlowFieldCount, resumed.Pathfinding().FlowFieldCount);
        Assert.Equal(straight.Hash(), resumed.Hash());
        for (int t = 0; t < 15; t++)
        {
            scenario.Advance(straight);
            scenario.Advance(resumed);
            Assert.Equal(straight.Hash(), resumed.Hash());
        }
    }

    private static World Run(FlowFieldScenario scenario, int workers, int ticks)
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
