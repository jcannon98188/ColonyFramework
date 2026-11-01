using Colony.Fields;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #2: region ids update incrementally from the change feed and always equal a from-scratch labelling.</summary>
public sealed class RegionUpkeepTests
{
    private static readonly WorldSize Plane = new(64, 32, 1); // 4 x 2 chunks

    [Fact]
    public void Opening_a_door_merges_two_regions_incrementally_with_the_labels_of_a_rebuild()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20);
        Assert.False(service.Reachable(new TilePos(0, 0, 0), new TilePos(63, 31, 0), walker));

        TestWorlds.Set(world, terrain, 20, 5, 0, 0);
        service.Sync();

        Assert.True(service.Reachable(new TilePos(0, 0, 0), new TilePos(63, 31, 0), walker));
        var counters = service.Regions(walker).Counters;
        Assert.Equal(1, counters.IncrementalRelabels);
        Assert.Equal(0, counters.FullRelabels);
        Assert.Equal(1, counters.ChunksRelabelled - Plane.ChunkCount); // only the door's chunk
        AssertMatchesOracleAndRebuild(world, walker, terrain);
    }

    [Fact]
    public void Closing_the_only_door_splits_a_region_at_the_next_tick()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 5);
        Assert.True(service.Reachable(new TilePos(0, 0, 0), new TilePos(63, 31, 0), walker));

        TestWorlds.Set(world, terrain, 20, 5, 0, 1);
        world.Tick();

        Assert.False(service.Reachable(new TilePos(0, 0, 0), new TilePos(63, 31, 0), walker));
        Assert.True(service.Reachable(new TilePos(21, 0, 0), new TilePos(63, 31, 0), walker));
        AssertMatchesOracleAndRebuild(world, walker, terrain);
        Assert.Equal(1, service.Regions(walker).Counters.IncrementalRelabels);
    }

    [Fact]
    public void A_mass_change_takes_the_rebuild_fallback_and_still_matches()
    {
        using var world = TestWorlds.Create(Plane, out var terrain, out var walker, out _);
        var service = world.Pathfinding();
        service.EnsureBuilt(walker);

        var accessor = world.Fields().Accessor(terrain);
        for (int y = 0; y < Plane.Y; y++)
        {
            for (int x = 0; x < Plane.X; x++)
            {
                accessor[x, y, 0] = (byte)(((x * 7) + (y * 13)) % 5 == 0 || x % 9 == 4 ? 1 : 0);
            }
        }

        service.Sync();

        var counters = service.Regions(walker).Counters;
        Assert.Equal(1, counters.FullRelabels);
        Assert.Equal(0, counters.IncrementalRelabels);
        AssertMatchesOracleAndRebuild(world, walker, terrain);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Random_toggles_in_a_3d_world_with_stairs_always_match_the_oracle(int workers)
    {
        var size = new WorldSize(40, 36, 20); // 3 x 3 x 2 chunks with partial edge chunks
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(size);
        var terrain = fields.AddField<byte>("terrain");
        var pathfinding = builder.AddPathfinding(fields, new PathfindingOptions { FullRebuildFraction = 1.0 });
        var walker = pathfinding.AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t != 1))
        {
            Vertical = Passability.OfField(terrain, static (byte t) => t == 2),
        }.DependsOn(terrain));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = workers });
        var service = world.Pathfinding();
        var accessor = world.Fields().Accessor(terrain);
        var random = world.Random.Derive("toggles");
        ulong counter = 0;
        for (int i = 0; i < 9000; i++)
        {
            accessor[random.Int32At(counter++, 0, size.X), random.Int32At(counter++, 0, size.Y), random.Int32At(counter++, 0, size.Z)] = (byte)(random.Int32At(counter++, 0, 8) == 0 ? 2 : 1);
        }

        service.EnsureBuilt(walker);
        AssertMatchesOracle(world, walker, terrain);

        for (int step = 0; step < 25; step++)
        {
            int toggles = 1 + random.Int32At(counter++, 0, step % 5 == 4 ? 400 : 12);
            for (int i = 0; i < toggles; i++)
            {
                accessor[random.Int32At(counter++, 0, size.X), random.Int32At(counter++, 0, size.Y), random.Int32At(counter++, 0, size.Z)] = (byte)random.Int32At(counter++, 0, 3);
            }

            world.Tick();
            AssertMatchesOracle(world, walker, terrain);
        }

        Assert.Equal(0, service.Regions(walker).Counters.FullRelabels);
        Assert.True(service.Regions(walker).Counters.IncrementalRelabels >= 20);
        AssertMatchesOracleAndRebuild(world, walker, terrain);
    }

    [Fact]
    public void Writes_to_fields_the_class_does_not_read_and_writes_that_keep_passability_are_not_relabelled()
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(Plane);
        var terrain = fields.AddField<byte>("terrain");
        var heat = fields.AddField<int>("heat");
        var walker = builder.AddPathfinding(fields).AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
        var service = world.Pathfinding();
        service.EnsureBuilt(walker);
        var before = service.Regions(walker).Counters;

        world.Fields().Accessor(heat)[3, 3, 0] = 7;
        world.Tick();
        var afterHeat = service.Regions(walker).Counters;
        world.Fields().Accessor(terrain)[3, 3, 0] = 9; // a wall...
        world.Fields().Accessor(terrain)[3, 3, 0] = 0; // ...and back: the chunk is written, passability unchanged
        world.Tick();
        var afterTerrain = service.Regions(walker).Counters;

        Assert.Equal(before.ChunksEvaluated, afterHeat.ChunksEvaluated);
        Assert.True(afterTerrain.ChunksEvaluated > afterHeat.ChunksEvaluated); // re-evaluated (at the Tick start and again in Late)...
        Assert.Equal(before.ChunksRelabelled, afterTerrain.ChunksRelabelled); // ...but not relabelled
    }

    [Fact]
    public void A_simulate_system_write_is_applied_by_the_late_system_of_the_same_tick()
    {
        var builder = new WorldBuilder();
        builder.DebugValidation(false);
        var fields = builder.AddFields(Plane);
        var terrain = fields.AddField<byte>("terrain");
        var walker = builder.AddPathfinding(fields).AddMovementClass(new MovementClassDefinition("walker", Passability.OfField(terrain, static (byte t) => t == 0)).DependsOn(terrain));
        bool reachableInLate = true;
        builder.AddSystem(new SystemDefinition
        {
            Name = "close-door",
            Phase = Phase.Simulate,
            Begin = (in SystemContext context) => TestWorlds.Set(context.World, terrain, 20, 5, 0, 1),
        });
        builder.AddSystem(new SystemDefinition
        {
            Name = "probe",
            Phase = Phase.Late,
            After = [PathfindingBuilder.SystemName],
            Begin = (in SystemContext context) => reachableInLate = context.World.Pathfinding().Reachable(new TilePos(0, 0, 0), new TilePos(63, 0, 0), walker),
        });
        using var world = builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 2 });
        TestWorlds.WallColumn(world, terrain, x: 20, doorY: 5);
        world.Pathfinding().EnsureBuilt(walker);

        world.Tick();

        Assert.False(reachableInLate);
    }

    private static void AssertMatchesOracle(World world, MovementClass movementClass, Field<byte> terrain)
    {
        // Plane walkers stand on 0; the 3D walker (Size.Z > 1) stands on anything but walls and climbs stairs (2).
        long[] expected = world.Fields().Size.Z > 1
            ? RegionOracle.Expected(world, t => Read(world, terrain, t) != 1, t => Read(world, terrain, t) == 2)
            : RegionOracle.Expected(world, t => Read(world, terrain, t) == 0);
        Assert.Equal(expected, RegionOracle.Actual(world, movementClass));
    }

    private static void AssertMatchesOracleAndRebuild(World world, MovementClass movementClass, Field<byte> terrain)
    {
        AssertMatchesOracle(world, movementClass, terrain);
        long[] incremental = RegionOracle.Actual(world, movementClass);
        world.Pathfinding().Rebuild(movementClass);
        Assert.Equal(incremental, RegionOracle.Actual(world, movementClass));
    }

    private static byte Read(World world, Field<byte> terrain, TilePos tile) => world.Fields().Accessor(terrain)[tile];
}