using System.Globalization;
using System.Text;

namespace Colony.Testing;

/// <summary>One run of a scenario on a fixed worker count: the World hash at every checkpoint, or how it stopped.</summary>
public sealed class DeterminismRun
{
    internal DeterminismRun(int workers, ulong[] hashes, int completed, WorldFault? fault, Exception? error, LeakReport leak)
    {
        Workers = workers;
        Hashes = hashes;
        CompletedCheckpoints = completed;
        Fault = fault;
        Error = error;
        Leak = leak;
    }

    public int Workers { get; }

    /// <summary>Hashes by checkpoint index; entries past <see cref="CompletedCheckpoints"/> are 0 when the run stopped early.</summary>
    public ReadOnlyMemory<ulong> Hashes { get; }

    public int CompletedCheckpoints { get; }

    /// <summary>Set when a System threw: the World faulted and the run stopped.</summary>
    public WorldFault? Fault { get; }

    /// <summary>The exception that stopped the run (the <see cref="SystemFaultException"/> for a fault, or whatever Populate/Step threw).</summary>
    public Exception? Error { get; }

    /// <summary>Native memory still held when the World was disposed (debug validation only).</summary>
    public LeakReport Leak { get; }

    public bool Completed => Error is null && CompletedCheckpoints == Hashes.Length;
}

/// <summary>What <see cref="DeterminismHarness"/> found: per-worker-count hash sequences, their first divergence, faults and leaks.</summary>
public sealed class DeterminismResult
{
    private readonly int[] _checkpoints;

    internal DeterminismResult(string scenarioName, ulong seed, int[] checkpoints, IReadOnlyList<DeterminismRun> runs)
    {
        ScenarioName = scenarioName;
        Seed = seed;
        _checkpoints = checkpoints;
        Runs = runs;
        FirstDivergingTick = FindFirstDivergence(checkpoints, runs);
    }

    public string ScenarioName { get; }

    public ulong Seed { get; }

    /// <summary>The Ticks (1-based) after which the World was hashed.</summary>
    public ReadOnlySpan<int> Checkpoints => _checkpoints;

    public IReadOnlyList<DeterminismRun> Runs { get; }

    /// <summary>The first checkpoint Tick at which any run's hash differs from the baseline run (the first worker count), or null.</summary>
    public long? FirstDivergingTick { get; }

    /// <summary>Every run completed, none faulted or leaked, and all hash sequences are identical.</summary>
    public bool Passed => FirstDivergingTick is null && Runs.All(static r => r.Completed && !r.Leak.HasLeaks);

    /// <summary>The baseline run's hash sequence (only meaningful when <see cref="Passed"/>).</summary>
    public ReadOnlyMemory<ulong> Hashes => Runs[0].Hashes;

    /// <summary>Reports the first Tick at which this result's baseline hashes differ from <paramref name="other"/>'s, or null when identical over the shared checkpoints.</summary>
    public long? FirstDivergenceFrom(DeterminismResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var mine = Hashes.Span;
        var theirs = other.Hashes.Span;
        int n = Math.Min(mine.Length, theirs.Length);
        for (int i = 0; i < n; i++)
        {
            if (mine[i] != theirs[i])
            {
                return _checkpoints[i];
            }
        }

        return mine.Length == theirs.Length ? null : _checkpoints[Math.Min(n, _checkpoints.Length - 1)];
    }

    public void ThrowIfFailed()
    {
        if (!Passed)
        {
            throw new HarnessException(Describe());
        }
    }

    /// <summary>A human-readable report: verdict, first diverging Tick, each run's status and the hashes around the divergence.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Scenario '{ScenarioName}' seed {Seed}: {(Passed ? "PASSED" : "FAILED")}");
        text.Append(CultureInfo.InvariantCulture, $" ({_checkpoints.Length} checkpoints over {(_checkpoints.Length == 0 ? 0 : _checkpoints[^1])} Ticks, workers {string.Join('/', Runs.Select(static r => r.Workers))}).");
        text.AppendLine();
        if (FirstDivergingTick is { } tick)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  First diverging Tick {tick}:");
            int index = Array.IndexOf(_checkpoints, (int)tick);
            foreach (var run in Runs)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    {run.Workers,3} workers: {FormatHash(run, index)}");
            }
        }

        foreach (var run in Runs)
        {
            if (run.Fault is { } fault)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {run.Workers,3} workers: System '{fault.SystemName}' threw during Tick {fault.Tick} (0-based, after {run.CompletedCheckpoints} checkpoints) in Phase '{fault.Phase.Name}': {fault.Exception.GetType().Name}: {fault.Exception.Message}");
            }
            else if (run.Error is { } error)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {run.Workers,3} workers: stopped after {run.CompletedCheckpoints} checkpoints: {error.GetType().Name}: {error.Message}");
            }

            if (run.Leak.HasLeaks)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {run.Workers,3} workers: leaked {run.Leak.LeakedChunks} chunks ({run.Leak.LeakedBytes} bytes) at dispose");
            }
        }

        if (Passed && _checkpoints.Length > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Final hash {Hashes.Span[^1]:x16}");
        }

        return text.ToString();
    }

    private static string FormatHash(DeterminismRun run, int index)
        => index < run.CompletedCheckpoints ? run.Hashes.Span[index].ToString("x16", CultureInfo.InvariantCulture) : "(not reached)";

    private static long? FindFirstDivergence(int[] checkpoints, IReadOnlyList<DeterminismRun> runs)
    {
        if (runs.Count == 0)
        {
            return null;
        }

        var baseline = runs[0];
        for (int i = 0; i < checkpoints.Length; i++)
        {
            for (int r = 1; r < runs.Count; r++)
            {
                var run = runs[r];
                bool baselineHas = i < baseline.CompletedCheckpoints;
                bool runHas = i < run.CompletedCheckpoints;
                if (baselineHas != runHas || (baselineHas && baseline.Hashes.Span[i] != run.Hashes.Span[i]))
                {
                    return checkpoints[i];
                }
            }
        }

        return null;
    }
}
