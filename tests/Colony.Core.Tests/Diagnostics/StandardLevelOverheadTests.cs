using System.Diagnostics;
using Colony.Diagnostics;
using Colony.Scenario;

namespace Colony.Core.Tests.Diagnostics;

/// <summary>
/// TASK-17 AC #2: Standard-level diagnostics cost under about 1% of Tick time on the colonist scenario.
/// Off and Standard Worlds Tick in interleaved batches so machine noise hits both alike; the fastest
/// batch of each side is the estimate (least disturbed by other test processes). The ratio is printed
/// (run with <c>--show-stdout All --show-test-results all</c>) and the test only asserts that both Worlds
/// agree and that Standard is not grossly slower; the real 1% gate is the TASK-30 benchmark, because a
/// percent-level timing gate is not stable while the whole solution's tests run in parallel.
/// Run in Release for the recorded number: <c>dotnet test tests/Colony.Core.Tests -c Release -- --filter-method "*Standard_level_overhead*" --show-stdout All --show-test-results all</c>.
/// </summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
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

        long offBest = long.MaxValue;
        long standardBest = long.MaxValue;
        for (int b = 0; b < Batches; b++)
        {
            offBest = Math.Min(offBest, Run(off, TicksPerBatch));
            standardBest = Math.Min(standardBest, Run(standard, TicksPerBatch));
        }

        double offMs = offBest * 1000.0 / Stopwatch.Frequency / TicksPerBatch;
        double standardMs = standardBest * 1000.0 / Stopwatch.Frequency / TicksPerBatch;
        double overhead = (standardMs - offMs) / offMs;
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Off: {offMs:F3} ms/Tick, Standard: {standardMs:F3} ms/Tick, overhead {overhead * 100:F2}% (best of {Batches} batches, {standard.Diagnostics.Latest.Tick.Jobs} jobs/Tick, {Workers} workers)");

        Assert.Equal(off.Hash(), standard.Hash());
        Assert.True(standardMs < offMs * 2, $"Standard-level diagnostics doubled the Tick time ({overhead * 100:F2}%); something is badly wrong, not noise");
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
