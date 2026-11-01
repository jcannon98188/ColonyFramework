using Colony.Diagnostics;
using Colony.Persistence;

namespace Colony.Testing;

/// <summary>
/// Runs a <see cref="Scenario"/> on N peers in lockstep over a <see cref="TestNetwork"/> (TASK-31, spec section
/// 7.4). Each peer owns a World (composed and populated from the scenario, its own worker count) and is Input
/// source <c>i</c>; its <see cref="LockstepInputScript"/> writes its commands <see cref="LockstepOptions.InputDelay"/>
/// Ticks ahead. Every network step each peer takes the packets that arrived, runs its next Tick when it holds
/// every source's frame for it (else it stalls), and sends each other peer one packet with its unacknowledged
/// frames and checkpoint hashes plus its acks, so losses are repaired by resending. A checkpoint hash that
/// differs from the receiver's own is a desync; the peers' hash trails then name the first diverging Tick,
/// Phase and System. Deterministic and single-threaded (peer Worlds use their own workers inside
/// <see cref="World.Tick"/>): the scenario, the options and <see cref="LockstepOptions.NetworkSeed"/> reproduce a run.
/// The scenario's <see cref="Scenario.Advance"/> is not used; inputs come from the script only.
/// </summary>
public static class LockstepHarness
{
    public static LockstepResult Run(Scenario scenario, LockstepInputScript script, LockstepOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(script);
        options ??= new LockstepOptions();
        options.Validate();

        var network = new TestNetwork(options.Peers, options.Profile, options.NetworkSeed);
        var peers = new Peer[options.Peers];
        LockstepDesync? desync = null;
        Exception? error = null;
        bool completed = false;
        long step = 0;
        try
        {
            for (int i = 0; i < peers.Length; i++)
            {
                peers[i] = new Peer(i, scenario, script, options);
            }

            var decoded = new LockstepPacket.Decoded();
            var writer = new LockstepPacket.Writer();
            int maxSteps = options.EffectiveMaxSteps;
            for (step = 0; step < maxSteps; step++)
            {
                foreach (var peer in peers)
                {
                    while (desync is null && network.TryReceive(peer.Index, out var packet))
                    {
                        LockstepPacket.Decode(packet.Payload.Span, decoded);
                        if (peer.Receive(decoded) is { } mismatch)
                        {
                            desync = Diagnose(peers, peer.Index, mismatch.Peer, mismatch.Tick, step, options);
                        }
                    }
                }

                foreach (var peer in peers)
                {
                    if (desync is null && peer.TryTick() is { } mismatch)
                    {
                        desync = Diagnose(peers, peer.Index, mismatch.Peer, mismatch.Tick, step, options);
                    }
                }

                if (desync is not null)
                {
                    break;
                }

                if (peers.All(p => p.IsDone))
                {
                    completed = true;
                    break;
                }

                foreach (var peer in peers)
                {
                    peer.Send(network, writer);
                }

                network.Advance();
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e;
        }

        var results = new List<LockstepPeerResult>(peers.Length);
        int applied = 0;
        foreach (var peer in peers)
        {
            if (peer is null)
            {
                continue;
            }

            results.Add(peer.ToResult());
            applied += peer.FaultsApplied;
            peer.Dispose();
        }

        return new LockstepResult(scenario.Name, options, step, completed && error is null, desync, results, network.Counters, applied, error);
    }

    private static LockstepDesync Diagnose(Peer[] peers, int detectedBy, int other, long checkpointTick, long step, LockstepOptions options)
    {
        var a = peers[detectedBy];
        var b = peers[other];
        long common = Math.Min(a.TicksRun, b.TicksRun);
        long tick = checkpointTick;
        for (long t = 0; t < common; t++)
        {
            if (a.HashAt(t) != b.HashAt(t))
            {
                tick = t;
                break;
            }
        }

        var schema = a.World.Schema;
        if (options.HashTrail && PeerHashTrail.Compare(a.Trail!, b.Trail!, schema) is { } where)
        {
            string? system = where.System >= 0 ? schema.Systems[where.System].Name : null;
            return new LockstepDesync(detectedBy, other, checkpointTick, step, where.Tick, where.Phase, schema.Phases[where.Phase].Name, where.System, system);
        }

        return new LockstepDesync(detectedBy, other, checkpointTick, step, tick, -1, null, -1, null);
    }

    /// <summary>One reference lockstep peer: its World, frame table, checkpoints and acks.</summary>
    private sealed class Peer : IDisposable
    {
        private static readonly byte[] EmptyFrame = new byte[4];

        private readonly LockstepOptions _options;
        private readonly LockstepInputScript _script;
        private readonly InputFrameWriter _writer;
        private readonly byte[]?[][] _frames;
        private readonly long[] _heldFrames;
        private readonly ulong[] _hashes;
        private readonly ulong[][] _peerHashes;
        private readonly bool[][] _hasPeerHash;
        private readonly long[] _heldHashes;
        private readonly long[] _remoteFrameAck;
        private readonly long[] _remoteHashAck;
        private readonly LockstepFault[] _faults;
        private readonly bool[] _hit;
        private long _scheduled = -1;
        private long _waiting;

        public Peer(int index, Scenario scenario, LockstepInputScript script, LockstepOptions options)
        {
            Index = index;
            _options = options;
            _script = script;
            Workers = options.PeerWorkers[index % options.PeerWorkers.Count];
            Trail = options.HashTrail ? new PeerHashTrail() : null;
            var builder = new WorldBuilder();
            scenario.Compose(builder);
            World = builder.Build().CreateWorld(new WorldOptions
            {
                Seed = scenario.Seed,
                WorkerThreads = Workers,
                InitialEntityCapacity = scenario.InitialEntityCapacity,
                Diagnostics = Trail is null ? DiagnosticsOptions.Default : new DiagnosticsOptions { HashTrail = true, Listener = Trail },
            });
            scenario.Populate(World);

            int peers = options.Peers;
            int ticks = options.Ticks;
            _writer = new InputFrameWriter(World.Schema, new InputSource((uint)index));
            _frames = new byte[]?[peers][];
            _peerHashes = new ulong[peers][];
            _hasPeerHash = new bool[peers][];
            for (int p = 0; p < peers; p++)
            {
                _frames[p] = new byte[]?[ticks];
                _peerHashes[p] = new ulong[ticks];
                _hasPeerHash[p] = new bool[ticks];
            }

            _heldFrames = new long[peers];
            _heldHashes = new long[peers];
            _remoteFrameAck = new long[peers];
            _remoteHashAck = new long[peers];
            _hashes = new ulong[ticks];
            _faults = [.. options.Faults.Where(f => f.Peer == index)];
            _hit = new bool[_faults.Length];
            for (int p = 0; p < peers; p++)
            {
                AdvanceHeldHashes(p);
            }

            Schedule(options.InputDelay);
        }

        public int Index { get; }

        public int Workers { get; }

        public World World { get; }

        public PeerHashTrail? Trail { get; }

        public long TicksRun { get; private set; }

        public long StallSteps { get; private set; }

        public int ProceededWithoutFrames { get; private set; }

        /// <summary>This peer's faults that hit a frame.</summary>
        public int FaultsApplied => _hit.Count(h => h);

        /// <summary>Ran every Tick and holds every other peer's checkpoints.</summary>
        public bool IsDone
        {
            get
            {
                if (TicksRun < _options.Ticks)
                {
                    return false;
                }

                for (int p = 0; p < _options.Peers; p++)
                {
                    if (p != Index && _heldHashes[p] < _options.Ticks)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public ulong HashAt(long tick) => _hashes[tick];

        /// <summary>Takes one packet; returns the (peer, Tick) of a checkpoint that contradicts this peer's own hash.</summary>
        public (int Peer, long Tick)? Receive(LockstepPacket.Decoded packet)
        {
            int from = packet.Sender;
            if ((uint)from >= (uint)_options.Peers || from == Index)
            {
                throw new FormatException($"Peer {Index} received a packet claiming to come from peer {from}.");
            }

            _remoteFrameAck[from] = Math.Max(_remoteFrameAck[from], packet.FrameAck);
            _remoteHashAck[from] = Math.Max(_remoteHashAck[from], packet.HashAck);
            foreach (var frame in packet.Frames)
            {
                if (frame.Tick < 0 || frame.Tick >= _options.Ticks || _frames[from][frame.Tick] is not null || IsLost(from, frame.Tick))
                {
                    continue;
                }

                _frames[from][frame.Tick] = frame.Bytes;
            }

            AdvanceHeldFrames(from);
            (int, long)? mismatch = null;
            foreach (var checkpoint in packet.Hashes)
            {
                long t = checkpoint.Tick;
                if (t < 0 || t >= _options.Ticks || _hasPeerHash[from][t])
                {
                    continue;
                }

                _peerHashes[from][t] = checkpoint.Hash;
                _hasPeerHash[from][t] = true;
                if (mismatch is null && t < TicksRun && _hashes[t] != checkpoint.Hash)
                {
                    mismatch = (from, t);
                }
            }

            AdvanceHeldHashes(from);
            return mismatch;
        }

        /// <summary>Runs the next Tick when every source's frame for it is held; returns a checkpoint contradiction found afterwards.</summary>
        public (int Peer, long Tick)? TryTick()
        {
            if (TicksRun >= _options.Ticks)
            {
                return null;
            }

            long tick = TicksRun;
            bool missing = false;
            for (int s = 0; s < _options.Peers; s++)
            {
                missing |= _frames[s][tick] is null;
            }

            if (missing)
            {
                StallSteps++;
                _waiting++;
                if (_options.StallTimeoutSteps == 0 || _waiting < _options.StallTimeoutSteps)
                {
                    return null;
                }

                for (int s = 0; s < _options.Peers; s++)
                {
                    if (_frames[s][tick] is null)
                    {
                        _frames[s][tick] = EmptyFrame;
                        AdvanceHeldFrames(s);
                    }
                }

                ProceededWithoutFrames++;
            }

            _waiting = 0;
            for (int s = 0; s < _options.Peers; s++)
            {
                var frame = Feed(s, tick, _frames[s][tick]!);
                InputFrame.Submit(World, frame, tick);
            }

            World.Tick();
            _hashes[tick] = World.Hash();
            TicksRun = tick + 1;
            Schedule(TicksRun + _options.InputDelay);
            if (_options.IsCheckpoint(tick))
            {
                for (int p = 0; p < _options.Peers; p++)
                {
                    if (p != Index && _hasPeerHash[p][tick] && _peerHashes[p][tick] != _hashes[tick])
                    {
                        return (p, tick);
                    }
                }
            }

            return null;
        }

        /// <summary>Sends every other peer its unacknowledged frames and checkpoints, plus this peer's acks.</summary>
        public void Send(TestNetwork network, LockstepPacket.Writer writer)
        {
            int max = _options.MaxFramesPerPacket;
            for (int p = 0; p < _options.Peers; p++)
            {
                if (p == Index)
                {
                    continue;
                }

                writer.Begin(Index, _heldFrames[p], _heldHashes[p]);
                int added = 0;
                for (long t = _remoteFrameAck[p]; t <= _scheduled && added < max; t++, added++)
                {
                    writer.AddFrame(t, _frames[Index][t]);
                }

                writer.BeginHashes();
                added = 0;
                for (long t = _remoteHashAck[p]; t < TicksRun && added < max; t++)
                {
                    if (_options.IsCheckpoint(t))
                    {
                        writer.AddHash(t, _hashes[t]);
                        added++;
                    }
                }

                network.Send(Index, p, writer.End());
            }
        }

        public LockstepPeerResult ToResult() => new(Index, Workers, TicksRun, StallSteps, ProceededWithoutFrames, _hashes.AsSpan(0, (int)TicksRun).ToArray());

        public void Dispose() => World.Dispose();

        private byte[] Feed(int source, long tick, byte[] frame)
        {
            for (int i = 0; i < _faults.Length; i++)
            {
                var fault = _faults[i];
                if (fault.Kind != LockstepFaultKind.LoseFrame && fault.Source == source && fault.Tick == tick && fault.Apply(frame, World.Schema) is { } replaced)
                {
                    _hit[i] = true;
                    frame = replaced;
                }
            }

            return frame;
        }

        private bool IsLost(int source, long tick)
        {
            for (int i = 0; i < _faults.Length; i++)
            {
                var fault = _faults[i];
                if (fault.Kind == LockstepFaultKind.LoseFrame && fault.Source == source && fault.Tick == tick)
                {
                    _hit[i] = true;
                    return true;
                }
            }

            return false;
        }

        private void Schedule(long upTo)
        {
            long last = Math.Min(_options.Ticks - 1, upTo);
            while (_scheduled < last)
            {
                _scheduled++;
                _script(World, _writer, _scheduled);
                _frames[Index][_scheduled] = _writer.ToFrame();
            }

            AdvanceHeldFrames(Index);
        }

        private void AdvanceHeldFrames(int source)
        {
            while (_heldFrames[source] < _options.Ticks && _frames[source][_heldFrames[source]] is not null)
            {
                _heldFrames[source]++;
            }
        }

        private void AdvanceHeldHashes(int source)
        {
            while (_heldHashes[source] < _options.Ticks && (!_options.IsCheckpoint(_heldHashes[source]) || _hasPeerHash[source][_heldHashes[source]]))
            {
                _heldHashes[source]++;
            }
        }
    }
}
