namespace Colony;

/// <summary>
/// What a <see cref="WorldSchema"/> knows about one registered Resource: its stable name, the layout of
/// the struct it holds (one value, or the element of an event queue), the hash of both, and whether it
/// is saved and hashed with the World (spec sections 4.1, 4.3 and 6).
/// </summary>
public readonly record struct ResourceDescriptor(
    ResourceType Type,
    string Name,
    int Size,
    int Alignment,
    ulong LayoutHash,
    ResourceKind Kind,
    bool IsPersistent)
{
    public bool IsEventQueue => Kind == ResourceKind.EventQueue;
}
