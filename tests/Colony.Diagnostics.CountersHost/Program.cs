using System.Globalization;
using Colony.Diagnostics;
using Colony.Scenario;

// Runs the colonist scenario with the TASK-18 sinks attached. Options: --ticks N (default 600, 0 = until
// Ctrl+C), --workers N, --level Standard|Detailed, --trace <path> (Perfetto JSON written at the end),
// --pace <ms per Tick> (default 16 so dotnet-counters has time to poll).
int ticks = 600;
int workers = 4;
int pace = 16;
string? tracePath = null;
var level = DiagnosticsLevel.Standard;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--ticks":
            ticks = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--workers":
            workers = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--pace":
            pace = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            break;
        case "--trace":
            tracePath = args[i + 1];
            break;
        case "--level":
            level = Enum.Parse<DiagnosticsLevel>(args[i + 1], ignoreCase: true);
            break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}");
            return 2;
    }
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var schema = ColonistScenario.BuildSchema();
using var world = ColonistScenario.CreateWorld(schema, workers, options: new DiagnosticsOptions { Level = level });
using var bridge = new MeterBridge(world);
var capture = new TraceCapture(world, tickCapacity: 256);

Console.WriteLine($"pid {Environment.ProcessId}: {world.EntityCount} Entities on {world.WorkerCount} worker(s), level {level}");
Console.WriteLine($"observe: dotnet-counters monitor --process-id {Environment.ProcessId} --counters {MeterBridge.DefaultMeterName},{MeterBridge.EventSourceName}");
Console.WriteLine(ticks > 0 ? $"running {ticks} Tick(s), {pace} ms apart" : "running until Ctrl+C");

long run = 0;
while (!stop.IsCancellationRequested && (ticks <= 0 || run < ticks))
{
    world.Tick();
    capture.Capture();
    bridge.Publish();
    run++;
    if (pace > 0)
    {
        Thread.Sleep(pace);
    }
}

var latest = world.Diagnostics.Latest;
Console.WriteLine($"ran {run} Tick(s); last Tick {latest.Tick.WallTicks * 1000.0 / TickRecord.TicksPerSecond:F3} ms, {latest.Tick.Jobs} jobs, {latest.Tick.AllocatedBytes} bytes allocated on Tick threads; hash {world.Hash():x16}");
if (tracePath is not null)
{
    PerfettoTrace.WriteFile(capture, tracePath);
    Console.WriteLine($"wrote {capture.Count} Tick(s) of trace to {Path.GetFullPath(tracePath)}; open it at https://ui.perfetto.dev");
}

return 0;
