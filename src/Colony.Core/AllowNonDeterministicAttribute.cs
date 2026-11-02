namespace Colony;

/// <summary>
/// Exempts a method, property, field or type inside System code from the Colony determinism analyzers
/// (COL1001-COL1007, spec section 6). The <see cref="Justification"/> is mandatory and should state why the
/// use cannot affect simulation state (for example diagnostics-only code that never writes a Component).
/// For a finer-grained exemption use <c>#pragma warning disable COL1xxx</c> around the statement.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property |
    AttributeTargets.Field | AttributeTargets.Constructor,
    Inherited = false,
    AllowMultiple = false)]
public sealed class AllowNonDeterministicAttribute : Attribute
{
    /// <summary>Creates the exemption with a human-readable justification.</summary>
    /// <param name="justification">Why the non-deterministic API cannot influence World state.</param>
    public AllowNonDeterministicAttribute(string justification)
    {
        Justification = justification;
    }

    /// <summary>Why the non-deterministic API cannot influence World state.</summary>
    public string Justification { get; }
}
