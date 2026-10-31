namespace Colony.Core.Tests.Scheduling;

public struct Damage
{
    public EntityId Target;
    public int Source;
    public int Row;
}

public struct Bell
{
    public long Tick;
}

/// <summary>TASK-8 AC #2: typed event queues as Resources, per-System buffers merged in System, job then record order, cleared at the End boundary.</summary>
public sealed class EventTests
{
    private const int Entities = 30_000; // several chunks of (EntityId, Health)

    private static (WorldSchema Schema, List<Damage> Seen) EmitterSchema()
    {
        var seen = new List<Damage>();
        var builder = new WorldBuilder().AddComponent<Health>("health").AddEvent<Damage>("damage");
        var health = builder.Component<Health>();
        var damage = builder.Resource<Damage>();
        ChunkSystemAction Emit(int source) => (in SystemContext c, in ChunkView chunk) =>
        {
            var entities = chunk.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                c.Emit(damage, new Damage { Target = entities[i], Source = source, Row = i });
            }
        };

        var schema = builder
            .AddSystem(new SystemDefinition { Name = "Fire", Reads = ComponentMask.Of(health), WritesResources = ResourceMask.Of(damage), ForEachChunk = Emit(1) })
            .AddSystem(new SystemDefinition { Name = "Frost", Reads = ComponentMask.Of(health), WritesResources = ResourceMask.Of(damage), ForEachChunk = Emit(2) })
            .AddSystem(new SystemDefinition
            {
                Name = "Apply",
                Phase = Phase.Late,
                ReadsResources = ResourceMask.Of(damage),
                Begin = (in SystemContext c) =>
                {
                    seen.Clear();
                    seen.AddRange(c.Events<Damage>(damage));
                },
            })
            .Build();
        return (schema, seen);
    }

    private static World Populate(WorldSchema schema, int workers, int count = Entities)
    {
        var world = schema.CreateWorld(new WorldOptions { WorkerThreads = workers, InitialEntityCapacity = count });
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < count; i++)
        {
            buffer.Create(ComponentMask.Of(schema.Component<Health>()));
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Events_from_parallel_chunk_jobs_arrive_in_system_then_job_then_record_order(int workers)
    {
        var (schema, seen) = EmitterSchema();
        using var world = Populate(schema, workers);
        Assert.Equal(0, schema.System("Fire").Wave);
        Assert.Equal(0, schema.System("Frost").Wave); // event writers never conflict

        world.Tick();

        Assert.Equal(2 * Entities, seen.Count);
        var expected = new List<Damage>();
        foreach (int source in new[] { 1, 2 })
        {
            foreach (var chunk in world.Query(schema.Component<Health>()))
            {
                for (int i = 0; i < chunk.Count; i++)
                {
                    expected.Add(new Damage { Target = chunk.Entities[i], Source = source, Row = i });
                }
            }
        }

        Assert.Equal(expected, seen);
    }

    [Fact]
    public void The_merged_queue_is_identical_on_1_and_4_threads()
    {
        var (schemaOne, seenOne) = EmitterSchema();
        var (schemaFour, seenFour) = EmitterSchema();
        using var one = Populate(schemaOne, 1);
        using var four = Populate(schemaFour, 4);

        one.Tick();
        four.Tick();

        Assert.Equal(seenOne, seenFour);
        Assert.Equal(one.Hash(), four.Hash());
    }

    [Fact]
    public void Events_are_visible_from_the_next_phase_until_the_end_boundary_and_end_phase_events_survive_into_the_next_tick()
    {
        var builder = new WorldBuilder().AddEvent<Bell>("bell");
        var bell = builder.Resource<Bell>();
        var counts = new List<(string, int)>();
        SystemAction Count(string where) => (in SystemContext c) => counts.Add((where, c.Events<Bell>(bell).Length));
        var schema = builder
            .AddSystem(new SystemDefinition { Name = "Ring", WritesResources = ResourceMask.Of(bell), Begin = (in SystemContext c) => c.Emit(bell, new Bell { Tick = c.Tick }) })
            .AddSystem(new SystemDefinition { Name = "SameEarly", ReadsResources = ResourceMask.Of(bell), Before = ["Ring"], Begin = Count("same-early") })
            .AddSystem(new SystemDefinition { Name = "SameLate", ReadsResources = ResourceMask.Of(bell), After = ["Ring"], Begin = Count("same-late") })
            .AddSystem(new SystemDefinition { Name = "Late", Phase = Phase.Late, ReadsResources = ResourceMask.Of(bell), Begin = Count("late") })
            .AddSystem(new SystemDefinition { Name = "End", Phase = Phase.End, ReadsResources = ResourceMask.Of(bell), Begin = Count("end") })
            .AddSystem(new SystemDefinition
            {
                Name = "Toll",
                Phase = Phase.End,
                WritesResources = ResourceMask.Of(bell),
                Begin = (in SystemContext c) =>
                {
                    c.Emit(bell, new Bell { Tick = -1 });
                    c.Emit(bell, new Bell { Tick = -2 });
                },
            })
            .Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });

        world.Tick();
        Assert.Equal([("same-early", 0), ("same-late", 0), ("late", 1), ("end", 1)], counts);
        var pending = world.Events<Bell>(bell);
        Assert.Equal(2, pending.Length);
        Assert.Equal(-1, pending[0].Tick);
        Assert.Equal(-2, pending[1].Tick);

        counts.Clear();
        world.Tick();
        // The End-Phase events of Tick 0 are delivered through Tick 1 and cleared at its End boundary.
        Assert.Equal([("same-early", 2), ("same-late", 2), ("late", 3), ("end", 3)], counts);
        Assert.Equal(2, world.Events<Bell>(bell).Length);
        Assert.Equal(1, world.Events<Bell>(bell)[1].Tick == -2 ? 1 : 0);
    }

    [Fact]
    public void A_persistent_queue_is_hashed_and_a_transient_one_is_not()
    {
        static ulong HashAfterOneTick(EventOptions options, bool emit)
        {
            var builder = new WorldBuilder().AddEvent<Bell>("bell", options);
            var bell = builder.Resource<Bell>();
            if (emit)
            {
                builder.AddSystem(new SystemDefinition { Name = "Toll", Phase = Phase.End, WritesResources = ResourceMask.Of(bell), Begin = (in SystemContext c) => c.Emit(bell, new Bell { Tick = 9 }) });
            }

            using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
            world.Tick();
            return world.Hash();
        }

        Assert.Equal(HashAfterOneTick(EventOptions.None, emit: true), HashAfterOneTick(EventOptions.None, emit: false));
        Assert.NotEqual(HashAfterOneTick(EventOptions.Persistent, emit: true), HashAfterOneTick(EventOptions.Persistent, emit: false));
        Assert.True(new WorldBuilder().AddEvent<Bell>("bell", EventOptions.Persistent).Build().Resources[0].IsPersistent);
        Assert.False(new WorldBuilder().AddEvent<Bell>("bell").Build().Resources[0].IsPersistent);
    }

    [Fact]
    public void Host_code_emits_through_a_command_buffer_and_undeclared_access_faults_the_system()
    {
        var builder = new WorldBuilder().AddEvent<Bell>("bell");
        var bell = builder.Resource<Bell>();
        var schema = builder.Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var buffer = world.CreateCommandBuffer();
        buffer.Emit(bell, new Bell { Tick = 5 });
        buffer.Emit(bell, new Bell { Tick = 6 });
        Assert.Equal(0, world.Events<Bell>(bell).Length);

        world.Playback(buffer);

        Assert.Equal(2, world.Events<Bell>(bell).Length);
        Assert.Equal(5, world.Events<Bell>(bell)[0].Tick);
        Assert.Throws<ArgumentException>(() => world.Events<Damage>(bell));
        Assert.Throws<ArgumentException>(() => world.Resource<Bell>(bell));

        var sneaky = new WorldBuilder().AddEvent<Bell>("bell");
        var b2 = sneaky.Resource<Bell>();
        using var reader = sneaky.AddSystem(new SystemDefinition { Name = "Sneaky", Begin = (in SystemContext c) => c.Emit(b2, new Bell()) }).Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var fault = Assert.Throws<SystemFaultException>(() => reader.Tick());
        Assert.Contains("WritesResources", fault.InnerException!.Message, StringComparison.Ordinal);
    }
}
