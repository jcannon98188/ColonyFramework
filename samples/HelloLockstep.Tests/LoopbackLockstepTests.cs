using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Colony.Samples.Lockstep.Tests;

/// <summary>TASK-36 AC #2 and #3: real peers over loopback sockets stay in sync, and a third peer joins mid-run.</summary>
public sealed class LoopbackLockstepTests
{
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

            return listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var l in listeners)
            {
                l.Stop();
            }
        }
    }

    private static PeerResult[] RunInProcess(int seats, int ticks, int[]? active = null, int joinSeat = -1, int joinMargin = 40, int tickMs = 2)
    {
        int[] ports = FreePorts(seats);
        var results = new PeerResult[seats];
        var threads = new List<Thread>();
        for (int seat = 0; seat < seats; seat++)
        {
            int s = seat;
            bool joiner = seat == joinSeat;
            var options = new PeerOptions
            {
                Seat = s,
                Ports = ports,
                Ticks = ticks,
                ActiveSeats = active,
                JoinFrom = joiner ? 0 : -1,
                JoinMarginTicks = joinMargin,
                TickMilliseconds = tickMs,
                Workers = 1 + (s % 2),
            };
            var t = new Thread(() => results[s] = PeerHost.Run(options)) { IsBackground = true };
            threads.Add(t);
        }

        for (int i = 0; i < threads.Count; i++)
        {
            if (i == joinSeat)
            {
                Thread.Sleep(800);
            }

            threads[i].Start();
        }

        foreach (var t in threads)
        {
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), "a peer did not finish");
        }

        return results;
    }

    private static void AssertInSync(PeerResult[] results, int ticks)
    {
        foreach (var r in results)
        {
            Assert.Null(r.Error);
            Assert.False(r.Desynced, $"peer {r.Seat} reported a desync");
            Assert.Equal(ticks, r.FinalTick);
        }

        foreach (var r in results)
        {
            for (long tick = r.StartTick; tick < ticks; tick++)
            {
                Assert.Equal(results[0].HashAt(tick), r.HashAt(tick));
            }
        }
    }

    [Fact]
    public void Two_peers_over_loopback_sockets_hash_identically_every_Tick()
    {
        var results = RunInProcess(2, ticks: 200);

        AssertInSync(results, 200);
        Assert.NotEqual(0UL, results[0].FinalHash);
        Assert.Equal(results[0].FinalHash, results[1].FinalHash);
    }

    [Fact]
    public void A_third_peer_joins_mid_run_over_the_socket_and_plays_in_lockstep()
    {
        var results = RunInProcess(3, ticks: 300, active: [0, 1], joinSeat: 2);

        AssertInSync(results, 300);
        Assert.True(results[2].StartTick > 0, "the joiner started from a join package, not from Tick 0");
        Assert.True(results[2].JoinTick > results[2].StartTick);
        Assert.Equal(results[0].FinalHash, results[2].FinalHash);
        Assert.Equal(results[0].FinalHash, results[1].FinalHash);
    }

    [Fact]
    public void Separate_processes_stay_in_sync_and_one_joins_mid_run()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "HelloLockstep.dll");
        Assert.True(File.Exists(dll), dll);
        int[] ports = FreePorts(3);
        string portList = string.Join(',', ports);
        var processes = new List<Process>();
        try
        {
            processes.Add(Start(dll, $"--seat 0 --ports {portList} --active 0,1 --ticks 300"));
            processes.Add(Start(dll, $"--seat 1 --ports {portList} --active 0,1 --ticks 300"));
            Thread.Sleep(400);
            processes.Add(Start(dll, $"--seat 2 --ports {portList} --join-from 0 --ticks 300"));
            var hashes = new List<string>();
            string joinerOutput = string.Empty;
            foreach (var p in processes)
            {
                string output = p.StandardOutput.ReadToEnd();
                joinerOutput = output;
                Assert.True(p.WaitForExit(60_000));
                Assert.Equal(0, p.ExitCode);
                string line = output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith("hash ", StringComparison.Ordinal));
                hashes.Add(line);
            }

            Assert.DoesNotContain("ran Ticks 0..", joinerOutput, StringComparison.Ordinal);
            Assert.Contains("joined from Tick", joinerOutput, StringComparison.Ordinal);
            Assert.Single(hashes.Distinct());
            Assert.StartsWith("hash 0x", hashes[0], StringComparison.Ordinal);
        }
        finally
        {
            foreach (var p in processes)
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }

                p.Dispose();
            }
        }
    }

    private static Process Start(string dll, string args)
    {
        var info = new ProcessStartInfo("dotnet", string.Create(CultureInfo.InvariantCulture, $"\"{dll}\" {args}"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        return Process.Start(info)!;
    }
}
