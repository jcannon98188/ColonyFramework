using System.Globalization;
using Colony.Diagnostics;
using Colony.Diagnostics.Inspection;
using Colony.Fields;
using Colony.Persistence;

namespace Colony.Samples.Hello;

/// <summary>
/// The headless host around the sketch (spec section 14, "a minimal headless host"). Options: <c>--ticks N</c>
/// (1000; 0 = run until an inspection client sends <c>shutdown</c>), <c>--colonists N</c> (100), <c>--workers N</c>
/// (1; 0 = physical cores), <c>--save path</c> (colony.save) or <c>--no-save</c>, <c>--counters</c> (attach the
/// MeterBridge and print the dotnet-counters command), <c>--trace out.json</c> (Perfetto trace of every Tick),
/// <c>--level Standard|Detailed</c>, <c>--pace ms</c> (sleep between Ticks so counters can be watched),
/// <c>--replay out.replay</c> (record a replay log; enables <c>rewind</c>), <c>--sketch</c> (run the literal spec
/// sketch instead). Inspection (spec section 10.2, compiled out under <c>COLONY_NO_INSPECT</c>): <c>--inspect PORT</c>
/// (0 = any free port) starts the loopback inspection server and prints <c>inspect: port N token T</c>;
/// <c>--inspect-token T</c> sets the token (random otherwise); <c>--allow-mutation</c> registers the debug
/// commands and lets clients mutate. Prints <c>hash 0x...</c>; exit 0, or 2 on bad usage.
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
        string? replayPath = null;
        bool counters = false;
        int? inspectPort = null;
        string? inspectToken = null;
        bool allowMutation = false;
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
                case "--allow-mutation":
                    allowMutation = true;
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
                    case "--replay":
                        replayPath = value;
                        break;
                    case "--level":
                        level = Enum.Parse<DiagnosticsLevel>(value, ignoreCase: true);
                        break;
                    case "--inspect":
                        inspectPort = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--inspect-token":
                        inspectToken = value;
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

        if (ticks == 0 && inspectPort is null)
        {
            error.WriteLine("--ticks 0 runs until an inspection client sends shutdown; it needs --inspect PORT.");
            return 2;
        }

#if COLONY_NO_INSPECT
        if (inspectPort is not null || inspectToken is not null || allowMutation)
        {
            error.WriteLine("This host was built with COLONY_NO_INSPECT: the inspection server does not exist.");
            return 2;
        }
#endif

        var schema = BuildSchema(debugCommands: allowMutation);
        // The gate is what colony-inspect pause/step/resume act on; only an inspected host needs one.
        using var world = CreateWorld(schema, colonists, workers, level, inspectPort is null ? null : new TickGate());
        using var bridge = counters ? new MeterBridge(world) : null;
        var capture = tracePath is null ? null : new TraceCapture(world, tickCapacity: Math.Max(1, ticks));
        var recorder = replayPath is null ? null : new ReplayRecorder(world, new ReplayOptions { CheckpointEvery = 1 });

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"HelloColony: {world.EntityCount} colonists on {world.WorkerCount} worker(s), {(ticks == 0 ? "unbounded" : ticks.ToString(CultureInfo.InvariantCulture))} Tick(s), diagnostics {level}"));
        if (bridge is not null)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"observe: dotnet-counters monitor --process-id {Environment.ProcessId} --counters {MeterBridge.DefaultMeterName},{MeterBridge.EventSourceName}"));
        }

#if !COLONY_NO_INSPECT
        using var server = inspectPort is null ? null : new InspectionServer(world, new InspectionServerOptions
        {
            Token = inspectToken,
            AllowMutation = allowMutation,
            Replay = recorder,
        });
        if (server is not null && inspectPort is int port)
        {
            var endpoint = server.ListenTcp(port);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"inspect: port {endpoint.Port} token {server.Token}{(allowMutation ? " mutation allowed" : " read-only")}"));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"inspect: colony-inspect status --port {endpoint.Port} --token {server.Token}"));
        }
#endif

        for (int tick = 0; ticks == 0 || tick < ticks;)
        {
#if !COLONY_NO_INSPECT
            if (server is not null)
            {
                server.Pump();
                if (server.ShutdownRequested)
                {
                    break;
                }
            }
#endif

            bool ticked = recorder is null ? world.Tick() : recorder.Tick();
            if (!ticked)
            {
#if !COLONY_NO_INSPECT
                // Paused through the Tick gate: wait for the next request instead of spinning.
                server?.WaitForRequest(TimeSpan.FromMilliseconds(20));
#endif
                continue;
            }

            tick++;
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

        if (recorder is not null)
        {
            recorder.WriteFile(replayPath!);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote replay of Ticks {recorder.StartTick}..{recorder.EndTick} to {Path.GetFullPath(replayPath!)}"));
        }

        if (capture is not null)
        {
            PerfettoTrace.WriteFile(capture, tracePath!);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {capture.Count} Tick(s) of trace to {Path.GetFullPath(tracePath!)}; open it at https://ui.perfetto.dev"));
        }

        return 0;
    }

    /// <summary>
    /// The sketch's schema: Hunger and Food Components, the heat Field, the generated Starve System. With
    /// <paramref name="debugCommands"/> the debug Input Command of spec section 10.2 is registered too (the schema
    /// hash, and so the World hash, then differ from the plain sketch).
    /// </summary>
    public static WorldSchema BuildSchema(bool debugCommands = false)
    {
        var builder = new WorldBuilder()
            .AddComponent<Hunger>().AddComponent<Food>();
        builder.AddFields(new WorldSize(64, 64, 1)).AddField<Heat>("heat");
        ColonySystems.AddSystems(builder);
        if (debugCommands)
        {
            builder.AddDebugCommands();
        }

        return builder.Build();
    }

    /// <summary>Creates and populates the World; <paramref name="gate"/> is the Tick gate inspection pause/step act on (null = none).</summary>
    public static World CreateWorld(WorldSchema schema, int colonists, int workers, DiagnosticsLevel level, TickGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var world = schema.CreateWorld(new WorldOptions
        {
            Seed = Seed,
            WorkerThreads = workers,
            InitialEntityCapacity = Math.Max(1024, colonists * 2),
            Diagnostics = new DiagnosticsOptions { Level = level },
            TickGate = gate,
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
