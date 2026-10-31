using Colony.Persistence;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests;

/// <summary>TASK-15 AC #2 and #3 (spec section 7.2): record, verified playback, seek via full-save checkpoints, fork to live input.</summary>
public sealed class ReplayTests
{
    private const int Ticks = 30;

    private static readonly ReplayOptions Options = new() { CheckpointEvery = 5, FullSaveEvery = 10 };

    [Fact]
    public void A_recorded_replay_played_back_on_a_fresh_world_reproduces_every_checkpoint_hash()
    {
        var (log, hashes) = Record(workers: 2);

        Assert.Equal(0, log.StartTick);
        Assert.Equal(Ticks, log.EndTick);
        Assert.Equal(Ticks, log.FrameCount); // every Tick had input
        Assert.Equal([0, 5, 10, 15, 20, 25, 30], log.CheckpointTicks.ToArray());
        Assert.Equal([0, 10, 20, 30], log.FullSaveTicks.ToArray());
        Assert.Equal(3, log.InputCommands.Length);
        Assert.True(log.TryGetCheckpoint(10, out ulong at10));
        Assert.Equal(hashes[10], at10);

        var reopened = ReplayLog.Open(log.Encode());
        var scenario = new InputScenario();
        using var world = ScenarioWorlds.Create(scenario, workers: 4);
        var player = new ReplayPlayer(reopened, world);
        while (!player.IsFinished)
        {
            player.Tick();
            Assert.Equal(hashes[world.CurrentTick], world.Hash());
        }

        Assert.Equal(Ticks, world.CurrentTick);
        Assert.Equal(7, player.VerifiedCheckpoints);
        Assert.Equal(0, world.Inputs.PendingCount);
    }

    [Fact]
    public void An_altered_input_fails_verification_at_the_first_checkpoint_after_it()
    {
        var (log, _) = Record(workers: 1);
        var altered = ReplayLog.Open(TestReplays.FlipPayloadByte(log, tick: 7));

        var scenario = new InputScenario();
        using var world = ScenarioWorlds.Create(scenario, workers: 1);
        var player = new ReplayPlayer(altered, world);
        player.SeekTo(5); // the altered frame is at Tick 7; the checkpoint at 5 still matches

        var e = Assert.Throws<ReplayDivergenceException>(player.RunToEnd);
        Assert.Equal(10, e.Tick);
        Assert.Equal(10, world.CurrentTick);
        Assert.NotEqual(e.Expected, e.Actual);
    }

    [Fact]
    public void Seeking_loads_the_nearest_full_save_and_plays_forward_then_a_fork_continues_deterministically()
    {
        var (log, hashes) = Record(workers: 2);
        var scenario = new InputScenario();

        // Seek to 23: load the full save at 20 and play Ticks 20..22 from the log.
        using var world = log.CreateWorld(23, ScenarioWorlds.Schema(scenario), new LoadOptions { WorldOptions = new WorldOptions { WorkerThreads = 4 } });
        Assert.Equal(20, world.CurrentTick);
        Assert.Equal(hashes[20], world.Hash());
        var player = new ReplayPlayer(log, world);
        player.SeekTo(23);
        Assert.Equal(23, world.CurrentTick);
        Assert.Equal(hashes[23], world.Hash());

        // Fork to live input: the same scripted commands the recording received keep the World on the recorded path...
        var fork = player.Fork(new ReplayOptions { CheckpointEvery = 1 });
        Assert.Same(world, fork.World);
        for (long tick = 23; tick < Ticks; tick++)
        {
            InputScenario.SubmitTick(world, tick, scenario.Seed, scenario.Sources, scenario.CommandsPerTick, concurrent: true);
            Assert.True(fork.Tick());
            Assert.Equal(hashes[world.CurrentTick], world.Hash());
        }

        // ...and the forked log is a complete, verifiable replay from Tick 0.
        var forked = fork.Finish();
        Assert.Equal(0, forked.StartTick);
        Assert.Equal(Ticks, forked.EndTick);
        Assert.Equal(Ticks, forked.FrameCount);
        using var fresh = ScenarioWorlds.Create(scenario, workers: 1);
        var verify = new ReplayPlayer(forked, fresh);
        verify.RunToEnd();
        Assert.Equal(hashes[Ticks], fresh.Hash());
        Assert.Equal(forked.CheckpointTicks.Length, verify.VerifiedCheckpoints);

        // Diverging live input after a fork is simply a different future, recorded as such.
        using var other = log.CreateWorld(23, ScenarioWorlds.Schema(scenario));
        var otherPlayer = new ReplayPlayer(log, other);
        otherPlayer.SeekTo(23);
        var otherFork = otherPlayer.Fork();
        other.Inputs.Submit(new InputSource(0), new InputScenario.Spawn { Count = 2 });
        otherFork.Tick();
        Assert.NotEqual(hashes[24], other.Hash());
        Assert.Equal(1, otherFork.Finish().Frame(23).Count);
    }

    [Fact]
    public void A_replay_log_is_a_save_container_with_the_replay_sections_and_round_trips_through_a_file()
    {
        var (log, _) = Record(workers: 1);
        string path = Path.Combine(Path.GetTempPath(), "colony-replay-" + Guid.NewGuid().ToString("N") + ".clnyreplay");
        try
        {
            log.WriteFile(path);
            var names = WorldPersistence.SectionNames(File.ReadAllBytes(path));
            Assert.Equal("replay", names[0]);
            Assert.Contains("frames", names);
            Assert.Contains("checkpoints", names);
            Assert.Contains("save/0", names);
            Assert.Contains("save/30", names);

            var reopened = ReplayLog.Open(path);
            Assert.Equal(log.Seed, reopened.Seed);
            Assert.Equal(log.SchemaHash, reopened.SchemaHash);
            Assert.Equal(log.EndTick, reopened.EndTick);
            Assert.Equal(log.Encode(), reopened.Encode());
            Assert.Throws<SaveFormatException>(() => WorldPersistence.ReadManifest(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Records the scenario for <see cref="Ticks"/> Ticks; returns the log and the World hash after every Tick (index = Tick).</summary>
    private static (ReplayLog Log, ulong[] Hashes) Record(int workers)
    {
        var scenario = new InputScenario { Concurrent = true };
        using var world = ScenarioWorlds.Create(scenario, workers);
        var recorder = new ReplayRecorder(world, Options);
        var hashes = new ulong[Ticks + 1];
        hashes[0] = world.Hash();
        for (int t = 1; t <= Ticks; t++)
        {
            InputScenario.SubmitTick(world, world.CurrentTick, scenario.Seed, scenario.Sources, scenario.CommandsPerTick, concurrent: true);
            Assert.True(recorder.Tick());
            hashes[t] = world.Hash();
        }

        return (recorder.Finish(), hashes);
    }
}

internal static class TestReplays
{
    /// <summary>Flips one payload byte of the first command in the frame recorded for <paramref name="tick"/> and re-encodes the log.</summary>
    public static byte[] FlipPayloadByte(ReplayLog log, long tick)
    {
        var frame = log.Frame(tick);
        Assert.NotEqual(0, frame.Count);
        var payload = frame.Payload(0).ToArray();
        payload[0] ^= 0x01;
        var builder = log.ToBuilder();
        builder.ReplaceCommand(tick, 0, payload);
        return builder.Encode();
    }
}
