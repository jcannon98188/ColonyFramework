namespace Colony.Diagnostics;

/// <summary>
/// The fixed numeric record of one Tick (spec section 10.1). Times are <see cref="System.Diagnostics.Stopwatch"/>
/// ticks (<see cref="TicksPerSecond"/>), never wall-clock. The layout is versioned (<see cref="LayoutVersion"/>)
/// because trace and protocol sinks depend on it: adding a field bumps the version.
/// </summary>
public record struct TickRecord
{
    /// <summary>Version of the numeric layout of <see cref="TickRecord"/>, <see cref="PhaseRecord"/>, <see cref="SystemRecord"/>, <see cref="WorkerRecord"/> and <see cref="JobRecord"/>.</summary>
    public const int LayoutVersion = 1;

    /// <summary>Stopwatch ticks per second, the unit of every time field.</summary>
    public static long TicksPerSecond => System.Diagnostics.Stopwatch.Frequency;

    public int Version { get; internal set; }

    public DiagnosticsLevel Level { get; internal set; }

    public long Tick { get; internal set; }

    /// <summary>Wall time of the whole Tick.</summary>
    public long WallTicks { get; internal set; }

    /// <summary>Time spent playing command buffers back at Phase boundaries.</summary>
    public long PlaybackTicks { get; internal set; }

    /// <summary>Chunk jobs run.</summary>
    public int Jobs { get; internal set; }

    public int ChunksVisited { get; internal set; }

    /// <summary>Chunks a System skipped because they were asleep or unchanged.</summary>
    public int ChunksSkipped { get; internal set; }

    public long EntitiesTouched { get; internal set; }

    /// <summary>Commands played back at the Phase boundaries.</summary>
    public long Commands { get; internal set; }

    /// <summary>Managed bytes allocated on every Tick thread during the Tick (0 in a correct steady state).</summary>
    public long AllocatedBytes { get; internal set; }

    /// <summary>Tick threads that allocated.</summary>
    public int AllocatingThreads { get; internal set; }

    public int EntityCount { get; internal set; }

    public int ArchetypeCount { get; internal set; }

    public int ChunksInUse { get; internal set; }

    public int PeakChunksInUse { get; internal set; }

    public int Slabs { get; internal set; }

    /// <summary>Native bytes reserved in slabs.</summary>
    public long ReservedBytes { get; internal set; }

    /// <summary><see cref="DiagnosticsOptions.MemoryBudgetBytes"/>, or 0.</summary>
    public long MemoryBudgetBytes { get; internal set; }

    /// <summary>Scratch arena bytes in use just before the end-of-Tick reset (its high-water mark for the Tick).</summary>
    public long ScratchUsedBytes { get; internal set; }

    /// <summary>Scratch arena chunks rented so far.</summary>
    public int ScratchChunks { get; internal set; }

    /// <summary>Log entries dropped from full rings so far.</summary>
    public long LogsDropped { get; internal set; }

    /// <summary>True when the Tick exceeded <see cref="DiagnosticsOptions.TickBudget"/>.</summary>
    public bool Slow { get; internal set; }

    /// <summary>World hash at the end of the Tick when the hash trail is on, else 0.</summary>
    public ulong Hash { get; internal set; }

    /// <summary>Jobs that did not fit the Detailed job trace.</summary>
    public int JobsNotTraced { get; internal set; }
}

/// <summary>One Phase of one Tick.</summary>
public record struct PhaseRecord
{
    public int PhaseId { get; internal set; }

    public long WallTicks { get; internal set; }

    public long PlaybackTicks { get; internal set; }

    public int Waves { get; internal set; }

    public int Jobs { get; internal set; }

    public long Commands { get; internal set; }

    /// <summary>World hash after the Phase boundary when the hash trail is on, else 0.</summary>
    public ulong Hash { get; internal set; }

    /// <summary>Archetypes whose per-archetype hash the trail recorded for this Phase.</summary>
    public int ArchetypesHashed { get; internal set; }
}

/// <summary>One System of one Tick, summed over every worker that ran its jobs.</summary>
public record struct SystemRecord
{
    public int SystemId { get; internal set; }

    /// <summary>False when the System's rate (<see cref="SystemDefinition.Every"/>) skipped this Tick.</summary>
    public bool Ran { get; internal set; }

    /// <summary>First job start to last job end across workers, plus the serial Begin/End hooks.</summary>
    public long WallTicks { get; internal set; }

    /// <summary>Sum of job times over every worker, plus the serial hooks.</summary>
    public long CpuTicks { get; internal set; }

    public int Jobs { get; internal set; }

    public int ChunksVisited { get; internal set; }

    public int ChunksSkipped { get; internal set; }

    public long EntitiesTouched { get; internal set; }

    /// <summary>Commands this System's buffers recorded.</summary>
    public long Commands { get; internal set; }

    /// <summary>Managed bytes allocated while this System ran, on any worker.</summary>
    public long AllocatedBytes { get; internal set; }

    /// <summary>World hash after this System when the hash trail is on, else 0.</summary>
    public ulong Hash { get; internal set; }
}

/// <summary>One Tick thread (worker 0 is the thread that called <see cref="World.Tick"/>) over one Tick.</summary>
public record struct WorkerRecord
{
    public int Worker { get; internal set; }

    /// <summary>Time running jobs.</summary>
    public long BusyTicks { get; internal set; }

    /// <summary>Time spinning for work.</summary>
    public long SpinTicks { get; internal set; }

    /// <summary>Time parked on the kernel event.</summary>
    public long ParkTicks { get; internal set; }

    public int Jobs { get; internal set; }

    public long AllocatedBytes { get; internal set; }

    /// <summary>The System running when this worker first allocated; -1 when it did not allocate or allocated outside Systems.</summary>
    public int FirstAllocatingSystem { get; internal set; }
}

/// <summary>One chunk job, recorded only at <see cref="DiagnosticsLevel.Detailed"/>.</summary>
public record struct JobRecord
{
    public int SystemId { get; internal set; }

    public int Worker { get; internal set; }

    public int Archetype { get; internal set; }

    public int Chunk { get; internal set; }

    public int JobIndex { get; internal set; }

    /// <summary>Stopwatch timestamp relative to the Tick start.</summary>
    public long StartTicks { get; internal set; }

    public long EndTicks { get; internal set; }
}
