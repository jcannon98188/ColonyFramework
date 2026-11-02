namespace Colony;

/// <summary>
/// Handle to a Relation registered in a <see cref="WorldSchema"/> (spec section 4.1): a named link from a
/// source Entity to one target Entity, queryable from either end. The forward link is stored as a
/// built-in non-fragmenting Component (<see cref="Component"/>, value <see cref="RelationLink"/>) and is
/// saved and hashed with the World; the reverse index behind <see cref="World.SourcesOf"/> is derived
/// state that <see cref="World.RebuildRelationIndex"/> recreates.
/// </summary>
public readonly struct RelationType : IEquatable<RelationType>
{
    internal RelationType(int id, ComponentType component)
    {
        Id = id;
        Component = component;
    }

    /// <summary>Dense id inside the owning schema (registration order).</summary>
    public int Id { get; }

    /// <summary>The non-fragmenting Component that holds each source's <see cref="RelationLink"/>.</summary>
    public ComponentType Component { get; }

    public bool Equals(RelationType other) => Id == other.Id && Component == other.Component;

    public override bool Equals(object? obj) => obj is RelationType other && Equals(other);

    public override int GetHashCode() => (Id * 397) ^ Component.Id;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Relation#{Id}");

    public static bool operator ==(RelationType left, RelationType right) => left.Equals(right);

    public static bool operator !=(RelationType left, RelationType right) => !left.Equals(right);
}

/// <summary>Registration options for a Relation (spec section 4.1).</summary>
[Flags]
public enum RelationOptions
{
    None = 0,

    /// <summary>Destroying the target also destroys every source, recursively (parent/child hierarchy). Without it the sources merely lose the link.</summary>
    CascadeDelete = 1,
}

/// <summary>The value of a Relation's Component: the source's current target. Change it only through <see cref="CommandBuffer.Relate"/> / <see cref="CommandBuffer.Unrelate"/> so the reverse index stays current.</summary>
public readonly record struct RelationLink(EntityId Target);

/// <summary>What a <see cref="WorldSchema"/> knows about one registered Relation.</summary>
public readonly record struct RelationDescriptor(RelationType Type, string Name, RelationOptions Options)
{
    public ComponentType Component => Type.Component;

    public bool CascadeDelete => (Options & RelationOptions.CascadeDelete) != 0;
}
