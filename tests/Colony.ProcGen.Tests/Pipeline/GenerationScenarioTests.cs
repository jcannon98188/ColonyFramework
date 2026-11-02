using Colony.Fields;
using Colony.Testing;

namespace Colony.ProcGen.Tests.Pipeline;

/// <summary>TASK-42 AC #3: the generation oracle is thread-count independent (and so fit for the AOT comparison) and really streams.</summary>
public sealed class GenerationScenarioTests
{
    [Fact]
    public void The_generation_scenario_hashes_identically_on_1_2_4_and_N_workers()
    {
        var result = DeterminismHarness.Run(new GenerationScenario(), new DeterminismOptions { Ticks = 40 });
        Assert.True(result.Passed, result.Describe());
    }

    [Fact]
    public void The_tour_generates_every_chunk_and_the_seed_reaches_the_hash()
    {
        var builder = new WorldBuilder();
        var scenario = new GenerationScenario();
        scenario.Compose(builder);
        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 1 });
        scenario.Populate(world);
        for (int t = 0; t < 120; t++)
        {
            scenario.Advance(world);
        }

        var residency = world.Residency();
        Assert.All(Enumerable.Range(0, world.Fields().ChunkCount), o => Assert.True(residency.HasBeenGenerated(o), $"chunk {o} never generated"));

        var a = DeterminismHarness.Run(new GenerationScenario(), new DeterminismOptions { Ticks = 6, WorkerCounts = [1] });
        var b = DeterminismHarness.Run(new GenerationScenario { Seed = GenerationScenario.DefaultSeed + 1 }, new DeterminismOptions { Ticks = 6, WorkerCounts = [1] });
        Assert.NotNull(a.FirstDivergenceFrom(b));
    }
}
