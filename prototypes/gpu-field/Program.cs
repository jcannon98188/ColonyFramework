using System.Diagnostics;
using System.Runtime.Intrinsics;
using GpuField.Backend;
using GpuField.Bench;
using GpuField.Fields;
using GpuField.Kernels;
using GpuField.Native;

namespace GpuField;

public static unsafe class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "probe";
        uint backend = args.Length > 1 && args[1].Equals("dx12", StringComparison.OrdinalIgnoreCase) ? Wgpu.BackendType_D3D12 : Wgpu.BackendType_Vulkan;
        Console.WriteLine($"# mode={mode} backend={(backend == Wgpu.BackendType_D3D12 ? "dx12" : "vulkan")} runtime={Environment.Version} aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} " +
                          $"V256={Vector256.IsHardwareAccelerated} V512={Vector512.IsHardwareAccelerated} cpus={Environment.ProcessorCount}");
        try
        {
            switch (mode)
            {
                case "probe": Probe(); break;
                case "verify": Verify(backend); break;
                case "cpu": CpuBench(); break;
                case "gpu": GpuBench(backend); break;
                case "e2e": EndToEnd(backend); break;
                case "pinvoke": PInvokeCost(backend); break;
                case "xfer": TransferDetail(backend); break;
                case "aot-smoke": Probe(); Verify(backend, quick: true); break;
                default: Console.Error.WriteLine($"unknown mode {mode}"); return 2;
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        Console.WriteLine($"# uncaptured wgpu errors: {WgpuDevice.UncapturedErrors}");
        return WgpuDevice.UncapturedErrors == 0 ? 0 : 3;
    }

    // ---------------------------------------------------------------------------------------------
    // World construction
    static uint Mix(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }

    /// <summary>World of size sx*sy*sz; `activeFraction` of chunks allocated (and stepped), the rest uniform.</summary>
    static ChunkedField<T> MakeWorld<T>(int sx, int sy, int sz, double activeFraction, Func<uint, T> value, T uniform) where T : unmanaged
    {
        var f = new ChunkedField<T>(sx, sy, sz, c => activeFraction >= 1 || Mix((uint)c * 2654435761u + 17) % 10000 < activeFraction * 10000, uniform);
        // Fill per slot in parallel: value depends only on (slot, cell) so it is deterministic.
        Parallel.For(0, f.SlotCount, s =>
        {
            T* p = f.ChunkPtr(f.Cur, s);
            for (int i = 0; i < ChunkedField<T>.CellsPerChunk; i++) p[i] = value(Mix((uint)(s * 4096 + i) ^ 0xA5A5A5A5u));
        });
        return f;
    }

    static ChunkedField<int> MakeExact(int sx, int sy, int sz, double active) => MakeWorld(sx, sy, sz, active, v => (int)(v & 0xFFFF), 1000);
    static ChunkedField<float> MakeApprox(int sx, int sy, int sz, double active) => MakeWorld(sx, sy, sz, active, v => (v & 0xFFFF) / 65536f, 1000 / 65536f);

    static readonly (string name, int x, int y, int z, double active)[] Worlds =
    [
        ("full 512x512x256 dense", 512, 512, 256, 1.0),
        ("small 256x256x64 dense", 256, 256, 64, 1.0),
        ("full 512x512x256 sparse 5% chunks", 512, 512, 256, 0.05),
    ];

    // ---------------------------------------------------------------------------------------------
    static void Probe()
    {
        Console.WriteLine($"wgpu-native version 0x{Wgpu.wgpuGetVersion():X8}");
        foreach (var bt in new[] { Wgpu.BackendType_Vulkan, Wgpu.BackendType_D3D12 })
        {
            try
            {
                using var d = new WgpuDevice(bt);
                var c = d.Capabilities;
                var a = d.AdapterLimits;
                Console.WriteLine($"{c.BackendName}: {c.AdapterName}");
                Console.WriteLine($"  adapter maxBufferSize={a.maxBufferSize} maxStorageBufferBindingSize={a.maxStorageBufferBindingSize} maxStorageBuffersPerStage={a.maxStorageBuffersPerShaderStage} maxWorkgroupsPerDim={a.maxComputeWorkgroupsPerDimension} maxInvocations={a.maxComputeInvocationsPerWorkgroup} minStorageOffsetAlign={a.minStorageBufferOffsetAlignment}");
                Console.WriteLine($"  timestamps={c.TimestampQuery} period={d.TimestampPeriodNs}ns");
            }
            catch (Exception e) { Console.WriteLine($"backend {bt}: {e.Message}"); }
            // What happens with WebGPU default limits (no requiredLimits)?
            try
            {
                using var d = new WgpuDevice(bt, defaultLimits: true);
                var l = d.DeviceLimits;
                Console.WriteLine($"  default-limits device: maxBufferSize={l.maxBufferSize} maxStorageBufferBindingSize={l.maxStorageBufferBindingSize} maxStorageBuffersPerStage={l.maxStorageBuffersPerShaderStage}");
                using var f = MakeExact(512, 512, 256, 1.0);
                try { using var g = new GpuDiffusion<int, ExactRule>(d, f); Console.WriteLine("  full-world mirror with default limits: OK (unexpected)"); }
                catch (Exception e) { Console.WriteLine($"  full-world mirror with default limits: FAILED as expected: {e.Message}"); }
            }
            catch (Exception e) { Console.WriteLine($"  default-limits device: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------------------------------------
    static void Verify(uint backend, bool quick = false)
    {
        Console.WriteLine("## Verify: CPU widths vs reference (Exact, small world)");
        foreach (double act in new[] { 1.0, 0.05 })
        {
            var hashes = new List<(string, ulong)>();
            foreach (var w in new[] { "reference", "scalar", "V128", "V256", "V256-MT" })
            {
                using var f = MakeExact(256, 256, 64, act);
                for (int i = 0; i < 3; i++)
                {
                    if (w == "reference") CpuDiffusion<int, ExactRule>.StepReference(f);
                    else CpuDiffusion<int, ExactRule>.Step(f, w switch { "scalar" => SimdWidth.Scalar, "V128" => SimdWidth.V128, _ => SimdWidth.V256 }, w.EndsWith("MT"));
                }
                hashes.Add((w, f.Hash()));
            }
            bool ok = hashes.All(h => h.Item2 == hashes[0].Item2);
            Console.WriteLine($"  active={act:P0}: {string.Join(", ", hashes.Select(h => $"{h.Item1}={h.Item2:X16}"))} -> {(ok ? "IDENTICAL" : "MISMATCH")}");
        }
        {
            using var a = MakeApprox(256, 256, 64, 1.0);
            using var b = MakeApprox(256, 256, 64, 1.0);
            for (int i = 0; i < 3; i++) { CpuDiffusion<float, ApproxRule>.StepReference(a); CpuDiffusion<float, ApproxRule>.Step(b, SimdWidth.V256, true); }
            Console.WriteLine($"  Approx reference vs V256-MT after 3 steps: {(a.Hash() == b.Hash() ? "IDENTICAL" : "MISMATCH")} (CPU float order is fixed, no FMA)");
        }

        using var dev = new WgpuDevice(backend);
        Console.WriteLine($"## Verify: GPU ({dev.Capabilities.BackendName}) vs CPU V256-MT, Exact kernel, hash compare");
        var runs = quick
            ? new[] { (256, 256, 64, 1.0, new[] { 1, 10, 50 }) }
            : new[] { (256, 256, 64, 1.0, new[] { 1, 10, 50, 100, 200, 500 }), (512, 512, 256, 1.0, new[] { 1, 5, 20 }), (512, 512, 256, 0.05, new[] { 1, 10, 100, 200 }) };
        foreach (var (sx, sy, sz, act, checkpoints) in runs)
        {
            using var cpu = MakeExact(sx, sy, sz, act);
            using var gpuSide = MakeExact(sx, sy, sz, act); // receives GPU readbacks
            using var g = new GpuDiffusion<int, ExactRule>(dev, gpuSide);
            g.UploadAll();
            int done = 0;
            var sb = new List<string>();
            bool all = true;
            foreach (int cp in checkpoints)
            {
                for (; done < cp; done++) CpuDiffusion<int, ExactRule>.Step(cpu, SimdWidth.V256, true);
                var b = dev.BeginBatch();
                g.RecordSteps(b, cp - (checkpoints.TakeWhile(c => c < cp).LastOrDefault()));
                dev.Submit(b).Wait();
                g.ReadbackAll();
                ulong hc = cpu.Hash(), hg = gpuSide.Hash();
                all &= hc == hg;
                sb.Add($"step {cp}: {(hc == hg ? "==" : "!=")} {hc:X16}");
            }
            Console.WriteLine($"  {sx}x{sy}x{sz} active={act:P0} slots={cpu.SlotCount}: {string.Join("; ", sb)} -> {(all ? "BIT-IDENTICAL" : "MISMATCH")}");
        }

        Console.WriteLine("## Verify: GPU vs CPU, Approx (float) kernel, 256x256x64 dense, 100 steps");
        {
            using var cpu = MakeApprox(256, 256, 64, 1.0);
            using var gpuSide = MakeApprox(256, 256, 64, 1.0);
            using var g = new GpuDiffusion<float, ApproxRule>(dev, gpuSide);
            g.UploadAll();
            int steps = quick ? 10 : 100;
            for (int i = 0; i < steps; i++) CpuDiffusion<float, ApproxRule>.Step(cpu, SimdWidth.V256, true);
            var b = dev.BeginBatch(); g.RecordSteps(b, steps); dev.Submit(b).Wait();
            g.ReadbackAll();
            long n = cpu.PoolBytes / 4, diff = 0; double maxAbs = 0; long maxUlp = 0;
            for (long i = 0; i < n; i++)
            {
                float x = cpu.Cur[i], y = gpuSide.Cur[i];
                if (BitConverter.SingleToInt32Bits(x) != BitConverter.SingleToInt32Bits(y))
                {
                    diff++;
                    maxAbs = Math.Max(maxAbs, Math.Abs(x - y));
                    maxUlp = Math.Max(maxUlp, Math.Abs((long)BitConverter.SingleToInt32Bits(x) - BitConverter.SingleToInt32Bits(y)));
                }
            }
            Console.WriteLine($"  {steps} steps: {diff} of {n} cells differ ({100.0 * diff / n:F3}%), max |diff| {maxAbs:E3}, max ULP {maxUlp}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    static void CpuBench()
    {
        Console.WriteLine($"## CPU diffusion step (Vector256 accelerated={Vector256.IsHardwareAccelerated}, Vector128={Vector128.IsHardwareAccelerated}, threads={Environment.ProcessorCount})");
        foreach (var w in Worlds)
        {
            foreach (var kind in new[] { "Exact i32", "Approx f32" })
            {
                foreach (var (label, width, mt) in new[] { ("V256 1T", SimdWidth.V256, false), ("V256 MT", SimdWidth.V256, true), ("V128 MT", SimdWidth.V128, true), ("scalar MT", SimdWidth.Scalar, true) })
                {
                    if (label.StartsWith("scalar") && w.active >= 1 && w.x == 512) continue; // too slow to be interesting
                    bool big = w.x == 512 && w.active >= 1;
                    int iters = big ? (mt ? 30 : 6) : (mt ? 200 : 30);
                    List<double> t;
                    if (kind.StartsWith("Exact"))
                    {
                        using var f = MakeExact(w.x, w.y, w.z, w.active);
                        t = Stats.Time(3, iters, () => CpuDiffusion<int, ExactRule>.Step(f, width, mt));
                    }
                    else
                    {
                        using var f = MakeApprox(w.x, w.y, w.z, w.active);
                        t = Stats.Time(3, iters, () => CpuDiffusion<float, ApproxRule>.Step(f, width, mt));
                    }
                    Stats.Line($"{w.name} | {kind} | {label}", t);
                }
            }
        }
        using (var f = MakeExact(512, 512, 256, 1.0))
            Console.WriteLine($"  full world host memory: pool {f.PoolBytes / 1048576.0:F0} MiB x2 (double buffer) + tables, total {f.HostBytes / 1048576.0:F1} MiB; slots={f.SlotCount}");
        using (var f = MakeExact(512, 512, 256, 0.05))
            Console.WriteLine($"  sparse world host memory: {f.HostBytes / 1048576.0:F1} MiB; slots={f.SlotCount} of {f.ChunkCount} chunks");
    }

    // ---------------------------------------------------------------------------------------------
    static string GpuMemUsed()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader") { RedirectStandardOutput = true, UseShellExecute = false })!;
            string s = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return s;
        }
        catch { return "n/a"; }
    }

    static int[] RandomSlots(int slotCount, double fraction, int seed)
    {
        int n = Math.Max(1, (int)(slotCount * fraction));
        var rnd = new Random(seed);
        var all = Enumerable.Range(0, slotCount).ToArray();
        rnd.Shuffle(all);
        var r = all.Take(n).ToArray();
        Array.Sort(r);
        return r;
    }

    static void GpuBench(uint backend)
    {
        string mem0 = GpuMemUsed();
        using var dev = new WgpuDevice(backend);
        Console.WriteLine($"## GPU Backend {dev.Capabilities.BackendName}: {dev.Capabilities.AdapterName}, timestamps={dev.Capabilities.TimestampQuery}");
        Console.WriteLine($"  GPU memory used before device: {mem0}");

        foreach (var w in Worlds)
        {
            foreach (var kind in new[] { "Exact i32", "Approx f32" })
            {
                using var fe = kind.StartsWith("Exact") ? MakeExact(w.x, w.y, w.z, w.active) : null;
                using var ff = fe == null ? MakeApprox(w.x, w.y, w.z, w.active) : null;
                using var ge = fe != null ? new GpuDiffusion<int, ExactRule>(dev, fe) : null;
                using var gf = ff != null ? new GpuDiffusion<float, ApproxRule>(dev, ff) : null;
                Action upload = ge != null ? ge.UploadAll : gf!.UploadAll;
                Action<ICommandBatch, int, int> record = ge != null ? ge.RecordSteps : gf!.RecordSteps;
                Func<int, double[]> readTs = ge != null ? ge.ReadTimestamps : gf!.ReadTimestamps;
                upload(); dev.WaitIdle();
                string tag = $"{w.name} | {kind}";
                if (kind.StartsWith("Exact") && w.active >= 1 && w.x == 512)
                    Console.WriteLine($"  GPU memory used with full-world mirror allocated: {GpuMemUsed()} (mirror buffers: {(ge!.DeviceBytes) / 1048576.0:F0} MiB incl. 2x pool + staging ring)");

                // (2a) kernel time via timestamp queries: 32 passes per submit, 4 submits after warm-up.
                if (dev.Capabilities.TimestampQuery)
                {
                    var gpuMs = new List<double>();
                    for (int rep = 0; rep < 6; rep++)
                    {
                        var b = dev.BeginBatch(); record(b, 32, 0); dev.Submit(b).Wait();
                        var ts = readTs(32);
                        if (rep >= 2) gpuMs.AddRange(ts);
                    }
                    Stats.Line($"{tag} | GPU kernel (timestamps)", gpuMs);
                }
                // (2b) CPU-side fence timing: submit one step, wait.
                var one = Stats.Time(5, 50, () => { var b = dev.BeginBatch(); record(b, 1, -1); dev.Submit(b).Wait(); });
                Stats.Line($"{tag} | 1 step submit+fence wait (CPU clock)", one);
                // Throughput: 20 steps per submit.
                var many = Stats.Time(1, 5, () => { var b = dev.BeginBatch(); record(b, 20, -1); dev.Submit(b).Wait(); });
                Stats.Line($"{tag} | per step, 20 steps/submit (CPU clock)", many.Select(x => x / 20).ToList());
            }
        }

        // (3) transfers, full dense world, Exact
        Console.WriteLine("## Transfers (full 512x512x256 dense world, i32, 16 KiB chunks)");
        {
            using var f = MakeExact(512, 512, 256, 1.0);
            using var g = new GpuDiffusion<int, ExactRule>(dev, f);
            double mib = f.PoolBytes / 1048576.0;
            var up = Stats.Time(2, 10, () => { g.UploadAll(); dev.Submit(dev.BeginBatch()).Wait(); }); // writeBuffer is only flushed by the next submit
            Stats.Line($"full upload {mib:F0} MiB, queue.writeBuffer + wait", up);
            Console.WriteLine($"    -> {mib / 1024 / (Stats.Mean(up) / 1000):F2} GiB/s");
            var down = Stats.Time(2, 10, g.ReadbackAll);
            Stats.Line($"full readback {mib:F0} MiB, copy+map+memcpy (4 x 64 MiB)", down);
            Console.WriteLine($"    -> {mib / 1024 / (Stats.Mean(down) / 1000):F2} GiB/s");

            foreach (double frac in new[] { 0.01, 0.05, 0.25 })
            {
                var slots = RandomSlots(f.SlotCount, frac, 42);
                double bm = slots.Length * 16.0 / 1024;
                var stag = Stats.Time(3, 30, () =>
                {
                    var b = dev.BeginBatch();
                    int r = g.UploadChunks(b, slots);
                    dev.Submit(b).Wait();
                    g.RecycleUpload(r);
                });
                Stats.Line($"dirty upload {frac:P0} ({slots.Length} chunks, {bm:F1} MiB) staging ring + copies", stag);
                var wb = Stats.Time(3, 30, () => { g.UploadChunksWriteBuffer(slots); dev.Submit(dev.BeginBatch()).Wait(); });
                Stats.Line($"dirty upload {frac:P0} ({slots.Length} chunks) writeBuffer per chunk", wb);
                var rb = Stats.Time(3, 30, () =>
                {
                    var b = dev.BeginBatch();
                    int r = g.RecordReadback(b, slots);
                    dev.Submit(b);
                    g.BeginReadbackMap(r, slots.Length);
                    g.CompleteReadback(r, slots);
                });
                Stats.Line($"dirty readback {frac:P0} ({slots.Length} chunks) copy+map+scatter", rb);
                Console.WriteLine($"    -> upload {bm / 1024 / (Stats.Mean(stag) / 1000):F2} GiB/s, readback {bm / 1024 / (Stats.Mean(rb) / 1000):F2} GiB/s");
            }
        }
        Console.WriteLine($"  process private bytes now: {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F0} MiB");
    }

    // ---------------------------------------------------------------------------------------------
    /// <summary>(4) per-Tick cost in the two usage patterns with deferred completion (k=1).</summary>
    static void EndToEnd(uint backend)
    {
        using var dev = new WgpuDevice(backend);
        Console.WriteLine($"## End-to-end per Tick, {dev.Capabilities.BackendName}, Exact i32, deferred completion k=1");
        foreach (var w in Worlds)
        {
            using var f = MakeExact(w.x, w.y, w.z, w.active);
            // Staging ring sized to the whole pool (2 x up, 2 x down) so a full readback is one deferred transfer.
            using var g = new GpuDiffusion<int, ExactRule>(dev, f, ringChunks: f.SlotCount, ringSize: 2);
            g.UploadAll(); dev.WaitIdle();
            var allSlots = Enumerable.Range(0, f.SlotCount).ToArray();

            // CPU baseline for the same world.
            var cpuT = Stats.Time(3, w.x == 512 && w.active >= 1 ? 30 : 100, () => CpuDiffusion<int, ExactRule>.Step(f, SimdWidth.V256, true));
            Stats.Line($"{w.name} | CPU V256 MT step (baseline)", cpuT);

            // ---- GPU-authoritative: state lives on GPU; occasional readback.
            foreach (var (label, every, frac) in new[] { ("no readback", 0, 0.0), ("1% chunks read back every tick", 1, 0.01), ("full readback every 60 ticks", 60, 1.0), ("full readback every tick", 1, 1.0) })
            {
                int ticks = frac >= 1 && every == 1 ? 20 : 240;
                var readSlots = frac >= 1 ? allSlots : RandomSlots(f.SlotCount, frac, 7);
                if (readSlots.Length > g.RingChunks) readSlots = readSlots[..g.RingChunks]; // single ring buffer per tick (shortcut, see report)
                var tickMs = new List<double>(); var waitMs = new List<double>();
                IFence? prev = null; int prevRing = -1;
                for (int t = 0; t < ticks + 10; t++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var b = dev.BeginBatch();
                    g.RecordSteps(b, 1);
                    int ring = -1;
                    if (every > 0 && t % every == 0) ring = g.RecordReadback(b, readSlots);
                    var fence = dev.Submit(b);
                    if (ring >= 0) g.BeginReadbackMap(ring, readSlots.Length);
                    // Apply tick T-1's results now (k=1). Wait if the GPU is late.
                    long w0 = Stopwatch.GetTimestamp();
                    prev?.Wait();
                    double waited = Stopwatch.GetElapsedTime(w0).TotalMilliseconds;
                    if (prevRing >= 0) g.CompleteReadback(prevRing, readSlots);
                    prev = fence; prevRing = ring;
                    if (t >= 10) { tickMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds); waitMs.Add(waited); }
                }
                prev?.Wait(); if (prevRing >= 0) g.CompleteReadback(prevRing, readSlots);
                Stats.Line($"{w.name} | GPU-auth, {label} | tick wall", tickMs);
                Stats.Line($"{w.name} | GPU-auth, {label} | of which fence wait", waitMs);
            }

            // ---- CPU-authoritative with GPU mirror: upload dirty chunks, step, read back changed chunks, apply.
            foreach (double dirty in new[] { 0.01, 0.05, 0.25 })
            {
                var dirtySlots = RandomSlots(f.SlotCount, dirty, 11);
                if (dirtySlots.Length > g.RingChunks) dirtySlots = dirtySlots[..g.RingChunks];
                // Changed chunks after a diffusion step = every stepped chunk. Read back in ring-sized pieces.
                int ticks = f.SlotCount > 4096 ? 10 : 100;
                var sync = Stats.Time(2, ticks, () =>
                {
                    foreach (int s in dirtySlots) f.ChunkPtr(f.Cur, s)[0] += 1; // the CPU "edits" the dirty chunks
                    var b = dev.BeginBatch();
                    int ur = g.UploadChunks(b, dirtySlots);
                    g.RecordSteps(b, 1);
                    int rr = g.RecordReadback(b, allSlots); // changed chunks after a diffusion step = every stepped chunk
                    dev.Submit(b);
                    g.RecycleUpload(ur);
                    g.BeginReadbackMap(rr, allSlots.Length);
                    g.CompleteReadback(rr, allSlots);
                });
                Stats.Line($"{w.name} | CPU-auth mirror, {dirty:P0} dirty up, all active back | sync", sync);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    /// <summary>Where the transfer time goes: host memcpy, pack into mapped staging, GPU copy, map wait.</summary>
    static void TransferDetail(uint backend)
    {
        using var dev = new WgpuDevice(backend);
        Console.WriteLine($"## Transfer breakdown, {dev.Capabilities.BackendName}, full dense world, whole-pool staging ring");
        using var f = MakeExact(512, 512, 256, 1.0);
        using var g = new GpuDiffusion<int, ExactRule>(dev, f, ringChunks: f.SlotCount, ringSize: 2);
        nuint bytes = (nuint)f.PoolBytes;
        var host = Stats.Time(2, 10, () => System.Runtime.InteropServices.NativeMemory.Copy(f.Cur, f.Next, bytes));
        Stats.Line("host memcpy 256 MiB (pool -> pool), reference", host);

        var all = Enumerable.Range(0, f.SlotCount).ToArray();
        foreach (var (label, slots) in new[] { ("full 256 MiB", all), ("25% scattered", RandomSlots(f.SlotCount, 0.25, 42)), ("5% scattered", RandomSlots(f.SlotCount, 0.05, 42)) })
        {
            var pack = new List<double>(); var gpu = new List<double>(); var tot = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var b = dev.BeginBatch();
                int r = g.UploadChunks(b, slots); // includes waiting for the ring buffer's re-map and the pack memcpy
                double p = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                long t1 = Stopwatch.GetTimestamp();
                dev.Submit(b).Wait();
                double q = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                g.RecycleUpload(r);
                if (i >= 2) { pack.Add(p); gpu.Add(q); tot.Add(p + q); }
            }
            Stats.Line($"staging upload {label}: map-wait+pack into mapped", pack);
            Stats.Line($"staging upload {label}: copy submit+fence", gpu);
            Stats.Line($"staging upload {label}: total", tot);

            var copy = new List<double>(); var map = new List<double>(); var scat = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var b = dev.BeginBatch();
                int r = g.RecordReadback(b, slots);
                dev.Submit(b).Wait();
                double c = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                long t1 = Stopwatch.GetTimestamp();
                g.BeginReadbackMap(r, slots.Length);
                while (!g.ReadbackReady(r)) { }
                double m = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                long t2 = Stopwatch.GetTimestamp();
                g.CompleteReadback(r, slots);
                double s = Stopwatch.GetElapsedTime(t2).TotalMilliseconds;
                if (i >= 2) { copy.Add(c); map.Add(m); scat.Add(s); }
            }
            Stats.Line($"readback {label}: GPU copy submit+fence", copy);
            Stats.Line($"readback {label}: mapAsync until ready", map);
            Stats.Line($"readback {label}: scatter memcpy from mapped", scat);
        }
        // queue.writeBuffer full pool, repeated: shows wgpu's internal staging allocation behaviour.
        var wb = new List<double>(); var wbCall = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            g.UploadAll();
            double c = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            dev.Submit(dev.BeginBatch()).Wait(); // pending writes are flushed by the next submit
            if (i >= 2) { wb.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds); wbCall.Add(c); }
        }
        Stats.Line("queue.writeBuffer 256 MiB: call only", wbCall);
        Stats.Line("queue.writeBuffer 256 MiB: call + empty submit + fence", wb);
    }

    // ---------------------------------------------------------------------------------------------
    static void PInvokeCost(uint backend)
    {
        Console.WriteLine("## P/Invoke layer cost");
        const int N = 20_000_000;
        for (int rep = 0; rep < 2; rep++)
        {
            long t0 = Stopwatch.GetTimestamp();
            uint acc = 0;
            for (int i = 0; i < N; i++) acc += Wgpu.wgpuGetVersion();
            double ns = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1e6 / N;
            if (rep == 1) Console.WriteLine($"  wgpuGetVersion (trivial native call): {ns:F2} ns/call (acc {acc})");
            t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < N; i++) acc += Wgpu.wgpuGetVersionNoTransition();
            ns = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1e6 / N;
            if (rep == 1) Console.WriteLine($"  same with [SuppressGCTransition]: {ns:F2} ns/call (acc {acc})");
        }
        using var dev = new WgpuDevice(backend);
        using var f = MakeExact(256, 256, 64, 1.0);
        using var g = new GpuDiffusion<int, ExactRule>(dev, f);
        // Recording one dispatch = BeginComputePass, SetPipeline, SetBindGroup, Dispatch, End, Release (6 calls).
        for (int rep = 0; rep < 3; rep++)
        {
            var b = dev.BeginBatch();
            long t0 = Stopwatch.GetTimestamp();
            g.RecordSteps(b, 1000);
            double us = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000 / 1000;
            long s0 = Stopwatch.GetTimestamp();
            dev.Submit(b);
            double subMs = Stopwatch.GetElapsedTime(s0).TotalMilliseconds;
            dev.WaitIdle();
            if (rep > 0) Console.WriteLine($"  record one dispatch (6 native calls incl. wgpu validation): {us:F2} us; finish+submit of 1000 dispatches: {subMs:F2} ms");
        }
        for (int rep = 0; rep < 3; rep++)
        {
            var b = dev.BeginBatch();
            var slots = Enumerable.Range(0, 1024).Where(i => i % 2 == 0).ToArray(); // non-adjacent -> one copy each
            long t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < 4; k++) g.RecordReadback(b, slots);
            double us = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000 / (4 * slots.Length);
            dev.Submit(b); dev.WaitIdle();
            if (rep > 0) Console.WriteLine($"  record one copyBufferToBuffer: {us:F2} us");
        }
        {
            byte* tmp = stackalloc byte[64];
            for (int rep = 0; rep < 2; rep++)
            {
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < 10000; i++) dev.Write(g.Params, 0, tmp, 32);
                double us = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000 / 10000;
                dev.WaitIdle();
                if (rep > 0) Console.WriteLine($"  queue.writeBuffer 32 B: {us:F2} us/call");
            }
            g.SetActive(Enumerable.Range(0, f.SlotCount).Select(i => (uint)i).ToArray());
        }
        {
            var t = Stats.Time(100, 2000, () => { var b = dev.BeginBatch(); dev.Submit(b).Wait(); });
            Stats.Line("  empty submit + fence wait round trip", t);
        }
    }
}




