using Colony.Persistence;

namespace Colony.Persistence.Tests;

/// <summary>TASK-14 AC #1 over the stores a scenario World alone does not stress: strings, blobs, buffers with heap overflow, Relations, hierarchy, enable bits, free list.</summary>
public sealed class TablesRoundTripTests
{
    [Fact]
    public void Strings_blobs_buffers_relations_and_hierarchy_survive_a_round_trip()
    {
        var tables = TablesWorld.Compose();
        using var world = tables.Create();
        byte[] bytes = world.SaveToBytes();

        var loadedTables = TablesWorld.Compose();
        using var loaded = WorldPersistence.Load(bytes, loadedTables.Schema);

        Assert.Equal(world.Hash(), loaded.Hash());
        var ids = tables.Labelled(world);
        var loadedIds = loadedTables.Labelled(loaded);
        Assert.Equal(ids, loadedIds);

        for (int i = 0; i < ids.Count; i++)
        {
            var e = ids[i];
            var a = world.Get<Label>(e, tables.Label);
            var b = loaded.Get<Label>(e, loadedTables.Label);
            Assert.Equal(a, b);
            Assert.Equal(world.Strings.Get(a.Name).ToString(), loaded.Strings.Get(b.Name).ToString());
            if (!a.Data.IsNull)
            {
                Assert.Equal(world.Blobs.Get(a.Data).ToArray(), loaded.Blobs.Get(b.Data).ToArray());
            }

            Assert.Equal(world.IsEnabled(e, tables.Weight), loaded.IsEnabled(e, loadedTables.Weight));
            Assert.Equal(world.Has(e, tables.Score), loaded.Has(e, loadedTables.Score));
            if (world.Has(e, tables.Score))
            {
                Assert.Equal(world.Get<Score>(e, tables.Score).Value, loaded.Get<Score>(e, loadedTables.Score).Value);
            }

            if (world.Has(e, tables.Items))
            {
                Assert.Equal(world.GetBuffer<int>(e, tables.Items).AsSpan().ToArray(), loaded.GetBuffer<int>(e, loadedTables.Items).AsSpan().ToArray());
            }

            Assert.Equal(world.TargetOf(e, tables.Likes), loaded.TargetOf(e, loadedTables.Likes));
            Assert.Equal(world.ParentOf(e), loaded.ParentOf(e));
            Assert.Equal(world.ChildrenOf(e).Count, loaded.ChildrenOf(e).Count);
        }

        Assert.Contains(ids, e => world.ChildrenOf(e).Count > 0);
        Assert.Contains(ids, e => world.GetBuffer<int>(e, tables.Items).Length > 4 && world.Has(e, tables.Items));
    }

    [Fact]
    public void A_loaded_world_hands_out_the_same_entity_ids_as_the_saved_one()
    {
        var tables = TablesWorld.Compose();
        using var world = tables.Create();
        byte[] bytes = world.SaveToBytes();
        using var loaded = WorldPersistence.Load(bytes, TablesWorld.Compose().Schema);

        var expected = CreateThree(world, tables.Weight);
        var actual = CreateThree(loaded, tables.Weight);

        Assert.Equal(expected, actual);
        Assert.Equal(world.Hash(), loaded.Hash());
    }

    private static List<EntityId> CreateThree(World world, ComponentType weight)
    {
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < 3; i++)
        {
            buffer.Create(ComponentMask.Of(weight));
        }

        world.Playback(buffer);
        var ids = new List<EntityId>();
        foreach (var chunk in world.Query(ComponentMask.Of(weight), ignoreEnableBits: true))
        {
            foreach (var e in chunk.Entities)
            {
                ids.Add(e);
            }
        }

        return ids;
    }
}
