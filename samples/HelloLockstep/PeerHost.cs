using System.Buffers.Binary;
using System.Diagnostics;
using Colony.Diagnostics;
using Colony.Persistence;

namespace Colony.Samples.Lockstep;

/// <summary>What one peer is asked to do. <see cref="Ports"/> has one loopback port per seat.</summary>
public sealed class PeerOptions
{
    public int Seat { get; init; }

    public int[] Ports { get; init; } = [];

    /// <summary>Run until the World reaches this Tick.</summary>
    public int Ticks { get; init; } = 300;

    /// <summary>Seats present from the start (null = every seat but this one). A joiner ignores it.</summary>
    public int[]? ActiveSeats { get; init; }

    /// <summary>Seat to fetch a join package from; -1 starts from Tick 0 like the founders.</summary>
    public int JoinFrom { get; init; } = -1;

    /// <summary>A joiner plays from its package's Tick plus this many Ticks (the others must not have run that far when it says hello).</summary>
    public int JoinMarginTicks { get; init; } = 60;

    /// <summary>Sleep after every Tick so the founders do not outrun a joiner (0 = as fast as possible).</summary>
    public int TickMilliseconds { get; init; } = 2;

    public int Workers { get; init; } = 1;

    public int InputDelay { get; init; } = 3;

    public int CheckpointEvery { get; init; } = 10;

    /// <summary>
    /// A founder that serves join packages: at this Tick, stop ticking until a joiner has fetched a package and its
    /// handshake has arrived (at most <see cref="AwaitJoinMilliseconds"/>), and ignore join requests that arrive
    /// earlier (the joiner repeats them). The other founders stall behind it within the input delay, so nobody runs
    /// past the joiner's join Tick however slowly it starts. -1 serves requests whenever they come and never waits.
    /// </summary>
    public int AwaitJoinAtTick { get; init; } = -1;

    /// <summary>How long <see cref="AwaitJoinAtTick"/> waits for a joiner before ticking on without one.</summary>
    public int AwaitJoinMilliseconds { get; init; } = 30_000;

    /// <summary>After the last Tick, keep exchanging messages this long so slower peers can still collect our frames.</summary>
    public int LingerMilliseconds { get; init; } = 400;

    public TextWriter? Log { get; init; }
}

/// <summary>What a peer ended with; <see cref="Hashes"/> holds the World hash after each Tick it ran, from <see cref="StartTick"/>.</summary>
public sealed class PeerResult
{
    public int Seat { get; init; }

    public long StartTick { get; init; }

    public long JoinTick { get; init; }

    public long FinalTick { get; init; }

    public ulong FinalHash { get; init; }

    public bool Desynced { get; init; }

    public long Stalls { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<ulong> Hashes { get; init; } = [];

    /// <summary>The hash after the World ran <paramref name="tick"/> (so its CurrentTick became tick + 1).</summary>
    public ulong HashAt(long tick) => Hashes[(int)(tick - StartTick)];
}

/// <summary>
/// One lockstep peer: its World, a <see cref="LockstepSession"/> and a <see cref="TcpMesh"/>. The loop is the one
/// from the guide: receive, submit the script for the open input Tick, <c>Tick()</c> when the session allows it,
/// then send one <c>NextMessage</c> to every other seat. A joiner first fetches a <see cref="JoinPackage"/> from a
/// founder over the same sockets.
/// </summary>
public static class PeerHost
{
    public static PeerResult Run(PeerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = LockstepGame.BuildSchema();
        var trail = new LockstepHashTrail();
        var gate = new TickGate();
        var worldOptions = new WorldOptions
        {
            Seed = LockstepGame.Seed,
            WorkerThreads = options.Workers,
            InitialEntityCapacity = 4096,
            TickGate = gate,
            Diagnostics = new DiagnosticsOptions { HashTrail = true, Listener = trail },
        };

        using var mesh = new TcpMesh(options.Seat, options.Ports);
        World? world = null;
        LockstepSession? session = null;
        bool desynced = false;
        var hashes = new List<ulong>();
        try
        {
            long joinTick = -1;
            if (options.JoinFrom >= 0)
            {
                var package = FetchJoinPackage(mesh, options, schema);
                world = package.Join(schema, new JoinOptions { WorldOptions = worldOptions });
                joinTick = package.EndTick + options.JoinMarginTicks;
                options.Log?.WriteLine($"seat {options.Seat}: joined from Tick {package.EndTick}, plays from Tick {joinTick}");
            }
            else
            {
                world = schema.CreateWorld(worldOptions);
            }

            long startTick = world.CurrentTick;
            session = new LockstepSession(world, new LockstepSessionOptions
            {
                LocalPeer = options.Seat,
                Peers = options.Ports.Length,
                ActivePeers = options.JoinFrom >= 0 ? null : options.ActiveSeats,
                JoinTick = joinTick,
                InputDelay = options.InputDelay,
                CheckpointEvery = options.CheckpointEvery,
                HashTrail = trail,
                Gate = gate,
            });
            session.Desynced += (_, d) =>
            {
                desynced = true;
                options.Log?.WriteLine($"seat {options.Seat}: DESYNC {d.Describe()}");
            };
            session.DesyncLocalised += (_, d) => options.Log?.WriteLine($"seat {options.Seat}: {d.Describe()}");

            long scripted = -1;
            int joiner = -1;
            Stopwatch? awaiting = null;
            joiner = Pump(mesh, session, world, options, joiner);
            while (world.CurrentTick < options.Ticks && !desynced && session.State != LockstepSessionState.Failed)
            {
                joiner = Pump(mesh, session, world, options, joiner);
                if (options.AwaitJoinAtTick >= 0 && world.CurrentTick == options.AwaitJoinAtTick
                    && !(joiner >= 0 && session.Peer(joiner).State == LockstepPeerState.Running))
                {
                    awaiting ??= Stopwatch.StartNew();
                    if (awaiting.ElapsedMilliseconds < options.AwaitJoinMilliseconds)
                    {
                        Send(mesh, session, options);
                        Thread.Sleep(1);
                        continue;
                    }
                }

                if (scripted < session.OpenInputTick && session.OpenInputTick < options.Ticks)
                {
                    scripted = session.OpenInputTick;
                    LockstepGame.Script(session, scripted);
                }

                if (session.Tick())
                {
                    hashes.Add(world.Hash());
                    if (options.TickMilliseconds > 0)
                    {
                        Thread.Sleep(options.TickMilliseconds);
                    }
                }
                else
                {
                    Thread.Sleep(1);
                }

                Send(mesh, session, options);
            }

            // Frames we already finished may still be unacknowledged: keep talking for a moment.
            var linger = Stopwatch.StartNew();
            while (linger.ElapsedMilliseconds < options.LingerMilliseconds)
            {
                joiner = Pump(mesh, session, world, options, joiner);
                Send(mesh, session, options);
                Thread.Sleep(2);
            }

            return new PeerResult
            {
                Seat = options.Seat,
                StartTick = startTick,
                JoinTick = session.JoinTick,
                FinalTick = world.CurrentTick,
                FinalHash = world.Hash(),
                Desynced = desynced,
                Stalls = session.Stalls,
                Hashes = hashes,
                Error = session.State == LockstepSessionState.Failed ? "the session failed: " + FailureText(session) : null,
            };
        }
#pragma warning disable CA1031 // A sample peer reports any failure in its result instead of crashing the host process.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new PeerResult { Seat = options.Seat, Error = exception.ToString(), Desynced = desynced };
        }
        finally
        {
            session?.Dispose();
            world?.Dispose();
        }
    }

    private static string FailureText(LockstepSession session)
    {
        var reasons = new List<string>();
        for (int peer = 0; peer < session.Peers; peer++)
        {
            if (peer != session.LocalPeer)
            {
                var status = session.Peer(peer);
                reasons.Add($"peer {peer} {status.State}: {string.Join("; ", status.Reasons)}");
            }
        }

        return string.Join(" | ", reasons);
    }

    private static void Send(TcpMesh mesh, LockstepSession session, PeerOptions options)
    {
        for (int to = 0; to < options.Ports.Length; to++)
        {
            if (to == options.Seat)
            {
                continue;
            }

            var message = session.NextMessage(to);
            if (!message.IsEmpty)
            {
                mesh.Send(to, TcpMesh.KindLockstep, message);
            }
        }
    }

    /// <summary>
    /// Hands every received packet to the session; answers join requests with a package captured between Ticks.
    /// Returns the seat last served a package (or <paramref name="joiner"/> when none was).
    /// </summary>
    private static int Pump(TcpMesh mesh, LockstepSession session, World world, PeerOptions options, int joiner)
    {
        while (mesh.TryReceive(out byte kind, out byte[] payload))
        {
            switch (kind)
            {
                case TcpMesh.KindLockstep:
                    session.Receive(payload);
                    break;
                case TcpMesh.KindJoinRequest:
                    int requester = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    if (world.CurrentTick < options.AwaitJoinAtTick)
                    {
                        break; // too early: the joiner asks again every second
                    }

                    joiner = requester;
                    var package = JoinPackage.Capture(world);
                    foreach (var chunk in package.ToChunks(16 * 1024))
                    {
                        mesh.Send(requester, TcpMesh.KindJoinChunk, chunk);
                    }

                    options.Log?.WriteLine($"seat {options.Seat}: served a join package at Tick {package.EndTick} to seat {requester}");
                    break;
            }
        }

        return joiner;
    }

    private static JoinPackage FetchJoinPackage(TcpMesh mesh, PeerOptions options, WorldSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var assembler = new JoinPackageAssembler();
        var request = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(request, options.Seat);
        var clock = Stopwatch.StartNew();
        long nextRequest = 0;
        while (!assembler.IsComplete)
        {
            if (clock.ElapsedMilliseconds >= nextRequest)
            {
                mesh.Send(options.JoinFrom, TcpMesh.KindJoinRequest, request);
                nextRequest = clock.ElapsedMilliseconds + 1000;
            }

            while (mesh.TryReceive(out byte kind, out byte[] payload))
            {
                if (kind == TcpMesh.KindJoinChunk)
                {
                    assembler.Add(payload);
                }
            }

            if (clock.ElapsedMilliseconds > 30_000)
            {
                throw new TimeoutException($"No join package from seat {options.JoinFrom} after 30 s.");
            }

            Thread.Sleep(2);
        }

        return assembler.Complete();
    }
}
