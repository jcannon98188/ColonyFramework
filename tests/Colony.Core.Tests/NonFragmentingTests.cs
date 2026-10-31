namespace Colony.Core.Tests;

public sealed class NonFragmentingTests
{
    [Fact]
    public void Adding_and_removing_a_non_fragmenting_component_does_not_move_the_entity()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var position = world.Schema.Component<Position>();
        var marked = world.Schema.Component<Marked>();
        Assert.True(world.Schema.Describe(marked).IsNonFragmenting);
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create(ComponentMask.Of(position));
        world.Playback(buffer);
        var entity = buffer.Resolve(deferred);
        int archetypes = world.ArchetypeCount;

        buffer.Add(entity, marked, new Marked { Stamp = 42 });
        world.Playback(buffer);

        Assert.True(world.Has(entity, marked));
        Assert.Equal(42, world.Get<Marked>(entity, marked).Stamp);
        Assert.Equal(archetypes, world.ArchetypeCount);
        foreach (var chunk in world.Query(position))
        {
            Assert.Equal(entity, chunk.Entities[0]);
            Assert.False(chunk.Signature.Contains(marked));
            Assert.False(chunk.Has(marked));
        }

        buffer.Remove(entity, marked);
        world.Playback(buffer);
        Assert.False(world.Has(entity, marked));
        Assert.True(world.Has(entity, position));
        Assert.Equal(archetypes, world.ArchetypeCount);
        Assert.Throws<InvalidOperationException>(() => world.Get<Marked>(entity, marked));
    }

    [Fact]
    public void Destroying_an_entity_drops_its_non_fragmenting_components()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var marked = world.Schema.Component<Marked>();
        var buffer = world.CreateCommandBuffer();
        var deferred = buffer.Create();
        buffer.Add(deferred, marked, new Marked { Stamp = 7 });
        world.Playback(buffer);
        var first = buffer.Resolve(deferred);

        buffer.Destroy(first);
        world.Playback(buffer);
        var again = buffer.Create();
        world.Playback(buffer);
        var second = buffer.Resolve(again);

        Assert.Equal(first.Index, second.Index);
        Assert.False(world.Has(first, marked));
        Assert.False(world.Has(second, marked));
    }

    [Fact]
    public void Non_fragmenting_components_survive_archetype_moves_and_swap_back()
    {
        using var world = TestSchemas.ColonistSchema().CreateWorld();
        var position = world.Schema.Component<Position>();
        var hunger = world.Schema.Component<Hunger>();
        var marked = world.Schema.Component<Marked>();
        var buffer = world.CreateCommandBuffer();
        var a = buffer.Create(ComponentMask.Of(position));
        var b = buffer.Create(ComponentMask.Of(position));
        var c = buffer.Create(ComponentMask.Of(position));
        buffer.Add(a, marked, new Marked { Stamp = 1 });
        buffer.Add(c, marked, new Marked { Stamp = 3 });
        world.Playback(buffer);
        var entityA = buffer.Resolve(a);
        var entityB = buffer.Resolve(b);
        var entityC = buffer.Resolve(c);

        buffer.Add(entityA, hunger, new Hunger { Value = 1f }); // archetype move; c swaps back into a's row
        buffer.Destroy(entityB);
        world.Playback(buffer);

        Assert.Equal(1, world.Get<Marked>(entityA, marked).Stamp);
        Assert.Equal(3, world.Get<Marked>(entityC, marked).Stamp);
        Assert.False(world.Has(entityB, marked));
    }
}
