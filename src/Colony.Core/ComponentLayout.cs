using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Colony;

/// <summary>
/// Implemented by source-generated <c>[Component]</c> structs (spec section 11): the generator emits the
/// <see cref="Layout"/> so <see cref="WorldBuilder.AddComponent{T}()"/> can register the Component under its
/// stable name with a field-level layout hash (spec section 4.1) without reflection.
/// </summary>
/// <typeparam name="TSelf">The implementing Component struct.</typeparam>
public interface IComponent<TSelf> where TSelf : unmanaged, IComponent<TSelf>
{
    /// <summary>The stable name, options and fields of the Component.</summary>
    public static abstract ComponentLayout Layout { get; }
}

/// <summary>One field of a Component layout: its name, the stable name of its type, and its byte offset and size.</summary>
public readonly record struct ComponentField(string Name, string TypeName, int Offset, int Size);

/// <summary>
/// The identity of a Component as data (spec section 4.1): its stable registered name, options and the
/// fields it is made of. Size and alignment are measured by the builder; the layout hash covers all of it.
/// </summary>
public sealed class ComponentLayout
{
    /// <summary>Seed for Component layout hashes ("LAYT"), so they never collide with a World hash by construction.</summary>
    internal const ulong HashSeed = 0x4C41_5954UL;

    public ComponentLayout(string name, ComponentOptions flags, ImmutableArray<ComponentField> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Flags = flags;
        Fields = fields.IsDefault ? ImmutableArray<ComponentField>.Empty : fields;
        for (int i = 0; i < Fields.Length; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Fields[i].Name, nameof(fields));
            ArgumentException.ThrowIfNullOrWhiteSpace(Fields[i].TypeName, nameof(fields));
            if (Fields[i].Offset < 0 || Fields[i].Size <= 0)
            {
                throw new ArgumentException($"Field '{Fields[i].Name}' of Component '{name}' has an invalid offset or size.", nameof(fields));
            }
        }
    }

    public string Name { get; }

    public ComponentOptions Flags { get; }

    /// <summary>The instance fields in declaration order. Empty for Components registered by name only.</summary>
    public ImmutableArray<ComponentField> Fields { get; }

    /// <summary>
    /// Hashes a layout: the name, size and alignment first (the TASK-6 identity), then every field's name,
    /// type name, offset and size, so renaming or reordering fields changes the identity.
    /// </summary>
    public static ulong ComputeHash(string name, int size, int alignment, ImmutableArray<ComponentField> fields)
    {
        ArgumentNullException.ThrowIfNull(name);
        ulong hash = ComputeBaseHash(name, size, alignment);
        if (fields.IsDefaultOrEmpty)
        {
            return hash;
        }

        foreach (var field in fields)
        {
            hash = Hashing.Hash64.Combine(hash, Hashing.Hash64.Compute(MemoryMarshal.AsBytes(field.Name.AsSpan()), HashSeed));
            hash = Hashing.Hash64.Combine(hash, Hashing.Hash64.Compute(MemoryMarshal.AsBytes(field.TypeName.AsSpan()), HashSeed));
            hash = Hashing.Hash64.Combine(hash, (ulong)field.Offset);
            hash = Hashing.Hash64.Combine(hash, (ulong)field.Size);
        }

        return hash;
    }

    /// <summary>The layout a generated Component declares; the generator implements the interface explicitly, so this is the public accessor.</summary>
    public static ComponentLayout Of<T>() where T : unmanaged, IComponent<T> => T.Layout;

    /// <summary>The name, size and alignment hash used when a Component is registered without a layout.</summary>
    internal static ulong ComputeBaseHash(string name, int size, int alignment)
    {
        ulong hash = Hashing.Hash64.Compute(MemoryMarshal.AsBytes(name.AsSpan()), HashSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)size);
        return Hashing.Hash64.Combine(hash, (ulong)alignment);
    }

    /// <summary>Checks that every field lies inside a struct of <paramref name="size"/> bytes.</summary>
    public void Validate(int size)
    {
        foreach (var field in Fields)
        {
            if (field.Offset + field.Size > size)
            {
                throw new ArgumentException($"Field '{field.Name}' of Component '{Name}' ends at byte {field.Offset + field.Size}, past the struct's {size} bytes.", nameof(size));
            }
        }
    }
}
