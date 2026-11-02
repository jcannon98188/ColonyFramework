using Colony.Jobs.Tests.Scenarios;
using Colony.Testing;

namespace Colony.Jobs.Tests;

/// <summary>TASK-42 AC #3: the claims and work index oracles hash the same under the published Native AOT host as under the JIT.</summary>
public sealed class AotComparisonTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 10, WorkerCounts = [1, 4] };

    [Fact]
    public void The_native_aot_host_produces_the_jit_hashes_for_claims_and_the_work_index()
    {
        string? exe = PublishedHost.Find("Colony.Testing.AotHost");
        if (exe is null)
        {
            Assert.Skip("Publish the AOT host first: dotnet publish tests/Colony.Testing.AotHost -c Release -r <rid> (see CONVENTIONS, TASK-10).");
        }

        foreach (var scenario in new Testing.Scenario[] { new ClaimScenario(), new WorkIndexScenario() })
        {
            long? diverged = PublishedHost.CompareWithJit(exe, scenario, Options, out var jit, out var aot);
            Assert.True(diverged is null, $"{scenario.Name}: AOT diverged from JIT at Tick {diverged}\nJIT:\n{jit.Describe()}\nAOT:\n{aot.Describe()}");
        }
    }
}
