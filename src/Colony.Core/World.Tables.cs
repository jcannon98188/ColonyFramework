namespace Colony;

public sealed partial class World
{
    /// <summary>The interned string table: dense <see cref="StringId"/>s in insertion order, saved with the World.</summary>
    public StringTable Strings { get; } = new();

    /// <summary>The immutable blob store: dense <see cref="BlobRef{T}"/>s in store order, saved with the World.</summary>
    public BlobStore Blobs { get; } = new();
}
