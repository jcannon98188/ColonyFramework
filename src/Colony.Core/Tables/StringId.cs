namespace Colony;

/// <summary>
/// A 32-bit handle to a string interned in a World's <see cref="StringTable"/> (spec section 4.1). Ids are
/// dense and assigned in insertion order, so the same insertion sequence yields the same ids in every
/// World; 0 is <see cref="Null"/>. Plain data: store it in Components, Resources and blobs.
/// </summary>
public readonly record struct StringId(uint Value)
{
    public static StringId Null => default;

    public bool IsNull => Value == 0;

    public override string ToString() => IsNull ? "StringId.Null" : $"StringId({Value})";
}
