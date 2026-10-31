using System.Globalization;
using System.Net.Sockets;

namespace Colony.Diagnostics.Inspection;

/// <summary>A request the inspection server refused or could not run; <see cref="Code"/> is the protocol error code.</summary>
public sealed class InspectionException : Exception
{
    public InspectionException()
        : this("failed", "The inspection request failed.")
    {
    }

    public InspectionException(string message)
        : this("failed", message)
    {
    }

    public InspectionException(string message, Exception innerException)
        : base(message, innerException) => Code = "failed";

    public InspectionException(string code, string message)
        : base(message) => Code = code;

    /// <summary><c>unauthorized</c>, <c>forbidden</c>, <c>bad-request</c>, <c>unknown-op</c>, <c>not-available</c>, <c>version</c>, <c>closed</c> or <c>failed</c>.</summary>
    public string Code { get; }
}

/// <summary>
/// A client of the inspection protocol (spec section 10.2), the transport <c>colony-inspect</c> uses. Connect over
/// loopback TCP with <see cref="ConnectTcp"/> or in-process with <c>InspectionServer.Connect</c>; both send
/// <c>hello</c> with the token first and expose its reply as <see cref="Info"/>. Requests are synchronous and
/// one at a time; the server answers each at the next Tick boundary of its host.
/// </summary>
public sealed class InspectionClient : IDisposable
{
    private readonly Func<InspectValue, InspectValue> _send;
    private readonly IDisposable? _owner;
    private readonly Lock _lock = new();
    private long _nextId;

    private InspectionClient(Func<InspectValue, InspectValue> send, IDisposable? owner)
    {
        _send = send;
        _owner = owner;
        Info = InspectValue.Null;
    }

    /// <summary>The server's <c>hello</c> reply: protocol version, Tick, schema hash, whether mutation is allowed.</summary>
    public InspectValue Info { get; private set; }

    /// <summary>Connects to a server on this machine and authenticates; throws <see cref="InspectionException"/> (code <c>unauthorized</c>) for a wrong token.</summary>
    public static InspectionClient ConnectTcp(int port, string token, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            tcp.Connect(System.Net.IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            int ms = (int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
            stream.ReadTimeout = ms;
            stream.WriteTimeout = ms;
            InspectCodec.WritePreamble(stream);
            var client = new InspectionClient(
                request =>
                {
                    InspectCodec.WriteFrame(stream, request);
                    return InspectCodec.ReadFrame(stream) ?? throw new InspectionException("closed", "The server closed the connection.");
                },
                tcp);
            client.Hello(token);
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    internal static InspectionClient Over(Func<InspectValue, InspectValue> send, IDisposable? owner, string token)
    {
        var client = new InspectionClient(send, owner);
        client.Hello(token);
        return client;
    }

    /// <summary>Sends a request map (the <c>op</c> key plus arguments) and returns the reply's <c>result</c>, or throws <see cref="InspectionException"/>.</summary>
    public InspectValue Request(string op, params ReadOnlySpan<KeyValuePair<string, InspectValue>> arguments)
    {
        ArgumentNullException.ThrowIfNull(op);
        var entries = new List<KeyValuePair<string, InspectValue>>(arguments.Length + 2) { new("op", op) };
        foreach (var argument in arguments)
        {
            entries.Add(argument);
        }

        return Request(InspectValue.Map(entries));
    }

    /// <summary>Sends a raw request map (it must carry <c>op</c>); an <c>id</c> is added when missing.</summary>
    public InspectValue Request(InspectValue request)
    {
        var response = Send(request);
        if (response.Kind != InspectValueKind.Map || response["ok"].Kind != InspectValueKind.Boolean)
        {
            throw new InspectionException("bad-request", "Malformed response.");
        }

        if (!response["ok"].AsBoolean)
        {
            throw new InspectionException(response["code"].Kind == InspectValueKind.Text ? response["code"].AsText : "failed", response["error"].Kind == InspectValueKind.Text ? response["error"].AsText : "Request failed.");
        }

        return response["result"];
    }

    /// <summary>Sends a raw request and returns the full response map (<c>ok</c>, <c>result</c> or <c>code</c>/<c>error</c>).</summary>
    public InspectValue Send(InspectValue request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lock)
        {
            if (request.Kind == InspectValueKind.Map && !request.Has("id"))
            {
                request = request.With("id", ++_nextId);
            }

            return _send(request);
        }
    }

    /// <summary>Lets <paramref name="ticks"/> Ticks through the server's gate and waits until the World has run them.</summary>
    public long StepAndWait(int ticks, TimeSpan? timeout = null)
    {
        long start = Request("status")["tick"].AsWhole;
        Request("step", new KeyValuePair<string, InspectValue>("count", ticks));
        return WaitForTick(start + ticks, timeout);
    }

    /// <summary>Polls <c>status</c> until the World reaches <paramref name="tick"/>; returns the Tick seen.</summary>
    public long WaitForTick(long tick, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            long now = Request("status")["tick"].AsWhole;
            if (now >= tick)
            {
                return now;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"The World stayed at Tick {now}; expected {tick}."));
            }

            Thread.Sleep(5);
        }
    }

    public void Dispose() => _owner?.Dispose();

    private void Hello(string token)
    {
        Info = Request("hello", new("token", token), new("version", InspectCodec.ProtocolVersion));
    }
}
