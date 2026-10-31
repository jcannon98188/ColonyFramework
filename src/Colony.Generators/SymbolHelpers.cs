using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Colony.Generators;

/// <summary>Shared symbol-to-model helpers for the Component and System parsers.</summary>
internal static class SymbolHelpers
{
    public const string ComponentAttributeName = "Colony.ComponentAttribute";
    public const string SystemAttributeName = "Colony.SystemAttribute";
    public const string ReadsAttributeName = "Colony.ReadsAttribute";
    public const string WritesAttributeName = "Colony.WritesAttribute";
    public const string WithAttributeName = "Colony.WithAttribute";
    public const string WithoutAttributeName = "Colony.WithoutAttribute";
    public const string ChangeFilterAttributeName = "Colony.ChangeFilterAttribute";

    /// <summary>Stable type names for the layout hash: no <c>global::</c>, keywords for the special types.</summary>
    public static readonly SymbolDisplayFormat StableNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Names written into generated code: fully qualified with <c>global::</c>.</summary>
    public static string CodeName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public static string StableName(ITypeSymbol type) => type.ToDisplayString(StableNameFormat);

    public static bool HasAttribute(ISymbol symbol, string fullName) => symbol.GetAttributes().Any(a => IsAttribute(a, fullName));

    public static bool IsAttribute(AttributeData attribute, string fullName) =>
        attribute.AttributeClass is not null && string.Equals(attribute.AttributeClass.ToDisplayString(), fullName, StringComparison.Ordinal);

    public static AttributeData? FindAttribute(ISymbol symbol, string fullName) => symbol.GetAttributes().FirstOrDefault(a => IsAttribute(a, fullName));

    /// <summary>The types listed in a <c>params Type[]</c> attribute such as [Reads(typeof(A), typeof(B))].</summary>
    public static ImmutableArray<ITypeSymbol> TypeArguments(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
        {
            return ImmutableArray<ITypeSymbol>.Empty;
        }

        var argument = attribute.ConstructorArguments[0];
        if (argument.Kind == TypedConstantKind.Array)
        {
            return argument.Values.Select(v => v.Value).OfType<ITypeSymbol>().ToImmutableArray();
        }

        return argument.Value is ITypeSymbol single ? ImmutableArray.Create(single) : ImmutableArray<ITypeSymbol>.Empty;
    }

    public static PlacementModel Placement(INamedTypeSymbol type, bool includeSelf)
    {
        var containers = ImmutableArray.CreateBuilder<TypeDeclarationModel>();
        for (var current = includeSelf ? type : type.ContainingType; current is not null; current = current.ContainingType)
        {
            containers.Insert(0, Declaration(current));
        }

        string? ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        string hint = type.ToDisplayString(StableNameFormat).Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_');
        return new PlacementModel(ns, new EquatableArray<TypeDeclarationModel>(containers.ToImmutable()), hint);
    }

    public static TypeDeclarationModel Declaration(INamedTypeSymbol type)
    {
        string keyword = type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            _ => type.IsRecord ? "record" : "class",
        };
        string name = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return new TypeDeclarationModel(keyword, name);
    }

    /// <summary>True when every declaration of the type carries the <c>partial</c> modifier (the compiler requires all or none).</summary>
    public static bool IsPartial(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Length > 0
        && type.DeclaringSyntaxReferences.All(r => r.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(m => m.ValueText == "partial"));

    /// <summary>True when the type and all its containing types are partial.</summary>
    public static bool IsPartialChain(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (!IsPartial(current))
            {
                return false;
            }
        }

        return true;
    }

    public static string CamelCase(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    public static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
}
