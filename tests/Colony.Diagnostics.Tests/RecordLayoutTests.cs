namespace Colony.Diagnostics.Tests;

/// <summary>TASK-18 AC #4: sinks consume the versioned record layout and fail clearly on a mismatch.</summary>
public sealed class RecordLayoutTests
{
    [Fact]
    public void The_sinks_are_compiled_for_the_layout_version_core_writes()
    {
        Assert.Equal(RecordLayout.CompiledVersion, WorldDiagnostics.LayoutVersion);
        RecordLayout.Verify(WorldDiagnostics.LayoutVersion, "test");
    }

    [Fact]
    public void A_different_layout_version_is_rejected_with_both_versions_named()
    {
        var error = Assert.Throws<RecordLayoutMismatchException>(() => RecordLayout.Verify(RecordLayout.CompiledVersion + 1, "Perfetto trace export"));

        Assert.Equal(RecordLayout.CompiledVersion, error.ExpectedVersion);
        Assert.Equal(RecordLayout.CompiledVersion + 1, error.ActualVersion);
        Assert.Contains("Perfetto trace export", error.Message, StringComparison.Ordinal);
        Assert.Contains($"version {RecordLayout.CompiledVersion + 1}", error.Message, StringComparison.Ordinal);
        Assert.Contains($"version {RecordLayout.CompiledVersion}", error.Message, StringComparison.Ordinal);
    }
}
