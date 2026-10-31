using Colony.Diagnostics;

namespace Colony.Core.Tests.Diagnostics;

/// <summary>
/// TASK-17 AC #1 and #6: <c>world.Diagnostics</c> hands out the latest N Tick records as structs and spans,
/// without allocating, with a versioned layout.
/// </summary>
public sealed class TickRecordTests
{
    private const int Entities = 2_000;

    [Fact]
    public void The_latest_record_describes_the_tick_that_just_ran()
    {
        using var world = CreateWorld(new DiagnosticsOptions { Level = DiagnosticsLevel.Standard });
        world.Tick();
        world.Tick();

        var record = world.Diagnostics.Latest;
        Assert.Equal(1, record.Tick.Tick);
        Assert.Equal(TickRecord.LayoutVersion, record.Tick.Version);
        Assert.Equal(DiagnosticsLevel.Standard, record.Tick.Level);
        Assert.True(record.Tick.WallTicks > 0);
        Assert.Equal(Entities, record.Tick.EntityCount);
        Assert.Equal(world.Schema.Phases.Length, record.Phases.Length);
        Assert.Equal(world.Schema.Systems.Length, record.Systems.Length);
        Assert.Equal(world.WorkerCount, record.Workers.Length);
        Assert.True(record.Tick.ChunksInUse > 0);
        Assert.True(record.Tick.ReservedBytes > 0);
    }

    [Fact]
    public void Per_system_records_carry_time_jobs_and_entities_touched()
    {
        using var world = CreateWorld(new DiagnosticsOptions { Level = DiagnosticsLevel.Standard });
        world.Tick();

        var record = world.Diagnostics.Latest;
        var move = record.Systems[world.Schema.System("Move").Id];
        Assert.True(move.Ran);
        Assert.True(move.Jobs > 0);
        Assert.Equal(move.Jobs, move.ChunksVisited);
        Assert.Equal(Entities, move.EntitiesTouched);
        Assert.True(move.CpuTicks > 0, "the Move System must have measured CPU time");
        Assert.True(move.WallTicks >= move.CpuTicks / world.WorkerCount);

        var skipper = record.Systems[world.Schema.System("Skipper").Id];
        Assert.False(skipper.Ran, "Skipper runs every 2 Ticks starting at offset 1");

        var simulate = record.Phases[Phase.Simulate.Id];
        Assert.True(simulate.WallTicks > 0);
        Assert.True(simulate.WallTicks >= move.WallTicks);
    }

    [Fact]
    public void Chunks_skipped_by_the_change_filter_are_counted()
    {
        using var world = CreateWorld(new DiagnosticsOptions { Level = DiagnosticsLevel.Standard });
        world.Tick();
        world.Tick();

        var filtered = world.Diagnostics.Latest.Systems[world.Schema.System("Filtered").Id];
        Assert.True(filtered.Ran);
        Assert.Equal(0, filtered.ChunksVisited);
        Assert.True(filtered.ChunksSkipped > 0);
    }

    [Fact]
    public void Off_records_only_tick_time_and_the_allocation_check()
    {
        using var world = CreateWorld(new DiagnosticsOptions { Level = DiagnosticsLevel.Off });
        world.Tick();

        var record = world.Diagnostics.Latest;
        Assert.Equal(DiagnosticsLevel.Off, record.Tick.Level);
        Assert.True(record.Tick.WallTicks > 0);
        Assert.Equal(0, record.Systems[world.Schema.System("Move").Id].CpuTicks);
        Assert.Equal(0, record.Phases[Phase.Simulate.Id].WallTicks);
    }

    [Fact]
    public void The_ring_keeps_the_latest_records_in_tick_order()
    {
        using var world = CreateWorld(new DiagnosticsOptions { RecordCapacity = 4 });
        for (int i = 0; i < 10; i++)
        {
            world.Tick();
        }

        Assert.Equal(4, world.Diagnostics.Count);
        Assert.Equal(10, world.Diagnostics.TicksRecorded);
        Assert.Equal(9, world.Diagnostics.Latest.Tick.Tick);
        Assert.Equal(6, world.Diagnostics.Record(3).Tick.Tick);
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Diagnostics.Record(4));

        Span<TickRecord> copy = stackalloc TickRecord[8];
        int copied = world.Diagnostics.CopyLatest(copy);
        Assert.Equal(4, copied);
        Assert.Equal([6L, 7L, 8L, 9L], copy[..copied].ToArray().Select(r => r.Tick).ToArray());
    }

    [Fact]
    public void Recording_and_reading_allocate_nothing_in_steady_state()
    {
        using var world = CreateWorld(new DiagnosticsOptions { Level = DiagnosticsLevel.Standard, RecordCapacity = 8 });
        for (int i = 0; i < 20; i++)
        {
            world.Tick();
            ReadAll(world);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long workersBefore = world.WorkerAllocatedBytes;
        for (int i = 0; i < 10; i++)
        {
            world.Tick();
            ReadAll(world);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, world.WorkerAllocatedBytes - workersBefore);
    }

    private static void ReadAll(World world)
    {
        for (int ago = 0; ago < world.Diagnostics.Count; ago++)
        {
            var record = world.Diagnostics.Record(ago);
            long sum = record.Tick.WallTicks;
            foreach (ref readonly var system in record.Systems)
            {
                sum += system.CpuTicks;
            }

            foreach (ref readonly var worker in record.Workers)
            {
                sum += worker.BusyTicks;
            }

            if (sum < 0)
            {
                throw new InvalidOperationException();
            }
        }
    }

    internal static World CreateWorld(DiagnosticsOptions diagnostics, int workers = 2, bool debug = false)
    {
        var builder = new WorldBuilder()
            .AddComponent<Position>("position")
            .AddComponent<Hunger>("hunger")
            .AddComponent<Health>("health")
            .DebugValidation(debug);
        var position = builder.Component<Position>();
        var hunger = builder.Component<Hunger>();
        var health = builder.Component<Health>();
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Move",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(position),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    var positions = chunk.Column<Position>(position);
                    for (int i = 0; i < positions.Length; i++)
                    {
                        positions[i].X += 1;
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Hunger",
                Phase = Phase.Simulate,
                Writes = ComponentMask.Of(hunger),
                ForEachChunk = (in SystemContext _, in ChunkView chunk) =>
                {
                    var values = chunk.Column<Hunger>(hunger);
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i].Value += 0.5f;
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Skipper",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(health),
                Every = 2,
                Offset = 1,
                Begin = (in SystemContext _) => { },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Filtered",
                Phase = Phase.Late,
                Reads = ComponentMask.Of(health),
                ChangeFilter = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext _, in ChunkView _) => { },
            })
            .Build();

        var world = schema.CreateWorld(new WorldOptions
        {
            WorkerThreads = workers,
            InitialEntityCapacity = Entities * 2,
            Diagnostics = diagnostics,
        });
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < Entities; i++)
        {
            var e = buffer.Create(ComponentMask.Of(position, hunger, health));
            buffer.Add(e, position, new Position { X = i, Y = i });
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }
}
