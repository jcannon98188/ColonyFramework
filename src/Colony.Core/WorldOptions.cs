namespace Colony;

/// <summary>Per-World settings that are not part of the schema.</summary>
public sealed class WorldOptions
{
    public static WorldOptions Default { get; } = new();

    /// <summary>Entity index slots reserved up front, so a World sized for its load allocates nothing later.</summary>
    public int InitialEntityCapacity { get; init; } = 1024;

    /// <summary>The World seed: root of every <see cref="Colony.Numerics.RandomSource"/> a System draws from (spec section 6).</summary>
    public ulong Seed { get; init; }

    /// <summary>
    /// Threads that run a Tick, including the one that calls <see cref="World.Tick"/>; 0 means one per
    /// physical core (spec section 4.3). Results never depend on this number.
    /// </summary>
    public int WorkerThreads { get; init; }

    /// <summary>When set, <see cref="World.Tick"/> runs only while the gate lets it (host pause, inspector step, lockstep waiting).</summary>
    public TickGate? TickGate { get; init; }

    /// <summary>
    /// Receives the leak report when a debug-validated World is disposed with native memory still in
    /// use. When null, a leak throws <see cref="InvalidOperationException"/> from <see cref="World.Dispose"/>.
    /// </summary>
    public IDebugListener? DebugListener { get; init; }
}

/// <summary>Sink for debug-validation findings (spec section 4.2). Release builds never call it.</summary>
public interface IDebugListener
{
    public void OnLeak(in LeakReport report);
}

/// <summary>Native memory still held when a World was disposed.</summary>
public readonly record struct LeakReport(int LeakedChunks, long LeakedBytes)
{
    public bool HasLeaks => LeakedChunks > 0;
}

/// <summary>Running totals of structural changes applied by command-buffer playback.</summary>
public readonly record struct StructuralCounters(long Creates, long Destroys, long Adds, long Removes, long StaleSkipped)
{
    public long Total => Creates + Destroys + Adds + Removes;
}
