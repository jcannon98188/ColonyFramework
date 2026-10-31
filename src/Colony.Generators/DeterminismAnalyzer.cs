using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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

    /// <summary>COL1007: System.Random.</summary>
    public static readonly DiagnosticDescriptor SystemRandom = Rule(
        "COL1007",
        "System.Random in System code",
        "'{0}' is not replayable; use the counter-based RNG keyed by (World seed, stream key, counter) instead (spec section 6)");

    // Declared after the descriptors: static field initializers run in textual order.
    private static readonly ImmutableArray<DiagnosticDescriptor> All =
        ImmutableArray.Create(NonDeterministicMath, VariableWidthVector, WallClockTime, NewGuid, SystemRandom);

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
        });
    }

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

        if (symbol is IMethodSymbol method && BannedMath.IsBanned(method))
        {
            Report(context, NonDeterministicMath, method);
            return;
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
            && named.ContainingNamespace is { } ns
            && Types.TryGetValue(QualifiedMetadataName(ns, named.MetadataName), out var rule)
                ? rule
                : null;

        public static DiagnosticDescriptor? ForMember(ISymbol member) =>
            member.ContainingType is { ContainingNamespace: { } ns } owner
            && Members.TryGetValue(QualifiedMetadataName(ns, owner.MetadataName) + "." + member.Name, out var rule)
                ? rule
                : null;

        private static string QualifiedMetadataName(INamespaceSymbol ns, string metadataName) =>
            ns.IsGlobalNamespace ? metadataName : ns.ToDisplayString() + "." + metadataName;
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
