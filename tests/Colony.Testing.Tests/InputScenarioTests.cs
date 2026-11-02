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

    [Fact]
    public void Restricted_sources_hash_identically_on_1_2_4_and_N_threads_differ_from_the_open_run_and_match_the_golden_hashes()
    {
        var open = DeterminismHarness.Run(new InputScenario(), Options);
        var restricted = DeterminismHarness.Run(new InputScenario { Restricted = true }, Options);
        var concurrent = DeterminismHarness.Run(new InputScenario { Restricted = true, Concurrent = true }, Options);

        Assert.True(restricted.Passed, restricted.Describe());
        Assert.True(concurrent.Passed, concurrent.Describe());
        Assert.Null(restricted.FirstDivergenceFrom(concurrent));
        Assert.NotEqual(open.Hashes.ToArray(), restricted.Hashes.ToArray());
        var golden = TestGolden.Store.Compare(restricted);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void A_restricted_run_denies_the_spectator_and_non_host_dismissals_at_the_tick_boundary()
    {
        var scenario = new InputScenario { Restricted = true };
        using var world = DeterminismHarness.CreateWorld(scenario, DeterminismHarness.BuildSchema(scenario, null), workers: 2, listener: null);
        var dismiss = world.Schema.InputCommand<InputScenario.Dismiss>();
        int denied = 0;
        for (int i = 0; i < 30; i++)
        {
            scenario.Advance(world);
            var list = world.Inputs.Current;
            for (int e = 0; e < list.Count; e++)
            {
                var record = list[e];
                bool expectDenied = record.Source.Value == (uint)(scenario.Sources - 1) || (record.Command == dismiss && record.Source != InputScenario.Host);
                Assert.Equal(expectDenied, list.IsDenied(e));
                denied += expectDenied ? 1 : 0;
            }

        }

        Assert.True(denied > 30, "the spectator submits every Tick");
        // Every denial is a CommandRejected the Begin-Phase System counts, on top of the handlers' own rejections.
        var stock = world.Resource<InputScenario.Stock>(world.Schema.Resource<InputScenario.Stock>());
        Assert.True(stock.Rejections >= denied, $"{stock.Rejections} rejections counted for {denied} denials");
    }
}