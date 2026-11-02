using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>AC #3: the allocation harness fails a scenario when any Framework thread allocates in steady state and names the System running at the time.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class AllocationHarnessTests
{
    private static readonly AllocationOptions Options = new() { Workers = 4, WarmupTicks = 5, MeasuredTicks = 5 };

    [Fact]
    public void A_clean_scenario_allocates_nothing_on_the_tick_thread_or_the_workers()
    {
        var result = AllocationHarness.Run(new CounterScenario(), Options);

        Assert.True(result.Passed, result.Describe());
        Assert.Equal(0, result.MainThreadBytes);
        Assert.Equal(0, result.WorkerBytes);
        Assert.Null(result.FirstOffender);
        Assert.Equal(4, result.Workers);
        // The measurement loop itself runs on the Tick thread, so a zero total proves the harness allocates nothing either.
    }

    [Fact]
    public void An_allocating_system_fails_the_scenario_and_is_named_with_its_phase()
    {
        var result = AllocationHarness.Run(new CounterScenario { AllocateFromTick = 1 }, Options);

        Assert.False(result.Passed);
        Assert.True(result.MainThreadBytes + result.WorkerBytes > 0);
        var offender = Assert.IsType<AllocationSite>(result.FirstOffender);
        Assert.Equal(Phase.Simulate, offender.Phase);
        Assert.Equal(["Count"], offender.Systems);
        Assert.Contains("Count", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("Simulate", result.Describe(), StringComparison.Ordinal);
        var error = Assert.Throws<HarnessException>(result.ThrowIfFailed);
        Assert.Contains("Count", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_thread_allocations_are_attributed_too()
    {
        // With 4 workers the dozen chunk jobs spread over the pool, so some of the per-job allocations land on worker threads.
        var result = AllocationHarness.Run(new CounterScenario { AllocateFromTick = 1 }, Options);

        Assert.True(result.WorkerBytes > 0, result.Describe());
        Assert.True(result.FirstOffender!.WorkerBytes > 0);
    }

    [Fact]
    public void Allocations_during_warm_up_do_not_count()
    {
        // Allocates only during the first 5 Ticks (0-based 0..4), all inside the warm-up window.
        var result = AllocationHarness.Run(new CounterScenario { AllocateFromTick = 1, StopAllocatingAtTick = 6 }, Options);

        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void Systems_sharing_a_wave_are_named_together()
    {
        // Two non-conflicting Systems run in one wave; without per-System hooks from Core the harness attributes the wave.
        var scenario = Scenario.Create(
            "two-in-a-wave",
            seed: 1,
            compose: builder =>
            {
                var a = builder.AddComponent<Counter>("a").DebugValidation(false).Component<Counter>();
                var b = builder.AddComponent<Second>("b").Component<Second>();
                builder.AddSystem(new SystemDefinition { Name = "Quiet", Writes = ComponentMask.Of(a), ForEachChunk = static (in SystemContext _, in ChunkView _) => { } });
                builder.AddSystem(new SystemDefinition { Name = "Noisy", Writes = ComponentMask.Of(b), ForEachChunk = static (in SystemContext _, in ChunkView _) => Sink = new byte[16] });
            },
            populate: world =>
            {
                var buffer = world.CreateCommandBuffer();
                buffer.Create(ComponentMask.Of(world.Schema.Component<Counter>(), world.Schema.Component<Second>()));
                world.Playback(buffer);
                world.ResetScratch();
            });

        var result = AllocationHarness.Run(scenario, Options);

        Assert.False(result.Passed);
        Assert.Equal(["Quiet", "Noisy"], result.FirstOffender!.Systems);

        // The World's diagnostics allocation check samples at every job boundary, so it pins the exact System.
        var system = Assert.Single(result.Systems);
        Assert.Equal("Noisy", system.Name);
        Assert.Equal(Phase.Simulate, system.Phase);
        Assert.True(system.Bytes >= 16 * Options.MeasuredTicks, result.Describe());
        Assert.Contains("Noisy", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_diagnostics_allocation_check_agrees_with_the_probes()
    {
        var clean = AllocationHarness.Run(new CounterScenario(), Options);
        Assert.Equal(0, clean.DiagnosticsBytes);
        Assert.Empty(clean.Systems);

        var dirty = AllocationHarness.Run(new CounterScenario { AllocateFromTick = 1 }, Options);
        Assert.Equal("Count", Assert.Single(dirty.Systems).Name);
        Assert.True(dirty.DiagnosticsBytes > 0, dirty.Describe());
        Assert.Equal(dirty.Systems[0].Bytes, dirty.FirstOffender!.TotalBytes);
    }

    [Fact]
    public void A_scenario_that_allocates_outside_any_system_is_attributed_to_the_tick_boundary()
    {
        var scenario = Scenario.Create(
            "allocating-driver",
            seed: 1,
            compose: builder => builder.AddComponent<Counter>("a").DebugValidation(false),
            populate: static _ => { },
            step: world =>
            {
                world.Tick();
                Sink = new byte[8];
            });

        var result = AllocationHarness.Run(scenario, Options);

        Assert.False(result.Passed);
        Assert.Empty(result.FirstOffender!.Systems);
        Assert.Null(result.FirstOffender.Phase);
    }

    public struct Second
    {
        public int Value;
    }

    public static object? Sink { get; private set; }
}
