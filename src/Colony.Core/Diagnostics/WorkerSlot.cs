namespace Colony.Diagnostics;

/// <summary>
/// The per-worker accumulation slots of the scheduler's instrumentation (spec section 10.1: one timestamp
/// per System per worker, accumulated in thread-local slots). Written only by the worker that owns the
/// slot while a wave runs; folded into the Tick record by the Tick thread after the wave, when the pool's
/// completion barrier has published every write. Also home of the worker's log ring and counters.
/// </summary>
internal sealed class WorkerSlot
{
    public const int NoAllocation = -2;
    public const int AllocatedOutsideSystems = -1;

    public WorkerSlot(int worker, int systemCount, int counterCount, int logCapacity)
    {
        Worker = worker;
        Cpu = new long[systemCount];
        FirstStart = new long[systemCount];
        LastEnd = new long[systemCount];
        Jobs = new int[systemCount];
        Entities = new long[systemCount];
        Allocated = new long[systemCount];
        Counters = new long[counterCount];
        Log = new LogRing(logCapacity);
        Array.Fill(FirstStart, long.MaxValue);
        FirstAllocatingSystem = NoAllocation;
    }

    public int Worker { get; }

    /// <summary>Summed job time per System id.</summary>
    public long[] Cpu { get; }

    /// <summary>Earliest job start per System id (long.MaxValue when none ran here).</summary>
    public long[] FirstStart { get; }

    /// <summary>Latest job end per System id.</summary>
    public long[] LastEnd { get; }

    public int[] Jobs { get; }

    public long[] Entities { get; }

    /// <summary>Managed bytes allocated while each System ran on this worker.</summary>
    public long[] Allocated { get; }

    public long[] Counters { get; }

    public LogRing Log { get; }

    /// <summary>Managed bytes this worker allocated during the Tick inside job and hook boundaries.</summary>
    public long AllocatedBytes;

    public int FirstAllocatingSystem;

    /// <summary>Time worker 0 spent in serial hooks (added to its busy time).</summary>
    public long SerialBusy;

    public int JobsRun;

    public void RecordJob(int system, long start, long end, int rows, long allocated)
    {
        Cpu[system] += end - start;
        if (start < FirstStart[system])
        {
            FirstStart[system] = start;
        }

        if (end > LastEnd[system])
        {
            LastEnd[system] = end;
        }

        Jobs[system]++;
        Entities[system] += rows;
        JobsRun++;
        if (allocated > 0)
        {
            RecordAllocation(system, allocated);
        }
    }

    public void RecordAllocation(int system, long bytes)
    {
        if (system >= 0)
        {
            Allocated[system] += bytes;
        }

        AllocatedBytes += bytes;
        if (FirstAllocatingSystem == NoAllocation)
        {
            FirstAllocatingSystem = system;
        }
    }

    /// <summary>Clears one System's slots after the Tick thread folded them into the record.</summary>
    public void ResetSystem(int system)
    {
        Cpu[system] = 0;
        FirstStart[system] = long.MaxValue;
        LastEnd[system] = 0;
        Jobs[system] = 0;
        Entities[system] = 0;
        Allocated[system] = 0;
    }

    public void ResetTick()
    {
        AllocatedBytes = 0;
        FirstAllocatingSystem = NoAllocation;
        SerialBusy = 0;
        JobsRun = 0;
        Array.Clear(Counters);
    }
}
