using System.Diagnostics;
using System.Globalization;
using GpuPath.Backend;
using GpuPath.Bench;
using GpuPath.Core;
using GpuPath.Cpu;
using GpuPath.Gpu;
using GpuPath.Native;

namespace GpuPath;

/// <summary>Workload: shared goals (flow-field friendly), unique goals, or mixed (first half shared).</summary>
public sealed record Workload(string Name, int Kind, double Churn, double Unreach) // Kind 0 shared, 1 unique, 2 mixed
{
    public bool NeedsUnique => Kind != 0;
}

public static unsafe class Program
{
    const int GoalCount = 64, PocketGoals = 6, MainGoals = GoalCount - PocketGoals;
    static World W = null!;
    static Regions R = null!;
    static WorkerPool Pool = null!;
    /// <summary>"nosearch" arg: skip the plain A* / HPA* baselines (big World: hours of spawn bursts); hybrids keep HPA*.</summary>
    static bool NoSearch;
    static int[] Goals = null!;
    static int[] Pocket = null!;
    static int MainRep;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static StreamWriter? Csv;

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = Inv; CultureInfo.CurrentCulture = Inv;
        string mode = args.Length > 0 ? args[0] : "verify";
        string world = args.Length > 1 ? args[1] : "small";
        Pool = new WorkerPool(Environment.ProcessorCount);
        Console.WriteLine($"# gpu-pathfinding prototype  mode={mode} world={world}  {DateTime.Now:yyyy-MM-dd HH:mm}  cores={Environment.ProcessorCount}  .NET {Environment.Version}");
        NoSearch = args.Contains("nosearch");
        Setup(world);
        switch (mode)
        {
            case "verify": Verify(); break;
            case "probe": Probe(); break;
            case "matrix": Matrix(args.Length > 2 ? int.Parse(args[2]) : 30); break;
            case "sweep": Sweep(); break;
            case "threads": ThreadsRun(); break;
            case "hybrid": HybridRun(); break;
            default: Console.WriteLine("modes: verify|probe|matrix|sweep|threads|hybrid  world: small|big"); return 1;
        }
        Csv?.Dispose();
        return 0;
    }

    static void OpenCsv(string name)
    {
        Directory.CreateDirectory("results");
        bool exists = File.Exists(name);
        Csv = new StreamWriter(name, append: true);
        if (!exists) Csv.WriteLine("world,approach,units,workload,toggles,threads,mean,p50,p99,max,regions,repair,gather,process,move,reqPerTick,unreachPerTick,spawnMs,cores,memMB,gpuMB,extra");
    }

    // ---------------------------------------------------------------------------------------------- setup
    static void Setup(string world)
    {
        long t0 = Stopwatch.GetTimestamp();
        W = world == "big" ? World.Generate(512, 512, 64, 12345) : World.Generate(256, 256, 32, 12345);
        double gen = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        R = new Regions(W);
        t0 = Stopwatch.GetTimestamp();
        R.BuildAll(Pool, Pool.Threads);
        double rb = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        // main region = the most common region among passable tiles
        var count = new Dictionary<int, int>();
        for (int i = 0; i < W.N; i++) { int r = R.RegionOf(i); if (r >= 0) count[r] = count.GetValueOrDefault(r) + 1; }
        int main = count.MaxBy(kv => kv.Value).Key;
        long pass = count.Values.Sum(v => (long)v);
        var pocket = new List<int>();
        for (int i = 0; i < W.N; i++) { int r = R.RegionOf(i); if (r >= 0 && r != main && (W.T[i] & World.Dyn) == 0) pocket.Add(i); }
        Pocket = pocket.ToArray();
        var rng = new Rng(777);
        MainRep = -1;
        while (MainRep < 0) { int t = rng.Int(W.N); if (W.P(t) && (W.T[t] & World.Dyn) == 0 && R.RegionOf(t) == main) MainRep = t; }
        Goals = new int[GoalCount];
        Goals[0] = MainRep;
        for (int i = 1; i < MainGoals; i++) Goals[i] = PickMain(ref rng);
        for (int i = 0; i < PocketGoals; i++) Goals[MainGoals + i] = Pocket[rng.Int(Pocket.Length)];
        Console.WriteLine($"# World {W.W}x{W.H}x{W.D} = {W.N:N0} tiles, gen {gen:F0} ms; blocked {W.BlockedFraction:P1}; stairs {W.StairCount:N0}; doors {W.DoorCount:N0}; dynamic tiles {W.DynCount:N0} ({(double)W.DynCount / W.N:P2})");
        Console.WriteLine($"# Regions: {R.NodeCount:N0} chunk components, {R.EdgeTotal:N0} abstract edges, {count.Count:N0} regions; main region {count[main]:N0} tiles ({(double)count[main] / pass:P1} of passable); pocket tiles {Pocket.Length:N0}; full build {rb:F1} ms (label {R.LastLabelMs:F1} / edges {R.LastEdgeMs:F1} / graph {R.LastGraphMs:F1}); {R.Bytes / 1e6:F0} MB");
        Console.WriteLine("# Moves: 4-neighbour in-plane + vertical between vertically adjacent stair tiles; uniform cost 1; tie-break order -x,+x,-y,+y,-z,+z");
    }

    static int MainRegion => R.RegionOf(MainRep);

    static int PickMain(ref Rng rng)
    {
        int main = MainRegion;
        while (true) { int t = rng.Int(W.N); if ((W.T[t] & (World.Pass | World.Dyn)) == World.Pass && R.RegionOf(t) == main) return t; }
    }

    static WgpuDevice? _dev;
    static WgpuDevice Dev
    {
        get
        {
            if (_dev != null) return _dev;
            _dev = new WgpuDevice(Wgpu.BackendType_Vulkan, false);
            var c = _dev.Capabilities;
            Console.WriteLine($"# GPU: {c.AdapterName} via {c.BackendName}; maxBuffer {c.MaxBufferSize / (1 << 20)} MiB, maxStorageBinding {c.MaxStorageBufferBindingSize} B, timestamps {c.TimestampQuery}");
            return _dev;
        }
    }

    // ---------------------------------------------------------------------------------------------- tick harness
    sealed class RunResult
    {
        public List<double> Tick = new(), Regions = new(), Repair = new(), Gather = new(), Process = new(), Move = new();
        public double SpawnMs, Cores, ReqPerTick, UnreachPerTick, MemMB, GpuMB;
        public string Extra = "";
    }

    sealed class Harness
    {
        public Units U = null!;
        public Workload Wl = null!;
        public Rng Rng;
        public Request* Req;
        public int* Tog;
        public int TogCount, Threads;
        public int[] G = Goals; public int MainCount = MainGoals;
        long _reqs, _unreach; int _prev;
        public bool TogglesPending => _prev > 0;

        public int Gather()
        {
            int n = 0;
            var u = U;
            int half = u.Count / 2;
            for (int i = 0; i < u.Count; i++)
            {
                byte st = u.Status[i];
                bool need = st == St.Idle || st == St.Arrived || st == St.Unreachable;
                bool same = st == St.Repath;
                if (!need && !same && Wl.Churn > 0 && Rng.Dbl() < Wl.Churn) need = true;
                if (same) { Req[n++] = new Request { Unit = i, Goal = u.Goal[i], Field = u.Field[i] }; continue; }
                if (!need) continue;
                bool shared = Wl.Kind == 0 || (Wl.Kind == 2 && i < half);
                bool bad = Wl.Unreach > 0 && Rng.Dbl() < Wl.Unreach;
                if (shared)
                {
                    int f = bad ? MainCount + Rng.Int(G.Length - MainCount) : Rng.Int(MainCount);
                    Req[n++] = new Request { Unit = i, Goal = G[f], Field = f };
                }
                else
                {
                    int g = bad ? Pocket[Rng.Int(Pocket.Length)] : PickMain(ref Rng);
                    Req[n++] = new Request { Unit = i, Goal = g, Field = -1 };
                }
            }
            return n;
        }

        /// <summary>One Tick: toggles -> regions -> service repair -> requests -> plan -> move.</summary>
        public void Tick(Service s, RunResult? r)
        {
            long t0 = Stopwatch.GetTimestamp();
            s.BeginTick();
            int nt = TogCount > 0 ? OscToggle(ref Rng, TogCount, Tog, ref _prev) : 0;
            R.Update(Tog, nt, Pool, Threads);
            long t1 = Stopwatch.GetTimestamp();
            s.OnToggles(Tog, nt);
            long t2 = Stopwatch.GetTimestamp();
            int m = Gather();
            long t3 = Stopwatch.GetTimestamp();
            s.Process(Req, m);
            long t4 = Stopwatch.GetTimestamp();
            s.Move();
            long t5 = Stopwatch.GetTimestamp();
            if (r == null) return;
            double f = 1000.0 / Stopwatch.Frequency;
            r.Tick.Add((t5 - t0) * f); r.Regions.Add((t1 - t0) * f); r.Repair.Add((t2 - t1) * f); r.Gather.Add((t3 - t2) * f);
            r.Process.Add((t4 - t3) * f); r.Move.Add((t5 - t4) * f);
            _reqs += m; _unreach += s.Unreachable;
        }

        public void Finish(RunResult r) { r.ReqPerTick = (double)_reqs / r.Tick.Count; r.UnreachPerTick = (double)_unreach / r.Tick.Count; }
    }

    static Units Spawn(int n, ulong seed)
    {
        var u = new Units(n);
        var rng = new Rng(seed);
        for (int i = 0; i < n; i++) { u.Pos[i] = PickMain(ref rng); u.Status[i] = St.Idle; u.Field[i] = -1; u.Goal[i] = -1; }
        return u;
    }

    static RunResult Run(Func<Units, Service> make, int units, Workload wl, int toggles, int threads, int warm, int ticks, Action<Service, RunResult>? extra = null)
    {
        var u = Spawn(units, 99);
        var h = new Harness { U = u, Wl = wl, Rng = new Rng(4242), Req = Mem.Alloc<Request>(units), Tog = Mem.Alloc<int>(Math.Max(1, toggles)), TogCount = toggles, Threads = threads };
        var s = make(u);
        s.Threads = threads;
        var res = new RunResult();
        long t0 = Stopwatch.GetTimestamp();
        h.Tick(s, null); // spawn burst: every unit requests a path
        if (s is GpuFlowService gs0) gs0.Drain();
        res.SpawnMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        for (int i = 0; i < warm; i++) h.Tick(s, null);
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime; long w0 = Stopwatch.GetTimestamp();
        var gpuWait = new List<double>(); int ready = 0;
        var iters = new List<double>(); var gpuRep = new List<double>();
        for (int i = 0; i < ticks; i++)
        {
            h.Tick(s, res);
            if (s is GpuFlowService g) { gpuWait.Add(g.WaitMs); if (g.LastWasReady) ready++; iters.Add(g.RepairIters + g.RepairInvalIters); gpuRep.Add(g.RepairMs); }
        }
        proc.Refresh();
        double wall = Stopwatch.GetElapsedTime(w0).TotalMilliseconds;
        res.Cores = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / wall;
        h.Finish(res);
        res.MemMB = Mem.Live / 1e6;
        if (s is GpuFlowService gg)
        {
            res.GpuMB = gg.G.DeviceBytes / 1e6;
            res.Extra = $"gpuWait {gpuWait.Average():F3} ms, readyAtT+1 {ready}/{ticks}, repairIters {iters.Average():F1}, upload {gg.UploadMs:F3} ms, submit {gg.SubmitMs:F3} ms, apply {gg.ApplyMs:F3} ms";
        }
        if (s is HpaService hs) res.Extra = $"refines/tick ~{hs.Refines}, replans {hs.Replans}";
        if (s is AStarService a) res.Extra = $"A* expanded total {a.Expanded:N0}";
        extra?.Invoke(s, res);
        // odd Tick count leaves one oscillating toggle set applied: run one unmeasured Tick so the World, regions and the
        // shared Fields are back at the generated state before the next run
        if (h.TogglesPending) h.Tick(s, null);
        if (s is GpuFlowService gd) gd.Drain();
        s.Dispose(); u.Dispose(); Mem.Free(h.Req, units); Mem.Free(h.Tog, Math.Max(1, toggles));
        return res;
    }

    static string Fmt3(List<double> l) { var s = l.OrderBy(x => x).ToArray(); double P(double q) => s[Math.Min(s.Length - 1, (int)Math.Ceiling(q * s.Length) - 1)]; return $"{l.Average(),8:F2} {P(0.5),8:F2} {P(0.99),8:F2}"; }

    static void Report(string approach, int units, Workload wl, int toggles, int threads, RunResult r)
    {
        var s = r.Tick.OrderBy(x => x).ToArray();
        double P(double q) => s[Math.Min(s.Length - 1, (int)Math.Ceiling(q * s.Length) - 1)];
        string ws = $"{W.W}x{W.H}x{W.D}";
        Console.WriteLine($"RESULT {ws,-10} {approach,-18} u={units,6} {wl.Name,-5} tog={toggles,6} thr={threads,2} | tick mean {r.Tick.Average(),8:F2} p50 {P(0.5),8:F2} p99 {P(0.99),8:F2} max {s[^1],8:F2} | regions {r.Regions.Average(),7:F2} repair {r.Repair.Average(),7:F2} gather {r.Gather.Average(),6:F2} process {r.Process.Average(),7:F2} move {r.Move.Average(),6:F2} | req/t {r.ReqPerTick,7:F0} unreach/t {r.UnreachPerTick,6:F0} | spawn {r.SpawnMs,8:F0} ms | cores {r.Cores,5:F1} | mem {r.MemMB,6:F0} MB gpu {r.GpuMB,6:F0} MB | {r.Extra}");
        Csv?.WriteLine(string.Join(",", ws, approach, units, wl.Name, toggles, threads, r.Tick.Average().ToString("F3"), P(0.5).ToString("F3"), P(0.99).ToString("F3"), s[^1].ToString("F3"),
            r.Regions.Average().ToString("F3"), r.Repair.Average().ToString("F3"), r.Gather.Average().ToString("F3"), r.Process.Average().ToString("F3"), r.Move.Average().ToString("F3"),
            r.ReqPerTick.ToString("F0"), r.UnreachPerTick.ToString("F0"), r.SpawnMs.ToString("F0"), r.Cores.ToString("F2"), r.MemMB.ToString("F0"), r.GpuMB.ToString("F0"), "\"" + r.Extra + "\""));
        Csv?.Flush();
    }

    static readonly Workload WA = new("A", 0, 0, 0), WB = new("B", 1, 0, 0), WCs = new("C-s", 0, 0.03, 0), WCu = new("C-u", 1, 0.03, 0),
        WDs = new("D-s", 0, 0.03, 0.10), WDu = new("D-u", 1, 0.03, 0.10), WM = new("M", 2, 0.03, 0.10);

    /// <summary>Even Ticks toggle a fresh random set of dynamic tiles, odd Ticks revert it, so the World oscillates around its
    /// generated state instead of drifting to "half of all doors closed" over a long benchmark.</summary>
    static int OscToggle(ref Rng rng, int count, int* buf, ref int prev)
    {
        if (prev > 0) { W.Flip(buf, prev); int n = prev; prev = 0; return n; }
        prev = W.Toggle(ref rng, count, buf);
        return prev;
    }

    static int Toggles(double rate) => rate <= 0 ? 0 : Math.Max(1, Math.Min(W.DynCount / 2, (int)Math.Round(rate * W.N)));

    const int AStarMaxExp = 1 << 24, HpaPages = 64;
    static int AStarPages => (W.N + 4095) / 4096; // whole-world page pool (64 MB/worker on big)

    // ---------------------------------------------------------------------------------------------- matrix
    static void Matrix(int ticks)
    {
        OpenCsv("results/matrix.csv");
        int[] unitCounts = [10_000, 50_000, 100_000];
        int tog = Toggles(1e-5);
        int thr = Pool.Threads;
        Console.WriteLine($"# matrix: toggles/Tick {tog} (0.001% of tiles), threads {thr}, warm-up 5, measured {ticks} Ticks; columns are ms per Tick");
        Workload[] all = [WA, WB, WCs, WCu, WDs, WDu], shared = [WA, WCs, WDs];
        Console.WriteLine("# cpu-astar is run at 10k units only (plain A* expands ~0.5M tiles per long path on these maps; 50k/100k spawn bursts alone take minutes); scale per request is linear");
        foreach (int n in NoSearch ? [] : new[] { 10_000 })
            foreach (var wl in all)
                Report("cpu-astar", n, wl, tog, thr, Run(u => new AStarService(W, R, u, Pool, thr, AStarPages, AStarMaxExp), n, wl, tog, thr, 5, ticks));
        foreach (int n in NoSearch ? [] : unitCounts)
            foreach (var wl in all)
                Report("cpu-hpa", n, wl, tog, thr, Run(u => new HpaService(W, R, u, Pool, thr, HpaPages), n, wl, tog, thr, 5, ticks));
        {
            var f = new CpuFlowFields(W, Goals, Pool.Threads);
            long t0 = Stopwatch.GetTimestamp(); f.BuildAll(Pool, thr);
            Console.WriteLine($"# cpu-flow: built {GoalCount} Fields in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms, {f.FieldBytes / 1e6:F0} MB");
            foreach (int n in unitCounts)
                foreach (var wl in shared)
                    Report("cpu-flow", n, wl, tog, thr, Run(u => new FlowService(W, R, u, Pool, thr, f), n, wl, tog, thr, 5, ticks));
            f.Dispose();
        }
        {
            var g = new GpuFlow(Dev, W, Goals, 100_000, Math.Max(1, W.DynCount / 2));
            g.BuildAll();
            Console.WriteLine($"# gpu-flow: built {GoalCount} Fields in {g.LastWallMs:F0} ms ({g.LastIterations} iterations, {g.LastSubmits} submits, {g.LastDispatches} dispatches), device {g.DeviceBytes / 1e6:F0} MB");
            foreach (bool def in new[] { false, true })
                foreach (int n in unitCounts)
                    foreach (var wl in shared)
                        Report(def ? "gpu-flow-deferred" : "gpu-flow", n, wl, tog, thr, Run(u => new GpuFlowService(W, R, u, Pool, thr, g, def), n, wl, tog, thr, 5, ticks));
            g.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------- hybrid (mixed workload)
    static void HybridRun()
    {
        OpenCsv("results/hybrid.csv");
        int tog = Toggles(1e-5), thr = Pool.Threads;
        int[] unitCounts = [10_000, 50_000, 100_000];
        Console.WriteLine($"# hybrid: workload M = first half shared goals (64 Fields), second half unique goals; 3% churn; 10% unreachable; toggles {tog}");
        foreach (int n in unitCounts)
        {
            if (n == 10_000 && !NoSearch) Report("cpu-astar", n, WM, tog, thr, Run(u => new AStarService(W, R, u, Pool, thr, AStarPages, AStarMaxExp), n, WM, tog, thr, 5, 30));
            Report("cpu-hpa", n, WM, tog, thr, Run(u => new HpaService(W, R, u, Pool, thr, HpaPages), n, WM, tog, thr, 5, 30));
        }
        var f = new CpuFlowFields(W, Goals, Pool.Threads); f.BuildAll(Pool, thr);
        foreach (int n in unitCounts)
            Report("hyb-cpuflow+hpa", n, WM, tog, thr, Run(u => new HybridService(W, R, u, Pool, thr, new FlowService(W, R, u, Pool, thr, f), new HpaService(W, R, u, Pool, thr, HpaPages), n / 2, false, "hyb-cpuflow+hpa"), n, WM, tog, thr, 5, 30));
        f.Dispose();
        var g = new GpuFlow(Dev, W, Goals, 100_000, Math.Max(1, W.DynCount / 2)); g.BuildAll();
        foreach (int n in unitCounts)
            Report("hyb-gpuflow+hpa", n, WM, tog, thr, Run(u => new HybridService(W, R, u, Pool, thr, new GpuFlowService(W, R, u, Pool, thr, g, true), new HpaService(W, R, u, Pool, thr, HpaPages), n / 2, true, "hyb-gpuflow+hpa"), n, WM, tog, thr, 5, 30,
                (s, r) => r.Extra = $"overlap: unique-plan {((HybridService)s).UniqueMs:F2} ms, waited for GPU repair {((HybridService)s).JoinWaitMs:F2} ms (last Tick)"));
        g.Dispose();
    }

    // ---------------------------------------------------------------------------------------------- toggle sweep
    static void Sweep()
    {
        OpenCsv("results/sweep.csv");
        int thr = Pool.Threads, n = 100_000, ticks = 15;
        double[] rates = [0, 1e-5, 1e-4, 1e-3, 1e-2];
        Console.WriteLine($"# toggle sweep at {n} units; rates {string.Join(", ", rates.Select(r => $"{r:P3}"))} of tiles (capped at half of the {W.DynCount:N0} dynamic tiles)");
        foreach (double rate in NoSearch ? [] : rates)
        {
            int tog = Toggles(rate);
            Report("cpu-astar", 10_000, WCu, tog, thr, Run(u => new AStarService(W, R, u, Pool, thr, AStarPages, AStarMaxExp), 10_000, WCu, tog, thr, 3, ticks));
            Report("cpu-hpa", n, WCu, tog, thr, Run(u => new HpaService(W, R, u, Pool, thr, HpaPages), n, WCu, tog, thr, 3, ticks));
        }
        var f = new CpuFlowFields(W, Goals, Pool.Threads); f.BuildAll(Pool, thr);
        foreach (double rate in rates)
        {
            int tog = Toggles(rate);
            Report("cpu-flow", n, WCs, tog, thr, Run(u => new FlowService(W, R, u, Pool, thr, f), n, WCs, tog, thr, 3, ticks,
                (s, r) => r.Extra = $"regions dirty chunks {R.LastDirtyChunks}, last repair touched {f.LastRepairTouched:N0} tiles"));
        }
        f.Dispose();
        var g = new GpuFlow(Dev, W, Goals, n, Math.Max(1, W.DynCount / 2)); g.BuildAll();
        foreach (double rate in rates)
        {
            int tog = Toggles(rate);
            Report("gpu-flow-deferred", n, WCs, tog, thr, Run(u => new GpuFlowService(W, R, u, Pool, thr, g, true), n, WCs, tog, thr, 3, ticks));
        }
        g.Dispose();
    }

    static void ThreadsRun()
    {
        OpenCsv("results/threads.csv");
        int n = 100_000, tog = Toggles(1e-5);
        foreach (int thr in new[] { 1, 4, Pool.Threads })
        {
            Report("cpu-astar", 10_000, WCu, tog, thr, Run(u => new AStarService(W, R, u, Pool, thr, AStarPages, AStarMaxExp), 10_000, WCu, tog, thr, 2, 10));
            Report("cpu-hpa", n, WCu, tog, thr, Run(u => new HpaService(W, R, u, Pool, thr, HpaPages), n, WCu, tog, thr, 2, 10));
        }
        var f = new CpuFlowFields(W, Goals, Pool.Threads); f.BuildAll(Pool, Pool.Threads);
        foreach (int thr in new[] { 1, 4, Pool.Threads })
            Report("cpu-flow", n, WCs, tog, thr, Run(u => new FlowService(W, R, u, Pool, thr, f), n, WCs, tog, thr, 2, 10));
        f.Dispose();
    }

    // ---------------------------------------------------------------------------------------------- probe: building blocks
    static void Probe()
    {
        int thr = Pool.Threads;
        var rng = new Rng(5);
        Console.WriteLine("## Regions: full rebuild vs incremental update (16 threads)");
        foreach (double rate in new[] { 1e-5, 1e-4, 1e-3, 1e-2 })
        {
            int tog = Toggles(rate);
            int* tb = Mem.Alloc<int>(tog); int prevN = 0;
            var inc = new List<double>(); var lab = new List<double>(); var gr = new List<double>(); int dirty = 0;
            for (int i = 0; i < 12; i++)
            {
                int nt = OscToggle(ref rng, tog, tb, ref prevN);
                long t0 = Stopwatch.GetTimestamp(); R.Update(tb, nt, Pool, thr);
                if (i >= 2) { inc.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds); lab.Add(R.LastLabelMs + R.LastEdgeMs); gr.Add(R.LastGraphMs); dirty = R.LastDirtyChunks; }
            }
            Mem.Free(tb, tog);
            Console.WriteLine($"  toggles {tog,7} ({rate:P3}): incremental {inc.Average(),7:F2} ms (chunk relabel+faces {lab.Average():F2}, graph rebuild {gr.Average():F2}); dirty chunks {dirty}/{W.ChunkCount}");
        }
        var full = Stats.Time(1, 5, () => R.BuildAll(Pool, thr));
        Console.WriteLine($"  full rebuild: {Stats.Fmt(full)}");
        var full1 = Stats.Time(0, 2, () => R.BuildAll(Pool, 1));
        Console.WriteLine($"  full rebuild 1 thread: {Stats.Fmt(full1)}");
        // region check cost
        {
            int main = MainRegion; int k = 0;
            long t0 = Stopwatch.GetTimestamp();
            var r2 = new Rng(9);
            for (int i = 0; i < 1_000_000; i++) { int a = Goals[i & 63], b = Pocket[r2.Int(Pocket.Length)]; if (R.RegionOf(a) == R.RegionOf(b)) k++; }
            Console.WriteLine($"  region early-out: {Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1e6 / 1_000_000:F1} ns per request (1 thread) [{k}]");
        }

        Console.WriteLine("## CPU flow fields: full build vs incremental repair (64 Fields)");
        var f = new CpuFlowFields(W, Goals, Pool.Threads);
        var b16 = Stats.Time(1, 3, () => f.BuildAll(Pool, thr));
        Console.WriteLine($"  build 64 Fields, {thr} threads: {Stats.Fmt(b16)}  ({f.FieldBytes / 1e6:F0} MB)");
        var b1 = Stats.Time(0, 1, () => f.Build(0, 0));
        Console.WriteLine($"  build 1 Field, 1 thread: {Stats.Fmt(b1)}");
        int maxD = 0; for (int i = 0; i < MainGoals; i++) maxD = Math.Max(maxD, f.MaxFinite(i));
        Console.WriteLine($"  max finite distance over main-region Fields (~world diameter from a goal): {maxD}");
        foreach (double rate in new[] { 1e-5, 1e-4, 1e-3, 1e-2 })
        {
            int tog = Toggles(rate);
            int* tb = Mem.Alloc<int>(tog); int prevN = 0;
            var rep = new List<double>(); long touched = 0, rebuilt = 0;
            for (int i = 0; i < 6; i++)
            {
                int nt = OscToggle(ref rng, tog, tb, ref prevN);
                R.Update(tb, nt, Pool, thr);
                long t0 = Stopwatch.GetTimestamp(); f.RepairAll(tb, nt, Pool, thr);
                if (i >= 1) { rep.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds); touched += f.LastRepairTouched; rebuilt += f.LastRebuilds; }
            }
            Mem.Free(tb, tog);
            Console.WriteLine($"  toggles {tog,7}: repair 64 Fields {Fmt3(rep)} (mean p50 p99 ms); tiles touched/Field/Tick {touched / 5.0 / GoalCount:N0}; fell back to full rebuild {rebuilt / 5.0:F1} of {GoalCount} Fields/Tick");
        }
        f.Dispose();

        Console.WriteLine("## A*: unreachable target without region IDs (search exhausts the start's region)");
        {
            var a = new AStar(W, (W.N + 4095) / 4096, int.MaxValue);
            var r3 = new Rng(11);
            var ts = new List<double>();
            for (int i = 0; i < 4; i++) { int s = PickMain(ref r3), g = Pocket[r3.Int(Pocket.Length)]; long t0 = Stopwatch.GetTimestamp(); int len = a.Find(s, g); ts.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds); if (len >= 0) Console.WriteLine("  (pocket was reachable!)"); }
            long e0 = a.Expanded;
            Console.WriteLine($"  {Stats.Fmt(ts)}; expanded {e0 / 4:N0} tiles per search");
            var tr = new List<double>(); long ex = a.Expanded; int lens = 0;
            for (int i = 0; i < 50; i++) { int s = PickMain(ref r3), g = PickMain(ref r3); long t0 = Stopwatch.GetTimestamp(); lens += a.Find(s, g); tr.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds); }
            Console.WriteLine($"  reachable random pairs: {Stats.Fmt(tr)}; mean length {lens / 50.0:F0}, expanded {(a.Expanded - ex) / 50:N0} per search");
            a.Dispose();
        }

        Console.WriteLine("## GPU flow fields (Vulkan)");
        var dev = Dev;
        {
            GpuFlow.GaussSeidel = false;
            var gj = new GpuFlow(dev, W, Goals, 1024, 1024);
            gj.BuildAll(); gj.BuildAll(); double gm = gj.ReadGpuMs();
            Console.WriteLine($"  [Jacobi in-block variant] build 64 Fields: wall {gj.LastWallMs:F1} ms, GPU {gm:F1} ms, {gj.LastIterations} iterations, {gj.LastBlockVisits:N0} block visits");
            gj.Dispose(); GpuFlow.GaussSeidel = true;
        }
        foreach (int count in new[] { 1, 32, 64 })
        {
            var goals = Goals.Take(count).ToArray();
            var g = new GpuFlow(dev, W, goals, 100_000, Math.Max(1, W.DynCount / 2));
            g.BuildAll(); // warm-up (pipeline compile happens in ctor)
            var wall = new List<double>(); var gpu = new List<double>(); int it = 0, sub = 0, disp = 0;
            for (int i = 0; i < 3; i++) { g.BuildAll(); wall.Add(g.LastWallMs); gpu.Add(g.ReadGpuMs()); it = g.LastIterations; sub = g.LastSubmits; disp = g.LastDispatches; }
            Console.WriteLine($"  build {count,2} Fields: wall {wall.Average(),8:F1} ms, GPU Kernel time {gpu.Average(),8:F1} ms, {it} block iterations, {sub} submits, {disp} dispatches, {g.LastBlockVisits:N0} block visits ({(double)g.LastBlockVisits / (count * (W.N / 256)):F1} per block), device memory {g.DeviceBytes / 1e6:F0} MB");
            if (count == 64)
            {
                foreach (int ips in new[] { 8, 32, 128 })
                {
                    g.IterPerSubmit = ips; g.BuildAll(); double gm = g.ReadGpuMs();
                    Console.WriteLine($"    IterPerSubmit {ips,3}: wall {g.LastWallMs:F1} ms, GPU {gm:F1} ms, submits {g.LastSubmits}, dispatches {g.LastDispatches}");
                }
                g.IterPerSubmit = 32;
                foreach (double rate in new[] { 1e-5, 1e-4, 1e-3, 1e-2 })
                {
                    int tog = Toggles(rate);
                    int* tb = Mem.Alloc<int>(tog); int prevN = 0;
                    var rw = new List<double>(); var rg = new List<double>(); var ri = new List<double>(); var rii = new List<double>(); var rs = new List<double>();
                    for (int i = 0; i < 6; i++)
                    {
                        int nt = OscToggle(ref rng, tog, tb, ref prevN);
                        g.ResetTimestamps();
                        g.Repair(tb, nt);
                        double gm = g.ReadGpuMs();
                        if (i >= 1) { rw.Add(g.LastWallMs); rg.Add(gm); ri.Add(g.LastIterations); rii.Add(g.LastInvalIterations); rs.Add(g.LastSubmits); }
                    }
                    Mem.Free(tb, tog);
                    Console.WriteLine($"  repair 64 Fields, toggles {tog,7}: wall {Fmt3(rw)} ms (mean p50 p99), GPU Kernel {rg.Average(),7:F2} ms, iterations inval {rii.Average():F1} + relax {ri.Average():F1}, submits {rs.Average():F1}{(tog > g.RebuildAbove ? $" [above RebuildAbove={g.RebuildAbove}: full rebuild]" : "")}");
                }
                // regions are now stale vs the world (toggles applied without R.Update): rebuild
                R.BuildAll(Pool, thr);
                // transfer costs
                var u = Spawn(100_000, 3);
                g.BindUnits(100_000);
                var up = Stats.Time(3, 20, () => { g.UploadFields(u.Field, 100_000); dev.Submit(dev.BeginBatch()).Wait(); });
                Console.WriteLine($"  upload 100k unit fields (400 KB) + empty submit/wait: {Stats.Fmt(up)}");
                var empty = Stats.Time(3, 20, () => dev.Submit(dev.BeginBatch()).Wait());
                Console.WriteLine($"  empty submit + fence wait: {Stats.Fmt(empty)}");
                for (int i = 0; i < 100_000; i++) u.Field[i] = i % MainGoals;
                g.UploadUnits(u.Pos, u.Field, 100_000);
                var mv = Stats.Time(3, 20, () => { g.ResetTimestamps(); g.SubmitMove(true).Wait(); var p = g.MapUnits(); g.UnmapUnits(); });
                g.ResetTimestamps(); g.SubmitMove(true).Wait(); double mk = g.ReadGpuMs(); var pp = g.MapUnits(); g.UnmapUnits();
                Console.WriteLine($"  move Kernel 100k units + 400 KB readback + map, sync: {Stats.Fmt(mv)}; GPU Kernel {mk:F3} ms");
                u.Dispose();
                // one-hop Jacobi estimate
                double bytes = 64.0 * W.N * 2;
                Console.WriteLine($"  plain one-hop relaxation (no block-local iteration) needs exactly maxDistance sweeps over all tiles of every Field: {maxD} sweeps x {bytes / 1e9:F2} GB read+write -> at 448 GB/s peak >= {maxD * 2 * bytes / 448e9 * 1000:F0} ms per full build; block-local worklist needed {it} iterations");
            }
            g.Dispose();
        }
        // (6) GPU per-unit queries: a batch of 32 single-goal BFS Fields is the GPU's natural per-query unit.
        Console.WriteLine("## (6) per-unit GPU queries = one Field per unique goal (batched 32): see 'build 32 Fields' above; extrapolate to requests/Tick");
    }

    // ---------------------------------------------------------------------------------------------- verify
    static void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) Failures++; }
    static int Failures;

    static int[] GlobalComponents()
    {
        var comp = new int[W.N]; Array.Fill(comp, -1);
        var q = new int[W.N];
        int c = 0;
        for (int s = 0; s < W.N; s++)
        {
            if (!W.P(s) || comp[s] >= 0) continue;
            int h = 0, t = 0; q[t++] = s; comp[s] = c;
            while (h < t) { int u = q[h++]; for (int k = 0; k < 6; k++) { int j = W.Nb(u, k); if (j >= 0 && comp[j] < 0) { comp[j] = c; q[t++] = j; } } }
            c++;
        }
        return comp;
    }

    static bool RegionsMatch()
    {
        var comp = GlobalComponents();
        var r2c = new Dictionary<int, int>(); var c2r = new Dictionary<int, int>();
        for (int i = 0; i < W.N; i++)
        {
            int r = R.RegionOf(i), c = comp[i];
            if ((r < 0) != (c < 0)) return false;
            if (r < 0) continue;
            if (r2c.TryGetValue(r, out int cc) && cc != c) return false;
            if (c2r.TryGetValue(c, out int rr) && rr != r) return false;
            r2c[r] = c; c2r[c] = r;
        }
        return true;
    }

    static void Verify()
    {
        int thr = Pool.Threads;
        var rng = new Rng(31);
        Console.WriteLine("## Regions vs global BFS components");
        Check(RegionsMatch(), "region IDs == global BFS connected components (after full build)");
        int* tb = Mem.Alloc<int>(W.N / 50);

        Console.WriteLine("## CPU flow repair vs fresh BFS; GPU vs CPU bit-exactness");
        int[] goals = Goals.Take(8).Concat(Goals.Skip(MainGoals).Take(2)).ToArray(); // 8 main + 2 pocket goals
        var f = new CpuFlowFields(W, goals, Pool.Threads); f.BuildAll(Pool, thr);
        var fresh = new CpuFlowFields(W, goals, Pool.Threads);
        var g = new GpuFlow(Dev, W, goals, 20_000, W.N / 50);
        g.BuildAll();
        var tmp = Mem.Alloc<ushort>(W.N, false);
        bool Same(ushort* a, ushort* b) => new ReadOnlySpan<ushort>(a, W.N).SequenceEqual(new ReadOnlySpan<ushort>(b, W.N));
        bool GpuEq() { for (int i = 0; i < goals.Length; i++) { g.ReadField(i, tmp); if (!Same(tmp, f.F[i])) { Console.WriteLine($"    field {i} differs ({Diff(tmp, f.F[i])})"); return false; } } return true; }
        Check(GpuEq(), $"GPU build == CPU BFS for {goals.Length} Fields ({g.LastIterations} iterations)");

        Console.WriteLine("## A* lengths == BFS distances; HPA* validity and path-length ratio; GPU move == CPU move");
        {
            var a = new AStar(W, AStarPages, AStarMaxExp);
            int bad = 0, n = 0, unreachOk = 0;
            for (int i = 0; i < 400; i++)
            {
                int fi = i % 8, s = PickMain(ref rng);
                int d = f.F[fi][s];
                int len = a.Find(s, goals[fi]);
                if (d == CpuFlowFields.INF) { if (len < 0) unreachOk++; else bad++; continue; }
                n++; if (len != d) bad++;
            }
            Check(bad == 0, $"A* length == BFS distance for {n} reachable pairs ({unreachOk} unreachable agreed)");
            a.Dispose();
        }
        // services: walk units to the goals with no toggles; count steps
        {
            int n = 3000;
            var uA = Spawn(n, 5); var uH = Spawn(n, 5); var uF = Spawn(n, 5); var uG = Spawn(n, 5);
            var reqs = Mem.Alloc<Request>(n);
            for (int i = 0; i < n; i++) reqs[i] = new Request { Unit = i, Goal = goals[i % 8], Field = i % 8 };
            var sA = new AStarService(W, R, uA, Pool, thr, AStarPages, AStarMaxExp);
            var sH = new HpaService(W, R, uH, Pool, thr, HpaPages);
            var sF = new FlowService(W, R, uF, Pool, thr, f);
            var sG = new GpuFlowService(W, R, uG, Pool, thr, g, false);
            var start = new int[n]; for (int i = 0; i < n; i++) start[i] = uA.Pos[i];
            Service[] all = [sA, sH, sF, sG]; Units[] us = [uA, uH, uF, uG];
            foreach (var s in all) s.Process(reqs, n);
            var steps = new int[4, n]; var arrived = new int[4, n];
            bool gpuSame = true; int maxTicks = 0;
            for (int t = 0; t < 4000; t++)
            {
                bool any = false;
                for (int k = 0; k < 4; k++)
                {
                    var u = us[k];
                    // HPA continuation: NeedRefine units are handled inside Process
                    if (k == 1) sH.Process(reqs, 0);
                    all[k].Move();
                    for (int i = 0; i < n; i++)
                    {
                        if (arrived[k, i] != 0) continue;
                        if (u.Status[i] == St.Arrived) { arrived[k, i] = 1; steps[k, i] = t + 1; }
                        else if (u.Status[i] == St.Moving || u.Status[i] == St.NeedRefine) any = true;
                    }
                }
                for (int i = 0; i < n && gpuSame; i++) if (uF.Pos[i] != uG.Pos[i] || uF.Status[i] != uG.Status[i]) { gpuSame = false; Console.WriteLine($"    unit {i} differs at Tick {t}: cpu {uF.Pos[i]}/{uF.Status[i]} gpu {uG.Pos[i]}/{uG.Status[i]}"); }
                maxTicks = t;
                if (!any) break;
            }
            Check(gpuSame, $"GPU move Kernel positions/status == CPU flow move for {n} units over {maxTicks + 1} Ticks");
            int okA = 0, okF = 0, reach = 0, hpaArr = 0; double ratio = 0, worst = 1;
            for (int i = 0; i < n; i++)
            {
                int d = f.F[i % 8][start[i]];
                if (d == CpuFlowFields.INF) continue;
                reach++;
                // a unit that arrives at Tick t made t-1 moves (arrival is detected on the Tick after the last step for path services)
                int stepsA = d == 0 ? 0 : steps[0, i];
                if (arrived[0, i] != 0) okA++;
                if (arrived[2, i] != 0) okF++;
                if (arrived[1, i] != 0 && d > 0) { hpaArr++; double rr = (double)(steps[1, i]) / d; ratio += rr; worst = Math.Max(worst, rr); }
            }
            Check(okA == reach && okF == reach, $"A* and flow units all arrive ({okA}, {okF} of {reach} reachable)");
            Check(hpaArr >= reach - reach / 100, $"HPA* units arrive: {hpaArr} of {reach}; Ticks-to-arrive / BFS distance mean {ratio / Math.Max(1, hpaArr):F3}, worst {worst:F2}");
            // exact step counts for A*: compare Ticks to arrive vs flow (flow arrives on the Tick it reaches the goal)
            int eq = 0;
            for (int i = 0; i < n; i++) if (arrived[0, i] != 0 && arrived[2, i] != 0 && Math.Abs(steps[0, i] - steps[2, i]) <= 1) eq++;
            Check(eq == okA, $"A* walk length == flow-field walk length (+-1 Tick of arrival detection) for {eq}/{okA} units");
            foreach (var s in all) s.Dispose();
            foreach (var u in us) u.Dispose();
            Mem.Free(reqs, n);
        }
        Console.WriteLine("## Under dynamic toggles: CPU repair vs fresh BFS, GPU repair vs CPU, regions vs BFS, GPU vs CPU units");
        foreach (double rate in new[] { 1e-5, 1e-4, 1e-3, 1e-2 })
        {
            bool okc = true, okg = true;
            for (int t = 0; t < 5; t++)
            {
                int nt = W.Toggle(ref rng, Toggles(rate), tb);
                R.Update(tb, nt, Pool, thr);
                f.RepairAll(tb, nt, Pool, thr);
                g.Repair(tb, nt);
            }
            fresh.BuildAll(Pool, thr);
            for (int i = 0; i < goals.Length; i++) okc &= Same(f.F[i], fresh.F[i]);
            okg = GpuEq();
            Check(okc, $"CPU incremental repair == fresh BFS after 5 Ticks at {Toggles(rate)} toggles/Tick");
            Check(okg, $"GPU repair == CPU after 5 Ticks at {Toggles(rate)} toggles/Tick (last: inval {g.LastInvalIterations} + relax {g.LastIterations} iterations)");
            Check(RegionsMatch(), $"region IDs == global BFS components after incremental updates at {Toggles(rate)} toggles/Tick");
        }
        fresh.Dispose();
        {
            // full service loop with toggles + churn: CPU flow vs GPU flow (sync and deferred-by-one compared separately)
            int n = 20_000, tog = Toggles(1e-4);
            var uF = Spawn(n, 8); var uG = Spawn(n, 8);
            var wl = new Workload("Vs", 0, 0.03, 0.0);
            var hF = new Harness { U = uF, Wl = wl, Rng = new Rng(1), Req = Mem.Alloc<Request>(n), Tog = tb, TogCount = 0, Threads = thr, G = goals, MainCount = 8 };
            var hG = new Harness { U = uG, Wl = wl, Rng = new Rng(1), Req = Mem.Alloc<Request>(n), Tog = tb, TogCount = 0, Threads = thr, G = goals, MainCount = 8 };
            // restrict shared goals to the verify goal set (fields 0..7 of `goals` correspond to Goals[0..7])
            var sF = new FlowService(W, R, uF, Pool, thr, f);
            var sG = new GpuFlowService(W, R, uG, Pool, thr, g, false);
            bool same = true; int ticks = 60;
            for (int t = 0; t < ticks && same; t++)
            {
                int nt = W.Toggle(ref rng, tog, tb);
                R.Update(tb, nt, Pool, thr);
                sF.OnToggles(tb, nt); sG.OnToggles(tb, nt);
                hF.Tick(sF, null); hG.Tick(sG, null);
                for (int i = 0; i < n; i++)
                    if (uF.Pos[i] != uG.Pos[i] || uF.Status[i] != uG.Status[i]) { same = false; Console.WriteLine($"    unit {i} differs at Tick {t}: cpu {uF.Pos[i]}/{uF.Status[i]} gpu {uG.Pos[i]}/{uG.Status[i]}"); break; }
            }
            Check(same, $"{n} units, {ticks} Ticks, {tog} toggles/Tick, 3% churn: GPU-repaired Fields + GPU move == CPU (positions and status)");
            sF.Dispose(); sG.Dispose(); uF.Dispose(); uG.Dispose(); Mem.Free(hF.Req, n); Mem.Free(hG.Req, n);
        }
        Mem.Free(tmp, W.N);
        f.Dispose(); g.Dispose(); Mem.Free(tb, W.N / 50);
        Console.WriteLine(Failures == 0 ? "# VERIFY: all checks passed" : $"# VERIFY: {Failures} check(s) FAILED");
    }

    static string Diff(ushort* a, ushort* b)
    {
        int cnt = 0, first = -1;
        for (int i = 0; i < W.N; i++) if (a[i] != b[i]) { if (first < 0) first = i; cnt++; }
        return first < 0 ? "none" : $"{cnt} tiles, first ({W.X(first)},{W.Y(first)},{W.Z(first)}) gpu {a[first]} cpu {b[first]}";
    }
}
