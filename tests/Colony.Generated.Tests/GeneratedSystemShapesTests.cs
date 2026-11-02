using Colony.Numerics;

namespace Colony.Generated.Tests.Shapes;

[Component("shapes.position")]
public partial struct Position
{
    public int X;
    public int Y;
}

[Component]
public partial struct Velocity
{
    public int Dx;
    public int Dy;
}

[Component]
public partial struct Visits
{
    public int Count;
    public long LastEntity;
}

[Component]
public partial struct Frozen
{
}

[Component(NonFragmenting = true)]
public partial struct Marked
{
    public int Stamp;
}

/// <summary>Every System shape the generator supports, with ordering constraints between the conflicting writers.</summary>
public static partial class ShapeSystems
{
    /// <summary>Per Entity with EntityId and context: records the Entity id and the Tick.</summary>
    [System(NamedPhase.Begin)]
    public static void Observe(in SystemContext context, EntityId entity, ref Visits visits)
    {
        visits.Count++;
        visits.LastEntity = (long)entity.Value + context.Tick;
    }

    /// <summary>Per Entity with a read and a write; skips frozen Entities through the query filter.</summary>
    [System(NamedPhase.Simulate)]
    [Without(typeof(Frozen))]
    public static void Move(in Velocity velocity, ref Position position)
    {
        position.X += velocity.Dx;
        position.Y += velocity.Dy;
    }

    /// <summary>Chunk level with the typed query: doubles the velocity of Entities past x = 8, after Move.</summary>
    [System(NamedPhase.Simulate, After = ["Move"])]
    [Reads(typeof(Position))]
    [Writes(typeof(Velocity))]
    public static void Accelerate(in AccelerateQuery query)
    {
        var positions = query.Position;
        var velocities = query.Velocity;
        for (int row = 0; row < query.Count; row++)
        {
            if (positions[row].X > 8)
            {
                velocities[row].Dx = 2;
                query.VelocityAt(row).Dy = 2;
            }
        }
    }

    /// <summary>Chunk level with the raw view, every 2 Ticks: clamps Y, after Accelerate.</summary>
    [System(NamedPhase.Simulate, Every = 2, Offset = 0, After = ["Accelerate"])]
    [Writes(typeof(Position))]
    public static void Clamp(in SystemContext context, in ChunkView chunk)
    {
        var type = context.World.Schema.Component<Position>();
        foreach (ref var position in chunk.Column<Position>(type))
        {
            position.Y = Math.Min(position.Y, 5);
        }
    }

    /// <summary>Serial: a Begin hook that counts Ticks in a static slot.</summary>
    [System(NamedPhase.Late)]
    [Reads(typeof(Visits))]
    public static void Census(in SystemContext context)
    {
        CensusTicks = context.Tick + 1;
    }

    public static long CensusTicks { get; private set; }
}

/// <summary>Ticket AC #1 and #2: generated Systems of every shape run under the scheduler with the same hash on 1, 2 and 4 workers.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class GeneratedSystemShapesTests
{
    private static WorldSchema BuildSchema()
    {
        var builder = new WorldBuilder()
            .AddComponent<Position>()
            .AddComponent<Velocity>()
            .AddComponent<Visits>()
            .AddComponent<Frozen>()
            .AddComponent<Marked>()
            .DebugValidation(true);
        return ShapeSystems.AddSystems(builder).Build();
    }

    private static World CreateWorld(WorldSchema schema, int workers, int entities)
    {
        var world = schema.CreateWorld(new WorldOptions { Seed = 7, WorkerThreads = workers });
        var buffer = world.CreateCommandBuffer();
        var position = schema.Component<Position>();
        var velocity = schema.Component<Velocity>();
        var frozen = schema.Component<Frozen>();
        var mover = ComponentMask.Of(position, velocity, schema.Component<Visits>());
        for (int i = 0; i < entities; i++)
        {
            var e = buffer.Create(mover);
            buffer.Add(e, velocity, new Velocity { Dx = 1, Dy = 1 });
            if (i % 10 == 0)
            {
                buffer.Add(e, frozen);
            }
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }

    [Fact]
    public void Every_system_shape_registers_with_its_declared_access_set_and_order()
    {
        var schema = BuildSchema();

        Assert.Equal("shapes.position", schema.Describe(schema.Component<Position>()).Name);
        Assert.True(schema.Describe(schema.Component<Marked>()).IsNonFragmenting);
        Assert.False(schema.Describe(schema.Component<Frozen>()).IsNonFragmenting);
        Assert.Equal(2, schema.Describe(schema.Component<Visits>()).Fields.Length);
        Assert.Equal(8, schema.Describe(schema.Component<Visits>()).Fields[1].Offset);

        var move = schema.System("Move");
        var accelerate = schema.System("Accelerate");
        var clamp = schema.System("Clamp");
        Assert.True(move.Reads.Contains(schema.Component<Velocity>()));
        Assert.True(move.Writes.Contains(schema.Component<Position>()));
        Assert.True(accelerate.Writes.Contains(schema.Component<Velocity>()));
        Assert.True(move.Order < accelerate.Order && accelerate.Order < clamp.Order);
        Assert.Equal(2, clamp.Every);
        Assert.Equal(Phase.Begin, schema.System("Observe").Phase);
        Assert.Equal(Phase.Late, schema.System("Census").Phase);
    }

    [Fact]
    public void Generated_systems_run_under_the_scheduler_and_produce_the_expected_state()
    {
        var schema = BuildSchema();
        using var world = CreateWorld(schema, workers: 2, entities: 50);
        for (int tick = 0; tick < 12; tick++)
        {
            Assert.True(world.Tick());
        }

        var position = schema.Component<Position>();
        var velocity = schema.Component<Velocity>();
        var visits = schema.Component<Visits>();
        var frozen = schema.Component<Frozen>();
        int moving = 0;
        int still = 0;
        foreach (var chunk in world.Query(position))
        {
            var positions = chunk.Column<Position>(position);
            var velocities = chunk.Column<Velocity>(velocity);
            var counts = chunk.Column<Visits>(visits);
            var entities = chunk.Entities;
            for (int row = 0; row < chunk.Count; row++)
            {
                Assert.Equal(12, counts[row].Count);
                Assert.Equal((long)entities[row].Value + 11, counts[row].LastEntity);
                if (world.Has(entities[row], frozen))
                {
                    Assert.Equal(new Position(), positions[row]);
                    Assert.Equal(1, velocities[row].Dx);
                    still++;
                }
                else
                {
                    // Move adds 1/Tick until X > 8 at Tick 8, when Accelerate sets 2/Tick; Clamp caps Y at 5 on even Ticks.
                    Assert.Equal(new Position { X = 15, Y = 7 }, positions[row]);
                    Assert.Equal(new Velocity { Dx = 2, Dy = 2 }, velocities[row]);
                    moving++;
                }
            }
        }

        Assert.Equal(45, moving);
        Assert.Equal(5, still);
        Assert.Equal(12, ShapeSystems.CensusTicks);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void The_world_hash_is_identical_across_worker_counts(int workers)
    {
        var schema = BuildSchema();
        ulong Run(int n)
        {
            using var world = CreateWorld(schema, n, entities: 5_000);
            for (int tick = 0; tick < 30; tick++)
            {
                world.Tick();
            }

            return world.Hash();
        }

        Assert.Equal(Run(1), Run(workers));
    }

    [Fact]
    public void Generated_per_entity_systems_allocate_nothing_in_steady_state()
    {
        var schema = BuildSchema();
        using var world = CreateWorld(schema, workers: 1, entities: 2_000);
        for (int tick = 0; tick < 4; tick++)
        {
            world.Tick();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = 0; tick < 10; tick++)
        {
            world.Tick();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Typed_query_exposes_chunk_spans_and_per_entity_refs()
    {
        var schema = BuildSchema();
        using var world = CreateWorld(schema, workers: 1, entities: 20);
        var position = schema.Component<Position>();
        var velocity = schema.Component<Velocity>();
        EntityId first = default;

        foreach (var chunk in world.Query(position, velocity))
        {
            first = chunk.Entities[0];
            var query = new ShapeSystems.AccelerateQuery(in chunk, schema);
            Assert.Equal(chunk.Count, query.Count);
            Assert.Equal(chunk.Count, query.Position.Length);
            Assert.Equal(chunk.Entities.Length, query.Entities.Length);
            query.VelocityAt(0).Dx = 9;
            Assert.Equal(9, query.Velocity[0].Dx);
            Assert.Equal(0, query.PositionAt(0).X);
        }

        Assert.Equal(9, world.Get<Velocity>(first, velocity).Dx);
    }
}
