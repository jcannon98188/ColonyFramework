using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Colony.Generators;

/// <summary>
/// Bans the APIs of spec section 6 that silently break Tier 1 determinism inside System code:
/// code lexically inside a type or method marked <c>[Colony.System]</c>. Each rule has its own id (COL1001-COL1007)
/// so a site can opt out with <c>#pragma warning disable COL1xxx</c>; a whole member or type opts out with
/// <c>[Colony.AllowNonDeterministic("why")]</c>. The attributes are matched by name, so the analyzer needs no
/// reference to Colony.Core and runs with or without the source generator.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "Colony.Determinism";
    private const string SystemAttributeName = "Colony.SystemAttribute";
    private const string AllowAttributeName = "Colony.AllowNonDeterministicAttribute";

    /// <summary>COL1001: Math/MathF/double/float transcendentals.</summary>
    public static readonly DiagnosticDescriptor NonDeterministicMath = Rule(
        "COL1001",
        "Non-deterministic math API in System code",
        "'{0}' is not bit-identical across CPUs; use the Colony math layer instead (spec section 6)");

    /// <summary>COL1002: System.Numerics.Vector&lt;T&gt; and the Vector helper class (width varies per CPU).</summary>
    public static readonly DiagnosticDescriptor VariableWidthVector = Rule(
        "COL1002",
        "Variable-width Vector<T> in System code",
        "'{0}' has a CPU-dependent width; use the Colony math layer's fixed-width SIMD instead (spec section 6)");

    /// <summary>COL1003: DateTime, DateTimeOffset, Stopwatch and Environment.TickCount.</summary>
    public static readonly DiagnosticDescriptor WallClockTime = Rule(
        "COL1003",
        "Wall-clock time in System code",
        "'{0}' reads wall-clock time, which never enters the simulation; use the Tick counter instead (spec sections 4.3 and 6)");

    /// <summary>COL1004: Guid.NewGuid.</summary>
    public static readonly DiagnosticDescriptor NewGuid = Rule(
        "COL1004",
        "Guid.NewGuid in System code",
        "'{0}' is random per call; derive ids from Entity IDs or the counter-based RNG instead (spec section 6)");

    /// <summary>COL1005: GetHashCode on a reference type (identity hash or randomised string hash).</summary>
    public static readonly DiagnosticDescriptor ReferenceTypeHashCode = Rule(
        "COL1005",
        "GetHashCode on a reference type in System code",
        "'{0}' hashes a reference type, which varies per process; hash unmanaged Component data or Entity IDs instead (spec section 6)");

    /// <summary>COL1006: enumerating an unordered managed collection.</summary>
    public static readonly DiagnosticDescriptor UnorderedEnumeration = Rule(
        "COL1006",
        "Enumeration of an unordered collection in System code",
        "Enumerating '{0}' yields an unspecified order; iterate an ordered collection and end every ordering with an Entity ID tiebreak (spec section 6)");

    /// <summary>COL1007: System.Random.</summary>
    public static readonly DiagnosticDescriptor SystemRandom = Rule(
        "COL1007",
        "System.Random in System code",
        "'{0}' is not replayable; use the counter-based RNG keyed by (World seed, stream key, counter) instead (spec section 6)");

    // Declared after the descriptors: static field initializers run in textual order.
    private static readonly ImmutableArray<DiagnosticDescriptor> All =
        ImmutableArray.Create(
            NonDeterministicMath, VariableWidthVector, WallClockTime, NewGuid, ReferenceTypeHashCode, UnorderedEnumeration, SystemRandom);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => All;

    /// <summary>True when <paramref name="id"/> is one of this analyzer's diagnostic ids.</summary>
    public static bool IsKnownId(string id) => All.Any(d => d.Id == id);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(static startContext =>
        {
            var scope = new SystemScope();
            startContext.RegisterSyntaxNodeAction(
                nodeContext => AnalyzeName(nodeContext, scope),
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName);
            startContext.RegisterOperationAction(
                operationContext => AnalyzeEnumeration(operationContext, scope),
                OperationKind.Loop,
                OperationKind.Conversion,
                OperationKind.Invocation);
        });
    }

    /// <summary>
    /// COL1006: a foreach over an unordered collection, an implicit conversion of one to IEnumerable (LINQ, collection
    /// constructors) or an explicit GetEnumerator call.
    /// </summary>
    private static void AnalyzeEnumeration(OperationAnalysisContext context, SystemScope scope)
    {
        if (!scope.IsSystemCode(context.ContainingSymbol))
        {
            return;
        }

        switch (context.Operation)
        {
            case IForEachLoopOperation loop:
                ReportIfUnordered(context, Unwrap(loop.Collection));
                break;

            case IConversionOperation conversion
                when conversion.IsImplicit && IsEnumerableInterface(conversion.Type) && conversion.Parent is not IForEachLoopOperation:
                ReportIfUnordered(context, Unwrap(conversion.Operand));
                break;

            case IInvocationOperation { TargetMethod.Name: "GetEnumerator", Instance: { } receiver } invocation
                when UnorderedCollections.IsUnordered(receiver.Type):
                var location = invocation.Syntax is InvocationExpressionSyntax call ? call.Expression.GetLocation() : invocation.Syntax.GetLocation();
                context.ReportDiagnostic(Diagnostic.Create(UnorderedEnumeration, location, DisplayName(receiver.Type!)));
                break;
        }
    }

    private static void ReportIfUnordered(OperationAnalysisContext context, IOperation collection)
    {
        if (UnorderedCollections.IsUnordered(collection.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnorderedEnumeration, collection.Syntax.GetLocation(), DisplayName(collection.Type!)));
        }
    }

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true, Operand: var inner })
        {
            operation = inner;
        }

        return operation;
    }

    private static bool IsEnumerableInterface(ITypeSymbol? type) =>
        type?.OriginalDefinition.SpecialType is SpecialType.System_Collections_IEnumerable or SpecialType.System_Collections_Generic_IEnumerable_T;

    private static string DisplayName(ISymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    private static void AnalyzeName(SyntaxNodeAnalysisContext context, SystemScope scope)
    {
        if (context.ContainingSymbol is null || !scope.IsSystemCode(context.ContainingSymbol))
        {
            return;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        if (symbol is null)
        {
            return;
        }

        if (symbol is IMethodSymbol method)
        {
            if (BannedMath.IsBanned(method))
            {
                Report(context, NonDeterministicMath, method);
                return;
            }

            if (HashesAReferenceType(context, method))
            {
                Report(context, ReferenceTypeHashCode, method);
                return;
            }
        }

        if (symbol is ITypeSymbol type)
        {
            if (BannedApis.ForType(type) is { } typeRule)
            {
                Report(context, typeRule, type);
            }

            return;
        }

        if (BannedApis.ForMember(symbol) is { } memberRule)
        {
            Report(context, memberRule, symbol);
            return;
        }

        // A member of a banned type reached through something declared outside System code
        // ("GetRng().Next()", "_hostClock.Elapsed"). When the receiver is a type name, a local or parameter
        // declared here, or another banned-type member, the chain was already reported upstream.
        if (symbol.ContainingType is { } owner
            && BannedApis.ForType(owner) is { } ownerRule
            && ReceiverIsUnreported(context, scope))
        {
            Report(context, ownerRule, symbol);
        }
    }

    /// <summary>
    /// GetHashCode whose hashed operand is a reference type (or an unconstrained type parameter), including
    /// RuntimeHelpers.GetHashCode, IEqualityComparer.GetHashCode(T), string.GetHashCode(span) and HashCode.Combine/Add
    /// with reference-type arguments.
    /// </summary>
    private static bool HashesAReferenceType(SyntaxNodeAnalysisContext context, IMethodSymbol method)
    {
        if (method.Name == "GetHashCode")
        {
            if (method.ContainingType.SpecialType == SpecialType.System_String)
            {
                return true;
            }

            if (method.Parameters.Length == 1)
            {
                return MayBeReference(method.Parameters[0].Type);
            }

            if (method.Parameters.Length == 0)
            {
                return MayBeReference(ReceiverType(context, method));
            }

            return false;
        }

        if (method.Name is "Combine" or "Add"
            && method.ContainingType.ToDisplayString() == "System.HashCode")
        {
            return method.TypeArguments.Any(MayBeReference);
        }

        return false;
    }

    private static ITypeSymbol? ReceiverType(SyntaxNodeAnalysisContext context, IMethodSymbol method)
    {
        if (context.Node.Parent is MemberAccessExpressionSyntax { Expression: var receiver })
        {
            return context.SemanticModel.GetTypeInfo(receiver, context.CancellationToken).Type;
        }

        // Bare "GetHashCode()": the receiver is "this" (or the overridden base when the method is a base member).
        return method.IsStatic ? null : context.ContainingSymbol?.ContainingType;
    }

    private static bool MayBeReference(ITypeSymbol? type) =>
        type is not null && (type.IsReferenceType || (type is ITypeParameterSymbol tp && !tp.IsValueType));

    private static bool ReceiverIsUnreported(SyntaxNodeAnalysisContext context, SystemScope scope)
    {
        if (context.Node.Parent is not MemberAccessExpressionSyntax { Expression: var receiver })
        {
            return false;
        }

        var receiverSymbol = context.SemanticModel.GetSymbolInfo(receiver, context.CancellationToken).Symbol;
        return receiverSymbol switch
        {
            null or ITypeSymbol or ILocalSymbol or IParameterSymbol => false,
            IMethodSymbol method => !(method.ContainingType is { } t && BannedApis.ForType(t) is not null),
            _ => !(receiverSymbol.ContainingType is { } t && BannedApis.ForType(t) is not null)
                 && !scope.IsSystemCode(receiverSymbol),
        };
    }

    private static void Report(SyntaxNodeAnalysisContext context, DiagnosticDescriptor descriptor, ISymbol symbol)
    {
        var node = context.Node;
        // Point at the whole member access ("Math.Sin") rather than the bare identifier when there is one.
        Location location = node.Parent is MemberAccessExpressionSyntax access && access.Name == node
            ? access.GetLocation()
            : node.GetLocation();
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
            location,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
    }

    private static DiagnosticDescriptor Rule(string id, string title, string messageFormat) =>
        new(id, title, messageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
            description: null,
            helpLinkUri: "https://github.com/jcannon98188/ColonyFramework/blob/main/docs/spec/architecture.md#6-determinism-decide-determinism-contract");

    /// <summary>
    /// Decides, per containing symbol, whether a syntax node is System code: lexically inside a type or method
    /// carrying <c>[Colony.System]</c> and not inside anything carrying <c>[Colony.AllowNonDeterministic]</c>.
    /// Cached per compilation; symbols are reference-equal within one.
    /// </summary>
    private sealed class SystemScope
    {
        private readonly ConcurrentDictionary<ISymbol, bool> _cache = new(SymbolEqualityComparer.Default);

        public bool IsSystemCode(ISymbol symbol) => _cache.GetOrAdd(symbol, static s => Compute(s));

        private static bool Compute(ISymbol symbol)
        {
            bool inSystem = false;
            for (ISymbol? current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
            {
                foreach (var attribute in current.GetAttributes())
                {
                    string? name = attribute.AttributeClass?.ToDisplayString();
                    if (name == AllowAttributeName)
                    {
                        return false;
                    }

                    if (name == SystemAttributeName)
                    {
                        inSystem = true;
                    }
                }
            }

            return inSystem;
        }
    }

    /// <summary>Spec section 6: types and members banned outright, keyed by fully qualified metadata name.</summary>
    private static class BannedApis
    {
        private static readonly ImmutableDictionary<string, DiagnosticDescriptor> Types =
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
            {
                new KeyValuePair<string, DiagnosticDescriptor>("System.Numerics.Vector`1", VariableWidthVector),
                new KeyValuePair<string, DiagnosticDescriptor>("System.Numerics.Vector", VariableWidthVector),
                new KeyValuePair<string, DiagnosticDescriptor>("System.DateTime", WallClockTime),
                new KeyValuePair<string, DiagnosticDescriptor>("System.DateTimeOffset", WallClockTime),
                new KeyValuePair<string, DiagnosticDescriptor>("System.Diagnostics.Stopwatch", WallClockTime),
                new KeyValuePair<string, DiagnosticDescriptor>("System.Random", SystemRandom),
            });

        private static readonly ImmutableDictionary<string, DiagnosticDescriptor> Members =
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
            {
                new KeyValuePair<string, DiagnosticDescriptor>("System.Guid.NewGuid", NewGuid),
                new KeyValuePair<string, DiagnosticDescriptor>("System.Environment.TickCount", WallClockTime),
                new KeyValuePair<string, DiagnosticDescriptor>("System.Environment.TickCount64", WallClockTime),
            });

        public static DiagnosticDescriptor? ForType(ITypeSymbol type) =>
            type.OriginalDefinition is INamedTypeSymbol named
            && Types.TryGetValue(MetadataFullName(named), out var rule)
                ? rule
                : null;

        public static DiagnosticDescriptor? ForMember(ISymbol member) =>
            member.ContainingType is { } owner
            && Members.TryGetValue(MetadataFullName(owner) + "." + member.Name, out var rule)
                ? rule
                : null;
    }

    /// <summary>Spec section 6: managed collections whose enumeration order is unspecified.</summary>
    private static class UnorderedCollections
    {
        private static readonly ImmutableHashSet<string> Types = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.Dictionary`2.KeyCollection",
            "System.Collections.Generic.Dictionary`2.ValueCollection",
            "System.Collections.Generic.HashSet`1",
            "System.Collections.Concurrent.ConcurrentDictionary`2",
            "System.Collections.Concurrent.ConcurrentBag`1");

        public static bool IsUnordered(ITypeSymbol? type) =>
            type?.OriginalDefinition is INamedTypeSymbol named && Types.Contains(MetadataFullName(named));
    }

    /// <summary>"System.Collections.Generic.Dictionary`2.KeyCollection": namespace, then containing types, then the type.</summary>
    private static string MetadataFullName(INamedTypeSymbol type)
    {
        string name = type.MetadataName;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            name = outer.MetadataName + "." + name;
        }

        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    /// <summary>Spec section 6: transcendentals on Math, MathF and the generic-math statics of double/float/Half.</summary>
    private static class BannedMath
    {
        private static readonly ImmutableHashSet<string> Transcendentals = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Sin", "Cos", "Tan", "SinCos", "SinPi", "CosPi", "TanPi", "SinCosPi",
            "Asin", "Acos", "Atan", "Atan2", "AsinPi", "AcosPi", "AtanPi", "Atan2Pi",
            "Sinh", "Cosh", "Tanh", "Asinh", "Acosh", "Atanh",
            "Exp", "Exp2", "Exp10", "ExpM1", "Exp2M1", "Exp10M1",
            "Log", "Log2", "Log10", "LogP1", "Log2P1", "Log10P1",
            "Pow", "Cbrt", "RootN", "Hypot");

        private static readonly ImmutableHashSet<string> HostTypes = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "System.Math", "System.MathF", "System.Double", "System.Single", "System.Half");

        public static bool IsBanned(IMethodSymbol method) =>
            method.IsStatic
            && Transcendentals.Contains(method.Name)
            && HostTypes.Contains(method.ContainingType.ToDisplayString());
    }
}
