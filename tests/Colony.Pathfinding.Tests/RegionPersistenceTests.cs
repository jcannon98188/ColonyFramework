using Colony.Fields;
using Colony.Pathfinding.Tests.Scenarios;
using Colony.Persistence;

namespace Colony.Pathfinding.Tests;

/// <summary>AC #4: region data is excluded from saves and rebuilt on load; save-load-continue equals never-saved.</summary>
public sealed class RegionPersistenceTests
{
    [Fact]
    public void A_save_holds_only_which_classes_were_built_and_loading_rebuilds_exactly_those()
    {
        using var world = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out var flyer);
        TestWorlds.WallColumn(world, terrain, x: 20);
        world.Pathfinding().EnsureBuilt(walker);
        world.Tick();

        byte[] image = world.SaveToBytes();

        var pathfindingSections = WorldPersistence.SectionNames(image).Where(n => n.Contains("pathfinding", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["ext/pathfinding/built"], pathfindingSections);
        using var loaded = WorldPersistence.Load(image, world.Schema);
        var service = loaded.Pathfinding();
        Assert.True(service.IsBuilt(walker));
        Assert.False(service.IsBuilt(flyer));
        Assert.Equal(RegionOracle.Actual(world, walker), RegionOracle.Actual(loaded, walker));
        Assert.False(service.Reachable(new TilePos(0, 0, 0), new TilePos(63, 0, 0), walker));
    }

    [Fact]
    public void Region_state_does_not_change_the_world_hash()
    {
        using var built = TestWorlds.Create(new WorldSize(64, 32, 1), out var terrain, out var walker, out _);
        using var unbuilt = TestWorlds.Create(new WorldSize(64, 32, 1), out _, out _, out _);
        TestWorlds.WallColumn(built, terrain, x: 20);
        TestWorlds.WallColumn(unbuilt, terrain, x: 20);
        built.Pathfinding().EnsureBuilt(walker);

        Assert.Equal(unbuilt.Hash(), built.Hash());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Save_load_and_continue_hashes_like_the_run_that_never_saved(int workers)
    {
        var scenario = new UnreachableTargetScenario();
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var schema = builder.Build();
        var options = new WorldOptions { Seed = scenario.Seed, WorkerThreads = workers, InitialEntityCapacity = scenario.InitialEntityCapacity };
        using var straight = schema.CreateWorld(options);
        scenario.Populate(straight);
        for (int t = 0; t < 10; t++)
        {
            scenario.Advance(straight);
        }

        byte[] image = straight.SaveToBytes();
        using var resumed = WorldPersistence.Load(image, schema, new LoadOptions { WorldOptions = options });
        scenario.OnLoaded(resumed);
        Assert.True(resumed.Pathfinding().IsBuilt(scenario.Walker));
        for (int t = 0; t < 15; t++)
        {
            scenario.Advance(straight);
            scenario.Advance(resumed);
            Assert.Equal(straight.Hash(), resumed.Hash());
        }
    }
}
