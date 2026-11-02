using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Colony.Generators;

/// <summary>Turns a <c>[Component]</c> struct symbol into a <see cref="ComponentModel"/>, collecting COL2003/COL2004.</summary>
internal static class ComponentParser
{
    public static ComponentModel Parse(INamedTypeSymbol type, AttributeData attribute, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var location = type.Locations.FirstOrDefault() ?? Location.None;

        if (type.TypeKind != TypeKind.Struct || type.IsGenericType || !SymbolHelpers.IsPartialChain(type))
        {
            diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.ComponentShape, location, type.Name));
        }

        string stableName = type.Name;
        bool nonFragmenting = false;
        if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string explicitName && !string.IsNullOrWhiteSpace(explicitName))
        {
            stableName = explicitName;
        }

        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "NonFragmenting" && named.Value.Value is bool flag)
            {
                nonFragmenting = flag;
            }
        }

        var fields = ImmutableArray.CreateBuilder<FieldModel>();
        foreach (var member in type.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } field)
            {
                continue;
            }

            var fieldLocation = field.Locations.FirstOrDefault() ?? location;
            if (field.IsFixedSizeBuffer)
            {
                diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.ComponentField, fieldLocation, type.Name, field.Name, "is a fixed-size buffer; use an [InlineArray] struct instead"));
                continue;
            }

            if (!field.Type.IsUnmanagedType)
            {
                diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.ComponentField, fieldLocation, type.Name, field.Name, "is not an unmanaged type"));
                continue;
            }

            fields.Add(new FieldModel(field.Name, SymbolHelpers.CodeName(field.Type), SymbolHelpers.StableName(field.Type)));
        }

        return new ComponentModel(
            SymbolHelpers.Placement(type, includeSelf: false),
            type.Name,
            SymbolHelpers.CodeName(type),
            stableName,
            nonFragmenting,
            new EquatableArray<FieldModel>(fields.ToImmutable()),
            EmitEquality: !DeclaresEquality(type),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));
    }

    /// <summary>True when the author already wrote any equality member; the generator then adds none and leaves CA1815 to them.</summary>
    private static bool DeclaresEquality(INamedTypeSymbol type) =>
        type.GetMembers().Any(m =>
            m is IMethodSymbol method
            && (method.Name is "Equals" or "GetHashCode" or "op_Equality" or "op_Inequality")
            && !method.IsImplicitlyDeclared);
}
