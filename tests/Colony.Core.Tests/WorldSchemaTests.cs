namespace Colony.Core.Tests;

public sealed class WorldSchemaTests
{
    [Fact]
    public void Component_identity_is_the_registered_name_and_layout_not_the_clr_type()
    {
        var first = new WorldBuilder().AddComponent<Health>("health").Build();
        var renamed = new WorldBuilder().AddComponent<Health>("hp").Build();
        var sameLayoutOtherType = new WorldBuilder().AddComponent<OtherInt>("health").Build();

        var health = first.Describe(first.Component("health"));
        Assert.Equal("health", health.Name);
        Assert.Equal(4, health.Size);
        Assert.Equal(4, health.Alignment);
        Assert.NotEqual(health.LayoutHash, renamed.Describe(renamed.Component("hp")).LayoutHash);
        Assert.Equal(health.LayoutHash, sameLayoutOtherType.Describe(sameLayoutOtherType.Component("health")).LayoutHash);
        Assert.Equal(first.SchemaHash, sameLayoutOtherType.SchemaHash);
        Assert.NotEqual(first.SchemaHash, renamed.SchemaHash);
    }

    [Fact]
    public void Layout_hash_covers_size_and_alignment()
    {
        var wide = new WorldBuilder().AddComponent<Position>("p").Build();
        var packed = new WorldBuilder().AddComponent<PackedLong>("p").Build();
        var natural = new WorldBuilder().AddComponent<NaturalLong>("p").Build();

        Assert.Equal(8, wide.Describe(wide.Component("p")).Size);
        Assert.Equal(4, wide.Describe(wide.Component("p")).Alignment);
        Assert.Equal(1, packed.Describe(packed.Component("p")).Alignment);
        Assert.Equal(8, natural.Describe(natural.Component("p")).Alignment);
        Assert.NotEqual(wide.SchemaHash, packed.SchemaHash);
        Assert.NotEqual(packed.SchemaHash, natural.SchemaHash);
    }

    [Fact]
    public void Duplicate_names_or_types_and_registration_after_build_are_rejected()
    {
        var builder = new WorldBuilder().AddComponent<Health>("health");

        Assert.Throws<ArgumentException>(() => builder.AddComponent<Hunger>("health"));
        Assert.Throws<ArgumentException>(() => builder.AddComponent<Health>("health2"));
        Assert.Throws<ArgumentException>(() => builder.AddComponent<Hunger>(" "));

        var schema = builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.AddComponent<Hunger>("hunger"));
        Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Throws<ArgumentException>(() => schema.Component<Hunger>());
        Assert.Throws<ArgumentException>(() => schema.Component("hunger"));
        Assert.False(schema.TryGetComponent("hunger", out _));
        Assert.True(schema.TryGetComponent("health", out var health));
        Assert.Equal(0, health.Id);
    }

    [Fact]
    public void Handles_from_another_schema_are_rejected_by_the_world()
    {
        var other = new WorldBuilder().AddComponent<Health>("a").AddComponent<Hunger>("b").AddComponent<Position>("c")
            .AddComponent<Marked>("d").AddComponent<OtherInt>("e").Build();
        using var world = TestSchemas.ColonistSchema().CreateWorld();

        Assert.Throws<ArgumentException>(() => world.Has(EntityId.Null, other.Component<OtherInt>()));
        Assert.Throws<ArgumentException>(() => world.Playback(other.CreateWorld().CreateCommandBuffer()));
    }

    public struct OtherInt
    {
        public int Value;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct PackedLong
    {
        public long Value;
    }

    public struct NaturalLong
    {
        public long Value;
    }
}
