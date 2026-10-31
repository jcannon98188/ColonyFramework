namespace Colony.Core.Tests.Relations;

/// <summary>
/// AC #5: the relation index is rebuildable state and stays out of the World hash; the Relations themselves
/// (the forward links), the string table and the blob store are saved data and are hashed.
/// </summary>
public sealed class RelationHashCoverageTests
{
    private static World NewWorld(out RelationType storedIn)
    {
        var builder = new WorldBuilder().AddComponent<Position>("position").DebugValidation(false);
        storedIn = builder.AddRelation("stored-in");
        return builder.Build().CreateWorld();
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

        return ids;
    }

    [Fact]
    public void Relating_changes_the_hash_but_the_reverse_index_does_not_contribute()
    {
        using var world = NewWorld(out var storedIn);
        var ids = Spawn(world, 4);
        ulong empty = world.Hash();

        var buffer = world.CreateCommandBuffer();
        buffer.Relate(ids[1], storedIn, ids[0]);
        buffer.Relate(ids[2], storedIn, ids[0]);
        world.Playback(buffer);
        ulong related = world.Hash();
        Assert.NotEqual(empty, related);
        Assert.Equal(2, world.SourcesOf(ids[0], storedIn).Count);

        // Corrupt the rebuildable index through the test seam: the saved data (forward links) is untouched,
        // so the hash must not move, while the reverse lookup is visibly broken until it is rebuilt.
        world.RelationIndexForTests.Clear();
        Assert.Equal(0, world.SourcesOf(ids[0], storedIn).Count);
        Assert.Equal(ids[0], world.TargetOf(ids[1], storedIn));
        Assert.Equal(related, world.Hash());

        world.RebuildRelationIndex();
        Assert.Equal(2, world.SourcesOf(ids[0], storedIn).Count);
        Assert.Equal(related, world.Hash());
    }

    [Fact]
    public void Two_worlds_related_in_different_orders_hash_alike_because_only_the_links_count()
    {
        using var a = NewWorld(out var storedIn);
        using var b = NewWorld(out _);
        var idsA = Spawn(a, 5);
        var idsB = Spawn(b, 5);

        var bufferA = a.CreateCommandBuffer();
        var bufferB = b.CreateCommandBuffer();
        for (int i = 1; i < 5; i++)
        {
            bufferA.Relate(idsA[i], storedIn, idsA[0]);
            bufferB.Relate(idsB[5 - i], storedIn, idsB[0]);
        }

        a.Playback(bufferA);
        b.Playback(bufferB);
        Assert.Equal(a.Hash(), b.Hash());
    }

    [Fact]
    public void String_table_and_blob_store_are_saved_state_that_the_hash_covers()
    {
        using var world = NewWorld(out _);
        ulong h0 = world.Hash();
        world.Strings.Intern("wood");
        ulong h1 = world.Hash();
        world.Blobs.Store<int>([1, 2, 3]);
        ulong h2 = world.Hash();

        Assert.NotEqual(h0, h1);
        Assert.NotEqual(h1, h2);
        Assert.Equal(h2, world.Hash());
    }
}
