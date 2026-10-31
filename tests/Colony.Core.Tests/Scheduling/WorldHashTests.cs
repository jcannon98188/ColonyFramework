namespace Colony.Core.Tests.Scheduling;

/// <summary>AC #5: what the World hash covers and what it excludes (spec section 6).</summary>
public sealed class WorldHashTests
{
    private static World Populate(WorldSchema schema, int count, int seed = 0)
    {
        var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var position = schema.Component<Position>();
        var hunger = schema.Component<Hunger>();
        var health = schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < count; i++)
        {
            var e = buffer.Create(ComponentMask.Of(position, hunger, health));
            buffer.Add(e, position, new Position { X = i + seed, Y = -i });
            buffer.Add(e, hunger, new Hunger { Value = i * 0.5f });
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }

    [Fact]
    public void Equal_worlds_hash_equal_and_a_single_component_value_changes_the_hash()
    {
        var schema = TestSchemas.ColonistSchema();
        using var a = Populate(schema, 5000);
        using var b = Populate(schema, 5000);
        using var c = Populate(schema, 5000, seed: 1);

        Assert.Equal(a.Hash(), b.Hash());
        Assert.NotEqual(a.Hash(), c.Hash());

        var health = schema.Component<Health>();
        foreach (var chunk in b.Query(health))
        {
            chunk.Column<Health>(health)[^1].Value = 1;
            break;
        }

        Assert.NotEqual(a.Hash(), b.Hash());
    }

    [Fact]
    public void Padding_rows_change_versions_and_the_asleep_bit_are_excluded()
    {
        var schema = TestSchemas.ColonistSchema();
        using var world = Populate(schema, 1000); // not a multiple of 16: the last chunk has padding rows
        var health = schema.Component<Health>();
        ulong before = world.Hash();

        foreach (var chunk in world.Query(health))
        {
            Assert.True(chunk.PaddedCount > chunk.Count);
            var padded = chunk.PaddedColumn<Health>(health);
            for (int i = chunk.Count; i < padded.Length; i++)
            {
                padded[i].Value = 0x5A5A5A5A;
            }

            chunk.SetAsleep(true);
        }

        world.ArchetypeAt(0).MarkWritten(0, ComponentMask.Of(health), 12345);

        Assert.Equal(before, world.Hash());
    }

    [Fact]
    public void Rng_state_the_tick_and_non_fragmenting_components_are_included()
    {
        var schema = TestSchemas.ColonistSchema();
        using var world = Populate(schema, 100);
        ulong start = world.Hash();

        world.SharedRandom.NextUInt32();
        ulong afterDraw = world.Hash();
        Assert.NotEqual(start, afterDraw);

        var marked = schema.Component<Marked>();
        var buffer = world.CreateCommandBuffer();
        EntityId first = default;
        foreach (var chunk in world.Query(schema.Component<Health>()))
        {
            first = chunk.Entities[0];
            break;
        }

        buffer.Add(first, marked, new Marked { Stamp = 3 });
        world.Playback(buffer);
        world.ResetScratch();
        ulong afterMark = world.Hash();
        Assert.NotEqual(afterDraw, afterMark);

        world.Get<Marked>(first, marked).Stamp = 4;
        Assert.NotEqual(afterMark, world.Hash());

        world.Tick();
        Assert.NotEqual(afterMark, world.Hash());
    }

    [Fact]
    public void Hashing_is_stable_across_calls_and_seeds_differ()
    {
        var schema = TestSchemas.ColonistSchema();
        using var a = schema.CreateWorld(new WorldOptions { Seed = 1, WorkerThreads = 1 });
        using var b = schema.CreateWorld(new WorldOptions { Seed = 2, WorkerThreads = 1 });

        Assert.Equal(a.Hash(), a.Hash());
        Assert.NotEqual(a.Hash(), b.Hash());
    }
}
