namespace Colony;

/// <summary>
/// Host-side helper that turns elapsed wall time into a number of Ticks to run (spec section 4.3): a fixed
/// Tick length, an accumulator, pause, a speed multiplier and a catch-up cap that drops backlog instead of
/// spiralling. The host measures time and calls <see cref="Advance"/>; wall-clock time never reaches the
/// World, which only ever sees <see cref="World.Tick"/> calls.
/// </summary>
public sealed class TickClock
{
    private double _speed = 1;

    public TickClock(double tickSeconds, int maxCatchUpTicks = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCatchUpTicks);
        TickSeconds = tickSeconds;
        MaxCatchUpTicks = maxCatchUpTicks;
    }

    /// <summary>Simulated seconds per Tick.</summary>
    public double TickSeconds { get; }

    /// <summary>Most Ticks one <see cref="Advance"/> returns; a larger backlog is dropped.</summary>
    public int MaxCatchUpTicks { get; }

    /// <summary>While paused, elapsed time is ignored and nothing accumulates.</summary>
    public bool Paused { get; set; }

    /// <summary>Simulated seconds per real second; 0 freezes without pausing.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _speed = value;
        }
    }

    /// <summary>Simulated time waiting to become a Tick, in [0, <see cref="TickSeconds"/>).</summary>
    public double Accumulator { get; private set; }

    /// <summary>Fraction of the next Tick already elapsed; for render interpolation.</summary>
    public double Alpha => Accumulator / TickSeconds;

    /// <summary>Adds elapsed real seconds and returns how many Ticks the host should run now.</summary>
    public int Advance(double elapsedSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedSeconds);
        if (Paused)
        {
            return 0;
        }

        Accumulator += elapsedSeconds * _speed;
        int ticks = (int)Math.Min(Accumulator / TickSeconds, int.MaxValue);
        if (ticks > MaxCatchUpTicks)
        {
            Accumulator = 0;
            return MaxCatchUpTicks;
        }

        Accumulator -= ticks * TickSeconds;
        return ticks;
    }

    /// <summary>Drops accumulated time.</summary>
    public void Reset() => Accumulator = 0;
}
