using Colony.Jobs.Tests.Scenarios;
using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>
/// TASK-14 AC #1, the acceptance that matters most (spec section 6): a World saved at Tick T and loaded
/// into a fresh World of a freshly composed schema, then advanced to T+K, hashes exactly like the unsaved
/// run at T+K, Tick by Tick, on one worker and on several.
/// </summary>
public sealed class ContinuationTests
{
    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in Scenarios.Names)
        {
            data.Add(name, 1);
            data.Add(name, 4);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_world_saved_at_T_and_continued_to_T_plus_K_matches_the_unsaved_run(string scenario, int workers)
    {
        const int T = 6;
        const int K = 8;

        var reference = Scenarios.Create(scenario);
        using var unsaved = ScenarioWorlds.Create(reference, workers);
        var expected = new ulong[T + K + 1];
        expected[0] = unsaved.Hash();
        for (int t = 1; t <= T + K; t++)
        {
            reference.Advance(unsaved);
            expected[t] = unsaved.Hash();
        }

        var first = Scenarios.Create(scenario);
        byte[] bytes;
        using (var world = ScenarioWorlds.Create(first, workers))
        {
            for (int t = 1; t <= T; t++)
            {
                first.Advance(world);
            }

            Assert.Equal(expected[T], world.Hash());
            bytes = world.SaveToBytes();
        }

        var second = Scenarios.Create(scenario);
        using var loaded = WorldPersistence.Load(bytes, ScenarioWorlds.Schema(second), new LoadOptions { WorldOptions = new WorldOptions { WorkerThreads = workers } });
        second.OnLoaded(loaded);
        Assert.Equal(T, loaded.CurrentTick);
        Assert.Equal(expected[T], loaded.Hash());
        for (int t = T + 1; t <= T + K; t++)
        {
            second.Advance(loaded);
            Assert.True(expected[t] == loaded.Hash(), $"{scenario} on {workers} worker(s): the loaded World diverged at Tick {t} (saved at {T}).");
        }
    }
}

/// <summary>The scenarios the persistence tests run: Entities and Systems, Fields, and Relations with Resources and buffers.</summary>
internal static class Scenarios
{
    public static readonly string[] Names = ["colonists", "fields", "claims"];

    public static Testing.Scenario Create(string name) => name switch
    {
        "colonists" => new ColonistHarnessScenario { Colonists = 2_000, Food = 100 },
        "fields" => new FieldScenario(),
        "claims" => new ClaimScenario { Workers = 2_000, Sites = 60 },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown scenario."),
    };
}
