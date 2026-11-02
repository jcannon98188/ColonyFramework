namespace Colony;

/// <summary>
/// Lets the host hold a World's Ticks (spec section 4.3): paused for inspection, stepping one Tick at a
/// time, or waiting for lockstep input. One gate may be shared by several Worlds. Thread-safe and
/// allocation-free; an inspector may hold or step it from another thread. <see cref="World.Tick"/>
/// returns false without running when the gate is held and no step is pending.
/// </summary>
public sealed class TickGate
{
    private int _held;
    private int _steps;

    /// <summary>True while Ticks are held (steps may still be pending).</summary>
    public bool IsHeld => Volatile.Read(ref _held) != 0;

    /// <summary>Ticks a held gate will still let through.</summary>
    public int PendingSteps => Volatile.Read(ref _steps);

    public void Hold() => Volatile.Write(ref _held, 1);

    public void Release()
    {
        Volatile.Write(ref _held, 0);
        Volatile.Write(ref _steps, 0);
    }

    /// <summary>Lets <paramref name="count"/> more Ticks through while the gate is held.</summary>
    public void Step(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Interlocked.Add(ref _steps, count);
    }

    /// <summary>Called by <see cref="World.Tick"/>: true when a Tick may run now, consuming a pending step if held.</summary>
    public bool TryPass()
    {
        if (!IsHeld)
        {
            return true;
        }

        while (true)
        {
            int steps = Volatile.Read(ref _steps);
            if (steps <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _steps, steps - 1, steps) == steps)
            {
                return true;
            }
        }
    }
}
