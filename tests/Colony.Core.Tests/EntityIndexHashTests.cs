namespace Colony.Core.Tests;

/// <summary>
/// Spec section 6: the World hash covers simulated state. Entity generations and the free list decide every
/// future EntityId (they are saved exactly), so two Worlds whose live data match but whose next Create would
/// return different ids must hash differently, and the hash tree must name where.
/// </summary>
public sealed class EntityIndexHashTests
{
    [Fact]
    public void Worlds_that_freed_slots_in_another_order_hash_differently_and_the_tree_names_the_entity_index()
    {
        using var a = Empty();
        using var b = Empty();
        CreateTwoAndDestroy(a, firstDestroyed: 0);
        CreateTwoAndDestroy(b, firstDestroyed: 1);

        Assert.NotEqual(a.Hash(), b.Hash());
        var comparison = WorldHashTree.Compare(a.HashTree(), b.HashTree());
        Assert.False(comparison.Identical);
        var difference = Assert.Single(comparison.Differences);
        Assert.Equal((WorldHashTree.ComponentsGroup, WorldHashTree.EntityIndexItem), (difference.Group, difference.Item));

        // Why it matters: the next Create returns different ids.
        Assert.NotEqual(NextId(a), NextId(b));
    }

    [Fact]
    public void Worlds_whose_slots_were_reused_a_different_number_of_times_hash_differently()
    {
        using var a = Empty();
        using var b = Empty();
        CreateAndDestroy(a, times: 1);
        CreateAndDestroy(b, times: 2);

        Assert.NotEqual(a.Hash(), b.Hash());
        Assert.Equal(a.Hash(), a.HashTree().WorldHash);
        Assert.NotEqual(NextId(a), NextId(b));
    }

    [Fact]
    public void Identical_histories_still_hash_alike()
    {
        using var a = Empty();
        using var b = Empty();
        CreateTwoAndDestroy(a, firstDestroyed: 1);
        CreateTwoAndDestroy(b, firstDestroyed: 1);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.True(WorldHashTree.Compare(a.HashTree(), b.HashTree()).Identical);
    }

    private static World Empty()
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        return builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
    }

    private static void CreateTwoAndDestroy(World world, int firstDestroyed)
    {
        var buffer = world.CreateCommandBuffer();
        buffer.Create();
        buffer.Create();
        world.Playback(buffer);
        var ids = LiveIds(world);
        Assert.Equal(2, ids.Count);
        buffer = world.CreateCommandBuffer();
        buffer.Destroy(ids[firstDestroyed]);
        buffer.Destroy(ids[1 - firstDestroyed]);
        world.Playback(buffer);
    }

    private static void CreateAndDestroy(World world, int times)
    {
        for (int i = 0; i < times; i++)
        {
            var buffer = world.CreateCommandBuffer();
            buffer.Create();
            world.Playback(buffer);
            buffer = world.CreateCommandBuffer();
            buffer.Destroy(Assert.Single(LiveIds(world)));
            world.Playback(buffer);
        }
    }

    private static EntityId NextId(World world)
    {
        var buffer = world.CreateCommandBuffer();
        buffer.Create();
        world.Playback(buffer);
        return Assert.Single(LiveIds(world));
    }

    private static List<EntityId> LiveIds(World world)
    {
        var ids = new List<EntityId>();
        for (int index = 0; index < 4; index++)
        {
            for (uint generation = 0; generation < 8; generation++)
            {
                if (world.IsAlive(new EntityId(index, generation)))
                {
                    ids.Add(new EntityId(index, generation));
                }
            }
        }

        return ids;
    }
}
