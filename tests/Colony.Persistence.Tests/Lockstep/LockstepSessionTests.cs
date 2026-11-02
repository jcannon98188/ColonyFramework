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
