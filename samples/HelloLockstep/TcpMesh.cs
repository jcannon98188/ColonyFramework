using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Colony.Samples.Lockstep;

/// <summary>
/// The sample's transport (the Framework has none): every seat listens on its own loopback port and sends to the
/// others over one lazily connected socket each. Frames on the wire are <c>int32 length, byte kind, payload</c>.
/// Sending never blocks the game loop: messages go to a per-destination queue drained by a sender thread that
/// connects (and reconnects) in the background; the queue is bounded and drops its oldest entry, which is fine
/// because the lockstep messages are idempotent (cumulative acks, resent frames). Received packets land in a
/// queue the game loop drains between Ticks.
/// </summary>
public sealed class TcpMesh : IDisposable
{
    public const byte KindLockstep = 0;
    public const byte KindJoinRequest = 1;
    public const byte KindJoinChunk = 2;

    private const int MaxQueued = 256;

    private readonly int _seat;
    private readonly int[] _ports;
    private readonly TcpListener _listener;
    private readonly ConcurrentQueue<(byte Kind, byte[] Payload)> _inbox = new();
    private readonly Outbox?[] _outboxes;
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<TcpClient> _accepted = [];

    public TcpMesh(int seat, int[] ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        _seat = seat;
        _ports = ports;
        _listener = new TcpListener(IPAddress.Loopback, ports[seat]);
        _listener.Start();
        _outboxes = new Outbox?[ports.Length];
        new Thread(AcceptLoop) { IsBackground = true, Name = $"mesh-accept-{seat}" }.Start();
    }

    public bool TryReceive(out byte kind, out byte[] payload)
    {
        bool got = _inbox.TryDequeue(out var packet);
        kind = packet.Kind;
        payload = packet.Payload ?? [];
        return got;
    }

    /// <summary>Queues a copy of <paramref name="payload"/> for seat <paramref name="to"/>.</summary>
    public void Send(int to, byte kind, ReadOnlySpan<byte> payload)
    {
        var box = _outboxes[to] ??= new Outbox(this, to);
        var packet = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length);
        packet[4] = kind;
        payload.CopyTo(packet.AsSpan(5));
        box.Enqueue(packet);
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _listener.Stop();
        foreach (var box in _outboxes)
        {
            box?.Wake();
        }

        lock (_accepted)
        {
            foreach (var client in _accepted)
            {
                client.Dispose();
            }
        }
    }

    private void AcceptLoop()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var client = _listener.AcceptTcpClient();
                lock (_accepted)
                {
                    _accepted.Add(client);
                }

                new Thread(() => ReadLoop(client)) { IsBackground = true, Name = $"mesh-read-{_seat}" }.Start();
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ReadLoop(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var header = new byte[5];
            while (!_cancel.IsCancellationRequested)
            {
                stream.ReadExactly(header);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                var payload = new byte[length];
                stream.ReadExactly(payload);
                _inbox.Enqueue((header[4], payload));
            }
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// One destination's queue and sender thread. The sender thread owns the signal and disposes it when it stops;
    /// every release (<see cref="Enqueue"/>, <see cref="Wake"/>) happens under <see cref="_lock"/> and checks
    /// <see cref="_closed"/> first, so a mesh disposed while its sender is already stopping never touches a disposed
    /// signal (TASK-44: <c>ObjectDisposedException</c> from <c>Wake</c> crashed the Linux test host).
    /// </summary>
    private sealed class Outbox : IDisposable
    {
        private readonly TcpMesh _mesh;
        private readonly int _to;
        private readonly Queue<byte[]> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Lock _lock = new();
        private bool _closed;

        public Outbox(TcpMesh mesh, int to)
        {
            _mesh = mesh;
            _to = to;
            new Thread(Run) { IsBackground = true, Name = $"mesh-send-{mesh._seat}-to-{to}" }.Start();
        }

        public void Enqueue(byte[] packet)
        {
            lock (_lock)
            {
                if (_closed)
                {
                    return; // the mesh is shutting down: nothing will send it
                }

                if (_queue.Count >= MaxQueued)
                {
                    _queue.Dequeue();
                }

                _queue.Enqueue(packet);
                _signal.Release();
            }
        }

        /// <summary>Unblocks the sender thread so it sees the mesh's cancellation; a no-op once the sender has stopped.</summary>
        public void Wake()
        {
            lock (_lock)
            {
                if (!_closed)
                {
                    _signal.Release();
                }
            }
        }

        /// <summary>Called by the sender thread only, as it stops.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _signal.Dispose();
            }
        }

        private void Run()
        {
            TcpClient? client = null;
            try
            {
                while (!_mesh._cancel.IsCancellationRequested)
                {
                    _signal.Wait();
                    byte[]? packet;
                    lock (_lock)
                    {
                        _queue.TryDequeue(out packet);
                    }

                    if (packet is null)
                    {
                        continue;
                    }

                    for (int attempt = 0; attempt < 50 && !_mesh._cancel.IsCancellationRequested; attempt++)
                    {
                        try
                        {
                            if (client is null)
                            {
                                client = new TcpClient { NoDelay = true };
                                client.Connect(IPAddress.Loopback, _mesh._ports[_to]);
                            }

                            client.GetStream().Write(packet);
                            break;
                        }
                        catch (SocketException)
                        {
                            client?.Dispose();
                            client = null;
                            Thread.Sleep(20);
                        }
                        catch (IOException)
                        {
                            client?.Dispose();
                            client = null;
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                client?.Dispose();
                Dispose();
            }
        }
    }
}
