using Microsoft.CodeAnalysis;

namespace Colony.Generators;

/// <summary>
/// Scaffold for the Colony incremental source generator (spec sections 3 and 11).
/// It registers no outputs yet; TASK-11 adds Component, System and typed-query generation.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ColonyGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Intentionally empty: the scaffold must produce no diagnostics and no source.
    }
}
