using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>
/// AC #5: each debug validation switch (stale ref checks, freed-memory poisoning, the leak check at dispose) is
/// exercised by a scenario that misbehaves on purpose: the harness reports the failure with validation on and
/// the same scenario passes unnoticed with validation off.
/// </summary>
public sealed class DebugValidationScenarioTests
{
    private static DeterminismOptions With(bool debug) => new() { Ticks = 4, WorkerCounts = [1, 2], DebugValidation = debug };

    [Fact]
    public void A_chunk_view_kept_across_a_structural_change_faults_the_system_under_debug_validation()
    {
        var result = DeterminismHarness.Run(new StaleViewScenario(), With(debug: true));

        Assert.False(result.Passed);
        Assert.All(result.Runs, run =>
        {
            Assert.Equal("debug-stale-view", run.Fault?.SystemName);
            Assert.IsType<InvalidOperationException>(run.Fault?.Exception);
        });
        Assert.Contains("debug-stale-view", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_stale_view_goes_unnoticed_without_debug_validation()
    {
        var result = DeterminismHarness.Run(new StaleViewScenario(), With(debug: false));

        Assert.True(result.Passed, result.Describe());
    }

    [Theory]
    [InlineData(UseAfterFreeScenario.Memory.Chunk, SlabAllocator.PoisonByte)]
    [InlineData(UseAfterFreeScenario.Memory.Scratch, ScratchArena.PoisonByte)]
    public void Reading_freed_memory_sees_the_poison_byte_and_faults_under_debug_validation(UseAfterFreeScenario.Memory kind, byte poison)
    {
        var scenario = new UseAfterFreeScenario { Kind = kind };

        var result = DeterminismHarness.Run(scenario, With(debug: true));

        Assert.False(result.Passed);
        Assert.Equal(poison, scenario.LastRead);
        Assert.All(result.Runs, run => Assert.Contains("use-after-free", run.Fault?.Exception.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UseAfterFreeScenario.Memory.Chunk)]
    [InlineData(UseAfterFreeScenario.Memory.Scratch)]
    public void Reading_freed_memory_returns_the_stale_marker_without_debug_validation(UseAfterFreeScenario.Memory kind)
    {
        var scenario = new UseAfterFreeScenario { Kind = kind };

        var result = DeterminismHarness.Run(scenario, With(debug: false));

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(UseAfterFreeScenario.Marker, scenario.LastRead);
    }

    [Fact]
    public void A_leaked_chunk_is_reported_at_dispose_under_debug_validation_and_fails_the_run()
    {
        var result = DeterminismHarness.Run(new LeakingScenario(), With(debug: true));

        Assert.False(result.Passed);
        Assert.All(result.Runs, run =>
        {
            Assert.Null(run.Fault);
            Assert.Equal(1, run.Leak.LeakedChunks);
            Assert.Equal(SlabAllocator.ChunkSize, run.Leak.LeakedBytes);
        });
        Assert.Contains("leaked 1 chunks", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_leak_goes_unreported_without_debug_validation()
    {
        var result = DeterminismHarness.Run(new LeakingScenario(), With(debug: false));

        Assert.True(result.Passed, result.Describe());
        Assert.All(result.Runs, run => Assert.False(run.Leak.HasLeaks));
    }
}
