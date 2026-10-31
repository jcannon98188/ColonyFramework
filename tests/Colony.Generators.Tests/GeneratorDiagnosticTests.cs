using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Colony.Generators.Tests;

/// <summary>COL2001-COL2007 (ticket AC #5): shapes the generator rejects and access declarations contradicted by the body.</summary>
public sealed class GeneratorDiagnosticTests
{
    private const string Components = """
        namespace Game
        {
            [Colony.Component] public partial struct Hunger { public int Value; }
            [Colony.Component] public partial struct Position { public int X; }
        }
        """;

    [Fact]
    public void A_system_in_a_non_partial_type_is_COL2001()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            using Colony;
            public static class Systems
            {
                [System]
                public static void {|COL2001:Starve|}(ref Game.Hunger hunger) { }
            }
            """ + Components);
    }

    [Fact]
    public void A_non_static_or_non_void_system_is_COL2001()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            using Colony;
            public partial class Systems
            {
                [System]
                public void {|COL2001:Starve|}(ref Game.Hunger hunger) { }

                [System]
                public static int {|COL2001:Count|}(ref Game.Hunger hunger) => 0;
            }
            """ + Components);
    }

    [Fact]
    public void A_component_parameter_passed_by_value_is_COL2002()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            using Colony;
            public static partial class Systems
            {
                [System]
                public static void Starve(Game.Hunger {|COL2002:hunger|}) { hunger.Value++; }
            }
            """ + Components);
    }

    [Fact]
    public void A_parameter_of_an_unknown_kind_or_a_mixed_shape_is_COL2002()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            using Colony;
            public static partial class Systems
            {
                [System]
                public static void Log(string {|COL2002:text|}) { }

                [System]
                public static void Mixed(in ChunkView chunk, ref Game.Hunger {|COL2002:hunger|}) { }

                [System]
                public static void Ids(in ChunkView chunk, EntityId {|COL2002:entity|}) { }
            }
            """ + Components);
    }

    [Fact]
    public void A_component_that_is_not_a_partial_struct_is_COL2003()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            [Colony.Component] public struct {|COL2003:Plain|} { public int X; }
            [Colony.Component] public partial struct {|COL2003:Generic|}<T> where T : unmanaged { public T X; }
            """);
    }

    [Fact]
    public void A_component_with_a_managed_or_fixed_buffer_field_is_COL2004()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            [Colony.Component] public partial struct Named { public string {|COL2004:Name|}; public int Id; }
            """);
    }

    [Fact]
    public void An_access_attribute_naming_a_non_struct_is_COL2006()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            using Colony;
            public static partial class Systems
            {
                [System]
                [{|COL2006:Reads(typeof(string))|}]
                public static void Sweep(in ChunkView chunk) { }
            }
            """ + Components);
    }

    [Fact]
    public void An_invalid_rate_is_COL2007()
    {
        GeneratorHarness.Verify(snapshot: false, markup: """
            using Colony;
            public static partial class Systems
            {
                [{|COL2007:System(Every = 0)|}]
                public static void Never(ref Game.Hunger hunger) { }

                [{|COL2007:System(Every = 4, Offset = 4)|}]
                public static void Late(ref Game.Hunger hunger) { }
            }
            """ + Components);
    }

    [Fact]
    public void A_system_body_touching_an_undeclared_component_is_COL2005()
    {
        VerifyAccess("""
            using Colony;
            public static partial class Systems
            {
                [System]
                [Writes(typeof(Game.Hunger))]
                public static void Sweep(in SystemContext context, in ChunkView chunk)
                {
                    var hunger = chunk.Column<Game.Hunger>(context.World.Schema.Component<Game.Hunger>());
                    var positions = {|COL2005:chunk.PaddedColumn<Game.Position>(context.World.Schema.Component<Game.Position>())|};
                    ref var one = ref {|COL2005:context.World.Get<Game.Position>(chunk.Entities[0], default)|};
                }

                [System]
                public static void Starve(in SystemContext context, ref Game.Hunger hunger)
                {
                    var world = context.World;
                    System.Action peek = () => _ = {|COL2005:world.Get<Game.Position>(default, default)|};
                }
            }
            """ + Components);
    }

    [Fact]
    public void Declared_components_and_non_system_code_are_not_COL2005()
    {
        VerifyAccess("""
            using Colony;
            public static partial class Systems
            {
                [System]
                [Reads(typeof(Game.Position))]
                public static void Sweep(in SystemContext context, in ChunkView chunk, in Game.Hunger hunger)
                {
                    _ = chunk.Column<Game.Position>(default);
                    _ = chunk.Column<Game.Hunger>(default);
                }

                public static void Debug(in SystemContext context, in ChunkView chunk) => _ = chunk.Column<Game.Position>(default);
            }
            """ + Components);
    }

    /// <summary>Runs the generator, then <see cref="SystemAccessAnalyzer"/> over the output compilation, and checks COL2005 markup.</summary>
    private static void VerifyAccess(string markup)
    {
        var (source, expected) = AnalyzerHarness.ParseMarkup(markup);
        var run = GeneratorHarness.Run(source);
        var errors = run.OutputCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "Snippet does not compile:\n" + string.Join("\n", errors));

        var actual = run.OutputCompilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SystemAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult()
            .Where(d => d.Location.SourceTree?.FilePath == "Snippet.cs")
            .Select(d => new AnalyzerHarness.ExpectedDiagnostic(d.Id, d.Location.SourceSpan))
            .OrderBy(d => d.Span.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            expected.SequenceEqual(actual),
            $"Expected diagnostics:\n{AnalyzerHarness.Describe(expected, source)}\nActual diagnostics:\n{AnalyzerHarness.Describe(actual, source)}");
    }
}
