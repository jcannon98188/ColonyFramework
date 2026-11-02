using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #2: dirty chunks repair lazily within the per-Tick budget, the staleness bound forces repair, and the rebuild-vs-incremental threshold is configurable; every path equals a from-scratch build.</summary>
public sealed class FlowRepairTests
{
    private static readonly WorldSize Plane = new(128, 64, 1);

    [Fact]
    public void A_door_toggle_marks_the_chunk_dirty_and_the_next_tick_repairs_the_field_incrementally_to_the_oracle()
    {
        // A new door moves the whole west side: the affected set grows past the default quarter, so raise the threshold to see the incremental path through.
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _, options: new PathfindingOptions { FlowRebuildFraction = 1.0 });
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 40, doorY: 10);
        TilePos[] goals = [new(50, 30, 0)];
        var field = service.FlowField(goals, walker, radiusChunks: 2); // chunks x 1..5, y 0..3
        int westBefore = field.DistanceTo(new TilePos(20, 50, 0));
        Assert.True(westBefore > 0);

        TestWorlds.Set(world, terrain, 40, 50, 0, 0); // a second door, far from the first
        service.Sync();
        Assert.True(field.IsDirty);
        Assert.Equal(1, field.DirtyChunkCount);
        Assert.Equal(westBefore, field.DistanceTo(new TilePos(20, 50, 0))); // not repaired yet: old answer, but...
        Assert.Equal(FlowStepStatus.Step, field.TryStep(new TilePos(41, 50, 0), out var next));
        Assert.NotEqual(1, world.Fields().Accessor(terrain)[next]); // ...never a step into a wall

        world.Tick();

        Assert.False(field.IsDirty);
        Assert.True(field.DistanceTo(new TilePos(20, 50, 0)) < westBefore, "the new door shortens the way");
        AssertMatchesOracle(world, terrain, field);
        var counters = service.FlowCounters;
        Assert.Equal(1, counters.Rebuilds); // the initial build
        Assert.Equal(1, counters.IncrementalRepairs);
    }

    [Fact]
    public void Between_ticks_asking_for_a_dirty_field_returns_it_dirty_and_an_explicit_repair_brings_it_up_to_date()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 40, doorY: 10);
        TilePos[] goals = [new(50, 30, 0)];
        var field = service.FlowField(goals, walker, radiusChunks: 2);

        TestWorlds.Set(world, terrain, 40, 50, 0, 0);
        service.Sync();
        Assert.True(field.IsDirty);
        var again = service.FlowField(goals, walker, radiusChunks: 2);
        Assert.Same(field, again);
        Assert.True(field.IsDirty, "a host read never repairs (it would change what the next Tick sees)");

        service.Repair(field);
        Assert.False(field.IsDirty);
        AssertMatchesOracle(world, terrain, field);
    }

    [Fact]
    public void A_mass_change_past_the_threshold_takes_the_rebuild_path_and_matches_too()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _, options: new PathfindingOptions { FlowRebuildFraction = 0.25 });
        var service = world.Pathfinding();
        TilePos[] goals = [new(64, 32, 0)];
        var field = service.FlowField(goals, walker, radiusChunks: 3);
        var accessor = world.Fields().Accessor(terrain);
        for (int y = 0; y < Plane.Y; y++)
        {
            for (int x = 0; x < Plane.X; x++)
            {
                accessor[x, y, 0] = (byte)(((x * 7) + (y * 13)) % 5 == 0 || x % 9 == 4 ? 1 : 0);
            }
        }

        world.Tick();

        Assert.False(field.IsDirty);
        AssertMatchesOracle(world, terrain, field);
        Assert.Equal(2, service.FlowCounters.Rebuilds);
        Assert.Equal(0, service.FlowCounters.IncrementalRepairs);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Random_toggles_in_a_3d_world_with_stairs_match_the_oracle_on_both_repair_paths(double rebuildFraction)
    {
        var size = new WorldSize(48, 40, 24); // 3 x 3 x 2 chunks with partial edge chunks
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(size);
        var terrain = fields.AddField<byte>("terrain");
        var pathfinding = builder.AddPathfinding(fields, new PathfindingOptions { FullRebuildFraction = 1.0, FlowRebuildFraction = rebuildFraction, FlowRepairBudgetPerTick = long.MaxValue });
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t != 1))
        {
            Vertical = Passability.OfField(terrain, static (byte t) => t == 2),
        }.DependsOn(terrain));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        var service = world.Pathfinding();
        var accessor = world.Fields().Accessor(terrain);
        var random = world.Random.Derive("flow-toggles");
        ulong counter = 0;
        for (int i = 0; i < 6000; i++)
        {
            accessor[random.Int32At(counter++, 0, size.X), random.Int32At(counter++, 0, size.Y), random.Int32At(counter++, 0, size.Z)] = (byte)(random.Int32At(counter++, 0, 6) == 0 ? 2 : 1);
        }

        TilePos[] goals = [new(5, 5, 0), new(40, 30, 12), new(20, 20, 23)];
        var field = service.FlowField(goals, walker, radiusChunks: 1);
        Assert.Equal(size.ChunkCount, field.ChunkCount); // radius 1 around chunks spanning the World covers it
        Assert3d(world, terrain, field);

        int incrementalBefore = 0;
        for (int step = 0; step < 20; step++)
        {
            int toggles = 1 + random.Int32At(counter++, 0, step % 5 == 4 ? 60 : 6);
            for (int i = 0; i < toggles; i++)
            {
                accessor[random.Int32At(counter++, 0, size.X), random.Int32At(counter++, 0, size.Y), random.Int32At(counter++, 0, size.Z)] = (byte)random.Int32At(counter++, 0, 3);
            }

            world.Tick();
            Assert.False(field.IsDirty);
            Assert3d(world, terrain, field);
            incrementalBefore = (int)service.FlowCounters.IncrementalRepairs;
        }

        if (rebuildFraction == 0.0)
        {
            Assert.Equal(0, incrementalBefore);
        }
        else
        {
            Assert.True(incrementalBefore >= 15, $"incremental repairs: {incrementalBefore}");
        }
    }

    [Fact]
    public void A_starved_repair_budget_still_repairs_one_field_per_tick_and_the_staleness_bound_forces_the_rest()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _, options: new PathfindingOptions { FlowRepairBudgetPerTick = 1, FlowStalenessTicks = 3 });
        var service = world.Pathfinding();
        var a = service.FlowField([new(10, 10, 0)], walker, radiusChunks: 0);
        var b = service.FlowField([new(100, 50, 0)], walker, radiusChunks: 0);
        var c = service.FlowField([new(60, 30, 0)], walker, radiusChunks: 0);
        TestWorlds.Set(world, terrain, 11, 10, 0, 1);
        TestWorlds.Set(world, terrain, 101, 50, 0, 1);
        TestWorlds.Set(world, terrain, 61, 30, 0, 1);
        service.Sync();
        Assert.True(a.IsDirty && b.IsDirty && c.IsDirty);

        world.Tick(); // tick 0: one Field (equal recency: the oldest, a) is repaired, two deferred
        Assert.Equal(1, new[] { a, b, c }.Count(f => !f.IsDirty));
        Assert.False(a.IsDirty);
        Assert.Equal(2, service.FlowCounters.RepairsDeferred);
        world.Tick(); // tick 1: b
        Assert.Equal(2, new[] { a, b, c }.Count(f => !f.IsDirty));
        world.Tick(); // tick 2: c, still within the bound and under the budget anyway
        Assert.True(!a.IsDirty && !b.IsDirty && !c.IsDirty);
        Assert.Equal(0, service.FlowCounters.StalenessForced);

        // Five dirty Fields at Tick k: k, k+1, k+2 repair one each under the budget; at k+3 the bound forces the other two in one Tick.
        var d = service.FlowField([new(30, 50, 0)], walker, radiusChunks: 0);
        var e = service.FlowField([new(80, 10, 0)], walker, radiusChunks: 0);
        foreach (var (x, y, value) in new[] { (11, 10, 0), (101, 50, 0), (61, 30, 0), (31, 50, 1), (81, 10, 1) })
        {
            TestWorlds.Set(world, terrain, x, y, 0, (byte)value);
        }

        service.Sync();
        FlowField[] all = [a, b, c, d, e];
        Assert.True(all.All(f => f.IsDirty));
        Assert.Equal(world.CurrentTick, a.DirtySinceTick);
        world.Tick();
        world.Tick();
        world.Tick();
        Assert.Equal(2, all.Count(f => f.IsDirty));
        world.Tick();
        Assert.True(all.All(f => !f.IsDirty), "the staleness bound forced the remaining two in one Tick");
        Assert.Equal(1, service.FlowCounters.StalenessForced); // the second forced one is past the budget the first one spent
    }

    private static void AssertMatchesOracle(World world, Field<byte> terrain, FlowField field)
    {
        var expected = FlowOracle.Expected(world, field.Goals, field.Bounds.Min, field.Bounds.Max, t => world.Fields().Accessor(terrain)[t] == 0);
        Assert.Equal(expected, FlowOracle.Actual(world, field));
    }

    private static void Assert3d(World world, Field<byte> terrain, FlowField field)
    {
        var expected = FlowOracle.Expected(world, field.Goals, field.Bounds.Min, field.Bounds.Max, t => world.Fields().Accessor(terrain)[t] != 1, t => world.Fields().Accessor(terrain)[t] == 2);
        Assert.Equal(expected, FlowOracle.Actual(world, field));
    }
}
