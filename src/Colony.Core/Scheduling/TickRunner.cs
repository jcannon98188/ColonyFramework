using Colony.Numerics;

namespace Colony;

/// <summary>
/// Runs one Tick for a World (spec section 4.3): every Phase in order, every wave of the Phase on the
/// worker pool, then the Phase boundary where command buffers merge in System order, then job order,
/// then record order; <see cref="World.ResetScratch"/> at the end. Per-World mutable state of the
/// scheduler (job list, per-System command buffers) lives here; the plan itself is the schema's
/// <see cref="SystemSchedule"/>. After warm-up a Tick allocates nothing.
/// </summary>
internal sealed class TickRunner : WorkerPool.IJobSource, IDisposable
{
    private readonly World _world;
    private readonly SystemSchedule _schedule;
    private readonly SystemState[] _states;
    private readonly WorkerPool _pool;
    private readonly Exception?[] _workerErrors;
    private readonly int[] _workerErrorJobs;
    private Job[] _jobs = new Job[64];
    private int _jobCount;
    private long _tick;

    public TickRunner(World world, SystemSchedule schedule, int workerCount)
    {
        _world = world;
        _schedule = schedule;
        _states = new SystemState[schedule.SystemCount];
        for (int i = 0; i < _states.Length; i++)
        {
            _states[i] = new SystemState();
        }

        _pool = new WorkerPool(workerCount, this);
        _workerErrors = new Exception?[_pool.WorkerCount];
        _workerErrorJobs = new int[_pool.WorkerCount];
    }

    public int WorkerCount => _pool.WorkerCount;

    public long WorkerAllocatedBytes => _pool.WorkerAllocatedBytes();

    public void RunTick(long tick)
    {
        _tick = tick;
        var phases = _schedule.Phases;
        for (int p = 0; p < phases.Length; p++)
        {
            var plan = phases[p];
            for (int wave = 0; wave < plan.WaveCount; wave++)
            {
                RunWave(plan, wave);
            }

            if (plan.Phase == Phase.End)
            {
                // The stated clearing point (spec section 4.3): End Systems saw this Tick's events; what the
                // End Phase itself emits merges into empty queues and is delivered through the next Tick.
                _world.ClearEvents();
            }

            Playback(plan);
        }

        _world.ResetScratch();
    }

    private void RunWave(SystemSchedule.PhasePlan plan, int wave)
    {
        uint counter = ++_world.ChangeCounter;
        _jobCount = 0;
        for (int k = 0; k < plan.Systems.Length; k++)
        {
            if (plan.WaveOf[k] != wave)
            {
                continue;
            }

            int id = plan.Systems[k];
            var def = _schedule.Definitions[id];
            var state = _states[id];
            ref readonly var descriptor = ref _schedule.Descriptors[id];
            state.Ran = _tick % descriptor.Every == descriptor.Offset;
            state.Jobs = 0;
            if (!state.Ran)
            {
                continue;
            }

            state.Random = _world.Random.Derive((ulong)id).Derive((ulong)_tick);
            if (def.ForEachChunk is not null)
            {
                GatherJobs(id, def, state);
            }

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
            if (plan.WaveOf[k] != wave)
            {
                continue;
            }

            int id = plan.Systems[k];
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
    }

    private void GatherJobs(int id, SystemDefinition def, SystemState state)
    {
        var all = _schedule.Queries[id];
        int archetypes = _world.ArchetypeCount;
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
                    continue;
                }

                if (!def.ChangeFilter.IsEmpty && !archetype.HasChangedSince(c, def.ChangeFilter, state.LastRun))
                {
                    continue;
                }

                if (_jobCount == _jobs.Length)
                {
                    Array.Resize(ref _jobs, _jobs.Length * 2);
                }

                _jobs[_jobCount++] = new Job(id, archetype, c, state.Jobs++);
            }
        }
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
        var context = new SystemContext(_world, _schedule.Definitions[id], state.Buffers[0], _tick, id, -1, state.Jobs, state.Random);
        try
        {
            action(in context);
        }
        catch (Exception e)
        {
            throw Fault(id, phase, e);
        }
    }

    /// <summary>One chunk job, called from any worker. Faults are recorded, never thrown across the pool.</summary>
    public void Run(int job, int worker)
    {
        ref readonly var j = ref _jobs[job];
        var def = _schedule.Definitions[j.System];
        var state = _states[j.System];
        try
        {
            var context = new SystemContext(_world, def, state.Buffers[j.JobIndex], _tick, j.System, j.JobIndex, state.Jobs, state.Random);
            var view = new ChunkView(j.Archetype, j.Chunk, _world.DebugValidation);
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
        for (int k = 0; k < plan.Systems.Length; k++)
        {
            var state = _states[plan.Systems[k]];
            if (!state.Ran)
            {
                continue;
            }

            int buffers = Math.Max(1, state.Jobs);
            for (int j = 0; j < buffers; j++)
            {
                _world.Playback(state.Buffers[j]);
            }
        }
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
