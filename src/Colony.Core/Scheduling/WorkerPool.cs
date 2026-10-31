using System.Diagnostics;

namespace Colony;

/// <summary>
/// The Framework's fixed work-stealing pool (spec section 4.3): <c>N - 1</c> dedicated threads plus the
/// calling thread run the jobs of one wave. Job <c>i</c> belongs to worker <c>i mod N</c> (chunk affinity,
/// so a chunk tends to meet the same core every Tick); a worker that runs out steals from the others.
/// Idle workers spin briefly, then park on a kernel event, which allocates nothing. Which worker runs a
/// job never influences results: jobs are keyed by index, not thread.
/// </summary>
internal sealed class WorkerPool : IDisposable
{
    private const int Pad = 16; // ints per cursor slot: 64 bytes, one cache line
    private const int DefaultSpinMicroseconds = 200;

    private readonly Worker[] _workers;
    private readonly IJobSource _source;
    private readonly int[] _cursors;
    private readonly long _spinTicks;
    private int _jobCount;
    private int _active;
    private int _generation;
    private volatile bool _stop;

    public WorkerPool(int workerCount, IJobSource source)
    {
        WorkerCount = Math.Max(1, workerCount);
        _source = source;
        _cursors = new int[WorkerCount * Pad];
        _spinTicks = Stopwatch.Frequency * DefaultSpinMicroseconds / 1_000_000;
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

    /// <summary>Runs jobs 0 to <paramref name="jobCount"/> - 1 and returns when all have finished.</summary>
    public void Run(int jobCount)
    {
        if (jobCount <= 0)
        {
            return;
        }

        if (_workers.Length == 0 || jobCount == 1)
        {
            for (int i = 0; i < jobCount; i++)
            {
                _source.Run(i, 0);
            }

            return;
        }

        _jobCount = jobCount;
        for (int w = 0; w < WorkerCount; w++)
        {
            _cursors[w * Pad] = 0;
        }

        Volatile.Write(ref _active, _workers.Length);
        Interlocked.Increment(ref _generation);
        foreach (var worker in _workers)
        {
            if (Volatile.Read(ref worker.Sleeping) == 1)
            {
                worker.Event.Set();
            }
        }

        Drain(0);
        var spin = new SpinWait();
        while (Volatile.Read(ref _active) != 0)
        {
            spin.SpinOnce(-1);
        }
    }

    private void Drain(int worker)
    {
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

                _source.Run(job, worker);
            }
        }
    }

    private void Loop(Worker worker, int index)
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

                Interlocked.Exchange(ref worker.Sleeping, 1);
                if (Volatile.Read(ref _generation) != seen)
                {
                    Volatile.Write(ref worker.Sleeping, 0);
                    break;
                }

                worker.Event.WaitOne();
                Volatile.Write(ref worker.Sleeping, 0);
                spinStart = Stopwatch.GetTimestamp();
            }

            if (_stop)
            {
                return;
            }

            seen = Volatile.Read(ref _generation);
            Drain(index);
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
