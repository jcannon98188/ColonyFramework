using System.Diagnostics;
using Colony.Diagnostics;
using Colony.Scenario;

namespace Colony.Core.Tests.Diagnostics;

/// <summary>
/// TASK-17 AC #2: Standard-level diagnostics cost under about 1% of Tick time on the colonist scenario.
/// Off and Standard Worlds Tick in interleaved batches so machine noise hits both alike; the ratio is
/// printed (run with <c>--show-stdout All</c>) and gated loosely, since a shared machine is noisy.
/// Run in Release for the recorded number: <c>dotnet run -c Release --project tests/Colony.Core.Tests -- --filter-class "*StandardLevelOverhead*" --show-stdout All</c>.
/// </summary>
public sealed class StandardLevelOverheadTests
{
    private const int Workers = 4;
    private const int WarmUpTicks = 30;
    private const int Batches = 8;
    private const int TicksPerBatch = 10;

    [Fact]
    public void Standard_level_overhead_report()
    {
        using var off = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), Workers, options: new DiagnosticsOptions { Level = DiagnosticsLevel.Off, AllocationCheck = false });
        using var standard = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), Workers, options: new DiagnosticsOptions { Level = DiagnosticsLevel.Standard });

        Run(off, WarmUpTicks);
        Run(standard, WarmUpTicks);
        Thread.Sleep(250); // let tiered compilation finish before timing

        long offTicks = 0;
        long standardTicks = 0;
        for (int b = 0; b < Batches; b++)
        {
            offTicks += Run(off, TicksPerBatch);
            standardTicks += Run(standard, TicksPerBatch);
        }

        double offMs = offTicks * 1000.0 / Stopwatch.Frequency / (Batches * TicksPerBatch);
        double standardMs = standardTicks * 1000.0 / Stopwatch.Frequency / (Batches * TicksPerBatch);
        double overhead = (standardMs - offMs) / offMs;
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Off: {offMs:F3} ms/Tick, Standard: {standardMs:F3} ms/Tick, overhead {overhead * 100:F2}% ({standard.Diagnostics.Latest.Tick.Jobs} jobs/Tick, {Workers} workers)");

        Assert.Equal(off.Hash(), standard.Hash());
        Assert.True(overhead < 0.05, $"Standard-level overhead {overhead * 100:F2}% is not under about 1% (gate 5% for noisy machines)");
    }

    private static long Run(World world, int ticks)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < ticks; i++)
        {
            world.Tick();
        }

        return Stopwatch.GetTimestamp() - start;
    }
}
