using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Colony.Diagnostics;
using Colony.Persistence;
using Colony.Scenario;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Benchmarks.TickHarness;

/// <summary>
/// The custom Tick harness (spec section 10.3). Options: <c>--scenario colonist-100k|colonist-1m</c> (default:
/// both), <c>--colonists N</c> / <c>--food N</c> (override the scenario's sizes), <c>--ticks N</c> measured
/// Ticks (60), <c>--warmup N</c> (100), <c>--workers N</c> (1; 0 = physical cores), <c>--diagnostics
/// Off|Standard|Detailed</c> (Off), <c>--no-fields</c>, <c>--no-save</c>, <c>--out file.json</c>
/// (tick-harness.json), <c>--baseline file.json</c> (compare: counts gate, timing reports), <c>--list</c>.
/// Exit 0 when every gate passed, 1 when a gate failed, 2 on bad usage.
/// </summary>
public static class TickHarness
{
    public const ulong Seed = ColonistScenario.DefaultSeed;

    /// <summary>The scenario sizes of spec section 10.3: 100k colonists with 5k food, 1M colonists with 50k food.</summary>
    public static IReadOnlyList<HarnessScenario> Scenarios { get; } =
    [
        new("colonist-100k", 100_000, 5_000),
        new("colonist-1m", 1_000_000, 50_000),
    ];

    public static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var options = new HarnessOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list":
                    foreach (var scenario in Scenarios)
                    {
                        output.WriteLine(scenario.Name);
                    }

                    return 0;
                case "--no-fields":
                    options.Fields = false;
                    continue;
                case "--no-save":
                    options.SaveLoad = false;
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
                    case "--scenario":
                        options.Scenario = value;
                        break;
                    case "--colonists":
                        options.Colonists = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--food":
                        options.Food = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--ticks":
                        options.Ticks = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--warmup":
                        options.Warmup = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--workers":
                        options.Workers = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--diagnostics":
                        options.Diagnostics = Enum.Parse<DiagnosticsLevel>(value, ignoreCase: true);
                        break;
                    case "--out":
                        options.OutputPath = value;
                        break;
                    case "--baseline":
                        options.BaselinePath = value;
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

        if (options.Ticks <= 0 || options.Warmup < 0 || options.Workers < 0)
        {
            error.WriteLine("--ticks must be positive; --warmup and --workers must not be negative.");
            return 2;
        }

        var selected = new List<HarnessScenario>();
        foreach (var scenario in Scenarios)
        {
            if (options.Scenario is null || string.Equals(scenario.Name, options.Scenario, StringComparison.Ordinal))
            {
                selected.Add(scenario);
            }
        }

        if (selected.Count == 0)
        {
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"No scenario named '{options.Scenario}'. Use --list to see the names."));
            return 2;
        }

        HarnessReport? baseline = null;
        if (options.BaselinePath is not null)
        {
            if (!File.Exists(options.BaselinePath))
            {
                error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Baseline {options.BaselinePath} does not exist."));
                return 2;
            }

            baseline = HarnessReport.ReadFile(options.BaselinePath);
        }

        var runs = new List<RunReport>();
        bool passed = true;
        foreach (var scenario in selected)
        {
            var run = Measure(scenario, options);
            var baselineRun = baseline?.Find(run.Scenario.Name, run.Scenario.Workers);
            run = run with
            {
                Gates = Gates.Evaluate(run, baselineRun),
                Comparison = baselineRun is null ? null : Gates.Compare(run, baselineRun, baseline!),
            };
            runs.Add(run);
            Describe(run, output);
            foreach (var gate in run.Gates)
            {
                if (!gate.Passed)
                {
                    passed = false;
                    error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"GATE FAILED {run.Scenario.Name} ({run.Scenario.Workers} worker(s)) {gate}"));
                }
            }
        }

        var report = new HarnessReport(HarnessReport.CurrentFormatVersion, DescribeMachine(), FindCommit(), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), runs);
        report.WriteFile(options.OutputPath);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"wrote {Path.GetFullPath(options.OutputPath)}"));
        return passed ? 0 : 1;
    }

    /// <summary>Builds the World, warms it up, measures <see cref="HarnessOptions.Ticks"/> Ticks, then runs the save/load round trip. Gates are not evaluated here.</summary>
    public static RunReport Measure(HarnessScenario scenario, HarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(options);
        int colonists = options.Colonists ?? scenario.Colonists;
        int food = options.Food ?? scenario.Food;

        var builder = new WorldBuilder();
        ColonistScenario.Compose(builder, sabotage: false, foodTarget: food);
        FieldScenario? fieldScenario = options.Fields ? new FieldScenario() : null;
        fieldScenario?.Compose(builder);
        var schema = builder.Build();

        var worldOptions = new WorldOptions
        {
            Seed = Seed,
            WorkerThreads = options.Workers,
            InitialEntityCapacity = (colonists + food) * 2,
            Diagnostics = DiagnosticsFor(options.Diagnostics),
        };
        using var world = schema.CreateWorld(worldOptions);
        ColonistScenario.Populate(world, colonists, food);
        fieldScenario?.Populate(world);

        for (int i = 0; i < options.Warmup; i++)
        {
            world.Tick();
        }

        if (options.Warmup > 0)
        {
            // Let tiered compilation finish promoting the Tick path before measuring (see CONVENTIONS, TASK-24).
            Thread.Sleep(300);
        }

        var samples = new long[options.Ticks];
        var countersBefore = world.SchedulerCounters;
        long mainBefore = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            world.Tick();
            samples[i] = Stopwatch.GetTimestamp() - start;
        }

        long mainBytes = GC.GetAllocatedBytesForCurrentThread() - mainBefore;
        long workerBytes = world.WorkerAllocatedBytes - workersBefore;
        var countersAfter = world.SchedulerCounters;
        ulong hash = world.Hash();

        var counts = new CountReport(
            mainBytes,
            workerBytes,
            countersAfter.ChunksVisited - countersBefore.ChunksVisited,
            countersAfter.ChunksSkippedAsleep - countersBefore.ChunksSkippedAsleep,
            countersAfter.ChunksSkippedByChangeFilter - countersBefore.ChunksSkippedByChangeFilter,
            countersAfter.ChunksSkippedDisabled - countersBefore.ChunksSkippedDisabled,
            world.EntityCount,
            hash);

        SaveLoadReport? saveLoad = options.SaveLoad ? RoundTrip(world, schema, worldOptions, hash) : null;

        return new RunReport(
            new ScenarioReport(scenario.Name, colonists, food, world.WorkerCount, options.Ticks, options.Warmup, options.Diagnostics.ToString(), options.Fields),
            Summarise(samples),
            counts,
            saveLoad,
            Gates: [],
            Comparison: null);
    }

    private static SaveLoadReport RoundTrip(World world, WorldSchema schema, WorldOptions worldOptions, ulong hash)
    {
        long start = Stopwatch.GetTimestamp();
        byte[] bytes = world.SaveToBytes();
        double saveMs = ToMs(Stopwatch.GetTimestamp() - start);

        start = Stopwatch.GetTimestamp();
        using var loaded = WorldPersistence.Load(bytes, schema, new LoadOptions { WorldOptions = worldOptions });
        double loadMs = ToMs(Stopwatch.GetTimestamp() - start);
        bool loadedMatches = loaded.Hash() == hash;

        // Save-load-continue equals never-saved (spec section 10.3): one more Tick on both Worlds.
        world.Tick();
        loaded.Tick();
        bool continuedMatches = loaded.Hash() == world.Hash();
        return new SaveLoadReport(saveMs, loadMs, bytes.Length, loadedMatches, continuedMatches);
    }

    private static DiagnosticsOptions DiagnosticsFor(DiagnosticsLevel level)
        => level == DiagnosticsLevel.Off
            ? new DiagnosticsOptions { Level = DiagnosticsLevel.Off, AllocationCheck = false }
            : new DiagnosticsOptions { Level = level };

    private static TimingReport Summarise(long[] samples)
    {
        var sorted = (long[])samples.Clone();
        Array.Sort(sorted);
        double sum = 0;
        foreach (long sample in sorted)
        {
            sum += sample;
        }

        return new TimingReport(
            ToMs(sum / sorted.Length),
            ToMs(Percentile(sorted, 0.50)),
            ToMs(Percentile(sorted, 0.90)),
            ToMs(Percentile(sorted, 0.99)),
            ToMs(sorted[0]),
            ToMs(sorted[^1]),
            sorted.Length);
    }

    /// <summary>Nearest-rank percentile over the sorted samples.</summary>
    private static long Percentile(long[] sorted, double q)
    {
        int rank = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private static double ToMs(double stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency;

    private static void Describe(RunReport run, TextWriter output)
    {
        var s = run.Scenario;
        var t = run.Timing;
        var c = run.Counts;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{s.Name}: {s.Colonists} colonists, {s.Food} food, {s.Workers} worker(s), diagnostics {s.Diagnostics}, fields {(s.Fields ? "on" : "off")}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  ms/Tick over {t.Samples} Ticks: mean {t.MeanMs:F3}, p50 {t.P50Ms:F3}, p90 {t.P90Ms:F3}, p99 {t.P99Ms:F3}, min {t.MinMs:F3}, max {t.MaxMs:F3}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  allocated {c.MainThreadAllocatedBytes} B main + {c.WorkerAllocatedBytes} B workers; chunks visited {c.ChunksVisited}; {c.Entities} Entities; hash 0x{c.WorldHash:X16}"));
        if (run.SaveLoad is { } sl)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  save {sl.SaveMs:F2} ms ({sl.Bytes} B), load {sl.LoadMs:F2} ms, loaded hash match {sl.LoadedHashMatches}, continued match {sl.ContinuedHashMatches}"));
        }

        if (run.Comparison is { } cmp)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  vs baseline {cmp.BaselineCommit} ({cmp.BaselineTimestamp}): p50 {cmp.P50DeltaPercent:+0.0;-0.0}% (was {cmp.BaselineP50Ms:F3} ms), mean {cmp.MeanDeltaPercent:+0.0;-0.0}% (timing is report-only)"));
        }

        foreach (var gate in run.Gates)
        {
            output.WriteLine("  gate " + gate);
        }
    }

    private static MachineReport DescribeMachine()
        => new(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            RuntimeInformation.FrameworkDescription,
            Vector256.IsHardwareAccelerated);

    /// <summary>The commit under test: <c>git rev-parse HEAD</c> of the repository containing the harness, else <c>GITHUB_SHA</c>, else "unknown".</summary>
    private static string FindCommit()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Colony.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is not null)
        {
            try
            {
                var start = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = Process.Start(start);
                if (process is not null)
                {
                    string text = process.StandardOutput.ReadToEnd().Trim();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0 && text.Length > 0)
                    {
                        return text;
                    }
                }
            }
            catch (Win32Exception)
            {
                // git is not installed: fall through.
            }
            catch (InvalidOperationException)
            {
            }
        }

        return Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown";
    }
}

/// <summary>A named scenario size of the harness.</summary>
public sealed record HarnessScenario(string Name, int Colonists, int Food);

/// <summary>Parsed command line of <see cref="TickHarness.Run"/>; also usable directly from code.</summary>
public sealed class HarnessOptions
{
    public string? Scenario { get; set; }

    public int? Colonists { get; set; }

    public int? Food { get; set; }

    public int Ticks { get; set; } = 60;

    public int Warmup { get; set; } = 100;

    public int Workers { get; set; } = 1;

    public DiagnosticsLevel Diagnostics { get; set; } = DiagnosticsLevel.Off;

    public bool Fields { get; set; } = true;

    public bool SaveLoad { get; set; } = true;

    public string OutputPath { get; set; } = "tick-harness.json";

    public string? BaselinePath { get; set; }
}
