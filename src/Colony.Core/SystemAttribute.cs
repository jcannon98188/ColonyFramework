namespace Colony;

/// <summary>The six named Phases of a Tick (spec section 4.3), as constants usable in attributes. See <see cref="Phase"/>.</summary>
public enum NamedPhase
{
    Streaming = 0,
    Input = 1,
    Begin = 2,
    Simulate = 3,
    Late = 4,
    End = 5,
}

/// <summary>
/// Marks a type or method as System code (spec section 4.3): logic that runs inside a Tick and must obey the
/// Tier 1 determinism contract (spec section 6). The Colony determinism analyzers (COL1001-COL1007) check every
/// member of a marked type and every marked method, including lambdas and local functions inside them.
/// On a static method of a partial class, the source generator (spec section 11) also emits the System entry
/// point and a <see cref="SystemDefinition"/>: <c>ref</c>/<c>in</c> Component parameters declare the access
/// set, the Phase and rate come from this attribute, and <see cref="After"/>/<see cref="Before"/> order it.
/// </summary>
/// <remarks>
/// The analyzers recognise this attribute by its fully qualified name, <c>Colony.SystemAttribute</c>, so they do
/// not reference <c>Colony.Core</c>. Suppress a single rule at a single site with
/// <see cref="AllowNonDeterministicAttribute"/> or a <c>#pragma warning disable COL1xxx</c> block.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class SystemAttribute : Attribute
{
    /// <summary>Marks System code; a generated System runs in <see cref="NamedPhase.Simulate"/>.</summary>
    public SystemAttribute()
    {
        PhaseName = nameof(NamedPhase.Simulate);
    }

    /// <summary>Marks a generated System that runs in the named Phase.</summary>
    public SystemAttribute(NamedPhase phase)
    {
        PhaseName = phase.ToString();
    }

    /// <summary>Marks a generated System that runs in the Phase named <paramref name="phase"/>: a named Phase or a Game Phase added with <see cref="WorldBuilder.AddPhase"/>.</summary>
    public SystemAttribute(string phase)
    {
        PhaseName = phase;
    }

    /// <summary>The Phase the generated System runs in.</summary>
    public string PhaseName { get; }

    /// <summary>The System name; defaults to the method name.</summary>
    public string? Name { get; set; }

    /// <summary>Run every N Ticks (1 = every Tick).</summary>
    public int Every { get; set; } = 1;

    /// <summary>Which Tick modulo <see cref="Every"/> the System runs on; -1 derives it from the System id.</summary>
    public int Offset { get; set; } = -1;

    /// <summary>Names of Systems in the same Phase that must run before this one.</summary>
    public string[] After { get; set; } = [];

    /// <summary>Names of Systems in the same Phase that must run after this one.</summary>
    public string[] Before { get; set; } = [];

    /// <summary>Skip chunks whose asleep bit is set (<see cref="SystemDefinition.SkipAsleepChunks"/>).</summary>
    public bool SkipAsleepChunks { get; set; }
}
