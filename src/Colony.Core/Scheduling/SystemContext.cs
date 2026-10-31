using Colony.Numerics;

namespace Colony;

/// <summary>
/// What a System body sees while it runs: the World (read it; write only your declared columns), the
/// command buffer for this job (merged at the Phase boundary in System order, then job order, then
/// record order), the Tick, the job index (the chunk ordinal in archetype then chunk order, never the
/// worker thread) and a <see cref="RandomSource"/> derived from the World seed, the System and the Tick.
/// Nothing here depends on which thread runs the job.
/// </summary>
public readonly ref struct SystemContext
{
    internal SystemContext(World world, CommandBuffer commands, long tick, int systemId, int jobIndex, int jobCount, RandomSource random)
    {
        World = world;
        Commands = commands;
        Tick = tick;
        SystemId = systemId;
        JobIndex = jobIndex;
        JobCount = jobCount;
        Random = random;
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
}
