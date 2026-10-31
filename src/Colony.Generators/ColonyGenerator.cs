using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Colony.Generators;

/// <summary>
/// The Colony incremental source generator (spec sections 4.3 and 11). For every <c>[Component]</c> partial struct
/// it emits the field-level layout and equality members; for every <c>[System]</c> static method of a partial type
/// it emits the typed query, the chunk entry and a <c>SystemDefinition</c> factory, plus an <c>AddSystems</c>
/// registration method per type. Generated code lives in the user's assembly and uses only Colony.Core's public
/// surface. Shapes it cannot generate are reported as COL2001-COL2007.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ColonyGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var components = context.SyntaxProvider.ForAttributeWithMetadataName(
            SymbolHelpers.ComponentAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, cancellationToken) => ComponentParser.Parse((INamedTypeSymbol)ctx.TargetSymbol, ctx.Attributes[0], cancellationToken));

        context.RegisterSourceOutput(components, static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            if (model.Diagnostics.Count == 0)
            {
                spc.AddSource(ComponentEmitter.HintName(model), SourceText.From(ComponentEmitter.Emit(model), System.Text.Encoding.UTF8));
            }
        });

        var systems = context.SyntaxProvider.ForAttributeWithMetadataName(
            SymbolHelpers.SystemAttributeName,
            static (node, _) => node is MethodDeclarationSyntax,
            static (ctx, cancellationToken) => SystemParser.Parse((IMethodSymbol)ctx.TargetSymbol, ctx.Attributes[0], cancellationToken));

        var systemTypes = systems.Collect().SelectMany(static (all, _) => GroupByOwner(all));

        context.RegisterSourceOutput(systemTypes, static (spc, group) =>
        {
            bool anyError = false;
            foreach (var method in group)
            {
                foreach (var diagnostic in method.System.Diagnostics)
                {
                    anyError = true;
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            }

            if (!anyError)
            {
                spc.AddSource(SystemEmitter.HintName(group), SourceText.From(SystemEmitter.Emit(group), System.Text.Encoding.UTF8));
            }
        });
    }

    /// <summary>Groups System methods by owning type, in source order, so one file per type carries its registration method.</summary>
    private static ImmutableArray<EquatableArray<SystemMethodModel>> GroupByOwner(ImmutableArray<SystemMethodModel> all)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, ImmutableArray<SystemMethodModel>.Builder>(StringComparer.Ordinal);
        foreach (var method in all)
        {
            if (!groups.TryGetValue(method.OwnerFullName, out var builder))
            {
                builder = ImmutableArray.CreateBuilder<SystemMethodModel>();
                groups.Add(method.OwnerFullName, builder);
                order.Add(method.OwnerFullName);
            }

            builder.Add(method);
        }

        return order.Select(key => new EquatableArray<SystemMethodModel>(groups[key].ToImmutable())).ToImmutableArray();
    }
}
