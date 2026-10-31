using System.Globalization;

namespace Colony.Testing;

/// <summary>
/// A console entry point for the determinism harness, so a scenario can run inside a separately built
/// process (Native AOT with <c>IlcInstructionSet x86-64-v3</c>, another OS, another ISA setting) and its
/// hashes compared with the in-process JIT run (spec section 6, Tier 1). Options:
/// <c>--scenario &lt;name&gt;</c> (default: the first), <c>--ticks N</c>, <c>--workers 1,4</c>,
/// <c>--every K</c> (checkpoint every K Ticks), <c>--list</c>. Output: one line per worker count,
/// <c>&lt;workers&gt; &lt;hash&gt;...</c> in hex; exit code 0, or 1 when the run itself failed (fault,
/// leak or divergence between worker counts), 2 on bad usage.
/// </summary>
public static class HarnessHost
{
    public static int Run(ReadOnlySpan<string> args, IReadOnlyList<Scenario> scenarios, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (scenarios.Count == 0)
        {
            error.WriteLine("No scenarios registered.");
            return 2;
        }

        string? name = null;
        int ticks = 30;
        int every = 1;
        int[] workers = [1];
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--list", StringComparison.Ordinal))
            {
                foreach (var scenario in scenarios)
                {
                    output.WriteLine(scenario.Name);
                }

                return 0;
            }

            if (i + 1 >= args.Length)
            {
                error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Option {args[i]} needs a value."));
                return 2;
            }

            string value = args[i + 1];
            switch (args[i])
            {
                case "--scenario":
                    name = value;
                    break;
                case "--ticks":
                    ticks = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--every":
                    every = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--workers":
                    workers = ParseList(value);
                    break;
                default:
                    error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Unknown option {args[i]}."));
                    return 2;
            }

            i++;
        }

        var selected = name is null ? scenarios[0] : Find(scenarios, name);
        if (selected is null)
        {
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"No scenario named '{name}'. Use --list to see the registered names."));
            return 2;
        }

        var result = DeterminismHarness.Run(selected, new DeterminismOptions { Ticks = ticks, CheckpointEvery = every, WorkerCounts = workers });
        Write(result, output);
        if (!result.Passed)
        {
            error.Write(result.Describe());
            return 1;
        }

        return 0;
    }

    /// <summary>Writes the host output format: one line per run, <c>&lt;workers&gt; &lt;hash&gt;...</c>.</summary>
    public static void Write(DeterminismResult result, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);
        foreach (var run in result.Runs)
        {
            output.Write(run.Workers.ToString(CultureInfo.InvariantCulture));
            var hashes = run.Hashes.Span;
            for (int i = 0; i < run.CompletedCheckpoints; i++)
            {
                output.Write(' ');
                output.Write(hashes[i].ToString("x16", CultureInfo.InvariantCulture));
            }

            output.WriteLine();
        }
    }

    /// <summary>Parses host output back into a result (no faults or leaks are representable: a failed host run exits non-zero instead).</summary>
    public static DeterminismResult Parse(string output, string scenarioName, ulong seed, int ticks, int checkpointEvery)
    {
        ArgumentNullException.ThrowIfNull(output);
        var options = new DeterminismOptions { Ticks = ticks, CheckpointEvery = checkpointEvery };
        options.Validate();
        var checkpoints = options.ComputeCheckpoints();
        var runs = new List<DeterminismRun>();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int workers = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var hashes = new ulong[checkpoints.Length];
            int count = Math.Min(parts.Length - 1, hashes.Length);
            for (int i = 0; i < count; i++)
            {
                hashes[i] = ulong.Parse(parts[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            runs.Add(new DeterminismRun(workers, hashes, count, null, null, default));
        }

        return new DeterminismResult(scenarioName, seed, checkpoints, runs);
    }

    private static Scenario? Find(IReadOnlyList<Scenario> scenarios, string name)
    {
        foreach (var scenario in scenarios)
        {
            if (string.Equals(scenario.Name, name, StringComparison.Ordinal))
            {
                return scenario;
            }
        }

        return null;
    }

    private static int[] ParseList(string text)
    {
        var parts = text.Split(',');
        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            values[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }

        return values;
    }
}
