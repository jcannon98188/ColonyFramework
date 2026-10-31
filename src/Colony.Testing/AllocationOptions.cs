namespace Colony.Testing;

/// <summary>How <see cref="AllocationHarness"/> runs a scenario.</summary>
public sealed class AllocationOptions
{
    /// <summary>Threads the Tick runs on, including the caller; use several so chunk jobs land on worker threads.</summary>
    public int Workers { get; init; } = 4;

    /// <summary>Ticks run before measuring: growth of indexes, buffers and reduction slots must finish here.</summary>
    public int WarmupTicks { get; init; } = 20;

    /// <summary>Ticks measured; every byte allocated by the Tick thread or a worker during them counts.</summary>
    public int MeasuredTicks { get; init; } = 10;

    /// <summary>Also hash the World after every measured Tick (<see cref="World.Hash"/> is part of the steady-state contract).</summary>
    public bool HashEveryTick { get; init; } = true;

    /// <summary>Bytes tolerated over all measured Ticks before the scenario fails. The Framework's own scenarios use 0.</summary>
    public long ToleranceBytes { get; init; }

    /// <summary>Overrides the scenario's <see cref="WorldBuilder.DebugValidation"/> setting when set.</summary>
    public bool? DebugValidation { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Workers, 1, nameof(Workers));
        ArgumentOutOfRangeException.ThrowIfNegative(WarmupTicks, nameof(WarmupTicks));
        ArgumentOutOfRangeException.ThrowIfLessThan(MeasuredTicks, 1, nameof(MeasuredTicks));
        ArgumentOutOfRangeException.ThrowIfNegative(ToleranceBytes, nameof(ToleranceBytes));
    }
}
