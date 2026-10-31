namespace Colony.Generators.Tests;

/// <summary>Generated Components (ticket AC #1 and #3): layout descriptor, stable name and equality members.</summary>
public sealed class ComponentGenerationTests
{
    [Fact]
    public void A_component_struct_gets_a_field_level_layout_and_equality_members()
    {
        GeneratorHarness.Verify("""
            using Colony;
            using Colony.Numerics;

            namespace Game;

            [Component]
            public partial struct Hunger
            {
                public Fixed32 Value;
                public int Ticks;
            }
            """);
    }
}

/// <summary>The snapshot layer itself (ticket AC #3): drifted or missing output fails and leaves a .received.cs file to inspect.</summary>
public sealed class SnapshotHarnessTests
{
    [Fact]
    public void Output_drift_fails_and_writes_a_received_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "colony-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "A.g.cs"), "line 1\nline 2\n");
            var generated = System.Collections.Immutable.ImmutableArray.Create(new GeneratorHarness.GeneratedFile("A.g.cs", "line 1\nline changed\n"));

            var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => GeneratorHarness.CompareSnapshots(generated, directory));

            Assert.Contains("Snapshot drift", failure.Message, StringComparison.Ordinal);
            Assert.Contains("line changed", failure.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory, "A.g.received.cs")));

            GeneratorHarness.CompareSnapshots(System.Collections.Immutable.ImmutableArray.Create(new GeneratorHarness.GeneratedFile("A.g.cs", "line 1\nline 2\n")), directory);
            Assert.False(File.Exists(Path.Combine(directory, "A.g.received.cs")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
