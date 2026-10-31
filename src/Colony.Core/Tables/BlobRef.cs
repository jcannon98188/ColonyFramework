namespace Colony;

/// <summary>
/// A 32-bit handle to an immutable blob of <typeparamref name="T"/> in a World's <see cref="BlobStore"/>
/// (spec section 4.1). Ids are dense and assigned in store order; 0 (<c>default</c>) is null. The element type is
/// checked on read, so a ref of the wrong type throws instead of reinterpreting bytes. Plain data: store it in
/// Components, Resources and other blobs.
/// </summary>
public readonly record struct BlobRef<T>(uint Id)
    where T : unmanaged
{
    public bool IsNull => Id == 0;

    public override string ToString() => IsNull ? $"BlobRef<{typeof(T).Name}>.Null" : $"BlobRef<{typeof(T).Name}>({Id})";
}
