using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GpuPath.Bench;

/// <summary>Tracked unmanaged allocations (so memory footprint is reported per component).</summary>
public static unsafe class Mem
{
    public static long Live;
    public static T* Alloc<T>(long count, bool zero = true) where T : unmanaged
    {
        nuint bytes = (nuint)(count * sizeof(T)) + 64;
        void* p = zero ? NativeMemory.AlignedAlloc(bytes, 64) : NativeMemory.AlignedAlloc(bytes, 64);
        if (zero) NativeMemory.Clear(p, bytes);
        Interlocked.Add(ref Live, (long)bytes);
        return (T*)p;
    }
    public static void Free<T>(T* p, long count) where T : unmanaged
    {
        if (p == null) return;
        NativeMemory.AlignedFree(p);
        Interlocked.Add(ref Live, -(count * sizeof(T) + 64));
    }
    public static void Fill(ushort* p, long count, ushort v) => new Span<ushort>(p, checked((int)count)).Fill(v);
    public static void FillL(ushort* p, long count, ushort v) { for (long o = 0; o < count; o += 1 << 30) Fill(p + o, Math.Min(1 << 30, count - o), v); }
}

/// <summary>Deterministic RNG (SplitMix64).</summary>
public struct Rng(ulong seed)
{
    ulong _s = seed;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Next() { ulong z = _s += 0x9E3779B97F4A7C15UL; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; return z ^ (z >> 31); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Int(int n) => (int)((Next() >> 33) % (ulong)n);
    public double Dbl() => (Next() >> 11) * (1.0 / (1UL << 53));
}

public interface IParallelJob { void Execute(int index, int worker); }

/// <summary>
/// Persistent worker threads. Run(job, count, threads) executes job.Execute(i, worker) for i in [0,count) on up to
/// `threads` threads (caller thread is worker 0). No managed allocation per Run.
/// </summary>
public sealed class WorkerPool : IDisposable
{
    public readonly int Threads;
    readonly SemaphoreSlim _start = new(0);
    readonly ManualResetEventSlim _done = new(false, 2000);
    IParallelJob? _job;
    int _count, _next, _remaining, _nextWorker;
    volatile bool _stop;

    public WorkerPool(int threads)
    {
        Threads = threads;
        for (int i = 1; i < threads; i++)
        {
            var t = new Thread(Loop) { IsBackground = true, Name = $"pf-worker-{i}" };
            t.Start();
        }
    }

    void Loop()
    {
        while (true)
        {
            _start.Wait();
            if (_stop) return;
            int w = Interlocked.Increment(ref _nextWorker);
            Work(w);
            if (Interlocked.Decrement(ref _remaining) == 0) _done.Set();
        }
    }

    void Work(int w)
    {
        var job = _job!;
        int i;
        while ((i = Interlocked.Increment(ref _next) - 1) < _count) job.Execute(i, w);
    }

    public void Run(IParallelJob job, int count, int threads)
    {
        if (count <= 0) return;
        int helpers = Math.Min(Math.Min(threads, Threads), count) - 1;
        _job = job; _count = count; _next = 0; _nextWorker = 0;
        if (helpers > 0) { _remaining = helpers; _done.Reset(); _start.Release(helpers); }
        Work(0);
        if (helpers > 0) _done.Wait();
        _job = null;
    }

    public void Dispose() { _stop = true; _start.Release(Threads); }
}
