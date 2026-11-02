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

    /// <summary>
    /// Set by <see cref="WorldBuilder.AddRelation"/> on the non-fragmenting Component that stores a
    /// Relation's forward link (<see cref="RelationLink"/>). Not for direct registration.
    /// </summary>
    Relation = 2,

    /// <summary>
    /// Set by the builder's buffer Component registration: the Component's elements live inline up to a
    /// declared capacity and overflow to the World's size-class allocator. Hashed logically (length and
    /// elements), never by raw bytes. Not for direct registration.
    /// </summary>
    Buffer = 4,
}
