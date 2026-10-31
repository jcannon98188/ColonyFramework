using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Colony.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Colony.Generators.Tests;

/// <summary>
/// Runs <see cref="ColonyGenerator"/> over a snippet compiled against the real Colony.Core, checks the
/// generator's diagnostics against <c>{|COL2xxx:...|}</c> markup, compiles the output and compares every
/// generated file with the committed snapshot under <c>Snapshots/&lt;test&gt;/</c> (spec section 10.3).
/// A drifted or missing snapshot writes a <c>.received.cs</c> file beside it and fails; set
/// <c>COLONY_ACCEPT_SNAPSHOTS=1</c> to accept the received output as the new snapshot.
/// </summary>
internal static class GeneratorHarness
{
    private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

    /// <summary>Runs the generator; asserts the markup diagnostics, a clean output compilation and matching snapshots.</summary>
    public static void Verify(string markup, bool snapshot = true, [CallerMemberName] string snapshotName = "", [CallerFilePath] string callerPath = "")
    {
        var (source, expected) = AnalyzerHarness.ParseMarkup(markup);
        var result = Run(source);

        var actual = result.GeneratorDiagnostics
            .Select(d => new AnalyzerHarness.ExpectedDiagnostic(d.Id, d.Location.SourceSpan))
            .OrderBy(d => d.Span.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            expected.SequenceEqual(actual),
            $"Expected generator diagnostics:\n{AnalyzerHarness.Describe(expected, source)}\nActual:\n{AnalyzerHarness.Describe(actual, source)}");

        var errors = result.OutputCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "Generated output does not compile:\n" + string.Join("\n", errors) + "\n\nGenerated:\n" + string.Join("\n\n", result.Generated.Select(g => $"// {g.HintName}\n{g.Text}")));

        Assert.True(result.Generated.Length > 0 || expected.Count > 0, "The generator produced no output and reported no diagnostics.");
        if (!snapshot)
        {
            return;
        }

        CompareSnapshots(result.Generated, Path.Combine(Path.GetDirectoryName(callerPath)!, "Snapshots", snapshotName));
    }

    /// <summary>Runs the generator and returns everything a test may inspect.</summary>
    public static GeneratorRun Run(string source, params string[] extraSources)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Snippet.cs") };
        for (int i = 0; i < extraSources.Length; i++)
        {
            trees.Add(CSharpSyntaxTree.ParseText(extraSources[i], new CSharpParseOptions(LanguageVersion.Latest), path: $"Extra{i}.cs"));
        }

        var compilation = CSharpCompilation.Create(
            "Snippet",
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: false));

        var inputErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && !IsExpectedBeforeGeneration(d)).ToList();
        Assert.True(inputErrors.Count == 0, "Snippet does not compile:\n" + string.Join("\n", inputErrors));

        GeneratorDriver driver = CSharpGeneratorDriver.Create([new ColonyGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.Latest));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        var generated = driver.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => new GeneratedFile(s.HintName, s.SourceText.ToString()))
            .OrderBy(s => s.HintName, StringComparer.Ordinal)
            .ToImmutableArray();
        return new GeneratorRun(driver, output, diagnostics, generated);
    }

    /// <summary>Snippets may use members the generator will emit (the typed query type, the interface); those errors vanish after generation.</summary>
    private static bool IsExpectedBeforeGeneration(Diagnostic d) => d.Id is "CS0246" or "CS0103" or "CS0117" or "CS1061";

    internal static void CompareSnapshots(ImmutableArray<GeneratedFile> generated, string directory)
    {
        bool accept = string.Equals(Environment.GetEnvironmentVariable("COLONY_ACCEPT_SNAPSHOTS"), "1", StringComparison.Ordinal);
        Directory.CreateDirectory(directory);
        var failures = new StringBuilder();
        var expectedFiles = new HashSet<string>(Directory.EnumerateFiles(directory, "*.cs").Where(f => !f.EndsWith(".received.cs", StringComparison.Ordinal)), StringComparer.OrdinalIgnoreCase);

        foreach (var file in generated)
        {
            string path = Path.Combine(directory, file.HintName);
            string receivedPath = Path.ChangeExtension(path, ".received.cs");
            expectedFiles.Remove(path);
            string actual = Normalize(file.Text);
            string? expected = File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
            if (expected == actual)
            {
                if (File.Exists(receivedPath))
                {
                    File.Delete(receivedPath);
                }

                continue;
            }

            if (accept)
            {
                File.WriteAllText(path, actual);
                if (File.Exists(receivedPath))
                {
                    File.Delete(receivedPath);
                }

                continue;
            }

            File.WriteAllText(receivedPath, actual);
            failures.Append(expected is null ? "Missing snapshot " : "Snapshot drift in ").AppendLine(path)
                .Append("  received: ").AppendLine(receivedPath);
            if (expected is not null)
            {
                failures.AppendLine(FirstDifference(expected, actual));
            }
        }

        foreach (var stale in expectedFiles)
        {
            if (accept)
            {
                File.Delete(stale);
            }
            else
            {
                failures.Append("Snapshot without generated output: ").AppendLine(stale);
            }
        }

        Assert.True(failures.Length == 0, failures + "\nSet COLONY_ACCEPT_SNAPSHOTS=1 to accept the received files.");
    }

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            string left = i < e.Length ? e[i] : "<eof>";
            string right = i < a.Length ? a[i] : "<eof>";
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                return $"  line {i + 1}:\n    expected: {left}\n    actual:   {right}";
            }
        }

        return string.Empty;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        string paths = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return paths
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal)
                        || Path.GetFileName(p).Equals("netstandard.dll", StringComparison.Ordinal)
                        || Path.GetFileName(p).Equals("mscorlib.dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(SystemAttribute).Assembly.Location))
            .ToImmutableArray();
    }

    public sealed record GeneratedFile(string HintName, string Text);

    public sealed record GeneratorRun(GeneratorDriver Driver, Compilation OutputCompilation, ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<GeneratedFile> Generated);
}
