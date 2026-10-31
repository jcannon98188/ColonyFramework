using System.Diagnostics;

namespace Colony.Diagnostics;

/// <summary>
/// The in-process diagnostics of one World (spec section 10.1): a preallocated ring of per-Tick records
/// with a versioned numeric layout, the structured log rings, the registered counters and the hash trail.
/// Reading never allocates; recording happens inside <see cref="World.Tick"/> through internal hooks the
/// scheduler calls, so Colony.Core never references the Colony.Diagnostics package (sinks plug in through
/// <see cref="IDiagnosticsListener"/> and this read API).
/// </summary>
public sealed class WorldDiagnostics
{
    private readonly World _world;
    private readonly DiagnosticsOptions _options;
    private readonly int _systemCount;
    private readonly int _phaseCount;
    private readonly int _counterCount;
    private readonly int _capacity;
    private readonly int _jobCapacity;
    private readonly int _archetypeStride;
    private readonly TickRecord[] _ticks;
    private readonly PhaseRecord[] _phases;
    private readonly SystemRecord[] _systems;
    private readonly WorkerRecord[] _workers;
    private readonly JobRecord[] _jobs;
    private readonly long[] _counters;
    private readonly long[] _hostCounters;
    private readonly ulong[] _archetypeHashes;
    private readonly SystemRecord[] _topSystems;
    private readonly long[] _lastBusy;
    private readonly long[] _lastSpin;
    private readonly long[] _lastPark;
    private readonly long _budgetTicks;
    private int _head = -1;
    private int _count;
    private long _ticksRecorded;
    private int _jobCount;
    private long _tickStart;
    private long _phaseStart;
    private long _mainAllocatedAtStart;
    private long _lastSlowReport = long.MinValue;
    private ulong _spotCheckHash;
    private bool _spotCheckPending;

    internal WorldDiagnostics(World world, WorldSchema schema, DiagnosticsOptions options, int workerCount)
    {
        _world = world;
        _options = options;
        Level = options.Level;
        _systemCount = schema.Systems.Length;
        _phaseCount = schema.Phases.Length;
        _counterCount = schema.Counters.Length;
        WorkerCount = workerCount;
        _capacity = options.ValidatedRecordCapacity;
        _jobCapacity = Level == DiagnosticsLevel.Detailed ? Math.Max(0, options.JobTraceCapacity) : 0;
        _archetypeStride = options.HashTrail ? Math.Max(0, options.HashTrailArchetypes) : 0;
        _ticks = new TickRecord[_capacity];
        _phases = new PhaseRecord[_capacity * _phaseCount];
        _systems = new SystemRecord[_capacity * _systemCount];
        _workers = new WorkerRecord[_capacity * workerCount];
        _jobs = new JobRecord[_capacity * _jobCapacity];
        _counters = new long[_capacity * _counterCount];
        _hostCounters = new long[_counterCount];
        _archetypeHashes = new ulong[_capacity * _phaseCount * _archetypeStride];
        _topSystems = new SystemRecord[Math.Clamp(options.SlowTickTopSystems, 0, _systemCount)];
        _lastBusy = new long[workerCount];
        _lastSpin = new long[workerCount];
        _lastPark = new long[workerCount];
        _budgetTicks = options.TickBudget > TimeSpan.Zero ? (long)(options.TickBudget.TotalSeconds * Stopwatch.Frequency) : 0;
        AllocationCheck = options.AllocationCheck;
        Timing = Level != DiagnosticsLevel.Off;
        AccessChecking = options.AccessChecking ?? schema.DebugValidation;
        Slots = new WorkerSlot[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            Slots[w] = new WorkerSlot(w, _systemCount, _counterCount, options.LogCapacityPerThread);
        }
    }

    /// <summary>Version of the record layout this World writes (<see cref="TickRecord.LayoutVersion"/>).</summary>
    public static int LayoutVersion => TickRecord.LayoutVersion;

    public DiagnosticsLevel Level { get; }

    public DiagnosticsOptions Options => _options;

    /// <summary>Records the ring can hold.</summary>
    public int Capacity => _capacity;

    /// <summary>Records currently in the ring (at most <see cref="Capacity"/>).</summary>
    public int Count => _count;

    /// <summary>Ticks recorded since the World was created.</summary>
    public long TicksRecorded => _ticksRecorded;

    public int WorkerCount { get; }

    public int SystemCount => _systemCount;

    public int PhaseCount => _phaseCount;

    public int CounterCount => _counterCount;

    /// <summary>Entries waiting in the log rings.</summary>
    public int PendingLogs
    {
        get
        {
            int n = 0;
            foreach (var slot in Slots)
            {
                n += slot.Log.Count;
            }

            return n;
        }
    }

    /// <summary>Log entries overwritten before they were drained, since the World was created.</summary>
    public long DroppedLogs
    {
        get
        {
            long n = 0;
            foreach (var slot in Slots)
            {
                n += slot.Log.Dropped;
            }

            return n;
        }
    }

    /// <summary>The record of the most recent Tick. Throws when no Tick has run.</summary>
    public TickRecordView Latest => Record(0);

    /// <summary>The record <paramref name="ago"/> Ticks before the latest (0 = latest); at most <see cref="Count"/> - 1.</summary>
    public TickRecordView Record(int ago)
    {
        if ((uint)ago >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(ago), ago, $"The ring holds {_count} record(s).");
        }

        return View(SlotOf(ago));
    }

    /// <summary>Copies the latest records into <paramref name="destination"/>, oldest first; returns how many were copied.</summary>
    public int CopyLatest(Span<TickRecord> destination)
    {
        int n = Math.Min(destination.Length, _count);
        for (int i = 0; i < n; i++)
        {
            destination[n - 1 - i] = _ticks[SlotOf(i)];
        }

        return n;
    }

    /// <summary>
    /// Drains the log rings of every Tick thread into <paramref name="destination"/>, merged into (Tick,
    /// System, job) order, oldest first; returns how many entries were written. Entries that do not fit stay
    /// in the rings. Call it between Ticks, never while one runs.
    /// </summary>
    public int DrainLogs(Span<LogEntry> destination)
    {
        int written = 0;
        while (written < destination.Length)
        {
            LogRing? best = null;
            foreach (var slot in Slots)
            {
                var ring = slot.Log;
                if (ring.Count == 0)
                {
                    continue;
                }

                if (best is null || LogEntry.CompareOrder(in ring.Peek(), in best.Peek()) < 0)
                {
                    best = ring;
                }
            }

            if (best is null)
            {
                break;
            }

            destination[written++] = best.Peek();
            best.Pop();
        }

        return written;
    }

    /// <summary>Discards every pending log entry.</summary>
    public void ClearLogs()
    {
        foreach (var slot in Slots)
        {
            slot.Log.Clear();
        }
    }

    /// <summary>Logs from host code between Ticks (System id -1, the current Tick). Inside a System use <see cref="SystemContext.Log"/>.</summary>
    public void Log(LogMessageId message, long arg0 = 0, long arg1 = 0, long arg2 = 0, long arg3 = 0)
    {
        Slots[0].Log.Write(new LogEntry(_world.CurrentTick, -1, -1, message, arg0, arg1, arg2, arg3));
    }

    /// <summary>Adds to a registered counter from host code or serial hooks; the Tick record sums every thread's contribution.</summary>
    public void AddToCounter(CounterId counter, long delta) => _hostCounters[counter.Id] += delta;

    // ---- Internal hooks, called by the scheduler on the Tick thread (or, for the slots, by the owning worker). ----

    internal WorkerSlot[] Slots { get; }

    internal bool Timing { get; }

    internal bool AllocationCheck { get; }

    internal bool AccessChecking { get; }

    internal bool Detailed => Level == DiagnosticsLevel.Detailed;

    internal bool HashTrail => _options.HashTrail;

    internal long TickStart => _tickStart;

    internal void BeginTick(long tick)
    {
        _head = _head + 1 == _capacity ? 0 : _head + 1;
        if (_count < _capacity)
        {
            _count++;
        }

        _jobCount = 0;
        ref var record = ref _ticks[_head];
        record = default;
        record.Version = TickRecord.LayoutVersion;
        record.Level = Level;
        record.Tick = tick;
        record.MemoryBudgetBytes = _options.MemoryBudgetBytes;
        var phases = PhaseSpan(_head);
        for (int p = 0; p < phases.Length; p++)
        {
            phases[p] = default;
            phases[p].PhaseId = p;
        }

        var systems = SystemSpan(_head);
        for (int s = 0; s < systems.Length; s++)
        {
            systems[s] = default;
            systems[s].SystemId = s;
        }

        if (_archetypeStride > 0)
        {
            ArchetypeHashSpan(_head).Clear();
        }

        foreach (var slot in Slots)
        {
            slot.ResetTick();
        }

        _mainAllocatedAtStart = AllocationCheck ? GC.GetAllocatedBytesForCurrentThread() : 0;
        _tickStart = Stopwatch.GetTimestamp();
    }

    internal void BeginPhase()
    {
        if (Timing)
        {
            _phaseStart = Stopwatch.GetTimestamp();
        }
    }

    internal void WaveStarted(int phaseId) => PhaseSpan(_head)[phaseId].Waves++;

    internal void SystemScheduled(int system, bool ran, int chunksSkipped)
    {
        ref var record = ref SystemSpan(_head)[system];
        record.Ran = ran;
        record.ChunksSkipped += chunksSkipped;
        _ticks[_head].ChunksSkipped += chunksSkipped;
    }

    /// <summary>Called on the Tick thread after a wave's barrier: folds every worker's slots for the wave's Systems into the record.</summary>
    internal void EndWave(int phaseId, ReadOnlySpan<int> systems, ReadOnlySpan<int> waveOf, int wave)
    {
        var records = SystemSpan(_head);
        ref var tick = ref _ticks[_head];
        ref var phase = ref PhaseSpan(_head)[phaseId];
        for (int k = 0; k < systems.Length; k++)
        {
            if (waveOf[k] != wave)
            {
                continue;
            }

            int id = systems[k];
            ref var record = ref records[id];
            long first = long.MaxValue;
            long last = 0;
            foreach (var slot in Slots)
            {
                record.CpuTicks += slot.Cpu[id];
                record.Jobs += slot.Jobs[id];
                record.EntitiesTouched += slot.Entities[id];
                record.AllocatedBytes += slot.Allocated[id];
                if (slot.FirstStart[id] < first)
                {
                    first = slot.FirstStart[id];
                }

                if (slot.LastEnd[id] > last)
                {
                    last = slot.LastEnd[id];
                }

                slot.ResetSystem(id);
            }

            if (last > first)
            {
                record.WallTicks += last - first;
            }

            record.ChunksVisited += record.Jobs;
            tick.Jobs += record.Jobs;
            tick.ChunksVisited += record.Jobs;
            tick.EntitiesTouched += record.EntitiesTouched;
            phase.Jobs += record.Jobs;
        }
    }

    /// <summary>A serial hook (Begin/End) ran on the Tick thread.</summary>
    internal void SerialRan(int system, long start, long end, long allocated)
    {
        var slot = Slots[0];
        if (Timing)
        {
            ref var record = ref SystemSpan(_head)[system];
            record.WallTicks += end - start;
            record.CpuTicks += end - start;
            slot.SerialBusy += end - start;
        }

        if (allocated > 0)
        {
            slot.RecordAllocation(system, allocated);
            SystemSpan(_head)[system].AllocatedBytes += allocated;
        }
    }

    internal void SystemHashed(int system, ulong hash) => SystemSpan(_head)[system].Hash = hash;

    internal void PlaybackRan(int phaseId, long start, long end, long commands, long allocated)
    {
        ref var phase = ref PhaseSpan(_head)[phaseId];
        ref var tick = ref _ticks[_head];
        if (Timing)
        {
            phase.PlaybackTicks += end - start;
            tick.PlaybackTicks += end - start;
            Slots[0].SerialBusy += end - start;
        }

        phase.Commands += commands;
        tick.Commands += commands;
        if (allocated > 0)
        {
            Slots[0].RecordAllocation(WorkerSlot.AllocatedOutsideSystems, allocated);
        }
    }

    internal void SystemCommands(int system, long commands) => SystemSpan(_head)[system].Commands += commands;

    internal void EndPhase(int phaseId)
    {
        if (Timing)
        {
            PhaseSpan(_head)[phaseId].WallTicks = Stopwatch.GetTimestamp() - _phaseStart;
        }

        if (HashTrail)
        {
            ref var phase = ref PhaseSpan(_head)[phaseId];
            phase.Hash = _world.Hash();
            int archetypes = Math.Min(_archetypeStride, _world.ArchetypeCount);
            var hashes = ArchetypeHashSpan(_head).Slice(phaseId * _archetypeStride, _archetypeStride);
            for (int a = 0; a < archetypes; a++)
            {
                hashes[a] = _world.HashArchetype(a);
            }

            phase.ArchetypesHashed = archetypes;
        }
    }

    /// <summary>Detailed level only: one job trace entry, written by the worker that ran the job.</summary>
    internal void TraceJob(int system, int worker, int archetype, int chunk, int jobIndex, long start, long end)
    {
        int index = Interlocked.Increment(ref _jobCount) - 1;
        if (index >= _jobCapacity)
        {
            return;
        }

        ref var job = ref _jobs[(_head * _jobCapacity) + index];
        job.SystemId = system;
        job.Worker = worker;
        job.Archetype = archetype;
        job.Chunk = chunk;
        job.JobIndex = jobIndex;
        job.StartTicks = start - _tickStart;
        job.EndTicks = end - _tickStart;
    }

    /// <summary>Called on the Tick thread after the scratch reset: memory stats, worker clocks, allocation reports, slow-Tick report, listener.</summary>
    internal void EndTick(WorkerClock[] clocks, long scratchUsedBytes, int scratchChunks)
    {
        ref var tick = ref _ticks[_head];
        if (AllocationCheck)
        {
            long mainTotal = GC.GetAllocatedBytesForCurrentThread() - _mainAllocatedAtStart;
            var main = Slots[0];
            if (mainTotal > main.AllocatedBytes)
            {
                main.RecordAllocation(WorkerSlot.AllocatedOutsideSystems, mainTotal - main.AllocatedBytes);
            }
        }

        tick.WallTicks = Stopwatch.GetTimestamp() - _tickStart;
        tick.EntityCount = _world.EntityCount;
        tick.ArchetypeCount = _world.ArchetypeCount;
        tick.ChunksInUse = _world.Allocator.ChunksInUse;
        tick.PeakChunksInUse = _world.Allocator.PeakChunksInUse;
        tick.Slabs = _world.Allocator.SlabCount;
        tick.ReservedBytes = _world.Allocator.ReservedBytes;
        tick.ScratchUsedBytes = scratchUsedBytes;
        tick.ScratchChunks = scratchChunks;
        tick.LogsDropped = DroppedLogs;
        tick.JobsNotTraced = _jobCapacity > 0 ? Math.Max(0, _jobCount - _jobCapacity) : 0;
        if (HashTrail)
        {
            tick.Hash = _world.Hash();
        }

        var workers = WorkerSpan(_head);
        var counters = CounterSpan(_head);
        _hostCounters.AsSpan().CopyTo(counters);
        Array.Clear(_hostCounters);
        for (int w = 0; w < workers.Length; w++)
        {
            var slot = Slots[w];
            ref var record = ref workers[w];
            record = default;
            record.Worker = w;
            record.Jobs = slot.JobsRun;
            record.AllocatedBytes = slot.AllocatedBytes;
            record.FirstAllocatingSystem = slot.FirstAllocatingSystem == WorkerSlot.NoAllocation ? -1 : slot.FirstAllocatingSystem;
            if (Timing && w < clocks.Length)
            {
                var clock = clocks[w];
                long busy = Volatile.Read(ref clock.Busy);
                long spin = Volatile.Read(ref clock.Spin);
                long park = Volatile.Read(ref clock.Park);
                record.BusyTicks = busy - _lastBusy[w] + slot.SerialBusy;
                record.SpinTicks = spin - _lastSpin[w];
                record.ParkTicks = park - _lastPark[w];
                _lastBusy[w] = busy;
                _lastSpin[w] = spin;
                _lastPark[w] = park;
            }

            tick.AllocatedBytes += slot.AllocatedBytes;
            if (slot.AllocatedBytes > 0)
            {
                tick.AllocatingThreads++;
            }

            for (int c = 0; c < counters.Length; c++)
            {
                counters[c] += slot.Counters[c];
            }
        }

        tick.Slow = _budgetTicks > 0 && tick.WallTicks > _budgetTicks;
        _ticksRecorded++;

        var listener = _options.Listener;
        if (listener is null)
        {
            return;
        }

        var view = View(_head);
        listener.OnTickRecorded(_world, in view);
        if (tick.AllocatingThreads > 0)
        {
            foreach (var slot in Slots)
            {
                if (slot.AllocatedBytes > 0)
                {
                    int system = slot.FirstAllocatingSystem;
                    string? name = system >= 0 ? _world.Schema.Systems[system].Name : null;
                    listener.OnAllocation(_world, new AllocationReport(tick.Tick, slot.Worker, system, name, slot.AllocatedBytes));
                }
            }
        }

        if (tick.Slow && (_lastSlowReport == long.MinValue || tick.Tick - _lastSlowReport >= Math.Max(1, _options.SlowTickReportInterval)))
        {
            _lastSlowReport = tick.Tick;
            int top = SelectTopSystems(view.Systems);
            listener.OnSlowTick(_world, new SlowTickReport(tick.Tick, tick.WallTicks, _budgetTicks, _topSystems.AsSpan(0, top)));
        }
    }

    /// <summary>Spot check, called by <see cref="World.Tick"/> after the Tick counter advanced.</summary>
    internal void AfterTick(long completedTick)
    {
        int every = _options.DeterminismSpotCheckEvery;
        if (every > 0 && completedTick % every == 0)
        {
            _spotCheckHash = _world.Hash();
            _spotCheckPending = true;
        }
    }

    /// <summary>Spot check, called by <see cref="World.Tick"/> before anything runs.</summary>
    internal void BeforeTick(long tick)
    {
        if (!_spotCheckPending)
        {
            return;
        }

        _spotCheckPending = false;
        ulong actual = _world.Hash();
        if (actual != _spotCheckHash)
        {
            SpotCheckFailures++;
            _options.Listener?.OnDeterminismCheckFailed(_world, new DeterminismCheckReport(tick, _spotCheckHash, actual));
        }
    }

    /// <summary>Determinism spot-checks that found the World changed between Ticks.</summary>
    public int SpotCheckFailures { get; private set; }

    private int SelectTopSystems(ReadOnlySpan<SystemRecord> systems)
    {
        int n = 0;
        foreach (ref readonly var system in systems)
        {
            if (!system.Ran)
            {
                continue;
            }

            int at = n;
            while (at > 0 && _topSystems[at - 1].WallTicks < system.WallTicks)
            {
                at--;
            }

            if (at >= _topSystems.Length)
            {
                continue;
            }

            int last = Math.Min(n, _topSystems.Length - 1);
            for (int i = last; i > at; i--)
            {
                _topSystems[i] = _topSystems[i - 1];
            }

            _topSystems[at] = system;
            if (n < _topSystems.Length)
            {
                n++;
            }
        }

        return n;
    }

    private int SlotOf(int ago)
    {
        int slot = _head - ago;
        return slot < 0 ? slot + _capacity : slot;
    }

    private TickRecordView View(int slot)
        => new(
            ref _ticks[slot],
            PhaseSpan(slot),
            SystemSpan(slot),
            WorkerSpan(slot),
            _jobs.AsSpan(slot * _jobCapacity, Math.Min(_jobCapacity, slot == _head ? _jobCount : _ticks[slot].Jobs)),
            CounterSpan(slot),
            ArchetypeHashSpan(slot),
            _archetypeStride);

    private Span<PhaseRecord> PhaseSpan(int slot) => _phases.AsSpan(slot * _phaseCount, _phaseCount);

    private Span<SystemRecord> SystemSpan(int slot) => _systems.AsSpan(slot * _systemCount, _systemCount);

    private Span<WorkerRecord> WorkerSpan(int slot) => _workers.AsSpan(slot * WorkerCount, WorkerCount);

    private Span<long> CounterSpan(int slot) => _counters.AsSpan(slot * _counterCount, _counterCount);

    private Span<ulong> ArchetypeHashSpan(int slot)
    {
        int stride = _phaseCount * _archetypeStride;
        return _archetypeHashes.AsSpan(slot * stride, stride);
    }
}

/// <summary>Per-worker time accounting of the worker pool; written by the owning thread, read by the Tick thread at the end of the Tick.</summary>
internal sealed class WorkerClock
{
    public long Busy;
    public long Spin;
    public long Park;
}
