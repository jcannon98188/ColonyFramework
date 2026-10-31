namespace Colony;

/// <summary>
/// What a <see cref="WorldSchema"/> knows about one registered Component: its stable name, its layout
/// (size and alignment) and the hash of both, which together are the Component's identity for saves and
/// Plugins (spec section 4.1). The CLR type is only a convenience for typed access in process.
/// </summary>
public readonly record struct ComponentDescriptor(
    ComponentType Type,
    string Name,
    int Size,
    int Alignment,
    ulong LayoutHash,
    ComponentOptions Flags)
{
    public bool IsNonFragmenting => (Flags & ComponentOptions.NonFragmenting) != 0;

    /// <summary>True for the Component behind a Relation (<see cref="WorldBuilder.AddRelation"/>).</summary>
    public bool IsRelation => (Flags & ComponentOptions.Relation) != 0;

    /// <summary>True for a buffer Component (inline elements overflowing to the World allocator).</summary>
    public bool IsBuffer => (Flags & ComponentOptions.Buffer) != 0;

    /// <summary>Element size of a buffer Component; 0 otherwise.</summary>
    public int ElementSize { get; init; }

    /// <summary>Elements a buffer Component stores inline before overflowing; 0 otherwise.</summary>
    public int InlineCapacity { get; init; }

    /// <summary>The fields of the layout (spec section 4.1); empty when the Component was registered by name only.</summary>
    public System.Collections.Immutable.ImmutableArray<ComponentField> Fields { get; init; } = System.Collections.Immutable.ImmutableArray<ComponentField>.Empty;
}
