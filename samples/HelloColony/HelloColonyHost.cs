using System.Globalization;
using Colony.Diagnostics;
using Colony.Fields;
using Colony.Persistence;

namespace Colony.Samples.Hello;

/// <summary>
/// The headless host around the sketch (spec section 14, "a minimal headless host"). Options: <c>--ticks N</c>
/// (1000), <c>--colonists N</c> (100), <c>--workers N</c> (1; 0 = physical cores), <c>--save path</c>
/// (colony.save) or <c>--no-save</c>, <c>--counters</c> (attach the MeterBridge and print the dotnet-counters
/// command), <c>--trace out.json</c> (Perfetto trace of every Tick), <c>--level Standard|Detailed</c>,
/// <c>--pace ms</c> (sleep between Ticks so counters can be watched), <c>--sketch</c> (run the literal spec
/// sketch instead). Prints <c>hash 0x...</c>; exit 0, or 2 on bad usage.
/// </summary>
public static class HelloColonyHost
{
    public const ulong Seed = 12345;

    public static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        int ticks = 1000;
        int colonists = 100;
        int workers = 1;
        int pace = 0;
        string? savePath = "colony.save";
        string? tracePath = null;
        bool counters = false;
        var level = DiagnosticsLevel.Standard;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sketch":
                    Sketch.Run();
                    return 0;
                case "--counters":
                    counters = true;
                    continue;
                case "--no-save":
                    savePath = null;
                    continue;
            }

            if (i + 1 >= args.Length)
            {
                error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Option {args[i]} needs a value."));
                return 2;
            }

            string value = args[++i];
            try
            {
                switch (args[i - 1])
                {
                    case "--ticks":
                        ticks = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--colonists":
                        colonists = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--workers":
                        workers = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--pace":
                        pace = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--save":
                        savePath = value;
                        break;
                    case "--trace":
                        tracePath = value;
                        break;
                    case "--level":
                        level = Enum.Parse<DiagnosticsLevel>(value, ignoreCase: true);
                        break;
                    default:
                        error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Unknown option {args[i - 1]}."));
                        return 2;
                }
            }
            catch (FormatException)
            {
                error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Option {args[i - 1]} has a bad value '{value}'."));
                return 2;
            }
            catch (ArgumentException)
            {
                error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Option {args[i - 1]} has a bad value '{value}'."));
                return 2;
            }
        }

        var schema = BuildSchema();
        using var world = CreateWorld(schema, colonists, workers, level);
        using var bridge = counters ? new MeterBridge(world) : null;
        var capture = tracePath is null ? null : new TraceCapture(world, tickCapacity: Math.Max(1, ticks));

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"HelloColony: {world.EntityCount} colonists on {world.WorkerCount} worker(s), {ticks} Tick(s), diagnostics {level}"));
        if (bridge is not null)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"observe: dotnet-counters monitor --process-id {Environment.ProcessId} --counters {MeterBridge.DefaultMeterName},{MeterBridge.EventSourceName}"));
        }

        for (int tick = 0; tick < ticks; tick++)
        {
            world.Tick();
            capture?.Capture();
            bridge?.Publish();
            if (pace > 0)
            {
                Thread.Sleep(pace);
            }
        }

        ulong hash = world.Hash();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hash 0x{hash:X16}"));

        if (savePath is not null)
        {
            world.Save(savePath, SaveOptions.Default);
            using var loaded = WorldPersistence.Load(savePath, schema);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"saved {Path.GetFullPath(savePath)} ({new FileInfo(savePath).Length} B); loaded hash matches {loaded.Hash() == hash}"));
        }

        if (capture is not null)
        {
            PerfettoTrace.WriteFile(capture, tracePath!);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {capture.Count} Tick(s) of trace to {Path.GetFullPath(tracePath!)}; open it at https://ui.perfetto.dev"));
        }

        return 0;
    }

    /// <summary>The sketch's schema: Hunger and Food Components, the heat Field, the generated Starve System.</summary>
    public static WorldSchema BuildSchema()
    {
        var builder = new WorldBuilder()
            .AddComponent<Hunger>().AddComponent<Food>();
        builder.AddFields(new WorldSize(64, 64, 1)).AddField<Heat>("heat");
        ColonySystems.AddSystems(builder);
        return builder.Build();
    }

    public static World CreateWorld(WorldSchema schema, int colonists, int workers, DiagnosticsLevel level)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var world = schema.CreateWorld(new WorldOptions
        {
            Seed = Seed,
            WorkerThreads = workers,
            InitialEntityCapacity = Math.Max(1024, colonists * 2),
            Diagnostics = new DiagnosticsOptions { Level = level },
        });
        var commands = world.CreateCommandBuffer();
        var hunger = ComponentMask.Of(schema.Component<Hunger>());
        for (int i = 0; i < colonists; i++)
        {
            commands.Create(hunger);
        }

        world.Playback(commands);
        return world;
    }

    /// <summary>Reads the <c>hash 0x...</c> line back from the host output.</summary>
    public static ulong ParseHash(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("hash 0x", StringComparison.Ordinal))
            {
                return ulong.Parse(line.AsSpan("hash 0x".Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        throw new FormatException("No 'hash 0x...' line in the host output.");
    }
}
