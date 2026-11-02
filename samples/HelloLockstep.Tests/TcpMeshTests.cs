using System.Net;
using System.Net.Sockets;

namespace Colony.Samples.Lockstep.Tests;

/// <summary>TASK-44: shutting the mesh down never throws, whatever its sender threads are doing (the Linux CI crash was <c>ObjectDisposedException</c> from waking a sender that had already stopped).</summary>
public sealed class TcpMeshTests
{
    [Fact]
    public void Dispose_and_Send_after_the_senders_stopped_do_not_throw()
    {
        int[] ports = FreePorts(2);
        var mesh = new TcpMesh(0, ports);
        mesh.Send(1, TcpMesh.KindLockstep, [1, 2, 3]); // starts the sender thread for seat 1
        mesh.Dispose(); // cancels and wakes it: the sender stops and disposes its signal

        // Give the sender time to stop, then hit every path that touches its signal again.
        Thread.Sleep(200);
        mesh.Send(1, TcpMesh.KindLockstep, [4]);
        mesh.Dispose();
    }

    [Fact]
    public void Disposing_while_senders_are_busy_never_throws()
    {
        for (int round = 0; round < 50; round++)
        {
            int[] ports = FreePorts(3);
            var mesh = new TcpMesh(0, ports);
            for (int i = 0; i < 20; i++)
            {
                mesh.Send(1 + (i % 2), TcpMesh.KindLockstep, [(byte)i]);
            }

            mesh.Dispose();
            mesh.Send(1, TcpMesh.KindLockstep, [9]);
            mesh.Dispose();
        }
    }

    private static int[] FreePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                listeners.Add(l);
            }

            return [.. listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)];
        }
        finally
        {
            foreach (var l in listeners)
            {
                l.Stop();
            }
        }
    }
}
