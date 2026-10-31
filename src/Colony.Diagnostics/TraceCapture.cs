namespace Colony.Diagnostics;

/// <summary>
/// A host-side copy of the latest N Tick records of one World, taken between Ticks for later export
/// (<see cref="PerfettoTrace"/>). Every array is allocated once in the constructor and
/// <see cref="Capture()"/> only copies spans, so capturing after each Tick allocates nothing and never touches
/// simulation state; the ring keeps the most recent <see cref="Capacity"/> Ticks.
/// </summary>
public sealed class TraceCapture
{
    private readonly World _world;
    private readonly int _capacity;
    private readonly int _phaseCount;
    private readonly int _systemCount;
    private readonly int _workerCount;
    private readonly int _jobCapacity;
    private readonly TickRecord[] _ticks;
    private readonly PhaseRecord[] _phases;
    private readonly SystemRecord[] _systems;
    private readonly WorkerRecord[] _workers;
    private readonly JobRecord[] _jobs;
    private readonly int[] _jobCounts;
    private int _head = -1;
    private int _count;
    private long _lastTick = -1;

    public TraceCapture(World world, int tickCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(world);
        RecordLayout.Verify(world, "Perfetto trace capture");
        _world = world;
        var diagnostics = world.Diagnostics;
        _capacity = Math.Max(1, tickCapacity);
        _phaseCount = diagnostics.PhaseCount;
        _systemCount = diagnostics.SystemCount;
        _workerCount = diagnostics.WorkerCount;
        _jobCapacity = diagnostics.Level == DiagnosticsLevel.Detailed ? Math.Max(0, diagnostics.Options.JobTraceCapacity) : 0;
        _ticks = new TickRecord[_capacity];
        _phases = new PhaseRecord[_capacity * _phaseCount];
        _systems = new SystemRecord[_capacity * _systemCount];
        _workers = new WorkerRecord[_capacity * _workerCount];
        _jobs = new JobRecord[_capacity * _jobCapacity];
        _jobCounts = new int[_capacity];
    }

    /// <summary>The World whose records are captured; its schema names the Phases and Systems on export.</summary>
    public World World => _world;

    /// <summary>Ticks the capture can hold.</summary>
    public int Capacity => _capacity;

    /// <summary>Ticks currently held (at most <see cref="Capacity"/>).</summary>
    public int Count => _count;

    /// <summary>Ticks captured since construction or the last <see cref="Clear"/> did not reset it; counts overwritten Ticks too.</summary>
    public long TicksCaptured { get; private set; }

    public int WorkerCount => _workerCount;

    /// <summary>Jobs the capture keeps per Tick (0 below <see cref="DiagnosticsLevel.Detailed"/>).</summary>
    public int JobCapacity => _jobCapacity;

    /// <summary>
    /// Copies the World's latest record unless it was already captured. Returns false when no Tick has run
    /// yet or the latest Tick is already in the capture. Call between Ticks on the host thread.
    /// </summary>
    public bool Capture()
    {
        var diagnostics = _world.Diagnostics;
        if (diagnostics.Count == 0)
        {
            return false;
        }

        var record = diagnostics.Latest;
        if (record.Tick.Tick == _lastTick)
        {
            return false;
        }

        Capture(in record);
        return true;
    }

    /// <summary>Copies one record into the capture (the listener-side variant; the record must belong to this capture's World).</summary>
    public void Capture(in TickRecordView record)
    {
        RecordLayout.Verify(record.Tick.Version, "Perfetto trace capture");
        if (record.Phases.Length != _phaseCount || record.Systems.Length != _systemCount || record.Workers.Length != _workerCount)
        {
            throw new ArgumentException("The record does not belong to this capture's World.", nameof(record));
        }

        _head = _head + 1 == _capacity ? 0 : _head + 1;
        if (_count < _capacity)
        {
            _count++;
        }

        _ticks[_head] = record.Tick;
        record.Phases.CopyTo(PhaseSpan(_head));
        record.Systems.CopyTo(SystemSpan(_head));
        record.Workers.CopyTo(WorkerSpan(_head));
        int jobs = Math.Min(record.Jobs.Length, _jobCapacity);
        record.Jobs[..jobs].CopyTo(_jobs.AsSpan(_head * _jobCapacity, jobs));
        _jobCounts[_head] = jobs;
        _lastTick = record.Tick.Tick;
        TicksCaptured++;
    }

    /// <summary>Forgets every captured Tick (the next <see cref="Capture()"/> takes the latest record again).</summary>
    public void Clear()
    {
        _head = -1;
        _count = 0;
        _lastTick = -1;
    }

    /// <summary>The captured Ticks oldest first; index 0 is the oldest held Tick.</summary>
    public CapturedTick Oldest(int index)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The capture holds {_count} Tick(s).");
        }

        int slot = _head - (_count - 1 - index);
        if (slot < 0)
        {
            slot += _capacity;
        }

        return new CapturedTick(
            ref _ticks[slot],
            PhaseSpan(slot),
            SystemSpan(slot),
            WorkerSpan(slot),
            _jobs.AsSpan(slot * _jobCapacity, _jobCounts[slot]));
    }

    private Span<PhaseRecord> PhaseSpan(int slot) => _phases.AsSpan(slot * _phaseCount, _phaseCount);

    private Span<SystemRecord> SystemSpan(int slot) => _systems.AsSpan(slot * _systemCount, _systemCount);

    private Span<WorkerRecord> WorkerSpan(int slot) => _workers.AsSpan(slot * _workerCount, _workerCount);
}

/// <summary>One captured Tick viewed in place inside a <see cref="TraceCapture"/>; valid until the next capture.</summary>
public readonly ref struct CapturedTick
{
    private readonly ref readonly TickRecord _tick;

    internal CapturedTick(ref readonly TickRecord tick, ReadOnlySpan<PhaseRecord> phases, ReadOnlySpan<SystemRecord> systems, ReadOnlySpan<WorkerRecord> workers, ReadOnlySpan<JobRecord> jobs)
    {
        _tick = ref tick;
        Phases = phases;
        Systems = systems;
        Workers = workers;
        Jobs = jobs;
    }

    public ref readonly TickRecord Tick => ref _tick;

    public ReadOnlySpan<PhaseRecord> Phases { get; }

    public ReadOnlySpan<SystemRecord> Systems { get; }

    public ReadOnlySpan<WorkerRecord> Workers { get; }

    /// <summary>Per-job trace; empty below <see cref="DiagnosticsLevel.Detailed"/>.</summary>
    public ReadOnlySpan<JobRecord> Jobs { get; }
}
