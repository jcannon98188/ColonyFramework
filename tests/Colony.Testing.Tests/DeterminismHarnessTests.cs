using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>AC #1: a scenario is seed plus parameters; the harness runs it across worker counts and reports the first diverging Tick.</summary>
public sealed class DeterminismHarnessTests
{
    [Fact]
    public void A_deterministic_scenario_hashes_identically_across_the_configured_worker_counts()
    {
        var options = new DeterminismOptions { Ticks = 12, WorkerCounts = [1, 2, 4] };

        var result = DeterminismHarness.Run(new CounterScenario(), options);

        Assert.True(result.Passed, result.Describe());
        Assert.Null(result.FirstDivergingTick);
        Assert.Equal(3, result.Runs.Count);
        Assert.Equal([1, 2, 4], result.Runs.Select(r => r.Workers).ToArray());
        Assert.Equal(12, result.Runs[0].Hashes.Length);
        Assert.Equal(12, result.Runs[0].Hashes.ToArray().Distinct().Count()); // the World really evolves
    }

    [Fact]
    public void A_thread_keyed_scenario_fails_and_names_the_first_diverging_tick()
    {
        var options = new DeterminismOptions { Ticks = 12, WorkerCounts = [1, 4] };

        var result = DeterminismHarness.Run(new CounterScenario { DivergeFromTick = 5 }, options);

        Assert.False(result.Passed);
        Assert.Equal(5, result.FirstDivergingTick);
        Assert.Contains("Tick 5", result.Describe(), StringComparison.Ordinal);
        var error = Assert.Throws<HarnessException>(result.ThrowIfFailed);
        Assert.Contains("Tick 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Checkpoints_can_be_sparser_than_every_tick()
    {
        var options = new DeterminismOptions { Ticks = 10, CheckpointEvery = 4, WorkerCounts = [1, 2] };

        var result = DeterminismHarness.Run(new CounterScenario(), options);

        Assert.Equal([4, 8, 10], result.Checkpoints.ToArray()); // the final Tick is always a checkpoint
        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void A_divergence_between_sparse_checkpoints_is_reported_at_the_first_checkpoint_that_differs()
    {
        var options = new DeterminismOptions { Ticks = 10, CheckpointEvery = 4, WorkerCounts = [1, 2] };

        var result = DeterminismHarness.Run(new CounterScenario { DivergeFromTick = 5 }, options);

        Assert.Equal(8, result.FirstDivergingTick);
    }

    [Fact]
    public void The_seed_is_part_of_the_scenario_and_changes_every_hash()
    {
        var options = new DeterminismOptions { Ticks = 3, WorkerCounts = [1] };

        var a = DeterminismHarness.Run(new CounterScenario { Seed = 1 }, options);
        var b = DeterminismHarness.Run(new CounterScenario { Seed = 2 }, options);

        Assert.Equal(1UL, a.Seed);
        Assert.NotEqual(a.Runs[0].Hashes.ToArray(), b.Runs[0].Hashes.ToArray());
    }

    [Fact]
    public void A_system_that_throws_faults_the_run_and_fails_the_result()
    {
        var options = new DeterminismOptions { Ticks = 6, WorkerCounts = [1, 2] };

        var result = DeterminismHarness.Run(new CounterScenario { ThrowAtTick = 3 }, options);

        Assert.False(result.Passed);
        Assert.All(result.Runs, run => Assert.Equal("Count", run.Fault?.SystemName));
        Assert.All(result.Runs, run => Assert.Equal(2, run.Fault?.Tick)); // Core numbers Ticks from 0: the third Advance is Tick 2
        Assert.All(result.Runs, run => Assert.Equal(2, run.CompletedCheckpoints));
        Assert.Contains("System 'Count' threw during Tick 2", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Default_worker_counts_are_1_2_4_and_N()
    {
        var counts = DeterminismOptions.DefaultWorkerCounts;

        Assert.Equal(1, counts[0]);
        Assert.Equal(2, counts[1]);
        Assert.Equal(4, counts[2]);
        Assert.Equal(4, counts.Count);
        Assert.True(counts[3] >= 4);
    }
}
