using GpuPath.Bench;
using GpuPath.Core;

namespace GpuPath.Cpu;

/// <summary>
/// HPA*-style hierarchical planner over <see cref="Regions"/>' abstract graph (chunk-local components + face
/// adjacency). Two layers:
///  1. Abstract A* (binary heap) from the start tile's node to the goal tile's node; edge cost and heuristic are
///     Manhattan distances between component centroids (consistent, but only an estimate of real cost).
///  2. Local refinement: BFS restricted to the tiles of a short window of the abstract path (current node + next
///     few nodes), ending at the first tile of the last window node (or the goal tile). Units refine again when the
///     segment runs out, so refinement cost is spread over the walk and stays local.
/// Paths are not optimal (reported as a ratio against BFS); refinement windows are re-planned if a chunk on them
/// changed (chunk version).
/// </summary>
public sealed unsafe class Hpa : IDisposable
{
    readonly World _w; readonly Regions _r;
    public PagedScratch S;
    UBuf<int> _q;
    UBuf<ulong> _heap;
    int* _g, _par; uint* _closed, _mark; int _cap; uint _stamp, _markStamp;
    public UBuf<int> NodePath;
    public UBuf<byte> Dirs;
    public UBuf<long> Enc = new(1 << 10);
    public long AbstractExpanded, RefineVisited;

    public Hpa(World w, Regions r, int pageSlots)
    {
        _w = w; _r = r; S = new PagedScratch(w.N, pageSlots);
        _q = new UBuf<int>(1 << 14); _heap = new UBuf<ulong>(1 << 12); NodePath = new UBuf<int>(1 << 10); Dirs = new UBuf<byte>(1 << 10);
    }

    public long Bytes => S.Bytes + _q.Bytes + _heap.Bytes + _cap * 16L + NodePath.Bytes + Dirs.Bytes;

    void Ensure()
    {
        int n = _r.NodeCount;
        if (n <= _cap) return;
        int nc = n + n / 2;
        Mem.Free(_g, _cap); Mem.Free(_par, _cap); Mem.Free(_closed, _cap); Mem.Free(_mark, _cap);
        _g = Mem.Alloc<int>(nc, false); _par = Mem.Alloc<int>(nc, false); _closed = Mem.Alloc<uint>(nc); _mark = Mem.Alloc<uint>(nc);
        _cap = nc; _stamp = 0; _markStamp = 0;
    }

    /// <summary>Abstract path from start's node to goal's node into NodePath (start node first). False if none.</summary>
    public bool Abstract(int startTile, int goalTile)
    {
        Ensure();
        NodePath.Clear();
        int s = _r.NodeOf(startTile), t = _r.NodeOf(goalTile);
        if (s < 0 || t < 0) return false;
        if (s == t) { NodePath.Push(s); return true; }
        _stamp += 2; if (_stamp >= uint.MaxValue - 4) { new Span<uint>(_closed, _cap).Clear(); _stamp = 2; }
        uint open = _stamp, closed = _stamp + 1; // _closed[n] == open: g valid; == closed: expanded
        _heap.Clear();
        _g[s] = 0; _closed[s] = open; _par[s] = -1;
        HeapPush(((ulong)(uint)_r.RepDist(s, goalTile) << 32) | (uint)s);
        int exp = 0;
        bool found = false;
        while (_heap.Len > 0)
        {
            ulong e = HeapPop();
            int n = (int)(uint)e;
            if (_closed[n] == closed) continue;
            _closed[n] = closed;
            exp++;
            if (n == t) { found = true; break; }
            int gn = _g[n];
            int a = _r.AdjOff[n], b = _r.AdjOff[n + 1];
            for (int i = a; i < b; i++)
            {
                int m = _r.Adj[i];
                if (_closed[m] == closed) continue;
                int ng = gn + _r.AdjCost[i];
                if (_closed[m] == open && _g[m] <= ng) continue;
                _g[m] = ng; _closed[m] = open; _par[m] = n;
                HeapPush(((ulong)(uint)(ng + _r.RepDist(m, goalTile)) << 32) | (uint)m);
            }
        }
        AbstractExpanded += exp;
        if (!found) return false;
        for (int n = t; n >= 0; n = _par[n]) NodePath.Push(n);
        // reverse to start-first
        for (long i = 0, j = NodePath.Len - 1; i < j; i++, j--) (NodePath.P[i], NodePath.P[j]) = (NodePath.P[j], NodePath.P[i]);
        return true;
    }

    /// <summary>
    /// BFS from start restricted to tiles whose node is in nodes[0..count); stops at goalTile if it lies in the last
    /// node, else at the first tile of the last node. Dirs = moves. Returns steps or -1.
    /// </summary>
    public int Refine(int start, int* nodes, int count, int goalTile)
    {
        Ensure();
        Dirs.Clear();
        if (++_markStamp == 0) { new Span<uint>(_mark, _cap).Clear(); _markStamp = 1; }
        for (int i = 0; i < count; i++) _mark[nodes[i]] = _markStamp;
        int last = nodes[count - 1];
        bool toGoal = goalTile >= 0 && _r.NodeOf(goalTile) == last;
        if (toGoal && start == goalTile) return 0;
        if (!toGoal && _r.NodeOf(start) == last) return 0;
        S.Begin();
        _q.Clear();
        *S.Ref(start) = 0; _q.Push(start);
        int target = -1;
        long h = 0;
        while (h < _q.Len)
        {
            int u = _q.P[h++];
            uint du = S.Get(u);
            for (int k = 0; k < 6; k++)
            {
                int j = _w.Nb(u, k);
                if (j < 0) continue;
                int nj = _r.NodeOf(j);
                if (_mark[nj] != _markStamp) continue;
                uint* pj = S.Ref(j);
                if (pj == null) goto done;
                if (*pj != PagedScratch.INF) continue;
                *pj = du + 1;
                if (toGoal ? j == goalTile : nj == last) { target = j; goto done; }
                _q.Push(j);
            }
        }
    done:
        RefineVisited += _q.Len;
        if (target < 0) return -1;
        int len = (int)S.Get(target);
        for (int i = 0; i < len; i++) Dirs.Push(0);
        int cur = target; uint g = (uint)len;
        while (g > 0)
        {
            int found = -1, fk = -1;
            for (int k = 0; k < 6; k++)
            {
                int j = _w.Nb(cur, k);
                if (j >= 0 && S.Get(j) == g - 1) { found = j; fk = k; break; }
            }
            if (found < 0) throw new InvalidOperationException("refine walk-back failed");
            Dirs.P[g - 1] = (byte)(fk ^ 1);
            cur = found; g--;
        }
        return len;
    }

    void HeapPush(ulong v)
    {
        _heap.Push(v);
        long i = _heap.Len - 1;
        ulong* a = _heap.P;
        while (i > 0) { long p = (i - 1) >> 1; if (a[p] <= v) break; a[i] = a[p]; i = p; }
        a[i] = v;
    }

    ulong HeapPop()
    {
        ulong* a = _heap.P;
        ulong top = a[0];
        ulong v = a[--_heap.Len];
        long n = _heap.Len, i = 0;
        while (true)
        {
            long c = 2 * i + 1;
            if (c >= n) break;
            if (c + 1 < n && a[c + 1] < a[c]) c++;
            if (a[c] >= v) break;
            a[i] = a[c]; i = c;
        }
        if (n > 0) a[i] = v;
        return top;
    }

    public void Dispose()
    {
        S.Free(); _q.Free(); _heap.Free(); NodePath.Free(); Dirs.Free();
        Mem.Free(_g, _cap); Mem.Free(_par, _cap); Mem.Free(_closed, _cap); Mem.Free(_mark, _cap);
    }
}
