using System.Runtime.CompilerServices;
using GpuPath.Bench;
using GpuPath.Core;

namespace GpuPath.Cpu;

/// <summary>
/// Chunk-level connectivity (16x16x16 chunks): every chunk's passable tiles are split into local connected
/// components (flood fill inside the chunk, stairs included). Each local component is an abstract node; abstract
/// edges join local components of face-adjacent chunks that touch (or are joined by a stair across a z border).
///  - Region ID (global connected component) = union-find root over the abstract graph: O(1) reachability check.
///  - The same graph (CSR, edge cost = Manhattan distance between component centroids) drives HPA*-style search.
/// Incremental update: relabel only chunks containing toggled tiles, recompute only their faces, then rebuild the
/// (small) union-find and CSR over the abstract graph. Full rebuild relabels every chunk.
/// </summary>
public sealed unsafe class Regions : IDisposable
{
    public const int CAP = 1024, EC = 768;
    public const ushort NONE = 0xFFFF;
    readonly World _w;
    public readonly ushort* L;
    public readonly int* CompCount, Version, NodeBase, Edges, EdgeCount;
    public readonly short* RepX, RepY, RepZ; // per chunk*CAP slot: component centroid
    public int NodeCount;
    int _nodeCap;
    public int* Parent, RegionOfNode, NodeChunk;
    public int* AdjOff, Adj, AdjCost;
    long _adjCap;
    public int EdgeTotal;
    readonly byte* _dirtyMark;
    readonly int* _dirtyList, _edgeList;
    int _dirtyN, _edgeN;
    public int LastDirtyChunks;
    public double LastLabelMs, LastEdgeMs, LastGraphMs;

    public Regions(World w)
    {
        _w = w;
        int C = w.ChunkCount;
        L = Mem.Alloc<ushort>(w.N, zero: false);
        CompCount = Mem.Alloc<int>(C); Version = Mem.Alloc<int>(C); NodeBase = Mem.Alloc<int>(C + 1);
        Edges = Mem.Alloc<int>((long)C * EC); EdgeCount = Mem.Alloc<int>(C);
        RepX = Mem.Alloc<short>((long)C * CAP); RepY = Mem.Alloc<short>((long)C * CAP); RepZ = Mem.Alloc<short>((long)C * CAP);
        _dirtyMark = Mem.Alloc<byte>(C); _dirtyList = Mem.Alloc<int>(C); _edgeList = Mem.Alloc<int>(C);
        _label = new LabelJob(this); _edge = new EdgeJob(this);
    }

    public long Bytes => _w.N * 2L + (long)_w.ChunkCount * (EC * 4 + CAP * 6 + 20) + _nodeCap * 12L + _adjCap * 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NodeOf(int tile)
    {
        ushort l = L[tile];
        return l == NONE ? -1 : NodeBase[_w.ChunkOf(tile)] + l;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegionOf(int tile)
    {
        int n = NodeOf(tile);
        return n < 0 ? -1 : RegionOfNode[n];
    }

    public int RepTile(int node)
    {
        int c = NodeChunk[node], l = node - NodeBase[c];
        long s = (long)c * CAP + l;
        return _w.Idx(RepX[s], RepY[s], RepZ[s]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RepDist(int node, int tile)
    {
        int c = NodeChunk[node]; long s = (long)c * CAP + (node - NodeBase[c]);
        return Math.Abs(RepX[s] - _w.X(tile)) + Math.Abs(RepY[s] - _w.Y(tile)) + Math.Abs(RepZ[s] - _w.Z(tile));
    }

    // ------------------------------------------------------------------ chunk labelling
    void LabelChunk(int c)
    {
        var w = _w;
        int cx = c % w.Cx, cy = (c / w.Cx) % w.Cy, cz = c / (w.Cx * w.Cy);
        int x0 = cx * 16, y0 = cy * 16, z0 = cz * 16;
        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
            {
                int b = w.Idx(x0, y0 + y, z0 + z);
                for (int x = 0; x < 16; x++) L[b + x] = NONE;
            }
        int* q = stackalloc int[4096];
        int label = 0;
        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    int t = w.Idx(x0 + x, y0 + y, z0 + z);
                    if (L[t] != NONE || (w.T[t] & World.Pass) == 0) continue;
                    if (label >= CAP) throw new InvalidOperationException($"chunk {c} has more than {CAP} local components");
                    ushort lb = (ushort)label;
                    L[t] = lb;
                    int qn = 1, qh = 0; q[0] = t;
                    long sx = 0, sy = 0, sz = 0;
                    while (qh < qn)
                    {
                        int i = q[qh++];
                        int ix = w.X(i) - x0, iy = w.Y(i) - y0, iz = w.Z(i) - z0;
                        sx += ix; sy += iy; sz += iz;
                        for (int k = 0; k < 6; k++)
                        {
                            bool inside = k switch { 0 => ix > 0, 1 => ix < 15, 2 => iy > 0, 3 => iy < 15, 4 => iz > 0, _ => iz < 15 };
                            if (!inside) continue;
                            int j = w.Nb(i, k);
                            if (j < 0 || L[j] != NONE) continue;
                            L[j] = lb; q[qn++] = j;
                        }
                    }
                    long s = (long)c * CAP + label;
                    RepX[s] = (short)(x0 + sx / qn); RepY[s] = (short)(y0 + sy / qn); RepZ[s] = (short)(z0 + sz / qn);
                    label++;
                }
        CompCount[c] = label;
        Version[c]++;
    }

    void ChunkEdges(int c)
    {
        var w = _w;
        int cx = c % w.Cx, cy = (c / w.Cx) % w.Cy, cz = c / (w.Cx * w.Cy);
        int x0 = cx * 16, y0 = cy * 16, z0 = cz * 16;
        int* buf = stackalloc int[EC];
        int n = 0;
        if (cx + 1 < w.Cx)
            for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++)
                {
                    int a = w.Idx(x0 + 15, y0 + y, z0 + z), b = a + 1;
                    if (L[a] != NONE && L[b] != NONE) buf[n++] = (0 << 20) | (L[a] << 10) | L[b];
                }
        if (cy + 1 < w.Cy)
            for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
                {
                    int a = w.Idx(x0 + x, y0 + 15, z0 + z), b = a + w.W;
                    if (L[a] != NONE && L[b] != NONE) buf[n++] = (1 << 20) | (L[a] << 10) | L[b];
                }
        if (cz + 1 < w.Cz)
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
                {
                    int a = w.Idx(x0 + x, y0 + y, z0 + 15);
                    if (w.Nb(a, 5) >= 0 && L[a] != NONE) { int b = a + w.Plane; if (L[b] != NONE) buf[n++] = (2 << 20) | (L[a] << 10) | L[b]; }
                }
        var span = new Span<int>(buf, n);
        span.Sort();
        int u = 0;
        int* dst = Edges + (long)c * EC;
        for (int i = 0; i < n; i++) if (i == 0 || buf[i] != buf[i - 1]) dst[u++] = buf[i];
        EdgeCount[c] = u;
    }

    sealed class LabelJob(Regions r) : IParallelJob { public void Execute(int i, int w) => r.LabelChunk(r._dirtyList[i]); }
    sealed class EdgeJob(Regions r) : IParallelJob { public void Execute(int i, int w) => r.ChunkEdges(r._edgeList[i]); }
    readonly LabelJob _label; readonly EdgeJob _edge;

    // ------------------------------------------------------------------ abstract graph
    int Find(int a) { while (Parent[a] != a) { Parent[a] = Parent[Parent[a]]; a = Parent[a]; } return a; }

    void EnsureNodeCap(int n)
    {
        if (n <= _nodeCap) return;
        int nc = Math.Max(n + n / 2, 1024);
        Mem.Free(Parent, _nodeCap); Mem.Free(RegionOfNode, _nodeCap); Mem.Free(NodeChunk, _nodeCap); Mem.Free(AdjOff, _nodeCap + 1);
        Parent = Mem.Alloc<int>(nc, false); RegionOfNode = Mem.Alloc<int>(nc, false); NodeChunk = Mem.Alloc<int>(nc, false); AdjOff = Mem.Alloc<int>(nc + 1, false);
        _nodeCap = nc;
    }

    void BuildGraph()
    {
        var w = _w;
        int C = w.ChunkCount;
        int acc = 0;
        for (int c = 0; c < C; c++) { NodeBase[c] = acc; acc += CompCount[c]; }
        NodeBase[C] = acc;
        NodeCount = acc;
        EnsureNodeCap(acc);
        for (int c = 0; c < C; c++) { int b = NodeBase[c], e = NodeBase[c + 1]; for (int n = b; n < e; n++) NodeChunk[n] = c; }
        for (int n = 0; n < acc; n++) { Parent[n] = n; AdjOff[n] = 0; }
        AdjOff[acc] = 0;
        int total = 0;
        int plane = w.Cx * w.Cy;
        for (int c = 0; c < C; c++)
        {
            int* e = Edges + (long)c * EC; int m = EdgeCount[c];
            total += m;
            for (int i = 0; i < m; i++)
            {
                int code = e[i]; int dir = code >> 20, la = (code >> 10) & 1023, lb = code & 1023;
                int c2 = c + (dir == 0 ? 1 : dir == 1 ? w.Cx : plane);
                int na = NodeBase[c] + la, nb = NodeBase[c2] + lb;
                int ra = Find(na), rb = Find(nb);
                if (ra != rb) { if (ra < rb) Parent[rb] = ra; else Parent[ra] = rb; }
                AdjOff[na + 1]++; AdjOff[nb + 1]++;
            }
        }
        EdgeTotal = total;
        for (int n = 0; n < acc; n++) RegionOfNode[n] = Find(n);
        for (int n = 0; n < acc; n++) AdjOff[n + 1] += AdjOff[n];
        long need = 2L * total;
        if (need > _adjCap)
        {
            long nc = Math.Max(need + need / 2, 1024);
            Mem.Free(Adj, _adjCap); Mem.Free(AdjCost, _adjCap);
            Adj = Mem.Alloc<int>(nc, false); AdjCost = Mem.Alloc<int>(nc, false); _adjCap = nc;
        }
        // fill (use Parent as a cursor would clobber UF; use a temp cursor in RegionOfNode? no: reuse NodeChunk-free approach)
        int* cur = stackalloc int[1];
        for (int c = 0; c < C; c++)
        {
            int* e = Edges + (long)c * EC; int m = EdgeCount[c];
            for (int i = 0; i < m; i++)
            {
                int code = e[i]; int dir = code >> 20, la = (code >> 10) & 1023, lb = code & 1023;
                int c2 = c + (dir == 0 ? 1 : dir == 1 ? w.Cx : plane);
                int na = NodeBase[c] + la, nb = NodeBase[c2] + lb;
                long sa = (long)c * CAP + la, sb = (long)c2 * CAP + lb;
                int cost = Math.Max(1, Math.Abs(RepX[sa] - RepX[sb]) + Math.Abs(RepY[sa] - RepY[sb]) + Math.Abs(RepZ[sa] - RepZ[sb]));
                int pa = AdjOff[na]++; Adj[pa] = nb; AdjCost[pa] = cost;
                int pb = AdjOff[nb]++; Adj[pb] = na; AdjCost[pb] = cost;
            }
        }
        // AdjOff[n] now holds the end of n's range; shift back
        for (int n = acc; n > 0; n--) AdjOff[n] = AdjOff[n - 1];
        AdjOff[0] = 0;
    }

    public void BuildAll(WorkerPool pool, int threads)
    {
        int C = _w.ChunkCount;
        for (int c = 0; c < C; c++) { _dirtyList[c] = c; _edgeList[c] = c; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        pool.Run(_label, C, threads);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        pool.Run(_edge, C, threads);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildGraph();
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastDirtyChunks = C;
        LastLabelMs = Ms(t0, t1); LastEdgeMs = Ms(t1, t2); LastGraphMs = Ms(t2, t3);
    }

    static double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Incremental update after tiles were toggled (World already updated).</summary>
    public void Update(int* tog, int n, WorkerPool pool, int threads)
    {
        if (n == 0) { LastDirtyChunks = 0; LastLabelMs = LastEdgeMs = LastGraphMs = 0; return; }
        var w = _w;
        _dirtyN = 0; _edgeN = 0;
        for (int i = 0; i < n; i++)
        {
            int c = w.ChunkOf(tog[i]);
            if ((_dirtyMark[c] & 1) == 0) { _dirtyMark[c] |= 1; _dirtyList[_dirtyN++] = c; }
        }
        int plane = w.Cx * w.Cy;
        for (int i = 0; i < _dirtyN; i++)
        {
            int c = _dirtyList[i];
            int cx = c % w.Cx, cy = (c / w.Cx) % w.Cy, cz = c / plane;
            AddEdgeChunk(c);
            if (cx > 0) AddEdgeChunk(c - 1);
            if (cy > 0) AddEdgeChunk(c - w.Cx);
            if (cz > 0) AddEdgeChunk(c - plane);
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        pool.Run(_label, _dirtyN, threads);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        pool.Run(_edge, _edgeN, threads);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < _dirtyN; i++) _dirtyMark[_dirtyList[i]] = 0;
        for (int i = 0; i < _edgeN; i++) _dirtyMark[_edgeList[i]] = 0;
        BuildGraph();
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastDirtyChunks = _dirtyN;
        LastLabelMs = Ms(t0, t1); LastEdgeMs = Ms(t1, t2); LastGraphMs = Ms(t2, t3);
    }

    void AddEdgeChunk(int c)
    {
        if ((_dirtyMark[c] & 2) != 0) return;
        _dirtyMark[c] |= 2; _edgeList[_edgeN++] = c;
    }

    public void Dispose()
    {
        int C = _w.ChunkCount;
        Mem.Free(L, _w.N); Mem.Free(CompCount, C); Mem.Free(Version, C); Mem.Free(NodeBase, C + 1);
        Mem.Free(Edges, (long)C * EC); Mem.Free(EdgeCount, C);
        Mem.Free(RepX, (long)C * CAP); Mem.Free(RepY, (long)C * CAP); Mem.Free(RepZ, (long)C * CAP);
        Mem.Free(_dirtyMark, C); Mem.Free(_dirtyList, C); Mem.Free(_edgeList, C);
        Mem.Free(Parent, _nodeCap); Mem.Free(RegionOfNode, _nodeCap); Mem.Free(NodeChunk, _nodeCap); Mem.Free(AdjOff, _nodeCap + 1);
        Mem.Free(Adj, _adjCap); Mem.Free(AdjCost, _adjCap);
    }
}
