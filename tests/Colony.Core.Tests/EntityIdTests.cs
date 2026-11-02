namespace Colony.Core.Tests;

public sealed class EntityIdTests
{
    [Fact]
    public void Packs_index_in_the_low_and_generation_in_the_high_32_bits()
    {
        var id = new EntityId(index: 0x0000_0042, generation: 0x0000_0007);

        Assert.Equal(0x0000_0007_0000_0042UL, id.Value);
        Assert.Equal(0x42, id.Index);
        Assert.Equal(7u, id.Generation);
        Assert.False(id.IsNull);
        Assert.False(id.IsDeferred);
    }

    [Fact]
    public void Default_is_null_and_never_alive()
    {
        Assert.True(default(EntityId).IsNull);
        Assert.True(EntityId.Null.IsNull);
        Assert.Equal(0UL, EntityId.Null.Value);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(new EntityId(1, 2), new EntityId(1, 2));
        Assert.NotEqual(new EntityId(1, 2), new EntityId(1, 3));
        Assert.True(new EntityId(1, 2) == new EntityId(1, 2));
        Assert.True(new EntityId(1, 2) != new EntityId(2, 2));
    }
}
