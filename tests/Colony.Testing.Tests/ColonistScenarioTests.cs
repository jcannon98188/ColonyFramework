using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>
/// TASK-7's acceptance oracle run through the harness (AC #1-#4): the colonist scenario (100k colonists,
/// 5k food, 1% churn) hashes identically on 1, 2, 4 and N threads and against its committed golden
/// hashes, allocates nothing in steady state on any thread, and the thread-keyed negative control fails.
/// </summary>
public sealed class ColonistScenarioTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 60, CheckpointEvery = 1 };

    [Fact]
    public void The_colonist_scenario_hashes_identically_on_1_2_4_and_N_threads_and_matches_its_golden_hashes()
    {
        var result = DeterminismHarness.Run(new ColonistScenario(), Options);

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(DeterminismOptions.DefaultWorkerCounts, result.Runs.Select(r => r.Workers));
        Assert.Equal(60, result.Hashes.Length);
        var golden = TestGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void The_scenario_really_churns_and_eats()
    {
        var scenario = new ColonistScenario();
        using var world = DeterminismHarness.CreateWorld(scenario, DeterminismHarness.BuildSchema(scenario, null), workers: 4, listener: null);
        for (int i = 0; i < 20; i++)
        {
            scenario.Advance(world);
        }

        Assert.Equal(ColonistScenario.DefaultColonists + ColonistScenario.DefaultFood, world.EntityCount);
        Assert.True(world.Counters.Destroys > 20 * ColonistScenario.DefaultColonists / 100, "the churn and the eating must have happened");
    }

    [Fact]
    public void The_negative_control_with_a_thread_keyed_rng_fails_the_hash_comparison()
    {
        var result = DeterminismHarness.Run(new ColonistScenario { Sabotage = true }, new DeterminismOptions { Ticks = 10, WorkerCounts = [1, 4] });

        Assert.False(result.Passed);
        Assert.NotNull(result.FirstDivergingTick);
        Assert.Equal("colonist-negative-control", result.ScenarioName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_steady_state_tick_allocates_nothing_on_any_tick_thread(int workers)
    {
        var result = AllocationHarness.Run(new ColonistScenario(), new AllocationOptions { Workers = workers, WarmupTicks = 30, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }
}
