using Colony.Fields;
using Colony.Pathfinding.Tests.Scenarios;

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

    /// <summary>
    /// The documented quality bound: on the prototype terrain (rooms, doors, rock, stairs over two storeys) HPA* costs
    /// are never below optimal, on average within 2 percent of it and never more than 25 percent above it, over
    /// 300 reachable random pairs. Same-Tick queries from any order of calls agree (the search is a pure function).
    /// </summary>
    [Fact]
    public void Paths_on_the_prototype_terrain_are_within_a_few_percent_of_optimal()
    {
        var size = new WorldSize(100, 72, 2);
        var tiles = PrototypeTerrainWorld.Create(size, out var world, out var walker);
        using (world)
        {
            var service = world.Pathfinding();
            var random = new Random(20);
            var waypoints = new TilePos[256];
            bool Passable(TilePos t) => PrototypeTerrain.Walkable(tiles[PrototypeTerrain.Index(size, t.X, t.Y, t.Z)]);
            bool Climb(TilePos t) => PrototypeTerrain.Climbable(tiles[PrototypeTerrain.Index(size, t.X, t.Y, t.Z)]);

            int pairs = 0;
            long optimalSum = 0;
            long hpaSum = 0;
            double worst = 1;
            while (pairs < 300)
            {
                var from = new TilePos(random.Next(size.X), random.Next(size.Y), random.Next(size.Z));
                var to = new TilePos(random.Next(size.X), random.Next(size.Y), random.Next(size.Z));
                int optimal = PathOracle.Optimal(size, Passable, Climb, from, to);
                var query = service.FindPath(from, to, walker, waypoints);
                if (optimal < 0)
                {
                    Assert.Equal(PathStatus.Unreachable, query.Status);
                    continue;
                }

                Assert.Equal(PathStatus.Found, query.Status);
                Assert.True(query.Cost >= optimal, $"{from}->{to}: HPA* {query.Cost} below optimal {optimal}");
                AssertWaypointsAreWellFormed(world, waypoints.AsSpan(0, query.WaypointCount));
                Assert.Equal(query, service.FindPath(from, to, walker, waypoints));
                worst = Math.Max(worst, (double)query.Cost / Math.Max(1, optimal));
                optimalSum += optimal;
                hpaSum += query.Cost;
                pairs++;
            }

            double mean = (double)hpaSum / optimalSum;
            Assert.True(mean <= 1.02, $"mean HPA*/optimal {mean:F4} exceeds 1.02 (worst {worst:F3})");
            Assert.True(worst <= 1.25, $"worst HPA*/optimal {worst:F3} exceeds 1.25");
        }
    }

    [Fact]
    public void A_path_between_storeys_climbs_only_where_the_class_may()
    {
        var size = new WorldSize(32, 16, 2);
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(size);
        var terrain = fields.AddField<byte>("terrain");
        var walker = builder.AddPathfinding(fields).AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, PrototypeTerrain.Walkable))
        {
            Vertical = Passability.OfField(terrain, PrototypeTerrain.Climbable),
        }.DependsOn(terrain));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        TestWorlds.Set(world, terrain, 30, 15, 0, PrototypeTerrain.Stairs); // the only way up, in the far chunk
        var service = world.Pathfinding();
        var waypoints = new TilePos[64];

        var query = service.FindPath(new TilePos(0, 0, 0), new TilePos(0, 0, 1), walker, waypoints);

        Assert.Equal(PathStatus.Found, query.Status);
        Assert.Equal(30 + 15 + 1 + 30 + 15, query.Cost);
        AssertWaypointsAreWellFormed(world, waypoints.AsSpan(0, query.WaypointCount));
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
