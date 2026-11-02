using System.Diagnostics;
using Colony.Diagnostics;

namespace Colony;

/// <summary>
/// The Framework's fixed work-stealing pool (spec section 4.3): <c>N - 1</c> dedicated threads plus the
/// calling thread run the jobs of one wave. Job <c>i</c> belongs to worker <c>i mod N</c> (chunk affinity,
/// so a chunk tends to meet the same core every Tick); a worker that runs out steals from the others.
/// Idle workers spin briefly, then park on a kernel event, which allocates nothing. Which worker runs a
/// job never influences results: jobs are keyed by index, not thread. With <see cref="Timing"/> on, each
/// worker accounts its busy, spinning and parked time in its <see cref="WorkerClock"/> (spec section 10.1).
/// </summary>
internal sealed class WorkerPool : IDisposable
{
    private const int Pad = 16; // ints per cursor slot: 64 bytes, one cache line
    private const int DefaultSpinMicroseconds = 200;

    private readonly Worker[] _workers;
    private readonly int[] _cursors;
    private readonly long _spinTicks;
    private IJobSource? _source;
    private int _jobCount;
    private int _active;
    private int _generation;
    private int _running;
    private volatile bool _stop;

    public WorkerPool(int workerCount, bool timing = false)
    {
        WorkerCount = Math.Max(1, workerCount);
        Timing = timing;
        _cursors = new int[WorkerCount * Pad];
        _spinTicks = Stopwatch.Frequency * DefaultSpinMicroseconds / 1_000_000;
        Clocks = new WorkerClock[WorkerCount];
        for (int i = 0; i < Clocks.Length; i++)
        {
            Clocks[i] = new WorkerClock();
        }

        _workers = new Worker[WorkerCount - 1];
        for (int i = 0; i < _workers.Length; i++)
        {
            var worker = new Worker();
            int index = i + 1;
            worker.Thread = new Thread(() => Loop(worker, index))
            {
                IsBackground = true,
                Name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"colony-worker-{index}"),
            };
            _workers[i] = worker;
            worker.Thread.Start();
        }
    }

    /// <summary>A source of indexed jobs; <see cref="Run"/> is called from any worker, concurrently.</summary>
    public interface IJobSource
    {
        public void Run(int job, int worker);
    }

    /// <summary>Workers including the calling thread (worker 0).</summary>
    public int WorkerCount { get; }

    /// <summary>Whether workers account their time; fixed for the pool's lifetime.</summary>
    public bool Timing { get; }

    /// <summary>Cumulative time per worker (index 0 is the calling thread), valid while <see cref="Timing"/> is on.</summary>
    public WorkerClock[] Clocks { get; }

    /// <summary>Managed bytes ever allocated by the dedicated worker threads, sampled after each wave they took part in.</summary>
    public long WorkerAllocatedBytes()
    {
        long total = 0;
        foreach (var worker in _workers)
        {
            total += Volatile.Read(ref worker.AllocatedBytes);
        }

        return total;
    }

    /// <summary>
    /// Runs jobs 0 to <paramref name="jobCount"/> - 1 of <paramref name="source"/> and returns when all have
    /// finished. One run at a time: a nested call (from a job, on any thread) throws instead of deadlocking.
    /// </summary>
    public void Run(int jobCount, IJobSource source)
    {
        if (jobCount <= 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("The worker pool is already running a wave; parallel jobs cannot be started from inside a job.");
        }

        try
        {
            RunCore(jobCount, source);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void RunCore(int jobCount, IJobSource source)
    {
        var clock = Clocks[0];
        if (_workers.Length == 0 || jobCount == 1)
        {
            long t0 = Timing ? Stopwatch.GetTimestamp() : 0;
            for (int i = 0; i < jobCount; i++)
            {
                source.Run(i, 0);
            }

            if (Timing)
            {
                clock.Busy += Stopwatch.GetTimestamp() - t0;
            }

            return;
        }

        _jobCount = jobCount;
        for (int w = 0; w < WorkerCount; w++)
        {
            _cursors[w * Pad] = 0;
        }

        _source = source;
        Volatile.Write(ref _active, _workers.Length);
        Interlocked.Increment(ref _generation);
        foreach (var worker in _workers)
        {
            if (Volatile.Read(ref worker.Sleeping) == 1)
            {
                worker.Event.Set();
            }
        }

        long start = Timing ? Stopwatch.GetTimestamp() : 0;
        Drain(0);
        long drained = Timing ? Stopwatch.GetTimestamp() : 0;
        var spin = new SpinWait();
        while (Volatile.Read(ref _active) != 0)
        {
            spin.SpinOnce(-1);
        }

        if (Timing)
        {
            clock.Busy += drained - start;
            clock.Spin += Stopwatch.GetTimestamp() - drained;
        }
    }

    private void Drain(int worker)
    {
        var source = _source!;
        int count = _jobCount;
        int n = WorkerCount;
        for (int step = 0; step < n; step++)
        {
            int owner = (worker + step) % n; // own stripe first, then steal round-robin
            while (true)
            {
                int k = Interlocked.Increment(ref _cursors[owner * Pad]) - 1;
                int job = owner + (k * n);
                if (job >= count)
                {
                    break;
                }

                source.Run(job, worker);
            }
        }
    }

    private void Loop(Worker worker, int index)
    {
        int seen = 0;
        var clock = Clocks[index];
        bool timing = Timing;
        // The first wait on a thread allocates its wait state once (observed on Linux). Pay it here, not in the first
        // Tick whose spin window a loaded machine overruns, so a steady-state Tick never allocates on a worker.
        worker.Event.WaitOne(0);
        while (true)
        {
            long spinStart = Stopwatch.GetTimestamp();
            long spun = 0;
            long parked = 0;
            while (Volatile.Read(ref _generation) == seen)
            {
                if (Stopwatch.GetTimestamp() - spinStart < _spinTicks)
                {
                    Thread.SpinWait(20);
                    continue;
                }

                Interlocked.Exchange(ref worker.Sleeping, 1);
                if (Volatile.Read(ref _generation) != seen)
                {
                    Volatile.Write(ref worker.Sleeping, 0);
                    break;
                }

                long parkStart = Stopwatch.GetTimestamp();
                spun += parkStart - spinStart;
                worker.Event.WaitOne();
                Volatile.Write(ref worker.Sleeping, 0);
                spinStart = Stopwatch.GetTimestamp();
                parked += spinStart - parkStart;
            }

            if (_stop)
            {
                return;
            }

            long busyStart = Stopwatch.GetTimestamp();
            spun += busyStart - spinStart;
            seen = Volatile.Read(ref _generation);
            Drain(index);
            if (timing)
            {
                clock.Busy += Stopwatch.GetTimestamp() - busyStart;
                clock.Spin += spun;
                clock.Park += parked;
            }

            worker.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            Interlocked.Decrement(ref _active);
        }
    }

    public void Dispose()
    {
        _stop = true;
        Interlocked.Increment(ref _generation);
        foreach (var worker in _workers)
        {
            worker.Event.Set();
        }

        foreach (var worker in _workers)
        {
            worker.Thread.Join();
            worker.Dispose();
        }
    }

    private sealed class Worker : IDisposable
    {
        // A kernel auto-reset event: parking on ManualResetEventSlim allocated Monitor waiter objects (prototype finding).
        public readonly AutoResetEvent Event = new(false);
        public Thread Thread = null!;
        public int Sleeping;
        public long AllocatedBytes;

        public void Dispose() => Event.Dispose();
    }
}
