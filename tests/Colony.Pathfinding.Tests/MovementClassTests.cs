using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #1: movement classes are registered as Definitions with passability rules, built on first use, limited per World.</summary>
public sealed class MovementClassTests
{
    [Fact]
    public void A_movement_class_is_registered_by_name_with_its_passability_rule_and_is_built_on_first_use()
    {
        var builder = new WorldBuilder();
        var fields = builder.AddFields(new WorldSize(32, 16, 1));
        var terrain = fields.AddField<byte>("terrain");
        var pathfinding = builder.AddPathfinding(fields);
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        var schema = builder.Build();
        using var world = schema.CreateWorld();
        var service = world.Pathfinding();

        Assert.Equal(0, walker.Id);
        Assert.Equal("walker", service.Describe(walker).Name);
        Assert.Equal(walker, service.MovementClass("walker"));
        Assert.False(service.IsBuilt(walker));

        Assert.True(service.Reachable(new TilePos(0, 0, 0), new TilePos(31, 15, 0), walker));

        Assert.True(service.IsBuilt(walker));
        Assert.Equal(1, service.Regions(walker).Counters.Builds);
    }

    [Fact]
    public void Each_class_has_its_own_passability_rule()
    {
        using var world = TestWorlds.Create(new WorldSize(32, 16, 1), out var terrain, out var walker, out var flyer);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 16);

        Assert.False(service.Reachable(new TilePos(0, 0, 0), new TilePos(31, 0, 0), walker));
        Assert.True(service.Reachable(new TilePos(0, 0, 0), new TilePos(31, 0, 0), flyer));
    }

    [Fact]
    public void Registration_beyond_the_per_world_limit_fails_at_build()
    {
        var builder = new WorldBuilder();
        var fields = builder.AddFields(new WorldSize(16, 16, 1));
        var pathfinding = builder.AddPathfinding(fields, new PathfindingOptions { MaxMovementClasses = 2 });
        for (int i = 0; i < 3; i++)
        {
            pathfinding.AddMovementClass(new MovementClassDefinition($"class-{i}", static (in PassabilityInput input, Span<ulong> passable) => passable.Fill(ulong.MaxValue)));
        }

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_limit_is_32_and_the_hard_ceiling_is_256()
    {
        Assert.Equal(32, PathfindingOptions.DefaultMovementClassLimit);
        Assert.Equal(256, PathfindingOptions.MovementClassCeiling);
        Assert.Equal(32, new PathfindingOptions().MaxMovementClasses);

        var builder = new WorldBuilder();
        var fields = builder.AddFields(new WorldSize(16, 16, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddPathfinding(fields, new PathfindingOptions { MaxMovementClasses = 257 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddPathfinding(fields, new PathfindingOptions { MaxMovementClasses = 0 }));
    }

    [Fact]
    public void Duplicate_names_and_registration_after_build_are_rejected()
    {
        var builder = new WorldBuilder();
        var fields = builder.AddFields(new WorldSize(16, 16, 1));
        var pathfinding = builder.AddPathfinding(fields);
        pathfinding.AddMovementClass(new MovementClassDefinition("walker", TestWorlds.AllPassable));

        Assert.Throws<ArgumentException>(() => pathfinding.AddMovementClass(new MovementClassDefinition("walker", TestWorlds.AllPassable)));
        builder.Build();
        Assert.Throws<InvalidOperationException>(() => pathfinding.AddMovementClass(new MovementClassDefinition("flyer", TestWorlds.AllPassable)));
    }

    [Fact]
    public void Classes_are_built_independently_and_count_toward_the_schema_hash()
    {
        using var world = TestWorlds.Create(new WorldSize(16, 16, 1), out _, out var walker, out var flyer);
        var service = world.Pathfinding();

        service.EnsureBuilt(flyer);

        Assert.True(service.IsBuilt(flyer));
        Assert.False(service.IsBuilt(walker));

        var one = new WorldBuilder();
        var fields1 = one.AddFields(new WorldSize(16, 16, 1));
        one.AddPathfinding(fields1).AddMovementClass(new MovementClassDefinition("a", TestWorlds.AllPassable));
        var two = new WorldBuilder();
        var fields2 = two.AddFields(new WorldSize(16, 16, 1));
        two.AddPathfinding(fields2).AddMovementClass(new MovementClassDefinition("b", TestWorlds.AllPassable));
        Assert.NotEqual(one.Build().SchemaHash, two.Build().SchemaHash);
    }
}
