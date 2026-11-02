using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #3: approximate path cost and the next-step-toward-goal query, both allocation free.</summary>
[Collection(Colony.TestSupport.AllocationSensitiveGroup.Name)]
public sealed class PathQueryTests
{
    [Fact]
    public void Approximate_cost_equals_the_path_cost_and_is_negative_when_unreachable()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);

        Assert.Equal(100, service.ApproximateCost(new TilePos(0, 0, 0), new TilePos(40, 0, 0), walker));
        Assert.Equal(0, service.ApproximateCost(new TilePos(3, 3, 0), new TilePos(3, 3, 0), walker));
        Assert.Equal(-1, service.ApproximateCost(new TilePos(0, 0, 0), new TilePos(20, 0, 0), walker));
        Assert.Equal(-1, service.ApproximateCost(new TilePos(0, 0, 0), new TilePos(99, 0, 0), walker));
    }

    [Fact]
    public void Next_step_walks_a_unit_to_its_goal_through_the_door_one_face_move_at_a_time()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);
        var at = new TilePos(0, 0, 0);
        var goal = new TilePos(40, 0, 0);

        int steps = 0;
        while (service.TryNextStep(at, goal, walker, out var next))
        {
            var d = next - at;
            Assert.Equal(1, Math.Abs(d.X) + Math.Abs(d.Y) + Math.Abs(d.Z));
            Assert.NotEqual(1, world.Fields().Accessor(terrain)[next]);
            at = next;
            Assert.True(++steps <= 100, "a 100-tile path takes at most 100 steps");
        }

        Assert.Equal(goal, at);
        Assert.Equal(100, steps); // following next steps walks the HPA* path exactly
        Assert.False(service.TryNextStep(goal, new TilePos(20, 0, 0), walker, out _)); // into the wall: no step
    }

    [Fact]
    public void Cost_and_next_step_queries_allocate_nothing_once_warm()
    {
        _ = PrototypeTerrainWorld.Create(new WorldSize(100, 72, 2), out var world, out var walker);
        using (world)
        {
            var service = world.Pathfinding();
            var from = new TilePos(1, 1, 0);
            var to = new TilePos(97, 70, 0);
            while (service.ApproximateCost(from, to, walker) < 0)
            {
                to = new TilePos(to.X - 1, to.Y, 0); // rock and pockets: slide to the first reachable far tile
            }

            for (int i = 0; i < 3; i++)
            {
                _ = service.ApproximateCost(from, to, walker);
                _ = service.TryNextStep(from, to, walker, out _);
                _ = service.FindPath(from, to, walker, Span<TilePos>.Empty);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int cost = service.ApproximateCost(from, to, walker);
            bool stepped = service.TryNextStep(from, to, walker, out var next);
            var query = service.FindPath(from, to, walker, Span<TilePos>.Empty);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

            Assert.Equal(0, allocated);
            Assert.True(cost > 0);
            Assert.True(stepped);
            Assert.NotEqual(from, next);
            Assert.Equal(cost, query.Cost);
        }
    }
}
