using Colony.Fields;
using Colony.Numerics;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #4: the path-follow helper moves a grid Entity along a result with fixed-point sub-tile offsets.</summary>
public sealed class PathFollowTests
{
    [Fact]
    public void Following_a_path_at_a_quarter_tile_per_tick_reaches_the_goal_in_four_ticks_per_tile()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);
        var waypoints = new TilePos[64];
        var query = service.FindPath(new TilePos(0, 0, 0), new TilePos(40, 0, 0), walker, waypoints);
        var path = waypoints.AsSpan(0, query.WaypointCount);
        var state = PathFollowState.At(new TilePos(0, 0, 0));
        var speed = Fixed32.FromRatio(1, 4);

        int ticks = 0;
        PathFollowStatus status;
        var visited = new List<TilePos> { state.Tile };
        do
        {
            status = service.Follow(ref state, path, walker, speed);
            ticks++; // the arriving call moves the last quarter tile too
            Assert.True(state.Offset.X >= Fixed32.Zero && state.Offset.X < Fixed32.One, $"offset stays inside the tile: {state.Offset}");
            Assert.True(state.Offset.Y >= Fixed32.Zero && state.Offset.Y < Fixed32.One);
            Assert.NotEqual(1, world.Fields().Accessor(terrain)[state.Tile]);
            if (visited[^1] != state.Tile)
            {
                visited.Add(state.Tile);
            }

            Assert.True(ticks <= 4 * query.Cost, "never slower than the path cost");
        }
        while (status == PathFollowStatus.Moving);

        Assert.Equal(PathFollowStatus.Arrived, status);
        Assert.Equal(new TilePos(40, 0, 0), state.Tile);
        Assert.Equal(PathFollowState.Centre, state.Offset);
        Assert.Equal(4 * query.Cost, ticks);
        Assert.Equal(query.Cost + 1, visited.Count); // one new tile every four ticks, each a face move
        for (int i = 1; i < visited.Count; i++)
        {
            var d = visited[i] - visited[i - 1];
            Assert.Equal(1, Math.Abs(d.X) + Math.Abs(d.Y) + Math.Abs(d.Z));
        }

        Assert.Equal(PathFollowStatus.Arrived, service.Follow(ref state, path, walker, speed)); // stays arrived
    }

    [Fact]
    public void A_large_step_crosses_several_tiles_in_one_call_and_a_closed_door_blocks()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);
        var waypoints = new TilePos[64];
        var query = service.FindPath(new TilePos(0, 0, 0), new TilePos(40, 0, 0), walker, waypoints);
        var path = waypoints.AsSpan(0, query.WaypointCount);
        var state = PathFollowState.At(new TilePos(0, 0, 0));

        Assert.Equal(PathFollowStatus.Moving, service.Follow(ref state, path, walker, Fixed32.FromInt32(10) + Fixed32.FromRatio(1, 2)));
        var start = PathFollowState.At(new TilePos(0, 0, 0)).Position;
        var travelled = Fixed32.Abs(state.Position.X - start.X) + Fixed32.Abs(state.Position.Y - start.Y) + Fixed32.Abs(state.Position.Z - start.Z);
        Assert.Equal(Fixed32.FromInt32(10) + Fixed32.FromRatio(1, 2), travelled); // the whole distance, across tile borders
        Assert.Equal(11, Math.Abs(state.Tile.X) + Math.Abs(state.Tile.Y)); // half a tile past the centre of the tenth tile is the border of the eleventh

        TestWorlds.Set(world, terrain, 20, 30, 0, 1);
        service.Sync();
        Assert.Equal(PathFollowStatus.Blocked, service.Follow(ref state, path, walker, Fixed32.FromInt32(200)));
        Assert.True(state.Tile.X < 20, "a blocked follower stops before the wall");
    }

    [Fact]
    public void Following_from_a_chunk_job_allocates_nothing_and_matches_the_tick_thread()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);
        var waypoints = new TilePos[64];
        var query = service.FindPath(new TilePos(0, 0, 0), new TilePos(40, 0, 0), walker, waypoints);
        var path = waypoints.AsSpan(0, query.WaypointCount);
        var speed = Fixed32.FromRatio(3, 7);
        var a = PathFollowState.At(new TilePos(0, 0, 0));
        var b = a;
        for (int i = 0; i < 3; i++)
        {
            _ = service.Follow(ref a, path, walker, speed);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            _ = service.Follow(ref a, path, walker, speed);
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, allocated);

        for (int i = 0; i < 53; i++)
        {
            _ = service.Follow(ref b, path, walker, speed);
        }

        Assert.Equal(a, b);
    }
}
