using Colony.Persistence;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>TASK-14 AC #1: a World saved at Tick T loads into a fresh World that hashes alike and continues identically.</summary>
public sealed class RoundTripTests
{
    [Fact]
    public void A_saved_colonist_world_loads_with_the_same_hash()
    {
        var scenario = new ColonistHarnessScenario { Colonists = 2_000, Food = 100 };
        using var world = ScenarioWorlds.Create(scenario);
        for (int i = 0; i < 5; i++)
        {
            world.Tick();
        }

        byte[] bytes = WorldPersistence.SaveToBytes(world);
        using var loaded = WorldPersistence.Load(bytes, ScenarioWorlds.Schema(scenario));

        Assert.Equal(world.CurrentTick, loaded.CurrentTick);
        Assert.Equal(world.Hash(), loaded.Hash());
    }
}

internal static class ScenarioWorlds
{
    public static WorldSchema Schema(Testing.Scenario scenario)
    {
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        builder.DebugValidation(false);
        return builder.Build();
    }

    public static World Create(Testing.Scenario scenario, int workers = 1)
    {
        var world = Schema(scenario).CreateWorld(new WorldOptions
        {
            Seed = scenario.Seed,
            WorkerThreads = workers,
            InitialEntityCapacity = scenario.InitialEntityCapacity,
        });
        scenario.Populate(world);
        return world;
    }
}
