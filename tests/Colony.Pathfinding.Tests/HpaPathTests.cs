using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #1: two-level chunk-graph HPA* paths for unique goals, near optimal and deterministic.</summary>
public sealed class HpaPathTests
{
    [Fact]
    public void A_path_inside_one_open_chunk_costs_the_manhattan_distance_and_has_two_waypoints()
    {
        using var world = TestWorlds.Create(new WorldSize(32, 32, 1), out _, out var walker, out _);
        var service = world.Pathfinding();
        var waypoints = new TilePos[64];

        var query = service.FindPath(new TilePos(1, 2, 0), new TilePos(9, 14, 0), walker, waypoints);

        Assert.Equal(PathStatus.Found, query.Status);
        Assert.Equal(8 + 12, query.Cost);
        Assert.Equal(2, query.WaypointCount);
        Assert.Equal([new TilePos(1, 2, 0), new TilePos(9, 14, 0)], waypoints.AsSpan(0, 2).ToArray());
    }

    [Fact]
    public void A_path_across_chunks_costs_the_manhattan_distance_on_an_open_plane()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 48, 1), out _, out var walker, out _);
        var service = world.Pathfinding();
        var waypoints = new TilePos[64];

        var query = service.FindPath(new TilePos(0, 0, 0), new TilePos(63, 47, 0), walker, waypoints);

        Assert.Equal(PathStatus.Found, query.Status);
        Assert.Equal(63 + 47, query.Cost);
        Assert.True(query.WaypointCount >= 3, "crossing chunks goes through at least one transition");
        AssertWaypointsAreWellFormed(world, waypoints.AsSpan(0, query.WaypointCount));
    }

    [Fact]
    public void A_path_through_the_only_door_detours_through_it_and_a_sealed_goal_is_unreachable()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);
        var waypoints = new TilePos[64];

        var query = service.FindPath(new TilePos(0, 0, 0), new TilePos(40, 0, 0), walker, waypoints);

        Assert.Equal(PathStatus.Found, query.Status);
        Assert.Equal(40 + 30 + 30, query.Cost); // up to the door row, across, back down
        var path = waypoints.AsSpan(0, query.WaypointCount).ToArray();
        Assert.Contains(path, w => w.Y >= 16); // the door sits in the upper chunk row; waypoints are chunk transitions, not interior tiles
        AssertWaypointsAreWellFormed(world, path);

        TestWorlds.Set(world, terrain, 20, 30, 0, 1);
        service.Sync();
        Assert.Equal(PathStatus.Unreachable, service.FindPath(new TilePos(0, 0, 0), new TilePos(40, 0, 0), walker, waypoints).Status);
        Assert.Equal(PathStatus.Unreachable, service.FindPath(new TilePos(0, 0, 0), new TilePos(20, 0, 0), walker, waypoints).Status); // into the wall
        Assert.Equal(PathStatus.Unreachable, service.FindPath(new TilePos(0, 0, 0), new TilePos(-1, 0, 0), walker, waypoints).Status); // outside
    }

    /// <summary>Consecutive waypoints are either in one chunk or face-adjacent tiles across a chunk border; the first and last are the endpoints.</summary>
    internal static void AssertWaypointsAreWellFormed(World world, ReadOnlySpan<TilePos> waypoints)
    {
        for (int i = 1; i < waypoints.Length; i++)
        {
            var a = waypoints[i - 1];
            var b = waypoints[i];
            if (a.Chunk != b.Chunk)
            {
                var d = b - a;
                Assert.Equal(1, Math.Abs(d.X) + Math.Abs(d.Y) + Math.Abs(d.Z));
            }

            Assert.True(world.Fields().Size.Contains(b));
        }
    }
}
