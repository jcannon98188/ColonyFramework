using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Colony.Generators;

/// <summary>
/// COL2005: a generated System's body reaches a Component column or ref that is not in its declared access set
/// (spec section 4.3: a System writes only its declared columns; the scheduler orders Systems by those
/// declarations). Detectable accesses are the typed accessors <c>ChunkView.Column&lt;T&gt;</c>,
/// <c>ChunkView.PaddedColumn&lt;T&gt;</c> and <c>World.Get&lt;T&gt;</c> inside a <c>[System]</c> method, its
/// lambdas and local functions. The declared set is the method's ref/in Component parameters plus the types
/// listed in [Reads]/[Writes]. The analyzer matches Colony types by name and needs no reference to Colony.Core.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SystemAccessAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> TypedAccessors = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Colony.ChunkView.Column",
        "Colony.ChunkView.PaddedColumn",
        "Colony.World.Get");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(GeneratorDiagnostics.UndeclaredAccess);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolStartAction(static symbolContext =>
        {
            if (symbolContext.Symbol is not IMethodSymbol method || !IsGeneratedSystem(method))
            {
                return;
            }

            var declared = DeclaredAccess(method);
            symbolContext.RegisterOperationAction(
                operationContext => AnalyzeInvocation(operationContext, method, declared),
                OperationKind.Invocation);
        }, SymbolKind.Method);
    }

    /// <summary>A static [System] method of a partial type: the shape the generator turns into a System entry.</summary>
    private static bool IsGeneratedSystem(IMethodSymbol method) =>
        method.IsStatic
        && SymbolHelpers.HasAttribute(method, SymbolHelpers.SystemAttributeName)
        && SymbolHelpers.IsPartialChain(method.ContainingType);

    private static ImmutableHashSet<ITypeSymbol> DeclaredAccess(IMethodSymbol method)
    {
        var builder = ImmutableHashSet.CreateBuilder<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None && parameter.Type.TypeKind == TypeKind.Struct)
            {
                builder.Add(parameter.Type);
            }
        }

        foreach (var attribute in method.GetAttributes())
        {
            if (SymbolHelpers.IsAttribute(attribute, SymbolHelpers.ReadsAttributeName) || SymbolHelpers.IsAttribute(attribute, SymbolHelpers.WritesAttributeName))
            {
                foreach (var type in SymbolHelpers.TypeArguments(attribute))
                {
                    builder.Add(type);
                }
            }
        }

        return builder.ToImmutable();
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, IMethodSymbol system, ImmutableHashSet<ITypeSymbol> declared)
    {
        if (context.Operation is not IInvocationOperation { TargetMethod: { IsGenericMethod: true, TypeArguments.Length: 1 } target } invocation)
        {
            return;
        }

        if (!TypedAccessors.Contains(target.ContainingType.ToDisplayString() + "." + target.Name))
        {
            return;
        }

        var component = target.TypeArguments[0];
        if (component.TypeKind != TypeKind.Struct || declared.Contains(component))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(GeneratorDiagnostics.UndeclaredAccess, invocation.Syntax.GetLocation(), system.Name, component.Name));
    }
}
