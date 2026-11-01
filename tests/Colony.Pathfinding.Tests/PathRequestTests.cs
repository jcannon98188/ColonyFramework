using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #2: requests submitted in Tick T are answered in Begin of Tick T+N in canonical order under a work-unit budget.</summary>
public sealed class PathRequestTests
{
    private static World CreateQueueWorld(PathfindingOptions options, out Field<byte> terrain, out MovementClass walker, out PathfindingBuilder pathfinding, Action<WorldBuilder, PathfindingBuilder, MovementClass>? systems = null, int workers = 1)
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(new WorldSize(64, 32, 1));
        terrain = fields.AddField<byte>("terrain");
        pathfinding = builder.AddPathfinding(fields, options);
        walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        systems?.Invoke(builder, pathfinding, walker);
        return builder.Build().CreateWorld(new WorldOptions { WorkerThreads = workers });
    }

    private static SystemDefinition Observer(PathfindingBuilder pathfinding, List<(long Tick, PathResult Result, TilePos[] Waypoints)> seen) => new()
    {
        Name = "test.observer",
        Phase = Phase.Simulate,
        ReadsResources = ResourceMask.Of(pathfinding.ResultQueue),
        Begin = (in SystemContext context) =>
        {
            var service = context.World.Pathfinding();
            foreach (var result in context.Events<PathResult>(pathfinding.ResultQueue))
            {
                seen.Add((context.Tick, result, service.Waypoints(in result).ToArray()));
            }
        },
    };

    private static void Submit(World world, PathfindingService service, params ReadOnlySpan<PathRequest> requests)
    {
        var buffer = world.CreateCommandBuffer();
        foreach (var request in requests)
        {
            buffer.Emit(service.RequestQueue, in request);
        }

        world.Playback(buffer);
    }

    [Fact]
    public void A_request_emitted_in_tick_T_is_answered_in_begin_of_tick_T_plus_N_with_a_well_formed_path()
    {
        const long submitTick = 2;
        var seen = new List<(long Tick, PathResult Result, TilePos[] Waypoints)>();
        using var world = CreateQueueWorld(new PathfindingOptions { ResultDelayTicks = 2 }, out var terrain, out var walker, out _, (builder, pathfinding, walker) =>
        {
            builder.AddSystem(new SystemDefinition
            {
                Name = "test.requester",
                Phase = Phase.Simulate,
                WritesResources = ResourceMask.Of(pathfinding.RequestQueue),
                Begin = (in SystemContext context) =>
                {
                    if (context.Tick == submitTick)
                    {
                        context.Emit(pathfinding.RequestQueue, new PathRequest(new EntityId(7, 1), new TilePos(0, 0, 0), new TilePos(40, 0, 0), walker, Priority: 0, Tag: 99));
                    }
                },
            });
            builder.AddSystem(Observer(pathfinding, seen));
        });
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);
        var service = world.Pathfinding();

        for (int t = 0; t < 8; t++)
        {
            world.Tick();
            if (world.CurrentTick == submitTick + 1)
            {
                Assert.Equal(1, service.PendingRequestCount); // taken in at End of the submitting Tick
            }
        }

        var (tick, result, waypoints) = Assert.Single(seen);
        Assert.Equal(submitTick + 2, tick);
        Assert.Equal(PathStatus.Found, result.Status);
        Assert.Equal(100, result.Cost);
        Assert.Equal(0, result.RequestId);
        Assert.Equal(new EntityId(7, 1), result.Requester);
        Assert.Equal(99u, result.Tag);
        Assert.Equal(result.WaypointCount, waypoints.Length);
        Assert.Equal(new TilePos(0, 0, 0), waypoints[0]);
        Assert.Equal(new TilePos(40, 0, 0), waypoints[^1]);
        HpaPathTests.AssertWaypointsAreWellFormed(world, waypoints);
        Assert.Equal(0, service.PendingRequestCount);
        Assert.Equal(1, service.QueueCounters.Delivered);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Requests_are_answered_in_priority_then_id_order_and_an_exhausted_budget_defers_the_rest_until_the_deadline(int workers)
    {
        var seen = new List<(long Tick, PathResult Result, TilePos[] Waypoints)>();
        using var world = CreateQueueWorld(new PathfindingOptions { ResultDelayTicks = 3, WorkBudgetPerTick = 1 }, out var terrain, out var walker, out _, (builder, pathfinding, _) => builder.AddSystem(Observer(pathfinding, seen)), workers);
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 30);
        var service = world.Pathfinding();
        var from = new TilePos(0, 0, 0);
        var to = new TilePos(40, 0, 0);
        Submit(world, service,
            new PathRequest(new EntityId(1, 1), from, to, walker, Priority: 2, Tag: 0),
            new PathRequest(new EntityId(2, 1), from, to, walker, Priority: 0, Tag: 1),
            new PathRequest(new EntityId(3, 1), from, to, walker, Priority: 1, Tag: 2),
            new PathRequest(new EntityId(4, 1), from, to, walker, Priority: 0, Tag: 3));

        for (int t = 0; t < 6; t++)
        {
            world.Tick(); // Tick 0 takes the requests in; a budget of one work unit computes one request per Tick
        }

        Assert.Equal(4, seen.Count);
        Assert.All(seen, s => Assert.Equal(3, s.Tick));
        Assert.Equal([1u, 3u, 2u, 0u], seen.Select(s => s.Result.Tag));
        Assert.Equal([1L, 3L, 2L, 0L], seen.Select(s => s.Result.RequestId));
        Assert.Equal([PathStatus.Found, PathStatus.Found, PathStatus.Found, PathStatus.DeadlineMissed], seen.Select(s => s.Result.Status));
        Assert.All(seen.Take(3), s => Assert.Equal(100, s.Result.Cost));
        Assert.Empty(seen[3].Waypoints);
        var counters = service.QueueCounters;
        Assert.Equal(4, counters.Submitted);
        Assert.Equal(3, counters.Computed);
        Assert.Equal(4, counters.Delivered);
        Assert.Equal(1, counters.DeadlinesMissed);
        Assert.True(counters.Deferred >= 3, $"each Tick deferred the requests the budget could not reach: {counters.Deferred}");
        Assert.Equal(0, service.PendingRequestCount);
    }

    [Fact]
    public void A_request_through_an_unloaded_chunk_fails_with_needs_loading_naming_the_chunk()
    {
        using var world = CreateQueueWorld(new PathfindingOptions { ResultDelayTicks = 1 }, out _, out var walker, out _);
        var service = world.Pathfinding();
        var fields = world.Fields();
        int unloaded = fields.OrdinalOf(new ChunkCoord(1, 0, 0));
        service.ResidencyOverride = ordinal => ordinal == unloaded ? ChunkResidency.Unloaded : ChunkResidency.Active;
        var from = new TilePos(0, 0, 0);
        var to = new TilePos(40, 0, 0);

        var direct = service.FindPath(from, to, walker, Span<TilePos>.Empty);
        Submit(world, service, new PathRequest(new EntityId(1, 1), from, to, walker, Priority: 0, Tag: 5));
        world.Tick();
        world.Tick();

        Assert.Equal(PathStatus.NeedsLoading, direct.Status);
        Assert.Equal(new ChunkCoord(1, 0, 0), direct.MissingChunk);
        var result = Assert.Single(service.Delivered.ToArray());
        Assert.Equal(PathStatus.NeedsLoading, result.Status);
        Assert.Equal(new ChunkCoord(1, 0, 0), result.MissingChunk);
        Assert.Equal(5u, result.Tag);
        Assert.Equal(0, result.WaypointCount);
    }
}
