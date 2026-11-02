namespace Colony.Diagnostics.Tests;

/// <summary>
/// Tests that attach an EventListener to the process-wide <c>Colony-Diagnostics</c> EventSource, or that
/// measure host-thread allocation while a <see cref="MeterBridge"/> publishes, run one at a time: an enabled
/// EventListener makes the runtime allocate on the publishing thread, which would fail a zero-alloc gate
/// running in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EventSourceTestGroup
{
    public const string Name = "Colony-Diagnostics EventSource";
}
