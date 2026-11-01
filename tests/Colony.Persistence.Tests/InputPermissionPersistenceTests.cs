using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>TASK-35 AC #2 and #3: denied commands stay in the replay log, and permissions travel in the save manifest and replay header.</summary>
public sealed class InputPermissionPersistenceTests
{
    private const int Ticks = 20;

    [Fact]
    public void A_restricted_replay_keeps_denied_commands_and_reproduces_every_checkpoint_on_another_worker_count()
    {
        var scenario = new InputScenario { Restricted = true };
        var (log, hashes) = Record(scenario, workers: 1);
        uint spectator = (uint)(scenario.Sources - 1);

        int spectatorCommands = 0;
        foreach (long tick in Enumerable.Range(0, Ticks))
        {
            var frame = log.Frame(tick);
            for (int i = 0; i < frame.Count; i++)
            {
                spectatorCommands += frame.Record(i).Source == spectator ? 1 : 0;
            }
        }

        Assert.True(spectatorCommands > 0, "denied commands are recorded");
        Assert.False(log.InputPermissions.IsUnrestricted);

        var reopened = ReplayLog.Open(log.Encode());
        Assert.Equal(log.InputPermissions.Describe(), reopened.InputPermissions.Describe());
        using var world = ScenarioWorlds.Create(scenario, workers: 4);
        var player = new ReplayPlayer(reopened, world);
        while (!player.IsFinished)
        {
            player.Tick();
            Assert.Equal(hashes[world.CurrentTick], world.Hash());
        }

        Assert.True(player.VerifiedCheckpoints > 0);
    }

    [Fact]
    public void Replaying_on_a_schema_with_different_permissions_fails_naming_the_difference()
    {
        var (log, _) = Record(new InputScenario { Restricted = true }, workers: 1);
        using var open = ScenarioWorlds.Create(new InputScenario(), workers: 1);

        var error = Assert.Throws<InvalidOperationException>(() => new ReplayPlayer(log, open));
        Assert.Contains("Input permissions", error.Message, StringComparison.Ordinal);
        Assert.Contains("source 3: deny", error.Message, StringComparison.Ordinal);
        Assert.Contains("command 'dismiss': deny", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_save_manifest_records_the_permissions_and_reports_differences_from_another_schema()
    {
        var restricted = new InputScenario { Restricted = true };
        using var world = ScenarioWorlds.Create(restricted, workers: 1);
        restricted.Advance(world);
        var manifest = WorldPersistence.ReadManifest(world.SaveToBytes());

        Assert.Equal(world.Schema.InputPermissions.Describe(), manifest.InputPermissions.Describe());
        Assert.Empty(manifest.InputPermissions.Differences(SavedInputPermissions.Of(world.Schema)));

        var openSchema = ScenarioWorlds.Schema(new InputScenario());
        var reasons = manifest.InputPermissions.Differences(SavedInputPermissions.Of(openSchema));
        Assert.Equal(
            [
                "input permission 'source 3' is deny here and not declared on the other side",
                "input permission 'command 'dismiss'' is deny here and not declared on the other side",
                "input permission 'source 0, command 'dismiss'' is allow here and not declared on the other side",
            ],
            reasons);

        var denyAll = new WorldBuilder();
        InputScenario.ComposeStatic(denyAll);
        denyAll.DefaultInputAccess(InputAccess.Deny);
        Assert.Contains("default input access is allow here and deny on the other side", SavedInputPermissions.Of(openSchema).Differences(SavedInputPermissions.Of(denyAll.Build())));

        // A load still works: permissions are rules, not World state, and the schema hash difference skips hash verification.
        using var loaded = WorldPersistence.Load(world.SaveToBytes(), world.Schema);
        Assert.Equal(world.Hash(), loaded.Hash());
    }

    [Fact]
    public void An_unrestricted_manifest_has_no_permission_block()
    {
        var scenario = new InputScenario();
        using var world = ScenarioWorlds.Create(scenario, workers: 1);
        var manifest = WorldPersistence.ReadManifest(world.SaveToBytes());
        Assert.True(manifest.InputPermissions.IsUnrestricted);
        Assert.Equal(["default allow"], manifest.InputPermissions.Describe());
    }

    private static (ReplayLog Log, ulong[] Hashes) Record(InputScenario scenario, int workers)
    {
        using var world = ScenarioWorlds.Create(scenario, workers);
        var recorder = new ReplayRecorder(world, new ReplayOptions { CheckpointEvery = 5, FullSaveEvery = 10 });
        var hashes = new ulong[Ticks + 1];
        hashes[0] = world.Hash();
        for (int t = 1; t <= Ticks; t++)
        {
            InputScenario.SubmitTick(world, world.CurrentTick, scenario.Seed, scenario.Sources, scenario.CommandsPerTick, concurrent: false);
            Assert.True(recorder.Tick());
            hashes[t] = world.Hash();
        }

        return (recorder.Finish(), hashes);
    }
}
