namespace Colony.Plugins;

/// <summary>
/// The baked Definitions of one registered type (spec section 8): names interned in the registry's string table,
/// values in one immutable blob. Lookups by name or <see cref="StringId"/> and reads allocate nothing, so Systems
/// use them inside Ticks. Capture the table from <see cref="DefinitionRegistration.AddType{T}"/> or fetch it
/// with <see cref="DefinitionRegistry.Table{T}"/>; it is empty until the schema is built.
/// </summary>
public abstract class DefinitionTable
{
    private protected DefinitionTable(DefinitionRegistry registry, string typeName)
    {
        Registry = registry;
        TypeName = typeName;
    }

    /// <summary>The registered Definition type name (for example <c>item</c>).</summary>
    public string TypeName { get; }

    public DefinitionRegistry Registry { get; }

    private protected StringId[] NameIds { get; set; } = [];

    /// <summary>Per registry <see cref="StringId"/> value: the Definition index, or -1 when the string is not a name of this type.</summary>
    private protected int[] IndexOfString { get; set; } = [];

    /// <summary>Definitions of this type; 0 until the schema is built.</summary>
    public int Count => NameIds.Length;

    public bool IsBaked { get; private protected set; }

    /// <summary>The index of the Definition named <paramref name="name"/>, without allocating.</summary>
    public bool TryFind(ReadOnlySpan<char> name, out int index)
    {
        if (Registry.Strings.TryGetId(name, out var id))
        {
            return TryFind(id, out index);
        }

        index = -1;
        return false;
    }

    /// <summary>The index of the Definition whose interned name is <paramref name="name"/> (a <see cref="StringId"/> of this registry).</summary>
    public bool TryFind(StringId name, out int index)
    {
        if (name.Value < (uint)IndexOfString.Length)
        {
            index = IndexOfString[name.Value];
            return index >= 0;
        }

        index = -1;
        return false;
    }

    /// <summary>The index of the Definition named <paramref name="name"/>; throws when there is none.</summary>
    public int IndexOf(ReadOnlySpan<char> name)
    {
        if (!TryFind(name, out int index))
        {
            throw new ArgumentException($"No Definition of type '{TypeName}' is named '{name}'.", nameof(name));
        }

        return index;
    }

    /// <summary>The interned name of the Definition at <paramref name="index"/>.</summary>
    public StringId NameIdOf(int index) => NameIds[index];

    public ReadOnlySpan<char> NameOf(int index) => Registry.Strings.Get(NameIds[index]);

    /// <summary>Bytes per Definition value: the registered struct size.</summary>
    internal abstract int Stride { get; }

    /// <summary>Every baked value as raw bytes, <see cref="Stride"/> each, in index order (the C ABI reads tables this way).</summary>
    internal abstract ReadOnlySpan<byte> RawValues { get; }

    private protected void ThrowIfNotBaked()
    {
        if (!IsBaked)
        {
            throw new InvalidOperationException($"Definitions of type '{TypeName}' are not baked yet; build the schema first.");
        }
    }
}

/// <summary>The typed view of a Definition table: <see cref="Values"/> and <see cref="this[int]"/> read the baked blob.</summary>
public sealed class DefinitionTable<T> : DefinitionTable
    where T : unmanaged
{
    private BlobRef<T> _blob;

    internal DefinitionTable(DefinitionRegistry registry, string typeName)
        : base(registry, typeName)
    {
    }

    /// <summary>Every Definition of the type in index order.</summary>
    public ReadOnlySpan<T> Values
    {
        get
        {
            ThrowIfNotBaked();
            return Registry.Blobs.Get(_blob);
        }
    }

    public ref readonly T this[int index] => ref Values[index];

    internal override int Stride => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    internal override ReadOnlySpan<byte> RawValues => System.Runtime.InteropServices.MemoryMarshal.AsBytes(Values);

    /// <summary>The Definition named <paramref name="name"/>; throws when there is none.</summary>
    public ref readonly T this[ReadOnlySpan<char> name] => ref Values[IndexOf(name)];

    internal void Bake(StringId[] names, int[] indexOfString, BlobRef<T> blob)
    {
        NameIds = names;
        IndexOfString = indexOfString;
        _blob = blob;
        IsBaked = true;
    }
}

/// <summary>
/// A Definition table registered by layout alone (a Native Plugin's type, TASK-28): values are raw bytes of
/// <see cref="Stride"/> each. C# code reads it through <see cref="DefinitionRegistry.Table{T}"/> only when the
/// type was registered from C#; a raw table is read through the C ABI.
/// </summary>
internal sealed class RawDefinitionTable : DefinitionTable
{
    private readonly int _stride;
    private BlobRef<byte> _blob;

    internal RawDefinitionTable(DefinitionRegistry registry, string typeName, int stride)
        : base(registry, typeName)
    {
        _stride = stride;
    }

    internal override int Stride => _stride;

    internal override ReadOnlySpan<byte> RawValues
    {
        get
        {
            ThrowIfNotBaked();
            return Registry.Blobs.Get(_blob);
        }
    }

    internal void Bake(StringId[] names, int[] indexOfString, BlobRef<byte> blob)
    {
        NameIds = names;
        IndexOfString = indexOfString;
        _blob = blob;
        IsBaked = true;
    }
}
