namespace Colony.Benchmarks.TickHarness;

/// <summary>
/// The hard gates of spec section 10.3: deterministic counts only. Steady-state allocation must be zero on
/// every thread; the save/load round trip must reproduce the World; and against a baseline report the
/// chunks visited and the World hash must be identical. ms/Tick is compared in <see cref="Compare"/> and
/// reported, never gated: shared runners are too noisy for a timing gate.
/// </summary>
public static class Gates
{
    public const string Allocations = "allocations";
    public const string SaveLoad = "save-load";
    public const string ChunksVisited = "chunks-visited";
    public const string WorldHash = "world-hash";

    public static IReadOnlyList<GateReport> Evaluate(RunReport run, RunReport? baseline)
    {
        ArgumentNullException.ThrowIfNull(run);
        var gates = new List<GateReport>
        {
            new(Allocations, run.Counts.AllocatedBytes == 0, 0, run.Counts.AllocatedBytes,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"main thread {run.Counts.MainThreadAllocatedBytes} B, workers {run.Counts.WorkerAllocatedBytes} B over {run.Timing.Samples} Ticks")),
        };

        if (run.SaveLoad is { } saveLoad)
        {
            bool passed = saveLoad.LoadedHashMatches && saveLoad.ContinuedHashMatches;
            gates.Add(new GateReport(SaveLoad, passed, 1, passed ? 1 : 0, passed ? "loaded and continued hashes match" : $"loaded match {saveLoad.LoadedHashMatches}, continued match {saveLoad.ContinuedHashMatches}"));
        }

        if (baseline is not null)
        {
            gates.Add(new GateReport(ChunksVisited, run.Counts.ChunksVisited == baseline.Counts.ChunksVisited, baseline.Counts.ChunksVisited, run.Counts.ChunksVisited));
            gates.Add(new GateReport(
                WorldHash,
                run.Counts.WorldHash == baseline.Counts.WorldHash,
                unchecked((long)baseline.Counts.WorldHash),
                unchecked((long)run.Counts.WorldHash),
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"baseline 0x{baseline.Counts.WorldHash:X16}, actual 0x{run.Counts.WorldHash:X16}")));
        }

        return gates;
    }

    /// <summary>Timing deltas against the baseline run, in percent (positive = slower).</summary>
    public static ComparisonReport Compare(RunReport run, RunReport baseline, HarnessReport baselineReport)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baselineReport);
        return new ComparisonReport(
            baselineReport.Commit,
            baselineReport.Timestamp,
            baseline.Timing.P50Ms,
            baseline.Timing.MeanMs,
            Delta(run.Timing.P50Ms, baseline.Timing.P50Ms),
            Delta(run.Timing.MeanMs, baseline.Timing.MeanMs));
    }

    private static double Delta(double actual, double baseline) => baseline <= 0 ? 0 : (actual - baseline) / baseline * 100.0;
}
