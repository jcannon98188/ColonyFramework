namespace Colony;

/// <summary>
/// Immutable result of <see cref="WorldBuilder.Build"/>: the registered Components in id order, their
/// layouts and the schema hash. Shared by every World created from it.
/// </summary>
public sealed class WorldSchema
{
    /// <summary>Seed for the schema hash ("SCHM").</summary>
    private const ulong SchemaHashSeed = 0x5343_484DUL;

    private readonly ComponentDescriptor[] _components;
    private readonly Dictionary<Type, int> _byClrType;
    private readonly Dictionary<string, int> _byName;
    private readonly ResourceDescriptor[] _resources;
    private readonly Dictionary<Type, int> _resourceByClrType;
    private readonly Dictionary<string, int> _resourceByName;
    private readonly Phase[] _phases;

    internal WorldSchema(
        List<ComponentDescriptor> components,
        Dictionary<Type, int> byClrType,
        Dictionary<string, int> byName,
        List<ResourceDescriptor> resources,
        Dictionary<Type, int> resourceByClrType,
        Dictionary<string, int> resourceByName,
        List<Phase> phases,
        SystemSchedule schedule,
        bool debugValidation)
    {
        _components = [.. components];
        _byClrType = byClrType;
        _byName = byName;
        _resources = [.. resources];
        _resourceByClrType = resourceByClrType;
        _resourceByName = resourceByName;
        _phases = [.. phases];
        Schedule = schedule;
        DebugValidation = debugValidation;

        var sizes = new int[_components.Length];
        var slots = new int[_components.Length];
        int nonFragmenting = 0;
        ulong hash = Hashing.Hash64.Compute(ReadOnlySpan<byte>.Empty, SchemaHashSeed);
        for (int i = 0; i < _components.Length; i++)
        {
            sizes[i] = _components[i].Size;
            slots[i] = _components[i].IsNonFragmenting ? nonFragmenting++ : -1;
            hash = Hashing.Hash64.Combine(hash, _components[i].LayoutHash);
        }

        var resourceSizes = new int[_resources.Length];
        for (int i = 0; i < _resources.Length; i++)
        {
            resourceSizes[i] = _resources[i].Size;
            hash = Hashing.Hash64.Combine(hash, _resources[i].LayoutHash);
            hash = Hashing.Hash64.Combine(hash, _resources[i].IsPersistent ? 1UL : 0UL);
        }

        Sizes = sizes;
        ResourceSizes = resourceSizes;
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

    public int ResourceCount => _resources.Length;

    /// <summary>Hash over every Component's and Resource's layout hash in id order; two schemas with equal hashes store state identically.</summary>
    public ulong SchemaHash { get; }

    public bool DebugValidation { get; }

    /// <summary>Component sizes by id.</summary>
    internal int[] Sizes { get; }

    /// <summary>Resource value (or event element) sizes by id.</summary>
    internal int[] ResourceSizes { get; }

    /// <summary>Per id: slot in the non-fragmenting store, or -1 for archetype-stored Components.</summary>
    internal int[] NonFragmentingSlots { get; }

    internal int NonFragmentingCount { get; }

    internal SystemSchedule Schedule { get; }

    /// <summary>Every registered System by id with the Phase, order and wave the scheduler gave it.</summary>
    public ReadOnlySpan<SystemDescriptor> Systems => Schedule.Descriptors;

    public SystemDescriptor System(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (ref readonly var descriptor in Systems)
        {
            if (string.Equals(descriptor.Name, name, StringComparison.Ordinal))
            {
                return descriptor;
            }
        }

        throw new ArgumentException($"No System named '{name}' is registered in this schema.", nameof(name));
    }

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

    /// <summary>Every registered Resource (values and event queues) in id order; id 0 is always <see cref="StructuralFailures"/>.</summary>
    public ReadOnlySpan<ResourceDescriptor> Resources => _resources;

    /// <summary>The built-in queue of <see cref="StructuralFailure"/> events (spec section 4.1).</summary>
    public ResourceType StructuralFailures => _resources[0].Type;

    /// <summary>The handle for the Resource registered with CLR type <typeparamref name="T"/>.</summary>
    public ResourceType Resource<T>() where T : unmanaged
    {
        if (!_resourceByClrType.TryGetValue(typeof(T), out int id))
        {
            throw new ArgumentException($"{typeof(T).Name} is not registered as a Resource in this schema.", nameof(T));
        }

        return new ResourceType(id);
    }

    /// <summary>The handle for the Resource registered under <paramref name="name"/>.</summary>
    public ResourceType Resource(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_resourceByName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Resource named '{name}' is registered in this schema.", nameof(name));
        }

        return new ResourceType(id);
    }

    public bool TryGetResource(string name, out ResourceType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool found = _resourceByName.TryGetValue(name, out int id);
        type = new ResourceType(id);
        return found;
    }

    public ResourceDescriptor DescribeResource(ResourceType type)
    {
        ValidateResource(type);
        return _resources[type.Id];
    }

    /// <summary>Every Phase in the order a Tick runs them: the six named Phases with Game Phases between them.</summary>
    public ReadOnlySpan<Phase> Phases => _phases;

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

    internal void ValidateResource(ResourceType type)
    {
        if ((uint)type.Id >= (uint)_resources.Length)
        {
            throw new ArgumentException($"{type} does not belong to this schema.", nameof(type));
        }
    }

    /// <summary>Throws unless <typeparamref name="T"/> has the registered size of the Resource.</summary>
    internal void ValidateResource<T>(ResourceType type, ResourceKind kind) where T : unmanaged
    {
        ValidateResource(type);
        var descriptor = _resources[type.Id];
        if (descriptor.Kind != kind)
        {
            throw new ArgumentException($"Resource '{descriptor.Name}' is {(descriptor.IsEventQueue ? "an event queue" : "a value")}, not {(kind == ResourceKind.EventQueue ? "an event queue" : "a value")}.", nameof(type));
        }

        int size = global::System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (size != descriptor.Size)
        {
            throw new ArgumentException($"{typeof(T).Name} is {size} bytes but Resource '{descriptor.Name}' is {descriptor.Size} bytes.", nameof(T));
        }
    }

    internal bool IsNonFragmenting(int id) => NonFragmentingSlots[id] >= 0;
}
