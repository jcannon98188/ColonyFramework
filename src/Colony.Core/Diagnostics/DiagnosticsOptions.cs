namespace Colony.Diagnostics;

/// <summary>
/// Per-World diagnostics settings (spec section 10.1): the cost level, the record ring size, the
/// allocation check, the optional Tick budget, the logging rings, the hash trail and the validation
/// switches that are separate from <see cref="WorldBuilder.DebugValidation"/>. Everything here is fixed
/// when the World is created so the record layout never changes while it Ticks.
/// </summary>
public sealed class DiagnosticsOptions
{
    public static DiagnosticsOptions Default { get; } = new();

    public DiagnosticsLevel Level { get; init; } = DiagnosticsLevel.Standard;

    /// <summary>Tick records kept in the ring (the latest N Ticks readable through <see cref="WorldDiagnostics"/>).</summary>
    public int RecordCapacity { get; init; } = 128;

    /// <summary>
    /// Sample <see cref="GC.GetAllocatedBytesForCurrentThread"/> at every job boundary on every Tick thread and
    /// attribute managed allocations to the System that was running. On at every level; the cost is one
    /// counter read per job.
    /// </summary>
    public bool AllocationCheck { get; init; } = true;

    /// <summary>Optional Tick budget. A Tick whose wall time exceeds it is marked slow and, rate-limited, reported to the <see cref="Listener"/>. Zero disables it.</summary>
    public TimeSpan TickBudget { get; init; }

    /// <summary>Minimum number of Ticks between two slow-Tick reports.</summary>
    public int SlowTickReportInterval { get; init; } = 60;

    /// <summary>How many Systems a slow-Tick report lists, by wall time.</summary>
    public int SlowTickTopSystems { get; init; } = 5;

    /// <summary>Native memory budget the record compares reserved bytes against. Zero means none; it is reporting only.</summary>
    public long MemoryBudgetBytes { get; init; }

    /// <summary>Log entries each Tick thread's ring holds between drains; the oldest entries are overwritten and counted as dropped.</summary>
    public int LogCapacityPerThread { get; init; } = 1024;

    /// <summary>Per-job records kept per Tick at <see cref="DiagnosticsLevel.Detailed"/>; jobs beyond it are counted, not traced.</summary>
    public int JobTraceCapacity { get; init; } = 4096;

    /// <summary>
    /// Hash the World after every System, every Phase and every Tick, plus every archetype at each Phase
    /// boundary, so two runs can be compared down to the first diverging System (spec section 10.1).
    /// Systems of one wave run one at a time while the trail is on; results are unchanged, the cost is high.
    /// </summary>
    public bool HashTrail { get; init; }

    /// <summary>Archetypes whose hashes the trail records per Phase; archetypes beyond it are only in the World hash.</summary>
    public int HashTrailArchetypes { get; init; } = 32;

    /// <summary>
    /// Every N Ticks, hash the World at the end of the Tick and again before the next Tick runs anything; a
    /// mismatch means state changed outside the Tick (host writes through refs, stray native writes) and is
    /// reported to the <see cref="Listener"/>. Zero disables it.
    /// </summary>
    public int DeterminismSpotCheckEvery { get; init; }

    /// <summary>
    /// Throw when a chunk job takes a column its System did not declare in Reads or Writes. Null follows
    /// the schema's debug-validation setting.
    /// </summary>
    public bool? AccessChecking { get; init; }

    /// <summary>Receives Tick records, slow-Tick reports, allocation reports and failed spot-checks, on the Tick thread at the end of the Tick.</summary>
    public IDiagnosticsListener? Listener { get; init; }

    internal int ValidatedRecordCapacity => RecordCapacity < 1 ? 1 : RecordCapacity;
}

/// <summary>
/// Hook through which sinks (Colony.Diagnostics, hosts, tests) observe a World's diagnostics without Core
/// referencing them (spec section 3). Every callback runs on the Tick thread at the end of the Tick, after
/// the record is complete; implementations should copy what they need and return quickly.
/// </summary>
public interface IDiagnosticsListener
{
    /// <summary>The record of the Tick that just finished.</summary>
    public void OnTickRecorded(World world, in TickRecordView record)
    {
    }

    /// <summary>The Tick exceeded <see cref="DiagnosticsOptions.TickBudget"/>; rate-limited by <see cref="DiagnosticsOptions.SlowTickReportInterval"/>.</summary>
    public void OnSlowTick(World world, in SlowTickReport report)
    {
    }

    /// <summary>A Tick thread allocated managed memory during the Tick; one report per thread per Tick naming the first System that allocated.</summary>
    public void OnAllocation(World world, in AllocationReport report)
    {
    }

    /// <summary>The World hash recomputed before this Tick differs from the one taken at the end of the previous Tick.</summary>
    public void OnDeterminismCheckFailed(World world, in DeterminismCheckReport report)
    {
    }
}

/// <summary>A Tick that exceeded its budget, with the Systems that took the most wall time.</summary>
public readonly ref struct SlowTickReport
{
    internal SlowTickReport(long tick, long wallTicks, long budgetTicks, ReadOnlySpan<SystemRecord> topSystems)
    {
        Tick = tick;
        WallTicks = wallTicks;
        BudgetTicks = budgetTicks;
        TopSystems = topSystems;
    }

    public long Tick { get; }

    /// <summary>Wall time of the Tick in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public long WallTicks { get; }

    public long BudgetTicks { get; }

    /// <summary>The slowest Systems first, by wall time.</summary>
    public ReadOnlySpan<SystemRecord> TopSystems { get; }
}

/// <summary>Managed allocation on one Tick thread during one Tick, attributed to the System running when it first happened.</summary>
public readonly record struct AllocationReport(long Tick, int Worker, int SystemId, string? SystemName, long Bytes)
{
    /// <summary>The allocation happened outside any System: during command-buffer playback or the Tick's bookkeeping.</summary>
    public bool OutsideSystems => SystemId < 0;
}

/// <summary>A determinism spot-check that found the World changed between two Ticks.</summary>
public readonly record struct DeterminismCheckReport(long Tick, ulong ExpectedHash, ulong ActualHash);
