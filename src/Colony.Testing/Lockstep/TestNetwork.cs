using Colony.Numerics;

namespace Colony.Testing;

/// <summary>One payload as a peer receives it from a <see cref="TestNetwork"/>.</summary>
public readonly record struct NetworkPacket(long Id, int From, int To, long SentAt, long DeliveredAt, bool IsDuplicate, ReadOnlyMemory<byte> Payload);

/// <summary>Running totals of a <see cref="TestNetwork"/>: packets sent, handed to a receiver, lost, copied and held back.</summary>
public readonly record struct NetworkCounters(long Sent, long Delivered, long Dropped, long Duplicated, long Reordered);

/// <summary>
/// A transport-free, in-memory network between N peers (TASK-31): peers <see cref="Send"/> byte spans to each
/// other and <see cref="TryReceive"/> what has arrived; the host moves time forward with <see cref="Advance"/>.
/// Latency, jitter, reordering, drops and duplicates follow the <see cref="NetworkProfile"/>; each decision is
/// drawn from a counter-based stream keyed by the seed and the packet's send index, so the same sends in the
/// same order with the same seed always produce the same deliveries. Single-threaded: drive it from one thread.
/// Use it to test a Game's own lockstep code without opening a socket; <c>LockstepHarness</c> is the
/// reference user.
/// </summary>
public sealed class TestNetwork
{
    private const ulong StreamKey = 0x4E45_5457_4F52_4B31UL; // "NETWORK1"
    private const int DrawsPerSend = 8;

    private readonly RandomSource _random;
    private readonly List<NetworkPacket>[] _queues;
    private readonly int _drop;
    private readonly int _duplicate;
    private readonly int _reorder;
    private long _sends;
    private long _nextId;
    private long _sent;
    private long _delivered;
    private long _dropped;
    private long _duplicated;
    private long _reordered;

    public TestNetwork(int peers, NetworkProfile profile, ulong seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(peers, 1);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        PeerCount = peers;
        Profile = profile;
        Seed = seed;
        _random = new RandomSource(seed, StreamKey);
        _drop = NetworkProfile.PartsPerMillion(profile.DropRate);
        _duplicate = NetworkProfile.PartsPerMillion(profile.DuplicateRate);
        _reorder = NetworkProfile.PartsPerMillion(profile.ReorderRate);
        _queues = new List<NetworkPacket>[peers];
        for (int i = 0; i < peers; i++)
        {
            _queues[i] = [];
        }
    }

    public int PeerCount { get; }

    public NetworkProfile Profile { get; }

    public ulong Seed { get; }

    /// <summary>The current step; packets with <see cref="NetworkPacket.DeliveredAt"/> at or before it can be received.</summary>
    public long Now { get; private set; }

    public NetworkCounters Counters => new(_sent, _delivered, _dropped, _duplicated, _reordered);

    /// <summary>Packets sent (and not dropped) that no receiver has taken yet.</summary>
    public int InFlight
    {
        get
        {
            int count = 0;
            foreach (var queue in _queues)
            {
                count += queue.Count;
            }

            return count;
        }
    }

    /// <summary>Sends a copy of <paramref name="payload"/> from <paramref name="from"/> to <paramref name="to"/>; the caller keeps its buffer.</summary>
    public void Send(int from, int to, ReadOnlySpan<byte> payload)
    {
        CheckPeer(from, nameof(from));
        CheckPeer(to, nameof(to));
        if (from == to)
        {
            throw new ArgumentException("A peer does not send to itself.", nameof(to));
        }

        ulong at = (ulong)_sends++ * DrawsPerSend;
        _sent++;
        if (Chance(at, _drop))
        {
            _dropped++;
            return;
        }

        var bytes = payload.ToArray();
        Enqueue(from, to, bytes, at + 1, isDuplicate: false);
        if (Chance(at + 3, _duplicate))
        {
            _duplicated++;
            Enqueue(from, to, bytes, at + 4, isDuplicate: true);
        }
    }

    /// <summary>Sends <paramref name="payload"/> to every peer but <paramref name="from"/>, in peer order.</summary>
    public void Broadcast(int from, ReadOnlySpan<byte> payload)
    {
        CheckPeer(from, nameof(from));
        for (int to = 0; to < PeerCount; to++)
        {
            if (to != from)
            {
                Send(from, to, payload);
            }
        }
    }

    /// <summary>Takes the next packet that has arrived at <paramref name="peer"/>, by (arrival step, send order).</summary>
    public bool TryReceive(int peer, out NetworkPacket packet)
    {
        CheckPeer(peer, nameof(peer));
        var queue = _queues[peer];
        if (queue.Count > 0 && queue[0].DeliveredAt <= Now)
        {
            packet = queue[0];
            queue.RemoveAt(0);
            _delivered++;
            return true;
        }

        packet = default;
        return false;
    }

    /// <summary>Moves time forward one step.</summary>
    public void Advance() => Now++;

    private void Enqueue(int from, int to, byte[] payload, ulong at, bool isDuplicate)
    {
        long delay = Profile.LatencySteps;
        if (Profile.JitterSteps > 0)
        {
            delay += _random.Int32At(at, 0, Profile.JitterSteps + 1);
        }

        if (Chance(at + 1, _reorder))
        {
            _reordered++;
            delay += Profile.ReorderDelaySteps;
        }

        var packet = new NetworkPacket(_nextId++, from, to, Now, Now + delay, isDuplicate, payload);
        var queue = _queues[to];
        int index = queue.Count;
        while (index > 0 && queue[index - 1].DeliveredAt > packet.DeliveredAt)
        {
            index--;
        }

        queue.Insert(index, packet);
    }

    private bool Chance(ulong counter, int partsPerMillion)
        => partsPerMillion > 0 && (partsPerMillion >= 1_000_000 || _random.Int32At(counter, 0, 1_000_000) < partsPerMillion);

    private void CheckPeer(int peer, string name)
    {
        if ((uint)peer >= (uint)PeerCount)
        {
            throw new ArgumentOutOfRangeException(name, peer, $"The network connects peers 0..{PeerCount - 1}.");
        }
    }
}
