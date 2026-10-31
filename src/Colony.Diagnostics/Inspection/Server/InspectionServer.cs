using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Colony.Diagnostics.Inspection;

/// <summary>
/// The inspection server (spec section 10.2): host-started, off by default, token-protected and read-only unless
/// <see cref="InspectionServerOptions.AllowMutation"/> is set. It serves the protocol of <see cref="InspectCodec"/>
/// in-process (<see cref="Connect"/>) or over a loopback TCP socket (<see cref="ListenTcp"/>).
/// <para>
/// Every request runs on the host's Tick thread at a Tick boundary: socket threads only queue requests, and the
/// host calls <see cref="Pump"/> between Ticks, so reads see a consistent World and never race a Tick. Nothing the
/// server does runs inside <see cref="World.Tick"/>. Pause and step go through the shared <see cref="TickGate"/>;
/// mutations are debug Input Commands (<see cref="DebugCommands"/>) applied through the command buffer at the next
/// Tick and recorded by the host's replay recorder. Building with <c>COLONY_NO_INSPECT</c> removes this type.
/// </para>
/// </summary>
public sealed partial class InspectionServer : IDisposable
{
    private readonly ConcurrentQueue<PendingRequest> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly byte[] _token;
    private readonly List<TcpClient> _connections = [];
    private readonly Lock _connectionsLock = new();
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _disposed;

    public InspectionServer(World world, InspectionServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        Options = options ?? new InspectionServerOptions();
        if (Options.Token is { Length: 0 })
        {
            throw new ArgumentException("The inspection token must not be empty; pass null to generate one.", nameof(options));
        }

        Token = Options.Token ?? RandomNumberGenerator.GetHexString(32, lowercase: true);
        _token = Encoding.UTF8.GetBytes(Token);
        World = world;
        Gate = Options.TickGate ?? world.TickGate;
    }

    /// <summary>The live World this server inspects (a rewound view is separate; see <see cref="InspectedWorld"/>).</summary>
    public World World { get; }

    public InspectionServerOptions Options { get; }

    /// <summary>The token clients must present.</summary>
    public string Token { get; }

    public TickGate? Gate { get; }

    /// <summary>The loopback endpoint after <see cref="ListenTcp"/>, else null.</summary>
    public IPEndPoint? Endpoint { get; private set; }

    /// <summary>Set by a client's <c>shutdown</c> request: the host should stop its loop.</summary>
    public bool ShutdownRequested { get; private set; }

    /// <summary>Requests answered so far (any outcome).</summary>
    public long RequestsServed { get; private set; }

    /// <summary>Connections refused for a missing or wrong token.</summary>
    public long ConnectionsRefused => Interlocked.Read(ref _refused);

    private long _refused;

    /// <summary>
    /// Starts listening on 127.0.0.1 (never another interface). Port 0 picks a free port; read it from
    /// <see cref="Endpoint"/>. Connections are accepted on a background thread.
    /// </summary>
    public IPEndPoint ListenTcp(int port = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
        {
            throw new InvalidOperationException("The server is already listening.");
        }

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "colony-inspect accept" };
        _acceptThread.Start();
        return Endpoint;
    }

    /// <summary>
    /// An in-process client. Its requests run synchronously on the calling thread, so call it only where
    /// <see cref="Pump"/> could run: on the Tick thread, between Ticks.
    /// </summary>
    public InspectionClient Connect(string token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = new Session();
        return InspectionClient.Over(request => Handle(session, request), owner: null, token);
    }

    /// <summary>Answers every queued socket request on the calling thread (the Tick thread, between Ticks). Returns how many ran.</summary>
    public int Pump()
    {
        int count = 0;
        while (_queue.TryDequeue(out var pending))
        {
            pending.Response = Handle(pending.Session, pending.Request);
            pending.Done.Set();
            count++;
        }

        return count;
    }

    /// <summary>Blocks up to <paramref name="timeout"/> until a socket request is queued (for a paused host loop that would otherwise spin).</summary>
    public bool WaitForRequest(TimeSpan timeout)
    {
        if (!_queue.IsEmpty)
        {
            return true;
        }

        return _signal.Wait(timeout);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener?.Stop();
        lock (_connectionsLock)
        {
            foreach (var connection in _connections)
            {
                connection.Dispose();
            }

            _connections.Clear();
        }

        while (_queue.TryDequeue(out var pending))
        {
            pending.Response = Failure(pending.Request, "closed", "The inspection server shut down.");
            pending.Done.Set();
        }

        EndRewind();
        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            lock (_connectionsLock)
            {
                if (_disposed)
                {
                    client.Dispose();
                    return;
                }

                _connections.Add(client);
            }

            new Thread(() => Serve(client)) { IsBackground = true, Name = "colony-inspect connection" }.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        var session = new Session();
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            InspectCodec.ReadPreamble(stream);
            while (!_disposed)
            {
                var request = InspectCodec.ReadFrame(stream);
                if (request is null)
                {
                    return;
                }

                var pending = new PendingRequest(session, request);
                _queue.Enqueue(pending);
                _signal.Release();
                pending.Done.Wait();
                InspectCodec.WriteFrame(stream, pending.Response!);
                if (!session.Authenticated)
                {
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (FormatException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            lock (_connectionsLock)
            {
                _connections.Remove(client);
            }

            client.Dispose();
        }
    }

    /// <summary>Runs one request for a session; never throws (errors become <c>ok: false</c> responses).</summary>
    private InspectValue Handle(Session session, InspectValue request)
    {
        RequestsServed++;
        if (request.Kind != InspectValueKind.Map || request["op"].Kind != InspectValueKind.Text)
        {
            return Failure(request, "bad-request", "A request is a map with a text 'op'.");
        }

        string op = request["op"].AsText;
        if (!session.Authenticated)
        {
            if (!string.Equals(op, "hello", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refused);
                return Failure(request, "unauthorized", "Send 'hello' with the token first.");
            }

            var presented = request["token"];
            if (presented.Kind != InspectValueKind.Text || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented.AsText), _token))
            {
                Interlocked.Increment(ref _refused);
                return Failure(request, "unauthorized", "Missing or wrong inspection token.");
            }

            if (request["version"] is { Kind: InspectValueKind.Whole } version && version.AsWhole != InspectCodec.ProtocolVersion)
            {
                return Failure(request, "version", $"The server speaks protocol {InspectCodec.ProtocolVersion}; the client sent {version.AsWhole}.");
            }

            session.Authenticated = true;
        }

        try
        {
            return Success(request, Dispatch(op, request));
        }
        catch (InspectionException e)
        {
            return Failure(request, e.Code, e.Message);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException or IOException or NotSupportedException)
        {
            return Failure(request, "failed", e.Message);
        }
    }

    private static InspectValue Success(InspectValue request, InspectValue result)
        => InspectValue.Map(new("id", request.Kind == InspectValueKind.Map ? request["id"] : InspectValue.Null), new("ok", true), new("result", result));

    private static InspectValue Failure(InspectValue request, string code, string message)
        => InspectValue.Map(
            new("id", request.Kind == InspectValueKind.Map ? request["id"] : InspectValue.Null),
            new("ok", false),
            new("code", code),
            new("error", message));

    private sealed class Session
    {
        public bool Authenticated;
    }

    private sealed class PendingRequest(Session session, InspectValue request)
    {
        public Session Session { get; } = session;

        public InspectValue Request { get; } = request;

        public InspectValue? Response { get; set; }

        public ManualResetEventSlim Done { get; } = new(false);
    }
}
