using Colony.Persistence;
using Colony.Testing;

namespace Colony.Testing.Tests.Scenarios;

/// <summary>
/// TASK-15 AC #5, replay equivalence (spec section 7.2): the input scenario driven from a replay log instead
/// of the live script. The harness hashes the playback World at every checkpoint; its sequence must equal
/// the live run's. <see cref="ReplayPlayer"/> verifies the recorded checkpoints as it goes, so a divergence
/// also fails the run through its exception. Not linked into the AOT host (it needs Colony.Persistence).
/// </summary>
public sealed class ReplayPlaybackScenario : Scenario
{
    private readonly ReplayLog _log;
    private ReplayPlayer? _player;

    public ReplayPlaybackScenario(ReplayLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        Seed = log.Seed;
    }

    public override string Name => "inputs-replay";

    public override ulong Seed { get; init; }

    public override int InitialEntityCapacity => 4096;

    public override void Compose(WorldBuilder builder) => InputScenario.ComposeStatic(builder);

    public override void Populate(World world) => _player = null;

    public override void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_player is null || !ReferenceEquals(_player.World, world))
        {
            _player = new ReplayPlayer(_log, world);
        }

        _player.Tick();
    }

    /// <summary>Runs <paramref name="scenario"/> live for <paramref name="ticks"/> Ticks on a fresh World and records it.</summary>
    public static ReplayLog Record(InputScenario scenario, int ticks, int workers, ReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = workers, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        var recorder = new ReplayRecorder(world, options);
        for (int i = 0; i < ticks; i++)
        {
            scenario.Advance(world);
            recorder.RecordTick();
        }

        return recorder.Finish();
    }
}

/// <summary>
/// TASK-15 AC #5, simulated lockstep (spec section 7.4): three peers run the input scenario side by side.
/// The host (the harness World) takes its commands from one thread per source; a second peer takes the same
/// commands sequentially in reverse source order on its own thread count; a third receives the host's
/// canonical frame over the wire form (<see cref="InputFrame"/>) after the host Ticks, as a relay peer would.
/// After every Tick the canonical frames must be byte-identical and the three World hashes equal, else the
/// run fails. The peers are created in <see cref="Populate"/> and disposed with the scenario.
/// </summary>
public sealed class LockstepScenario : Scenario, IDisposable
{
    private World? _peer;
    private World? _relay;

    public override string Name => "lockstep";

    public override ulong Seed { get; init; } = InputScenario.DefaultSeed;

    public int Sources { get; init; } = 4;

    public int CommandsPerTick { get; init; } = 6;

    /// <summary>Worker threads of the second peer (the host's come from the harness).</summary>
    public int PeerWorkers { get; init; } = 3;

    public override int InitialEntityCapacity => 4096;

    public override void Compose(WorldBuilder builder) => InputScenario.ComposeStatic(builder);

    public override void Populate(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        DisposePeers();
        _peer = CreatePeer(PeerWorkers);
        _relay = CreatePeer(1);
    }

    public override void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var host = world;
        var peer = _peer ?? throw new InvalidOperationException("Populate was not called.");
        var relay = _relay!;
        long tick = host.CurrentTick;
        if (peer.CurrentTick != tick || relay.CurrentTick != tick)
        {
            throw new HarnessException($"The peers fell out of step: host at Tick {tick}, peer at {peer.CurrentTick}, relay at {relay.CurrentTick}.");
        }

        InputScenario.SubmitTick(host, tick, Seed, Sources, CommandsPerTick, concurrent: true);
        for (int s = Sources - 1; s >= 0; s--)
        {
            InputScenario.SubmitSource(peer, tick, Seed, (uint)s, CommandsPerTick);
        }

        host.Tick();
        peer.Tick();
        var frame = InputFrame.Encode(host);
        if (!frame.AsSpan().SequenceEqual(InputFrame.Encode(peer)))
        {
            throw new HarnessException($"The canonical input lists of Tick {tick} differ between the host and the peer.");
        }

        InputFrame.Submit(relay, frame, tick);
        relay.Tick();
        ulong hash = host.Hash();
        ulong peerHash = peer.Hash();
        ulong relayHash = relay.Hash();
        if (hash != peerHash || hash != relayHash)
        {
            throw new HarnessException($"Lockstep diverged after Tick {tick}: host {hash:x16}, peer {peerHash:x16}, relay {relayHash:x16}.");
        }
    }

    public void Dispose() => DisposePeers();

    private World CreatePeer(int workers)
    {
        var builder = new WorldBuilder();
        Compose(builder);
        return builder.Build().CreateWorld(new WorldOptions { Seed = Seed, WorkerThreads = workers, InitialEntityCapacity = InitialEntityCapacity });
    }

    private void DisposePeers()
    {
        _peer?.Dispose();
        _relay?.Dispose();
        _peer = null;
        _relay = null;
    }
}
