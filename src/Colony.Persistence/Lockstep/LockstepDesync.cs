using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Colony.Persistence;

/// <summary>
/// A peer's checkpoint hash disagreed with this peer's (spec section 7.4). Raised by
/// <see cref="LockstepSession.Desynced"/> as soon as it is detected with the checkpoint Tick; refined by
/// <see cref="LockstepSession.DesyncLocalised"/> when the peer's hash trail (first diverging Tick, Phase and
/// System) and hash tree (TASK-32 localisation) arrive over the wire.
/// </summary>
public sealed class LockstepDesync
{
    internal LockstepDesync(int peer, long tick, ulong? localHash, ulong remoteHash, long lastAgreedTick)
    {
        Peer = peer;
        Tick = tick;
        LocalHash = localHash;
        RemoteHash = remoteHash;
        LastAgreedTick = lastAgreedTick;
    }

    /// <summary>The peer whose checkpoint disagreed.</summary>
    public int Peer { get; }

    /// <summary>The checkpoint Tick (the hash taken after that Tick ran).</summary>
    public long Tick { get; }

    /// <summary>This peer's hash at <see cref="Tick"/>; null when the peer reported the desync before this peer ran the Tick.</summary>
    public ulong? LocalHash { get; internal set; }

    public ulong RemoteHash { get; }

    /// <summary>The last checkpoint Tick both peers agreed on, or -1.</summary>
    public long LastAgreedTick { get; }

    /// <summary>From the two hash trails: the first diverging Tick, Phase and System. Null until the peer's trail arrived (or when a trail is missing).</summary>
    public HashTrailDivergence? Divergence { get; internal set; }

    public string? Phase { get; internal set; }

    /// <summary>Null with <see cref="Divergence"/> set means only the Phase boundary (command playback) differed.</summary>
    public string? System { get; internal set; }

    /// <summary>The hash-tree comparison at <see cref="Tick"/>, when both peers kept the tree.</summary>
    public HashTreeComparison? Localisation { get; internal set; }

    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"desync with peer {Peer} at the Tick {Tick} checkpoint (local {Hex(LocalHash)}, remote 0x{RemoteHash:X16}; last agreed checkpoint {LastAgreedTick})");
        if (Divergence is { } d)
        {
            text.Append(CultureInfo.InvariantCulture, $"; first diverging at Tick {d.Tick}, Phase {Phase}, System {System ?? "(Phase boundary)"}");
        }

        if (Localisation is { } l)
        {
            text.Append("; ").Append(l.Describe());
        }

        return text.ToString();
    }

    private static string Hex(ulong? value) => value is { } v ? "0x" + v.ToString("X16", CultureInfo.InvariantCulture) : "not yet run";
}

/// <summary>One seat of a <see cref="LockstepSession"/> as the local peer sees it.</summary>
public sealed class LockstepPeerStatus
{
    internal LockstepPeerStatus(int peer, LockstepPeerState state)
    {
        Peer = peer;
        State = state;
    }

    public int Peer { get; }

    public LockstepPeerState State { get; internal set; }

    /// <summary>The first Tick the peer's frames are required for (its join Tick), once its handshake arrived.</summary>
    public long JoinTick { get; internal set; } = -1;

    /// <summary>The handshake comparison, once the peer's identity arrived.</summary>
    public HandshakeResult? Handshake { get; internal set; }

    /// <summary>Why the peer was refused or lost; empty otherwise.</summary>
    public ImmutableArray<string> Reasons { get; internal set; } = [];

    /// <summary>The next Tick whose frame from the peer this peer lacks.</summary>
    public long NextMissingFrame { get; internal set; }

    /// <summary>The last checkpoint Tick whose hash the peer and this peer agreed on, or -1.</summary>
    public long LastAgreedCheckpoint { get; internal set; } = -1;

    public string Describe()
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"peer {Peer}: {State}"));
        if (JoinTick >= 0)
        {
            text.Append(CultureInfo.InvariantCulture, $", joins at Tick {JoinTick}");
        }

        foreach (var reason in Reasons)
        {
            text.Append("; ").Append(reason);
        }

        return text.ToString();
    }
}

/// <summary>A message that is not a lockstep message of this session (wrong magic, sender, recipient or truncated).</summary>
public sealed class LockstepProtocolException : Exception
{
    public LockstepProtocolException()
    {
    }

    public LockstepProtocolException(string message)
        : base(message)
    {
    }

    public LockstepProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
