using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #1: Fields are built lazily once per goal set and evicted least-recently-used under the memory budget.</summary>
public sealed class FlowBudgetTests
{
    private static readonly WorldSize Plane = new(128, 64, 1);

    [Fact]
    public void The_same_goal_set_in_any_order_returns_the_one_field_and_a_different_radius_another()
    {
        using var world = TestWorlds.Create(Plane, out _, out var walker, out _);
        var service = world.Pathfinding();
        Assert.Equal(0, service.FlowFieldCount);

        var a = service.FlowField([new(50, 5, 0), new(52, 40, 0), new(50, 5, 0)], walker, radiusChunks: 1);
        var b = service.FlowField([new(52, 40, 0), new(50, 5, 0)], walker, radiusChunks: 1);
        var c = service.FlowField([new(52, 40, 0), new(50, 5, 0)], walker, radiusChunks: 2);

        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(2, service.FlowFieldCount);
        Assert.Equal(new TilePos[] { new(50, 5, 0), new(52, 40, 0) }, a.Goals.ToArray());
        Assert.Same(a, service.FlowField(a.Id));
        Assert.Equal(1, service.FlowCounters.Built - 1);
        Assert.Equal(a.Bytes + c.Bytes, service.FlowFieldBytes);
    }

    [Fact]
    public void Past_the_memory_budget_the_least_recently_used_field_is_evicted_first()
    {
        // Three chunks per Field (radius 1 on a 1-high plane row), budget for two of them.
        long one = FlowField.BytesFor(3, 1);
        using var world = TestWorlds.Create(new WorldSize(128, 16, 1), out _, out var walker, out _, options: new PathfindingOptions { FlowFieldMemoryBudgetBytes = 2 * one });
        var service = world.Pathfinding();

        var first = service.FlowField([new(20, 5, 0)], walker, radiusChunks: 1);
        world.Tick();
        var second = service.FlowField([new(60, 5, 0)], walker, radiusChunks: 1);
        world.Tick();
        _ = service.FlowField([new(20, 5, 0)], walker, radiusChunks: 1); // touch the first: now the second is the oldest
        world.Tick();
        var third = service.FlowField([new(100, 5, 0)], walker, radiusChunks: 1);

        Assert.True(second.IsEvicted);
        Assert.False(first.IsEvicted);
        Assert.False(third.IsEvicted);
        Assert.Null(service.FlowField(second.Id));
        Assert.Equal(FlowStepStatus.Evicted, second.TryStep(new TilePos(60, 5, 0), out _));
        Assert.Equal(-1, second.DistanceTo(new TilePos(60, 5, 0)));
        Assert.Equal(2, service.FlowFieldCount);
        Assert.True(service.FlowFieldBytes <= 2 * one);
        Assert.Equal(1, service.FlowCounters.Evicted);

        // Asking again rebuilds it (and evicts the first, the oldest now).
        var again = service.FlowField([new(60, 5, 0)], walker, radiusChunks: 1);
        Assert.NotSame(second, again);
        Assert.True(first.IsEvicted);
    }

    [Fact]
    public void Reads_from_parallel_chunk_jobs_count_as_use_for_the_eviction_order()
    {
        long one = FlowField.BytesFor(3, 1);
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        builder.AddComponent<TilePos>("tile.pos");
        var tile = builder.Component<TilePos>();
        var fields = builder.AddFields(new WorldSize(128, 16, 1));
        var terrain = fields.AddField<byte>("terrain");
        var pathfinding = builder.AddPathfinding(fields, new PathfindingOptions { FlowFieldMemoryBudgetBytes = 2 * one });
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        FlowField? read = null;
        builder.AddSystem(new SystemDefinition
        {
            Name = "readers",
            Phase = Phase.Simulate,
            Reads = ComponentMask.Of(tile),
            ForEachChunk = (in SystemContext context, in ChunkView chunk) =>
            {
                foreach (var t in chunk.Column<TilePos>(tile))
                {
                    _ = read!.TryStep(t, out _);
                }
            },
        });
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var buffer = world.CreateCommandBuffer();
        var e = buffer.Create(ComponentMask.Of(tile));
        buffer.Add(e, tile, new TilePos(21, 5, 0));
        world.Playback(buffer);
        var service = world.Pathfinding();

        var first = service.FlowField([new(20, 5, 0)], walker, radiusChunks: 1);
        read = first;
        world.Tick();
        var second = service.FlowField([new(60, 5, 0)], walker, radiusChunks: 1);
        world.Tick(); // the readers touch `first` this Tick
        world.Tick();
        _ = service.FlowField([new(100, 5, 0)], walker, radiusChunks: 1);

        Assert.True(second.IsEvicted, "the Field nobody read since its creation goes first");
        Assert.False(first.IsEvicted);
    }
}
