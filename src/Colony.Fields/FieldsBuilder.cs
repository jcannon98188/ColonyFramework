using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony.Fields;

/// <summary>
/// Registers the Fields of a World (spec section 5.1): the bounded <see cref="WorldSize"/> and one named,
/// unmanaged cell type per Field. Created by <see cref="WorldBuilderExtensions.AddFields"/>, frozen by
/// <see cref="WorldBuilder.Build"/>; each World then gets its own <see cref="FieldSet"/>.
/// </summary>
public sealed class FieldsBuilder : WorldExtensionDefinition
{
    /// <summary>Seed of Field layout hashes ("FLDL").</summary>
    private const ulong LayoutSeed = 0x464C_444CUL;

    /// <summary>Seed of the Fields extension layout hash ("FLDS").</summary>
    private const ulong ExtensionSeed = 0x464C_4453UL;

    private readonly List<FieldDescriptor> _fields = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly List<byte[]> _initialValues = [];
    private readonly List<Type> _cellTypes = [];
    private ChunkLayout? _layout;
    private bool _frozen;
    private ulong _layoutHash;

    internal FieldsBuilder(WorldSize size)
    {
        Size = size;
    }

    public WorldSize Size { get; }

    public override string Name => "fields";

    public override ulong LayoutHash => _layoutHash;

    /// <summary>The chunk grid shared by every World of this schema; available after <see cref="Freeze"/>.</summary>
    internal ChunkLayout Layout => _layout ?? throw new InvalidOperationException("The Fields builder has not been frozen yet.");

    /// <summary>
    /// Registers a Field of cells <typeparamref name="T"/> under a stable <paramref name="name"/>; every
    /// chunk starts uniform with <paramref name="initialValue"/>. The layout hash covers name, size and
    /// alignment, never the CLR type. Field ids are registration order.
    /// </summary>
    public Field<T> AddField<T>(string name, T initialValue = default)
        where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_frozen)
        {
            throw new InvalidOperationException("The schema has been built; Field registration is closed.");
        }

        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"A Field named '{name}' is already registered.", nameof(name));
        }

        int size = Unsafe.SizeOf<T>();
        int alignment = Unsafe.SizeOf<AlignmentProbe<T>>() - size;
        ulong hash = Hash64.Compute(MemoryMarshal.AsBytes(name.AsSpan()), LayoutSeed);
        hash = Hash64.Combine(hash, (ulong)size);
        hash = Hash64.Combine(hash, (ulong)alignment);
        int id = _fields.Count;
        _fields.Add(new FieldDescriptor(id, name, size, alignment, hash));
        _byName.Add(name, id);
        _initialValues.Add(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in initialValue)).ToArray());
        _cellTypes.Add(typeof(T));
        return new Field<T>(id);
    }

    /// <summary>The handle of an already-registered Field, for System definitions written before <see cref="WorldBuilder.Build"/>.</summary>
    public Field<T> Field<T>(string name)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_byName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Field named '{name}' is registered; call AddField first.", nameof(name));
        }

        if (_cellTypes[id] != typeof(T))
        {
            throw new ArgumentException($"Field '{name}' has cells of type {_cellTypes[id].Name}, not {typeof(T).Name}.", nameof(name));
        }

        return new Field<T>(id);
    }

    public override void Freeze()
    {
        if (_frozen)
        {
            return;
        }

        _frozen = true;
        _layout = new ChunkLayout(Size);
        ulong hash = Hash64.Compute(ReadOnlySpan<byte>.Empty, ExtensionSeed);
        hash = Hash64.Combine(hash, (ulong)Size.X);
        hash = Hash64.Combine(hash, (ulong)Size.Y);
        hash = Hash64.Combine(hash, (ulong)Size.Z);
        foreach (var field in _fields)
        {
            hash = Hash64.Combine(hash, field.LayoutHash);
        }

        _layoutHash = hash;
    }

    public override IWorldExtension Create(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Freeze();
        return new FieldSet(world, _layout!, _fields, _cellTypes, _initialValues);
    }

    /// <summary>The offset of the value equals the natural alignment of <typeparamref name="T"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AlignmentProbe<T>
        where T : unmanaged
    {
#pragma warning disable CS0169, CA1823, IDE0051 // Fields are read by the layout only.
        private byte _pad;
        private T _value;
#pragma warning restore CS0169, CA1823, IDE0051
    }
}

/// <summary>Registration entry point of the Fields package on the Core builder.</summary>
public static class WorldBuilderExtensions
{
    /// <summary>
    /// Gives Worlds of this schema a bounded tile grid of <paramref name="size"/> and returns the builder on
    /// which Fields are registered. Call once per schema.
    /// </summary>
    public static FieldsBuilder AddFields(this WorldBuilder builder, WorldSize size)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var fields = new FieldsBuilder(size);
        builder.AddExtension(fields);
        return fields;
    }
}

/// <summary>Access to a World's Fields.</summary>
public static class WorldFieldsExtensions
{
    /// <summary>The World's <see cref="FieldSet"/>; throws when the schema registered no Fields.</summary>
    public static FieldSet Fields(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Extension<FieldSet>();
    }
}
