namespace Colony.Benchmarks.TickHarness.Tests;

/// <summary>
/// Ticket AC #2 and #4: the Tick harness runs the colonist scenario (churn, Fields, save/load) and writes a
/// JSON report comparable across runs; deterministic-count gates fail the run, timing is report-only.
/// The tests use tiny scenario sizes: long benchmark runs are not tests.
/// </summary>
public sealed class TickHarnessTests
{
    private static string TempFile(string suffix) => Path.Combine(Path.GetTempPath(), $"colony-tick-harness-{Guid.NewGuid():N}{suffix}");

    [Fact]
    public void A_smoke_run_writes_a_report_with_timing_counts_save_load_and_passing_gates()
    {
        string path = TempFile(".json");
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            int exit = TickHarness.Run(["--scenario", "colonist-100k", "--colonists", "2000", "--food", "100", "--ticks", "4", "--warmup", "12", "--out", path], output, error);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            var report = HarnessReport.ReadFile(path);
            var run = Assert.Single(report.Runs);
            Assert.Equal("colonist-100k", run.Scenario.Name);
            Assert.Equal(2000, run.Scenario.Colonists);
            Assert.Equal(1, run.Scenario.Workers);
            Assert.True(run.Scenario.Fields);
            Assert.Equal(4, run.Timing.Samples);
            Assert.True(run.Timing.P50Ms > 0 && run.Timing.P99Ms >= run.Timing.P50Ms && run.Timing.MaxMs >= run.Timing.P99Ms);
            Assert.True(run.Counts.ChunksVisited > 0);
            Assert.NotEqual(0UL, run.Counts.WorldHash);
            Assert.NotNull(run.SaveLoad);
            Assert.True(run.SaveLoad.Bytes > 0);
            Assert.True(run.SaveLoad.LoadedHashMatches);
            Assert.True(run.SaveLoad.ContinuedHashMatches);
            Assert.All(run.Gates, g => Assert.True(g.Passed, g.ToString()));
            Assert.Contains(run.Gates, g => g.Name == "allocations");
            Assert.NotEqual(string.Empty, report.Machine.Name);
            Assert.NotEqual(string.Empty, report.Commit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_baseline_with_a_different_chunk_count_or_hash_fails_the_run_while_timing_differences_only_report()
    {
        string path = TempFile(".json");
        string baselinePath = TempFile(".json");
        try
        {
            Assert.Equal(0, TickHarness.Run(["--scenario", "colonist-100k", "--colonists", "1000", "--food", "50", "--ticks", "3", "--warmup", "12", "--out", baselinePath], TextWriter.Null, TextWriter.Null));
            var baseline = HarnessReport.ReadFile(baselinePath);
            var tampered = baseline with
            {
                Runs =
                [
                    baseline.Runs[0] with
                    {
                        Timing = baseline.Runs[0].Timing with { P50Ms = baseline.Runs[0].Timing.P50Ms * 100 },
                        Counts = baseline.Runs[0].Counts with { ChunksVisited = baseline.Runs[0].Counts.ChunksVisited + 1 },
                    },
                ],
            };
            tampered.WriteFile(baselinePath);

            var output = new StringWriter();
            var error = new StringWriter();
            int exit = TickHarness.Run(["--scenario", "colonist-100k", "--colonists", "1000", "--food", "50", "--ticks", "3", "--warmup", "12", "--out", path, "--baseline", baselinePath], output, error);

            Assert.Equal(1, exit);
            var report = HarnessReport.ReadFile(path);
            var run = Assert.Single(report.Runs);
            var chunks = Assert.Single(run.Gates, g => g.Name == "chunks-visited");
            Assert.False(chunks.Passed);
            Assert.Equal(baseline.Runs[0].Counts.ChunksVisited + 1, chunks.Expected);
            Assert.Equal(baseline.Runs[0].Counts.ChunksVisited, chunks.Actual);
            Assert.True(Assert.Single(run.Gates, g => g.Name == "world-hash").Passed);
            Assert.Contains("chunks-visited", error.ToString(), StringComparison.Ordinal);
            Assert.NotNull(run.Comparison);
            Assert.True(run.Comparison.P50DeltaPercent < -90, "timing is compared and reported, never gated");
        }
        finally
        {
            File.Delete(path);
            File.Delete(baselinePath);
        }
    }

    [Fact]
    public void Gates_are_pure_over_the_report_allocations_must_be_zero_and_counts_must_match_the_baseline()
    {
        var run = SampleRun(mainBytes: 0, workerBytes: 0, chunks: 10, hash: 7);

        var clean = Gates.Evaluate(run, baseline: null);
        Assert.All(clean, g => Assert.True(g.Passed));
        Assert.Equal(["allocations", "save-load"], clean.Select(g => g.Name));

        var allocating = Gates.Evaluate(SampleRun(mainBytes: 24, workerBytes: 0, chunks: 10, hash: 7), baseline: null);
        var allocation = Assert.Single(allocating, g => g.Name == "allocations");
        Assert.False(allocation.Passed);
        Assert.Equal(24, allocation.Actual);

        var compared = Gates.Evaluate(run, SampleRun(mainBytes: 0, workerBytes: 0, chunks: 10, hash: 8));
        Assert.True(Assert.Single(compared, g => g.Name == "chunks-visited").Passed);
        Assert.False(Assert.Single(compared, g => g.Name == "world-hash").Passed);
    }

    [Fact]
    public void Unknown_scenarios_and_options_exit_2()
    {
        Assert.Equal(2, TickHarness.Run(["--scenario", "nope"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, TickHarness.Run(["--bogus", "1"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, TickHarness.Run(["--ticks"], TextWriter.Null, TextWriter.Null));
    }

    [Fact]
    public void The_scenario_list_names_both_colonist_sizes()
    {
        var output = new StringWriter();

        Assert.Equal(0, TickHarness.Run(["--list"], output, TextWriter.Null));

        var names = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["colonist-100k", "colonist-1m"], names);
    }

    private static RunReport SampleRun(long mainBytes, long workerBytes, long chunks, ulong hash)
        => new(
            new ScenarioReport("colonist-100k", 100, 5, 1, 4, 2, "Off", Fields: true),
            new TimingReport(1, 1, 1, 1, 1, 1, 4),
            new CountReport(mainBytes, workerBytes, chunks, 0, 0, 0, 105, hash),
            new SaveLoadReport(1, 1, 1000, LoadedHashMatches: true, ContinuedHashMatches: true),
            Gates: [],
            Comparison: null);
}
