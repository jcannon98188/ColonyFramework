using System.Text.Json;
using Colony.Scenario;

namespace Colony.Diagnostics.Tests;

/// <summary>
/// TASK-18 AC #2: a trace exported from a captured run is Chrome Trace Event JSON (the format the Perfetto UI
/// opens) with Phases, Systems and one lane per worker. The UI cannot be driven from a test, so these tests
/// parse the JSON back and check the lanes and slices the UI would draw.
/// </summary>
public sealed class PerfettoTraceTests
{
    private const int Colonists = 2_000;
    private const int Food = 200;

    [Fact]
    public void A_detailed_capture_exports_tick_phase_system_and_worker_lanes()
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 4, colonists: Colonists, food: Food, options: new DiagnosticsOptions { Level = DiagnosticsLevel.Detailed });
        var capture = new TraceCapture(world, tickCapacity: 16);
        for (int t = 0; t < 3; t++)
        {
            world.Tick();
            Assert.True(capture.Capture());
        }

        Assert.Equal(3, capture.Count);
        using var document = Export(capture);
        var events = document.RootElement.GetProperty("traceEvents").EnumerateArray().ToList();

        // Lanes: metadata names the Tick, Phase and System lanes and one lane per worker, all in one process.
        var laneNames = events.Where(e => e.GetProperty("ph").GetString() == "M" && e.GetProperty("name").GetString() == "thread_name")
            .ToDictionary(e => e.GetProperty("tid").GetInt32(), e => e.GetProperty("args").GetProperty("name").GetString());
        Assert.Equal("Ticks", laneNames[PerfettoTrace.TickLane]);
        Assert.Equal("Phases", laneNames[PerfettoTrace.PhaseLane]);
        Assert.Equal("Systems", laneNames[PerfettoTrace.SystemLane]);
        for (int w = 0; w < 4; w++)
        {
            Assert.Equal($"Worker {w}", laneNames[PerfettoTrace.WorkerLane(w)]);
        }

        Assert.All(events, e => Assert.Equal(PerfettoTrace.ProcessId, e.GetProperty("pid").GetInt32()));

        // Slices: complete events with a start and a duration in microseconds.
        var slices = events.Where(e => e.GetProperty("ph").GetString() == "X").ToList();
        Assert.All(slices, e =>
        {
            Assert.True(e.GetProperty("ts").GetDouble() >= 0);
            Assert.True(e.GetProperty("dur").GetDouble() >= 0);
            Assert.False(string.IsNullOrEmpty(e.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrEmpty(e.GetProperty("cat").GetString()));
        });

        var ticks = slices.Where(e => e.GetProperty("tid").GetInt32() == PerfettoTrace.TickLane).ToList();
        Assert.Equal(["Tick 0", "Tick 1", "Tick 2"], ticks.Select(e => e.GetProperty("name").GetString()).ToArray());
        Assert.True(ticks[0].GetProperty("ts").GetDouble() + ticks[0].GetProperty("dur").GetDouble() <= ticks[1].GetProperty("ts").GetDouble() + 1e-6);

        var phases = slices.Where(e => e.GetProperty("tid").GetInt32() == PerfettoTrace.PhaseLane).ToList();
        Assert.Equal(3 * schema.Phases.Length, phases.Count);
        Assert.Contains(phases, e => e.GetProperty("name").GetString() == Phase.Simulate.Name);
        Assert.All(phases, e => Assert.Equal("phase", e.GetProperty("cat").GetString()));

        var systems = slices.Where(e => e.GetProperty("tid").GetInt32() == PerfettoTrace.SystemLane).ToList();
        Assert.Contains(systems, e => e.GetProperty("name").GetString() == "Wander");
        Assert.Contains(systems, e => e.GetProperty("name").GetString() == "Eat");
        Assert.All(systems, e => Assert.Equal("system", e.GetProperty("cat").GetString()));
        Assert.All(systems, e => Assert.True(e.GetProperty("args").GetProperty("jobs").GetInt32() >= 0));

        // Worker lanes carry the Detailed job trace: one slice per job, named after its System, with the chunk.
        var jobs = slices.Where(e => e.GetProperty("tid").GetInt32() >= PerfettoTrace.WorkerLane(0)).ToList();
        int tracedJobs = 0;
        for (int ago = 0; ago < 3; ago++)
        {
            tracedJobs += world.Diagnostics.Record(ago).Jobs.Length;
        }

        Assert.Equal(tracedJobs, jobs.Count);
        Assert.True(jobs.Count > 0);
        Assert.Contains(jobs, e => e.GetProperty("name").GetString() == "Wander");
        Assert.All(jobs, e =>
        {
            Assert.Equal("job", e.GetProperty("cat").GetString());
            Assert.True(e.GetProperty("args").GetProperty("chunk").GetInt32() >= 0);
            Assert.True(e.GetProperty("args").GetProperty("job").GetInt32() >= 0);
        });

        // Every job lies inside its Tick's slice.
        foreach (var tick in ticks)
        {
            double start = tick.GetProperty("ts").GetDouble();
            double end = start + tick.GetProperty("dur").GetDouble();
            long number = tick.GetProperty("args").GetProperty("tick").GetInt64();
            foreach (var job in jobs.Where(j => j.GetProperty("args").GetProperty("tick").GetInt64() == number))
            {
                Assert.InRange(job.GetProperty("ts").GetDouble(), start - 1e-6, end + 1e-6);
            }
        }

        // Tick markers: one instant event per Tick.
        var markers = events.Where(e => e.GetProperty("ph").GetString() == "i").ToList();
        Assert.Equal(3, markers.Count);
        Assert.Equal(["Tick 0", "Tick 1", "Tick 2"], markers.Select(e => e.GetProperty("name").GetString()).ToArray());
    }

    [Fact]
    public void A_standard_capture_has_no_job_slices_but_still_shows_worker_busy_time()
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 2, colonists: Colonists, food: Food);
        var capture = new TraceCapture(world, tickCapacity: 4);
        world.Tick();
        capture.Capture();

        using var document = Export(capture);
        var slices = document.RootElement.GetProperty("traceEvents").EnumerateArray().Where(e => e.GetProperty("ph").GetString() == "X").ToList();

        Assert.DoesNotContain(slices, e => e.GetProperty("cat").GetString() == "job");
        var busy = slices.Where(e => e.GetProperty("cat").GetString() == "worker").ToList();
        Assert.Equal(2, busy.Count);
        Assert.Contains(slices, e => e.GetProperty("cat").GetString() == "system" && e.GetProperty("name").GetString() == "Wander");
    }

    [Fact]
    public void The_capture_keeps_the_latest_ticks_when_full()
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 200, food: 20);
        var capture = new TraceCapture(world, tickCapacity: 2);
        for (int t = 0; t < 5; t++)
        {
            world.Tick();
            capture.Capture();
        }

        Assert.Equal(2, capture.Count);
        Assert.Equal(5, capture.TicksCaptured);
        using var document = Export(capture);
        var ticks = document.RootElement.GetProperty("traceEvents").EnumerateArray()
            .Where(e => e.GetProperty("ph").GetString() == "X" && e.GetProperty("tid").GetInt32() == PerfettoTrace.TickLane)
            .Select(e => e.GetProperty("args").GetProperty("tick").GetInt64()).ToArray();
        Assert.Equal([3L, 4L], ticks);

        capture.Clear();
        Assert.Equal(0, capture.Count);
        Assert.Equal(5, capture.TicksCaptured);
    }

    [Fact]
    public void Capturing_before_any_tick_captures_nothing()
    {
        var schema = ColonistScenario.BuildSchema(foodTarget: Food);
        using var world = ColonistScenario.CreateWorld(schema, workers: 1, colonists: 200, food: 20);
        var capture = new TraceCapture(world, tickCapacity: 2);

        Assert.False(capture.Capture());
        Assert.Equal(0, capture.Count);
        using var document = Export(capture);
        Assert.DoesNotContain(document.RootElement.GetProperty("traceEvents").EnumerateArray(), e => e.GetProperty("ph").GetString() == "X");
    }

    private static JsonDocument Export(TraceCapture capture)
    {
        using var stream = new MemoryStream();
        PerfettoTrace.Write(capture, stream);
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }
}
