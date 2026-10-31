namespace Colony;

/// <summary>
/// Marks a type or method as System code (spec section 4.3): logic that runs inside a Tick and must obey the
/// Tier 1 determinism contract (spec section 6). The Colony determinism analyzers (COL1001-COL1007) check every
/// member of a marked type and every marked method, including lambdas and local functions inside them.
/// The source generator (spec section 11) reads the same attribute to emit System entry points and metadata.
/// </summary>
/// <remarks>
/// The analyzers recognise this attribute by its fully qualified name, <c>Colony.SystemAttribute</c>, so they do
/// not reference <c>Colony.Core</c>. Suppress a single rule at a single site with
/// <see cref="AllowNonDeterministicAttribute"/> or a <c>#pragma warning disable COL1xxx</c> block.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class SystemAttribute : Attribute
{
}
