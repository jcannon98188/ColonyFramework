using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Colony.Bench;
using Colony.Ecs;

// Register Components in a fixed order (Component IDs feed archetype layout and hashing).
_ = ComponentId<Position>.Value; _ = ComponentId<Target>.Value; _ = ComponentId<Hunger>.Value; _ = ComponentId<Health>.Value;
_ = ComponentId<Food>.Value; _ = ComponentId<Lifetime>.Value; _ = ComponentId<Marked>.Value;

var opt = Options.Parse(args);
Console.WriteLine("# Colony ECS prototype benchmark");
Console.WriteLine($"- Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"- Logical processors: {Environment.ProcessorCount}");
Console.WriteLine($"- Vector256 accelerated: {Vector256.IsHardwareAccelerated}, AVX2: {Avx2.IsSupported}, AVX-512F: {Avx512F.IsSupported}");
Console.WriteLine($"- GC: server={GCSettings.IsServerGC}, latency={GCSettings.LatencyMode}, dynamic code (JIT)={RuntimeFeature.IsDynamicCodeSupported}");
Console.WriteLine($"- Scenario: colonists={opt.Colonists}, food={opt.Food}, debris={opt.Debris}, warmup={opt.Warmup}, ticks={opt.Ticks}, seed={opt.Seed}");
Console.WriteLine();

if (opt.Mode is "trace") Runner.Trace(opt);
if (opt.Mode is "all" or "determinism") Runner.Determinism(opt);
if (opt.Mode is "all" or "bench") Runner.Bench(opt);

Process.GetCurrentProcess().Refresh();
Console.WriteLine($"Process peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024):F1} MiB");

sealed class Options
{
    public int Repeat = 1;
    public int Colonists = 100_000, Food = 5_000, Debris = 0, Warmup = 500, Ticks = 1000;
    public ulong Seed = 12345;
    public string Mode = "all";
    public int[] Threads = DefaultThreads();

    static int[] DefaultThreads()
    {
        var l = new List<int>();
        for (int t = 1; t < Environment.ProcessorCount; t *= 2) l.Add(t);
        l.Add(Environment.ProcessorCount);
        return l.ToArray();
    }

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string v = i + 1 < a.Length ? a[i + 1] : "";
            switch (a[i])
            {
                case "--colonists": o.Colonists = int.Parse(v); i++; break;
                case "--food": o.Food = int.Parse(v); i++; break;
                case "--debris": o.Debris = int.Parse(v); i++; break;
                case "--warmup": o.Warmup = int.Parse(v); i++; break;
                case "--ticks": o.Ticks = int.Parse(v); i++; break;
                case "--seed": o.Seed = ulong.Parse(v); i++; break;
                case "--mode": o.Mode = v; i++; break;
                case "--repeat": o.Repeat = int.Parse(v); i++; break;
                case "--sabotage": Sim.Sabotage = true; break;
                case "--threads": o.Threads = v.Split(',').Select(int.Parse).ToArray(); i++; break;
                default: throw new ArgumentException("unknown arg " + a[i]);
            }
        }
        return o;
    }
}

static class Runner
{
    public static World Setup(Options o, int threads)
    {
        var w = new World(o.Seed, threads);
        w.SetResource(new FoodGrid());
        w.SetResource(new ColonyStats());
        w.SetResource(new EatenFood());
        w.AddSystem(new FoodIndexSystem());
        w.AddSystem(new HungerSystem());
        w.AddSystem(new MoveSystem());
        w.AddSystem(new EatSystem());
        w.AddSystem(new WanderSystem());
        w.AddSystem(new StarvationSystem());
        w.AddSystem(new StatsSystem());
        w.AddSystem(new FoodSpawnerSystem(o.Food));
        if (o.Debris > 0) w.AddSystem(new ChurnSystem());
        w.Scheduler.Build();

        for (uint i = 0; i < o.Colonists; i++)
        {
            var r = Philox.For(o.Seed, 1000, i, 0);
            var r2 = Philox.For(o.Seed, 1001, i, 0);
            w.Create(Sim.RandomTile(r), Unsafe.BitCast<Position, Target>(Sim.RandomTile(r2)),
                new Hunger { Value = (r.C % 600) / 10f }, new Health { Value = 100 });
        }
        for (uint i = 0; i < o.Food; i++)
            w.Create(Sim.RandomTile(Philox.For(o.Seed, 1002, i, 0)), new Food { Nutrition = 50 });
        for (uint i = 0; i < o.Debris; i++)
        {
            var r = Philox.For(o.Seed, 1003, i, 0);
            w.Create(Sim.RandomTile(r), new Lifetime { Ticks = 1 + (int)(r.C % 10) });
        }
        return w;
    }

    /// <summary>Prints simulation behaviour over time (to confirm the scenario reaches a steady state).</summary>
    public static void Trace(Options o)
    {
        using var w = Setup(o, Environment.ProcessorCount);
        var a = w.Archetypes[0];
        for (int tick = 1; tick <= o.Warmup + o.Ticks; tick++)
        {
            long s = Stopwatch.GetTimestamp();
            w.Tick();
            double ms = (Stopwatch.GetTimestamp() - s) * 1000.0 / Stopwatch.Frequency;
            if (tick % 100 != 0) continue;
            int hungry = 0, atTarget = 0; long distSum = 0;
            var grid = w.GetResource<FoodGrid>();
            for (int c = 0; c < a.ChunkCount; c++)
            {
                var ch = a.GetChunk(c);
                var pos = ch.Column<Position>(); var tgt = ch.Column<Target>(); var hun = ch.Column<Hunger>();
                for (int i = 0; i < ch.Count; i++)
                {
                    if (pos[i].X == tgt[i].X && pos[i].Y == tgt[i].Y) atTarget++;
                    if (hun[i].Value < Sim.HungryThreshold) continue;
                    hungry++;
                    if (grid.FindNearest(pos[i].X, pos[i].Y, out _, out var fp)) distSum += Math.Max(Math.Abs(fp.X - pos[i].X), Math.Abs(fp.Y - pos[i].Y)) / Sim.TileUnits;
                }
            }
            var st = w.GetResource<ColonyStats>();
            Console.WriteLine($"tick {tick}: {ms:F2} ms, avgHunger {st.AverageHunger:F1}, hungry {hungry}, atTarget {atTarget}, meanDistToFood {(hungry == 0 ? 0 : distSum / (double)hungry):F1} tiles, eaten {st.EatenThisTick}, destroys total {w.Structural.Destroys}");
        }
    }

    public static void Determinism(Options o)
    {
        int[] checkpoints = { 1, 10, 50, 100, 250, 500 };
        var threadSets = new[] { 1, 2, 4, Environment.ProcessorCount }.Distinct().ToArray();
        foreach (var debris in new[] { 0, Math.Max(o.Debris, 100_000) })
        {
            var oo = new Options { Colonists = o.Colonists, Food = o.Food, Debris = debris, Seed = o.Seed };
            Console.WriteLine($"## Determinism: colonists={oo.Colonists}, food={oo.Food}, debris={debris}");
            var results = new Dictionary<int, ulong[]>();
            foreach (var t in threadSets)
            {
                using var w = Setup(oo, t);
                var hashes = new ulong[checkpoints.Length];
                int k = 0;
                for (int tick = 1; tick <= checkpoints[^1]; tick++)
                {
                    w.Tick();
                    if (tick == checkpoints[k]) hashes[k++] = w.ComputeHash();
                }
                results[t] = hashes;
            }
            Console.WriteLine("| Tick | " + string.Join(" | ", threadSets.Select(t => $"{t} thr")) + " | equal |");
            Console.WriteLine("|---|" + string.Concat(threadSets.Select(_ => "---|")) + "---|");
            bool allOk = true;
            for (int k = 0; k < checkpoints.Length; k++)
            {
                bool eq = threadSets.All(t => results[t][k] == results[threadSets[0]][k]);
                allOk &= eq;
                Console.WriteLine($"| {checkpoints[k]} | " + string.Join(" | ", threadSets.Select(t => results[t][k].ToString("x16"))) + $" | {(eq ? "yes" : "NO")} |");
            }
            bool changes = results[1].Distinct().Count() == checkpoints.Length;
            Console.WriteLine($"\nDeterminism {(allOk ? "PASS" : "FAIL")} (hash changes between checkpoints: {changes})\n");
            if (!allOk) Environment.ExitCode = 1;
        }
    }

    public static void Bench(Options o)
    {
        Console.WriteLine($"## Bench: colonists={o.Colonists}, food={o.Food}, debris={o.Debris}");
        {
            // Control: process-wide allocation while the main thread does nothing for 1 s (runtime background noise).
            long a0 = GC.GetTotalAllocatedBytes(precise: true);
            Thread.Sleep(1000);
            long a1 = GC.GetTotalAllocatedBytes(precise: true);
            long a2 = GC.GetTotalAllocatedBytes(precise: true);
            Console.WriteLine($"Idle control: {a1 - a0} B allocated process-wide during 1 s sleep; back-to-back query delta {a2 - a1} B\n");
        }
        var rows = new List<string>();
        var sysRows = new List<string>();
        string[]? sysNames = null;
        var summary = new List<(int t, double mean, double p50, double p99, double max)>();
        // Repeats are interleaved across thread counts so background noise on a desktop machine spreads evenly.
        var order = Enumerable.Range(0, o.Repeat).SelectMany(rep => o.Threads.Select(t => (t, rep))).ToArray();
        foreach (var (t, rep) in order)
        {
            using var w = Setup(o, t);
            if (t == o.Threads[0] && rep == 0) Console.Write("Waves:\n" + w.Scheduler.DescribeWaves() + "\n");
            for (int i = 0; i < o.Warmup; i++) w.Tick();
            w.Scheduler.ResetStats();
            w.Structural = default;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var times = new double[o.Ticks];
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long allocAll0 = GC.GetTotalAllocatedBytes(precise: true);
            long mainOnly0 = GC.GetAllocatedBytesForCurrentThread(); long workers0 = w.Scheduler.WorkerAllocatedBytes();
            long eaten0 = w.GetResource<ColonyStats>().TotalEaten;
            for (int i = 0; i < o.Ticks; i++)
            {
                long s = Stopwatch.GetTimestamp();
                w.Tick();
                times[i] = (Stopwatch.GetTimestamp() - s) * 1000.0 / Stopwatch.Frequency;
            }
            long allocAll = GC.GetTotalAllocatedBytes(precise: true) - allocAll0;
            long mainOnly = GC.GetAllocatedBytesForCurrentThread() - mainOnly0; long workersOnly = w.Scheduler.WorkerAllocatedBytes() - workers0; long allocMain = mainOnly + workersOnly;
            int gcs = GC.CollectionCount(0) - g0 + GC.CollectionCount(1) - g1 + GC.CollectionCount(2) - g2;

            var sorted = (double[])times.Clone();
            Array.Sort(sorted);
            double mean = times.Average();
            double p50 = sorted[sorted.Length / 2], p99 = sorted[(int)(sorted.Length * 0.99)], max = sorted[^1];
            var stats = w.GetResource<ColonyStats>();
            double eatenPerTick = (stats.TotalEaten - eaten0) / (double)o.Ticks;
            var st = w.Structural;
            double f = 1000.0 / Stopwatch.Frequency / o.Ticks;
            var sch = w.Scheduler;
            string phases = string.Join(" / ", Enumerable.Range(0, 3).Select(p => $"{sch.WaveWallTimestampTicks[p] * f:F3}+{sch.PlaybackTimestampTicks[p] * f:F3}"));
            Process.GetCurrentProcess().Refresh();
            summary.Add((t, mean, p50, p99, max));
            rows.Add($"| {t} | {mean:F3} | {p50:F3} | {p99:F3} | {max:F3} | run {rep} | {allocAll / (double)o.Ticks:F1} | {allocMain / (double)o.Ticks:F1} | {gcs} | {phases} | {st.Total / (double)o.Ticks:F0} | {w.Allocator.PeakChunksInUse} / {w.Allocator.ReservedBytes / (1024 * 1024)} MiB | {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MiB |");

            sysNames ??= sch.Systems.Select(s => s.Name).ToArray();
            sysRows.Add($"| {t} (run {rep}) | " + string.Join(" | ", sch.Systems.Select(s => $"{s.CpuMilliseconds() / o.Ticks:F3} ({s.TotalJobs / o.Ticks})")) + " |");

            Console.WriteLine($"[threads={t} run={rep}] mean {mean:F3} ms; alloc total bytes: process {allocAll}, main {mainOnly}, workers {workersOnly}; avg hunger {stats.AverageHunger:F1}; colonists {stats.Colonists}; below-full-health {stats.Starving}; eaten/tick {eatenPerTick:F1}; food {w.CountMatching(Mask.Of<Food>())}; alive {w.AliveCount}; archetypes {w.Archetypes.Count}; structural ops/tick C{st.Creates / (double)o.Ticks:F0} D{st.Destroys / (double)o.Ticks:F0} A{st.Adds / (double)o.Ticks:F0} R{st.Removes / (double)o.Ticks:F0} stale{st.StaleSkipped / (double)o.Ticks:F1}");
        }
        Console.WriteLine();
        Console.WriteLine("| Threads | Mean ms | p50 ms | p99 ms | Max ms | Run | Alloc B/Tick (process-wide) | Alloc B/Tick (Tick threads: main+workers) | GCs | Phase wall ms: waves+playback (Pre / Update / Late) | Structural ops/Tick | Peak chunks / slabs reserved | Working set |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows) Console.WriteLine(r);
        Console.WriteLine();
        static double Median(IEnumerable<double> xs) { var a = xs.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
        double base50 = Median(summary.Where(s => s.t == o.Threads[0]).Select(s => s.p50));
        double baseMeanMed = Median(summary.Where(s => s.t == o.Threads[0]).Select(s => s.mean));
        Console.WriteLine($"Summary: median over {o.Repeat} run(s) per thread count\n");
        Console.WriteLine("| Threads | Mean ms | p50 ms | p99 ms | Max ms (worst run) | Speedup (mean) | Speedup (p50) | Fits 16 ms (p99)? |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|");
        foreach (var t in o.Threads)
        {
            var s = summary.Where(x => x.t == t).ToArray();
            double m = Median(s.Select(x => x.mean)), p = Median(s.Select(x => x.p50)), q = Median(s.Select(x => x.p99)), mx = s.Max(x => x.max);
            Console.WriteLine($"| {t} | {m:F3} | {p:F3} | {q:F3} | {mx:F3} | {baseMeanMed / m:F2}x | {base50 / p:F2}x | {(q < 16 ? "yes" : "NO")} |");
        }
        Console.WriteLine();
        Console.WriteLine("Per-System CPU ms per Tick, summed over threads (jobs per Tick):\n");
        Console.WriteLine("| Threads | " + string.Join(" | ", sysNames!) + " |");
        Console.WriteLine("|---|" + string.Concat(sysNames!.Select(_ => "---|")));
        foreach (var r in sysRows) Console.WriteLine(r);
        Console.WriteLine();
    }
}
