using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>TASK-15 AC #1 (spec section 7.3): the canonical input list, and so the World hash, does not depend on how commands were submitted or on how many threads ran the Tick.</summary>
public sealed class InputScenarioTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 1 };

    [Fact]
    public void Concurrent_and_sequential_submission_hash_identically_on_1_2_4_and_N_threads_and_match_the_golden_hashes()
    {
        var sequential = DeterminismHarness.Run(new InputScenario(), Options);
        var concurrent = DeterminismHarness.Run(new InputScenario { Concurrent = true }, Options);

        Assert.True(sequential.Passed, sequential.Describe());
        Assert.True(concurrent.Passed, concurrent.Describe());
        Assert.Null(sequential.FirstDivergenceFrom(concurrent));
        Assert.Equal(30, sequential.Hashes.ToArray().Distinct().Count()); // the colony really evolves
        var golden = TestGolden.Store.Compare(sequential);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void The_scenario_applies_commands_rejects_some_and_reaches_a_steady_colony()
    {
        var scenario = new InputScenario { Concurrent = true };
        using var world = DeterminismHarness.CreateWorld(scenario, DeterminismHarness.BuildSchema(scenario, null), workers: 2, listener: null);
        for (int i = 0; i < 30; i++)
        {
            scenario.Advance(world);
        }

        var stock = world.Resource<InputScenario.Stock>(world.Schema.Resource<InputScenario.Stock>());
        Assert.True(stock.Workers > 0);
        Assert.True(stock.Wood > 0);
        Assert.True(stock.Rejections > 0, "the script submits invalid commands on purpose");
        Assert.True(stock.Dismissed > 0);
        Assert.Equal(0, world.Inputs.PendingCount);
        Assert.Equal(30, world.CurrentTick);
    }
}
