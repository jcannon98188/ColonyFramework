using System.Runtime.CompilerServices;
using GpuPath.Bench;
using GpuPath.Core;

namespace GpuPath.Cpu;

/// <summary>
/// CPU flow fields: one exact integer BFS distance field (u16, 0xFFFF = unreachable) per shared goal.
/// Build = BFS from the goal. Repair after passability toggles = exact incremental (unit weights):
///   1) invalidate: a blocked tile goes to INF; any tile that lost every neighbour at distance d-1 goes to INF
///      (worklist to fixed point);
///   2) reseed every invalidated/opened tile from its finite neighbours, sort seeds by distance, then a two-queue
///      (sorted seeds + FIFO) BFS that also propagates decreases through opened tiles.
/// The result equals a fresh BFS bit-for-bit (checked in verify mode). Units descend by picking the first neighbour
/// (order -x,+x,-y,+y,-z,+z) whose distance is d-1.
/// Fields are processed in parallel (one job per field); each BFS is single-threaded.
/// </summary>
public sealed unsafe class CpuFlowFields : IDisposable
{
    public const ushort INF = 0xFFFF;
    readonly World _w;
    public readonly int Count;
    public readonly ushort*[] F;
    public readonly int[] Goals;
    readonly Scratch[] _s;
    public long Saturated;
    public long LastRepairTouched; // tiles invalidated + reseeded in the last RepairAll (summed over fields)

    sealed class Scratch { public UBuf<int> Q, Inval; public UBuf<ulong> Seeds; public long Touched, Rebuilds; }

    public CpuFlowFields(World w, int[] goals, int workers)
    {
        _w = w; Goals = goals; Count = goals.Length;
        F = new ushort*[Count];
        for (int i = 0; i < Count; i++) F[i] = Mem.Alloc<ushort>(w.N, zero: false);
        _s = new Scratch[workers];
        for (int i = 0; i < workers; i++) _s[i] = new Scratch { Q = new UBuf<int>(1 << 16), Inval = new UBuf<int>(1 << 12), Seeds = new UBuf<ulong>(1 << 12) };
        _build = new BuildJob(this); _repair = new RepairJob(this);
        RebuildBudget = w.N / 12;
    }

    public long FieldBytes => (long)Count * _w.N * 2;
    public long ScratchBytes { get { long b = 0; foreach (var s in _s) b += s.Q.Bytes + s.Inval.Bytes + s.Seeds.Bytes; return b; } }

    // ------------------------------------------------------------------ full build
    public void Build(int f, int worker)
    {
        var s = _s[worker];
        ushort* d = F[f];
        byte* T = _w.T;
        int W = _w.W, Pl = _w.Plane, N = _w.N;
        Mem.FillL(d, N, INF);
        int g = Goals[f];
        if ((T[g] & World.Pass) == 0) return;
        d[g] = 0;
        ref var q = ref s.Q;
        q.Clear(); q.Push(g);
        long head = 0;
        long sat = 0;
        while (head < q.Len)
        {
            int i = q.P[head++];
            int ndi = d[i] + 1;
            if (ndi >= INF) { sat++; continue; }
            ushort nd = (ushort)ndi;
            int j = i - 1; if ((T[j] & 1) != 0 && d[j] == INF) { d[j] = nd; q.Push(j); }
            j = i + 1; if ((T[j] & 1) != 0 && d[j] == INF) { d[j] = nd; q.Push(j); }
            j = i - W; if ((T[j] & 1) != 0 && d[j] == INF) { d[j] = nd; q.Push(j); }
            j = i + W; if ((T[j] & 1) != 0 && d[j] == INF) { d[j] = nd; q.Push(j); }
            if ((T[i] & World.Stair) != 0)
            {
                j = i - Pl; if (i >= Pl && (T[j] & 3) == 3 && d[j] == INF) { d[j] = nd; q.Push(j); }
                j = i + Pl; if (i < N - Pl && (T[j] & 3) == 3 && d[j] == INF) { d[j] = nd; q.Push(j); }
            }
        }
        if (sat != 0) Interlocked.Add(ref Saturated, sat);
    }

    sealed class BuildJob(CpuFlowFields o) : IParallelJob { public void Execute(int i, int w) => o.Build(i, w); }
    readonly BuildJob _build;
    public void BuildAll(WorkerPool pool, int threads) => pool.Run(_build, Count, threads);

    // ------------------------------------------------------------------ incremental repair
    int* _tog; int _togN;
    sealed class RepairJob(CpuFlowFields o) : IParallelJob { public void Execute(int i, int w) => o.Repair(i, w, o._tog, o._togN); }
    readonly RepairJob _repair;

    public void RepairAll(int* toggled, int n, WorkerPool pool, int threads)
    {
        if (n == 0) { LastRepairTouched = 0; return; }
        _tog = toggled; _togN = n;
        foreach (var s in _s) { s.Touched = 0; s.Rebuilds = 0; }
        pool.Run(_repair, Count, threads);
        long t = 0, rb = 0; foreach (var s in _s) { t += s.Touched; rb += s.Rebuilds; }
        LastRebuilds = rb;
        LastRepairTouched = t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool HasSupport(ushort* d, int u, int want)
    {
        int W = _w.W;
        if (d[u - 1] == want || d[u + 1] == want || d[u - W] == want || d[u + W] == want) return true;
        if ((_w.T[u] & World.Stair) != 0)
        {
            int j = _w.Nb(u, 4); if (j >= 0 && d[j] == want) return true;
            j = _w.Nb(u, 5); if (j >= 0 && d[j] == want) return true;
        }
        return false;
    }

    public void Repair(int f, int worker, int* tog, int n)
    {
        var s = _s[worker];
        ushort* d = F[f];
        byte* T = _w.T;
        ref var chk = ref s.Q; ref var inval = ref s.Inval; ref var seeds = ref s.Seeds;
        chk.Clear(); inval.Clear(); seeds.Clear();
        // Phase 1: invalidation
        for (int a = 0; a < n; a++)
        {
            int t = tog[a];
            if ((T[t] & World.Pass) == 0)
            {
                int od = d[t];
                if (od == INF) continue;
                d[t] = INF;
                for (int k = 0; k < 6; k++)
                {
                    int j = k switch { 0 => t - 1, 1 => t + 1, 2 => t - _w.W, 3 => t + _w.W, 4 => (T[t] & World.Stair) != 0 && t >= _w.Plane ? t - _w.Plane : -1, _ => (T[t] & World.Stair) != 0 && t < _w.N - _w.Plane ? t + _w.Plane : -1 };
                    if (j >= 0 && d[j] == od + 1) chk.Push(j);
                }
            }
            else inval.Push(t); // opened: reseed below
        }
        for (long h = 0; h < chk.Len; h++)
        {
            int u = chk.P[h];
            int du = d[u];
            if (du == INF || du == 0) continue;
            if (HasSupport(d, u, du - 1)) continue;
            d[u] = INF;
            if (inval.Len > RebuildBudget) { s.Touched += inval.Len; s.Rebuilds++; Build(f, worker); return; }
            inval.Push(u);
            for (int k = 0; k < 6; k++)
            {
                int j = _w.Nb(u, k);
                if (j >= 0 && d[j] == du + 1) chk.Push(j);
            }
        }
        // Phase 2: reseed from finite neighbours
        for (long a = 0; a < inval.Len; a++)
        {
            int u = inval.P[a];
            if ((T[u] & World.Pass) == 0 || d[u] != INF) continue;
            int best = INF;
            for (int k = 0; k < 6; k++)
            {
                int j = _w.Nb(u, k);
                if (j >= 0 && d[j] < best) best = d[j];
            }
            if (best < INF - 1) { d[u] = (ushort)(best + 1); seeds.Push(((ulong)(uint)(best + 1) << 32) | (uint)u); }
        }
        s.Touched += inval.Len;
        new Span<ulong>(seeds.P, checked((int)seeds.Len)).Sort();
        // two-queue merge BFS (seeds sorted by distance; FIFO is monotone)
        chk.Clear();
        long si = 0, qh = 0;
        int W = _w.W, Pl = _w.Plane, N = _w.N;
        long budget = RebuildBudget;
        while (si < seeds.Len || qh < chk.Len)
        {
            int u;
            if (qh < chk.Len && (si >= seeds.Len || d[chk.P[qh]] <= (int)(seeds.P[si] >> 32))) u = chk.P[qh++];
            else
            {
                ulong e = seeds.P[si++];
                u = (int)(uint)e;
                if (d[u] != (int)(e >> 32)) continue; // stale
            }
            int nd = d[u] + 1;
            if (nd >= INF) continue;
            ushort ndu = (ushort)nd;
            int j = u - 1; if ((T[j] & 1) != 0 && d[j] > nd) { d[j] = ndu; chk.Push(j); }
            j = u + 1; if ((T[j] & 1) != 0 && d[j] > nd) { d[j] = ndu; chk.Push(j); }
            j = u - W; if ((T[j] & 1) != 0 && d[j] > nd) { d[j] = ndu; chk.Push(j); }
            j = u + W; if ((T[j] & 1) != 0 && d[j] > nd) { d[j] = ndu; chk.Push(j); }
            if ((T[u] & World.Stair) != 0)
            {
                j = u - Pl; if (u >= Pl && (T[j] & 3) == 3 && d[j] > nd) { d[j] = ndu; chk.Push(j); }
                j = u + Pl; if (u < N - Pl && (T[j] & 3) == 3 && d[j] > nd) { d[j] = ndu; chk.Push(j); }
            }
            if (chk.Len > budget)
            {
                // the change is not local: a full BFS is cheaper than continuing (exact either way)
                s.Touched += chk.Len; s.Rebuilds++;
                Build(f, worker);
                return;
            }
        }
        s.Touched += chk.Len;
    }

    /// <summary>Repair work (queue pushes) above which a Field is rebuilt from scratch instead. Default N/12.</summary>
    public long RebuildBudget;
    public long LastRebuilds;

    // ------------------------------------------------------------------ gradient step
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Step(int f, int p)
    {
        ushort* d = F[f];
        int dp = d[p];
        if (dp == 0 || dp == INF) return p;
        int want = dp - 1;
        for (int k = 0; k < 6; k++)
        {
            int j = _w.Nb(p, k);
            if (j >= 0 && d[j] == want) return j;
        }
        return p;
    }

    public ushort MaxFinite(int f)
    {
        ushort m = 0; ushort* d = F[f];
        for (int i = 0; i < _w.N; i++) if (d[i] != INF && d[i] > m) m = d[i];
        return m;
    }

    public void Dispose()
    {
        for (int i = 0; i < Count; i++) Mem.Free(F[i], _w.N);
        foreach (var s in _s) { s.Q.Free(); s.Inval.Free(); s.Seeds.Free(); }
    }
}
