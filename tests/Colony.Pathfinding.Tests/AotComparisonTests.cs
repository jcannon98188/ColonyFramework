using Colony.Pathfinding.Tests.Scenarios;
using Colony.Testing;

namespace Colony.Pathfinding.Tests;

/// <summary>TASK-42 AC #3: the path request and flow field oracles (default and budget-starved) hash the same under the published Native AOT host as under the JIT.</summary>
public sealed class AotComparisonTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 10, WorkerCounts = [1, 4] };

    [Fact]
    public void The_native_aot_host_produces_the_jit_hashes_for_path_requests_and_flow_fields()
    {
        string? exe = PublishedHost.Find("Colony.Testing.AotHost");
        if (exe is null)
        {
            Assert.Skip("Publish the AOT host first: dotnet publish tests/Colony.Testing.AotHost -c Release -r <rid> (see CONVENTIONS, TASK-10).");
        }

        foreach (var scenario in new Testing.Scenario[] { new PathRequestScenario(), new PathRequestScenario { Starved = true }, new FlowFieldScenario(), new FlowFieldScenario { TightBudget = true } })
        {
            long? diverged = PublishedHost.CompareWithJit(exe, scenario, Options, out var jit, out var aot);
            Assert.True(diverged is null, $"{scenario.Name}: AOT diverged from JIT at Tick {diverged}\nJIT:\n{jit.Describe()}\nAOT:\n{aot.Describe()}");
        }
    }
}
