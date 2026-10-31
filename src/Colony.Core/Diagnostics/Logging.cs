namespace Colony.Diagnostics;

/// <summary>
/// A log message registered at startup with <see cref="WorldBuilder.AddLogMessage"/>. Systems log the id
/// plus up to four numeric arguments; the template text is looked up only when the entries are drained.
/// </summary>
public readonly record struct LogMessageId(int Id);

/// <summary>A named counter registered with <see cref="WorldBuilder.AddCounter"/>; Systems add to it through <see cref="SystemContext.Count"/>.</summary>
public readonly record struct CounterId(int Id);

/// <summary>
/// One structured log entry (spec section 10.1): the Tick and System it came from, the job (chunk ordinal,
/// -1 for serial hooks and host logging) and the message id with its fixed numeric arguments. There is no
/// wall-clock time anywhere: entries sort by (Tick, System, job), which is the same on any thread count.
/// </summary>
public readonly record struct LogEntry(long Tick, int SystemId, int JobIndex, LogMessageId Message, long Arg0, long Arg1, long Arg2, long Arg3)
{
    public const int ArgumentCount = 4;

    public long Argument(int index) => index switch
    {
        0 => Arg0,
        1 => Arg1,
        2 => Arg2,
        3 => Arg3,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A log entry carries four arguments."),
    };

    /// <summary>
    /// Position of this entry among those its logging thread wrote. Entries of one job all come from one
    /// thread, so (Tick, System, job, Sequence) is a total order that still never depends on which thread it was.
    /// </summary>
    internal long Sequence { get; init; }

    /// <summary>Orders entries by Tick, then System id, then job index, then write order within the job: a deterministic order that never depends on which thread logged.</summary>
    internal static int CompareOrder(in LogEntry a, in LogEntry b)
    {
        int c = a.Tick.CompareTo(b.Tick);
        if (c != 0)
        {
            return c;
        }

        c = a.SystemId.CompareTo(b.SystemId);
        if (c != 0)
        {
            return c;
        }

        c = a.JobIndex.CompareTo(b.JobIndex);
        return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
    }

    /// <summary>Allocation-free comparer over <see cref="CompareOrder"/> for span sorts.</summary>
    internal readonly struct OrderComparer : IComparer<LogEntry>
    {
        public int Compare(LogEntry x, LogEntry y) => CompareOrder(in x, in y);
    }
}

/// <summary>
/// One Tick thread's log ring: a preallocated array written only by its owner thread during a Tick and
/// drained by the host between Ticks. When full, the oldest entry is overwritten and counted as dropped.
/// A worker runs its own stripe of jobs and then steals, so a ring is NOT in (Tick, System, job) order;
/// <see cref="SortPending"/> puts it there before a merge.
/// </summary>
internal sealed class LogRing
{
    private readonly LogEntry[] _entries;
    private int _head; // index of the oldest entry
    private int _count;
    private long _written;

    public LogRing(int capacity)
    {
        _entries = new LogEntry[Math.Max(1, capacity)];
    }

    public int Count => _count;

    public long Dropped { get; private set; }

    public void Write(in LogEntry entry)
    {
        var entries = _entries;
        var stamped = entry with { Sequence = _written++ };
        if (_count == entries.Length)
        {
            entries[_head] = stamped;
            _head = _head + 1 == entries.Length ? 0 : _head + 1;
            Dropped++;
            return;
        }

        int tail = _head + _count;
        if (tail >= entries.Length)
        {
            tail -= entries.Length;
        }

        entries[tail] = stamped;
        _count++;
    }

    /// <summary>Puts the pending entries into <see cref="LogEntry.CompareOrder"/> order, in place, without allocating.</summary>
    public void SortPending()
    {
        if (_count == 0)
        {
            _head = 0;
            return;
        }

        if (_head != 0)
        {
            // Rotate left by _head with three reversals so the oldest entry lands at index 0.
            var all = _entries.AsSpan();
            all[.._head].Reverse();
            all[_head..].Reverse();
            all.Reverse();
            _head = 0;
        }

        _entries.AsSpan(0, _count).Sort(default(LogEntry.OrderComparer));
    }

    public ref readonly LogEntry Peek() => ref _entries[_head];

    public void Pop()
    {
        _head = _head + 1 == _entries.Length ? 0 : _head + 1;
        _count--;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
