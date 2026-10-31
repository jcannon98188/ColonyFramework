namespace Colony.Testing;

/// <summary>How <see cref="DeterminismHarness"/> runs a scenario.</summary>
public sealed class DeterminismOptions
{
    private static IReadOnlyList<int>? _defaultWorkerCounts;

    /// <summary>Ticks to run; the last one is always a checkpoint.</summary>
    public int Ticks { get; init; } = 30;

    /// <summary>Hash the World every N Ticks (1 = every Tick, the finest divergence report).</summary>
    public int CheckpointEvery { get; init; } = 1;

    /// <summary>Worker counts to run and compare, in order; the first is the baseline.</summary>
    public IReadOnlyList<int> WorkerCounts { get; init; } = DefaultWorkerCounts;

    /// <summary>Overrides the scenario's <see cref="WorldBuilder.DebugValidation"/> setting when set.</summary>
    public bool? DebugValidation { get; init; }

    /// <summary>1, 2, 4 and N, where N is the machine's logical processor count or 4, whichever is larger (spec section 10.3).</summary>
    public static IReadOnlyList<int> DefaultWorkerCounts
        => _defaultWorkerCounts ??= Array.AsReadOnly<int>([1, 2, 4, Math.Max(4, Environment.ProcessorCount)]);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Ticks, 1, nameof(Ticks));
        ArgumentOutOfRangeException.ThrowIfLessThan(CheckpointEvery, 1, nameof(CheckpointEvery));
        if (WorkerCounts.Count == 0)
        {
            throw new ArgumentException("At least one worker count is required.", nameof(WorkerCounts));
        }

        foreach (int workers in WorkerCounts)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1, nameof(WorkerCounts));
        }
    }

    internal int[] ComputeCheckpoints()
    {
        int count = Ticks / CheckpointEvery;
        bool finalExtra = Ticks % CheckpointEvery != 0;
        var checkpoints = new int[count + (finalExtra ? 1 : 0)];
        for (int i = 0; i < count; i++)
        {
            checkpoints[i] = (i + 1) * CheckpointEvery;
        }

        if (finalExtra)
        {
            checkpoints[^1] = Ticks;
        }

        return checkpoints;
    }
}
