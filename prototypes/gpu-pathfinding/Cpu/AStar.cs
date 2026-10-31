using System.Runtime.CompilerServices;
using GpuPath.Bench;
using GpuPath.Core;

namespace GpuPath.Cpu;

/// <summary>
/// Per-worker sparse g-value storage for tile searches: pages of 4096 consecutive tile indices, allocated from a
/// fixed per-worker pool on first touch and invalidated by a search stamp (no clearing between searches).
/// Memory is proportional to the explored area, not the World. Pool exhaustion = search budget exceeded.
/// </summary>
public unsafe struct PagedScratch
{
    public uint* PageStamp; public int* PageSlot; public uint* Pool;
    public int Slots, Used, Pages; public uint Stamp; public bool Overflow;
    public const uint INF = uint.MaxValue;

    public PagedScratch(int worldTiles, int slots)
    {
        Pages = (worldTiles + 4095) >> 12; Slots = slots;
        PageStamp = Mem.Alloc<uint>(Pages); PageSlot = Mem.Alloc<int>(Pages, false); Pool = Mem.Alloc<uint>((long)slots * 4096, false);
        Stamp = 0; Used = 0; Overflow = false;
    }

    public void Begin() { Stamp++; Used = 0; Overflow = false; if (Stamp == 0) { new Span<uint>(PageStamp, Pages).Clear(); Stamp = 1; } }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Get(int t)
    {
        int p = t >> 12;
        return PageStamp[p] == Stamp ? Pool[((long)PageSlot[p] << 12) + (t & 4095)] : INF;
    }

    /// <summary>Pointer to t's slot (allocating its page). Null if the pool is exhausted.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint* Ref(int t)
    {
        int p = t >> 12;
        if (PageStamp[p] != Stamp) return Alloc(p, t);
        return Pool + ((long)PageSlot[p] << 12) + (t & 4095);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    uint* Alloc(int p, int t)
    {
        if (Used == Slots) { Overflow = true; return null; }
        int s = Used++;
        PageSlot[p] = s; PageStamp[p] = Stamp;
        uint* pg = Pool + ((long)s << 12);
        new Span<uint>(pg, 4096).Fill(INF);
        return pg + (t & 4095);
    }

    public long Bytes => Pages * 8L + (long)Slots * 4096 * 4;
    public void Free() { Mem.Free(PageStamp, Pages); Mem.Free(PageSlot, Pages); Mem.Free(Pool, (long)Slots * 4096); }
}

/// <summary>
/// Plain tile A*: Manhattan heuristic (admissible and consistent: unit moves, vertical only on stairs), bucket queue
/// keyed by f (f changes by 0 or 2 per step), LIFO inside a bucket (prefers deeper nodes on ties). Path recovered by
/// walking g downwards (first neighbour in fixed order with g-1), so no parent array is stored.
/// </summary>
public sealed unsafe class AStar : IDisposable
{
    readonly World _w;
    public PagedScratch S;
    int* _head; readonly int _buckets;
    UBuf<long> _entries; // (tile << 32) | g, plus next index in a parallel buffer
    UBuf<int> _next;
    public int MaxExpansions;
    public long Expanded;
    public UBuf<byte> Dirs;

    public AStar(World w, int pageSlots, int maxExpansions)
    {
        _w = w; S = new PagedScratch(w.N, pageSlots); MaxExpansions = maxExpansions;
        _buckets = 1 << 17;
        _head = Mem.Alloc<int>(_buckets, false);
        new Span<int>(_head, _buckets).Fill(-1);
        _entries = new UBuf<long>(1 << 16); _next = new UBuf<int>(1 << 16); Dirs = new UBuf<byte>(1 << 12);
    }

    public long Bytes => S.Bytes + _buckets * 4L + _entries.Bytes + _next.Bytes + Dirs.Bytes;

    /// <summary>Returns path length (Dirs holds the moves start->goal), -1 if unreachable/budget exceeded.</summary>
    public int Find(int start, int goal)
    {
        var w = _w;
        Dirs.Clear();
        if (start == goal) return 0;
        S.Begin();
        _entries.Clear(); _next.Clear();
        int h0 = w.Manhattan(start, goal);
        int lo = 0, hi = 0;
        *S.Ref(start) = 0;
        Push(0, start, 0);
        int expanded = 0;
        int result = -1;
        int cur = 0;
        while (cur <= hi)
        {
            int e = _head[cur];
            if (e < 0) { cur++; continue; }
            _head[cur] = _next.P[e];
            long ent = _entries.P[e];
            int t = (int)(ent >> 32); uint g = (uint)ent;
            if (S.Get(t) != g) continue;
            if (t == goal) { result = (int)g; break; }
            if (++expanded > MaxExpansions) break;
            uint ng = g + 1;
            for (int k = 0; k < 6; k++)
            {
                int j = w.Nb(t, k);
                if (j < 0) continue;
                uint* pj = S.Ref(j);
                if (pj == null) goto done;
                if (*pj <= ng) continue;
                *pj = ng;
                int f = (int)ng + w.Manhattan(j, goal);
                int b = (f - h0) >> 1;
                if (b >= _buckets) goto done;
                Push(b, j, ng);
                if (b > hi) hi = b;
            }
        }
    done:
        Expanded += expanded;
        // reset touched buckets
        for (int b = lo; b <= hi && b < _buckets; b++) _head[b] = -1;
        if (result < 0) return -1;
        // walk back
        Dirs.Clear();
        for (int i = 0; i < result; i++) Dirs.Push(0);
        int u = goal; uint gu = (uint)result;
        while (gu > 0)
        {
            int found = -1, fk = -1;
            for (int k = 0; k < 6; k++)
            {
                int j = w.Nb(u, k);
                if (j >= 0 && S.Get(j) == gu - 1) { found = j; fk = k; break; }
            }
            if (found < 0) throw new InvalidOperationException("A* walk-back failed");
            Dirs.P[gu - 1] = (byte)(fk ^ 1); // move from found to u is the opposite direction
            u = found; gu--;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Push(int bucket, int tile, uint g)
    {
        int idx = (int)_entries.Len;
        _entries.Push(((long)tile << 32) | g);
        _next.Push(_head[bucket]);
        _head[bucket] = idx;
    }

    public void Dispose() { S.Free(); Mem.Free(_head, _buckets); _entries.Free(); _next.Free(); Dirs.Free(); }
}
