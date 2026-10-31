using Colony.Diagnostics;
using Colony.Numerics;

namespace Colony;

/// <summary>
/// What a System body sees while it runs: the World (read it; write only your declared columns), the
/// command buffer for this job (merged at the Phase boundary in System order, then job order, then
/// record order), the Tick, the job index (the chunk ordinal in archetype then chunk order, never the
/// worker thread) and a <see cref="RandomSource"/> derived from the World seed, the System and the Tick.
/// Nothing here depends on which thread runs the job; <see cref="Log"/> and <see cref="Count"/> write to
/// the running thread's diagnostics slot, which the Tick record merges in a thread-independent order.
/// </summary>
public readonly ref struct SystemContext
{
    private readonly SystemDefinition _definition;
    private readonly WorkerSlot _slot;

    internal SystemContext(World world, SystemDefinition definition, CommandBuffer commands, long tick, int systemId, int jobIndex, int jobCount, RandomSource random, WorkerSlot slot)
    {
        World = world;
        _definition = definition;
        Commands = commands;
        Tick = tick;
        SystemId = systemId;
        JobIndex = jobIndex;
        JobCount = jobCount;
        Random = random;
        _slot = slot;
    }

    /// <summary>
    /// A value Resource this System declared in <see cref="SystemDefinition.ReadsResources"/> or
    /// <see cref="SystemDefinition.WritesResources"/>. Reading an undeclared Resource throws, because the
    /// scheduler could then run this System concurrently with its writer.
    /// </summary>
    public ref readonly T ReadResource<T>(ResourceType type) where T : unmanaged
    {
        if (!_definition.ReadsResources.Contains(type) && !_definition.WritesResources.Contains(type))
        {
            throw new InvalidOperationException($"System '{_definition.Name}' reads {World.Schema.DescribeResource(type).Name} without declaring it in ReadsResources.");
        }

        return ref World.Resource<T>(type);
    }

    /// <summary>
    /// A value Resource this System declared in <see cref="SystemDefinition.WritesResources"/>, writable.
    /// Write it from <see cref="SystemDefinition.Begin"/> or <see cref="SystemDefinition.End"/>, never from a chunk job (jobs run concurrently).
    /// </summary>
    public ref T Resource<T>(ResourceType type) where T : unmanaged
    {
        if (!_definition.WritesResources.Contains(type))
        {
            throw new InvalidOperationException($"System '{_definition.Name}' writes {World.Schema.DescribeResource(type).Name} without declaring it in WritesResources.");
        }

        return ref World.Resource<T>(type);
    }

    /// <summary>
    /// Records an event on a queue this System declared in <see cref="SystemDefinition.WritesResources"/>.
    /// Goes through this job's command buffer, so events from parallel chunk jobs merge in System, job then
    /// record order at the Phase boundary and are readable from the next Phase. Safe from any hook or job.
    /// </summary>
    public void Emit<T>(ResourceType queue, in T value) where T : unmanaged
    {
        if (!_definition.WritesResources.Contains(queue))
        {
            throw new InvalidOperationException($"System '{_definition.Name}' emits {World.Schema.DescribeResource(queue).Name} without declaring it in WritesResources.");
        }

        Commands.Emit(queue, in value);
    }

    /// <summary>The pending events of a queue this System declared in <see cref="SystemDefinition.ReadsResources"/> (or writes), as merged at the last Phase boundary.</summary>
    public ReadOnlySpan<T> Events<T>(ResourceType queue) where T : unmanaged
    {
        if (!_definition.ReadsResources.Contains(queue) && !_definition.WritesResources.Contains(queue))
        {
            throw new InvalidOperationException($"System '{_definition.Name}' reads {World.Schema.DescribeResource(queue).Name} without declaring it in ReadsResources.");
        }

        return World.Events<T>(queue);
    }

    public World World { get; }

    /// <summary>This job's command buffer. Serial hooks share job 0's buffer.</summary>
    public CommandBuffer Commands { get; }

    public long Tick { get; }

    public int SystemId { get; }

    /// <summary>Chunk ordinal of this job (stable across thread counts), or -1 in <see cref="SystemDefinition.Begin"/> and <see cref="SystemDefinition.End"/>.</summary>
    public int JobIndex { get; }

    /// <summary>Number of chunk jobs this System runs this Tick. Size per-job reduction slots to it in <see cref="SystemDefinition.Begin"/>.</summary>
    public int JobCount { get; }

    /// <summary>Random stream for this System and Tick; address it by Entity index, job or counter, never by thread.</summary>
    public RandomSource Random { get; }

    /// <summary>
    /// Structured, allocation-free logging (spec section 10.1): a message registered with
    /// <see cref="WorldBuilder.AddLogMessage"/> plus up to four numeric arguments, stamped with this Tick,
    /// System and job. Text is resolved only when the host drains <see cref="WorldDiagnostics.DrainLogs"/>.
    /// Logging never changes simulation state.
    /// </summary>
    public void Log(LogMessageId message, long arg0 = 0, long arg1 = 0, long arg2 = 0, long arg3 = 0)
        => _slot.Log.Write(new LogEntry(Tick, SystemId, JobIndex, message, arg0, arg1, arg2, arg3));

    /// <summary>Adds to a counter registered with <see cref="WorldBuilder.AddCounter"/>; the Tick record holds the sum over every thread.</summary>
    public void Count(CounterId counter, long delta) => _slot.Counters[counter.Id] += delta;
}
