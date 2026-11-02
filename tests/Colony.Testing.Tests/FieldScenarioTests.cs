using Colony.Fields;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>TASK-12's stencil oracle run through the harness: both stencil modes hash identically across thread counts and the golden file, and allocate nothing.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class FieldScenarioTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 1 };

    [Fact]
    public void Both_stencil_modes_hash_identically_on_1_2_4_and_N_threads_and_match_the_golden_hashes()
    {
        var result = DeterminismHarness.Run(new FieldScenario(), Options);

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(30, result.Hashes.ToArray().Distinct().Count()); // the Fields really evolve
        var golden = TestGolden.Store.Compare(result);
        Assert.True(golden.Passed, golden.Message);
    }

    [Fact]
    public void The_scenario_changes_field_state_every_tick()
    {
        var scenario = new FieldScenario();
        using var world = DeterminismHarness.CreateWorld(scenario, DeterminismHarness.BuildSchema(scenario, null), workers: 2, listener: null);
        var fields = world.Fields();
        world.Tick();
        Assert.NotEmpty(fields.LastTickChanges.ToArray());
        Assert.Contains(fields.LastTickChanges.ToArray(), c => c.FieldId == fields.Field<int>("heat").Id);
        Assert.Contains(fields.LastTickChanges.ToArray(), c => c.FieldId == fields.Field<byte>("life").Id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_steady_state_tick_with_stencils_allocates_nothing(int workers)
    {
        var result = AllocationHarness.Run(new ChangeFeedReadingFieldScenario(), new AllocationOptions { Workers = workers, WarmupTicks = 20, MeasuredTicks = 10 });

        Assert.True(result.Passed, result.Describe());
    }

    /// <summary>The Fields scenario plus a driver that reads the change feed after every Tick, as a Game would.</summary>
    private sealed class ChangeFeedReadingFieldScenario : Scenario
    {
        private readonly FieldScenario _inner = new();

        public override string Name => "fields-with-change-feed";

        public override ulong Seed { get => _inner.Seed; init => _ = value; }

        public override void Compose(WorldBuilder builder) => _inner.Compose(builder);

        public override void Populate(World world) => _inner.Populate(world);

        public override void Advance(World world)
        {
            world.Tick();
            var fields = world.Fields();
            foreach (var change in fields.ChangedSince(fields.CurrentVersion - 1))
            {
                _ = change;
            }
        }
    }
}
