namespace Colony.Core.Tests.Scheduling;

/// <summary>
/// TASK-8 AC #6 (spec section 4.1): conflicting structural ops on one Entity in a Phase resolve
/// first-in-stable-order; every loser produces a deterministic failure event, delivered the next Phase,
/// naming the Entity and the op.
/// </summary>
public sealed class StructuralFailureTests
{
    private const int Entities = 20_000;

    private static (WorldSchema Schema, List<StructuralFailure> Seen, List<StructuralFailure> SeenInSimulate) ConflictSchema()
    {
        var seen = new List<StructuralFailure>();
        var seenInSimulate = new List<StructuralFailure>();
        var builder = new WorldBuilder().AddComponent<Health>("health").AddComponent<Marked>("marked", ComponentOptions.NonFragmenting);
        var health = builder.Component<Health>();
        var marked = builder.Component<Marked>();
        var failures = builder.StructuralFailures;
        var schema = builder
            .AddSystem(new SystemDefinition
            {
                Name = "Reaper",
                Reads = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext c, in ChunkView chunk) =>
                {
                    var entities = chunk.Entities;
                    for (int i = 0; i < entities.Length; i += 3)
                    {
                        c.Commands.Destroy(entities[i]);
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Healer",
                Reads = ComponentMask.Of(health),
                ForEachChunk = (in SystemContext c, in ChunkView chunk) =>
                {
                    var entities = chunk.Entities;
                    for (int i = 0; i < entities.Length; i += 3)
                    {
                        c.Commands.Destroy(entities[i]); // loses to the Reaper (System 0 merges first)
                        c.Commands.Add(entities[i], marked, new Marked { Stamp = 1 }); // loses too
                        c.Commands.Remove(entities[i], health); // and this one
                    }
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "SameEarly",
                ReadsResources = ResourceMask.Of(failures),
                After = ["Reaper", "Healer"],
                Begin = (in SystemContext c) =>
                {
                    seenInSimulate.Clear();
                    seenInSimulate.AddRange(c.Events<StructuralFailure>(failures));
                },
            })
            .AddSystem(new SystemDefinition
            {
                Name = "Watch",
                Phase = Phase.Late,
                ReadsResources = ResourceMask.Of(failures),
                Begin = (in SystemContext c) =>
                {
                    seen.Clear();
                    seen.AddRange(c.Events<StructuralFailure>(failures));
                },
            })
            .Build();
        return (schema, seen, seenInSimulate);
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
    public void The_loser_of_a_same_phase_conflict_gets_a_failure_event_the_next_phase_naming_entity_op_and_system(int workers)
    {
        var (schema, seen, seenInSimulate) = ConflictSchema();
        using var world = Populate(schema, workers);
        Assert.Equal("colony.structural-failure", schema.DescribeResource(schema.StructuralFailures).Name);
        var targets = new List<EntityId>();
        foreach (var chunk in world.Query(schema.Component<Health>()))
        {
            for (int i = 0; i < chunk.Count; i += 3)
            {
                targets.Add(chunk.Entities[i]);
            }
        }

        world.Tick();

        Assert.Empty(seenInSimulate); // delivered the next Phase, not within the Phase
        Assert.Equal(targets.Count * 3, seen.Count);
        Assert.Equal(targets.Count, world.Counters.Destroys);
        Assert.Equal(targets.Count * 3, world.Counters.StaleSkipped);
        int healer = schema.System("Healer").Id;
        for (int k = 0; k < targets.Count; k++)
        {
            Assert.Equal(new StructuralFailure(targets[k], StructuralOp.Destroy, default, healer, StructuralFailureReason.Conflict), seen[(k * 3) + 0]);
            Assert.Equal(new StructuralFailure(targets[k], StructuralOp.Add, schema.Component<Marked>(), healer, StructuralFailureReason.Conflict), seen[(k * 3) + 1]);
            Assert.Equal(new StructuralFailure(targets[k], StructuralOp.Remove, schema.Component<Health>(), healer, StructuralFailureReason.Conflict), seen[(k * 3) + 2]);
        }

        Assert.Equal(0, world.Events<StructuralFailure>(schema.StructuralFailures).Length); // cleared at the End boundary
    }

    [Fact]
    public void Failure_events_are_identical_on_1_and_4_threads()
    {
        var (schemaOne, seenOne, _) = ConflictSchema();
        var (schemaFour, seenFour, _) = ConflictSchema();
        using var one = Populate(schemaOne, 1);
        using var four = Populate(schemaFour, 4);

        one.Tick();
        four.Tick();

        Assert.Equal(seenOne, seenFour);
        Assert.Equal(one.Hash(), four.Hash());
    }

    [Fact]
    public void An_op_on_an_entity_that_died_in_an_earlier_phase_is_reported_as_stale_and_host_playback_reports_system_minus_one()
    {
        var schema = new WorldBuilder().AddComponent<Health>("health").Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var health = schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create(ComponentMask.Of(health));
        world.Playback(buffer);
        var e = buffer.Resolve(deferred);
        buffer.Destroy(e);
        world.Playback(buffer);
        world.ResetScratch();
        Assert.Equal(0, world.Events<StructuralFailure>(schema.StructuralFailures).Length);

        buffer.Destroy(e);
        buffer.Add(e, health, new Health { Value = 1 });
        world.Playback(buffer);

        var failures = world.Events<StructuralFailure>(schema.StructuralFailures);
        Assert.Equal(2, failures.Length);
        Assert.Equal(new StructuralFailure(e, StructuralOp.Destroy, default, -1, StructuralFailureReason.Stale), failures[0]);
        Assert.Equal(new StructuralFailure(e, StructuralOp.Add, health, -1, StructuralFailureReason.Stale), failures[1]);
        Assert.Equal(2, world.Counters.StaleSkipped);
    }
}
