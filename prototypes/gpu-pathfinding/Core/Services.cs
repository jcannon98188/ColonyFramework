using GpuPath.Bench;
using GpuPath.Cpu;

namespace GpuPath.Core;

public static class St
{
    public const byte Idle = 0, Moving = 1, Arrived = 2, Repath = 3, Unreachable = 4, NeedRefine = 5;
}

/// <summary>Unit (Entity) pathing state, SoA in unmanaged memory.</summary>
public sealed unsafe class Units : IDisposable
{
    public readonly int Count;
    public readonly int* Pos, Goal, Field;
    public readonly byte* Status;
    public Units(int n) { Count = n; Pos = Mem.Alloc<int>(n); Goal = Mem.Alloc<int>(n); Field = Mem.Alloc<int>(n); Status = Mem.Alloc<byte>(n); }
    public long Bytes => Count * 13L;
    public void Dispose() { Mem.Free(Pos, Count); Mem.Free(Goal, Count); Mem.Free(Field, Count); Mem.Free(Status, Count); }
}

public struct Request { public int Unit, Goal, Field; }

/// <summary>
/// The pathfinding service under test. Per Tick the harness calls OnToggles (after World + Regions were updated),
/// Process (new path requests), Move (every unit advances one step). Unit partitions (contiguous ranges) are the unit
/// of parallel work and each owns its path storage, so no locks are needed.
/// </summary>
public abstract unsafe class Service : IDisposable
{
    public const int Parts = 64;
    protected readonly World W; protected readonly Regions R; protected readonly Units U;
    protected readonly WorkerPool Pool;
    public int Threads;
    protected readonly int PartSize;
    // request buckets per partition
    readonly Request* _buckets; readonly int* _bStart, _bCount;
    public long Unreachable, Planned, Failed;

    protected Service(World w, Regions r, Units u, WorkerPool pool, int threads)
    {
        W = w; R = r; U = u; Pool = pool; Threads = threads;
        PartSize = (u.Count + Parts - 1) / Parts;
        _buckets = Mem.Alloc<Request>(u.Count);
        _bStart = Mem.Alloc<int>(Parts + 1); _bCount = Mem.Alloc<int>(Parts);
        _proc = new ProcJob(this); _move = new MoveJob(this);
    }

    public abstract string Name { get; }
    public virtual void OnToggles(int* tog, int n) { }
    /// <summary>Called first in every Tick (deferred GPU completion is applied here).</summary>
    public virtual void BeginTick() { }
    /// <summary>Unit range this service owns for Move / continuation work (hybrids split the units).</summary>
    public int From = 0, To = int.MaxValue;
    public virtual long Bytes => U.Count * (long)sizeof(Request);

    public virtual void Process(Request* req, int n)
    {
        for (int p = 0; p < Parts; p++) _bCount[p] = 0;
        for (int i = 0; i < n; i++) _bCount[req[i].Unit / PartSize]++;
        _bStart[0] = 0;
        for (int p = 0; p < Parts; p++) { _bStart[p + 1] = _bStart[p] + _bCount[p]; _bCount[p] = 0; }
        for (int i = 0; i < n; i++) { int p = req[i].Unit / PartSize; _buckets[_bStart[p] + _bCount[p]++] = req[i]; }
        BeforeProcess(n);
        Pool.Run(_proc, Parts, Threads);
        AfterProcess();
    }

    protected virtual void BeforeProcess(int n) { }
    protected virtual void AfterProcess() { }

    /// <summary>Handle requests of partition p (and any continuation work, e.g. HPA* refinement).</summary>
    protected abstract void ProcessPart(int p, int worker, Request* req, int n);
    protected abstract void MovePart(int p, int worker, int u0, int u1);

    public virtual void Move() => Pool.Run(_move, Parts, Threads);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected bool Reachable(int from, int to)
    {
        int a = R.RegionOf(from);
        return a >= 0 && a == R.RegionOf(to);
    }

    sealed class ProcJob(Service s) : IParallelJob { public void Execute(int p, int w) => s.ProcessPart(p, w, s._buckets + s._bStart[p], s._bStart[p + 1] - s._bStart[p]); }
    sealed class MoveJob(Service s) : IParallelJob { public void Execute(int p, int w) { int a = Math.Max(s.From, p * s.PartSize), b = Math.Min(Math.Min(s.To, s.U.Count), p * s.PartSize + s.PartSize); if (a < b) s.MovePart(p, w, a, b); } }
    readonly ProcJob _proc; readonly MoveJob _move;

    public virtual void Dispose() { Mem.Free(_buckets, U.Count); Mem.Free(_bStart, Parts + 1); Mem.Free(_bCount, Parts); }
}

/// <summary>(2) Region-ID early-out + plain tile A* per request, full path stored per unit.</summary>
public sealed unsafe class AStarService : Service
{
    readonly AStar[] _a;
    readonly BlockPool[] _pools;
    readonly PStream* _path;
    long _unreach, _planned, _failed;
    public override string Name => "cpu-astar";

    public AStarService(World w, Regions r, Units u, WorkerPool pool, int threads, int pageSlots, int maxExp) : base(w, r, u, pool, threads)
    {
        _a = new AStar[pool.Threads];
        for (int i = 0; i < _a.Length; i++) _a[i] = new AStar(w, pageSlots, maxExp);
        _pools = new BlockPool[Parts];
        for (int i = 0; i < Parts; i++) _pools[i] = new BlockPool(256);
        _path = Mem.Alloc<PStream>(u.Count);
        for (int i = 0; i < u.Count; i++) _path[i] = PStream.Empty;
    }

    public long Expanded { get { long e = 0; foreach (var a in _a) e += a.Expanded; return e; } }
    public override long Bytes { get { long b = base.Bytes + U.Count * 12L; foreach (var a in _a) b += a.Bytes; foreach (var p in _pools) b += p.Bytes; return b; } }

    protected override void ProcessPart(int p, int worker, Request* req, int n)
    {
        var a = _a[worker];
        ref var pool = ref _pools[p];
        int un = 0, pl = 0, fl = 0;
        for (int i = 0; i < n; i++)
        {
            int u = req[i].Unit, g = req[i].Goal;
            U.Goal[u] = g; U.Field[u] = req[i].Field;
            int pos = U.Pos[u];
            if (!Reachable(pos, g)) { pool.Clear(ref _path[u]); U.Status[u] = St.Unreachable; un++; continue; }
            int len = a.Find(pos, g);
            if (len < 0) { pool.Clear(ref _path[u]); U.Status[u] = St.Unreachable; fl++; continue; }
            pool.Write(ref _path[u], a.Dirs.P, len, 1);
            U.Status[u] = len == 0 ? St.Arrived : St.Moving;
            pl++;
        }
        Interlocked.Add(ref _unreach, un); Interlocked.Add(ref _planned, pl); Interlocked.Add(ref _failed, fl);
    }

    protected override void AfterProcess() { Unreachable = _unreach; Planned = _planned; Failed = _failed; }

    protected override void MovePart(int p, int worker, int u0, int u1)
    {
        ref var pool = ref _pools[p];
        for (int u = u0; u < u1; u++)
        {
            if (U.Status[u] != St.Moving) continue;
            ref var s = ref _path[u];
            int pos = U.Pos[u];
            if (s.Count == 0) { U.Status[u] = pos == U.Goal[u] ? St.Arrived : St.Repath; continue; }
            int d = *pool.Peek(ref s, 0, 1);
            int nx = W.Nb(pos, d);
            if (nx < 0) { U.Status[u] = St.Repath; continue; } // blocked by a toggle: re-plan next Tick
            pool.Pop(ref s, 1);
            U.Pos[u] = nx;
            if (s.Count == 0) U.Status[u] = nx == U.Goal[u] ? St.Arrived : St.Repath;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var a in _a) a.Dispose();
        for (int i = 0; i < Parts; i++) _pools[i].Free();
        Mem.Free(_path, U.Count);
    }
}

/// <summary>(3) HPA*-style: abstract path over chunk components, refined a few nodes at a time.</summary>
public sealed unsafe class HpaService : Service
{
    public const int Window = 4;
    readonly Hpa[] _h;
    readonly BlockPool[] _dirPools, _nodePools;
    readonly PStream* _dirs, _nodes;
    long _unreach, _planned, _failed, _refines, _replans;
    public long Refines, Replans;
    public override string Name => "cpu-hpa";

    public HpaService(World w, Regions r, Units u, WorkerPool pool, int threads, int pageSlots) : base(w, r, u, pool, threads)
    {
        _h = new Hpa[pool.Threads];
        for (int i = 0; i < _h.Length; i++) _h[i] = new Hpa(w, r, pageSlots);
        _dirPools = new BlockPool[Parts]; _nodePools = new BlockPool[Parts];
        for (int i = 0; i < Parts; i++) { _dirPools[i] = new BlockPool(64); _nodePools[i] = new BlockPool(64); }
        _dirs = Mem.Alloc<PStream>(u.Count); _nodes = Mem.Alloc<PStream>(u.Count);
        for (int i = 0; i < u.Count; i++) { _dirs[i] = PStream.Empty; _nodes[i] = PStream.Empty; }
    }

    public override long Bytes { get { long b = base.Bytes + U.Count * 24L; foreach (var h in _h) b += h.Bytes; for (int i = 0; i < Parts; i++) b += _dirPools[i].Bytes + _nodePools[i].Bytes; return b; } }

    bool PlanAbstract(Hpa h, ref BlockPool np, int u)
    {
        if (!h.Abstract(U.Pos[u], U.Goal[u])) return false;
        int n = (int)h.NodePath.Len;
        // encode (version << 32 | chunk << 10 | label): node ids are renumbered on graph rebuilds, chunk+label+version are stable
        h.Enc.Clear();
        for (int i = 0; i < n; i++)
        {
            int node = h.NodePath.P[i];
            int c = R.NodeChunk[node], l = node - R.NodeBase[c];
            h.Enc.Push(((long)R.Version[c] << 32) | (uint)((c << 10) | l));
        }
        np.Write(ref _nodes[u], (byte*)h.Enc.P, n, 8);
        return true;
    }

    /// <summary>Refine the next window for unit u. Returns false if the unit should re-plan.</summary>
    bool RefineNext(Hpa h, ref BlockPool np, ref BlockPool dp, int u)
    {
        ref var ns = ref _nodes[u];
        int pos = U.Pos[u];
        int curNode = R.NodeOf(pos);
        if (curNode < 0) return false;
        int cc = R.NodeChunk[curNode], cl = curNode - R.NodeBase[cc];
        int curCode = (cc << 10) | cl;
        // drop nodes already passed
        int drop = 0;
        while (ns.Count > 0 && drop < 8)
        {
            long e = *(long*)np.Peek(ref ns, 0, 8);
            if ((int)(uint)e == curCode && (int)(e >> 32) == R.Version[cc]) break;
            np.Pop(ref ns, 8); drop++;
        }
        if (ns.Count == 0) return false;
        int k = Math.Min(Window, ns.Count);
        int* ids = stackalloc int[Window];
        for (int i = 0; i < k; i++)
        {
            long e = *(long*)np.Peek(ref ns, i, 8);
            int code = (int)(uint)e, ver = (int)(e >> 32);
            int c = code >> 10, l = code & 1023;
            if (R.Version[c] != ver || l >= R.CompCount[c]) return false; // chunk changed since planning
            ids[i] = R.NodeBase[c] + l;
        }
        int len = h.Refine(pos, ids, k, k == ns.Count ? U.Goal[u] : -1);
        if (len < 0) return false;
        dp.Write(ref _dirs[u], h.Dirs.P, len, 1);
        return true;
    }

    protected override void ProcessPart(int p, int worker, Request* req, int n)
    {
        var h = _h[worker];
        ref var np = ref _nodePools[p]; ref var dp = ref _dirPools[p];
        int un = 0, pl = 0, fl = 0, rf = 0, rp = 0;
        for (int i = 0; i < n; i++)
        {
            int u = req[i].Unit, g = req[i].Goal;
            U.Goal[u] = g; U.Field[u] = req[i].Field;
            int pos = U.Pos[u];
            dp.Clear(ref _dirs[u]); np.Clear(ref _nodes[u]);
            if (!Reachable(pos, g)) { U.Status[u] = St.Unreachable; un++; continue; }
            if (pos == g) { U.Status[u] = St.Arrived; pl++; continue; }
            if (!PlanAbstract(h, ref np, u) || !RefineNext(h, ref np, ref dp, u)) { U.Status[u] = St.Unreachable; fl++; continue; }
            U.Status[u] = St.Moving; pl++;
        }
        // continuation: units whose refined segment ran out
        int a = Math.Max(From, p * PartSize), b = Math.Min(Math.Min(To, U.Count), p * PartSize + PartSize);
        for (int u = a; u < b; u++)
        {
            if (U.Status[u] != St.NeedRefine) continue;
            rf++;
            if (RefineNext(h, ref np, ref dp, u)) { U.Status[u] = St.Moving; continue; }
            rp++;
            if (Reachable(U.Pos[u], U.Goal[u]) && PlanAbstract(h, ref np, u) && RefineNext(h, ref np, ref dp, u)) { U.Status[u] = St.Moving; continue; }
            np.Clear(ref _nodes[u]); dp.Clear(ref _dirs[u]);
            U.Status[u] = St.Unreachable; fl++;
        }
        Interlocked.Add(ref _unreach, un); Interlocked.Add(ref _planned, pl); Interlocked.Add(ref _failed, fl);
        Interlocked.Add(ref _refines, rf); Interlocked.Add(ref _replans, rp);
    }

    protected override void BeforeProcess(int n) { _unreach = _planned = _failed = _refines = _replans = 0; }
    protected override void AfterProcess() { Unreachable = _unreach; Planned = _planned; Failed = _failed; Refines = _refines; Replans = _replans; }

    protected override void MovePart(int p, int worker, int u0, int u1)
    {
        ref var dp = ref _dirPools[p];
        for (int u = u0; u < u1; u++)
        {
            if (U.Status[u] != St.Moving) continue;
            ref var s = ref _dirs[u];
            int pos = U.Pos[u];
            if (s.Count == 0) { U.Status[u] = pos == U.Goal[u] ? St.Arrived : St.NeedRefine; continue; }
            int d = *dp.Peek(ref s, 0, 1);
            int nx = W.Nb(pos, d);
            if (nx < 0) { U.Status[u] = St.NeedRefine; dp.Clear(ref s); continue; }
            dp.Pop(ref s, 1);
            U.Pos[u] = nx;
            if (s.Count == 0) U.Status[u] = nx == U.Goal[u] ? St.Arrived : St.NeedRefine;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var h in _h) h.Dispose();
        for (int i = 0; i < Parts; i++) { _dirPools[i].Free(); _nodePools[i].Free(); }
        Mem.Free(_dirs, U.Count); Mem.Free(_nodes, U.Count);
    }
}

/// <summary>(4) CPU flow fields for shared goals: assignment is O(1) (region check), Move descends the gradient.</summary>
public sealed unsafe class FlowService : Service
{
    public readonly CpuFlowFields F;
    long _unreach, _planned;
    public override string Name => "cpu-flow";

    public FlowService(World w, Regions r, Units u, WorkerPool pool, int threads, CpuFlowFields f) : base(w, r, u, pool, threads) { F = f; }
    public override long Bytes => base.Bytes + F.FieldBytes + F.ScratchBytes;

    public override void OnToggles(int* tog, int n) => F.RepairAll(tog, n, Pool, Threads);

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

    protected override void MovePart(int p, int worker, int u0, int u1)
    {
        for (int u = u0; u < u1; u++)
        {
            if (U.Status[u] != St.Moving) continue;
            int f = U.Field[u], pos = U.Pos[u];
            int d = F.F[f][pos];
            if (d == 0) { U.Status[u] = St.Arrived; continue; }
            if (d == CpuFlowFields.INF) { U.Status[u] = St.Repath; continue; }
            int nx = F.Step(f, pos);
            U.Pos[u] = nx;
            if (F.F[f][nx] == 0) U.Status[u] = St.Arrived;
        }
    }
}
