namespace Colony.Core.Tests.Relations;

/// <summary>AC #1: Relations are declared on the builder, reverse lookup is deterministic, and removing an Entity updates the index.</summary>
public sealed class RelationTests
{
    private static WorldSchema Schema(out RelationType storedIn)
    {
        var builder = new WorldBuilder().AddComponent<Position>("position").DebugValidation(false);
        storedIn = builder.AddRelation("stored-in");
        return builder.Build();
    }

    private static EntityId[] Spawn(World world, int count)
    {
        var buffer = world.CreateCommandBuffer();
        var deferred = new EntityId[count];
        for (int i = 0; i < count; i++)
        {
            deferred[i] = buffer.Create(ComponentMask.Of(world.Schema.Component<Position>()));
        }

        world.Playback(buffer);
        var ids = new EntityId[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = buffer.Resolve(deferred[i]);
        }

        world.ResetScratch();
        return ids;
    }

    private static EntityId[] Sources(World world, EntityId target, RelationType relation)
    {
        var result = new List<EntityId>();
        foreach (var source in world.SourcesOf(target, relation))
        {
            result.Add(source);
        }

        return [.. result];
    }

    [Fact]
    public void A_relation_is_a_named_schema_entry_backed_by_a_non_fragmenting_component()
    {
        var schema = Schema(out var storedIn);

        Assert.Equal("stored-in", schema.Relation("stored-in").Name);
        Assert.Equal(storedIn, schema.Relation("stored-in").Type);
        Assert.Single(schema.Relations.ToArray());
        Assert.True(schema.Describe(storedIn.Component).IsNonFragmenting);
        Assert.True(schema.Describe(storedIn.Component).IsRelation);
        Assert.Equal("stored-in", schema.Describe(storedIn.Component).Name);
        Assert.False(schema.TryGetRelation("missing", out _));
    }

    [Fact]
    public void Relating_sets_the_target_and_reverse_lookup_lists_sources_ascending_regardless_of_insertion_order()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var ids = Spawn(world, 6);
        var crate = ids[0];
        var buffer = world.CreateCommandBuffer();
        buffer.Relate(ids[4], storedIn, crate);
        buffer.Relate(ids[1], storedIn, crate);
        buffer.Relate(ids[5], storedIn, crate);
        buffer.Relate(ids[2], storedIn, crate);
        world.Playback(buffer);

        Assert.Equal(crate, world.TargetOf(ids[4], storedIn));
        Assert.Equal(EntityId.Null, world.TargetOf(ids[3], storedIn));
        Assert.Equal(EntityId.Null, world.TargetOf(crate, storedIn));
        Assert.Equal(4, world.SourcesOf(crate, storedIn).Count);
        Assert.Equal(new[] { ids[1], ids[2], ids[4], ids[5] }, Sources(world, crate, storedIn));
        Assert.Empty(Sources(world, ids[3], storedIn));
        Assert.True(world.Has(ids[4], storedIn.Component));
    }

    [Fact]
    public void Relating_again_moves_the_source_and_unrelating_drops_it()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var ids = Spawn(world, 4);
        var buffer = world.CreateCommandBuffer();
        buffer.Relate(ids[2], storedIn, ids[0]);
        buffer.Relate(ids[3], storedIn, ids[0]);
        world.Playback(buffer);

        buffer.Relate(ids[2], storedIn, ids[1]);
        world.Playback(buffer);
        Assert.Equal(new[] { ids[3] }, Sources(world, ids[0], storedIn));
        Assert.Equal(new[] { ids[2] }, Sources(world, ids[1], storedIn));

        buffer.Unrelate(ids[3], storedIn);
        world.Playback(buffer);
        Assert.Empty(Sources(world, ids[0], storedIn));
        Assert.Equal(EntityId.Null, world.TargetOf(ids[3], storedIn));
        Assert.False(world.Has(ids[3], storedIn.Component));
    }

    [Fact]
    public void Destroying_a_source_or_a_target_updates_the_index()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var ids = Spawn(world, 4);
        var buffer = world.CreateCommandBuffer();
        buffer.Relate(ids[1], storedIn, ids[0]);
        buffer.Relate(ids[2], storedIn, ids[0]);
        buffer.Relate(ids[3], storedIn, ids[0]);
        world.Playback(buffer);

        buffer.Destroy(ids[2]);
        world.Playback(buffer);
        Assert.Equal(new[] { ids[1], ids[3] }, Sources(world, ids[0], storedIn));

        // Without cascade delete the sources survive and simply lose the link.
        buffer.Destroy(ids[0]);
        world.Playback(buffer);
        Assert.True(world.IsAlive(ids[1]));
        Assert.True(world.IsAlive(ids[3]));
        Assert.Equal(EntityId.Null, world.TargetOf(ids[1], storedIn));
        Assert.False(world.Has(ids[1], storedIn.Component));
        Assert.Equal(0, world.SourcesOf(ids[0], storedIn).Count);
    }

    [Fact]
    public void A_relation_to_a_dead_target_is_skipped_and_reported_as_a_structural_failure()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var ids = Spawn(world, 2);
        var buffer = world.CreateCommandBuffer();
        buffer.Destroy(ids[0]);
        world.Playback(buffer);

        buffer.Relate(ids[1], storedIn, ids[0]);
        world.Playback(buffer);

        Assert.Equal(EntityId.Null, world.TargetOf(ids[1], storedIn));
        Assert.False(world.Has(ids[1], storedIn.Component));
        Assert.Equal(1, world.Counters.StaleSkipped);
        var failure = Assert.Single(world.Events<StructuralFailure>(schema.StructuralFailures).ToArray());
        Assert.Equal(ids[0], failure.Entity);
        Assert.Equal(StructuralOp.Relate, failure.Op);
        Assert.Equal(storedIn.Component, failure.Component);
        Assert.Equal(StructuralFailureReason.Stale, failure.Reason);
    }

    [Fact]
    public void Deferred_handles_work_as_source_and_target()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var crate = buffer.Create();
        var item = buffer.Create();
        buffer.Relate(item, storedIn, crate);
        world.Playback(buffer);

        var crateId = buffer.Resolve(crate);
        var itemId = buffer.Resolve(item);
        Assert.Equal(crateId, world.TargetOf(itemId, storedIn));
        Assert.Equal(new[] { itemId }, Sources(world, crateId, storedIn));
    }

    [Fact]
    public void Slot_reuse_never_resurrects_links()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var ids = Spawn(world, 3);
        var buffer = world.CreateCommandBuffer();
        buffer.Relate(ids[1], storedIn, ids[0]);
        buffer.Relate(ids[2], storedIn, ids[0]);
        world.Playback(buffer);

        buffer.Destroy(ids[0]);
        world.Playback(buffer);
        var reused = Spawn(world, 1)[0];
        Assert.Equal(ids[0].Index, reused.Index);

        Assert.Equal(0, world.SourcesOf(reused, storedIn).Count);
        Assert.Equal(0, world.SourcesOf(ids[0], storedIn).Count);
        Assert.Equal(EntityId.Null, world.TargetOf(ids[1], storedIn));
    }

    [Fact]
    public void Rebuilding_the_index_from_the_components_reproduces_the_incremental_index()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var ids = Spawn(world, 64);
        var buffer = world.CreateCommandBuffer();
        for (int i = 1; i < ids.Length; i++)
        {
            buffer.Relate(ids[i], storedIn, ids[i % 5]);
        }

        world.Playback(buffer);
        var before = new EntityId[5][];
        for (int t = 0; t < 5; t++)
        {
            before[t] = Sources(world, ids[t], storedIn);
        }

        world.RebuildRelationIndex();

        for (int t = 0; t < 5; t++)
        {
            Assert.Equal(before[t], Sources(world, ids[t], storedIn));
        }
    }

    [Fact]
    public void Lookups_outside_the_schema_or_on_dead_entities_are_rejected_or_empty()
    {
        var schema = Schema(out var storedIn);
        using var world = schema.CreateWorld();
        var ids = Spawn(world, 2);
        var buffer = world.CreateCommandBuffer();
        buffer.Relate(ids[1], storedIn, ids[0]);
        buffer.Destroy(ids[1]);
        world.Playback(buffer);

        Assert.Equal(EntityId.Null, world.TargetOf(ids[1], storedIn));
        Assert.Equal(0, world.SourcesOf(ids[0], storedIn).Count);
        var other = new WorldBuilder();
        other.AddRelation("a");
        var foreign = other.AddRelation("b");
        Assert.Throws<ArgumentException>(() => world.TargetOf(ids[0], foreign));
        Assert.Throws<ArgumentException>(() => world.SourcesOf(ids[0], foreign));
    }
}
