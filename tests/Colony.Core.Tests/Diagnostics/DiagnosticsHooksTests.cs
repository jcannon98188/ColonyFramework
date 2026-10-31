using System.Diagnostics;
using Colony.Diagnostics;

namespace Colony.Core.Tests.Diagnostics;

/// <summary>
/// TASK-17 AC #3 (structured logging), #4 (allocation check names the System per thread), the Tick budget,
/// the validation switches (access declarations, determinism spot-check), counters and the Detailed trace.
/// </summary>
public sealed class DiagnosticsHooksTests
{
    [Fact]
    public void The_allocation_check_names_the_system_that_allocated_on_each_thread()
    {
        var listener = new Recorder();
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var sink = new List<object>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Clean",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) => chunk.Column<Position>(position)[0].X++,
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Leaky",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext context, in ChunkView _) =>
                {
                    // Long enough that the other workers take jobs too on an idle machine; the
                    // assertions below do not depend on it (a loaded machine may give worker 0 every job).
                    var spin = new SpinWait();
                    long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2000;
                    while (Stopwatch.GetTimestamp() < until)
                    {
                        spin.SpinOnce(-1);
                    }

                    lock (sink)
                    {
                        sink.Add(new byte[64 + context.JobIndex]);
                    }
                },
            })
            .Build();
        using var world = Populate(schema, position, chunks: 8, new WorldOptions
        {
            WorkerThreads = 4,
            Diagnostics = new DiagnosticsOptions { Listener = listener },
        });

        for (int i = 0; i < 3; i++)
        {
            world.Tick();
            listener.Allocations.Clear();
            world.Tick();
        }

        var record = world.Diagnostics.Latest;
        int leaky = schema.System("Leaky").Id;
        Assert.True(record.Tick.AllocatedBytes >= 8 * 64);
        Assert.True(record.Tick.AllocatingThreads > 0);
        Assert.True(record.Systems[leaky].AllocatedBytes >= 8 * 64);
        Assert.Equal(0, record.Systems[schema.System("Clean").Id].AllocatedBytes);

        // Per thread: every worker that allocated names Leaky as the first allocating System, and the
        // listener got exactly one report per allocating worker, whichever workers the pool used.
        var allocatingWorkers = new List<int>();
        for (int w = 0; w < record.Workers.Length; w++)
        {
            ref readonly var worker = ref record.Workers[w];
            if (worker.AllocatedBytes > 0)
            {
                Assert.Equal(leaky, worker.FirstAllocatingSystem);
                allocatingWorkers.Add(w);
            }
            else
            {
                Assert.Equal(-1, worker.FirstAllocatingSystem);
            }
        }

        Assert.Equal(record.Tick.AllocatingThreads, allocatingWorkers.Count);
        Assert.Equal(allocatingWorkers, listener.Allocations.Select(a => a.Worker).Order());
        Assert.All(listener.Allocations, a => Assert.Equal("Leaky", a.SystemName));
        long tick = record.Tick.Tick;
        Assert.All(listener.Allocations, a => Assert.Equal(tick, a.Tick));
    }

    [Fact]
    public void Structured_logging_allocates_nothing_and_resolves_to_entries_in_tick_system_job_order()
    {
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var moved = builder.AddLogMessage("colonist {0} moved to {1},{2}");
        var began = builder.AddLogMessage("tick {0} begins with {1} jobs");
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Move",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(position),
                Begin = (in SystemContext context) => context.Log(began, context.Tick, context.JobCount),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
                {
                    var positions = chunk.Column<Position>(position);
                    positions[0].X++;
                    context.Log(moved, chunk.Entities[0].Index, positions[0].X, positions[0].Y);
                },
            })
            .Build();
        using var world = Populate(schema, position, chunks: 6, new WorldOptions
        {
            WorkerThreads = 3,
            Diagnostics = new DiagnosticsOptions { LogCapacityPerThread = 64 },
        });
        var drained = new LogEntry[64];

        for (int i = 0; i < 5; i++)
        {
            world.Tick();
            world.Diagnostics.DrainLogs(drained);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        world.Tick();
        world.Tick();
        int pending = world.Diagnostics.PendingLogs;
        int count = world.Diagnostics.DrainLogs(drained);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long workersAllocated = world.WorkerAllocatedBytes - workersBefore;

        Assert.Equal(0, allocated);
        Assert.Equal(0, workersAllocated);
        Assert.Equal(14, pending);
        Assert.Equal(14, count);
        Assert.Equal(0, world.Diagnostics.PendingLogs);
        var entries = drained[..count];
        Assert.Equal(began, entries[0].Message);
        Assert.Equal(5, entries[0].Tick);
        Assert.Equal(-1, entries[0].JobIndex);
        Assert.Equal(6, entries[0].Arg1);
        Assert.Equal(Enumerable.Range(0, 6), entries[1..7].Select(e => e.JobIndex));
        Assert.All(entries[1..7], e => Assert.Equal(5, e.Tick));
        Assert.All(entries[1..7], e => Assert.Equal(moved, e.Message));
        Assert.All(entries[1..7], e => Assert.Equal(schema.System("Move").Id, e.SystemId));
        Assert.All(entries[7..], e => Assert.Equal(6, e.Tick));
        Assert.Equal(0, world.Diagnostics.DroppedLogs);
        Assert.Equal("colonist {0} moved to {1},{2}", schema.LogMessages[moved.Id]);
    }

    [Fact]
    public void Full_log_rings_overwrite_the_oldest_entries_and_count_them_as_dropped()
    {
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var message = builder.AddLogMessage("job {0}");
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Chatty",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext context, in ChunkView _) => context.Log(message, context.JobIndex),
            })
            .Build();
        using var world = Populate(schema, position, chunks: 10, new WorldOptions
        {
            WorkerThreads = 1,
            Diagnostics = new DiagnosticsOptions { LogCapacityPerThread = 4 },
        });

        world.Tick();

        Assert.Equal(6, world.Diagnostics.DroppedLogs);
        var drained = new LogEntry[10];
        Assert.Equal(4, world.Diagnostics.DrainLogs(drained));
        Assert.Equal([6L, 7L, 8L, 9L], drained[..4].Select(e => e.Arg0));
        Assert.Equal(6, world.Diagnostics.Latest.Tick.LogsDropped);
    }

    [Fact]
    public void A_tick_over_budget_is_marked_slow_and_reported_with_the_top_systems_rate_limited()
    {
        var listener = new Recorder();
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Fast",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(position),
                Begin = (in SystemContext _) => { },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Slow",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(position),
                Begin = (in SystemContext _) => Thread.Sleep(3),
            })
            .Build();
        using var world = Populate(schema, position, chunks: 1, new WorldOptions
        {
            WorkerThreads = 1,
            Diagnostics = new DiagnosticsOptions
            {
                TickBudget = TimeSpan.FromMilliseconds(1),
                SlowTickReportInterval = 3,
                SlowTickTopSystems = 1,
                Listener = listener,
            },
        });

        for (int i = 0; i < 6; i++)
        {
            world.Tick();
            Assert.True(world.Diagnostics.Latest.Tick.Slow);
        }

        Assert.Equal([0L, 3L], listener.SlowTicks.Select(s => s.Tick));
        Assert.All(listener.SlowTicks, s => Assert.Equal("Slow", s.TopSystem));
        Assert.All(listener.SlowTicks, s => Assert.True(s.WallTicks > s.BudgetTicks));
    }

    [Fact]
    public void Access_checking_rejects_a_column_the_system_did_not_declare()
    {
        var builder = new WorldBuilder().AddComponent<Position>("position").AddComponent<Hunger>("hunger");
        var position = builder.Component<Position>();
        var hunger = builder.Component<Hunger>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Sneaky",
                Phase = Phase.Simulate,
                All = ComponentMask.Of(position, hunger),
                Writes = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) => chunk.Column<Hunger>(hunger)[0].Value++,
            })
            .Build();
        using var checkedWorld = Populate(schema, position, chunks: 1, new WorldOptions
        {
            WorkerThreads = 1,
            Diagnostics = new DiagnosticsOptions { AccessChecking = true },
        }, hunger);
        using var uncheckedWorld = Populate(schema, position, chunks: 1, new WorldOptions
        {
            WorkerThreads = 1,
            Diagnostics = new DiagnosticsOptions { AccessChecking = false },
        }, hunger);

        var fault = Assert.Throws<SystemFaultException>(() => checkedWorld.Tick());
        Assert.Equal("Sneaky", fault.Fault!.Value.SystemName);
        Assert.Contains("neither Reads nor Writes", fault.Fault.Value.Exception.Message, StringComparison.Ordinal);
        Assert.True(uncheckedWorld.Tick());
    }

    [Fact]
    public void Counters_registered_at_build_time_are_summed_over_every_thread()
    {
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var visited = builder.AddCounter("test.visited");
        var rows = builder.AddCounter("test.rows");
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Count",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
                {
                    context.Count(visited, 1);
                    context.Count(rows, chunk.Count);
                },
            })
            .Build();
        using var world = Populate(schema, position, chunks: 7, new WorldOptions { WorkerThreads = 4 });
        world.Diagnostics.AddToCounter(visited, 100);

        world.Tick();

        Assert.Equal(["test.visited", "test.rows"], schema.Counters.ToArray());
        var counters = world.Diagnostics.Latest.Counters;
        Assert.Equal(107, counters[visited.Id]);
        Assert.Equal(world.EntityCount, counters[rows.Id]);
    }

    [Fact]
    public void The_determinism_spot_check_reports_state_changed_between_ticks()
    {
        var listener = new Recorder();
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Idle",
                Phase = Phase.Simulate,
                Reads = ComponentMask.Of(position),
                Begin = (in SystemContext _) => { },
            })
            .Build();
        using var world = Populate(schema, position, chunks: 1, new WorldOptions
        {
            WorkerThreads = 1,
            Diagnostics = new DiagnosticsOptions { DeterminismSpotCheckEvery = 2, Listener = listener },
        });
        EntityId first = default;
        foreach (var chunk in world.Query(position))
        {
            first = chunk.Entities[0];
        }

        world.Tick();
        world.Tick(); // Tick 2 completes: hash taken.
        world.Get<Position>(first, position).X = 12345; // out-of-Tick mutation
        world.Tick(); // recheck fails
        world.Tick(); // Tick 4 completes: hash taken.
        world.Tick(); // recheck passes

        Assert.Equal(1, world.Diagnostics.SpotCheckFailures);
        var report = Assert.Single(listener.SpotChecks);
        Assert.Equal(2, report.Tick);
        Assert.NotEqual(report.ExpectedHash, report.ActualHash);
    }

    [Fact]
    public void Detailed_level_traces_every_job_with_its_system_worker_and_chunk()
    {
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Move",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) => chunk.Column<Position>(position)[0].X++,
            })
            .Build();
        using var world = Populate(schema, position, chunks: 5, new WorldOptions
        {
            WorkerThreads = 2,
            Diagnostics = new DiagnosticsOptions { Level = DiagnosticsLevel.Detailed, JobTraceCapacity = 3 },
        });

        world.Tick();

        var record = world.Diagnostics.Latest;
        Assert.Equal(5, record.Tick.Jobs);
        Assert.Equal(3, record.Jobs.Length);
        Assert.Equal(2, record.Tick.JobsNotTraced);
        foreach (ref readonly var job in record.Jobs)
        {
            Assert.Equal(0, job.SystemId);
            Assert.InRange(job.Worker, 0, 1);
            Assert.InRange(job.Chunk, 0, 4);
            Assert.True(job.EndTicks >= job.StartTicks);
            Assert.True(job.StartTicks >= 0);
        }
    }

    [Fact]
    public void Worker_clocks_account_busy_time_at_standard_level()
    {
        var builder = new WorldBuilder().AddComponent<Position>("position");
        var position = builder.Component<Position>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Work",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    var positions = chunk.Column<Position>(position);
                    for (int k = 0; k < 50; k++)
                    {
                        for (int i = 0; i < positions.Length; i++)
                        {
                            positions[i].X += positions[i].Y;
                        }
                    }
                },
            })
            .Build();
        using var world = Populate(schema, position, chunks: 16, new WorldOptions { WorkerThreads = 2 });

        for (int i = 0; i < 3; i++)
        {
            world.Tick();
        }

        var record = world.Diagnostics.Latest;
        long busy = 0;
        foreach (ref readonly var worker in record.Workers)
        {
            busy += worker.BusyTicks;
        }

        Assert.True(busy > 0);
        Assert.True(busy >= record.Systems[0].CpuTicks);
        Assert.True(record.Workers[1].Jobs > 0 || record.Workers[0].Jobs == 16);
    }

    /// <summary>A World with <paramref name="chunks"/> full chunks of Position (plus optional extra Components) entities.</summary>
    private static World Populate(WorldSchema schema, ComponentType position, int chunks, WorldOptions options, params ReadOnlySpan<ComponentType> extra)
    {
        var world = schema.CreateWorld(new WorldOptions
        {
            WorkerThreads = options.WorkerThreads,
            Diagnostics = options.Diagnostics,
            InitialEntityCapacity = 64 * 1024,
        });
        var signature = ComponentMask.Of(position);
        foreach (var type in extra)
        {
            signature = signature.With(type);
        }

        int capacity = 0;
        var buffer = world.CreateCommandBuffer();
        var probe = buffer.Create(signature);
        buffer.Add(probe, position, new Position { X = 1, Y = 1 });
        world.Playback(buffer);
        foreach (var chunk in world.Query(signature))
        {
            capacity = chunk.Capacity;
        }

        for (int i = 1; i < capacity * chunks; i++)
        {
            var e = buffer.Create(signature);
            buffer.Add(e, position, new Position { X = i, Y = 1 });
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }

    private sealed class Recorder : IDiagnosticsListener
    {
        public List<AllocationReport> Allocations { get; } = [];

        public List<(long Tick, long WallTicks, long BudgetTicks, string TopSystem)> SlowTicks { get; } = [];

        public List<DeterminismCheckReport> SpotChecks { get; } = [];

        public void OnAllocation(World world, in AllocationReport report) => Allocations.Add(report);

        public void OnSlowTick(World world, in SlowTickReport report)
            => SlowTicks.Add((report.Tick, report.WallTicks, report.BudgetTicks, world.Schema.Systems[report.TopSystems[0].SystemId].Name));

        public void OnDeterminismCheckFailed(World world, in DeterminismCheckReport report) => SpotChecks.Add(report);
    }
}
