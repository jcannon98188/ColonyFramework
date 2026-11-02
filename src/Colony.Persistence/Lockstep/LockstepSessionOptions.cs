namespace Colony.Persistence;

/// <summary>
/// Lets a <see cref="LockstepSession"/> change its input delay while it runs, from how its peers' frames arrive.
/// Every <see cref="WindowTicks"/> Ticks: if the session stalled for a missing frame during the window the delay
/// grows by one (up to <see cref="Max"/>); if every frame arrived at least two Ticks before it was needed, it
/// shrinks by one (down to <see cref="Min"/>). The delay is a local choice: frames carry their Tick, so peers
/// with different delays stay in lockstep.
/// </summary>
public sealed record AdaptiveInputDelay
{
    public int Min { get; init; } = 1;

    public int Max { get; init; } = 8;

    public int WindowTicks { get; init; } = 16;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Min, nameof(Min));
        ArgumentOutOfRangeException.ThrowIfLessThan(Max, Min, nameof(Max));
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowTicks, 1, nameof(WindowTicks));
    }
}

/// <summary>Settings of a <see cref="LockstepSession"/>; the values the peers must agree on are checked in the handshake.</summary>
public sealed record LockstepSessionOptions
{
    /// <summary>This peer's seat; it is also its <see cref="InputSource"/>.</summary>
    public int LocalPeer { get; init; }

    /// <summary>Seats in the session (peer <c>i</c> is Input source <c>i</c>). Every peer must agree.</summary>
    public int Peers { get; init; } = 2;

    /// <summary>
    /// Seats whose peers are present from the start and whose handshake the session waits for before its first
    /// Tick. Null means every seat but the local one. A seat left out is absent until its peer's handshake arrives
    /// with a join Tick this peer has not run yet (join-in-progress).
    /// </summary>
    public IReadOnlyList<int>? ActivePeers { get; init; }

    /// <summary>
    /// The first Tick this peer's own frames are required for; -1 means the Tick the World is at when the session
    /// starts. A joiner whose World was loaded from a join package at Tick K and that may only start playing at
    /// Tick J &gt; K passes J: its frames before J are implicitly empty on every peer.
    /// </summary>
    public long JoinTick { get; init; } = -1;

    /// <summary>Ticks between a command's submission and the Tick it takes effect (spec section 7.4). The starting value when <see cref="AdaptiveInputDelay"/> is set.</summary>
    public int InputDelay { get; init; } = 2;

    /// <summary>Null keeps <see cref="InputDelay"/> fixed.</summary>
    public AdaptiveInputDelay? AdaptiveInputDelay { get; init; }

    /// <summary>Peers exchange their World hash after every Nth Tick. Every peer must agree.</summary>
    public int CheckpointEvery { get; init; } = 10;

    /// <summary>Most frames, and most checkpoint hashes, one message carries.</summary>
    public int MaxFramesPerMessage { get; init; } = 32;

    /// <summary>Most Ticks whose frames stay available for resending (and for a joiner catching up) after they ran.</summary>
    public int FrameRetentionTicks { get; init; } = 4096;

    /// <summary>What this peer presents in the handshake; null derives <see cref="PeerIdentity.Of(World, PeerIdentityOptions?)"/> from the World.</summary>
    public PeerIdentity? Identity { get; init; }

    /// <summary>
    /// The World's hash trail (also given as <see cref="Diagnostics.DiagnosticsOptions.Listener"/> with
    /// <see cref="Diagnostics.DiagnosticsOptions.HashTrail"/> on). With it a desync names the first diverging Tick,
    /// Phase and System; without it only the checkpoint Tick.
    /// </summary>
    public LockstepHashTrail? HashTrail { get; init; }

    /// <summary>
    /// Keep the <see cref="World.HashTree"/> of the last N checkpoints so a desync is localised to the chunk
    /// (TASK-32). Zero keeps none; each tree is built at the checkpoint, so it costs a Tick-sized hash pass.
    /// </summary>
    public int HashTreesKept { get; init; }

    /// <summary>The Tick gate the session holds; null uses the World's gate, or creates one. Pass it as <see cref="WorldOptions.TickGate"/>.</summary>
    public TickGate? Gate { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Peers, 1, nameof(Peers));
        if ((uint)LocalPeer >= (uint)Peers)
        {
            throw new ArgumentOutOfRangeException(nameof(LocalPeer), LocalPeer, $"LocalPeer must be a seat in 0..{Peers - 1}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(InputDelay, nameof(InputDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(CheckpointEvery, 1, nameof(CheckpointEvery));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFramesPerMessage, 1, nameof(MaxFramesPerMessage));
        ArgumentOutOfRangeException.ThrowIfLessThan(FrameRetentionTicks, 1, nameof(FrameRetentionTicks));
        ArgumentOutOfRangeException.ThrowIfNegative(HashTreesKept, nameof(HashTreesKept));
        ArgumentOutOfRangeException.ThrowIfLessThan(JoinTick, -1, nameof(JoinTick));
        AdaptiveInputDelay?.Validate();
        if (ActivePeers is not null)
        {
            foreach (int peer in ActivePeers)
            {
                if ((uint)peer >= (uint)Peers)
                {
                    throw new ArgumentOutOfRangeException(nameof(ActivePeers), peer, $"ActivePeers names seat {peer}; the session has {Peers} seats.");
                }
            }
        }
    }
}

/// <summary>Where a <see cref="LockstepSession"/> stands.</summary>
public enum LockstepSessionState
{
    /// <summary>Waiting for an active peer's handshake; no Tick runs yet.</summary>
    Handshaking,

    /// <summary>Every active peer matched; Ticks run when every required frame is held.</summary>
    Running,

    /// <summary>A checkpoint hash disagreed (<see cref="LockstepSession.Desync"/>); the session Ticks no more.</summary>
    Desynced,

    /// <summary>A peer's handshake was refused or a peer fell too far behind to be served (<see cref="LockstepSession.Peer"/>).</summary>
    Failed,
}

/// <summary>Where one seat of a <see cref="LockstepSession"/> stands, from the local peer's point of view.</summary>
public enum LockstepPeerState
{
    /// <summary>No peer sits there yet; its frames are not required.</summary>
    Absent,

    /// <summary>Present from the start; its handshake has not arrived.</summary>
    Expected,

    /// <summary>Handshake matched; its frames are required from <see cref="LockstepPeerStatus.JoinTick"/>.</summary>
    Running,

    /// <summary>Its handshake did not match (<see cref="LockstepPeerStatus.Reasons"/>).</summary>
    Refused,

    /// <summary>It asked for frames or checkpoints this peer no longer retains.</summary>
    Lost,
}
