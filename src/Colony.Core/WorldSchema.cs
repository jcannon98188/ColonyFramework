namespace Colony;

/// <summary>
/// Immutable result of <see cref="WorldBuilder.Build"/>: the registered Components in id order, their
/// layouts and the schema hash. Shared by every World created from it.
/// </summary>
public sealed class WorldSchema
{
    private readonly ComponentDescriptor[] _components;
    private readonly Dictionary<Type, int> _byClrType;
    private readonly Dictionary<string, int> _byName;

    internal WorldSchema(
        List<ComponentDescriptor> components,
        Dictionary<Type, int> byClrType,
        Dictionary<string, int> byName,
        bool debugValidation)
    {
        _components = [.. components];
        _byClrType = byClrType;
        _byName = byName;
        DebugValidation = debugValidation;

        var sizes = new int[_components.Length];
        var slots = new int[_components.Length];
        int nonFragmenting = 0;
        ulong hash = Fnv1a.Offset;
        for (int i = 0; i < _components.Length; i++)
        {
            sizes[i] = _components[i].Size;
            slots[i] = _components[i].IsNonFragmenting ? nonFragmenting++ : -1;
            hash = Fnv1a.Hash(_components[i].LayoutHash, hash);
        }

        Sizes = sizes;
        NonFragmentingSlots = slots;
        NonFragmentingCount = nonFragmenting;
        SchemaHash = hash;
    }

    /// <summary>Default for <see cref="WorldBuilder.DebugValidation"/>: on in Debug builds of the Framework, off in Release.</summary>
    public static bool DefaultDebugValidation =>
#if DEBUG
        true;
#else
        false;
#endif

    public int ComponentCount => _components.Length;

    /// <summary>Hash over every Component's layout hash in id order; two schemas with equal hashes store Components identically.</summary>
    public ulong SchemaHash { get; }

    public bool DebugValidation { get; }

    /// <summary>Component sizes by id.</summary>
    internal int[] Sizes { get; }

    /// <summary>Per id: slot in the non-fragmenting store, or -1 for archetype-stored Components.</summary>
    internal int[] NonFragmentingSlots { get; }

    internal int NonFragmentingCount { get; }

    /// <summary>The handle for the Component registered with CLR type <typeparamref name="T"/>.</summary>
    public ComponentType Component<T>() where T : unmanaged
    {
        if (!_byClrType.TryGetValue(typeof(T), out int id))
        {
            throw new ArgumentException($"{typeof(T).Name} is not registered as a Component in this schema.", nameof(T));
        }

        return new ComponentType(id);
    }

    /// <summary>The handle for the Component registered under <paramref name="name"/>.</summary>
    public ComponentType Component(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_byName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Component named '{name}' is registered in this schema.", nameof(name));
        }

        return new ComponentType(id);
    }

    public bool TryGetComponent(string name, out ComponentType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool found = _byName.TryGetValue(name, out int id);
        type = new ComponentType(id);
        return found;
    }

    public ComponentDescriptor Describe(ComponentType type)
    {
        ValidateType(type);
        return _components[type.Id];
    }

    public ReadOnlySpan<ComponentDescriptor> Components => _components;

    /// <summary>Creates a World with default options.</summary>
    public World CreateWorld() => new(this, WorldOptions.Default);

    public World CreateWorld(WorldOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new World(this, options);
    }

    internal void ValidateType(ComponentType type)
    {
        if ((uint)type.Id >= (uint)_components.Length)
        {
            throw new ArgumentException($"{type} does not belong to this schema.", nameof(type));
        }
    }

    internal bool IsNonFragmenting(int id) => NonFragmentingSlots[id] >= 0;
}
