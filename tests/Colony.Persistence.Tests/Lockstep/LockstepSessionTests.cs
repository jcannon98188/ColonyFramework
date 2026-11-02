using Colony.Persistence;
using Colony.Testing;

namespace Colony.Persistence.Tests.Lockstep;

/// <summary>
/// TASK-34 (spec section 7.4): the transport-free lockstep session. Peers exchange input frames, acks and
/// checkpoint hashes as byte spans over the TASK-31 test network; every peer's per-Tick hash must equal a single
/// World fed the same frames directly.
/// </summary>
public sealed class LockstepSessionTests
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
    public void Every_peer_hashes_like_the_oracle_under_each_fault_profile(string profileName, int peers)
    {
        var profile = NetworkProfile.Named(profileName);
        using var rig = new SessionRig(peers, Ticks, profile, networkSeed: 0x31);

        Assert.True(rig.Run(), $"did not complete in {rig.Steps} steps: {string.Join("; ", rig.Peers.Select(p => $"peer {p.Index} ran {p.TicksRun}, {p.Session!.State}"))}");
        var oracle = SessionRig.Oracle(peers, Ticks, inputDelay: 2);
        foreach (var peer in rig.Peers)
        {
            Assert.Equal(oracle, peer.Hashes);
            Assert.Empty(peer.Desyncs);
            for (int other = 0; other < peers; other++)
            {
                Assert.Equal(LockstepPeerState.Running, peer.Session!.Peer(other).State);
            }
        }

        if (profile.DropRate > 0)
        {
            Assert.True(rig.Network.Counters.Dropped > 0);
        }

        if (profile.LatencySteps > 1)
        {
            Assert.Contains(rig.Peers, p => p.Stalls > 0);
        }
    }

    [Fact]
    public void A_peer_with_other_input_permissions_is_refused_with_the_permission_differences()
    {
        var restricted = new Colony.Testing.Tests.Scenarios.InputScenario { Restricted = true, Sources = 2 };
        using var a = new SessionPeer(new Colony.Testing.Tests.Scenarios.InputScenario(), new LockstepSessionOptions { LocalPeer = 0, Peers = 2 }, workers: 1);
        using var b = new SessionPeer(restricted, new LockstepSessionOptions { LocalPeer = 1, Peers = 2 }, workers: 1);

        b.Session!.Receive(a.Session!.NextMessage(1));
        a.Session.Receive(b.Session.NextMessage(0));

        Assert.Equal(LockstepSessionState.Failed, a.Session.State);
        Assert.Equal(LockstepSessionState.Failed, b.Session.State);
        var status = a.Session.Peer(1);
        Assert.Equal(LockstepPeerState.Refused, status.State);
        Assert.True(status.Handshake!.Has(HandshakeMismatchKind.SchemaHash));
        Assert.Contains(status.Reasons, r => r.Contains("Different schema", StringComparison.Ordinal));
        Assert.Contains(status.Reasons, r => r.Contains("input permission 'source 1' is not declared here and deny on the other side", StringComparison.Ordinal));
        Assert.Contains(status.Reasons, r => r.Contains("command 'dismiss'", StringComparison.Ordinal));
        Assert.Contains(b.Session.Peer(0).Reasons, r => r.Contains("input permission 'source 1' is deny here and not declared on the other side", StringComparison.Ordinal));
        Assert.False(a.Session.TryBeginTick());
    }

    [Fact]
    public void A_partitioned_peer_stalls_at_the_shared_gate_and_catches_up_when_the_network_heals()
    {
        using var rig = new SessionRig(peers: 2, Ticks, NetworkProfile.Perfect);
        bool completed = rig.Run(maxSteps: 400, beforeStep: (r, step) =>
        {
            // Steps 10..60: nothing reaches peer 1 (its packets are dropped on the floor).
            if (step is >= 10 and < 60)
            {
                while (r.Network.TryReceive(1, out _))
                {
                }
            }
        });

        Assert.True(completed);
        Assert.True(rig.Peers[1].Stalls >= 40, $"peer 1 stalled {rig.Peers[1].Stalls} times");
        Assert.True(rig.Peers[1].Session!.Gate.IsHeld);
        Assert.Equal(0, rig.Peers[1].Session!.Gate.PendingSteps);
        Assert.Equal(SessionRig.Oracle(2, Ticks, 2), rig.Peers[1].Hashes);
    }

    [Fact]
    public void An_adaptive_input_delay_grows_under_latency_and_shrinks_on_a_fast_network()
    {
        var slow = new NetworkProfile { Name = "slow", LatencySteps = 5 };
        var adaptive = new AdaptiveInputDelay { Min = 1, Max = 8, WindowTicks = 4 };
        using var grow = new SessionRig(peers: 2, ticks: 60, slow, configure: (_, o) => o with { InputDelay = 1, AdaptiveInputDelay = adaptive });
        Assert.True(grow.Run(maxSteps: 4000));
        Assert.All(grow.Peers, p => Assert.True(p.Session!.InputDelay > 1, $"peer {p.Index} delay {p.Session!.InputDelay}"));
        Assert.Equal(grow.Peers[0].Hashes, grow.Peers[1].Hashes);

        using var shrink = new SessionRig(peers: 2, ticks: 60, NetworkProfile.Perfect, configure: (_, o) => o with { InputDelay = 6, AdaptiveInputDelay = adaptive });
        Assert.True(shrink.Run());
        Assert.All(shrink.Peers, p => Assert.True(p.Session!.InputDelay <= 2, $"peer {p.Index} delay {p.Session!.InputDelay}"));
        Assert.Equal(shrink.Peers[0].Hashes, shrink.Peers[1].Hashes);
        Assert.All(shrink.Peers, p => Assert.Empty(p.Desyncs));
    }

    [Fact]
    public void A_desync_names_the_checkpoint_then_the_Tick_Phase_System_and_chunk_once_the_diagnostics_crossed_the_wire()
    {
        using var rig = new SessionRig(peers: 3, Ticks, NetworkProfile.Lossy, configure: (_, o) => o with { HashTreesKept = 2 });
        bool sabotaged = false;
        rig.Peers[2].Sabotage = (peer, tick) =>
        {
            if (tick == 7 && !sabotaged)
            {
                sabotaged = true;
                peer.World.Inputs.Submit(new InputSource(99), new Colony.Testing.Tests.Scenarios.InputScenario.Deposit { Wood = 999, Stone = 1 }, 7);
            }
        };

        Assert.False(rig.Run(maxSteps: 600));
        foreach (var peer in rig.Peers)
        {
            var session = peer.Session!;
            Assert.Equal(LockstepSessionState.Desynced, session.State);
            Assert.False(session.TryBeginTick());
            var desync = Assert.Single(peer.Desyncs);
            Assert.Same(desync, session.Desync);
            Assert.Equal(8, desync.Tick); // CheckpointEvery = 3: checkpoints after Ticks 2, 5, 8...
            Assert.Equal(5, desync.LastAgreedTick);
            Assert.True(peer.Index == 2 || desync.Peer == 2, desync.Describe());
            Assert.NotEmpty(peer.Localised);
            Assert.Equal(new HashTrailDivergence(7, Phase.Input.Id, peer.World.Schema.System("inputs.deposit").Id), desync.Divergence);
            Assert.Equal("Input", desync.Phase);
            Assert.Equal("inputs.deposit", desync.System);
            var localisation = Assert.IsType<HashTreeComparison>(desync.Localisation);
            Assert.False(localisation.Identical);
            Assert.Contains(localisation.Differences, d => d.Group == WorldHashTree.ResourcesGroup && d.Item == "stock");
            Assert.Contains("Tick 7, Phase Input, System inputs.deposit", desync.Describe(), StringComparison.Ordinal);
            Assert.Contains("resources/stock", desync.Describe(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_peer_joining_in_progress_from_a_join_package_is_served_the_frames_since_and_plays_from_its_join_Tick()
    {
        const int Seats = 3;
        using var rig = new SessionRig(Seats, ticks: 40, NetworkProfile.Jittery, configure: (i, o) => o with { ActivePeers = [0, 1], CheckpointEvery = 5 });
        var late = rig.Peers[2];
        late.Dispose(); // seat 2 is empty at the start
        var scenario = new Colony.Testing.Tests.Scenarios.InputScenario();
        SessionPeer? joiner = null;
        JoinPackage? package = null;
        bool completed = rig.Run(maxSteps: 2000, beforeStep: (r, step) =>
        {
            if (package is null && r.Peers[0].TicksRun >= 12)
            {
                package = JoinPackage.Capture(r.Peers[0].World); // between Ticks the session holds no pending commands
            }

            if (joiner is null && package is not null && r.Peers[0].TicksRun >= 16)
            {
                var trail = new LockstepHashTrail();
                var gate = new TickGate();
                var world = package.Join(r.Peers[0].World.Schema, new JoinOptions { WorldOptions = SessionPeer.WorldOptions(2, trail, gate, scenario) });
                joiner = new SessionPeer(world, new LockstepSessionOptions { LocalPeer = 2, Peers = Seats, InputDelay = 2, CheckpointEvery = 5, JoinTick = 24, HashTrail = trail, Gate = gate });
                r.Peers[2] = joiner;
            }
        });

        Assert.True(completed);
        Assert.NotNull(joiner);
        Assert.Equal(package!.EndTick, joiner.Session!.StartTick);
        Assert.Equal(24, joiner.Session.JoinTick);
        Assert.Equal(40 - package.EndTick, joiner.TicksRun);
        var reference = rig.Peers[0].Hashes;
        Assert.Equal(reference.Skip((int)package.EndTick), joiner.Hashes);
        Assert.Equal(reference, rig.Peers[1].Hashes);
        Assert.All(rig.Peers, p => Assert.Empty(p.Desyncs));
        Assert.Equal(24, rig.Peers[0].Session!.Peer(2).JoinTick);
        Assert.Equal(LockstepPeerState.Running, rig.Peers[0].Session!.Peer(2).State);
        // The joiner's commands took effect: its deposits from Tick 24 on changed the shared stock versus a session without it.
        using var without = new SessionRig(2, ticks: 40, NetworkProfile.Perfect, configure: (_, o) => o with { CheckpointEvery = 5 });
        Assert.True(without.Run());
        Assert.Equal(reference.Take(24), without.Peers[0].Hashes.Take(24));
        Assert.NotEqual(reference[24], without.Peers[0].Hashes[24]);
    }

    [Fact]
    public void A_peer_that_joins_at_a_Tick_the_session_already_ran_is_refused()
    {
        using var a = new SessionPeer(new Colony.Testing.Tests.Scenarios.InputScenario(), new LockstepSessionOptions { LocalPeer = 0, Peers = 2, ActivePeers = [] }, workers: 1);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(a.Session!.Tick());
        }

        using var b = new SessionPeer(new Colony.Testing.Tests.Scenarios.InputScenario(), new LockstepSessionOptions { LocalPeer = 1, Peers = 2 }, workers: 1);
        a.Session!.Receive(b.Session!.NextMessage(0));
        var status = a.Session.Peer(1);
        Assert.Equal(LockstepPeerState.Refused, status.State);
        Assert.Contains(status.Reasons, r => r.Contains("joins at Tick 0 but the local peer already ran Tick 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Foreign_or_misaddressed_bytes_are_protocol_errors_and_options_are_validated()
    {
        using var a = new SessionPeer(new Colony.Testing.Tests.Scenarios.InputScenario(), new LockstepSessionOptions { LocalPeer = 0, Peers = 3 }, workers: 1);
        using var b = new SessionPeer(new Colony.Testing.Tests.Scenarios.InputScenario(), new LockstepSessionOptions { LocalPeer = 1, Peers = 3 }, workers: 1);
        var toPeer2 = b.Session!.NextMessage(2).ToArray();
        Assert.Throws<LockstepProtocolException>(() => a.Session!.Receive(toPeer2));
        Assert.Throws<LockstepProtocolException>(() => a.Session!.Receive([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]));
        Assert.Throws<LockstepProtocolException>(() => a.Session!.Receive(toPeer2.AsSpan(0, toPeer2.Length - 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Session!.NextMessage(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Session!.NextMessage(3));
        Assert.Throws<InvalidOperationException>(() => a.Session!.EndTick());

        using var world = BuildWorld();
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockstepSession(world, new LockstepSessionOptions { LocalPeer = 2, Peers = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockstepSession(world, new LockstepSessionOptions { InputDelay = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockstepSession(world, new LockstepSessionOptions { CheckpointEvery = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockstepSession(world, new LockstepSessionOptions { ActivePeers = [5] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockstepSession(world, new LockstepSessionOptions { AdaptiveInputDelay = new AdaptiveInputDelay { Min = 4, Max = 2 } }));
    }

    private static World BuildWorld()
    {
        var builder = new WorldBuilder();
        Colony.Testing.Tests.Scenarios.InputScenario.ComposeStatic(builder);
        return builder.Build().CreateWorld(new WorldOptions { WorkerThreads = 1 });
    }

    [Fact]
    public void Two_peers_over_a_perfect_network_hash_like_the_oracle_every_Tick()
    {
        using var rig = new SessionRig(peers: 2, Ticks, NetworkProfile.Perfect);

        Assert.True(rig.Run(), $"did not complete in {rig.Steps} steps");
        var oracle = SessionRig.Oracle(sources: 2, Ticks, inputDelay: 2);
        foreach (var peer in rig.Peers)
        {
            Assert.Equal(LockstepSessionState.Running, peer.Session!.State);
            Assert.Equal(oracle, peer.Hashes);
            Assert.Empty(peer.Desyncs);
        }
    }
}
