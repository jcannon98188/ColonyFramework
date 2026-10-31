namespace Colony.Core.Tests;

/// <summary>TASK-8 AC #3: per-Component enable bits; iteration respects them; flipping one is not a structural change.</summary>
public sealed class EnableBitTests
{
    private static World Populate(WorldSchema schema, int count)
    {
        var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 1, InitialEntityCapacity = count * 2 });
        var position = schema.Component<Position>();
        var hunger = schema.Component<Hunger>();
        var health = schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        for (int i = 0; i < count; i++)
        {
            var e = buffer.Create(ComponentMask.Of(position, hunger, health));
            buffer.Add(e, position, new Position { X = i, Y = -i });
        }

        world.Playback(buffer);
        world.ResetScratch();
        return world;
    }

    [Fact]
    public void Components_start_enabled_and_a_flip_is_visible_through_the_entity_and_chunk_apis_without_a_structural_change()
    {
        var schema = TestSchemas.ColonistSchema(debug: true);
        using var world = Populate(schema, 100);
        var health = schema.Component<Health>();
        var marked = schema.Component<Marked>();
        EntityId third = default;
        foreach (var chunk in world.Query(health))
        {
            third = chunk.Entities[2];
            Assert.Equal(chunk.Count, chunk.EnabledCount(health));
            Assert.True(chunk.IsEnabled(health, 2));
        }

        var view = world.Query(health);
        Assert.True(view.MoveNext());
        var before = view.Current;
        long structural = world.Counters.Total;
        world.SetEnabled(third, health, false);

        Assert.False(world.IsEnabled(third, health));
        Assert.True(before.IsCurrent); // not a structural change: views stay valid
        Assert.Equal(structural, world.Counters.Total);
        Assert.False(before.IsEnabled(health, 2));
        Assert.Equal(99, before.EnabledCount(health));
        Assert.True(world.IsEnabled(third, schema.Component<Position>()));

        before.SetEnabled(health, 2, true);
        Assert.True(world.IsEnabled(third, health));

        // Non-fragmenting Components have enable bits too.
        var buffer = world.CreateCommandBuffer();
        buffer.Add(third, marked, new Marked { Stamp = 1 });
        world.Playback(buffer);
        Assert.True(world.IsEnabled(third, marked));
        world.SetEnabled(third, marked, false);
        Assert.False(world.IsEnabled(third, marked));
        Assert.True(world.Has(third, marked));

        // A missing Component or a dead Entity is an error, not a silent no-op.
        buffer = world.CreateCommandBuffer();
        buffer.Remove(third, schema.Component<Hunger>());
        world.Playback(buffer);
        Assert.Throws<InvalidOperationException>(() => world.SetEnabled(third, schema.Component<Hunger>(), true));
        Assert.Throws<InvalidOperationException>(() => world.IsEnabled(new EntityId(third.Index, third.Generation + 1), health));
    }

    [Fact]
    public void Enabled_rows_enumerates_only_rows_with_every_listed_component_enabled()
    {
        var schema = TestSchemas.ColonistSchema();
        using var world = Populate(schema, 70);
        var health = schema.Component<Health>();
        var hunger = schema.Component<Hunger>();
        foreach (var chunk in world.Query(health))
        {
            for (int row = 0; row < chunk.Count; row += 2)
            {
                chunk.SetEnabled(health, row, false);
            }

            chunk.SetEnabled(hunger, 1, false);
            chunk.SetEnabled(hunger, 69, false);
        }

        var rows = new List<int>();
        var both = new List<int>();
        foreach (var chunk in world.Query(health))
        {
            foreach (int row in chunk.EnabledRows(health))
            {
                rows.Add(row);
            }

            foreach (int row in chunk.EnabledRows(ComponentMask.Of(health, hunger)))
            {
                both.Add(row);
            }
        }

        Assert.Equal(Enumerable.Range(0, 70).Where(r => r % 2 == 1), rows);
        Assert.Equal(Enumerable.Range(0, 70).Where(r => r % 2 == 1 && r != 1 && r != 69), both);
    }

    [Fact]
    public void Enable_bits_follow_rows_through_swap_removal_and_archetype_moves()
    {
        var schema = TestSchemas.ColonistSchema();
        using var world = Populate(schema, 20);
        var health = schema.Component<Health>();
        var hunger = schema.Component<Hunger>();
        var position = schema.Component<Position>();
        var entities = new List<EntityId>();
        foreach (var chunk in world.Query(health))
        {
            entities.AddRange(chunk.Entities);
        }

        var last = entities[^1];
        world.SetEnabled(last, health, false);
        var buffer = world.CreateCommandBuffer();
        buffer.Destroy(entities[0]); // the last row is swapped into row 0
        world.Playback(buffer);
        Assert.False(world.IsEnabled(last, health));
        Assert.True(world.IsEnabled(entities[1], health));

        world.SetEnabled(entities[5], hunger, false);
        buffer.Remove(entities[5], position); // moves to another archetype
        world.Playback(buffer);
        Assert.False(world.IsEnabled(entities[5], hunger));
        Assert.True(world.IsEnabled(entities[5], health));

        buffer.Add(entities[5], position, new Position { X = 1 }); // moves back; the new Component is enabled
        world.Playback(buffer);
        Assert.False(world.IsEnabled(entities[5], hunger));
        Assert.True(world.IsEnabled(entities[5], position));
        world.ResetScratch();
    }

    [Fact]
    public void Enable_bits_are_part_of_the_world_hash()
    {
        var schema = TestSchemas.ColonistSchema();
        using var a = Populate(schema, 50);
        using var b = Populate(schema, 50);
        var health = schema.Component<Health>();
        Assert.Equal(a.Hash(), b.Hash());

        EntityId e = default;
        foreach (var chunk in b.Query(health))
        {
            e = chunk.Entities[7];
        }

        b.SetEnabled(e, health, false);
        Assert.NotEqual(a.Hash(), b.Hash());
        b.SetEnabled(e, health, true);
        Assert.Equal(a.Hash(), b.Hash());
    }

    [Fact]
    public void A_chunk_whose_queried_column_is_fully_disabled_is_skipped_unless_the_query_ignores_enable_bits()
    {
        var schema = TestSchemas.ColonistSchema();
        using var world = Populate(schema, 40);
        var health = schema.Component<Health>();
        var hunger = schema.Component<Hunger>();
        foreach (var chunk in world.Query(health))
        {
            for (int row = 0; row < chunk.Count; row++)
            {
                chunk.SetEnabled(health, row, false);
            }
        }

        int seen = 0;
        foreach (var chunk in world.Query(health))
        {
            seen += chunk.Count;
        }

        int seenByHunger = 0;
        foreach (var chunk in world.Query(hunger))
        {
            seenByHunger += chunk.Count;
        }

        int seenIgnoring = 0;
        foreach (var chunk in world.Query(ComponentMask.Of(health), ignoreEnableBits: true))
        {
            seenIgnoring += chunk.Count;
        }

        Assert.Equal(0, seen);
        Assert.Equal(40, seenByHunger);
        Assert.Equal(40, seenIgnoring);
    }
}
