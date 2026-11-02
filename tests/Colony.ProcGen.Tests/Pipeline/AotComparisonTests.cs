using Colony.Testing;

namespace Colony.ProcGen.Tests.Pipeline;

/// <summary>TASK-42 AC #3: the streaming generation oracle hashes the same under the published Native AOT host as under the JIT.</summary>
public sealed class AotComparisonTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 10, WorkerCounts = [1, 4] };

    [Fact]
    public void The_native_aot_host_produces_the_jit_hashes_for_streaming_generation()
    {
        string? exe = PublishedHost.Find("Colony.Testing.AotHost");
        if (exe is null)
        {
            Assert.Skip("Publish the AOT host first: dotnet publish tests/Colony.Testing.AotHost -c Release -r <rid> (see CONVENTIONS, TASK-10).");
        }

        foreach (var scenario in new Testing.Scenario[] { new GenerationScenario() })
        {
            long? diverged = PublishedHost.CompareWithJit(exe, scenario, Options, out var jit, out var aot);
            Assert.True(diverged is null, $"{scenario.Name}: AOT diverged from JIT at Tick {diverged}\nJIT:\n{jit.Describe()}\nAOT:\n{aot.Describe()}");
        }
    }
}
