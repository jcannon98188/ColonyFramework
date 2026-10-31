using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colony.Hashing;

namespace Colony.Plugins;

/// <summary>
/// The typed Definition registry of a schema (spec section 8): Plugins register unmanaged Definition types with
/// their field layouts, load Definitions from <see cref="IDefinitionSource"/>s and patch them, in load order;
/// <see cref="WorldBuilder.Build"/> bakes every type into one immutable blob with names interned in the
/// registry's own <see cref="StringTable"/>. The baked tables are shared by every World of the schema and read
/// without allocation. Definition content is not World state: it is neither saved nor hashed into the World;
/// the type layouts (names, sizes, alignments) are part of the schema hash.
/// </summary>
public sealed class DefinitionRegistry
{
    /// <summary>Seed of the Definition type layout hash ("DEFL").</summary>
    private const ulong LayoutSeed = 0x4445_464CUL;

    /// <summary>Seed of the extension layout hash ("DEFS").</summary>
    private const ulong ExtensionSeed = 0x4445_4653UL;

    private readonly List<DefinitionType> _types = [];
    private readonly Dictionary<string, int> _typeByName = new(StringComparer.Ordinal);
    private ulong _layoutHash;

    internal DefinitionRegistry()
    {
        Extension = new DefinitionsExtension(this);
    }

    /// <summary>Names of Definitions and text field values, interned at build. Lookups allocate nothing.</summary>
    public StringTable Strings { get; } = new();

    /// <summary>One blob per Definition type, written at build.</summary>
    public BlobStore Blobs { get; } = new();

    public bool IsFrozen { get; private set; }

    public int TypeCount => _types.Count;

    /// <summary>The schema extension that bakes the registry at build and exposes it per World (<see cref="WorldDefinitionsExtensions.Definitions"/>).</summary>
    internal WorldExtensionDefinition Extension { get; }

    /// <summary>The table of the Definition type <paramref name="typeName"/>; throws when none is registered or its struct is not <typeparamref name="T"/>.</summary>
    public DefinitionTable<T> Table<T>(string typeName)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(typeName);
        if (!_typeByName.TryGetValue(typeName, out int index))
        {
            throw new ArgumentException($"No Definition type named '{typeName}' is registered.", nameof(typeName));
        }

        return _types[index] is DefinitionType<T> typed
            ? typed.Table
            : throw new ArgumentException($"Definition type '{typeName}' holds {_types[index].ClrType.Name}, not {typeof(T).Name}.", nameof(typeName));
    }

    public bool HasType(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return _typeByName.ContainsKey(typeName);
    }

    internal DefinitionRegistration For(PluginManifest manifest, int pluginIndex) => new(this, manifest, pluginIndex);

    internal DefinitionTable<T> AddType<T>(string typeName, DefinitionLayout<T> layout, string pluginId)
        where T : unmanaged
    {
        ThrowIfFrozen();
        if (_typeByName.ContainsKey(typeName))
        {
            throw new PluginException(pluginId, $"Plugin '{pluginId}' registers Definition type '{typeName}', which is already registered.");
        }

        var type = new DefinitionType<T>(this, typeName, layout, pluginId);
        _typeByName.Add(typeName, _types.Count);
        _types.Add(type);
        return type.Table;
    }

    internal DefinitionType Type(string typeName, string pluginId)
    {
        if (!_typeByName.TryGetValue(typeName, out int index))
        {
            throw new PluginException(pluginId, $"Plugin '{pluginId}' refers to Definition type '{typeName}', which no Plugin registered (register it, or depend on the Plugin that does).");
        }

        return _types[index];
    }

    internal DefinitionType<T> Type<T>(string typeName, string pluginId)
        where T : unmanaged
    {
        var type = Type(typeName, pluginId);
        return type as DefinitionType<T>
            ?? throw new PluginException(pluginId, $"Plugin '{pluginId}' uses Definition type '{typeName}' as {typeof(T).Name}, but it holds {type.ClrType.Name}.");
    }

    internal void Apply(IDefinitionSource source, string pluginId)
    {
        ThrowIfFrozen();
        foreach (var document in source.Read())
        {
            ArgumentNullException.ThrowIfNull(document, nameof(source));
            Type(document.Type, pluginId).Apply(document, source.Name, pluginId);
        }
    }

    internal void Freeze()
    {
        if (IsFrozen)
        {
            return;
        }

        IsFrozen = true;
        ulong hash = Hash64.Compute(ReadOnlySpan<byte>.Empty, ExtensionSeed);
        foreach (var type in _types)
        {
            hash = Hash64.Combine(hash, type.LayoutHash);
            type.Bake();
        }

        _layoutHash = hash;
    }

    internal static ulong TypeLayoutHash(string name, int size, int alignment)
    {
        ulong hash = Hash64.Compute(MemoryMarshal.AsBytes(name.AsSpan()), LayoutSeed);
        hash = Hash64.Combine(hash, (ulong)size);
        return Hash64.Combine(hash, (ulong)alignment);
    }

    private void ThrowIfFrozen()
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException("The schema has been built; Definition registration is closed.");
        }
    }

    /// <summary>Folds the type layouts into the schema hash and hands every World a <see cref="DefinitionSet"/>.</summary>
    private sealed class DefinitionsExtension : WorldExtensionDefinition
    {
        private readonly DefinitionRegistry _registry;

        public DefinitionsExtension(DefinitionRegistry registry)
        {
            _registry = registry;
        }

        public override string Name => "definitions";

        public override ulong LayoutHash => _registry._layoutHash;

        public override void Freeze() => _registry.Freeze();

        public override IWorldExtension Create(World world) => new DefinitionSet(_registry);
    }

    /// <summary>One registered Definition type while it is being filled, and its baked table afterwards.</summary>
    internal abstract class DefinitionType
    {
        protected DefinitionType(string name, Type clrType, int size, int alignment, string pluginId)
        {
            Name = name;
            ClrType = clrType;
            LayoutHash = TypeLayoutHash(name, size, alignment);
            PluginId = pluginId;
        }

        public string Name { get; }

        public Type ClrType { get; }

        public ulong LayoutHash { get; }

        /// <summary>The Plugin that registered the type.</summary>
        public string PluginId { get; }

        public abstract void Apply(DefinitionDocument document, string origin, string pluginId);

        public abstract void Bake();
    }

    internal sealed class DefinitionType<T> : DefinitionType
        where T : unmanaged
    {
        private readonly DefinitionRegistry _registry;
        private readonly DefinitionLayout<T> _layout;
        private readonly List<string> _names = [];
        private readonly List<T> _values = [];
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

        public DefinitionType(DefinitionRegistry registry, string name, DefinitionLayout<T> layout, string pluginId)
            : base(name, typeof(T), Unsafe.SizeOf<T>(), AlignmentOf(), pluginId)
        {
            _registry = registry;
            _layout = layout;
            Table = new DefinitionTable<T>(registry, name);
        }

        public DefinitionTable<T> Table { get; }

        public void Add(string name, in T value, string pluginId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_byName.ContainsKey(name))
            {
                throw new PluginException(pluginId, $"Plugin '{pluginId}' adds Definition '{Name}/{name}', which already exists; patch it instead.");
            }

            _byName.Add(name, _names.Count);
            _names.Add(name);
            _values.Add(value);
        }

        public void Patch(string name, DefinitionPatch<T> patch, string pluginId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!_byName.TryGetValue(name, out int index))
            {
                throw new PluginException(pluginId, $"Plugin '{pluginId}' patches Definition '{Name}/{name}', which does not exist.");
            }

            var value = _values[index];
            patch(ref value);
            _values[index] = value;
        }

        public override void Apply(DefinitionDocument document, string origin, string pluginId)
        {
            T value = default;
            bool exists = _byName.TryGetValue(document.Name, out int index);
            if (exists)
            {
                value = _values[index];
            }

            foreach (var field in document.Fields)
            {
                _layout.Set(ref value, field, $"{origin}: '{Name}/{document.Name}'", pluginId);
            }

            if (exists)
            {
                _values[index] = value;
            }
            else
            {
                Add(document.Name, in value, pluginId);
            }
        }

        public override void Bake()
        {
            var names = new StringId[_names.Count];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = _registry.Strings.Intern(_names[i]);
            }

            var indexOfString = new int[_registry.Strings.Count + 1];
            Array.Fill(indexOfString, -1);
            for (int i = 0; i < names.Length; i++)
            {
                indexOfString[names[i].Value] = i;
            }

            var blob = _registry.Blobs.Store<T>(CollectionsMarshal.AsSpan(_values));
            Table.Bake(names, indexOfString, blob);
            _names.Clear();
            _values.Clear();
            _byName.Clear();
        }

        private static int AlignmentOf() => Unsafe.SizeOf<AlignmentProbe>() - Unsafe.SizeOf<T>();

        /// <summary>The offset of the value equals the natural alignment of <typeparamref name="T"/>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct AlignmentProbe
        {
#pragma warning disable CS0169, CA1823, IDE0051 // Fields are read by the layout only.
            private byte _pad;
            private T _value;
#pragma warning restore CS0169, CA1823, IDE0051
        }
    }
}

/// <summary>The per-World handle to the schema's <see cref="DefinitionRegistry"/>; fetch it with <see cref="WorldDefinitionsExtensions.Definitions"/>.</summary>
public sealed class DefinitionSet : IWorldExtension
{
    internal DefinitionSet(DefinitionRegistry registry)
    {
        Registry = registry;
    }

    public DefinitionRegistry Registry { get; }

    /// <summary>The baked table of Definition type <paramref name="typeName"/>; cache it, do not look it up per Tick.</summary>
    public DefinitionTable<T> Table<T>(string typeName)
        where T : unmanaged
        => Registry.Table<T>(typeName);

    void IWorldExtension.BeginTick(long tick)
    {
    }

    void IWorldExtension.EndPhase(Phase phase)
    {
    }

    void IWorldExtension.EndTick(long tick)
    {
    }

    /// <summary>Definitions are schema data, not World state: nothing is hashed here.</summary>
    void IWorldExtension.Hash(ref Hash64State state)
    {
    }

    void IDisposable.Dispose()
    {
    }
}

/// <summary>Access to a World's Definitions.</summary>
public static class WorldDefinitionsExtensions
{
    /// <summary>The Definitions of the World's schema; throws when it was built without a <see cref="PluginHost"/>.</summary>
    public static DefinitionSet Definitions(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Extension<DefinitionSet>();
    }
}
