namespace Colony.Core.Tests;

public sealed class EntityLifecycleTests
{
    [Fact]
    public void Entities_created_through_a_command_buffer_are_alive_after_playback()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var position = world.Schema.Component<Position>();
        var buffer = world.CreateCommandBuffer();

        var deferred = buffer.Create();
        buffer.Add(deferred, position, new Position { X = 3, Y = 4 });
        Assert.True(deferred.IsDeferred);
        Assert.Equal(0, world.EntityCount);

        world.Playback(buffer);

        var entity = buffer.Resolve(deferred);
        Assert.False(entity.IsDeferred);
        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.EntityCount);
        Assert.True(world.Has(entity, position));
        Assert.Equal(3, world.Get<Position>(entity, position).X);
        Assert.Equal(4, world.Get<Position>(entity, position).Y);
    }

    [Fact]
    public void Destroyed_entities_fail_the_generation_check_and_their_index_is_reused()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create();
        world.Playback(buffer);
        var first = buffer.Resolve(deferred);

        buffer.Destroy(first);
        world.Playback(buffer);
        Assert.False(world.IsAlive(first));
        Assert.Equal(0, world.EntityCount);

        var again = buffer.Create();
        world.Playback(buffer);
        var second = buffer.Resolve(again);

        Assert.Equal(first.Index, second.Index);
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.NotEqual(first, second);
        Assert.False(world.IsAlive(first));
        Assert.True(world.IsAlive(second));
    }

    [Fact]
    public void Commands_against_stale_ids_are_skipped_and_counted()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var hunger = world.Schema.Component<Hunger>();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create();
        world.Playback(buffer);
        var entity = buffer.Resolve(deferred);

        buffer.Destroy(entity);
        buffer.Destroy(entity);
        buffer.Add(entity, hunger, new Hunger { Value = 1f });
        buffer.Remove(entity, hunger);
        world.Playback(buffer);

        Assert.Equal(1, world.Counters.Destroys);
        Assert.Equal(3, world.Counters.StaleSkipped);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void Adding_and_removing_components_moves_the_entity_between_archetypes()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var position = world.Schema.Component<Position>();
        var hunger = world.Schema.Component<Hunger>();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create();
        buffer.Add(deferred, position, new Position { X = 7, Y = 8 });
        world.Playback(buffer);
        var entity = buffer.Resolve(deferred);

        buffer.Add(entity, hunger, new Hunger { Value = 0.5f });
        world.Playback(buffer);
        Assert.True(world.Has(entity, position));
        Assert.True(world.Has(entity, hunger));
        Assert.Equal(7, world.Get<Position>(entity, position).X);
        Assert.Equal(0.5f, world.Get<Hunger>(entity, hunger).Value);

        buffer.Remove(entity, position);
        world.Playback(buffer);
        Assert.False(world.Has(entity, position));
        Assert.True(world.Has(entity, hunger));
        Assert.Equal(0.5f, world.Get<Hunger>(entity, hunger).Value);
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void Adding_a_component_the_entity_already_has_overwrites_the_value()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var health = world.Schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create();
        buffer.Add(deferred, health, new Health { Value = 10 });
        buffer.Add(deferred, health, new Health { Value = 20 });
        world.Playback(buffer);

        Assert.Equal(20, world.Get<Health>(buffer.Resolve(deferred), health).Value);
        Assert.Equal(1, world.Counters.Creates);
    }

    [Fact]
    public void Get_on_a_dead_entity_throws()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var health = world.Schema.Component<Health>();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create();
        buffer.Add(deferred, health, new Health { Value = 10 });
        world.Playback(buffer);
        var entity = buffer.Resolve(deferred);
        buffer.Destroy(entity);
        world.Playback(buffer);

        Assert.Throws<InvalidOperationException>(() => world.Get<Health>(entity, health));
    }

    [Fact]
    public void Component_of_the_wrong_size_is_rejected()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var hunger = world.Schema.Component<Hunger>();
        var buffer = world.CreateCommandBuffer();

        Assert.Throws<ArgumentException>(() => buffer.Add(buffer.Create(), hunger, new Position()));
    }
}
