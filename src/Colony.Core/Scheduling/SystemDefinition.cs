namespace Colony;

/// <summary>Serial System hook: runs on the Tick thread before (<see cref="SystemDefinition.Begin"/>) or after (<see cref="SystemDefinition.End"/>) the System's chunk jobs.</summary>
public delegate void SystemAction(in SystemContext context);

/// <summary>Chunk-parallel System body: runs once per matching chunk, possibly on a worker thread, writing only the System's declared columns of that chunk.</summary>
public delegate void ChunkSystemAction(in SystemContext context, in ChunkView chunk);

/// <summary>
/// Everything the scheduler needs to know about one System (spec section 4.3), as plain data: the query,
/// the declared Component access set, the Phase, the rate and the ordering constraints, plus the entry
/// points. Hand-written here; the source generator (spec section 11) emits the same shape from attributes.
/// Register with <see cref="WorldBuilder.AddSystem"/>; the System id is its registration order.
/// </summary>
public sealed class SystemDefinition
{
    /// <summary>Unique within a schema; After/Before constraints name Systems by it.</summary>
    public required string Name { get; init; }

    public Phase Phase { get; init; } = Phase.Simulate;

    /// <summary>Components a chunk must have to be visited. Empty means "every Component the System reads or writes".</summary>
    public ComponentMask All { get; init; }

    /// <summary>Components a chunk must not have.</summary>
    public ComponentMask None { get; init; }

    /// <summary>Components the System reads. Two Systems conflict when one writes what the other reads or writes.</summary>
    public ComponentMask Reads { get; init; }

    /// <summary>Components the System writes; a chunk job may write only these columns of its own chunk.</summary>
    public ComponentMask Writes { get; init; }

    /// <summary>Run every N Ticks (1 = every Tick).</summary>
    public int Every { get; init; } = 1;

    /// <summary>Which Tick modulo <see cref="Every"/> the System runs on; -1 derives it from the System id.</summary>
    public int Offset { get; init; } = -1;

    /// <summary>Names of Systems in the same Phase that must run before this one.</summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>Names of Systems in the same Phase that must run after this one.</summary>
    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>
    /// When not empty, a chunk is visited only if one of these columns changed (was written by a System or
    /// structurally altered) since this System last ran.
    /// </summary>
    public ComponentMask ChangeFilter { get; init; }

    /// <summary>Skip chunks whose asleep bit is set (<see cref="ChunkView.SetAsleep"/>).</summary>
    public bool SkipAsleepChunks { get; init; }

    /// <summary>Serial hook before the chunk jobs (job index -1). Use it to prepare per-job reduction slots.</summary>
    public SystemAction? Begin { get; init; }

    /// <summary>Chunk-parallel body; null makes a serial System with only <see cref="Begin"/> and <see cref="End"/>.</summary>
    public ChunkSystemAction? ForEachChunk { get; init; }

    /// <summary>Serial hook after every chunk job finished. Combine per-job partial results here, in job order.</summary>
    public SystemAction? End { get; init; }
}
