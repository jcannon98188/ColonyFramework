using BenchmarkDotNet.Attributes;
using Colony.Diagnostics;
using Colony.Scenario;

namespace Colony.Benchmarks;

/// <summary>The scheduler and the whole colonist Tick (spec section 4.3): 100k colonists, 1% churn, on one worker and on every physical core.</summary>
public class SchedulerBenchmarks
{
    public const int Colonists = 100_000;

    private World _world = null!;

    /// <summary>1 = single-threaded (the prototype's 1.02 ms/Tick reference); 0 = all physical cores.</summary>
    [Params(1, 0)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _world = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), Workers, colonists: Colonists, options: new DiagnosticsOptions { Level = DiagnosticsLevel.Off, AllocationCheck = false });
        for (int i = 0; i < 20; i++)
        {
            _world.Tick();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _world.Dispose();

    [Benchmark]
    public bool ColonistTick() => _world.Tick();
}

/// <summary>
/// TASK-17's deferred gate: Standard-level diagnostics (per-job timing and allocation counters folded into the
/// Tick record) must cost under about 1% of the Off-level Tick. Both Worlds run the same colonist scenario.
/// </summary>
public class DiagnosticsOverheadBenchmarks
{
    public const int Colonists = 100_000;

    private World _off = null!;
    private World _standard = null!;

    [Params(1, 4)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _off = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), Workers, colonists: Colonists, options: new DiagnosticsOptions { Level = DiagnosticsLevel.Off, AllocationCheck = false });
        _standard = ColonistScenario.CreateWorld(ColonistScenario.BuildSchema(), Workers, colonists: Colonists, options: new DiagnosticsOptions { Level = DiagnosticsLevel.Standard });
        for (int i = 0; i < 20; i++)
        {
            _off.Tick();
            _standard.Tick();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _off.Dispose();
        _standard.Dispose();
    }

    [Benchmark(Baseline = true)]
    public bool TickOff() => _off.Tick();

    [Benchmark]
    public bool TickStandard() => _standard.Tick();
}
