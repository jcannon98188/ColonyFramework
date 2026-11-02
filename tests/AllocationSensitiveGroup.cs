namespace Colony.TestSupport;

/// <summary>
/// Tests that gate allocation (<c>GC.GetAllocatedBytesForCurrentThread</c>, the allocation harness, the
/// diagnostics allocation check) or a timing ratio run one at a time, after the parallel tests finish. Another
/// test's garbage collection perturbs the per-thread counter (a "clean" Tick showed 8040 bytes under load on
/// Linux), and CPU contention distorts the timing gate. Separate from the EventSource group, which has its own
/// reason (an enabled EventListener makes the publishing thread allocate).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationSensitiveGroup
{
    public const string Name = "Allocation- and timing-sensitive";
}
