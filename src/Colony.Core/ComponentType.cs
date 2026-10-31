namespace Colony;

/// <summary>
/// Handle to a Component registered in a <see cref="WorldSchema"/>. Identity is the registered name plus
/// the layout hash, never the CLR type (spec section 4.1); the handle is just the schema's dense id.
/// </summary>
public readonly struct ComponentType : IEquatable<ComponentType>
{
    internal ComponentType(int id)
    {
        Id = id;
    }

    /// <summary>Dense id inside the owning schema; also the column order inside a chunk.</summary>
    public int Id { get; }

    public bool Equals(ComponentType other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is ComponentType other && Equals(other);

    public override int GetHashCode() => Id;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Component#{Id}");

    public static bool operator ==(ComponentType left, ComponentType right) => left.Equals(right);

    public static bool operator !=(ComponentType left, ComponentType right) => !left.Equals(right);
}

/// <summary>Registration options for a Component (spec section 4.1).</summary>
[Flags]
public enum ComponentOptions
{
    None = 0,

    /// <summary>
    /// Opt-in non-fragmenting mode for Components that flip on and off often: the Component lives in a
    /// World-level sparse column keyed by Entity index instead of the archetype, so adding or removing
    /// it is not a structural change and never moves the Entity between chunks. Non-fragmenting
    /// Components are not part of archetype signatures and do not take part in chunk queries.
    /// </summary>
    NonFragmenting = 1,
}
