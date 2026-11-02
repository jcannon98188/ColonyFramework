using Colony.Diagnostics;
using Colony.Persistence;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Persistence.Tests.Lockstep;

/// <summary>
/// Drives N <see cref="LockstepSession"/>s over a <see cref="TestNetwork"/> step by step, the way a Game loop
/// would: receive what arrived, submit this Tick's commands, try to Tick, send one message per peer, advance the
/// network. The input script is <see cref="LockstepScript"/>-like: keyed by the effective Tick, so the single-World
/// <see cref="Oracle"/> reproduces exactly what the peers must hash.
/// </summary>
internal sealed class SessionRig : IDisposable
{
    private readonly InputScenario _scenario;

    public SessionRig(int peers, int ticks, NetworkProfile profile, ulong networkSeed = 0x34, Func<int, LockstepSessionOptions, LockstepSessionOptions>? configure = null, InputScenario? scenario = null, int[]? workers = null)
    {
        _scenario = scenario ?? new InputScenario();
        Ticks = ticks;
        Network = new TestNetwork(peers, profile, networkSeed);
        Peers = new SessionPeer[peers];
        workers ??= [1, 2, 3];
        for (int i = 0; i < peers; i++)
        {
            var options = new LockstepSessionOptions { LocalPeer = i, Peers = peers, InputDelay = 2, CheckpointEvery = 3 };
            options = configure?.Invoke(i, options) ?? options;
            Peers[i] = new SessionPeer(_scenario, options, workers[i % workers.Length]);
        }
    }

    public int Ticks { get; }

    public TestNetwork Network { get; }

    public SessionPeer[] Peers { get; }

    public long Steps { get; private set; }

    /// <summary>Runs until every peer ran <see cref="Ticks"/> Ticks or <paramref name="maxSteps"/> passed; returns true on completion.</summary>
    public bool Run(int maxSteps = 4096, Action<SessionRig, long>? beforeStep = null)
    {
        for (int step = 0; step < maxSteps; step++, Steps++)
        {
            beforeStep?.Invoke(this, step);
            foreach (var peer in Peers)
            {
                if (peer.Session is null)
                {
                    continue;
                }

                while (Network.TryReceive(peer.Index, out var packet))
                {
                    peer.Session.Receive(packet.Payload.Span);
                }
            }

            foreach (var peer in Peers)
            {
                peer.Step(Ticks);
            }

            if (Peers.All(p => p.Session is null || p.World.CurrentTick >= Ticks))
            {
                return true;
            }

            foreach (var peer in Peers)
            {
                if (peer.Session is null)
                {
                    continue;
                }

                for (int to = 0; to < Peers.Length; to++)
                {
                    if (to == peer.Index)
                    {
                        continue;
                    }

                    var message = peer.Session.NextMessage(to);
                    if (!message.IsEmpty)
                    {
                        Network.Send(peer.Index, to, message);
                    }
                }
            }

            Network.Advance();
        }

        return false;
    }

    /// <summary>One World fed every source's frame directly: the hashes after each Tick that every peer must reproduce.</summary>
    public static ulong[] Oracle(int sources, int ticks, int inputDelay, InputScenario? scenario = null)
    {
        scenario ??= new InputScenario();
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        using var world = builder.Build().CreateWorld(new WorldOptions { Seed = scenario.Seed, WorkerThreads = 1, InitialEntityCapacity = scenario.InitialEntityCapacity });
        scenario.Populate(world);
        var writers = new InputFrameWriter[sources];
        for (int s = 0; s < sources; s++)
        {
            writers[s] = new InputFrameWriter(world.Schema, new InputSource((uint)s));
        }

        var hashes = new ulong[ticks];
        for (int t = 0; t < ticks; t++)
        {
            if (t >= inputDelay)
            {
                for (int s = 0; s < sources; s++)
                {
                    Script(writers[s], t);
                    InputFrame.Submit(world, writers[s].ToFrame(), t);
                }
            }

            world.Tick();
            hashes[t] = world.Hash();
        }

        return hashes;
    }

    /// <summary>Every Tick a deposit; every third Tick also a spawn; every fifth a dismiss (the TASK-31 script).</summary>
    public static void Script(InputFrameWriter frame, long tick)
    {
        uint source = frame.Source.Value;
        frame.Add(new InputScenario.Deposit { Wood = (int)(tick + source), Stone = 1 });
        if (tick % 3 == 0)
        {
            frame.Add(new InputScenario.Spawn { Count = 2 + (int)source });
        }

        if (tick % 5 == 4)
        {
            frame.Add(new InputScenario.Dismiss { Count = 1 });
        }
    }

    public static void Script(LockstepSession session, long tick)
    {
        uint source = (uint)session.LocalPeer;
        session.Submit(new InputScenario.Deposit { Wood = (int)(tick + source), Stone = 1 });
        if (tick % 3 == 0)
        {
            session.Submit(new InputScenario.Spawn { Count = 2 + (int)source });
        }

        if (tick % 5 == 4)
        {
            session.Submit(new InputScenario.Dismiss { Count = 1 });
        }
    }

    public void Dispose()
    {
        foreach (var peer in Peers)
        {
            peer.Dispose();
        }
    }
}

/// <summary>One peer of a <see cref="SessionRig"/>: its World, session, hash trail and per-Tick hashes.</summary>
internal sealed class SessionPeer : IDisposable
{
    private long _scripted = -1;

    public SessionPeer(InputScenario scenario, LockstepSessionOptions options, int workers)
        : this(CreateWorld(scenario, options, workers, out var trail, out var gate), options with { HashTrail = trail, Gate = gate })
    {
    }

    /// <summary>A peer over an existing World (a joiner's): the World must already use <paramref name="options"/>' gate and hash trail.</summary>
    public SessionPeer(World world, LockstepSessionOptions options)
    {
        Index = options.LocalPeer;
        World = world;
        Trail = options.HashTrail!;
        Session = new LockstepSession(World, options);
        Session.Desynced += (_, d) => Desyncs.Add(d);
        Session.DesyncLocalised += (_, d) => Localised.Add(d);
    }

    /// <summary>The World options a peer's World needs: the shared gate and the hash trail listener.</summary>
    public static WorldOptions WorldOptions(int workers, LockstepHashTrail trail, TickGate gate, InputScenario? scenario = null) => new()
    {
        Seed = scenario?.Seed ?? InputScenario.DefaultSeed,
        WorkerThreads = workers,
        InitialEntityCapacity = scenario?.InitialEntityCapacity ?? 4096,
        TickGate = gate,
        Diagnostics = new DiagnosticsOptions { HashTrail = true, Listener = trail },
    };

    private static World CreateWorld(InputScenario scenario, LockstepSessionOptions options, int workers, out LockstepHashTrail trail, out TickGate gate)
    {
        trail = options.HashTrail ?? new LockstepHashTrail();
        gate = options.Gate ?? new TickGate();
        var builder = new WorldBuilder();
        scenario.Compose(builder);
        var world = builder.Build().CreateWorld(WorldOptions(workers, trail, gate, scenario));
        scenario.Populate(world);
        return world;
    }

    public int Index { get; }

    public World World { get; }

    public LockstepSession? Session { get; private set; }

    public LockstepHashTrail Trail { get; }

    public List<ulong> Hashes { get; } = [];

    public List<LockstepDesync> Desyncs { get; } = [];

    public List<LockstepDesync> Localised { get; } = [];

    public long TicksRun => Hashes.Count;

    public long Stalls { get; private set; }

    /// <summary>Extra commands fed straight to the World (seen by this peer only): a deliberate desync.</summary>
    public Action<SessionPeer, long>? Sabotage { get; set; }

    /// <summary>Submits the script for every newly opened input Tick, then Ticks when the session allows it.</summary>
    public void Step(int ticks)
    {
        if (Session is null || World.CurrentTick >= ticks)
        {
            return;
        }

        if (_scripted < Session.OpenInputTick && Session.OpenInputTick < ticks)
        {
            _scripted = Session.OpenInputTick;
            SessionRig.Script(Session, _scripted);
        }

        long tick = World.CurrentTick;
        Sabotage?.Invoke(this, tick);
        if (!Session.TryBeginTick())
        {
            Stalls++;
            return;
        }

        Assert.True(World.Tick());
        Session.EndTick();
        Hashes.Add(World.Hash());
    }

    public void Dispose()
    {
        Session?.Dispose();
        Session = null;
        World.Dispose();
    }
}
