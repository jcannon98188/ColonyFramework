using System.Globalization;
using Colony.Testing;
using Colony.Testing.Tests.Scenarios;

namespace Colony.Testing.Tests;

/// <summary>AC #2: golden hashes are committed per scenario as files and compared on every run; accepting works like the snapshot convention.</summary>
public sealed class GoldenHashStoreTests : IDisposable
{
    private static readonly DeterminismOptions Options = new() { Ticks = 6, CheckpointEvery = 2, WorkerCounts = [1] };
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "colony-golden-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_golden_file_fails_unless_accepting_and_accepting_writes_it()
    {
        var store = new GoldenHashStore(_directory);
        var result = DeterminismHarness.Run(new CounterScenario(), Options);

        var missing = store.Compare(result, accept: false);
        Assert.Equal(GoldenStatus.Missing, missing.Status);
        Assert.False(missing.Passed);
        Assert.Contains("COLONY_ACCEPT_GOLDEN", missing.Message, StringComparison.Ordinal);

        var accepted = store.Compare(result, accept: true);
        Assert.Equal(GoldenStatus.Accepted, accepted.Status);
        Assert.True(accepted.Passed);
        string path = Path.Combine(_directory, "counter.golden");
        Assert.True(File.Exists(path));
        var lines = File.ReadAllLines(path);
        Assert.Contains("scenario counter", lines);
        Assert.Contains("seed 42", lines);
        Assert.Contains($"2 {result.Hashes.Span[0].ToString("x16", CultureInfo.InvariantCulture)}", lines);
        Assert.Contains($"6 {result.Hashes.Span[2].ToString("x16", CultureInfo.InvariantCulture)}", lines);

        var match = store.Compare(result, accept: false);
        Assert.Equal(GoldenStatus.Match, match.Status);
        Assert.True(match.Passed);
    }

    [Fact]
    public void A_changed_hash_is_a_mismatch_naming_the_first_diverging_tick_and_writes_a_received_file()
    {
        var store = new GoldenHashStore(_directory);
        store.Compare(DeterminismHarness.Run(new CounterScenario(), Options), accept: true);

        var other = DeterminismHarness.Run(new CounterScenario { DivergeFromTick = 3 }, Options);
        var comparison = store.Compare(other, accept: false);

        Assert.Equal(GoldenStatus.Mismatch, comparison.Status);
        Assert.False(comparison.Passed);
        Assert.Equal(4, comparison.FirstDivergingTick); // checkpoints are 2, 4, 6; Tick 3 diverged
        Assert.Contains("Tick 4", comparison.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_directory, "counter.received.golden")));
        Assert.Throws<HarnessException>(comparison.ThrowIfFailed);
    }

    [Fact]
    public void Different_checkpoints_are_a_mismatch_not_a_silent_pass()
    {
        var store = new GoldenHashStore(_directory);
        store.Compare(DeterminismHarness.Run(new CounterScenario(), Options), accept: true);

        var fewer = DeterminismHarness.Run(new CounterScenario(), new DeterminismOptions { Ticks = 4, CheckpointEvery = 2, WorkerCounts = [1] });

        Assert.Equal(GoldenStatus.Mismatch, store.Compare(fewer, accept: false).Status);
    }

    [Fact]
    public void A_failed_determinism_result_is_never_compared_or_accepted()
    {
        var store = new GoldenHashStore(_directory);
        var failed = DeterminismHarness.Run(new CounterScenario { ThrowAtTick = 2 }, Options);

        var comparison = store.Compare(failed, accept: true);

        Assert.Equal(GoldenStatus.NotRun, comparison.Status);
        Assert.False(comparison.Passed);
        Assert.False(File.Exists(Path.Combine(_directory, "counter.golden")));
    }
}
