using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>AC #4: the harness host prints hashes a separately built (Native AOT) process can be compared on; the AOT comparison runs when the host is published.</summary>
public sealed class HarnessHostTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 10, WorkerCounts = [1, 4] };

    [Fact]
    public void The_host_prints_one_line_per_worker_count_that_parses_back_to_the_same_hashes()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = HarnessHost.Run(["--scenario", "counter", "--ticks", "6", "--every", "2", "--workers", "1,2"], [new CounterScenario()], output, error);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        var expected = DeterminismHarness.Run(new CounterScenario(), new DeterminismOptions { Ticks = 6, CheckpointEvery = 2, WorkerCounts = [1, 2] });
        var parsed = HarnessHost.Parse(output.ToString(), "counter", 42, 6, 2);
        Assert.Equal([1, 2], parsed.Runs.Select(r => r.Workers));
        Assert.Equal(expected.Hashes.ToArray(), parsed.Hashes.ToArray());
        Assert.Null(expected.FirstDivergenceFrom(parsed));
    }

    [Fact]
    public void A_failing_scenario_makes_the_host_exit_1_with_the_report_on_stderr()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = HarnessHost.Run(["--ticks", "6", "--workers", "1,4"], [new CounterScenario { DivergeFromTick = 2 }], output, error);

        Assert.Equal(1, exit);
        Assert.Contains("First diverging Tick 2", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_scenarios_and_options_exit_2()
    {
        Assert.Equal(2, HarnessHost.Run(["--scenario", "nope"], [new CounterScenario()], TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, HarnessHost.Run(["--bogus", "1"], [new CounterScenario()], TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, HarnessHost.Run(["--ticks"], [new CounterScenario()], TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void A_different_hash_sequence_reports_the_first_diverging_tick()
    {
        var a = DeterminismHarness.Run(new CounterScenario(), new DeterminismOptions { Ticks = 6, WorkerCounts = [1] });
        var b = DeterminismHarness.Run(new CounterScenario { DivergeFromTick = 4 }, new DeterminismOptions { Ticks = 6, WorkerCounts = [1] });
        var c = HarnessHost.Parse("1 " + string.Join(' ', a.Hashes.ToArray().Take(4).Select(h => h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture))), "counter", 42, 6, 1);

        Assert.Null(a.FirstDivergenceFrom(a));
        Assert.Equal(4, a.FirstDivergenceFrom(b));
        Assert.Equal(5, a.FirstDivergenceFrom(c)); // the host run stopped after 4 checkpoints
    }

    [Fact]
    public void The_native_aot_host_produces_the_same_hashes_as_the_jit_run()
    {
        string? exe = PublishedHost.Find("Colony.Testing.AotHost");
        if (exe is null)
        {
            Assert.Skip("Publish the AOT host first: dotnet publish tests/Colony.Testing.AotHost -c Release -r <rid> (see CONVENTIONS, TASK-10).");
        }

        long? colonist = PublishedHost.CompareWithJit(exe, new ColonistHarnessScenario(), Options, out var jit, out var aot);
        Assert.True(colonist is null, $"colonist: AOT diverged from JIT at Tick {colonist}\nJIT:\n{jit.Describe()}\nAOT:\n{aot.Describe()}");
        Assert.Equal(2, aot.Runs.Count);

        long? fields = PublishedHost.CompareWithJit(exe, new FieldScenario(), Options, out _, out _);
        Assert.True(fields is null, $"fields: AOT diverged from JIT at Tick {fields}");

        long? streaming = PublishedHost.CompareWithJit(exe, new StreamingScenario(), Options, out _, out _);
        Assert.True(streaming is null, $"streaming: AOT diverged from JIT at Tick {streaming}");
    }
}
