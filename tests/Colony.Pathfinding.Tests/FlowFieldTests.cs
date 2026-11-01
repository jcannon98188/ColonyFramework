using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #1: a flow Field for a goal set is built lazily and bounded by a chunk radius around the goal chunks.</summary>
public sealed class FlowFieldTests
{
    private static readonly WorldSize Plane = new(128, 64, 1); // 8 x 4 chunks

    [Fact]
    public void A_flow_field_holds_the_shortest_distances_to_the_nearest_goal_inside_its_bound_and_nothing_outside()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 40, doorY: 10);
        TilePos[] goals = [new(50, 5, 0), new(52, 40, 0)];

        var field = service.FlowField(goals, walker, radiusChunks: 1);

        // Goal chunks (3,0) and (3,2) plus one chunk of radius: x chunks 2..4, y chunks 0..3 (the World has 4).
        Assert.Equal(new ChunkCoord(2, 0, 0), field.Bounds.Min);
        Assert.Equal(new ChunkCoord(4, 3, 0), field.Bounds.Max);
        Assert.Equal(12, field.ChunkCount);
        var expected = FlowOracle.Expected(world, goals, field.Bounds.Min, field.Bounds.Max, t => world.Fields().Accessor(terrain)[t] == 0);
        Assert.Equal(expected, FlowOracle.Actual(world, field));
        Assert.Equal(0, field.DistanceTo(goals[0]));
        Assert.Equal(-1, field.DistanceTo(new TilePos(0, 0, 0))); // outside the bound
        Assert.Equal(-1, field.DistanceTo(new TilePos(40, 0, 0))); // the wall
        Assert.True(field.DistanceTo(new TilePos(33, 0, 0)) > 20, "west of the wall the only way is through the door");
    }
}
