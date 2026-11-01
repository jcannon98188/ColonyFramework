using System.Diagnostics;
using System.Globalization;
using Colony.Diagnostics.Inspection;
using Colony.Testing;

namespace Colony.Samples.Hello.Tests;

/// <summary>Ticket AC #3: the headless sample host built from the hello-colony sketch runs on JIT and Native AOT and prints one World hash.</summary>
public sealed class HelloColonyHostTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hello-colony-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_host_runs_the_sketch_saves_the_world_and_prints_the_same_hash_on_1_and_4_workers()
    {
        string dir = TempDir();
        try
        {
            string save = Path.Combine(dir, "colony.save");
            var output = new StringWriter();
            var error = new StringWriter();

            int exit = HelloColonyHost.Run(["--save", save], output, error);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            Assert.True(File.Exists(save));
            ulong hash = HelloColonyHost.ParseHash(output.ToString());
            Assert.NotEqual(0UL, hash);
            Assert.Contains("loaded hash matches", output.ToString(), StringComparison.Ordinal);

            var four = new StringWriter();
            Assert.Equal(0, HelloColonyHost.Run(["--workers", "4", "--no-save"], four, TextWriter.Null));
            Assert.Equal(hash, HelloColonyHost.ParseHash(four.ToString()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Counters_and_trace_flags_attach_the_diagnostics_sinks()
    {
        string dir = TempDir();
        try
        {
            string trace = Path.Combine(dir, "trace.json");
            var output = new StringWriter();

            int exit = HelloColonyHost.Run(["--ticks", "20", "--no-save", "--counters", "--trace", trace], output, TextWriter.Null);

            Assert.Equal(0, exit);
            Assert.Contains("dotnet-counters", output.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(trace));
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(trace));
            Assert.True(document.RootElement.GetProperty("traceEvents").GetArrayLength() > 20);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unknown_options_exit_2()
    {
        Assert.Equal(2, HelloColonyHost.Run(["--bogus"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, HelloColonyHost.Run(["--ticks"], TextWriter.Null, TextWriter.Null));
    }

    /// <summary>TASK-29 AC #1, #2, #4: colony-inspect against the running sample host over loopback TCP.</summary>
    [Fact]
    public void Colony_inspect_connects_to_the_running_host_reads_pauses_steps_mutates_and_rewinds()
    {
        string dir = TempDir();
        var output = new ThreadSafeWriter();
        var error = new ThreadSafeWriter();
        int exit = -1;
        string replay = Path.Combine(dir, "colony.replay");
        var host = new Thread(() => exit = HelloColonyHost.Run(
            ["--ticks", "0", "--no-save", "--colonists", "10", "--inspect", "0", "--inspect-token", "hello-token", "--allow-mutation", "--replay", replay],
            output,
            error))
        { IsBackground = true };
        host.Start();
        try
        {
            string port = WaitForPort(output);
            string[] at = ["--port", port, "--token", "hello-token"];
            string[] Args(params string[] command) => [.. command, .. at];

            // Reads at a Tick boundary while the host runs.
            var status = Cli(Args("status"));
            Assert.Equal(0, status.Exit);
            Assert.Contains("paused: false", status.Out, StringComparison.Ordinal);
            Assert.Contains("Hunger", Cli(Args("schema")).Out, StringComparison.Ordinal);
            Assert.Equal(10, InspectJson.Read(Cli(Args("entities", "--json")).Out)["total"].AsWhole);
            Assert.Equal(0, Cli(Args("field", "heat", "0", "0", "--w", "2")).Exit);
            Assert.Equal(0, Cli(Args("resources")).Exit);
            Assert.Equal(0, Cli(Args("events", WorldBuilder.StructuralFailureQueueName)).Exit);
            Assert.Equal(0, Cli(Args("diagnostics")).Exit);
            Assert.Equal(0, Cli(Args("inspectors")).Exit);

            // Pause and step through the shared Tick gate.
            Assert.Equal(0, Cli(Args("pause")).Exit);
            long paused = InspectJson.Read(Cli(Args("status", "--json")).Out)["tick"].AsWhole;
            Assert.Contains("paused: true", Cli(Args("status")).Out, StringComparison.Ordinal);
            Assert.Equal(0, Cli(Args("step", "3")).Exit);
            using (var client = InspectionClient.ConnectTcp(int.Parse(port, CultureInfo.InvariantCulture), "hello-token"))
            {
                Assert.Equal(paused + 3, client.WaitForTick(paused + 3));
            }

            // A debug mutation lands at the next Tick and is recorded in the replay log.
            string first = InspectJson.Read(Cli(Args("entities", "--json")).Out)["entities"][0].AsWhole.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(0, Cli(Args("mutate", "destroy", first)).Exit);
            Assert.Equal(0, Cli(Args("step")).Exit);
            using (var client = InspectionClient.ConnectTcp(int.Parse(port, CultureInfo.InvariantCulture), "hello-token"))
            {
                client.WaitForTick(paused + 4);
                Assert.Equal(9, client.Request("status")["entities"].AsWhole);
            }

            // Replay-based rewind shows the past World, live returns.
            var rewind = Cli(Args("rewind", (paused + 2).ToString(CultureInfo.InvariantCulture)));
            Assert.Equal(0, rewind.Exit);
            Assert.Equal(10, InspectJson.Read(Cli(Args("entities", "--json")).Out)["total"].AsWhole);
            Assert.Equal(0, Cli(Args("live")).Exit);

            // A wrong token is refused.
            Assert.Equal(3, Cli("status", "--port", port, "--token", "wrong").Exit);

            Assert.Equal(0, Cli(Args("shutdown")).Exit);
            Assert.True(host.Join(TimeSpan.FromSeconds(30)), "the host did not stop after shutdown");
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.Snapshot());
            Assert.NotEqual(0UL, HelloColonyHost.ParseHash(output.Snapshot()));
            Assert.True(File.Exists(replay));
            var log = Persistence.ReplayLog.Open(replay);
            Assert.Equal(1, log.Frame(paused + 3).Count);
        }
        finally
        {
            host.Join(TimeSpan.FromSeconds(5));
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_read_only_host_refuses_mutations_and_the_default_host_has_no_inspection_server()
    {
        var output = new ThreadSafeWriter();
        var host = new Thread(() => HelloColonyHost.Run(["--ticks", "0", "--no-save", "--inspect", "0", "--inspect-token", "ro-token"], output, TextWriter.Null)) { IsBackground = true };
        host.Start();
        try
        {
            string port = WaitForPort(output);
            var forbidden = Cli("mutate", "destroy", "1", "--port", port, "--token", "ro-token");
            Assert.Equal(3, forbidden.Exit);
            Assert.Contains("forbidden", forbidden.Err, StringComparison.Ordinal);
            Assert.Equal(0, Cli("shutdown", "--port", port, "--token", "ro-token").Exit);
        }
        finally
        {
            host.Join(TimeSpan.FromSeconds(30));
        }

        var plain = new StringWriter();
        Assert.Equal(0, HelloColonyHost.Run(["--ticks", "2", "--no-save"], plain, TextWriter.Null));
        Assert.DoesNotContain("inspect:", plain.ToString(), StringComparison.Ordinal);
    }

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = InspectCli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Waits for the host's <c>inspect: port N ...</c> line and returns the port.</summary>
    private static string WaitForPort(ThreadSafeWriter output)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            foreach (string line in output.Snapshot().Split('\n'))
            {
                if (line.StartsWith("inspect: port ", StringComparison.Ordinal))
                {
                    return line["inspect: port ".Length..].Split(' ')[0].Trim();
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "the host never announced its inspection port: " + output.Snapshot());
            Thread.Sleep(10);
        }
    }

    /// <summary>A StringWriter the host thread writes and the test thread reads.</summary>
    private sealed class ThreadSafeWriter : TextWriter
    {
        private readonly StringWriter _inner = new();
        private readonly Lock _lock = new();

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _inner.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _inner.Write(value);
            }
        }

        public string Snapshot()
        {
            lock (_lock)
            {
                return _inner.ToString();
            }
        }
    }

    [Fact]
    public void The_native_aot_build_of_the_host_prints_the_same_hash_as_the_jit_run()
    {
        string? exe = PublishedHost.Find("HelloColony");
        if (exe is null)
        {
            Assert.Skip("Publish the sample first: dotnet publish samples/HelloColony -c Release -r <rid> (see samples/README.md).");
        }

        var jit = new StringWriter();
        Assert.Equal(0, HelloColonyHost.Run(["--ticks", "300", "--no-save"], jit, TextWriter.Null));

        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--ticks");
        start.ArgumentList.Add("300");
        start.ArgumentList.Add("--no-save");
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error + output);
        Assert.Equal(HelloColonyHost.ParseHash(jit.ToString()).ToString("X16", CultureInfo.InvariantCulture), HelloColonyHost.ParseHash(output).ToString("X16", CultureInfo.InvariantCulture));
    }
}
