using Colony.Scenario;

namespace Colony.Diagnostics.Tests;

/// <summary>
/// TASK-18 AC #3: a sink changes no World hash and introduces no steady-state allocation on Framework threads.
/// The colonist scenario runs with and without the sinks attached; the per-Tick record's own allocation check
/// (every Tick thread) and the host thread's allocation counter must both stay at zero over the measured window.
/// </summary>
[Collection(EventSourceTestGroup.Name)]
public sealed class SinkIsolationTests
{
    private const int Colonists = 3_000;
    private const int Food = 300;
    private const int Ticks = 20;

    [Fact]
    public void The_world_hash_is_identical_with_and_without_sinks_attached()
    {
        ulong bare = Run(workers: 4, sinks: false, level: DiagnosticsLevel.Standard);
        ulong traced = Run(workers: 4, sinks: true, level: DiagnosticsLevel.Detailed);
        ulong tracedOneWorker = Run(workers: 1, sinks: true, level: DiagnosticsLevel.Detailed);

        Assert.Equal(bare, traced);
        Assert.Equal(bare, tracedOneWorker);
    }

    [Fact]
    public void Capturing_and_publishing_after_each_tick_allocates_nothing_in_steady_state()
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 4, colonists: Colonists, food: Food, options: new DiagnosticsOptions { Level = DiagnosticsLevel.Detailed });
        var capture = new TraceCapture(world, tickCapacity: 8);
        using var bridge = new MeterBridge(world);
        for (int t = 0; t < 10; t++)
        {
            world.Tick();
            capture.Capture();
            bridge.Publish();
        }

        long hostBefore = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        long recordedAllocations = 0;
        for (int t = 0; t < 10; t++)
        {
            world.Tick();
            capture.Capture();
            bridge.Publish();
            recordedAllocations += world.Diagnostics.Latest.Tick.AllocatedBytes;
        }

        long hostAllocated = GC.GetAllocatedBytesForCurrentThread() - hostBefore;
        long workersAllocated = world.WorkerAllocatedBytes - workersBefore;

        Assert.Equal(0, hostAllocated);
        Assert.Equal(0, workersAllocated);
        Assert.Equal(0, recordedAllocations);
        Assert.Equal(20, bridge.TicksPublished);
    }

    private static ulong Run(int workers, bool sinks, DiagnosticsLevel level)
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers, colonists: Colonists, food: Food, options: new DiagnosticsOptions { Level = level });
        TraceCapture? capture = sinks ? new TraceCapture(world, tickCapacity: Ticks) : null;
        using MeterBridge? bridge = sinks ? new MeterBridge(world) : null;
        for (int t = 0; t < Ticks; t++)
        {
            world.Tick();
            capture?.Capture();
            bridge?.Publish();
        }

        if (capture is not null)
        {
            using var stream = new MemoryStream();
            PerfettoTrace.Write(capture, stream);
            Assert.True(stream.Length > 0);
        }

        return world.Hash();
    }
}
