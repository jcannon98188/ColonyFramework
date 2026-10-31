using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Colony.Generators;

/// <summary>
/// Turns a <c>[System]</c> method symbol into a <see cref="SystemMethodModel"/>: the Phase, rate and ordering from
/// the attribute, the access set from <c>ref</c>/<c>in</c> parameters and [Reads]/[Writes], and the entry shape
/// (per Entity, per chunk, typed query or serial). Shapes that cannot be generated become COL2001/2002/2006/2007.
/// </summary>
internal static class SystemParser
{
    private static readonly string[] NamedPhases = ["Streaming", "Input", "Begin", "Simulate", "Late", "End"];

    public static SystemMethodModel Parse(IMethodSymbol method, AttributeData attribute, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var location = method.Locations.FirstOrDefault() ?? Location.None;
        var owner = method.ContainingType;

        if (!method.IsStatic || !method.ReturnsVoid || method.IsGenericMethod || owner.TypeKind is not (TypeKind.Class or TypeKind.Struct) || !SymbolHelpers.IsPartialChain(owner))
        {
            diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.SystemShape, location, method.Name));
        }

        string systemName = method.Name;
        string phase = "Simulate";
        int every = 1;
        int offset = -1;
        bool skipAsleep = false;
        var after = ImmutableArray<string>.Empty;
        var before = ImmutableArray<string>.Empty;

        if (attribute.ConstructorArguments.Length == 1)
        {
            var argument = attribute.ConstructorArguments[0];
            if (argument.Value is int enumValue && enumValue >= 0 && enumValue < NamedPhases.Length)
            {
                phase = NamedPhases[enumValue];
            }
            else if (argument.Value is string phaseName && !string.IsNullOrWhiteSpace(phaseName))
            {
                phase = phaseName;
            }
        }

        foreach (var named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "Name" when named.Value.Value is string name && !string.IsNullOrWhiteSpace(name):
                    systemName = name;
                    break;
                case "Every" when named.Value.Value is int e:
                    every = e;
                    break;
                case "Offset" when named.Value.Value is int o:
                    offset = o;
                    break;
                case "SkipAsleepChunks" when named.Value.Value is bool skip:
                    skipAsleep = skip;
                    break;
                case "After":
                    after = Strings(named.Value);
                    break;
                case "Before":
                    before = Strings(named.Value);
                    break;
            }
        }

        if (every < 1 || offset < -1 || offset >= every)
        {
            var attributeLocation = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? location;
            diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.SystemRate, attributeLocation, systemName, every.ToString(System.Globalization.CultureInfo.InvariantCulture), offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var parameters = ParseParameters(method, diagnostics);
        var kind = Classify(method, parameters, diagnostics);

        var columns = ImmutableArray.CreateBuilder<ColumnModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (kind == SystemKind.PerEntity)
        {
            foreach (var parameter in parameters.Where(p => p.Kind == ParameterKind.Component))
            {
                if (seen.Add(parameter.TypeCode))
                {
                    columns.Add(Column(parameter.TypeCode, parameter.TypeSimpleName, parameter.IsWrite));
                }
            }
        }

        foreach (var type in AccessTypes(method, SymbolHelpers.WritesAttributeName, "Writes", systemName, diagnostics))
        {
            if (seen.Add(SymbolHelpers.CodeName(type)))
            {
                columns.Add(Column(SymbolHelpers.CodeName(type), type.Name, isWrite: true));
            }
        }

        foreach (var type in AccessTypes(method, SymbolHelpers.ReadsAttributeName, "Reads", systemName, diagnostics))
        {
            if (seen.Add(SymbolHelpers.CodeName(type)))
            {
                columns.Add(Column(SymbolHelpers.CodeName(type), type.Name, isWrite: false));
            }
        }

        var system = new SystemModel(
            method.Name,
            systemName,
            phase,
            every,
            offset,
            new EquatableArray<string>(after),
            new EquatableArray<string>(before),
            skipAsleep,
            kind,
            new EquatableArray<ParameterModel>(parameters),
            new EquatableArray<ColumnModel>(Dedupe(columns.ToImmutable())),
            Filter(method, SymbolHelpers.WithAttributeName, "With", systemName, diagnostics),
            Filter(method, SymbolHelpers.WithoutAttributeName, "Without", systemName, diagnostics),
            Filter(method, SymbolHelpers.ChangeFilterAttributeName, "ChangeFilter", systemName, diagnostics),
            new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));

        return new SystemMethodModel(SymbolHelpers.Placement(owner, includeSelf: false), SymbolHelpers.Declaration(owner), SymbolHelpers.CodeName(owner), system);
    }

    private static ColumnModel Column(string typeCode, string simpleName, bool isWrite) =>
        new(typeCode, simpleName, SymbolHelpers.CamelCase(simpleName) + "Type", isWrite);

    /// <summary>Two Components with the same simple name in different namespaces get numbered property names.</summary>
    private static ImmutableArray<ColumnModel> Dedupe(ImmutableArray<ColumnModel> columns)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            counts[column.PropertyName] = counts.TryGetValue(column.PropertyName, out int n) ? n + 1 : 1;
        }

        if (counts.Values.All(n => n == 1))
        {
            return columns;
        }

        var result = ImmutableArray.CreateBuilder<ColumnModel>(columns.Length);
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (counts[column.PropertyName] == 1)
            {
                result.Add(column);
                continue;
            }

            int index = used.TryGetValue(column.PropertyName, out int u) ? u + 1 : 1;
            used[column.PropertyName] = index;
            string suffix = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result.Add(column with { PropertyName = column.PropertyName + suffix, LocalName = column.LocalName + suffix });
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<string> Strings(TypedConstant constant) =>
        constant.Kind == TypedConstantKind.Array
            ? constant.Values.Select(v => v.Value).OfType<string>().ToImmutableArray()
            : ImmutableArray<string>.Empty;

    private static ImmutableArray<ITypeSymbol> AccessTypes(IMethodSymbol method, string attributeName, string shortName, string systemName, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var attribute = SymbolHelpers.FindAttribute(method, attributeName);
        if (attribute is null)
        {
            return ImmutableArray<ITypeSymbol>.Empty;
        }

        var types = SymbolHelpers.TypeArguments(attribute);
        var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? method.Locations.FirstOrDefault() ?? Location.None;
        var valid = ImmutableArray.CreateBuilder<ITypeSymbol>(types.Length);
        foreach (var type in types)
        {
            if (type.TypeKind == TypeKind.Struct && type.IsUnmanagedType)
            {
                valid.Add(type);
            }
            else
            {
                diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.AccessAttributeType, location, type.Name, shortName, systemName));
            }
        }

        return valid.ToImmutable();
    }

    private static EquatableArray<ColumnModel> Filter(IMethodSymbol method, string attributeName, string shortName, string systemName, ImmutableArray<DiagnosticInfo>.Builder diagnostics) =>
        new(AccessTypes(method, attributeName, shortName, systemName, diagnostics)
            .Select(t => Column(SymbolHelpers.CodeName(t), t.Name, isWrite: false))
            .ToImmutableArray());

    private static ImmutableArray<ParameterModel> ParseParameters(IMethodSymbol method, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var result = ImmutableArray.CreateBuilder<ParameterModel>(method.Parameters.Length);
        foreach (var parameter in method.Parameters)
        {
            var type = parameter.Type;
            var location = parameter.Locations.FirstOrDefault() ?? Location.None;
            string typeName = type.ToDisplayString();
            ParameterKind kind;
            bool isWrite = parameter.RefKind == RefKind.Ref;

            if (typeName == "Colony.SystemContext")
            {
                kind = ParameterKind.Context;
                if (parameter.RefKind is RefKind.Ref or RefKind.Out)
                {
                    diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.SystemParameter, location, parameter.Name, method.Name, "the SystemContext is read-only; pass it by 'in'"));
                }
            }
            else if (typeName == "Colony.ChunkView")
            {
                kind = ParameterKind.ChunkView;
            }
            else if (typeName == "Colony.EntityId")
            {
                kind = ParameterKind.EntityId;
            }
            else if (type.Name == method.Name + "Query")
            {
                kind = ParameterKind.Query;
            }
            else if (type.TypeKind == TypeKind.Struct && type.IsUnmanagedType)
            {
                kind = ParameterKind.Component;
                if (parameter.RefKind is RefKind.None or RefKind.Out)
                {
                    diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.SystemParameter, location, parameter.Name, method.Name, "a Component parameter must be passed by 'ref' (written) or 'in' (read); a by-value copy cannot be written back and declares no access"));
                }
            }
            else
            {
                kind = ParameterKind.Component;
                diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.SystemParameter, location, parameter.Name, method.Name, $"use a ref/in unmanaged Component struct, EntityId, in SystemContext, in ChunkView or in {method.Name}Query"));
            }

            result.Add(new ParameterModel(kind, parameter.Name, SymbolHelpers.CodeName(type), type.Name, isWrite, parameter.RefKind != RefKind.None));
        }

        return result.ToImmutable();
    }

    private static SystemKind Classify(IMethodSymbol method, ImmutableArray<ParameterModel> parameters, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        SystemKind kind = parameters.Any(p => p.Kind == ParameterKind.Query) ? SystemKind.TypedQuery
            : parameters.Any(p => p.Kind == ParameterKind.ChunkView) ? SystemKind.Chunk
            : parameters.Any(p => p.Kind == ParameterKind.Component) ? SystemKind.PerEntity
            : SystemKind.Serial;

        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            bool allowed = parameter.Kind switch
            {
                ParameterKind.Context => true,
                ParameterKind.Query => kind == SystemKind.TypedQuery && parameters.Count(p => p.Kind == ParameterKind.Query) == 1,
                ParameterKind.ChunkView => kind == SystemKind.Chunk && parameters.Count(p => p.Kind == ParameterKind.ChunkView) == 1,
                ParameterKind.Component => kind == SystemKind.PerEntity,
                ParameterKind.EntityId => kind == SystemKind.PerEntity,
                _ => false,
            };

            if (!allowed)
            {
                var location = method.Parameters[i].Locations.FirstOrDefault() ?? Location.None;
                diagnostics.Add(DiagnosticInfo.Create(GeneratorDiagnostics.SystemParameter, location, parameter.Name, method.Name, "a System is either per Entity (ref/in Components, EntityId, in SystemContext), per chunk (in ChunkView or in " + method.Name + "Query, plus in SystemContext) or serial (in SystemContext only)"));
            }
        }

        return kind;
    }
}
