using System.Numerics;
using System.Runtime.CompilerServices;
using GpuPath.Bench;

namespace GpuPath.Core;

/// <summary>
/// Bounded 3D tile World, plain row-major array (x fastest, then y, then z): idx = x + W*(y + H*z).
/// Tile byte: bit0 passable, bit1 stair, bit2 dynamic (toggle candidate), bit7 scratch.
/// Moves: 4-neighbour in-plane plus vertical between two vertically adjacent stair tiles. Uniform cost 1.
/// Neighbour order (used for every tie-break, CPU and GPU): 0:-x 1:+x 2:-y 3:+y 4:-z 5:+z.
/// The outer x/y border is always blocked, so in-plane neighbours of a passable tile are always in range.
/// </summary>
public sealed unsafe class World : IDisposable
{
    public const byte Pass = 1, Stair = 2, Dyn = 4, Mark = 0x80;
    public readonly int W, H, D, N, Plane, LogW, LogH;
    public readonly byte* T;
    public int* DynTiles; public int DynCount;
    public readonly int Cx, Cy, Cz, ChunkCount;
    public double BlockedFraction;
    public int StairCount, DoorCount;

    public World(int w, int h, int d)
    {
        if (!BitOperations.IsPow2(w) || !BitOperations.IsPow2(h) || w % 16 != 0 || h % 16 != 0 || d % 16 != 0) throw new ArgumentException("dims");
        W = w; H = h; D = d; Plane = w * h; N = Plane * d;
        LogW = BitOperations.Log2((uint)w); LogH = BitOperations.Log2((uint)h);
        T = Mem.Alloc<byte>(N);
        Cx = w / 16; Cy = h / 16; Cz = d / 16; ChunkCount = Cx * Cy * Cz;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public int X(int i) => i & (W - 1);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public int Y(int i) => (i >> LogW) & (H - 1);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public int Z(int i) => i >> (LogW + LogH);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public int Idx(int x, int y, int z) => x + W * (y + H * z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public bool P(int i) => (T[i] & Pass) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ChunkOf(int i) => (X(i) >> 4) + Cx * ((Y(i) >> 4) + Cy * (Z(i) >> 4));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Manhattan(int a, int b) => Math.Abs(X(a) - X(b)) + Math.Abs(Y(a) - Y(b)) + Math.Abs(Z(a) - Z(b));

    /// <summary>Neighbour k of passable tile i, or -1 if there is no edge (blocked or no stair link).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Nb(int i, int k)
    {
        int j;
        switch (k)
        {
            case 0: j = i - 1; break;
            case 1: j = i + 1; break;
            case 2: j = i - W; break;
            case 3: j = i + W; break;
            case 4: if ((T[i] & Stair) == 0 || i < Plane) return -1; j = i - Plane; return (T[j] & (Stair | Pass)) == (Stair | Pass) ? j : -1;
            default: if ((T[i] & Stair) == 0 || i >= N - Plane) return -1; j = i + Plane; return (T[j] & (Stair | Pass)) == (Stair | Pass) ? j : -1;
        }
        return (T[j] & Pass) != 0 ? j : -1;
    }

    /// <summary>
    /// Deterministic generation: per z-level a grid of 12x12 rooms (1-tile walls); doors along a random spanning tree
    /// (winding routes), 35% extra doors (loops), 15% of walls removed (halls), 2% sealed rooms (pockets), rock blobs
    /// until >= 30% of the level is blocked. Stairs: W*H/1024 random stair links per z pair. Dynamic tiles = all doors
    /// plus 1% random non-stair tiles.
    /// </summary>
    public static World Generate(int w, int h, int d, ulong seed)
    {
        var wd = new World(w, h, d);
        var rng = new Rng(seed);
        const int S = 12;
        int ncx = (w - 2) / S + 1, ncy = (h - 2) / S + 1;
        int doors = 0;
        var uf = new int[ncx * ncy];
        var edges = new List<(int a, int b, bool horiz)>();
        for (int z = 0; z < d; z++)
        {
            byte* L = wd.T + (long)z * wd.Plane;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool wall = x % S == 0 || y % S == 0 || x >= w - 1 || y >= h - 1;
                    L[x + w * y] = wall ? (byte)0 : Pass;
                }
            for (int i = 0; i < uf.Length; i++) uf[i] = i;
            edges.Clear();
            for (int cy = 0; cy < ncy; cy++)
                for (int cx = 0; cx < ncx; cx++)
                {
                    if (cx + 1 < ncx && (cx + 1) * S < w - 1) edges.Add((cx + ncx * cy, cx + 1 + ncx * cy, true));
                    if (cy + 1 < ncy && (cy + 1) * S < h - 1) edges.Add((cx + ncx * cy, cx + ncx * (cy + 1), false));
                }
            for (int i = edges.Count - 1; i > 0; i--) { int j = rng.Int(i + 1); (edges[i], edges[j]) = (edges[j], edges[i]); }
            foreach (var (a, b, horiz) in edges)
            {
                int ra = Find(uf, a), rb = Find(uf, b);
                bool tree = ra != rb;
                if (tree) uf[ra] = rb;
                bool hall = rng.Dbl() < 0.15;
                bool extra = !tree && rng.Dbl() < 0.35;
                if (!tree && !hall && !extra) continue;
                int acx = a % ncx, acy = a / ncx;
                int lo, hi, fixedc;
                if (horiz) { fixedc = (acx + 1) * S; lo = acy * S + 1; hi = Math.Min((acy + 1) * S - 1, h - 2); }
                else { fixedc = (acy + 1) * S; lo = acx * S + 1; hi = Math.Min((acx + 1) * S - 1, w - 2); }
                if (hi < lo) continue;
                if (hall)
                {
                    for (int t = lo; t <= hi; t++) L[horiz ? fixedc + w * t : t + w * fixedc] = Pass;
                }
                else
                {
                    int pos = lo + rng.Int(hi - lo + 1);
                    int width = rng.Dbl() < 0.3 && pos < hi ? 2 : 1;
                    for (int t = pos; t < pos + width; t++) { L[horiz ? fixedc + w * t : t + w * fixedc] = Pass | Dyn; doors++; }
                }
            }
            // sealed rooms: re-wall all four sides (door tiles keep their dynamic bit, so toggles can reopen them)
            for (int c = 0; c < ncx * ncy; c++)
            {
                if (rng.Dbl() >= 0.02) continue;
                int x0 = (c % ncx) * S, y0 = (c / ncx) * S, x1 = Math.Min(x0 + S, w - 1), y1 = Math.Min(y0 + S, h - 1);
                for (int x = x0; x <= x1; x++) { L[x + w * y0] &= Dyn; L[x + w * y1] &= Dyn; }
                for (int y = y0; y <= y1; y++) { L[x0 + w * y] &= Dyn; L[x1 + w * y] &= Dyn; }
            }
            long blocked = 0;
            for (int i = 0; i < wd.Plane; i++) if ((L[i] & Pass) == 0) blocked++;
            while (blocked < 0.30 * wd.Plane)
            {
                int cx = 1 + rng.Int(w - 2), cy = 1 + rng.Int(h - 2), r = 1 + rng.Int(3);
                for (int y = Math.Max(1, cy - r); y <= Math.Min(h - 2, cy + r); y++)
                    for (int x = Math.Max(1, cx - r); x <= Math.Min(w - 2, cx + r); x++)
                    {
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r) continue;
                        ref byte t = ref L[x + w * y];
                        if ((t & Pass) != 0) { t = (byte)(t & ~Pass); blocked++; }
                    }
            }
        }
        int stairs = 0;
        int perPair = w * h / 1024;
        for (int z = 0; z + 1 < d; z++)
            for (int k = 0; k < perPair; k++)
            {
                int x = 1 + rng.Int(w - 2), y = 1 + rng.Int(h - 2);
                int a = wd.Idx(x, y, z), b = a + wd.Plane;
                if ((wd.T[a] & (Pass | Dyn)) != Pass || (wd.T[b] & (Pass | Dyn)) != Pass) continue;
                wd.T[a] |= Stair; wd.T[b] |= Stair; stairs++;
            }
        for (int i = 0; i < wd.N / 100; i++)
        {
            int x = 1 + rng.Int(w - 2), y = 1 + rng.Int(h - 2), z = rng.Int(d);
            int t = wd.Idx(x, y, z);
            if ((wd.T[t] & Stair) == 0) wd.T[t] |= Dyn;
        }
        int dyn = 0;
        for (int i = 0; i < wd.N; i++) if ((wd.T[i] & Dyn) != 0) dyn++;
        wd.DynTiles = Mem.Alloc<int>(dyn);
        dyn = 0;
        long bl = 0;
        for (int i = 0; i < wd.N; i++) { if ((wd.T[i] & Dyn) != 0) wd.DynTiles[dyn++] = i; if ((wd.T[i] & Pass) == 0) bl++; }
        wd.DynCount = dyn; wd.BlockedFraction = (double)bl / wd.N; wd.StairCount = stairs; wd.DoorCount = doors;
        return wd;
    }

    static int Find(int[] uf, int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; }

    /// <summary>Picks `count` distinct dynamic tiles, flips their passability, writes them to `outTiles`.</summary>
    public int Toggle(ref Rng rng, int count, int* outTiles)
    {
        count = Math.Min(count, DynCount / 2);
        int n = 0;
        while (n < count)
        {
            int t = DynTiles[rng.Int(DynCount)];
            if ((T[t] & Mark) != 0) continue;
            T[t] |= Mark;
            outTiles[n++] = t;
        }
        for (int i = 0; i < n; i++) { int t = outTiles[i]; T[t] = (byte)((T[t] & ~Mark) ^ Pass); }
        return n;
    }

    /// <summary>Flips passability of the given tiles back (used to revert the previous Tick's toggles).</summary>
    public void Flip(int* tiles, int n) { for (int i = 0; i < n; i++) T[tiles[i]] ^= Pass; }

    public long Bytes => N + DynCount * 4L;
    public void Dispose() { Mem.Free(T, N); Mem.Free(DynTiles, DynCount); }
}
