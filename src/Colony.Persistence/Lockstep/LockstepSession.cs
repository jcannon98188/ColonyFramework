using System.Collections.Immutable;
using System.Globalization;

namespace Colony.Persistence;

/// <summary>
/// The lockstep state machine of spec section 7.4, without a transport: a Game feeds it the bytes its peers sent
/// (<see cref="Receive"/>) and sends the bytes it produces (<see cref="NextMessage"/>); the session does the rest.
/// Local commands (<see cref="Submit{T}"/>) go into the frame for <see cref="OpenInputTick"/> (the next Tick plus
/// the input delay, fixed or <see cref="AdaptiveInputDelay"/>); every peer's frame for a Tick is required before
/// that Tick runs: <see cref="TryBeginTick"/> submits them all to the World and lets one Tick through the shared
/// <see cref="Gate"/>, or reports a stall. Frames and checkpoint hashes are acknowledged cumulatively and resent
/// until acknowledged, missing frames are requested explicitly; after every <see cref="LockstepSessionOptions.CheckpointEvery"/>
/// Ticks the World hash is exchanged and a disagreement raises <see cref="Desynced"/>, then
/// <see cref="DesyncLocalised"/> once the peers' hash trails and hash trees (TASK-32) have crossed the wire.
/// The handshake (<see cref="PeerHandshake"/>) runs over the same messages; a peer whose identity, session
/// parameters, Input permissions or start state differ is refused with the reasons. A peer may join in progress:
/// its handshake names the Tick from which its frames count, and this peer serves it the frames it retains.
/// No sockets, threads or clocks: every call runs on the caller's thread, between Ticks of the World.
/// </summary>
public sealed class LockstepSession : IDisposable
{
    private const int MaxMissingRequests = 16;

    private readonly World _world;
    private readonly LockstepSessionOptions _options;
    private readonly Slot[] _slots;
    private readonly InputFrameWriter _writer;
    private readonly TickTable<byte[]> _ownFrames;
    private readonly TickTable<ulong> _ownHashes;
    private readonly TickTable<WorldHashTree>? _ownTrees;
    private readonly LockstepHashTrail? _trail;
    private readonly PeerIdentity _identity;
    private readonly SavedInputPermissions _permissions;
    private readonly byte[] _identityBytes;
    private readonly byte[] _permissionBytes;
    private readonly ulong _startHash;
    private readonly ByteWriter _out = new(1024);
    private readonly LockstepMessage.Decoded _in = new();
    private long _nextTick;
    private long _open;
    private int _delay;
    private bool _inTick;
    private int _windowTicks;
    private bool _windowStalled;
    private long _windowMinSlack = long.MaxValue;
    private bool _disposed;

    public LockstepSession(World world, LockstepSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _world = world;
        _options = options;
        LocalPeer = options.LocalPeer;
        Peers = options.Peers;
        StartTick = world.CurrentTick;
        _nextTick = StartTick;
        JoinTick = options.JoinTick < 0 ? StartTick : options.JoinTick;
        if (JoinTick < StartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.JoinTick, $"JoinTick {options.JoinTick} is before the World's Tick {StartTick}.");
        }

        _delay = options.InputDelay;
        if (options.AdaptiveInputDelay is { } adaptive)
        {
            _delay = Math.Clamp(_delay, adaptive.Min, adaptive.Max);
        }

        _trail = options.HashTrail;
        _identity = options.Identity ?? PeerIdentity.Of(world);
        _identityBytes = _identity.Encode();
        _permissions = SavedInputPermissions.Of(world.Schema);
        var permissionWriter = new ByteWriter(64);
        _permissions.Write(permissionWriter);
        _permissionBytes = permissionWriter.ToArray();
        _startHash = world.Hash();
        Gate = options.Gate ?? world.TickGate ?? new TickGate();
        Gate.Hold();

        _writer = new InputFrameWriter(world.Schema, new InputSource((uint)LocalPeer));
        _ownFrames = new TickTable<byte[]>(JoinTick);
        _ownHashes = new TickTable<ulong>(StartTick);
        _ownTrees = options.HashTreesKept > 0 ? new TickTable<WorldHashTree>(StartTick) : null;

        _slots = new Slot[Peers];
        for (int i = 0; i < Peers; i++)
        {
            var state = i == LocalPeer ? LockstepPeerState.Running : options.ActivePeers is null || options.ActivePeers.Contains(i) ? LockstepPeerState.Expected : LockstepPeerState.Absent;
            _slots[i] = new Slot(i, state, StartTick);
        }

        _slots[LocalPeer].Status.JoinTick = JoinTick;
        _open = JoinTick - 1;
        CloseFramesThrough(OpenTickFrom(_nextTick) - 1);
    }

    /// <summary>A checkpoint hash disagreed with a peer's; raised once, as soon as it is detected.</summary>
    public event EventHandler<LockstepDesync>? Desynced;

    /// <summary>More is known about <see cref="Desync"/>: the peer's hash trail or hash tree arrived and was compared.</summary>
    public event EventHandler<LockstepDesync>? DesyncLocalised;

    /// <summary>A seat changed state (handshake matched, refused, peer lost).</summary>
    public event EventHandler<LockstepPeerStatus>? PeerStateChanged;

    public World World => _world;

    public LockstepSessionOptions Options => _options;

    public int LocalPeer { get; }

    /// <summary>Seats in the session.</summary>
    public int Peers { get; }

    /// <summary>The Tick the World was at when the session started: the first Tick it runs in lockstep.</summary>
    public long StartTick { get; }

    /// <summary>The first Tick this peer's own frames are required for.</summary>
    public long JoinTick { get; }

    /// <summary>The gate the session holds; one Tick is let through per successful <see cref="TryBeginTick"/>. Give it to the World as <see cref="WorldOptions.TickGate"/>.</summary>
    public TickGate Gate { get; }

    /// <summary>The next Tick to run (the World's <see cref="World.CurrentTick"/> between Ticks).</summary>
    public long NextTick => _nextTick;

    /// <summary>The Tick a command submitted now takes effect: <see cref="NextTick"/> plus <see cref="InputDelay"/>, never behind a frame already closed.</summary>
    public long OpenInputTick => _open + 1;

    /// <summary>The current input delay in Ticks.</summary>
    public int InputDelay => _delay;

    /// <summary>Times <see cref="TryBeginTick"/> found a required frame missing.</summary>
    public long Stalls { get; private set; }

    /// <summary>Consecutive stalls since the last Tick that ran.</summary>
    public int ConsecutiveStalls { get; private set; }

    /// <summary>The first desync detected, or null.</summary>
    public LockstepDesync? Desync { get; private set; }

    public LockstepSessionState State
    {
        get
        {
            if (Desync is not null)
            {
                return LockstepSessionState.Desynced;
            }

            bool waiting = false;
            foreach (var slot in _slots)
            {
                switch (slot.Status.State)
                {
                    case LockstepPeerState.Refused:
                    case LockstepPeerState.Lost:
                        return LockstepSessionState.Failed;
                    case LockstepPeerState.Expected:
                        waiting = true;
                        break;
                }
            }

            return waiting ? LockstepSessionState.Handshaking : LockstepSessionState.Running;
        }
    }

    /// <summary>The state of seat <paramref name="peer"/>.</summary>
    public LockstepPeerStatus Peer(int peer)
    {
        if ((uint)peer >= (uint)Peers)
        {
            throw new ArgumentOutOfRangeException(nameof(peer), peer, $"The session has {Peers} seats.");
        }

        return _slots[peer].Status;
    }

    /// <summary>True when <see cref="TryBeginTick"/> would wait for <paramref name="peer"/>'s frame for <see cref="NextTick"/>.</summary>
    public bool IsMissingFrame(int peer)
    {
        if ((uint)peer >= (uint)Peers)
        {
            throw new ArgumentOutOfRangeException(nameof(peer), peer, $"The session has {Peers} seats.");
        }

        var slot = _slots[peer];
        return peer != LocalPeer && slot.Status.State == LockstepPeerState.Running && _nextTick >= slot.RequiredFrom && !slot.Frames.Contains(_nextTick);
    }

    /// <summary>Adds a local command to the frame for <see cref="OpenInputTick"/>; returns its sequence.</summary>
    public uint Submit<T>(in T command) where T : unmanaged
    {
        ThrowIfDisposed();
        return _writer.Add(in command);
    }

    /// <summary>Adds a local command from its wire bytes (exactly the registered size) to the frame for <see cref="OpenInputTick"/>; returns its sequence.</summary>
    public uint Submit(InputCommandType command, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        return _writer.Add(command, payload);
    }

    /// <summary>
    /// Takes one message from a peer. Malformed or misaddressed bytes throw <see cref="LockstepProtocolException"/>;
    /// a peer that does not match is refused (<see cref="Peer"/>), never thrown. Call it between Ticks.
    /// </summary>
    public void Receive(ReadOnlySpan<byte> message)
    {
        ThrowIfDisposed();
        LockstepMessage.Decode(message, _in);
        if ((uint)_in.Sender >= (uint)Peers || _in.Sender == LocalPeer)
        {
            throw new LockstepProtocolException($"Peer {LocalPeer} received a message claiming to come from peer {_in.Sender}.");
        }

        if (_in.Recipient != LocalPeer)
        {
            throw new LockstepProtocolException($"Peer {LocalPeer} received a message addressed to peer {_in.Recipient}.");
        }

        var slot = _slots[_in.Sender];
        if (_in.Hello is { } hello)
        {
            slot.HelloWanted = !_in.Has(LockstepMessage.HasSync);
            HandleHello(slot, hello);
        }

        if (_in.Has(LockstepMessage.HasSync))
        {
            slot.HelloAcked = true;
            slot.HelloWanted = false;
            if (slot.Status.State == LockstepPeerState.Running)
            {
                HandleSync(slot);
            }
        }

        if (_in.Has(LockstepMessage.DiagnosisSeen))
        {
            slot.SendDiagnosis = false;
        }

        if (_in.Diagnosis is { } diagnosis && slot.Status.State == LockstepPeerState.Running)
        {
            HandleDiagnosis(slot, diagnosis);
        }
    }

    /// <summary>
    /// If every required frame for <see cref="NextTick"/> is held and the session is running, submits them to the
    /// World in seat order, lets one Tick through the <see cref="Gate"/> and returns true: call <see cref="World.Tick"/>
    /// then <see cref="EndTick"/>. Otherwise counts a stall and returns false.
    /// </summary>
    public bool TryBeginTick()
    {
        ThrowIfDisposed();
        if (_inTick)
        {
            throw new InvalidOperationException("TryBeginTick was called again before EndTick.");
        }

        if (State != LockstepSessionState.Running)
        {
            return false;
        }

        long tick = _nextTick;
        foreach (var slot in _slots)
        {
            if (slot.Index != LocalPeer && slot.Status.State == LockstepPeerState.Running && tick >= slot.RequiredFrom && !slot.Frames.Contains(tick))
            {
                Stalls++;
                ConsecutiveStalls++;
                _windowStalled = true;
                return false;
            }
        }

        CloseFramesThrough(tick);
        foreach (var slot in _slots)
        {
            byte[]? frame = null;
            if (slot.Index == LocalPeer)
            {
                if (tick >= JoinTick)
                {
                    _ownFrames.TryGet(tick, out frame);
                }
            }
            else if (slot.Status.State == LockstepPeerState.Running && tick >= slot.RequiredFrom)
            {
                slot.Frames.TryGet(tick, out frame);
            }

            if (frame is not null && frame.Length > 4)
            {
                InputFrame.Submit(_world, frame, tick, ReadOnlySpan<int>.Empty);
            }
        }

        ConsecutiveStalls = 0;
        _inTick = true;
        Gate.Step();
        return true;
    }

    /// <summary>
    /// After <see cref="World.Tick"/>: records the checkpoint hash (and hash tree) when due, closes the frames the
    /// new input delay calls for and forgets frames past retention. False when the World has not run the Tick yet
    /// (another hold on the gate): call it again after the Tick ran.
    /// </summary>
    public bool EndTick()
    {
        ThrowIfDisposed();
        if (!_inTick)
        {
            throw new InvalidOperationException("EndTick without a successful TryBeginTick.");
        }

        long tick = _nextTick;
        if (_world.CurrentTick == tick)
        {
            return false;
        }

        if (_world.CurrentTick != tick + 1)
        {
            throw new InvalidOperationException($"The World ran to Tick {_world.CurrentTick}; the session expected exactly Tick {tick} to run.");
        }

        _inTick = false;
        _nextTick = tick + 1;
        if (IsCheckpoint(tick))
        {
            ulong hash = _world.Hash();
            _ownHashes.Set(tick, hash);
            if (_ownTrees is not null)
            {
                _ownTrees.Set(tick, _world.HashTree());
                _ownTrees.Trim(tick - ((long)_options.CheckpointEvery * (_options.HashTreesKept - 1)));
            }

            foreach (var slot in _slots)
            {
                if (slot.Index != LocalPeer && slot.Status.State == LockstepPeerState.Running && slot.Hashes.TryGet(tick, out ulong remote))
                {
                    CheckHash(slot, tick, hash, remote);
                }
            }
        }

        AdaptDelay();
        CloseFramesThrough(OpenTickFrom(_nextTick) - 1);
        long floor = _nextTick - _options.FrameRetentionTicks;
        _ownFrames.Trim(floor);
        _ownHashes.Trim(floor);
        foreach (var slot in _slots)
        {
            slot.Frames.Trim(_nextTick);
            slot.Hashes.Trim(floor);
        }

        return true;
    }

    /// <summary>
    /// <see cref="TryBeginTick"/>, <see cref="World.Tick"/> and <see cref="EndTick"/> in one call; false when the
    /// session stalled, is not running, or the gate is also held elsewhere.
    /// </summary>
    public bool Tick()
    {
        if (!TryBeginTick())
        {
            return false;
        }

        if (!_world.Tick())
        {
            return false;
        }

        return EndTick();
    }

    /// <summary>
    /// The next message for seat <paramref name="toPeer"/>: the handshake until the peer acknowledged it, this peer's
    /// acks, resend requests, unacknowledged frames and checkpoint hashes, and desync diagnostics. Empty for an
    /// absent seat. The span is valid until the next call; copy it into the transport.
    /// </summary>
    public ReadOnlySpan<byte> NextMessage(int toPeer)
    {
        ThrowIfDisposed();
        if ((uint)toPeer >= (uint)Peers || toPeer == LocalPeer)
        {
            throw new ArgumentOutOfRangeException(nameof(toPeer), toPeer, $"A message goes to another seat in 0..{Peers - 1}.");
        }

        var slot = _slots[toPeer];
        var state = slot.Status.State;
        if (state == LockstepPeerState.Absent)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        bool hello = state is LockstepPeerState.Expected or LockstepPeerState.Refused || !slot.HelloAcked || slot.HelloWanted;
        bool sync = state == LockstepPeerState.Running;
        bool diagnosis = sync && slot.SendDiagnosis && Desync is not null;
        byte flags = (byte)((hello ? LockstepMessage.HasHello : 0) | (sync ? LockstepMessage.HasSync : 0) | (diagnosis ? LockstepMessage.HasDiagnosis : 0) | (slot.DiagnosisReceived ? LockstepMessage.DiagnosisSeen : 0));
        _out.Reset();
        LockstepMessage.WriteHeader(_out, LocalPeer, toPeer, flags);
        if (hello)
        {
            LockstepMessage.WriteHello(_out, StartTick, JoinTick, Peers, _options.CheckpointEvery, _startHash, _identityBytes, _permissionBytes);
        }

        if (sync)
        {
            WriteSync(slot);
        }

        if (diagnosis)
        {
            WriteDiagnosis();
        }

        return _out.WrittenSpan;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Gate.Release();
        }
    }

    private void WriteSync(Slot slot)
    {
        _out.WriteInt64(slot.HeldFrames);
        _out.WriteInt64(slot.HeldHashes);
        int missingAt = _out.Position;
        _out.WriteInt32(0);
        int missing = 0;
        for (long t = slot.HeldFrames; t <= slot.Frames.Highest && missing < MaxMissingRequests; t++)
        {
            if (!slot.Frames.Contains(t))
            {
                _out.WriteInt64(t);
                missing++;
            }
        }

        _out.WriteUInt32At(missingAt, (uint)missing);

        int max = _options.MaxFramesPerMessage;
        int countAt = _out.Position;
        _out.WriteInt32(0);
        int count = 0;
        long from = Math.Max(slot.FrameAckFromPeer, JoinTick);
        if (from < _ownFrames.Floor && from <= _open)
        {
            Lose(slot, string.Create(CultureInfo.InvariantCulture, $"peer {slot.Index} needs this peer's frame for Tick {from}; frames before Tick {_ownFrames.Floor} are no longer retained"));
        }
        else
        {
            foreach (long t in slot.Requested)
            {
                if (count < max && t >= from && _ownFrames.TryGet(t, out var requested))
                {
                    WriteFrame(t, requested);
                    count++;
                }
            }

            for (long t = from; t <= _open && count < max; t++)
            {
                if (_ownFrames.TryGet(t, out var frame))
                {
                    WriteFrame(t, frame);
                    count++;
                }
            }
        }

        _out.WriteUInt32At(countAt, (uint)count);

        countAt = _out.Position;
        _out.WriteInt32(0);
        count = 0;
        long hashFrom = Math.Max(slot.HashAckFromPeer, StartTick);
        if (hashFrom < _ownHashes.Floor && NextCheckpoint(hashFrom) < _nextTick)
        {
            Lose(slot, string.Create(CultureInfo.InvariantCulture, $"peer {slot.Index} needs this peer's checkpoint for Tick {hashFrom}; checkpoints before Tick {_ownHashes.Floor} are no longer retained"));
        }
        else
        {
            for (long t = NextCheckpoint(hashFrom); t < _nextTick && count < max; t = NextCheckpoint(t + 1))
            {
                if (_ownHashes.TryGet(t, out ulong hash))
                {
                    _out.WriteInt64(t);
                    _out.WriteUInt64(hash);
                    count++;
                }
            }
        }

        _out.WriteUInt32At(countAt, (uint)count);
    }

    private void WriteFrame(long tick, byte[] frame)
    {
        _out.WriteInt64(tick);
        _out.WriteInt32(frame.Length);
        _out.WriteBytes(frame);
    }

    private void WriteDiagnosis()
    {
        var desync = Desync!;
        _out.WriteInt64(desync.Tick);
        bool hasLocal = _ownHashes.TryGet(desync.Tick, out ulong local);
        _out.WriteByte(hasLocal ? (byte)1 : (byte)0);
        _out.WriteUInt64(local);
        long from = Math.Max(StartTick, desync.LastAgreedTick + 1);
        var trail = _trail?.Slice(from, desync.Tick) ?? new LockstepHashTrail(1);
        trail.Write(_out);
        if (_ownTrees is not null && _ownTrees.TryGet(desync.Tick, out var tree))
        {
            _out.WriteByte(1);
            LockstepMessage.WriteTree(_out, tree);
        }
        else
        {
            _out.WriteByte(0);
        }
    }

    private void HandleHello(Slot slot, LockstepMessage.Hello hello)
    {
        if (slot.Status.State is LockstepPeerState.Running or LockstepPeerState.Refused or LockstepPeerState.Lost)
        {
            return;
        }

        var reasons = ImmutableArray.CreateBuilder<string>();
        if (hello.Seats != Peers)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"Different seat count: the local peer has {Peers}, the remote peer has {hello.Seats}."));
        }

        if (hello.CheckpointEvery != _options.CheckpointEvery)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"Different checkpoint interval: the local peer has {_options.CheckpointEvery}, the remote peer has {hello.CheckpointEvery}."));
        }

        var handshake = PeerHandshake.Compare(_identity, hello.Identity);
        foreach (var mismatch in handshake.Mismatches)
        {
            reasons.Add(mismatch.Message);
        }

        if (handshake.Has(HandshakeMismatchKind.SchemaHash))
        {
            foreach (string difference in _permissions.Differences(hello.Permissions))
            {
                reasons.Add("Input permissions: " + difference + ".");
            }
        }

        long requiredFrom = Math.Max(StartTick, hello.JoinTick);
        if (requiredFrom < _nextTick)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"The remote peer joins at Tick {hello.JoinTick} but the local peer already ran Tick {_nextTick - 1}."));
        }

        if (hello.StartTick == StartTick && hello.StartHash != _startHash)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"Different start state at Tick {StartTick}: the local peer has 0x{_startHash:X16}, the remote peer has 0x{hello.StartHash:X16}."));
        }

        slot.Status.Handshake = handshake;
        slot.Status.JoinTick = hello.JoinTick;
        if (reasons.Count > 0)
        {
            slot.Status.Reasons = reasons.ToImmutable();
            slot.Status.State = LockstepPeerState.Refused;
            PeerStateChanged?.Invoke(this, slot.Status);
            return;
        }

        slot.RequiredFrom = requiredFrom;
        slot.HeldFrames = requiredFrom;
        // What the peer will ask of us first: our frames from the later of its start and our join, our checkpoints from the later start.
        slot.FrameAckFromPeer = Math.Max(slot.FrameAckFromPeer, Math.Max(hello.StartTick, JoinTick));
        slot.HashAckFromPeer = Math.Max(slot.HashAckFromPeer, Math.Max(hello.StartTick, StartTick));
        slot.Status.NextMissingFrame = requiredFrom;
        slot.HashesFrom = Math.Max(StartTick, hello.StartTick);
        slot.HeldHashes = NextCheckpoint(slot.HashesFrom);
        slot.Frames.Trim(requiredFrom);
        slot.Hashes.Trim(slot.HashesFrom);
        slot.Status.State = LockstepPeerState.Running;
        PeerStateChanged?.Invoke(this, slot.Status);
    }

    private void HandleSync(Slot slot)
    {
        slot.FrameAckFromPeer = Math.Max(slot.FrameAckFromPeer, _in.FrameAck);
        slot.HashAckFromPeer = Math.Max(slot.HashAckFromPeer, _in.HashAck);
        slot.Requested.Clear();
        slot.Requested.AddRange(_in.Missing);
        foreach (var (tick, bytes) in _in.Frames)
        {
            if (tick < slot.RequiredFrom || tick < _nextTick || slot.Frames.Contains(tick))
            {
                continue;
            }

            slot.Frames.Set(tick, bytes);
            _windowMinSlack = Math.Min(_windowMinSlack, tick - _nextTick);
        }

        while (slot.Frames.Contains(slot.HeldFrames))
        {
            slot.HeldFrames++;
        }

        slot.Status.NextMissingFrame = slot.HeldFrames;
        foreach (var (tick, hash) in _in.Hashes)
        {
            if (tick < slot.HashesFrom || !IsCheckpoint(tick) || slot.Hashes.Contains(tick))
            {
                continue;
            }

            slot.Hashes.Set(tick, hash);
            if (_ownHashes.TryGet(tick, out ulong own))
            {
                CheckHash(slot, tick, own, hash);
            }
        }

        while (slot.Hashes.Contains(slot.HeldHashes))
        {
            slot.HeldHashes = NextCheckpoint(slot.HeldHashes + 1);
        }
    }

    private void CheckHash(Slot slot, long tick, ulong own, ulong remote)
    {
        if (own == remote)
        {
            slot.Status.LastAgreedCheckpoint = Math.Max(slot.Status.LastAgreedCheckpoint, tick);
            return;
        }

        DetectDesync(slot, tick, own, remote);
    }

    private void DetectDesync(Slot slot, long tick, ulong? own, ulong remote)
    {
        if (Desync is not null)
        {
            return;
        }

        Desync = new LockstepDesync(slot.Index, tick, own, remote, slot.Status.LastAgreedCheckpoint);
        slot.SendDiagnosis = true;
        Desynced?.Invoke(this, Desync);
    }

    private void HandleDiagnosis(Slot slot, LockstepMessage.Diagnosis diagnosis)
    {
        slot.DiagnosisReceived = true;
        if (Desync is null)
        {
            _ownHashes.TryGet(diagnosis.Tick, out ulong ownAtTick);
            DetectDesync(slot, diagnosis.Tick, _ownHashes.Contains(diagnosis.Tick) ? ownAtTick : null, diagnosis.HasLocalHash ? diagnosis.LocalHash : 0);
        }

        var desync = Desync!;
        if (desync.Peer != slot.Index)
        {
            return;
        }

        if (desync.LocalHash is null && _ownHashes.TryGet(desync.Tick, out ulong local))
        {
            desync.LocalHash = local;
        }

        slot.SendDiagnosis = true;
        bool refined = false;
        if (_trail is not null && desync.Divergence is null && diagnosis.Trail.Count > 0 && LockstepHashTrail.Compare(_trail, diagnosis.Trail, _world.Schema) is { } where)
        {
            desync.Divergence = where;
            desync.Phase = _world.Schema.Phases[where.PhaseId].Name;
            desync.System = where.SystemId >= 0 ? _world.Schema.Systems[where.SystemId].Name : null;
            refined = true;
        }

        if (diagnosis.Tree is { } tree && desync.Localisation is null && _ownTrees is not null && _ownTrees.TryGet(desync.Tick, out var ownTree) && tree.SchemaHash == ownTree.SchemaHash)
        {
            desync.Localisation = WorldHashTree.Compare(ownTree, tree);
            refined = true;
        }

        if (refined)
        {
            DesyncLocalised?.Invoke(this, desync);
        }
    }

    private void Lose(Slot slot, string reason)
    {
        if (slot.Status.State != LockstepPeerState.Running)
        {
            return;
        }

        slot.Status.State = LockstepPeerState.Lost;
        slot.Status.Reasons = [reason];
        PeerStateChanged?.Invoke(this, slot.Status);
    }

    private void AdaptDelay()
    {
        if (_options.AdaptiveInputDelay is not { } adaptive)
        {
            return;
        }

        _windowTicks++;
        if (_windowTicks < adaptive.WindowTicks)
        {
            return;
        }

        if (_windowStalled)
        {
            _delay = Math.Min(adaptive.Max, _delay + 1);
        }
        else if (_windowMinSlack >= 2)
        {
            _delay = Math.Max(adaptive.Min, _delay - 1);
        }

        _windowTicks = 0;
        _windowStalled = false;
        _windowMinSlack = long.MaxValue;
    }

    private long OpenTickFrom(long nextTick) => Math.Max(Math.Max(nextTick + _delay, JoinTick), _open + 1);

    /// <summary>Closes the local frames for every Tick up to <paramref name="through"/>: the open frame takes the commands submitted so far, later ones are empty.</summary>
    private void CloseFramesThrough(long through)
    {
        while (_open < through)
        {
            _open++;
            _ownFrames.Set(_open, _writer.ToFrame());
        }
    }

    private bool IsCheckpoint(long tick) => (tick + 1) % _options.CheckpointEvery == 0;

    private long NextCheckpoint(long tick)
    {
        int every = _options.CheckpointEvery;
        long rem = (tick + 1) % every;
        return rem == 0 ? tick : tick + (every - rem);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>What this peer holds about one seat.</summary>
    private sealed class Slot
    {
        public Slot(int index, LockstepPeerState state, long startTick)
        {
            Index = index;
            Status = new LockstepPeerStatus(index, state);
            Frames = new TickTable<byte[]>(startTick);
            Hashes = new TickTable<ulong>(startTick);
        }

        public int Index { get; }

        public LockstepPeerStatus Status { get; }

        public TickTable<byte[]> Frames { get; }

        public TickTable<ulong> Hashes { get; }

        /// <summary>First Tick the peer's frames are required for.</summary>
        public long RequiredFrom { get; set; } = long.MaxValue;

        /// <summary>First checkpoint Tick the peer can have a hash for.</summary>
        public long HashesFrom { get; set; }

        /// <summary>Next Tick whose frame from the peer is missing (our cumulative frame ack to it).</summary>
        public long HeldFrames { get; set; }

        /// <summary>Next checkpoint Tick whose hash from the peer is missing (our cumulative hash ack to it).</summary>
        public long HeldHashes { get; set; }

        /// <summary>The peer's cumulative acks of our frames and hashes.</summary>
        public long FrameAckFromPeer { get; set; }

        public long HashAckFromPeer { get; set; }

        public List<long> Requested { get; } = [];

        public bool HelloAcked { get; set; }

        public bool HelloWanted { get; set; }

        public bool SendDiagnosis { get; set; }

        public bool DiagnosisReceived { get; set; }
    }
}
