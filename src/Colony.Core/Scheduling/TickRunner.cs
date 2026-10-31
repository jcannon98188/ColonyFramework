using System.Diagnostics;
using Colony.Diagnostics;
using Colony.Numerics;

namespace Colony;

/// <summary>
/// Runs one Tick for a World (spec section 4.3): every Phase in order, every wave of the Phase on the
/// worker pool, then the Phase boundary where command buffers merge in System order, then job order,
/// then record order; <see cref="World.ResetScratch"/> at the end. Per-World mutable state of the
/// scheduler (job list, per-System command buffers) lives here; the plan itself is the schema's
/// <see cref="SystemSchedule"/>. After warm-up a Tick allocates nothing. The diagnostics hooks
/// (spec section 10.1) take one timestamp pair and one allocation-counter pair per job into the running
/// worker's <see cref="WorkerSlot"/>; the Tick thread folds the slots into the record after each wave.
/// </summary>
internal sealed class TickRunner : WorkerPool.IJobSource, IDisposable
{
    private readonly World _world;
    private readonly SystemSchedule _schedule;
    private readonly SystemState[] _states;
    private readonly WorkerPool _pool;
    private readonly WorldDiagnostics _diagnostics;
    private readonly WorkerSlot[] _slots;
    private readonly ComponentMask[] _access;
    private readonly Exception?[] _workerErrors;
    private readonly int[] _workerErrorJobs;
    private readonly bool _timing;
    private readonly bool _allocationCheck;
    private readonly bool _detailed;
    private readonly bool _accessChecking;
    private readonly bool _hashTrail;
    private Job[] _jobs = new Job[64];
    private int _jobCount;
    private long _tick;

    public TickRunner(World world, SystemSchedule schedule, int workerCount)
    {
        _world = world;
        _schedule = schedule;
        _diagnostics = world.Diagnostics;
        _slots = _diagnostics.Slots;
        _timing = _diagnostics.Timing;
        _allocationCheck = _diagnostics.AllocationCheck;
        _detailed = _diagnostics.Detailed;
        _accessChecking = _diagnostics.AccessChecking;
        _hashTrail = _diagnostics.HashTrail;
        _states = new SystemState[schedule.SystemCount];
        _access = new ComponentMask[schedule.SystemCount];
        for (int i = 0; i < _states.Length; i++)
        {
            _states[i] = new SystemState();
            _access[i] = schedule.Definitions[i].Reads.Union(schedule.Definitions[i].Writes);
        }

        _pool = new WorkerPool(workerCount, this, _timing);
        _workerErrors = new Exception?[_pool.WorkerCount];
        _workerErrorJobs = new int[_pool.WorkerCount];
    }

    public int WorkerCount => _pool.WorkerCount;

    public long WorkerAllocatedBytes => _pool.WorkerAllocatedBytes();

    public void RunTick(long tick)
    {
        _tick = tick;
        _diagnostics.BeginTick(tick);
        var phases = _schedule.Phases;
        for (int p = 0; p < phases.Length; p++)
        {
            var plan = phases[p];
            _diagnostics.BeginPhase();
            for (int wave = 0; wave < plan.WaveCount; wave++)
            {
                if (_hashTrail)
                {
                    // One System at a time so the trail attributes every hash to a single System. Systems of
                    // one wave never conflict, so the result is the same as running them together.
                    for (int k = 0; k < plan.Systems.Length; k++)
                    {
                        if (plan.WaveOf[k] == wave)
                        {
                            RunWave(plan, wave, plan.Systems[k]);
                            _diagnostics.SystemHashed(plan.Systems[k], _world.Hash());
                        }
                    }
                }
                else
                {
                    RunWave(plan, wave, -1);
                }
            }

            Playback(plan);
            _diagnostics.EndPhase(plan.Phase.Id);
        }

        long scratchUsed = _world.Scratch.UsedBytes;
        int scratchChunks = _world.Scratch.ChunkCount;
        _world.ResetScratch();
        _diagnostics.EndTick(_pool.Clocks, scratchUsed, scratchChunks);
    }

    /// <summary>Runs the Systems of one wave, or only <paramref name="only"/> when it is a System id.</summary>
    private void RunWave(SystemSchedule.PhasePlan plan, int wave, int only)
    {
        uint counter = ++_world.ChangeCounter;
        _diagnostics.WaveStarted(plan.Phase.Id);
        _jobCount = 0;
        for (int k = 0; k < plan.Systems.Length; k++)
        {
            int id = plan.Systems[k];
            if (plan.WaveOf[k] != wave || (only >= 0 && id != only))
            {
                continue;
            }

            var def = _schedule.Definitions[id];
            var state = _states[id];
            ref readonly var descriptor = ref _schedule.Descriptors[id];
            state.Ran = _tick % descriptor.Every == descriptor.Offset;
            state.Jobs = 0;
            if (!state.Ran)
            {
                _diagnostics.SystemScheduled(id, ran: false, 0);
                continue;
            }

            state.Random = _world.Random.Derive((ulong)id).Derive((ulong)_tick);
            int skipped = 0;
            if (def.ForEachChunk is not null)
            {
                skipped = GatherJobs(id, def, state);
            }

            _diagnostics.SystemScheduled(id, ran: true, skipped);
            EnsureBuffers(state, Math.Max(1, state.Jobs));
            if (def.Begin is not null)
            {
                RunSerial(def.Begin, id, state, plan.Phase);
            }
        }

        _pool.Run(_jobCount);
        ThrowIfJobFaulted(plan.Phase);

        for (int k = 0; k < plan.Systems.Length; k++)
        {
            int id = plan.Systems[k];
            if (plan.WaveOf[k] != wave || (only >= 0 && id != only))
            {
                continue;
            }

            var state = _states[id];
            if (!state.Ran)
            {
                continue;
            }

            var def = _schedule.Definitions[id];
            if (def.End is not null)
            {
                RunSerial(def.End, id, state, plan.Phase);
            }

            state.LastRun = counter;
        }

        if (only >= 0)
        {
            _diagnostics.EndWave(plan.Phase.Id, [only], [wave], wave);
        }
        else
        {
            _diagnostics.EndWave(plan.Phase.Id, plan.Systems, plan.WaveOf, wave);
        }
    }

    /// <summary>Appends one job per matching chunk; returns how many matching chunks were skipped (asleep or unchanged).</summary>
    private int GatherJobs(int id, SystemDefinition def, SystemState state)
    {
        var all = _schedule.Queries[id];
        int archetypes = _world.ArchetypeCount;
        int skipped = 0;
        for (int a = 0; a < archetypes; a++)
        {
            var archetype = _world.ArchetypeAt(a);
            if (archetype.ChunkCount == 0 || !archetype.Signature.ContainsAll(all) || !archetype.Signature.ContainsNone(def.None))
            {
                continue;
            }

            for (int c = 0; c < archetype.ChunkCount; c++)
            {
                if (def.SkipAsleepChunks && archetype.IsAsleep(c))
                {
                    skipped++;
                    continue;
                }

                if (!def.ChangeFilter.IsEmpty && !archetype.HasChangedSince(c, def.ChangeFilter, state.LastRun))
                {
                    skipped++;
                    continue;
                }

                if (_jobCount == _jobs.Length)
                {
                    Array.Resize(ref _jobs, _jobs.Length * 2);
                }

                _jobs[_jobCount++] = new Job(id, archetype, c, state.Jobs++);
            }
        }

        return skipped;
    }

    private void EnsureBuffers(SystemState state, int count)
    {
        if (state.Buffers.Length >= count)
        {
            return;
        }

        int old = state.Buffers.Length;
        Array.Resize(ref state.Buffers, Math.Max(count, old * 2));
        for (int i = old; i < state.Buffers.Length; i++)
        {
            state.Buffers[i] = _world.CreateCommandBuffer();
        }
    }

    private void RunSerial(SystemAction action, int id, SystemState state, Phase phase)
    {
        var context = new SystemContext(_world, state.Buffers[0], _tick, id, -1, state.Jobs, state.Random, _slots[0]);
        long allocated = _allocationCheck ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long start = _timing ? Stopwatch.GetTimestamp() : 0;
        try
        {
            action(in context);
        }
        catch (Exception e)
        {
            throw Fault(id, phase, e);
        }

        long end = _timing ? Stopwatch.GetTimestamp() : 0;
        allocated = _allocationCheck ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0;
        _diagnostics.SerialRan(id, start, end, allocated);
    }

    /// <summary>One chunk job, called from any worker. Faults are recorded, never thrown across the pool.</summary>
    public void Run(int job, int worker)
    {
        ref readonly var j = ref _jobs[job];
        var def = _schedule.Definitions[j.System];
        var state = _states[j.System];
        var slot = _slots[worker];
        long allocated = _allocationCheck ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long start = _timing ? Stopwatch.GetTimestamp() : 0;
        int rows = 0;
        try
        {
            var context = new SystemContext(_world, state.Buffers[j.JobIndex], _tick, j.System, j.JobIndex, state.Jobs, state.Random, slot);
            var view = _accessChecking
                ? new ChunkView(j.Archetype, j.Chunk, _world.DebugValidation, _access[j.System], def.Name)
                : new ChunkView(j.Archetype, j.Chunk, _world.DebugValidation);
            rows = view.Count;
            def.ForEachChunk!(in context, in view);
            j.Archetype.MarkWritten(j.Chunk, def.Writes, _world.ChangeCounter);
        }
        catch (Exception e)
        {
            if (_workerErrors[worker] is null || job < _workerErrorJobs[worker])
            {
                _workerErrors[worker] = e;
                _workerErrorJobs[worker] = job;
            }
        }

        long end = _timing ? Stopwatch.GetTimestamp() : 0;
        allocated = _allocationCheck ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0;
        slot.RecordJob(j.System, start, end, rows, allocated);
        if (_detailed)
        {
            _diagnostics.TraceJob(j.System, worker, j.Archetype.Id, j.Chunk, j.JobIndex, start, end);
        }
    }

    private void ThrowIfJobFaulted(Phase phase)
    {
        int firstJob = int.MaxValue;
        Exception? first = null;
        for (int w = 0; w < _workerErrors.Length; w++)
        {
            if (_workerErrors[w] is { } e && _workerErrorJobs[w] < firstJob)
            {
                firstJob = _workerErrorJobs[w];
                first = e;
            }

            _workerErrors[w] = null;
        }

        if (first is not null)
        {
            throw Fault(_jobs[firstJob].System, phase, first);
        }
    }

    private SystemFaultException Fault(int id, Phase phase, Exception e)
        => new(new WorldFault(_schedule.Definitions[id].Name, id, phase, _tick, e));

    private void Playback(SystemSchedule.PhasePlan plan)
    {
        long allocated = _allocationCheck ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long start = _timing ? Stopwatch.GetTimestamp() : 0;
        long commands = 0;
        for (int k = 0; k < plan.Systems.Length; k++)
        {
            int id = plan.Systems[k];
            var state = _states[id];
            if (!state.Ran)
            {
                continue;
            }

            int buffers = Math.Max(1, state.Jobs);
            long systemCommands = 0;
            for (int j = 0; j < buffers; j++)
            {
                systemCommands += state.Buffers[j].Count;
                _world.Playback(state.Buffers[j]);
            }

            if (systemCommands > 0)
            {
                _diagnostics.SystemCommands(id, systemCommands);
                commands += systemCommands;
            }
        }

        long end = _timing ? Stopwatch.GetTimestamp() : 0;
        allocated = _allocationCheck ? GC.GetAllocatedBytesForCurrentThread() - allocated : 0;
        _diagnostics.PlaybackRan(plan.Phase.Id, start, end, commands, allocated);
    }

    public void Dispose() => _pool.Dispose();

    private readonly struct Job
    {
        public Job(int system, Archetype archetype, int chunk, int jobIndex)
        {
            System = system;
            Archetype = archetype;
            Chunk = chunk;
            JobIndex = jobIndex;
        }

        public int System { get; }

        public Archetype Archetype { get; }

        public int Chunk { get; }

        public int JobIndex { get; }
    }

    private sealed class SystemState
    {
        public CommandBuffer[] Buffers = [];
        public int Jobs;
        public bool Ran;
        public uint LastRun;
        public RandomSource Random;
    }
}
