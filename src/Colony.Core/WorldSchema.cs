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
    private readonly WorldExtensionDefinition[] _extensions;
    private readonly RelationDescriptor[] _relations;
    private readonly string[] _logMessages;
    private readonly string[] _counters;

    internal WorldSchema(
        List<ComponentDescriptor> components,
        Dictionary<Type, int> byClrType,
        Dictionary<string, int> byName,
        List<ResourceDescriptor> resources,
        Dictionary<Type, int> resourceByClrType,
        Dictionary<string, int> resourceByName,
        List<Phase> phases,
        SystemSchedule schedule,
        List<WorldExtensionDefinition> extensions,
        List<RelationDescriptor> relations,
        bool debugValidation,
        List<string> logMessages,
        List<string> counters,
        List<InputCommandDescriptor> inputCommands,
        Dictionary<Type, int> inputByClrType,
        Dictionary<string, int> inputByName,
        ResourceType? commandRejections,
        List<Colony.Diagnostics.InspectorDescriptor> inspectors)
    {
        _inspectors = [.. inspectors];
        _inputCommands = [.. inputCommands];
        _inputByClrType = inputByClrType;
        _inputByName = inputByName;
        _commandRejections = commandRejections;
        _components = [.. components];
        _byClrType = byClrType;
        _byName = byName;
        _resources = [.. resources];
        _resourceByClrType = resourceByClrType;
        _resourceByName = resourceByName;
        _phases = [.. phases];
        _extensions = [.. extensions];
        _relations = [.. relations];
        _logMessages = [.. logMessages];
        _counters = [.. counters];
        Schedule = schedule;
        DebugValidation = debugValidation;

        var sizes = new int[_components.Length];
        var slots = new int[_components.Length];
        var relationOf = new int[_components.Length];
        Array.Fill(relationOf, -1);
        foreach (var relation in _relations)
        {
            relationOf[relation.Component.Id] = relation.Type.Id;
        }

        int nonFragmenting = 0;
        bool anyBuffer = false;
        ulong hash = Hashing.Hash64.Compute(ReadOnlySpan<byte>.Empty, SchemaHashSeed);
        for (int i = 0; i < _components.Length; i++)
        {
            sizes[i] = _components[i].Size;
            slots[i] = _components[i].IsNonFragmenting ? nonFragmenting++ : -1;
            anyBuffer |= _components[i].IsBuffer;
            hash = Hashing.Hash64.Combine(hash, _components[i].LayoutHash);
        }

        RelationOfComponent = relationOf;
        HasBufferComponents = anyBuffer;

        var resourceSizes = new int[_resources.Length];
        for (int i = 0; i < _resources.Length; i++)
        {
            resourceSizes[i] = _resources[i].Size;
            hash = Hashing.Hash64.Combine(hash, _resources[i].LayoutHash);
            hash = Hashing.Hash64.Combine(hash, _resources[i].IsPersistent ? 1UL : 0UL);
        }

        foreach (var extension in _extensions)
        {
            hash = Hashing.Hash64.Combine(hash, extension.LayoutHash);
        }

        // Input Commands (spec section 7.3) follow the extensions, so a schema without any hashes as before.
        foreach (var input in _inputCommands)
        {
            hash = Hashing.Hash64.Combine(hash, input.LayoutHash);
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

    /// <summary>Per Component id: the Relation it stores, or -1.</summary>
    internal int[] RelationOfComponent { get; }

    /// <summary>True when any Component is a buffer Component (the structural paths then free overflow blocks).</summary>
    internal bool HasBufferComponents { get; }

    public int RelationCount => _relations.Length;

    /// <summary>Every registered Relation in id order.</summary>
    public ReadOnlySpan<RelationDescriptor> Relations => _relations;

    /// <summary>The Relation registered under <paramref name="name"/>.</summary>
    public RelationDescriptor Relation(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!TryGetRelation(name, out var type))
        {
            throw new ArgumentException($"No Relation named '{name}' is registered in this schema.", nameof(name));
        }

        return _relations[type.Id];
    }

    public bool TryGetRelation(string name, out RelationType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (ref readonly var relation in Relations)
        {
            if (string.Equals(relation.Name, name, StringComparison.Ordinal))
            {
                type = relation.Type;
                return true;
            }
        }

        type = default;
        return false;
    }

    public RelationDescriptor DescribeRelation(RelationType type)
    {
        ValidateRelation(type);
        return _relations[type.Id];
    }

    /// <summary>True when <see cref="WorldBuilder.AddHierarchy"/> registered the parent/child Relation.</summary>
    public bool HasHierarchy => TryGetRelation(WorldBuilder.HierarchyRelationName, out _);

    /// <summary>The built-in parent/child Relation (<see cref="WorldBuilder.AddHierarchy"/>); throws when the schema has none.</summary>
    public RelationType Hierarchy => TryGetRelation(WorldBuilder.HierarchyRelationName, out var type)
        ? type
        : throw new InvalidOperationException("The schema has no hierarchy; call WorldBuilder.AddHierarchy.");

    internal void ValidateRelation(RelationType type)
    {
        if ((uint)type.Id >= (uint)_relations.Length || _relations[type.Id].Component != type.Component)
        {
            throw new ArgumentException($"{type} does not belong to this schema.", nameof(type));
        }
    }

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

    private readonly InputCommandDescriptor[] _inputCommands;
    private readonly Dictionary<Type, int> _inputByClrType;
    private readonly Dictionary<string, int> _inputByName;
    private readonly ResourceType? _commandRejections;

    /// <summary>Every registered Input Command (spec section 7.3) in id order; their layout hashes are part of <see cref="SchemaHash"/>.</summary>
    public ReadOnlySpan<InputCommandDescriptor> InputCommands => _inputCommands;

    public int InputCommandCount => _inputCommands.Length;

    public bool HasInputCommands => _inputCommands.Length > 0;

    /// <summary>The built-in <see cref="CommandRejected"/> event queue (<see cref="WorldBuilder.CommandRejections"/>); throws when the schema has no Input Commands.</summary>
    public ResourceType CommandRejections => _commandRejections ?? throw new InvalidOperationException("The schema has no Input Commands, so no CommandRejected queue; register one with WorldBuilder.AddInputCommand.");

    /// <summary>The handle for the Input Command registered with CLR type <typeparamref name="T"/>.</summary>
    public InputCommandType InputCommand<T>() where T : unmanaged
    {
        if (!_inputByClrType.TryGetValue(typeof(T), out int id))
        {
            throw new ArgumentException($"{typeof(T).Name} is not registered as an Input Command in this schema.", nameof(T));
        }

        return new InputCommandType(id);
    }

    /// <summary>The handle for the Input Command registered under <paramref name="name"/>.</summary>
    public InputCommandType InputCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_inputByName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Input Command named '{name}' is registered in this schema.", nameof(name));
        }

        return new InputCommandType(id);
    }

    public bool TryGetInputCommand(string name, out InputCommandType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool found = _inputByName.TryGetValue(name, out int id);
        type = new InputCommandType(id);
        return found;
    }

    public InputCommandDescriptor DescribeInputCommand(InputCommandType type)
    {
        if ((uint)type.Id >= (uint)_inputCommands.Length)
        {
            throw new ArgumentException($"{type} does not belong to this schema.", nameof(type));
        }

        return _inputCommands[type.Id];
    }

    private readonly Colony.Diagnostics.InspectorDescriptor[] _inspectors;

    /// <summary>Registered inspectors (spec section 10.2) in registration order; not part of <see cref="SchemaHash"/>.</summary>
    public ReadOnlySpan<Colony.Diagnostics.InspectorDescriptor> Inspectors => _inspectors;

    /// <summary>The inspector registered under <paramref name="name"/>.</summary>
    public Colony.Diagnostics.InspectorDescriptor Inspector(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var inspector in _inspectors)
        {
            if (string.Equals(inspector.Name, name, StringComparison.Ordinal))
            {
                return inspector;
            }
        }

        throw new ArgumentException($"No inspector named '{name}' is registered in this schema.", nameof(name));
    }

    /// <summary>Every Phase in the order a Tick runs them: the six named Phases with Game Phases between them.</summary>
    public ReadOnlySpan<Phase> Phases => _phases;

    /// <summary>Registered World extensions in registration order (their layout hashes are part of <see cref="SchemaHash"/>).</summary>
    public ReadOnlySpan<WorldExtensionDefinition> Extensions => _extensions;

    /// <summary>Log message templates by <see cref="Colony.Diagnostics.LogMessageId"/> (spec section 10.1).</summary>
    public ReadOnlySpan<string> LogMessages => _logMessages;

    /// <summary>Registered counter names by <see cref="Colony.Diagnostics.CounterId"/>.</summary>
    public ReadOnlySpan<string> Counters => _counters;

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
