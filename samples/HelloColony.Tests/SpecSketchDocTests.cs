using System.Text.RegularExpressions;

namespace Colony.Samples.Hello.Tests;

/// <summary>
/// Ticket AC #3, the compile-checked doc test: <c>samples/HelloColony/Sketch.cs</c> is the hello-colony sketch of
/// spec section 11.1 as it compiles today (it is built into the sample host). This test extracts the code block
/// from <c>docs/spec/architecture.md</c> and asserts it equals the compiled sketch up to the documented
/// deviations below. The spec says the sketch "shows intended shape, not final signatures"; the deviations are
/// the exact spec follow-up, and the lists shrink to empty when the spec is updated. Any other drift on either
/// side fails here with the diff.
/// </summary>
public sealed class SpecSketchDocTests
{
    /// <summary>Lines of the spec block that do not compile as written (what the spec says).</summary>
    private static readonly string[] SpecOnly =
    [
        "[Field(\"heat\")] public partial struct Heat { public Fixed32 Value; }",
        "[System(Phase.Simulate, Every = 1)]",
        "var schema = new WorldBuilder()",
        ".Seed(12345)",
        ".AddComponent<Hunger>().AddComponent<Food>()",
        ".AddField<Heat>()",
        ".AddSystems<ColonySystems>()",
        ".Build();                                  // validates, freezes the schema",
        "using var world = schema.CreateWorld();",
        "for (int i = 0; i < 100; i++) world.Create(new Hunger());",
    ];

    /// <summary>What the compiling sketch has instead (what the API is).</summary>
    private static readonly string[] SketchOnly =
    [
        "public struct Heat { public Fixed32 Value; }",
        "[System(NamedPhase.Simulate, Every = 1)]",
        "var builder = new WorldBuilder()",
        ".AddComponent<Hunger>().AddComponent<Food>();",
        "builder.AddFields(new WorldSize(64, 64, 1)).AddField<Heat>(\"heat\");",
        "ColonySystems.AddSystems(builder);",
        "var schema = builder.Build();                                  // validates, freezes the schema",
        "using var world = schema.CreateWorld(new WorldOptions { Seed = 12345 });",
        "var commands = world.CreateCommandBuffer();",
        "for (int i = 0; i < 100; i++) commands.Create(ComponentMask.Of(schema.Component<Hunger>()));",
        "world.Playback(commands);",
        "return world.Hash();",
    ];

    [Fact]
    public void The_spec_sketch_equals_the_compiled_sketch_up_to_the_documented_deviations()
    {
        string root = RepoRoot();
        var spec = SpecBlockLines(File.ReadAllText(Path.Combine(root, "docs", "spec", "architecture.md")));
        var sketch = SketchLines(File.ReadAllText(Path.Combine(root, "samples", "HelloColony", "Sketch.cs")));

        Assert.NotEmpty(spec);
        Assert.NotEmpty(sketch);
        var specCommon = spec.Where(l => !SpecOnly.Contains(l, StringComparer.Ordinal)).ToArray();
        var sketchCommon = sketch.Where(l => !SketchOnly.Contains(l, StringComparer.Ordinal)).ToArray();
        Assert.Equal(specCommon, sketchCommon);
        // Every documented deviation must still be present on its side; otherwise the lists are stale.
        Assert.All(SpecOnly, l => Assert.Contains(l, spec));
        Assert.All(SketchOnly, l => Assert.Contains(l, sketch));
    }

    [Fact]
    public void The_compiled_sketch_runs_and_prints_a_hash()
    {
        // The sketch writes colony.save into the current directory, as the spec does; the test cleans it up.
        try
        {
            ulong hash = Sketch.Run();

            Assert.NotEqual(0UL, hash);
            Assert.True(File.Exists("colony.save"));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(".", "colony.save*"))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>The first <c>```csharp</c> block after the "### 11.1" heading, trimmed, without blank lines.</summary>
    private static string[] SpecBlockLines(string markdown)
    {
        int heading = markdown.IndexOf("### 11.1", StringComparison.Ordinal);
        Assert.True(heading >= 0, "section 11.1 not found");
        var match = Regex.Match(markdown[heading..], "```csharp\\r?\\n(.*?)```", RegexOptions.Singleline);
        Assert.True(match.Success, "no csharp block in section 11.1");
        return Normalise(match.Groups[1].Value);
    }

    /// <summary>The lines between every <c>// &lt;sketch&gt;</c> and <c>// &lt;/sketch&gt;</c> marker of Sketch.cs, in order.</summary>
    private static string[] SketchLines(string source)
    {
        var lines = new List<string>();
        foreach (Match region in Regex.Matches(source, "// <sketch>\\r?\\n(.*?)// </sketch>", RegexOptions.Singleline))
        {
            lines.AddRange(Normalise(region.Groups[1].Value));
        }

        return [.. lines];
    }

    private static string[] Normalise(string block)
        => block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Colony.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("Colony.slnx not found above the test directory.");
    }
}
