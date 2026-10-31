using System.Diagnostics;
using GpuPath.Backend;
using GpuPath.Bench;
using GpuPath.Core;

namespace GpuPath.Gpu;

/// <summary>
/// GPU flow fields on wgpu (WGSL compute). Distance layout is identical to the CPU's: u16 per tile, two tiles per
/// u32 word (even x in the low half), so CPU and GPU fields compare byte-for-byte.
///
/// Field build / repair = block-local iterative relaxation with a work list:
///   - a block is 16x16 tiles of one z-level; a workgroup (128 threads, 2 tiles each) loads the block plus a 1-tile
///     halo and the stair neighbours into workgroup memory and iterates to local convergence (Jacobi with barriers);
///   - changed blocks enqueue the neighbour blocks whose halo they touched (dedup by atomicMax on an iteration mark);
///   - the next iteration is an indirect dispatch over that list. One iteration = 2 dispatches (Kernel + args).
/// Repair after toggles (exact, mirrors the CPU): toggle Kernel (flip tile, set INF, seed lists) -> invalidation
/// iterations to a fixed point (tiles that lost all d-1 support become INF) -> relaxation iterations seeded by every
/// block touched. Convergence is detected by reading back per-iteration list counts every `IterPerSubmit` iterations.
/// Fields are grouped in batches of up to 32 (one 2^31-byte binding limit), each with its own lists.
/// </summary>
public sealed unsafe class GpuFlow : IDisposable
{
    public const int FPB = 32;
    public const int Hist = 1 << 14;
    readonly WgpuDevice _d;
    readonly World _w;
    public readonly int FieldCount, Batches;
    readonly int _nb; // blocks per field
    public readonly WgpuBuffer Tiles, Params, Toggles, UnitPos, UnitField, UnitOut, UnitRead, CountRead;
    readonly Batch[] _b;
    readonly IKernel _relax, _inval, _prep, _toggle, _fill, _seed, _move;
    IBindingSet _moveBg = null!;
    public readonly nint QuerySet;
    readonly WgpuBuffer _tsResolve, _tsRead;
    public int IterPerSubmit = 32;
    /// <summary>In-block updates visible immediately (chaotic relaxation; monotone so the fixed point is unchanged). Read at Kernel creation.</summary>
    public static bool GaussSeidel = true;
    /// <summary>Toggle count above which Repair does a full rebuild instead.</summary>
    public int RebuildAbove;
    public bool LastWasRebuild;
    public long LastBlockVisits;
    public long DeviceBytes;
    uint _iter = 1, _epoch = 1;
    int _units;
    public readonly int[] Goals;

    // stats of the last Build/Repair
    public int LastIterations, LastInvalIterations, LastSubmits, LastDispatches;
    public double LastGpuMs, LastWallMs;

    sealed class Batch
    {
        public int First, Count;
        public WgpuBuffer Dist = null!, Marks = null!, L0 = null!, L1 = null!, L2 = null!, Counters = null!, Args = null!, History = null!, Goals = null!;
        public IBindingSet Relax = null!, Inval = null!, Prep = null!, Toggle = null!, Fill = null!, Seed = null!;
    }

    // params slots (256-byte aligned): kernel mode/in/out combinations
    // slot = in*3 + out for relax/inval (in,out in 0..2) ; prep slots 16 + zero*4 + args  (zero 0..3 where 3 = none)
    static uint Slot(int a, int b) => (uint)(a * 3 + b) * 256;
    static uint PrepSlot(int zero, int args) => (uint)(16 + zero * 4 + args) * 256;

    string Common => $$"""
        const W: u32 = {{_w.W}}u; const H: u32 = {{_w.H}}u; const D: u32 = {{_w.D}}u;
        const PLANE: u32 = {{_w.Plane}}u; const HALFN: u32 = {{_w.N / 2}}u;
        const BX: u32 = {{_w.W / 16}}u; const BY: u32 = {{_w.H / 16}}u; const NBF: u32 = {{_nb}}u;
        const INF: u32 = 0xFFFFu;
        const HIST: u32 = {{Hist}}u;
        struct Params { a: u32, b: u32, c: u32, d: u32 };
        """;

    // Relaxation / invalidation Kernel. MODE 0 = relax, 1 = invalidate.
    string BlockKernel(int mode) => Common + $$"""
        const MODE: u32 = {{mode}}u;
        const GS: u32 = {{(GaussSeidel ? 1 : 0)}}u;
        @group(0) @binding(0) var<storage, read> tiles: array<u32>;
        @group(0) @binding(1) var<storage, read_write> dist: array<u32>;
        @group(0) @binding(2) var<storage, read_write> marks: array<atomic<u32>>;
        @group(0) @binding(3) var<storage, read_write> list0: array<u32>;
        @group(0) @binding(4) var<storage, read_write> list1: array<u32>;
        @group(0) @binding(5) var<storage, read_write> list2: array<u32>;
        @group(0) @binding(6) var<storage, read_write> counters: array<atomic<u32>>;
        @group(0) @binding(7) var<uniform> P: Params;   // a = in list, b = out list

        var<workgroup> sd: array<u32, 324>;
        var<workgroup> wgAny: atomic<u32>;
        var<workgroup> wgEdges: atomic<u32>;
        var<workgroup> wgCopy: u32;

        fn tileByte(i: u32) -> u32 { return (tiles[i >> 2u] >> ((i & 3u) * 8u)) & 0xFFu; }
        fn getD(fb: u32, i: u32) -> u32 { return (dist[fb + (i >> 1u)] >> ((i & 1u) * 16u)) & 0xFFFFu; }
        fn listLoad(s: u32, i: u32) -> u32 { if (s == 0u) { return list0[i]; } if (s == 1u) { return list1[i]; } return list2[i]; }
        fn listStore(s: u32, i: u32, v: u32) { if (s == 0u) { list0[i] = v; } else if (s == 1u) { list1[i] = v; } else { list2[i] = v; } }
        fn enqueue(f: u32, nb: u32, iter: u32) {
            if (atomicMax(&marks[f * NBF + nb], iter) < iter) {
                let idx = atomicAdd(&counters[P.b], 1u);
                listStore(P.b, idx, (f << 20u) | nb);
            }
        }
        fn seedRelax(f: u32, nb: u32, epoch: u32) {
            if (atomicMax(&marks[{{FPB}}u * NBF + f * NBF + nb], epoch) < epoch) {
                let idx = atomicAdd(&counters[2], 1u);
                list2[idx] = (f << 20u) | nb;
            }
        }

        @compute @workgroup_size(128)
        fn main(@builtin(workgroup_id) wg: vec3<u32>, @builtin(num_workgroups) nwg: vec3<u32>, @builtin(local_invocation_index) li: u32) {
            if (li == 0u) {
                let idx = wg.y * nwg.x + wg.x;
                var item = 0xFFFFFFFFu;
                if (idx < atomicLoad(&counters[P.a])) { item = listLoad(P.a, idx); }
                wgCopy = item;
                atomicStore(&wgAny, 0u);
                atomicStore(&wgEdges, 0u);
            }
            let item = workgroupUniformLoad(&wgCopy);
            if (item == 0xFFFFFFFFu) { return; }
            let f = item >> 20u;
            let b = item & 0xFFFFFu;
            let bx = b % BX; let by = (b / BX) % BY; let z = b / (BX * BY);
            let x0 = bx * 16u; let y0 = by * 16u;
            let fb = f * HALFN;
            let ly = li / 8u; let lx = (li % 8u) * 2u;
            let gi = x0 + lx + W * (y0 + ly + H * z);
            let wordIdx = fb + (gi >> 1u);
            let word = dist[wordIdx];
            let o0 = word & 0xFFFFu; let o1 = word >> 16u;
            var d0 = o0; var d1 = o1;
            let t0 = tileByte(gi); let t1 = tileByte(gi + 1u);
            let c0 = (ly + 1u) * 18u + lx + 1u; let c1 = c0 + 1u;
            sd[c0] = d0; sd[c1] = d1;
            if (li < 68u) {
                var hx: i32; var hy: i32;
                if (li < 16u) { hx = i32(li); hy = -1; }
                else if (li < 32u) { hx = i32(li) - 16; hy = 16; }
                else if (li < 50u) { hx = -1; hy = i32(li) - 33; }
                else { hx = 16; hy = i32(li) - 51; }
                let gx = i32(x0) + hx; let gy = i32(y0) + hy;
                var v = INF;
                if (gx >= 0 && gx < i32(W) && gy >= 0 && gy < i32(H)) { v = getD(fb, u32(gx) + W * (u32(gy) + H * z)); }
                sd[u32(hy + 1) * 18u + u32(hx + 1)] = v;
            }
            var up0 = INF; var dn0 = INF; var up1 = INF; var dn1 = INF;
            if ((t0 & 2u) != 0u) {
                if (z + 1u < D && (tileByte(gi + PLANE) & 3u) == 3u) { up0 = getD(fb, gi + PLANE); }
                if (z > 0u && (tileByte(gi - PLANE) & 3u) == 3u) { dn0 = getD(fb, gi - PLANE); }
            }
            if ((t1 & 2u) != 0u) {
                if (z + 1u < D && (tileByte(gi + 1u + PLANE) & 3u) == 3u) { up1 = getD(fb, gi + 1u + PLANE); }
                if (z > 0u && (tileByte(gi + 1u - PLANE) & 3u) == 3u) { dn1 = getD(fb, gi + 1u - PLANE); }
            }
            let p0 = (t0 & 1u) != 0u; let p1 = (t1 & 1u) != 0u;
            workgroupBarrier();
            var guard = 0u;
            loop {
                var n0 = d0; var n1 = d1;
                if (MODE == 0u) {
                    if (p0 && d0 != 0u) {
                        let m = min(min(min(sd[c0 - 1u], sd[c0 + 1u]), min(sd[c0 - 18u], sd[c0 + 18u])), min(up0, dn0));
                        n0 = min(d0, m + 1u);
                        if (GS != 0u && n0 != d0) { sd[c0] = n0; }
                    }
                    if (p1 && d1 != 0u) {
                        let m = min(min(min(sd[c1 - 1u], sd[c1 + 1u]), min(sd[c1 - 18u], sd[c1 + 18u])), min(up1, dn1));
                        n1 = min(d1, m + 1u);
                    }
                } else {
                    if (p0 && d0 != INF && d0 != 0u) {
                        let wv = d0 - 1u;
                        if (sd[c0 - 1u] != wv && sd[c0 + 1u] != wv && sd[c0 - 18u] != wv && sd[c0 + 18u] != wv && up0 != wv && dn0 != wv) { n0 = INF; }
                    }
                    if (GS != 0u && n0 != d0) { sd[c0] = n0; }
                    if (p1 && d1 != INF && d1 != 0u) {
                        let wv = d1 - 1u;
                        if (sd[c1 - 1u] != wv && sd[c1 + 1u] != wv && sd[c1 - 18u] != wv && sd[c1 + 18u] != wv && up1 != wv && dn1 != wv) { n1 = INF; }
                    }
                }
                if (GS == 0u) { workgroupBarrier(); }
                if (n0 != d0 || n1 != d1) {
                    var e = 32u;
                    if (n0 != d0) {
                        sd[c0] = n0;
                        if (lx == 0u) { e |= 1u; }
                        if ((t0 & 2u) != 0u) { e |= 16u; }
                    }
                    if (n1 != d1) {
                        sd[c1] = n1;
                        if (lx == 14u) { e |= 2u; }
                        if ((t1 & 2u) != 0u) { e |= 16u; }
                    }
                    if (ly == 0u) { e |= 4u; }
                    if (ly == 15u) { e |= 8u; }
                    atomicOr(&wgEdges, e);
                    atomicOr(&wgAny, 1u);
                    d0 = n0; d1 = n1;
                }
                workgroupBarrier();
                if (li == 0u) { wgCopy = atomicLoad(&wgAny); atomicStore(&wgAny, 0u); }
                guard += 1u;
                let anyc = workgroupUniformLoad(&wgCopy);
                if (anyc == 0u || guard >= 600u) { break; }
            }
            if (d0 != o0 || d1 != o1) { dist[wordIdx] = d0 | (d1 << 16u); }
            workgroupBarrier();
            if (li == 0u) {
                let e = atomicLoad(&wgEdges);
                if (e != 0u) {
                    let iter = atomicLoad(&counters[4]);
                    if ((e & 1u) != 0u && bx > 0u) { enqueue(f, b - 1u, iter); }
                    if ((e & 2u) != 0u && bx + 1u < BX) { enqueue(f, b + 1u, iter); }
                    if ((e & 4u) != 0u && by > 0u) { enqueue(f, b - BX, iter); }
                    if ((e & 8u) != 0u && by + 1u < BY) { enqueue(f, b + BX, iter); }
                    if ((e & 16u) != 0u) {
                        if (z > 0u) { enqueue(f, b - BX * BY, iter); }
                        if (z + 1u < D) { enqueue(f, b + BX * BY, iter); }
                    }
                    if (guard >= 600u) { enqueue(f, b, iter); }
                    if (MODE == 1u) { seedRelax(f, b, atomicLoad(&counters[3])); }
                }
            }
        }
        """;

    // Args Kernel: zero list `a` (3 = none), indirect args from list `b`, record history, advance the iteration.
    string PrepKernel => Common + """
        @group(0) @binding(0) var<storage, read_write> counters: array<u32>;
        @group(0) @binding(1) var<storage, read_write> args: array<u32>;
        @group(0) @binding(2) var<storage, read_write> history: array<u32>;
        @group(0) @binding(3) var<uniform> P: Params;
        @compute @workgroup_size(1)
        fn main() {
            if (P.a < 3u) { counters[P.a] = 0u; }
            let c = counters[P.b];
            args[0] = min(c, 65535u);
            args[1] = max(1u, (c + 65534u) / 65535u);
            args[2] = 1u;
            history[counters[4] % HIST] = c;
            counters[4] = counters[4] + 1u;
        }
        """;

    // Toggle Kernel: thread (toggle i, field f). Entry = tile | (newPassable << 31).
    string ToggleKernel => Common + $$"""
        @group(0) @binding(0) var<storage, read_write> tiles: array<atomic<u32>>;
        @group(0) @binding(1) var<storage, read_write> dist: array<atomic<u32>>;
        @group(0) @binding(2) var<storage, read_write> marks: array<atomic<u32>>;
        @group(0) @binding(3) var<storage, read_write> list0: array<u32>;
        @group(0) @binding(4) var<storage, read_write> list2: array<u32>;
        @group(0) @binding(5) var<storage, read_write> counters: array<atomic<u32>>;
        @group(0) @binding(6) var<storage, read> toggles: array<u32>;
        @group(0) @binding(7) var<uniform> P: Params;   // a = toggle count, b = fields in batch, c = groupsX*64
        fn inval(f: u32, nb: u32, iter: u32) {
            if (atomicMax(&marks[f * NBF + nb], iter) < iter) { let idx = atomicAdd(&counters[0], 1u); list0[idx] = (f << 20u) | nb; }
        }
        fn seedRelax(f: u32, nb: u32, epoch: u32) {
            if (atomicMax(&marks[{{FPB}}u * NBF + f * NBF + nb], epoch) < epoch) { let idx = atomicAdd(&counters[2], 1u); list2[idx] = (f << 20u) | nb; }
        }
        @compute @workgroup_size(64)
        fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
            let id = gid.y * P.c + gid.x;
            if (id >= P.a * P.b) { return; }
            let i = id / P.b; let f = id % P.b;
            let e = toggles[i];
            let t = e & 0x7FFFFFFFu;
            let open = (e >> 31u) != 0u;
            let bit = 1u << ((t & 3u) * 8u);
            if (f == 0u) { if (open) { atomicOr(&tiles[t >> 2u], bit); } else { atomicAnd(&tiles[t >> 2u], ~bit); } }
            let x = t % W; let y = (t / W) % H; let z = t / PLANE;
            let bx = x / 16u; let by = y / 16u;
            let b = bx + BX * (by + BY * z);
            let iter = atomicLoad(&counters[4]);
            let epoch = atomicLoad(&counters[3]);
            if (!open) {
                atomicOr(&dist[f * HALFN + (t >> 1u)], 0xFFFFu << ((t & 1u) * 16u));
                inval(f, b, iter);
                if (bx > 0u) { inval(f, b - 1u, iter); }
                if (bx + 1u < BX) { inval(f, b + 1u, iter); }
                if (by > 0u) { inval(f, b - BX, iter); }
                if (by + 1u < BY) { inval(f, b + BX, iter); }
            }
            seedRelax(f, b, epoch);
        }
        """;

    string FillKernel => Common + """
        @group(0) @binding(0) var<storage, read_write> dist: array<u32>;
        @group(0) @binding(1) var<uniform> P: Params;  // a = words, c = groupsX*256
        @compute @workgroup_size(256)
        fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
            let base = (gid.y * P.c + gid.x) * 8u;
            for (var k = 0u; k < 8u; k++) { let i = base + k; if (i < P.a) { dist[i] = 0xFFFFFFFFu; } }
        }
        """;

    string SeedKernel => Common + """
        @group(0) @binding(0) var<storage, read_write> dist: array<u32>;
        @group(0) @binding(1) var<storage, read_write> list2: array<u32>;
        @group(0) @binding(2) var<storage, read_write> counters: array<atomic<u32>>;
        @group(0) @binding(3) var<storage, read> goals: array<u32>;
        @group(0) @binding(4) var<uniform> P: Params;  // a = fields
        @compute @workgroup_size(64)
        fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
            let f = gid.x;
            if (f >= P.a) { return; }
            let g = goals[f];
            if (g == 0xFFFFFFFFu) { return; }
            let wi = f * HALFN + (g >> 1u);
            dist[wi] = dist[wi] & ~(0xFFFFu << ((g & 1u) * 16u));
            let b = (g % W) / 16u + BX * (((g / W) % H) / 16u + BY * (g / PLANE));
            let idx = atomicAdd(&counters[2], 1u);
            list2[idx] = (f << 20u) | b;
        }
        """;

    // Move Kernel: one thread per unit, identical rule to the CPU (first neighbour in fixed order with d-1).
    string MoveKernel => Common + $$"""
        @group(0) @binding(0) var<storage, read> tiles: array<u32>;
        @group(0) @binding(1) var<storage, read> distA: array<u32>;
        @group(0) @binding(2) var<storage, read> distB: array<u32>;
        @group(0) @binding(3) var<storage, read_write> pos: array<u32>;
        @group(0) @binding(4) var<storage, read> field: array<u32>;
        @group(0) @binding(5) var<uniform> P: Params; // a = units
        fn tileByte(i: u32) -> u32 { return (tiles[i >> 2u] >> ((i & 3u) * 8u)) & 0xFFu; }
        fn dd(f: u32, i: u32) -> u32 {
            var w: u32;
            if (f < {{FPB}}u) { w = distA[f * HALFN + (i >> 1u)]; } else { w = distB[(f - {{FPB}}u) * HALFN + (i >> 1u)]; }
            return (w >> ((i & 1u) * 16u)) & 0xFFFFu;
        }
        @compute @workgroup_size(256)
        fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
            let u = gid.x;
            if (u >= P.a) { return; }
            let fe = field[u];
            let pe = pos[u];
            let p = pe & 0x0FFFFFFFu;
            if (fe == 0xFFFFFFFFu) { pos[u] = p; return; }
            let d = dd(fe, p);
            if (d == 0u) { pos[u] = p | (1u << 28u); return; }      // arrived
            if (d == INF) { pos[u] = p | (2u << 28u); return; }     // no route (repath)
            let want = d - 1u;
            var nx = p;
            if (dd(fe, p - 1u) == want) { nx = p - 1u; }
            else if (dd(fe, p + 1u) == want) { nx = p + 1u; }
            else if (dd(fe, p - W) == want) { nx = p - W; }
            else if (dd(fe, p + W) == want) { nx = p + W; }
            else if ((tileByte(p) & 2u) != 0u) {
                if (p >= PLANE && (tileByte(p - PLANE) & 3u) == 3u && dd(fe, p - PLANE) == want) { nx = p - PLANE; }
                else if (p + PLANE < PLANE * D && (tileByte(p + PLANE) & 3u) == 3u && dd(fe, p + PLANE) == want) { nx = p + PLANE; }
            }
            var st = 0u;
            if (dd(fe, nx) == 0u) { st = 1u; }
            pos[u] = nx | (st << 28u);
        }
        """;

    public GpuFlow(WgpuDevice dev, World w, int[] goals, int maxUnits, int maxToggles)
    {
        _d = dev; _w = w; Goals = goals; FieldCount = goals.Length;
        _nb = (w.W / 16) * (w.H / 16) * w.D;
        RebuildAbove = w.N / 2000;
        if (_nb >= 1 << 20) throw new ArgumentException("too many blocks for list encoding");
        Batches = (FieldCount + FPB - 1) / FPB;
        ulong tileBytes = (ulong)((w.N + 3) / 4 * 4);
        Tiles = dev.CreateBufferRaw(tileBytes, BufferKind.Storage, "tiles", false);
        Params = dev.CreateBufferRaw(64 * 256, BufferKind.Uniform, "params", false);
        Toggles = dev.CreateBufferRaw((ulong)Math.Max(16, maxToggles * 4), BufferKind.Storage, "toggles", false);
        UnitPos = dev.CreateBufferRaw((ulong)Math.Max(16, maxUnits * 4), BufferKind.Storage, "unitPos", false);
        UnitField = dev.CreateBufferRaw((ulong)Math.Max(16, maxUnits * 4), BufferKind.Storage, "unitField", false);
        UnitOut = UnitPos;
        UnitRead = dev.CreateBufferRaw((ulong)Math.Max(16, maxUnits * 4), BufferKind.ReadbackStaging, "unitRead", false);
        CountRead = dev.CreateBufferRaw(Hist * 4 + 64, BufferKind.ReadbackStaging, "countRead", false);
        DeviceBytes = (long)tileBytes + 64 * 256 + maxToggles * 4L + maxUnits * 12L + Hist * 4 + 64;
        dev.Write(Tiles, 0, w.T, (nuint)w.N); // tile bytes are uploaded as-is (bits: pass, stair, dyn)

        BindingKind S = BindingKind.StorageReadWrite, R = BindingKind.StorageRead, Ud = BindingKind.UniformDynamic;
        _relax = dev.CreateKernel("relax", BlockKernel(0), "main", [R, S, S, S, S, S, S, Ud]);
        _inval = dev.CreateKernel("inval", BlockKernel(1), "main", [R, S, S, S, S, S, S, Ud]);
        _prep = dev.CreateKernel("prep", PrepKernel, "main", [S, S, S, Ud]);
        _toggle = dev.CreateKernel("toggle", ToggleKernel, "main", [S, S, S, S, S, S, R, Ud]);
        _fill = dev.CreateKernel("fill", FillKernel, "main", [S, Ud]);
        _seed = dev.CreateKernel("seed", SeedKernel, "main", [S, S, S, R, Ud]);
        _move = dev.CreateKernel("move", MoveKernel, "main", [R, R, R, S, R, Ud]);

        // params: relax/inval slots
        var pbuf = new uint[64 * 64];
        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) { int s = (a * 3 + b) * 64; pbuf[s] = (uint)a; pbuf[s + 1] = (uint)b; }
        for (int z = 0; z < 4; z++) for (int a = 0; a < 3; a++) { int s = (16 + z * 4 + a) * 64; pbuf[s] = (uint)z; pbuf[s + 1] = (uint)a; }
        fixed (uint* p = pbuf) dev.Write(Params, 0, p, (nuint)(pbuf.Length * 4));

        ulong fieldWords = (ulong)w.N / 2;
        _b = new Batch[Batches];
        for (int i = 0; i < Batches; i++)
        {
            var bt = new Batch { First = i * FPB, Count = Math.Min(FPB, FieldCount - i * FPB) };
            ulong distBytes = (ulong)bt.Count * fieldWords * 4;
            ulong listBytes = (ulong)bt.Count * (ulong)_nb * 4 + 16;
            bt.Dist = dev.CreateBufferRaw(distBytes, BufferKind.Storage, $"dist{i}", false);
            bt.Marks = dev.CreateBufferRaw((ulong)FPB * 2 * (ulong)_nb * 4, BufferKind.Storage, $"marks{i}", false);
            bt.L0 = dev.CreateBufferRaw(listBytes, BufferKind.Storage, $"l0_{i}", false);
            bt.L1 = dev.CreateBufferRaw(listBytes, BufferKind.Storage, $"l1_{i}", false);
            bt.L2 = dev.CreateBufferRaw(listBytes, BufferKind.Storage, $"l2_{i}", false);
            bt.Counters = dev.CreateBufferRaw(64, BufferKind.Storage, $"cnt{i}", false);
            bt.Args = dev.CreateBufferRaw(64, BufferKind.StorageIndirect, $"args{i}", false);
            bt.History = dev.CreateBufferRaw(Hist * 4, BufferKind.Storage, $"hist{i}", false);
            bt.Goals = dev.CreateBufferRaw(FPB * 4, BufferKind.Storage, $"goals{i}", false);
            var gl = new uint[FPB];
            for (int k = 0; k < FPB; k++) gl[k] = k < bt.Count ? (uint)goals[bt.First + k] : 0xFFFFFFFFu;
            fixed (uint* p = gl) dev.Write(bt.Goals, 0, p, FPB * 4);
            bt.Relax = dev.CreateBindings(_relax, [(Tiles, 0, 0), (bt.Dist, 0, 0), (bt.Marks, 0, 0), (bt.L0, 0, 0), (bt.L1, 0, 0), (bt.L2, 0, 0), (bt.Counters, 0, 0), (Params, 0, 16)]);
            bt.Inval = dev.CreateBindings(_inval, [(Tiles, 0, 0), (bt.Dist, 0, 0), (bt.Marks, 0, 0), (bt.L0, 0, 0), (bt.L1, 0, 0), (bt.L2, 0, 0), (bt.Counters, 0, 0), (Params, 0, 16)]);
            bt.Prep = dev.CreateBindings(_prep, [(bt.Counters, 0, 0), (bt.Args, 0, 0), (bt.History, 0, 0), (Params, 0, 16)]);
            bt.Toggle = dev.CreateBindings(_toggle, [(Tiles, 0, 0), (bt.Dist, 0, 0), (bt.Marks, 0, 0), (bt.L0, 0, 0), (bt.L2, 0, 0), (bt.Counters, 0, 0), (Toggles, 0, 0), (Params, 0, 16)]);
            bt.Fill = dev.CreateBindings(_fill, [(bt.Dist, 0, 0), (Params, 0, 16)]);
            bt.Seed = dev.CreateBindings(_seed, [(bt.Dist, 0, 0), (bt.L2, 0, 0), (bt.Counters, 0, 0), (bt.Goals, 0, 0), (Params, 0, 16)]);
            DeviceBytes += (long)distBytes + FPB * 2L * _nb * 4 + 3L * (long)listBytes + 128 + Hist * 4 + FPB * 4;
            _b[i] = bt;
        }
        if (dev.Capabilities.TimestampQuery)
        {
            QuerySet = dev.CreateTimestampQuerySet(256);
            _tsResolve = dev.CreateBufferRaw(256 * 8, BufferKind.Timestamps, "tsResolve", false);
            _tsRead = dev.CreateBufferRaw(256 * 8, BufferKind.ReadbackStaging, "tsRead", false);
        }
        else { _tsResolve = null!; _tsRead = null!; }
        dev.WaitIdle();
    }

    public void BindUnits(int units)
    {
        if ((ulong)units * 4 > UnitPos.Size) throw new ArgumentException("unit capacity exceeded");
        _units = units;
        _moveBg?.Dispose();
        var distB = Batches > 1 ? _b[1].Dist : _b[0].Dist;
        _moveBg = _d.CreateBindings(_move, [(Tiles, 0, 0), (_b[0].Dist, 0, 0), (distB, 0, 0), (UnitPos, 0, 0), (UnitField, 0, 0), (Params, 0, 16)]);
    }

    // dynamic params written per call (slots 40..63 are scratch for per-call params)
    int _scratchSlot;
    uint ScratchParams(uint a, uint b, uint c, uint d)
    {
        uint slot = (uint)(40 + (_scratchSlot++ % 24));
        var p = stackalloc uint[4] { a, b, c, d };
        _d.Write(Params, slot * 256, p, 16);
        return slot * 256;
    }

    void ResetCounters(Batch bt)
    {
        var c = stackalloc uint[16];
        for (int i = 0; i < 16; i++) c[i] = 0;
        c[3] = _epoch; c[4] = _iter;
        _d.Write(bt.Counters, 0, c, 64);
    }

    int _tsUsed;
    WgpuBatch Begin()
    {
        var b = (WgpuBatch)_d.BeginBatch();
        b.TimestampQuerySet = QuerySet;
        b.BeginPass(_tsUsed < 128 ? _tsUsed++ : -1);
        return b;
    }

    void RecordIterations(WgpuBatch cb, IKernel k, Func<Batch, IBindingSet> bg, int[] inList, int[] outList, int iters, bool[] active)
    {
        for (int it = 0; it < iters; it++)
        {
            for (int bi = 0; bi < Batches; bi++)
            {
                if (!active[bi]) continue;
                var bt = _b[bi];
                cb.SetKernel(k, bg(bt), Slot(inList[bi], outList[bi]));
                cb.DispatchIndirect(bt.Args, 0);
                cb.SetKernel(_prep, bt.Prep, PrepSlot(inList[bi], outList[bi]));
                cb.Dispatch(1);
                int nin = outList[bi];
                outList[bi] = inList[bi] == 2 ? (nin == 0 ? 1 : 0) : inList[bi];
                inList[bi] = nin;
            }
            _iter++;
        }
    }

    /// <summary>Runs work-list iterations until every active batch's list is empty. Returns iterations.</summary>
    int Converge(IKernel k, Func<Batch, IBindingSet> bg, int[] inList, int[] outList, bool[] active, ref int submits, ref int dispatches, double* gpuMs)
    {
        // All active batches are recorded every iteration (finished ones run empty indirect dispatches) so their
        // iteration counters stay in lock-step with _iter; the loop ends when every batch produced an empty list.
        int total = 0;
        var done = new bool[Batches];
        var act = active;
        while (true)
        {
            bool any = false;
            for (int i = 0; i < Batches; i++) if (active[i] && !done[i]) any = true;
            if (!any) break;
            uint iterStart = _iter;
            var cb = Begin();
            RecordIterations(cb, k, bg, inList, outList, IterPerSubmit, act);
            cb.EndPass();
            dispatches += cb.Dispatches;
            // copy history slots of this submit for all active batches
            int n = IterPerSubmit;
            int slot0 = (int)(iterStart % Hist);
            int bi2 = 0;
            for (int i = 0; i < Batches; i++)
            {
                if (!act[i]) continue;
                int cnt = Math.Min(n, Hist - slot0);
                cb.Copy(_b[i].History, (ulong)slot0 * 4, CountRead, (ulong)(bi2 * IterPerSubmit) * 4, (ulong)cnt * 4);
                if (cnt < n) cb.Copy(_b[i].History, 0, CountRead, (ulong)(bi2 * IterPerSubmit + cnt) * 4, (ulong)(n - cnt) * 4);
                bi2++;
            }
            _d.Submit(cb).Wait();
            submits++;
            CountRead.BeginMap(false, 0, (nuint)(bi2 * IterPerSubmit * 4));
            CountRead.FinishMap();
            var h = (uint*)CountRead.ConstMappedRange(0, (nuint)(bi2 * IterPerSubmit * 4));
            bi2 = 0;
            int maxIt = 0;
            for (int i = 0; i < Batches; i++)
            {
                if (!act[i]) continue;
                if (done[i]) { bi2++; continue; }
                int z = -1;
                for (int j = 0; j < n; j++) if (h[bi2 * n + j] == 0) { z = j; break; }
                for (int j = 0; j < n; j++) LastBlockVisits += h[bi2 * n + j];
                if (z >= 0) { done[i] = true; maxIt = Math.Max(maxIt, z + 1); } else maxIt = n;
                bi2++;
            }
            CountRead.Unmap();
            total += maxIt;
        }
        return total;
    }

    public void ResetTimestamps() => _tsUsed = 0;

    /// <summary>Sum of GPU pass times recorded since ResetTimestamps (blocking).</summary>
    public double ReadGpuMs()
    {
        if (QuerySet == 0 || _tsUsed == 0) return double.NaN;
        var b = (WgpuBatch)_d.BeginBatch();
        b.TimestampQuerySet = QuerySet;
        b.ResolveTimestamps(0, (uint)_tsUsed * 2, _tsResolve, 0);
        b.Copy(_tsResolve, 0, _tsRead, 0, (ulong)_tsUsed * 16);
        _d.Submit(b).Wait();
        _tsRead.BeginMap(false, 0, (nuint)_tsUsed * 16);
        _tsRead.FinishMap();
        var t = (ulong*)_tsRead.ConstMappedRange(0, (nuint)_tsUsed * 16);
        double s = 0;
        for (int i = 0; i < _tsUsed; i++) s += (t[2 * i + 1] - t[2 * i]) * _d.TimestampPeriodNs / 1e6;
        _tsRead.Unmap();
        _tsUsed = 0;
        return s;
    }

    /// <summary>Full build of every field from scratch (synchronous, exact).</summary>
    public void BuildAll()
    {
        long t0 = Stopwatch.GetTimestamp();
        ResetTimestamps(); LastBlockVisits = 0;
        int submits = 0, dispatches = 0; LastBlockVisits = 0;
        _epoch++;
        var cb = Begin();
        foreach (var bt in _b)
        {
            ResetCounters(bt);
            ulong words = (ulong)bt.Count * (ulong)_w.N / 2;
            ulong groups = (words + 256 * 8 - 1) / (256 * 8);
            uint gx = (uint)Math.Min(groups, 65535UL), gy = (uint)((groups + gx - 1) / gx);
            cb.SetKernel(_fill, bt.Fill, ScratchParams((uint)words, 0, gx * 256, 0));
            cb.Dispatch(gx, gy);
            cb.SetKernel(_seed, bt.Seed, ScratchParams((uint)bt.Count, 0, 0, 0));
            cb.Dispatch(1);
            cb.SetKernel(_prep, bt.Prep, PrepSlot(3, 2));
            cb.Dispatch(1);
        }
        _iter++;
        cb.EndPass();
        dispatches += cb.Dispatches;
        _d.Submit(cb); submits++;
        var inL = new int[Batches]; var outL = new int[Batches]; var act = new bool[Batches];
        for (int i = 0; i < Batches; i++) { inL[i] = 2; outL[i] = 0; act[i] = true; }
        double g = 0;
        LastInvalIterations = 0;
        LastIterations = Converge(_relax, b => b.Relax, inL, outL, act, ref submits, ref dispatches, &g);
        LastSubmits = submits; LastDispatches = dispatches;
        LastWallMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>Applies toggles (tiles already flipped on the CPU World; entries carry the new state) and repairs every field exactly.</summary>
    public void Repair(int* tog, int n)
    {
        long t0 = Stopwatch.GetTimestamp();
        int submits = 0, dispatches = 0; LastBlockVisits = 0;
        if (n == 0) { LastIterations = LastInvalIterations = 0; LastSubmits = 0; LastDispatches = 0; LastWallMs = 0; return; }
        var e = stackalloc uint[1];
        uint* entries = (uint*)NativeAlloc(n);
        for (int i = 0; i < n; i++) entries[i] = (uint)tog[i] | ((_w.T[tog[i]] & World.Pass) != 0 ? 0x80000000u : 0u);
        _d.Write(Toggles, 0, entries, (nuint)(n * 4));
        LastWasRebuild = n > RebuildAbove;
        if (LastWasRebuild)
        {
            // non-local change: flip the tiles (toggle Kernel on one field of batch 0) and rebuild every Field
            var cr = Begin();
            uint grp = (uint)((n + 63) / 64), rx = Math.Min(grp, 65535u), ry = (grp + rx - 1) / rx;
            cr.SetKernel(_toggle, _b[0].Toggle, ScratchParams((uint)n, 1, rx * 64, 0));
            cr.Dispatch(rx, ry);
            cr.EndPass();
            _d.Submit(cr);
            BuildAll();
            LastWallMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return;
        }
        _epoch++;
        var cb = Begin();
        foreach (var bt in _b)
        {
            ResetCounters(bt);
            ulong threads = (ulong)n * (ulong)bt.Count;
            ulong groups = (threads + 63) / 64;
            uint gx = (uint)Math.Min(groups, 65535UL), gy = (uint)((groups + gx - 1) / gx);
            cb.SetKernel(_toggle, bt.Toggle, ScratchParams((uint)n, (uint)bt.Count, gx * 64, 0));
            cb.Dispatch(gx, gy);
            cb.SetKernel(_prep, bt.Prep, PrepSlot(3, 0));
            cb.Dispatch(1);
            // note: only the first batch's toggle Kernel flips tiles (f == 0 in batch 0 suffices); see FlipOnce
        }
        _iter++;
        cb.EndPass();
        dispatches += cb.Dispatches;
        _d.Submit(cb); submits++;
        var inL = new int[Batches]; var outL = new int[Batches]; var act = new bool[Batches];
        for (int i = 0; i < Batches; i++) { inL[i] = 0; outL[i] = 1; act[i] = true; }
        double g = 0;
        LastInvalIterations = Converge(_inval, b => b.Inval, inL, outL, act, ref submits, ref dispatches, &g);
        // relax phase from the seed list (list 2)
        cb = Begin();
        foreach (var bt in _b)
        {
            cb.SetKernel(_prep, bt.Prep, PrepSlot(3, 2));
            cb.Dispatch(1);
        }
        _iter++;
        cb.EndPass();
        dispatches += cb.Dispatches;
        _d.Submit(cb); submits++;
        for (int i = 0; i < Batches; i++) { inL[i] = 2; outL[i] = 0; }
        LastIterations = Converge(_relax, b => b.Relax, inL, outL, act, ref submits, ref dispatches, &g);
        LastSubmits = submits; LastDispatches = dispatches;
        LastWallMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _ = e;
    }

    uint* _ent; int _entCap;
    void* NativeAlloc(int n)
    {
        if (n > _entCap) { if (_ent != null) Mem.Free(_ent, _entCap); _entCap = Math.Max(n, 1024); _ent = Mem.Alloc<uint>(_entCap, false); }
        return _ent;
    }

    /// <summary>Records the move Kernel and the position readback copy; returns after submit (deferred).</summary>
    public IFence SubmitMove(bool readback)
    {
        var cb = Begin();
        cb.SetKernel(_move, _moveBg, ScratchParams((uint)_units, 0, 0, 0));
        cb.Dispatch((uint)((_units + 255) / 256));
        cb.EndPass();
        if (readback) cb.Copy(UnitPos, 0, UnitRead, 0, (ulong)_units * 4);
        return _d.Submit(cb);
    }

    public void UploadUnits(int* pos, int* field, int n)
    {
        _d.Write(UnitPos, 0, pos, (nuint)(n * 4));
        _d.Write(UnitField, 0, field, (nuint)(n * 4));
    }

    public void UploadFields(int* field, int n) => _d.Write(UnitField, 0, field, (nuint)(n * 4));
    public void UploadPositions(int* pos, int n) => _d.Write(UnitPos, 0, pos, (nuint)(n * 4));

    public uint* MapUnits()
    {
        UnitRead.BeginMap(false, 0, (nuint)(_units * 4));
        UnitRead.FinishMap();
        return (uint*)UnitRead.ConstMappedRange(0, (nuint)(_units * 4));
    }
    public void UnmapUnits() => UnitRead.Unmap();

    /// <summary>Blocking readback of field f into dst (N u16).</summary>
    public void ReadField(int f, ushort* dst)
    {
        var bt = _b[f / FPB];
        int lf = f % FPB;
        ulong bytes = (ulong)_w.N * 2;
        ulong chunk = 64UL << 20;
        var stg = _d.CreateBufferRaw(chunk, BufferKind.ReadbackStaging, "fieldRead", false);
        for (ulong off = 0; off < bytes; off += chunk)
        {
            ulong n = Math.Min(chunk, bytes - off);
            var cb = (WgpuBatch)_d.BeginBatch();
            cb.Copy(bt.Dist, (ulong)lf * bytes + off, stg, 0, n);
            _d.Submit(cb).Wait();
            stg.BeginMap(false, 0, (nuint)n); stg.FinishMap();
            System.Runtime.InteropServices.NativeMemory.Copy(stg.ConstMappedRange(0, (nuint)n), (byte*)dst + off, (nuint)n);
            stg.Unmap();
        }
        stg.Dispose();
    }

    public void Dispose()
    {
        foreach (var bt in _b)
        {
            bt.Relax.Dispose(); bt.Inval.Dispose(); bt.Prep.Dispose(); bt.Toggle.Dispose(); bt.Fill.Dispose(); bt.Seed.Dispose();
            bt.Dist.Dispose(); bt.Marks.Dispose(); bt.L0.Dispose(); bt.L1.Dispose(); bt.L2.Dispose(); bt.Counters.Dispose(); bt.Args.Dispose(); bt.History.Dispose(); bt.Goals.Dispose();
        }
        _moveBg?.Dispose();
        _relax.Dispose(); _inval.Dispose(); _prep.Dispose(); _toggle.Dispose(); _fill.Dispose(); _seed.Dispose(); _move.Dispose();
        Tiles.Dispose(); Params.Dispose(); Toggles.Dispose(); UnitPos.Dispose(); UnitField.Dispose(); UnitRead.Dispose(); CountRead.Dispose();
        _tsResolve?.Dispose(); _tsRead?.Dispose();
        if (QuerySet != 0) Native.Wgpu.wgpuQuerySetRelease(QuerySet);
        if (_ent != null) Mem.Free(_ent, _entCap);
    }
}
