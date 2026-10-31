using System.Text.Json;
using System.Text.Json.Serialization;

namespace Colony.Benchmarks.TickHarness;

/// <summary>
/// The JSON report of one harness invocation: where it ran, which commit, and one <see cref="RunReport"/> per
/// scenario. Reports are compared across runs by scenario name and worker count (<see cref="Gates"/>).
/// </summary>
public sealed record HarnessReport(
    int FormatVersion,
    MachineReport Machine,
    string Commit,
    string Timestamp,
    IReadOnlyList<RunReport> Runs)
{
    public const int CurrentFormatVersion = 1;

    public static HarnessReport ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, HarnessJsonContext.Default.HarnessReport)
            ?? throw new InvalidDataException($"{path} does not contain a harness report.");
    }

    public void WriteFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, HarnessJsonContext.Default.HarnessReport);
    }

    /// <summary>The run of <paramref name="scenario"/> on <paramref name="workers"/> workers, or null.</summary>
    public RunReport? Find(string scenario, int workers)
    {
        foreach (var run in Runs)
        {
            if (string.Equals(run.Scenario.Name, scenario, StringComparison.Ordinal) && run.Scenario.Workers == workers)
            {
                return run;
            }
        }

        return null;
    }
}

public sealed record MachineReport(string Name, string Os, string Architecture, int ProcessorCount, string Runtime, bool Vector256);

/// <param name="Diagnostics">The <c>DiagnosticsLevel</c> the World ran at (Off unless asked; Standard measures the TASK-17 overhead).</param>
/// <param name="Fields">Whether the Field scenario (diffusion and Life stencils) was composed into the World.</param>
public sealed record ScenarioReport(string Name, int Colonists, int Food, int Workers, int Ticks, int Warmup, string Diagnostics, bool Fields);

/// <summary>Wall time per measured Tick in milliseconds.</summary>
public sealed record TimingReport(double MeanMs, double P50Ms, double P90Ms, double P99Ms, double MinMs, double MaxMs, int Samples);

/// <summary>Deterministic counts over the measured Ticks (allocation totals, scheduler counters) and the final World hash.</summary>
public sealed record CountReport(
    long MainThreadAllocatedBytes,
    long WorkerAllocatedBytes,
    long ChunksVisited,
    long ChunksSkippedAsleep,
    long ChunksSkippedByChangeFilter,
    long ChunksSkippedDisabled,
    long Entities,
    ulong WorldHash)
{
    public long AllocatedBytes => MainThreadAllocatedBytes + WorkerAllocatedBytes;
}

/// <param name="LoadedHashMatches">The loaded World hashes like the saved one.</param>
/// <param name="ContinuedHashMatches">One more Tick on both Worlds still agrees (save-load-continue equals never-saved).</param>
public sealed record SaveLoadReport(double SaveMs, double LoadMs, long Bytes, bool LoadedHashMatches, bool ContinuedHashMatches);

/// <summary>One deterministic-count gate. Timing never appears here.</summary>
public sealed record GateReport(string Name, bool Passed, long Expected, long Actual, string? Detail = null)
{
    public override string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Name}: {(Passed ? "ok" : "FAILED")} (expected {Expected}, actual {Actual}){(Detail is null ? string.Empty : "; " + Detail)}");
}

/// <summary>Timing relative to a baseline report: reported, never gated (spec section 10.3).</summary>
public sealed record ComparisonReport(string BaselineCommit, string BaselineTimestamp, double BaselineP50Ms, double BaselineMeanMs, double P50DeltaPercent, double MeanDeltaPercent);

public sealed record RunReport(
    ScenarioReport Scenario,
    TimingReport Timing,
    CountReport Counts,
    SaveLoadReport? SaveLoad,
    IReadOnlyList<GateReport> Gates,
    ComparisonReport? Comparison)
{
    public bool Passed
    {
        get
        {
            foreach (var gate in Gates)
            {
                if (!gate.Passed)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HarnessReport))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext
{
}
