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
