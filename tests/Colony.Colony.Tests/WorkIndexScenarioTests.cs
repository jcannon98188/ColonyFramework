using Colony.Jobs.Tests.Scenarios;
using Colony.Testing;

namespace Colony.Jobs.Tests;

/// <summary>TASK-42 AC #3: the work index oracle is thread-count independent (and so fit for the AOT comparison) and allocation-free in steady state.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class WorkIndexScenarioTests
{
    [Fact]
    public void The_work_index_scenario_hashes_identically_on_1_2_4_and_N_workers()
    {
        var result = DeterminismHarness.Run(new WorkIndexScenario(), new DeterminismOptions { Ticks = 40 });
        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void The_work_index_scenario_finds_work_and_changes_the_world_hash_with_its_hits()
    {
        var a = DeterminismHarness.Run(new WorkIndexScenario(), new DeterminismOptions { Ticks = 10, WorkerCounts = [1] });
        var b = DeterminismHarness.Run(new WorkIndexScenario { Seed = WorkIndexScenario.DefaultSeed + 1 }, new DeterminismOptions { Ticks = 10, WorkerCounts = [1] });
        Assert.NotNull(a.FirstDivergenceFrom(b)); // the hash is not seed-blind

        var builder = new WorldBuilder();
        var scenario = new WorkIndexScenario();
        scenario.Compose(builder);
        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 1, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        for (int t = 0; t < 10; t++)
        {
            scenario.Advance(world);
        }

        long found = 0;
        var worker = world.Schema.Component<SeekingWorker>();
        foreach (var chunk in world.Query(worker))
        {
            foreach (var w in chunk.ReadColumn<SeekingWorker>(worker))
            {
                found += w.Found;
            }
        }

        Assert.True(found > 1_000, $"workers found only {found} items in 10 Ticks");
    }

    [Fact]
    public void The_work_index_scenario_allocates_nothing_in_steady_state_on_main_or_worker_threads()
    {
        var result = AllocationHarness.Run(new WorkIndexScenario(), new AllocationOptions { Workers = 4, WarmupTicks = 40, MeasuredTicks = 10 });
        Assert.True(result.Passed, result.Describe());
    }
}
