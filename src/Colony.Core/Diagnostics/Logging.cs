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

    /// <summary>Orders entries by Tick, then System id, then job index: a deterministic order that never depends on which thread logged.</summary>
    internal static int CompareOrder(in LogEntry a, in LogEntry b)
    {
        int c = a.Tick.CompareTo(b.Tick);
        if (c != 0)
        {
            return c;
        }

        c = a.SystemId.CompareTo(b.SystemId);
        return c != 0 ? c : a.JobIndex.CompareTo(b.JobIndex);
    }
}

/// <summary>
/// One Tick thread's log ring: a preallocated array written only by its owner thread during a Tick and
/// drained by the host between Ticks. When full, the oldest entry is overwritten and counted as dropped.
/// </summary>
internal sealed class LogRing
{
    private readonly LogEntry[] _entries;
    private int _head; // index of the oldest entry
    private int _count;

    public LogRing(int capacity)
    {
        _entries = new LogEntry[Math.Max(1, capacity)];
    }

    public int Count => _count;

    public long Dropped { get; private set; }

    public void Write(in LogEntry entry)
    {
        var entries = _entries;
        if (_count == entries.Length)
        {
            entries[_head] = entry;
            _head = _head + 1 == entries.Length ? 0 : _head + 1;
            Dropped++;
            return;
        }

        int tail = _head + _count;
        if (tail >= entries.Length)
        {
            tail -= entries.Length;
        }

        entries[tail] = entry;
        _count++;
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
