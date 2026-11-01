using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #4: pathfinding counters (queue depth, work units, repair backlog, Field memory versus budget) appear in the diagnostics records.</summary>
public sealed class FlowDiagnosticsTests
{
    [Fact]
    public void The_tick_record_carries_queue_depth_work_units_repair_backlog_and_flow_memory_versus_budget()
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(new WorldSize(128, 64, 1));
        var terrain = fields.AddField<byte>("terrain");
        var pathfinding = builder.AddPathfinding(fields, new PathfindingOptions { FlowRepairBudgetPerTick = 1, FlowStalenessTicks = 100, FlowFieldMemoryBudgetBytes = 1 << 20, ResultDelayTicks = 3 });
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        var schema = builder.Build();
        using var world = schema.CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var service = world.Pathfinding();
        var ids = pathfinding.Counters;

        var names = schema.Counters.ToArray();
        Assert.Equal(ids.QueueDepth.Id, Array.IndexOf(names, PathfindingCounterIds.QueueDepthName));
        Assert.Equal(ids.WorkUnits.Id, Array.IndexOf(names, PathfindingCounterIds.WorkUnitsName));
        Assert.Equal(ids.RepairBacklog.Id, Array.IndexOf(names, PathfindingCounterIds.RepairBacklogName));
        Assert.Equal(ids.FlowFieldBytes.Id, Array.IndexOf(names, PathfindingCounterIds.FlowFieldBytesName));
        Assert.Equal(ids.FlowFieldBudgetBytes.Id, Array.IndexOf(names, PathfindingCounterIds.FlowFieldBudgetBytesName));

        var a = service.FlowField([new(10, 10, 0)], walker, radiusChunks: 1);
        var b = service.FlowField([new(100, 50, 0)], walker, radiusChunks: 1);
        TestWorlds.Set(world, terrain, 11, 10, 0, 1);
        TestWorlds.Set(world, terrain, 101, 50, 0, 1);
        var buffer = world.CreateCommandBuffer();
        buffer.Emit(service.RequestQueue, new PathRequest(default, new TilePos(0, 0, 0), new TilePos(120, 60, 0), walker, 0, 1));
        buffer.Emit(service.RequestQueue, new PathRequest(default, new TilePos(0, 0, 0), new TilePos(120, 0, 0), walker, 0, 2));
        world.Playback(buffer);

        world.Tick(); // intake at End: nothing computed yet this Tick, but the repair pass runs (one Field under a budget of 1)
        var record = world.Diagnostics.Latest.Counters;
        Assert.Equal(0, record[ids.QueueDepth.Id]);
        Assert.Equal(1, record[ids.RepairBacklog.Id]); // one dirty chunk left on the deferred Field
        Assert.Equal(a.Bytes + b.Bytes, record[ids.FlowFieldBytes.Id]);
        Assert.Equal(1 << 20, record[ids.FlowFieldBudgetBytes.Id]);
        Assert.True(record[ids.WorkUnits.Id] > 0, "the repair spent work units");

        world.Tick(); // searches run in Begin; both requests computed, pending until their delivery Tick
        record = world.Diagnostics.Latest.Counters;
        Assert.Equal(2, record[ids.QueueDepth.Id]);
        Assert.Equal(0, record[ids.RepairBacklog.Id]);
        Assert.True(record[ids.WorkUnits.Id] >= service.QueueCounters.WorkUnits, "search work units are in the record");
    }
}
