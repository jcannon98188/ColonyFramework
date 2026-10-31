using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Colony.Generators;

/// <summary>
/// Compile-time checks of the attributed shapes the generator consumes (spec sections 4.3 and 11): COL2001-COL2007.
/// They are errors: a System that cannot be generated, or whose access declaration is contradicted by its body,
/// would otherwise fail at World build or, worse, race at run time.
/// </summary>
public static class GeneratorDiagnostics
{
    private const string Category = "Colony.Generator";
    private const string HelpLink = "https://github.com/jcannon98188/ColonyFramework/blob/main/docs/spec/architecture.md#11-public-api-shape";

    /// <summary>COL2001: a [System] method the generator cannot turn into a System entry.</summary>
    public static readonly DiagnosticDescriptor SystemShape = Rule(
        "COL2001",
        "System method has an unsupported shape",
        "System '{0}' must be a static void method of a partial class or struct");

    /// <summary>COL2002: a parameter of a [System] method the generator cannot bind.</summary>
    public static readonly DiagnosticDescriptor SystemParameter = Rule(
        "COL2002",
        "System parameter is not supported",
        "Parameter '{0}' of System '{1}' is not supported: {2}");

    /// <summary>COL2003: a [Component] type that is not a non-generic partial struct.</summary>
    public static readonly DiagnosticDescriptor ComponentShape = Rule(
        "COL2003",
        "Component must be a partial struct",
        "Component '{0}' must be a non-generic partial struct");

    /// <summary>COL2004: a [Component] field that cannot be part of an unmanaged layout.</summary>
    public static readonly DiagnosticDescriptor ComponentField = Rule(
        "COL2004",
        "Component field is not unmanaged",
        "Field '{1}' of Component '{0}' {2}; Components are plain unmanaged structs (spec section 2)");

    /// <summary>COL2005: a System body touches a Component outside its declared access set.</summary>
    public static readonly DiagnosticDescriptor UndeclaredAccess = Rule(
        "COL2005",
        "System accesses an undeclared Component",
        "System '{0}' accesses Component '{1}' which is not in its declared access set; add a ref/in parameter or list it in [Reads]/[Writes] (spec section 4.3)");

    /// <summary>COL2006: a type listed in an access attribute that is not an unmanaged struct.</summary>
    public static readonly DiagnosticDescriptor AccessAttributeType = Rule(
        "COL2006",
        "Access attribute names a non-Component type",
        "'{0}' in [{1}] on System '{2}' is not an unmanaged struct and cannot be a Component");

    /// <summary>COL2007: an invalid rate on a [System] attribute.</summary>
    public static readonly DiagnosticDescriptor SystemRate = Rule(
        "COL2007",
        "System rate is invalid",
        "System '{0}' has Every = {1} and Offset = {2}; Every must be at least 1 and Offset below Every or -1 (spec section 4.3)");

    /// <summary>COL2005 is reported by <see cref="SystemAccessAnalyzer"/>; the rest by the generator.</summary>
    public static readonly ImmutableArray<DiagnosticDescriptor> All =
        ImmutableArray.Create(SystemShape, SystemParameter, ComponentShape, ComponentField, UndeclaredAccess, AccessAttributeType, SystemRate);

    /// <summary>True when <paramref name="id"/> is one of the generator's diagnostic ids.</summary>
    public static bool IsKnownId(string id) => All.Any(d => d.Id == id);

    internal static DiagnosticDescriptor ById(string id) => All.First(d => d.Id == id);

    private static DiagnosticDescriptor Rule(string id, string title, string messageFormat) =>
        new(id, title, messageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: null, helpLinkUri: HelpLink);
}

/// <summary>An equatable stand-in for <see cref="Location"/>, so models that carry diagnostics stay cacheable.</summary>
internal readonly record struct LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo From(Location location)
    {
        var lineSpan = location.GetLineSpan();
        return new LocationInfo(lineSpan.Path, location.SourceSpan, lineSpan.Span);
    }

    public static LocationInfo From(SyntaxNode node) => From(node.GetLocation());

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}

/// <summary>A diagnostic captured during model extraction, reported when the model is emitted.</summary>
internal sealed record DiagnosticInfo(string Id, LocationInfo Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location location, params string[] arguments) =>
        new(descriptor.Id, LocationInfo.From(location), arguments.ToEquatableArray());

    public Diagnostic ToDiagnostic() => Diagnostic.Create(GeneratorDiagnostics.ById(Id), Location.ToLocation(), Arguments.AsImmutableArray().ToArray());
}
