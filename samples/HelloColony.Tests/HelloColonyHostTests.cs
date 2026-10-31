using System.Diagnostics;
using System.Globalization;
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
