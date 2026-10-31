using Colony.Jobs.Tests.Scenarios;
using Colony.Testing;

namespace Colony.Jobs.Tests;

/// <summary>AC #1 (thread-count independence), AC #3 (budgeted decisions stay deterministic), AC #4 (thrash measurably lower with the defaults) and zero steady-state allocation.</summary>
public sealed class ClaimScenarioTests
{
    [Fact]
    public void The_claim_scenario_hashes_identically_on_1_2_4_and_N_workers()
    {
        var result = DeterminismHarness.Run(new ClaimScenario(), new DeterminismOptions { Ticks = 40 });
        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void A_budgeted_decision_system_stays_deterministic_across_worker_counts()
    {
        var result = DeterminismHarness.Run(new ClaimScenario { Budget = 1500, Cadence = 3 }, new DeterminismOptions { Ticks = 30 });
        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void The_anti_thrash_defaults_cut_denials_measurably_against_the_disabled_policy()
    {
        var defaults = Run(new ClaimScenario(), ticks: 120);
        var none = Run(new ClaimScenario { Policy = ClaimPolicy.None }, ticks: 120);

        Assert.True(none.Denied > 0, "the scenario must thrash without a policy");
        Assert.True(defaults.Denied * 2 < none.Denied, $"defaults denied {defaults.Denied}, none denied {none.Denied}");
        Assert.True(defaults.Granted > none.Granted / 2, $"defaults granted {defaults.Granted}, none granted {none.Granted}");
        Assert.True(defaults.Lost > 0 && none.Lost > 0, "site churn must lose claims in both runs");
    }

    [Fact]
    public void The_claim_scenario_allocates_nothing_in_steady_state_on_main_or_worker_threads()
    {
        var result = AllocationHarness.Run(new ClaimScenario(), new AllocationOptions { Workers = 4, WarmupTicks = 40, MeasuredTicks = 10 });
        Assert.True(result.Passed, result.Describe());
    }

    private static ClaimScenario Run(ClaimScenario scenario, int ticks)
    {
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 1, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        for (int t = 0; t < ticks; t++)
        {
            scenario.Advance(world);
        }

        return scenario;
    }
}
