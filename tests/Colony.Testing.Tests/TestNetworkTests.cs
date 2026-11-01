using Colony.Testing;

namespace Colony.Testing.Tests;

/// <summary>TASK-31 AC #1 and #2: the in-memory network, its faults and their reproducibility.</summary>
public sealed class TestNetworkTests
{
    [Fact]
    public void A_perfect_network_delivers_every_payload_once_in_send_order_after_the_latency()
    {
        var network = new TestNetwork(3, new NetworkProfile { Name = "lan", LatencySteps = 2 }, seed: 1);
        network.Send(0, 1, [1, 2, 3]);
        network.Send(2, 1, [4]);
        network.Send(0, 2, [5]);

        Assert.False(network.TryReceive(1, out _));
        network.Advance();
        Assert.False(network.TryReceive(1, out _));
        network.Advance();

        Assert.True(network.TryReceive(1, out var first));
        Assert.Equal(0, first.From);
        Assert.Equal(1, first.To);
        Assert.Equal(0, first.SentAt);
        Assert.Equal(2, first.DeliveredAt);
        Assert.Equal([1, 2, 3], first.Payload.ToArray());
        Assert.True(network.TryReceive(1, out var second));
        Assert.Equal(2, second.From);
        Assert.Equal([4], second.Payload.ToArray());
        Assert.False(network.TryReceive(1, out _));
        Assert.True(network.TryReceive(2, out var third));
        Assert.Equal([5], third.Payload.ToArray());
        Assert.Equal(new NetworkCounters(3, 3, 0, 0, 0), network.Counters);
    }

    [Fact]
    public void The_sender_owns_its_buffer_after_send()
    {
        var network = new TestNetwork(2, NetworkProfile.Perfect, seed: 1);
        byte[] buffer = [7, 7];
        network.Send(0, 1, buffer);
        buffer[0] = 9;
        network.Advance();
        Assert.True(network.TryReceive(1, out var packet));
        Assert.Equal([7, 7], packet.Payload.ToArray());
    }

    [Fact]
    public void Broadcast_reaches_every_other_peer()
    {
        var network = new TestNetwork(4, NetworkProfile.Perfect, seed: 1);
        network.Broadcast(2, [42]);
        network.Advance();
        for (int peer = 0; peer < 4; peer++)
        {
            Assert.Equal(peer != 2, network.TryReceive(peer, out _));
        }
    }

    [Theory]
    [InlineData("lossy")]
    [InlineData("duplicating")]
    [InlineData("reordering")]
    [InlineData("jittery")]
    [InlineData("hostile")]
    public void Every_fault_is_a_function_of_the_seed(string profileName)
    {
        var profile = NetworkProfile.Named(profileName);
        var a = Trace(profile, seed: 11);
        var b = Trace(profile, seed: 11);
        var c = Trace(profile, seed: 12);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Drop_duplicate_and_reorder_rates_show_up_in_the_counters()
    {
        var lossy = Run(NetworkProfile.Lossy);
        Assert.InRange(lossy.Dropped, 150, 450);
        Assert.Equal(lossy.Sent - lossy.Dropped, lossy.Delivered);

        var duplicating = Run(NetworkProfile.Duplicating);
        Assert.InRange(duplicating.Duplicated, 150, 450);
        Assert.Equal(duplicating.Sent + duplicating.Duplicated, duplicating.Delivered);

        var reordering = Run(NetworkProfile.Reordering);
        Assert.InRange(reordering.Reordered, 100, 400);
        Assert.Equal(reordering.Sent, reordering.Delivered);
    }

    [Fact]
    public void Jitter_and_reordering_deliver_packets_out_of_send_order()
    {
        var network = new TestNetwork(2, NetworkProfile.Reordering, seed: 5);
        for (int i = 0; i < 200; i++)
        {
            network.Send(0, 1, [(byte)i]);
            network.Advance();
        }

        for (int i = 0; i < 64; i++)
        {
            network.Advance();
        }

        int previous = -1;
        int inversions = 0;
        int received = 0;
        while (network.TryReceive(1, out var packet))
        {
            received++;
            if (packet.Payload.Span[0] < previous)
            {
                inversions++;
            }

            previous = packet.Payload.Span[0];
        }

        Assert.Equal(200, received);
        Assert.True(inversions > 10, $"only {inversions} inversions");
    }

    [Fact]
    public void Invalid_profiles_and_peers_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestNetwork(2, new NetworkProfile { Name = "x", DropRate = 1.5 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestNetwork(2, new NetworkProfile { Name = "x", LatencySteps = -1 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestNetwork(0, NetworkProfile.Perfect, 0));
        var network = new TestNetwork(2, NetworkProfile.Perfect, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => network.Send(0, 2, [1]));
        Assert.Throws<ArgumentException>(() => network.Send(1, 1, [1]));
        Assert.Throws<ArgumentException>(() => NetworkProfile.Named("nope"));
    }

    private static NetworkCounters Run(NetworkProfile profile)
    {
        var network = new TestNetwork(2, profile, seed: 3);
        for (int i = 0; i < 1000; i++)
        {
            network.Send(0, 1, [(byte)i]);
            network.Advance();
        }

        for (int i = 0; i < 64; i++)
        {
            network.Advance();
        }

        while (network.TryReceive(1, out _))
        {
        }

        return network.Counters;
    }

    private static string Trace(NetworkProfile profile, ulong seed)
    {
        var network = new TestNetwork(3, profile, seed);
        var log = new System.Text.StringBuilder();
        for (int step = 0; step < 300; step++)
        {
            network.Send(step % 3, (step + 1) % 3, [(byte)step]);
            network.Broadcast((step + 2) % 3, [(byte)(step * 7)]);
            network.Advance();
            for (int peer = 0; peer < 3; peer++)
            {
                while (network.TryReceive(peer, out var packet))
                {
                    log.Append(System.Globalization.CultureInfo.InvariantCulture, $"{packet.DeliveredAt}:{packet.From}>{packet.To}={packet.Payload.Span[0]};");
                }
            }
        }

        return log.ToString();
    }
}
