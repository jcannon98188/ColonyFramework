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
}
