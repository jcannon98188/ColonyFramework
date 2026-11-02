namespace Colony.Persistence;

/// <summary>
/// Values keyed by Tick over a sliding window: frames and checkpoint hashes a lockstep peer holds. Ticks below
/// <see cref="Floor"/> are forgotten (<see cref="Trim"/>); storage grows as needed and allocates nothing once grown.
/// </summary>
internal sealed class TickTable<T>
{
    private T[] _items;
    private bool[] _present;
    private int _start;
    private int _count;

    public TickTable(long floor, int capacity = 64)
    {
        Floor = floor;
        _items = new T[capacity];
        _present = new bool[capacity];
    }

    /// <summary>Ticks below it are not held and cannot be set.</summary>
    public long Floor { get; private set; }

    /// <summary>The highest Tick ever set, or <see cref="Floor"/> - 1.</summary>
    public long Highest { get; private set; } = long.MinValue;

    public bool Contains(long tick)
    {
        long offset = tick - Floor;
        return offset >= 0 && offset < _count && _present[Index(offset)];
    }

    public bool TryGet(long tick, out T value)
    {
        long offset = tick - Floor;
        if (offset >= 0 && offset < _count && _present[Index(offset)])
        {
            value = _items[Index(offset)];
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Stores <paramref name="value"/> at <paramref name="tick"/>; false (nothing stored) below the floor.</summary>
    public bool Set(long tick, T value)
    {
        long offset = tick - Floor;
        if (offset < 0)
        {
            return false;
        }

        if (offset >= int.MaxValue / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"Tick {tick} is too far above the table floor {Floor}.");
        }

        Ensure((int)offset + 1);
        int index = Index(offset);
        _items[index] = value;
        _present[index] = true;
        if (tick > Highest || Highest == long.MinValue)
        {
            Highest = tick;
        }

        return true;
    }

    /// <summary>Forgets every Tick below <paramref name="below"/> (no-op when below the current floor).</summary>
    public void Trim(long below)
    {
        if (below <= Floor)
        {
            return;
        }

        long drop = below - Floor;
        if (drop >= _count)
        {
            for (int i = 0; i < _count; i++)
            {
                int index = Index(i);
                _items[index] = default!;
                _present[index] = false;
            }

            _count = 0;
            _start = 0;
        }
        else
        {
            for (long i = 0; i < drop; i++)
            {
                int index = Index(i);
                _items[index] = default!;
                _present[index] = false;
            }

            _start = Index(drop);
            _count -= (int)drop;
        }

        Floor = below;
    }

    private int Index(long offset) => (int)((_start + offset) % _items.Length);

    private void Ensure(int count)
    {
        if (count <= _items.Length)
        {
            if (count > _count)
            {
                _count = count;
            }

            return;
        }

        int capacity = _items.Length;
        while (capacity < count)
        {
            capacity *= 2;
        }

        var items = new T[capacity];
        var present = new bool[capacity];
        for (int i = 0; i < _count; i++)
        {
            int index = Index(i);
            items[i] = _items[index];
            present[i] = _present[index];
        }

        _items = items;
        _present = present;
        _start = 0;
        _count = count;
    }
}
