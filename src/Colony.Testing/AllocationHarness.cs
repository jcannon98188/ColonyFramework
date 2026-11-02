using System.Globalization;

namespace Colony.Testing;

/// <summary>
/// The allocation harness (spec section 10.3, principle 2): after a warm-up, every byte the Tick thread
/// or a worker thread allocates during the measured Ticks fails the scenario. To say where, the harness
/// composes the scenario a second time with probe Systems between the waves of every Phase; each probe
/// samples the Tick thread's and the workers' allocation counters, so an allocation is attributed to the
/// wave (and so the System, when it ran alone) that was executing. It also reads the World's own diagnostics
/// allocation check (<see cref="Colony.Diagnostics.DiagnosticsOptions.AllocationCheck"/>, sampled at every job
/// boundary) for the measured Ticks, which names the exact System even inside a shared wave
/// (<see cref="AllocationResult.Systems"/>). The measurement loop allocates nothing.
/// </summary>
public static class AllocationHarness
{
    private const string ProbePrefix = "colony.testing.alloc-probe";

    public static AllocationResult Run(Scenario scenario, AllocationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        options ??= new AllocationOptions();
        options.Validate();

        // Pass 1: the plain schema tells us the Phases, waves and System names.
        var plain = DeterminismHarness.BuildSchema(scenario, options.DebugValidation);
        var plan = ProbePlan.From(plain, options.HashEveryTick);

        // Pass 2: the same scenario with a probe at every wave boundary.
        var probes = new Probes(plan.ProbeCount);
        var schema = DeterminismHarness.BuildSchema(scenario, options.DebugValidation, builder => plan.AddProbes(builder, probes));
        using var world = DeterminismHarness.CreateWorld(scenario, schema, options.Workers, listener: null);
        probes.World = world;

        for (int i = 0; i < options.WarmupTicks; i++)
        {
            scenario.Advance(world);
            if (options.HashEveryTick)
            {
                world.Hash();
            }
        }

        var mainBytes = new long[plan.SiteCount];
        var workerBytes = new long[plan.SiteCount];
        var systemBytes = new long[schema.Systems.Length];
        long diagnosticsBytes = 0;
        for (int i = 0; i < options.MeasuredTicks; i++)
        {
            long recorded = world.Diagnostics.TicksRecorded;
            MeasureTick(scenario, world, options.HashEveryTick, probes, mainBytes, workerBytes);
            diagnosticsBytes += ReadDiagnostics(world.Diagnostics, recorded, systemBytes);
        }

        return new AllocationResult(scenario.Name, world.WorkerCount, options.MeasuredTicks, options.ToleranceBytes, plan.ToSites(mainBytes, workerBytes), diagnosticsBytes, Systems(schema, systemBytes));
    }

    /// <summary>Adds the diagnostics records of the Ticks published since <paramref name="recordedBefore"/>; returns their Tick totals.</summary>
    private static long ReadDiagnostics(Colony.Diagnostics.WorldDiagnostics diagnostics, long recordedBefore, long[] systemBytes)
    {
        int ticks = (int)Math.Min(diagnostics.TicksRecorded - recordedBefore, diagnostics.Count);
        long total = 0;
        for (int ago = 0; ago < ticks; ago++)
        {
            var record = diagnostics.Record(ago);
            total += record.Tick.AllocatedBytes;
            var systems = record.Systems;
            for (int s = 0; s < systems.Length && s < systemBytes.Length; s++)
            {
                systemBytes[s] += systems[s].AllocatedBytes;
            }
        }

        return total;
    }

    private static List<SystemAllocation> Systems(WorldSchema schema, long[] systemBytes)
    {
        var result = new List<SystemAllocation>();
        var systems = schema.Systems;
        for (int s = 0; s < systemBytes.Length; s++)
        {
            if (systemBytes[s] > 0 && !systems[s].Name.StartsWith(ProbePrefix, StringComparison.Ordinal))
            {
                result.Add(new SystemAllocation(systems[s].Name, systems[s].Phase, systemBytes[s]));
            }
        }

        return result;
    }

    /// <summary>One measured Tick: sites are [start..probe0], [probe k..probe k+1]..., [last probe..end], then the hash.</summary>
    private static void MeasureTick(Scenario scenario, World world, bool hash, Probes probes, long[] mainBytes, long[] workerBytes)
    {
        int count = probes.Count;
        probes.Reset();
        long main = GC.GetAllocatedBytesForCurrentThread();
        long workers = world.WorkerAllocatedBytes;
        scenario.Advance(world);
        long mainEnd = GC.GetAllocatedBytesForCurrentThread();
        long workersEnd = world.WorkerAllocatedBytes;

        for (int k = 0; k < count; k++)
        {
            // A probe that did not fire (its Phase ran nothing this Tick) attributes nothing and passes the baseline on.
            long probeMain = probes.Fired[k] ? probes.Main[k] : main;
            long probeWorkers = probes.Fired[k] ? probes.Workers[k] : workers;
            mainBytes[k] += probeMain - main;
            workerBytes[k] += probeWorkers - workers;
            main = probeMain;
            workers = probeWorkers;
        }

        mainBytes[count] += mainEnd - main;
        workerBytes[count] += workersEnd - workers;

        if (hash)
        {
            world.Hash();
            mainBytes[count + 1] += GC.GetAllocatedBytesForCurrentThread() - mainEnd;
        }
    }

    /// <summary>Per-World probe storage, written by the probe Systems' Begin hooks on the Tick thread.</summary>
    private sealed class Probes
    {
        public Probes(int count)
        {
            Count = count;
            Main = new long[count];
            Workers = new long[count];
            Fired = new bool[count];
        }

        public int Count { get; }

        public long[] Main { get; }

        public long[] Workers { get; }

        public bool[] Fired { get; }

        public World? World { get; set; }

        public void Reset() => Array.Clear(Fired);

        public void Sample(int index)
        {
            Main[index] = GC.GetAllocatedBytesForCurrentThread();
            Workers[index] = World!.WorkerAllocatedBytes;
            Fired[index] = true;
        }
    }

    /// <summary>Where the probes go and what each measured stretch means.</summary>
    private sealed class ProbePlan
    {
        private readonly List<(Phase Phase, int Index, string[] After, string[] Before)> _probes = [];
        private readonly List<(string Label, Phase? Phase, string[] Systems)> _sites = [];

        public int ProbeCount => _probes.Count;

        public int SiteCount => _sites.Count;

        public static ProbePlan From(WorldSchema schema, bool hash)
        {
            var plan = new ProbePlan();
            var systems = schema.Systems;
            string[] previousBoundaryLabel = [];
            Phase? previousPhase = null;
            plan._sites.Add(("Tick start (extension BeginTick, Phases without Systems)", null, previousBoundaryLabel));
            foreach (var phase in schema.Phases)
            {
                int waveCount = 0;
                foreach (ref readonly var descriptor in systems)
                {
                    if (descriptor.Phase == phase)
                    {
                        waveCount = Math.Max(waveCount, descriptor.Wave + 1);
                    }
                }

                if (waveCount == 0)
                {
                    continue;
                }

                var waves = new string[waveCount][];
                for (int w = 0; w < waveCount; w++)
                {
                    var names = new List<string>();
                    foreach (ref readonly var descriptor in systems)
                    {
                        if (descriptor.Phase == phase && descriptor.Wave == w)
                        {
                            names.Add(descriptor.Name);
                        }
                    }

                    names.Sort((a, b) => schema.System(a).Order.CompareTo(schema.System(b).Order));
                    waves[w] = [.. names];
                }

                // The previous site (Tick start or the previous Phase's boundary) ends at this Phase's first probe.
                if (previousPhase is { } prev)
                {
                    plan._sites[^1] = ($"Phase {prev.Name} boundary (command playback, extension EndPhase)", prev, []);
                }

                for (int w = 0; w <= waveCount; w++)
                {
                    plan._probes.Add((phase, w, w == 0 ? [] : waves[w - 1], w == waveCount ? [] : waves[w]));
                    if (w < waveCount)
                    {
                        plan._sites.Add(($"Phase {phase.Name} wave {w}: {string.Join(", ", waves[w])}", phase, waves[w]));
                    }
                }

                plan._sites.Add(($"Phase {phase.Name} boundary and Tick end (command playback, extension hooks, scratch reset, driver)", phase, []));
                previousPhase = phase;
            }

            if (previousPhase is null)
            {
                plan._sites[0] = ("Tick (no Systems registered)", null, []);
            }

            if (hash)
            {
                plan._sites.Add(("World.Hash()", null, []));
            }

            return plan;
        }

        public void AddProbes(WorldBuilder builder, Probes probes)
        {
            for (int i = 0; i < _probes.Count; i++)
            {
                var (phase, index, after, before) = _probes[i];
                int slot = i;
                builder.AddSystem(new SystemDefinition
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"{ProbePrefix}.{phase.Name}.{index}"),
                    Phase = phase,
                    After = after,
                    Before = before,
                    Begin = (in SystemContext _) => probes.Sample(slot),
                });
            }
        }

        public AllocationSite[] ToSites(long[] mainBytes, long[] workerBytes)
        {
            var sites = new AllocationSite[_sites.Count];
            for (int i = 0; i < sites.Length; i++)
            {
                var (label, phase, systems) = _sites[i];
                sites[i] = new AllocationSite(label, phase, systems) { MainThreadBytes = mainBytes[i], WorkerBytes = workerBytes[i] };
            }

            return sites;
        }
    }
}
