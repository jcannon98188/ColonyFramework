using System.Runtime.InteropServices;

namespace Colony.Testing;

/// <summary>
/// Writes one peer's Input Commands for one Tick into <paramref name="frame"/> (whose <see cref="InputFrameWriter.Source"/>
/// is the peer). Called once per peer per Tick in Tick order, <see cref="LockstepOptions.InputDelay"/> Ticks before
/// <paramref name="tick"/> runs, with the peer's own World (which may be read, never written).
/// </summary>
public delegate void LockstepInputScript(World world, InputFrameWriter frame, long tick);

/// <summary>Settings of a <see cref="LockstepHarness"/> run.</summary>
public sealed record LockstepOptions
{
    /// <summary>Peers in the session; peer <c>i</c> is <see cref="InputSource"/> <c>i</c>.</summary>
    public int Peers { get; init; } = 2;

    /// <summary>Ticks every peer runs.</summary>
    public int Ticks { get; init; } = 30;

    /// <summary>Ticks between the step a peer writes its commands and the Tick they take effect (spec section 7.4).</summary>
    public int InputDelay { get; init; } = 2;

    /// <summary>Peers exchange their World hash after every Nth Tick (and after the last one).</summary>
    public int CheckpointEvery { get; init; } = 1;

    public NetworkProfile Profile { get; init; } = NetworkProfile.Perfect;

    /// <summary>Seed of the network's fault stream: with the scenario and these options it reproduces a run step for step.</summary>
    public ulong NetworkSeed { get; init; }

    /// <summary>Worker threads per peer, cycled: peer <c>i</c> gets <c>PeerWorkers[i % Count]</c>.</summary>
    public IReadOnlyList<int> PeerWorkers { get; init; } = [1, 2];

    /// <summary>Record every peer's hash trail so a desync names its first diverging Phase and System (slower Ticks).</summary>
    public bool HashTrail { get; init; } = true;

    /// <summary>
    /// Steps a peer waits for missing frames before it gives up and runs the Tick as if those sources sent
    /// nothing (a naive policy, there to show what an unrecoverable loss does). Zero waits forever.
    /// </summary>
    public int StallTimeoutSteps { get; init; }

    /// <summary>Network steps before the run is declared stalled; zero derives <c>64 * Ticks + 256</c>.</summary>
    public int MaxSteps { get; init; }

    /// <summary>Most frames, and most checkpoint hashes, a packet carries.</summary>
    public int MaxFramesPerPacket { get; init; } = 32;

    public IReadOnlyList<LockstepFault> Faults { get; init; } = [];

    internal int EffectiveMaxSteps => MaxSteps > 0 ? MaxSteps : (64 * Ticks) + 256;

    internal bool IsCheckpoint(long tick) => (tick + 1) % CheckpointEvery == 0 || tick == Ticks - 1;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Peers, 1, nameof(Peers));
        ArgumentOutOfRangeException.ThrowIfLessThan(Ticks, 1, nameof(Ticks));
        ArgumentOutOfRangeException.ThrowIfNegative(InputDelay, nameof(InputDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(CheckpointEvery, 1, nameof(CheckpointEvery));
        ArgumentOutOfRangeException.ThrowIfNegative(StallTimeoutSteps, nameof(StallTimeoutSteps));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxSteps, nameof(MaxSteps));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFramesPerPacket, 1, nameof(MaxFramesPerPacket));
        ArgumentNullException.ThrowIfNull(Profile, nameof(Profile));
        Profile.Validate();
        ArgumentNullException.ThrowIfNull(PeerWorkers, nameof(PeerWorkers));
        if (PeerWorkers.Count == 0 || PeerWorkers.Any(w => w < 0))
        {
            throw new ArgumentException("PeerWorkers needs at least one non-negative worker count.", nameof(PeerWorkers));
        }

        ArgumentNullException.ThrowIfNull(Faults, nameof(Faults));
        foreach (var fault in Faults)
        {
            fault.Validate(this);
        }
    }
}

/// <summary>What a <see cref="LockstepFault"/> does to the frame it targets.</summary>
public enum LockstepFaultKind
{
    /// <summary>The frame never reaches the peer: it is stripped from every packet the peer receives.</summary>
    LoseFrame,

    /// <summary>The peer feeds its World other bytes in place of the frame.</summary>
    ReplaceFrame,

    /// <summary>The peer feeds its World one command changed (same source and sequence, other Input Command or payload).</summary>
    WrongCommand,
}

/// <summary>
/// A fault injected above the network, at one peer's view of one source's frame for one Tick (TASK-31 AC #4):
/// losses the retransmission cannot recover and corrupted commands. Network-level faults are the
/// <see cref="NetworkProfile"/>'s business.
/// </summary>
public sealed class LockstepFault
{
    private readonly Func<WorldSchema, InputCommandType>? _command;
    private readonly byte[]? _bytes;

    private LockstepFault(LockstepFaultKind kind, int peer, long tick, int source, int index, byte[]? bytes, Func<WorldSchema, InputCommandType>? command)
    {
        Kind = kind;
        Peer = peer;
        Tick = tick;
        Source = source;
        Index = index;
        _bytes = bytes;
        _command = command;
    }

    public LockstepFaultKind Kind { get; }

    /// <summary>The peer whose view is faulted.</summary>
    public int Peer { get; }

    public long Tick { get; }

    /// <summary>The source (peer) whose frame is faulted.</summary>
    public int Source { get; }

    /// <summary>For <see cref="LockstepFaultKind.WrongCommand"/>, the entry replaced.</summary>
    public int Index { get; }

    /// <summary><paramref name="peer"/> never receives <paramref name="source"/>'s frame for <paramref name="tick"/>.</summary>
    public static LockstepFault LoseFrame(int peer, long tick, int source)
    {
        if (peer == source)
        {
            throw new ArgumentException("A peer always holds its own frames; lose a remote source's frame.", nameof(source));
        }

        return new LockstepFault(LockstepFaultKind.LoseFrame, peer, tick, source, 0, null, null);
    }

    /// <summary><paramref name="peer"/> feeds <paramref name="frame"/> (wire form) in place of <paramref name="source"/>'s frame for <paramref name="tick"/>.</summary>
    public static LockstepFault ReplaceFrame(int peer, long tick, int source, ReadOnlySpan<byte> frame)
        => new(LockstepFaultKind.ReplaceFrame, peer, tick, source, 0, frame.ToArray(), null);

    /// <summary><paramref name="peer"/> feeds <paramref name="command"/> in place of entry <paramref name="index"/> of <paramref name="source"/>'s frame for <paramref name="tick"/>.</summary>
    public static LockstepFault WrongCommand<T>(int peer, long tick, int source, int index, in T command) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var payload = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in command)).ToArray();
        return new LockstepFault(LockstepFaultKind.WrongCommand, peer, tick, source, index, payload, static schema => schema.InputCommand<T>());
    }

    internal void Validate(LockstepOptions options)
    {
        if ((uint)Peer >= (uint)options.Peers || (uint)Source >= (uint)options.Peers)
        {
            throw new ArgumentException($"Fault {Kind} names peer {Peer} / source {Source}; the session has {options.Peers} peers.", nameof(options));
        }

        if (Tick < 0 || Tick >= options.Ticks)
        {
            throw new ArgumentException($"Fault {Kind} names Tick {Tick}; the session runs Ticks 0..{options.Ticks - 1}.", nameof(options));
        }
    }

    /// <summary>The frame the peer feeds instead of <paramref name="frame"/>, or null when the fault does not apply to it.</summary>
    internal byte[]? Apply(byte[] frame, WorldSchema schema)
    {
        if (Kind == LockstepFaultKind.ReplaceFrame)
        {
            return _bytes;
        }

        var view = new Persistence.InputFrameView(frame);
        if (Index >= view.Count)
        {
            return null;
        }

        var writer = new InputFrameWriter(schema, new InputSource((uint)Source));
        for (int i = 0; i < view.Count; i++)
        {
            var (source, sequence, type) = view.Record(i);
            if (i == Index)
            {
                writer.AddRaw(source, sequence, _command!(schema).Id, _bytes);
            }
            else
            {
                writer.AddRaw(source, sequence, type, view.Payload(i));
            }
        }

        return writer.ToFrame();
    }
}
