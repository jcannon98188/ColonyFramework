using System.Diagnostics;
using GpuPath.Backend;
using GpuPath.Bench;
using GpuPath.Core;
using GpuPath.Cpu;

namespace GpuPath.Gpu;

/// <summary>
/// (5) GPU flow fields: the GPU is authoritative for the distance Fields (repaired on the GPU from uploaded toggle
/// lists) and for unit positions of the units it owns (move Kernel). The CPU keeps World + Regions (fail-fast region
/// check at request time), uploads per-unit field assignments and reads positions + arrival/repath status back.
/// Deferred = the move submitted in Tick T is applied at the start of Tick T+1 (one Tick of latency, CPU never
/// waits for the move unless the GPU is late). Field repair is always synchronous (convergence needs readbacks).
/// </summary>
public sealed unsafe class GpuFlowService : Service
{
    public readonly GpuFlow G;
    public readonly bool Deferred;
    readonly int* _gf;
    IFence? _pending;
    long _unreach, _planned;
    public override string Name => Deferred ? "gpu-flow-deferred" : "gpu-flow";
    // per-Tick timings (ms)
    public double RepairMs, UploadMs, SubmitMs, WaitMs, ApplyMs;
    public bool LastWasReady;
    public int RepairIters, RepairInvalIters, RepairSubmits, RepairDispatches;

    public GpuFlowService(World w, Regions r, Units u, WorkerPool pool, int threads, GpuFlow g, bool deferred) : base(w, r, u, pool, threads)
    {
        G = g; Deferred = deferred;
        _gf = Mem.Alloc<int>(u.Count);
        g.BindUnits(u.Count);
        g.UploadPositions(u.Pos, u.Count);
    }

    public override long Bytes => base.Bytes + U.Count * 4L;

    public override void OnToggles(int* tog, int n)
    {
        long t0 = Stopwatch.GetTimestamp();
        G.Repair(tog, n);
        RepairMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        RepairIters = G.LastIterations; RepairInvalIters = G.LastInvalIterations; RepairSubmits = G.LastSubmits; RepairDispatches = G.LastDispatches;
    }

    protected override void BeforeProcess(int n) { _unreach = _planned = 0; }
    protected override void AfterProcess() { Unreachable = _unreach; Planned = _planned; }

    protected override void ProcessPart(int p, int worker, Request* req, int n)
    {
        int un = 0;
        for (int i = 0; i < n; i++)
        {
            int u = req[i].Unit;
            U.Goal[u] = req[i].Goal; U.Field[u] = req[i].Field;
            if (req[i].Field < 0) throw new InvalidOperationException("flow fields need a shared goal");
            if (!Reachable(U.Pos[u], req[i].Goal)) { U.Status[u] = St.Unreachable; un++; continue; }
            U.Status[u] = St.Moving;
        }
        Interlocked.Add(ref _unreach, un); Interlocked.Add(ref _planned, n - un);
    }

    protected override void MovePart(int p, int worker, int u0, int u1) => throw new NotSupportedException();

    public override void BeginTick()
    {
        if (Deferred && _pending != null) Complete();
    }

    void Complete()
    {
        long t0 = Stopwatch.GetTimestamp();
        LastWasReady = _pending!.IsComplete;
        _pending.Wait();
        _pending = null;
        var m = G.MapUnits();
        long t1 = Stopwatch.GetTimestamp();
        int a = Math.Max(0, From), b = Math.Min(To, U.Count);
        for (int u = a; u < b; u++)
        {
            uint v = m[u];
            U.Pos[u] = (int)(v & 0x0FFFFFFFu);
            if (U.Status[u] != St.Moving) continue;
            uint st = v >> 28;
            if (st == 1) U.Status[u] = St.Arrived; else if (st == 2) U.Status[u] = St.Repath;
        }
        G.UnmapUnits();
        WaitMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        ApplyMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
    }

    public override void Move()
    {
        long t0 = Stopwatch.GetTimestamp();
        int a = Math.Max(0, From), b = Math.Min(To, U.Count);
        for (int u = 0; u < U.Count; u++) _gf[u] = -1;
        for (int u = a; u < b; u++) _gf[u] = U.Status[u] == St.Moving ? U.Field[u] : -1;
        G.UploadFields(_gf, U.Count);
        long t1 = Stopwatch.GetTimestamp();
        _pending = G.SubmitMove(true);
        long t2 = Stopwatch.GetTimestamp();
        UploadMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        SubmitMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
        if (!Deferred) Complete();
    }

    /// <summary>Waits for an outstanding deferred move (end of a run).</summary>
    public void Drain() { if (_pending != null) Complete(); }

    public override void Dispose() { Drain(); base.Dispose(); Mem.Free(_gf, U.Count); }
}

/// <summary>
/// (7) Hybrid: shared-goal units [0, split) use a flow-field service, unique-goal units [split, n) use HPA*.
/// With overlap = true the shared service's Field repair runs on a separate thread while the CPU workers plan
/// HPA* requests (used for the GPU variant, whose repair thread mostly waits on fences).
/// </summary>
public sealed unsafe class HybridService : Service
{
    readonly Service _shared, _unique;
    readonly bool _overlap;
    readonly int _split;
    readonly Request* _a, _b;
    Task? _repair;
    readonly string _name;
    public override string Name => _name;
    public double SharedMs, UniqueMs, JoinWaitMs;

    public HybridService(World w, Regions r, Units u, WorkerPool pool, int threads, Service shared, Service unique, int split, bool overlap, string name) : base(w, r, u, pool, threads)
    {
        _shared = shared; _unique = unique; _split = split; _overlap = overlap; _name = name;
        shared.From = 0; shared.To = split; unique.From = split; unique.To = u.Count;
        _a = Mem.Alloc<Request>(u.Count); _b = Mem.Alloc<Request>(u.Count);
    }

    public override long Bytes => _shared.Bytes + _unique.Bytes;
    public override void BeginTick() { _shared.BeginTick(); _unique.BeginTick(); }

    int* _tog; int _n;
    public override void OnToggles(int* tog, int n)
    {
        _tog = tog; _n = n;
        if (_overlap) _repair = Task.Factory.StartNew(() => _shared.OnToggles(_tog, _n), TaskCreationOptions.LongRunning);
        else _shared.OnToggles(tog, n);
        _unique.OnToggles(tog, n);
    }

    public override void Process(Request* req, int n)
    {
        int na = 0, nb = 0;
        for (int i = 0; i < n; i++) { if (req[i].Unit < _split) _a[na++] = req[i]; else _b[nb++] = req[i]; }
        long t0 = Stopwatch.GetTimestamp();
        _unique.Process(_b, nb);
        long t1 = Stopwatch.GetTimestamp();
        if (_repair != null) { _repair.Wait(); _repair = null; }
        long t2 = Stopwatch.GetTimestamp();
        _shared.Process(_a, na);
        UniqueMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        JoinWaitMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
        Unreachable = _shared.Unreachable + _unique.Unreachable;
        Planned = _shared.Planned + _unique.Planned;
        Failed = _shared.Failed + _unique.Failed;
    }

    protected override void ProcessPart(int p, int worker, Request* req, int n) => throw new NotSupportedException();
    protected override void MovePart(int p, int worker, int u0, int u1) => throw new NotSupportedException();

    public override void Move()
    {
        long t0 = Stopwatch.GetTimestamp();
        _shared.Move();   // GPU: upload + submit (deferred) or full move (sync / CPU)
        long t1 = Stopwatch.GetTimestamp();
        _unique.Move();
        SharedMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
    }

    public override void Dispose() { base.Dispose(); Mem.Free(_a, U.Count); Mem.Free(_b, U.Count); }
}
