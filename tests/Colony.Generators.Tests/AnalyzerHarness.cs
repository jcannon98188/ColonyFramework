using System.Collections.Immutable;
using System.Text;
using Colony.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Colony.Generators.Tests;

/// <summary>
/// Compiles a snippet, runs <see cref="DeterminismAnalyzer"/> over it and compares the reported diagnostics
/// with <c>{|COL1xxx:expression|}</c> markup in the snippet. A snippet without markup asserts "no diagnostic".
/// </summary>
internal static class AnalyzerHarness
{
    /// <summary>
    /// Stand-in for the marker attributes so snippets need no reference to Colony.Core; the analyzers match the
    /// attributes by fully qualified name only. <see cref="VerifyAgainstCore"/> uses the real assembly instead.
    /// </summary>
    private const string MarkerAttributes = """
        namespace Colony
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Method)]
            public sealed class SystemAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class AllowNonDeterministicAttribute : System.Attribute
            {
                public AllowNonDeterministicAttribute(string justification) { }
            }
        }
        """;

    private static readonly ImmutableArray<MetadataReference> RuntimeReferences = LoadRuntimeReferences();

    /// <summary>Runs the analyzers over <paramref name="markup"/> with the stand-in marker attributes.</summary>
    public static void Verify(string markup) => Verify(markup, MarkerAttributes, []);

    /// <summary>Runs the analyzers over <paramref name="markup"/> compiled against the real Colony.Core assembly.</summary>
    public static void VerifyAgainstCore(string markup) =>
        Verify(markup, extraSource: null, [MetadataReference.CreateFromFile(typeof(SystemAttribute).Assembly.Location)]);

    private static void Verify(string markup, string? extraSource, ImmutableArray<MetadataReference> extraReferences)
    {
        var (source, expected) = ParseMarkup(markup);
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, path: "Snippet.cs") };
        if (extraSource is not null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(extraSource, path: "Markers.cs"));
        }

        var compilation = CSharpCompilation.Create(
            "Snippet",
            trees,
            RuntimeReferences.AddRange(extraReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0, "Snippet does not compile:\n" + string.Join("\n", compileErrors));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new DeterminismAnalyzer());
        var actual = compilation
            .WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult()
            .Select(d => new ExpectedDiagnostic(d.Id, d.Location.SourceSpan))
            .OrderBy(d => d.Span.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            expected.SequenceEqual(actual),
            $"Expected diagnostics:\n{Describe(expected, source)}\nActual diagnostics:\n{Describe(actual, source)}");

        foreach (var diagnostic in actual)
        {
            Assert.True(DeterminismAnalyzer.IsKnownId(diagnostic.Id), $"Unknown diagnostic id {diagnostic.Id}");
        }
    }

    private static string Describe(IEnumerable<ExpectedDiagnostic> diagnostics, string source)
    {
        var builder = new StringBuilder();
        foreach (var diagnostic in diagnostics)
        {
            builder.Append("  ").Append(diagnostic.Id).Append(" @ ").Append(diagnostic.Span)
                .Append(": ").AppendLine(source.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
        }

        return builder.Length == 0 ? "  (none)" : builder.ToString();
    }

    /// <summary>Strips <c>{|ID:...|}</c> markers and returns the clean source with the expected (id, span) pairs.</summary>
    private static (string Source, List<ExpectedDiagnostic> Expected) ParseMarkup(string markup)
    {
        var source = new StringBuilder(markup.Length);
        var expected = new List<ExpectedDiagnostic>();
        var open = new Stack<(string Id, int Start)>();

        int i = 0;
        while (i < markup.Length)
        {
            if (markup[i] == '{' && i + 1 < markup.Length && markup[i + 1] == '|')
            {
                int colon = markup.IndexOf(':', i + 2);
                string id = markup.Substring(i + 2, colon - (i + 2));
                open.Push((id, source.Length));
                i = colon + 1;
            }
            else if (markup[i] == '|' && i + 1 < markup.Length && markup[i + 1] == '}')
            {
                var (id, start) = open.Pop();
                expected.Add(new ExpectedDiagnostic(id, TextSpan.FromBounds(start, source.Length)));
                i += 2;
            }
            else
            {
                source.Append(markup[i]);
                i++;
            }
        }

        Assert.True(open.Count == 0, "Unbalanced {| |} markup in snippet.");
        expected.Sort((a, b) =>
        {
            int byStart = a.Span.Start.CompareTo(b.Span.Start);
            return byStart != 0 ? byStart : string.CompareOrdinal(a.Id, b.Id);
        });
        return (source.ToString(), expected);
    }

    private static ImmutableArray<MetadataReference> LoadRuntimeReferences()
    {
        // The test host's own trusted platform assemblies: the exact BCL the snippets should be checked against.
        string paths = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return paths
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal)
                        || Path.GetFileName(p).Equals("netstandard.dll", StringComparison.Ordinal)
                        || Path.GetFileName(p).Equals("mscorlib.dll", StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }

    private readonly record struct ExpectedDiagnostic(string Id, TextSpan Span);
}
