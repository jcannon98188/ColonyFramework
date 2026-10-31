using System.Diagnostics;

namespace Colony.Ecs;

/// <summary>Fixed Phases. Structural command buffers are merged at the end of each Phase.</summary>
public enum Phase { Pre = 0, Update = 1, Late = 2 }

public readonly unsafe ref struct JobContext
{
    public readonly World World;
    public readonly CommandBuffer Commands;
    public readonly ChunkView Chunk;   // default for non-split Systems
    public readonly int JobIndex;      // stable: chunk ordinal in archetype-ID then chunk order
    public readonly int ThreadIndex;   // for scratch only; never affects results
    public readonly long Tick;

    public JobContext(World w, CommandBuffer cb, ChunkView chunk, int job, int thread, long tick)
    {
        World = w; Commands = cb; Chunk = chunk; JobIndex = job; ThreadIndex = thread; Tick = tick;
    }
}

/// <summary>
/// Hand-written System plumbing. Each System declares its query and its Component/Resource read/write sets in data;
/// the Scheduler uses only these declarations to decide what may run concurrently.
/// </summary>
public abstract class SystemBase
{
    public int Id { get; internal set; } = -1;
    public abstract string Name { get; }
    public abstract Phase Phase { get; }

    public ulong All;      // query: required Components
    public ulong None;     // query: excluded Components
    public ulong Reads;    // Component read set
    public ulong Writes;   // Component write set
    public ulong ResourceReads;
    public ulong ResourceWrites;
    public bool SplitByChunk = true;
    public int Interval = 1;   // run every N Ticks...
    public int Offset;         // ...when tick % Interval == Offset

    internal CommandBuffer[] Buffers = new CommandBuffer[1] { new() };
    public int JobsThisTick { get; internal set; }
    internal bool RanThisTick;
    internal long[] CpuTimestampTicks = new long[Scheduler.MaxThreads * Scheduler.Pad];
    public long TotalJobs;

    public virtual void BeginTick(World w, long tick) { }
    /// <summary>Called single-threaded with the number of jobs about to run (size per-job reduction slots here).</summary>
    public virtual void PrepareJobs(int jobCount) { }
    public abstract void Execute(in JobContext ctx);
    /// <summary>Single-threaded after all jobs: combine per-job partials in job order.</summary>
    public virtual void EndTick(World w, long tick) { }

    public bool ConflictsWith(SystemBase o) =>
        (Writes & (o.Reads | o.Writes)) != 0 || (o.Writes & Reads) != 0 ||
        (ResourceWrites & (o.ResourceReads | o.ResourceWrites)) != 0 || (o.ResourceWrites & ResourceReads) != 0;

    public double CpuMilliseconds()
    {
        long t = 0;
        for (int i = 0; i < Scheduler.MaxThreads; i++) t += CpuTimestampTicks[i * Scheduler.Pad];
        return t * 1000.0 / Stopwatch.Frequency;
    }

    public void ResetStats()
    {
        Array.Clear(CpuTimestampTicks);
        TotalJobs = 0;
    }
}

internal struct Job
{
    public SystemBase System;
    public Archetype? Archetype;
    public int Chunk;
    public int JobIndex;
}

/// <summary>
/// Builds per-Phase "waves" from declared access: Systems are ordered by ID; a System conflicting with an earlier one
/// in the same Phase lands in a later wave (level = 1 + max level of earlier conflicting Systems). Systems in one wave
/// never conflict, so all their chunk jobs go into one job list drained by the worker pool.
/// </summary>
public sealed unsafe class Scheduler : IDisposable
{
    public const int MaxThreads = 64;
    internal const int Pad = 16; // longs per thread slot (128 bytes) to avoid false sharing
    private const int PhaseCount = 3;

    private readonly World _world;
    private readonly List<SystemBase> _systems = new();
    private SystemBase[][][] _waves = Array.Empty<SystemBase[][]>(); // [phase][wave][system]
    private SystemBase[][] _phaseSystems = Array.Empty<SystemBase[]>();
    private Job[] _jobs = new Job[256];
    private int _jobCount;
    private readonly WorkerPool _pool;
    private long _tick;
    private bool _built;

    public int ThreadCount { get; }
    public long[] PlaybackTimestampTicks = new long[PhaseCount];
    public long[] WaveWallTimestampTicks = new long[PhaseCount];

    public Scheduler(World world, int threadCount)
    {
        _world = world;
        ThreadCount = Math.Clamp(threadCount, 1, MaxThreads);
        _pool = new WorkerPool(ThreadCount - 1, RunJob);
    }

    public IReadOnlyList<SystemBase> Systems => _systems;

    internal void Add(SystemBase s)
    {
        if (_built) throw new InvalidOperationException("Scheduler already built");
        s.Id = _systems.Count;
        _systems.Add(s);
    }

    public void Build()
    {
        _phaseSystems = new SystemBase[PhaseCount][];
        _waves = new SystemBase[PhaseCount][][];
        for (int p = 0; p < PhaseCount; p++)
        {
            var inPhase = _systems.Where(s => (int)s.Phase == p).OrderBy(s => s.Id).ToArray();
            _phaseSystems[p] = inPhase;
            var level = new int[inPhase.Length];
            for (int i = 0; i < inPhase.Length; i++)
                for (int j = 0; j < i; j++)
                    if (inPhase[i].ConflictsWith(inPhase[j])) level[i] = Math.Max(level[i], level[j] + 1);
            int waves = inPhase.Length == 0 ? 0 : level.Max() + 1;
            _waves[p] = Enumerable.Range(0, waves)
                .Select(l => inPhase.Where((_, i) => level[i] == l).ToArray()).ToArray();
        }
        _built = true;
    }

    public string DescribeWaves()
    {
        var sb = new System.Text.StringBuilder();
        for (int p = 0; p < PhaseCount; p++)
        {
            sb.Append((Phase)p).Append(": ");
            sb.Append(string.Join(" -> ", _waves[p].Select(w => "[" + string.Join(", ", w.Select(s => $"{s.Id}:{s.Name}")) + "]")));
            sb.Append(" -> playback\n");
        }
        return sb.ToString();
    }

    public void ResetStats()
    {
        foreach (var s in _systems) s.ResetStats();
        Array.Clear(PlaybackTimestampTicks);
        Array.Clear(WaveWallTimestampTicks);
    }

    internal void RunTick(long tick)
    {
        if (!_built) Build();
        _tick = tick;
        for (int p = 0; p < PhaseCount; p++)
        {
            long t0 = Stopwatch.GetTimestamp();
            foreach (var wave in _waves[p]) RunWave(wave, tick);
            long t1 = Stopwatch.GetTimestamp();
            // Phase boundary: merge command buffers in System-ID order, then job order.
            foreach (var s in _phaseSystems[p])
            {
                if (!s.RanThisTick) continue;
                for (int j = 0; j < s.JobsThisTick; j++) s.Buffers[j].Playback(_world);
            }
            long t2 = Stopwatch.GetTimestamp();
            WaveWallTimestampTicks[p] += t1 - t0;
            PlaybackTimestampTicks[p] += t2 - t1;
        }
    }

    private void RunWave(SystemBase[] wave, long tick)
    {
        _jobCount = 0;
        foreach (var s in wave)
        {
            s.RanThisTick = s.Interval <= 1 || tick % s.Interval == s.Offset;
            s.JobsThisTick = 0;
            if (!s.RanThisTick) continue;
            s.BeginTick(_world, tick);
            int jobIndex = 0;
            if (s.SplitByChunk)
            {
                var archetypes = _world.Archetypes;
                for (int a = 0; a < archetypes.Count; a++)
                {
                    var arch = archetypes[a];
                    if ((arch.Signature & s.All) != s.All || (arch.Signature & s.None) != 0) continue;
                    for (int c = 0; c < arch.ChunkCount; c++)
                        PushJob(s, arch, c, jobIndex++);
                }
            }
            else PushJob(s, null, 0, jobIndex++);
            s.JobsThisTick = jobIndex;
            if (s.Buffers.Length < jobIndex)
            {
                int old = s.Buffers.Length;
                Array.Resize(ref s.Buffers, Math.Max(jobIndex, old * 2));
                for (int i = old; i < s.Buffers.Length; i++) s.Buffers[i] = new CommandBuffer();
            }
            s.PrepareJobs(jobIndex);
            s.TotalJobs += jobIndex;
        }
        _pool.Run(_jobCount);
        foreach (var s in wave)
            if (s.RanThisTick) s.EndTick(_world, tick);
    }

    private void PushJob(SystemBase s, Archetype? a, int chunk, int jobIndex)
    {
        if (_jobCount == _jobs.Length) Array.Resize(ref _jobs, _jobs.Length * 2);
        _jobs[_jobCount++] = new Job { System = s, Archetype = a, Chunk = chunk, JobIndex = jobIndex };
    }

    private void RunJob(int index, int thread)
    {
        ref var job = ref _jobs[index];
        long t0 = Stopwatch.GetTimestamp();
        var chunk = job.Archetype is null ? default : job.Archetype.GetChunk(job.Chunk);
        var ctx = new JobContext(_world, job.System.Buffers[job.JobIndex], chunk, job.JobIndex, thread, _tick);
        job.System.Execute(in ctx);
        job.System.CpuTimestampTicks[thread * Pad] += Stopwatch.GetTimestamp() - t0;
    }

    public long WorkerAllocatedBytes() => _pool.WorkerAllocatedBytes();

    public void Dispose() => _pool.Dispose();
}

/// <summary>
/// Minimal pool: N-1 dedicated threads plus the calling thread drain a shared job index (dynamic, one job at a time).
/// Idle workers spin for a short window and then park on a per-worker event.
/// </summary>
internal sealed class WorkerPool : IDisposable
{
    private sealed class Worker
    {
        public Thread Thread = null!;
        public int Sleeping;
        public long AllocatedBytes; // GC.GetAllocatedBytesForCurrentThread() sampled by the worker after each wave
        // Kernel auto-reset event: parking on ManualResetEventSlim (Monitor.Wait) allocated managed waiter objects.
        public readonly AutoResetEvent Event = new(false);
    }

    /// <summary>Sum of managed bytes ever allocated by worker threads (sampled at the end of their last wave).</summary>
    public long WorkerAllocatedBytes()
    {
        long s = 0;
        foreach (var w in _workers) s += Volatile.Read(ref w.AllocatedBytes);
        return s;
    }

    private readonly Worker[] _workers;
    private readonly Action<int, int> _run;
    private int _count;
    private int _next;
    private int _active;
    private int _generation;
    private volatile bool _stop;
    private Exception? _error;
    private readonly long _spinTicks = Environment.GetEnvironmentVariable("COLONY_SPIN_US") is { } us
        ? Stopwatch.Frequency * long.Parse(us) / 1_000_000
        : Stopwatch.Frequency / 5000; // ~200 us default idle spin before parking

    public WorkerPool(int workers, Action<int, int> run)
    {
        _run = run;
        _workers = new Worker[workers];
        for (int i = 0; i < workers; i++)
        {
            var w = new Worker();
            int threadIndex = i + 1;
            w.Thread = new Thread(() => Loop(w, threadIndex)) { IsBackground = true, Name = $"colony-worker-{threadIndex}" };
            _workers[i] = w;
            w.Thread.Start();
        }
    }

    public void Run(int count)
    {
        if (count == 0) return;
        if (_workers.Length == 0 || count == 1)
        {
            for (int i = 0; i < count; i++) _run(i, 0);
            return;
        }
        _count = count;
        _next = 0;
        _error = null;
        Volatile.Write(ref _active, _workers.Length);
        Interlocked.Increment(ref _generation);
        foreach (var w in _workers)
            if (Volatile.Read(ref w.Sleeping) == 1) w.Event.Set();
        Drain(0);
        var sw = new SpinWait();
        while (Volatile.Read(ref _active) != 0) sw.SpinOnce(-1);
        if (_error is not null) throw new AggregateException(_error);
    }

    private void Drain(int thread)
    {
        while (true)
        {
            int i = Interlocked.Increment(ref _next) - 1;
            if (i >= _count) return;
            _run(i, thread);
        }
    }

    private void Loop(Worker w, int threadIndex)
    {
        int seen = 0;
        while (true)
        {
            long spinStart = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref _generation) == seen)
            {
                if (Stopwatch.GetTimestamp() - spinStart < _spinTicks)
                {
                    Thread.SpinWait(20);
                    continue;
                }
                Interlocked.Exchange(ref w.Sleeping, 1);
                if (Volatile.Read(ref _generation) != seen)
                {
                    Volatile.Write(ref w.Sleeping, 0);
                    break;
                }
                w.Event.WaitOne();
                Volatile.Write(ref w.Sleeping, 0);
                spinStart = Stopwatch.GetTimestamp();
            }
            if (_stop) return;
            seen = Volatile.Read(ref _generation);
            try { Drain(threadIndex); }
            catch (Exception ex) { _error = ex; }
            w.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            Interlocked.Decrement(ref _active);
        }
    }

    public void Dispose()
    {
        _stop = true;
        Interlocked.Increment(ref _generation);
        foreach (var w in _workers) w.Event.Set();
        foreach (var w in _workers) w.Thread.Join();
    }
}
