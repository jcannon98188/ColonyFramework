using Colony.Plugins;
using Colony.Plugins.Native;
using Colony.Testing;

namespace Colony.Native.Tests;

/// <summary>AC #4: a Native Plugin System participates in the determinism harness and hashes identically to its C# equivalent.</summary>
[Collection(NativeTestGroup.Name)]
public sealed class DeterminismTests
{
    private static readonly DeterminismOptions Options = new() { Ticks = 30, CheckpointEvery = 1, WorkerCounts = [1, 2, 4] };

    [Fact]
    public void The_Native_Plugin_passes_the_determinism_harness_across_worker_counts()
    {
        string library = ConformanceTests.RequireLibrary();
        var result = DeterminismHarness.Run(new ConformanceScenario("conformance.native", () => NativePlugin.Load(library)), Options);
        Assert.True(result.Passed, result.Describe());
        Assert.NotEqual(result.Hashes.Span[0], result.Hashes.Span[^1]);
    }

    [Fact]
    public void The_Native_Plugin_hashes_identically_to_its_C_sharp_twin_at_every_checkpoint()
    {
        string library = ConformanceTests.RequireLibrary();
        var native = DeterminismHarness.Run(new ConformanceScenario("conformance.native", () => NativePlugin.Load(library)), Options);
        var twin = DeterminismHarness.Run(new ConformanceScenario("conformance.twin", () => new ConformanceTwinPlugin()), Options);
        native.ThrowIfFailed();
        twin.ThrowIfFailed();
        Assert.Null(native.FirstDivergenceFrom(twin));
        Assert.Equal(twin.Hashes.ToArray(), native.Hashes.ToArray());
    }

    [Fact]
    public void A_Native_Plugin_loaded_from_a_library_carries_its_manifest()
    {
        string library = ConformanceTests.RequireLibrary();
        var plugin = NativePlugin.Load(library);
        Assert.Equal("conformance.native", plugin.Manifest.Id);
        Assert.Equal("1.0.0", plugin.Manifest.Version);
        Assert.True(plugin.Manifest.Deterministic);
        Assert.Equal(ColonyAbi.Version, plugin.Manifest.AbiVersion);
        Assert.Empty(plugin.Manifest.Dependencies);
    }
}
