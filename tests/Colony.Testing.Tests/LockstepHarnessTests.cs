using Colony.Persistence;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>
/// TASK-31 AC #3 and #4: peers of the input scenario in lockstep over the <see cref="TestNetwork"/>. Every
/// peer's per-Tick hash must equal a single World fed the same frames directly (the oracle), under every fault
/// profile; injected wrong commands and unrecoverable frame losses must surface as a desync naming the first
/// diverging Tick, Phase and System.
/// </summary>
public sealed class LockstepHarnessTests
{
    private const int Ticks = 30;

    public static TheoryData<string, int> ProfilesAndPeers()
    {
        var data = new TheoryData<string, int>();
        foreach (var profile in NetworkProfile.All)
        {
            for (int peers = 2; peers <= 4; peers++)
            {
                data.Add(profile.Name, peers);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ProfilesAndPeers))]
    public void Every_peer_matches_the_oracle_every_Tick_under_each_fault_profile(string profileName, int peers)
    {
        var profile = NetworkProfile.Named(profileName);
        var result = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions
        {
            Peers = peers,
            Ticks = Ticks,
            Profile = profile,
            NetworkSeed = 0x31,
            PeerWorkers = [1, 2, 3],
        });

        result.ThrowIfFailed();
        Assert.True(result.Passed, result.Describe());
        Assert.Null(result.Desync);
        Assert.Null(result.FirstDivergingTick);
        var oracle = LockstepScript.Oracle(peers, Ticks);
        foreach (var peer in result.Peers)
        {
            Assert.Equal(Ticks, peer.TicksRun);
            Assert.Equal(oracle, peer.Hashes);
        }

        if (profile.DropRate > 0)
        {
            Assert.True(result.Network.Dropped > 0, result.Describe());
        }

        if (profile.DuplicateRate > 0)
        {
            Assert.True(result.Network.Duplicated > 0, result.Describe());
        }

        if (profile.ReorderRate > 0)
        {
            Assert.True(result.Network.Reordered > 0, result.Describe());
        }

        if (profile.LatencySteps > 0)
        {
            // Latency longer than the input delay covers makes peers wait: stalls are allowed, divergence is not.
            Assert.Contains(result.Peers, p => p.StallSteps > 0);
        }
    }

    [Fact]
    public void A_failing_run_reproduces_step_for_step_from_its_seed()
    {
        var options = new LockstepOptions { Peers = 3, Ticks = 20, Profile = NetworkProfile.Hostile, NetworkSeed = 77 };
        var a = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, options);
        var b = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, options);
        var c = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, options with { NetworkSeed = 78 });

        Assert.True(a.Passed, a.Describe());
        Assert.Equal(a.Steps, b.Steps);
        Assert.Equal(a.Network, b.Network);
        Assert.Equal(a.Peers.Select(p => p.StallSteps), b.Peers.Select(p => p.StallSteps));
        Assert.NotEqual(a.Network, c.Network);
        Assert.Equal(a.Peers[0].Hashes, c.Peers[0].Hashes);
    }

    [Fact]
    public void A_wrong_command_is_reported_as_a_desync_at_its_Tick_Phase_and_System()
    {
        var result = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions
        {
            Peers = 3,
            Ticks = Ticks,
            Profile = NetworkProfile.Lan,
            Faults = [LockstepFault.WrongCommand(peer: 2, tick: 7, source: 1, index: 0, new InputScenario.Deposit { Wood = 999, Stone = 1 })],
        });

        Assert.False(result.Passed);
        Assert.Equal(1, result.FaultsApplied);
        var desync = Assert.IsType<LockstepDesync>(result.Desync);
        Assert.Equal(7, desync.Tick);
        Assert.Equal("Input", desync.Phase);
        Assert.Equal("inputs.deposit", desync.System);
        Assert.True(desync.DetectedBy == 2 || desync.Peer == 2, desync.Describe());
        Assert.True(desync.DetectedAtTick >= 7);
        Assert.Equal(7, result.FirstDivergingTick);
        Assert.Contains("Tick 7, Phase Input, System inputs.deposit", result.Describe(), StringComparison.Ordinal);
        Assert.Throws<HarnessException>(result.ThrowIfFailed);
    }

    [Fact]
    public void A_frame_lost_for_good_stalls_the_peer_when_it_waits_for_it()
    {
        var result = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions
        {
            Peers = 2,
            Ticks = Ticks,
            Profile = NetworkProfile.Perfect,
            MaxSteps = 400,
            Faults = [LockstepFault.LoseFrame(peer: 1, tick: 4, source: 0)],
        });

        Assert.False(result.Passed);
        Assert.True(result.Stalled, result.Describe());
        Assert.Null(result.Desync);
        Assert.Equal(4, result.Peers[1].TicksRun);
        Assert.Contains("stalled", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_frame_a_peer_gives_up_on_is_reported_as_a_desync()
    {
        var result = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions
        {
            Peers = 2,
            Ticks = Ticks,
            Profile = NetworkProfile.Lossy,
            StallTimeoutSteps = 30,
            Faults = [LockstepFault.LoseFrame(peer: 1, tick: 4, source: 0)],
        });

        var desync = Assert.IsType<LockstepDesync>(result.Desync);
        Assert.Equal(4, desync.Tick);
        Assert.Equal("Input", desync.Phase);
        Assert.Equal("inputs.deposit", desync.System);
        Assert.Equal(1, result.Peers[1].ProceededWithoutFrames);
    }

    [Fact]
    public void Without_the_hash_trail_a_desync_names_the_Tick_only()
    {
        var result = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions
        {
            Peers = 2,
            Ticks = Ticks,
            HashTrail = false,
            Faults = [LockstepFault.WrongCommand(peer: 0, tick: 3, source: 0, index: 0, new InputScenario.Deposit { Wood = 5 })],
        });

        var desync = Assert.IsType<LockstepDesync>(result.Desync);
        Assert.Equal(3, desync.Tick);
        Assert.Null(desync.Phase);
        Assert.Null(desync.System);
    }

    [Fact]
    public void A_replaced_frame_feeds_the_given_bytes()
    {
        var schema = BuildSchema();
        var writer = new InputFrameWriter(schema, new InputSource(0), firstSequence: 2);
        // Source 0's real Tick-1 frame is one deposit with sequence 2; the replacement is a spawn instead.
        writer.Add(new InputScenario.Spawn { Count = 1 });
        var result = LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions
        {
            Peers = 2,
            Ticks = 10,
            Faults = [LockstepFault.ReplaceFrame(peer: 1, tick: 1, source: 0, writer.ToFrame())],
        });

        var desync = Assert.IsType<LockstepDesync>(result.Desync);
        Assert.Equal(1, desync.Tick);
        Assert.Equal("inputs.spawn", desync.System);
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions { Peers = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions { InputDelay = -1 }));
        Assert.Throws<ArgumentException>(() => LockstepHarness.Run(new InputScenario(), LockstepScript.Script, new LockstepOptions { Faults = [LockstepFault.LoseFrame(0, 1, 0)] }));
    }

    private static WorldSchema BuildSchema()
    {
        var builder = new WorldBuilder();
        InputScenario.ComposeStatic(builder);
        return builder.Build();
    }
}

/// <summary>A simple, fully predictable input script for the lockstep tests, and the single-World oracle.</summary>
internal static class LockstepScript
{
    /// <summary>Every Tick a deposit; every third Tick also a spawn; every fifth a dismiss.</summary>
    public static void Script(World world, InputFrameWriter frame, long tick)
    {
        uint source = frame.Source.Value;
        frame.Add(new InputScenario.Deposit { Wood = (int)(tick + source), Stone = 1 });
        if (tick % 3 == 0)
        {
            frame.Add(new InputScenario.Spawn { Count = 2 + (int)source });
        }

        if (tick % 5 == 4)
        {
            frame.Add(new InputScenario.Dismiss { Count = 1 });
        }
    }

    /// <summary>One World, every source's frame submitted directly before each Tick: the hashes every peer must reproduce.</summary>
    public static ulong[] Oracle(int sources, int ticks)
    {
        var builder = new WorldBuilder();
        var scenario = new InputScenario();
        scenario.Compose(builder);
        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 1, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        var writers = new InputFrameWriter[sources];
        for (int s = 0; s < sources; s++)
        {
            writers[s] = new InputFrameWriter(world.Schema, new InputSource((uint)s));
        }

        var hashes = new ulong[ticks];
        for (int t = 0; t < ticks; t++)
        {
            for (int s = 0; s < sources; s++)
            {
                Script(world, writers[s], t);
                InputFrame.Submit(world, writers[s].ToFrame(), t);
            }

            world.Tick();
            hashes[t] = world.Hash();
        }

        return hashes;
    }
}
