using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #1: movers inside the bound descend the Field one face move at a time; movers outside it reach the bound with HPA*.</summary>
public sealed class FlowStepTests
{
    private static readonly WorldSize Plane = new(128, 64, 1);

    [Fact]
    public void Steps_descend_by_exactly_one_each_move_and_arrive_at_a_goal_from_every_in_bound_tile()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 40, doorY: 10);
        TilePos[] goals = [new(50, 5, 0), new(52, 40, 0)];
        var field = service.FlowField(goals, walker, radiusChunks: 1);

        var size = world.Fields().Size;
        int walked = 0;
        for (int y = 0; y < size.Y; y += 3)
        {
            for (int x = 32; x < 80; x += 5)
            {
                var at = new TilePos(x, y, 0);
                int distance = field.DistanceTo(at);
                if (distance < 0)
                {
                    continue;
                }

                var status = field.TryStep(at, out var next);
                while (status == FlowStepStatus.Step)
                {
                    Assert.Equal(1, Math.Abs(next.X - at.X) + Math.Abs(next.Y - at.Y) + Math.Abs(next.Z - at.Z));
                    Assert.Equal(distance - 1, field.DistanceTo(next));
                    at = next;
                    distance--;
                    status = field.TryStep(at, out next);
                }

                Assert.Equal(FlowStepStatus.AtGoal, status);
                Assert.Contains(at, goals);
                walked++;
            }
        }

        Assert.True(walked > 50, "most sampled tiles are in the bound and reachable");
        Assert.Equal(FlowStepStatus.OutsideBound, field.TryStep(new TilePos(0, 0, 0), out _));
        Assert.Equal(FlowStepStatus.Unreachable, field.TryStep(new TilePos(40, 0, 0), out _)); // the wall
    }

    [Fact]
    public void A_mover_outside_the_bound_walks_to_it_with_hpa_star_and_then_follows_the_field()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 40, doorY: 10);
        TilePos[] goals = [new(100, 5, 0)];
        var field = service.FlowField(goals, walker, radiusChunks: 0);
        Assert.Equal(1, field.ChunkCount);

        var at = new TilePos(2, 60, 0);
        int hpaSteps = 0;
        int flowSteps = 0;
        FlowStepStatus status;
        while ((status = service.TryNextStep(field, at, out var next)) == FlowStepStatus.Step)
        {
            Assert.Equal(1, Math.Abs(next.X - at.X) + Math.Abs(next.Y - at.Y));
            Assert.NotEqual(1, world.Fields().Accessor(terrain)[next]);
            if (field.Bounds.Contains(at.Chunk))
            {
                flowSteps++;
            }
            else
            {
                hpaSteps++;
            }

            at = next;
            Assert.True(hpaSteps + flowSteps < 1000, "the walk terminates");
        }

        Assert.Equal(FlowStepStatus.AtGoal, status);
        Assert.Equal(goals[0], at);
        Assert.True(hpaSteps > 60, $"the long approach is HPA* ({hpaSteps})");
        Assert.True(flowSteps > 0, "the last chunk is walked on the Field");
        Assert.Equal(FlowStepStatus.OutsideBound, service.TryNextStep(field, new TilePos(40, 0, 0), out _)); // standing in the wall: no way in
    }

    [Fact]
    public void Flow_reads_allocate_nothing()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 40, doorY: 10);
        TilePos[] goals = [new(50, 5, 0)];
        var field = service.FlowField(goals, walker, radiusChunks: 2);
        var inside = new TilePos(33, 30, 0);
        var outside = new TilePos(2, 60, 0);
        for (int i = 0; i < 3; i++)
        {
            _ = field.TryStep(inside, out _);
            _ = service.TryNextStep(field, outside, out _);
            _ = service.FlowField(goals, walker, radiusChunks: 2);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var a = field.TryStep(inside, out _);
        var b = service.TryNextStep(field, outside, out _);
        var again = service.FlowField(goals, walker, radiusChunks: 2);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.Equal(0, allocated);
        Assert.Equal(FlowStepStatus.Step, a);
        Assert.Equal(FlowStepStatus.Step, b);
        Assert.Same(field, again);
    }
}
