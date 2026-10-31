using System.Runtime.CompilerServices;

namespace Colony;

/// <summary>
/// Composition root for a World (spec section 11): registers Components and validates them, then
/// <see cref="Build"/> freezes everything into an immutable <see cref="WorldSchema"/> that many Worlds
/// can share. Systems, Fields, Plugins and budgets are registered here by later tickets.
/// </summary>
public sealed class WorldBuilder
{
    /// <summary>Seed for Resource layout hashes ("RSRC"), so they never collide with a World or Component layout hash by construction.</summary>
    private const ulong ResourceLayoutHashSeed = 0x5253_5243UL;

    private readonly List<ComponentDescriptor> _components = [];
    private readonly Dictionary<Type, int> _byClrType = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly List<ResourceDescriptor> _resources = [];
    private readonly Dictionary<Type, int> _resourceByClrType = [];
    private readonly Dictionary<string, int> _resourceByName = new(StringComparer.Ordinal);
    private readonly List<Phase> _phases = [Phase.Streaming, Phase.Input, Phase.Begin, Phase.Simulate, Phase.Late, Phase.End];
    private readonly List<SystemDefinition> _systems = [];
    private readonly HashSet<string> _systemNames = new(StringComparer.Ordinal);
    private readonly List<WorldExtensionDefinition> _extensions = [];
    private readonly List<RelationDescriptor> _relations = [];

    /// <summary>
    /// Registers a World extension (Fields, spatial index, ...). Extensions are frozen at <see cref="Build"/>,
    /// hashed into the schema and created once per World, in registration order.
    /// </summary>
    public WorldBuilder AddExtension(WorldExtensionDefinition extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(extension.Name, nameof(extension));
        if (_extensions.Exists(e => string.Equals(e.Name, extension.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"An extension named '{extension.Name}' is already registered.", nameof(extension));
        }

        _extensions.Add(extension);
        return this;
    }

    public WorldBuilder()
    {
        // Resource 0 in every schema: the queue that structural ops losing a conflict are reported on
        // (spec section 4.1). Persistent, so load-then-continue delivers the same events.
        StructuralFailures = AddResourceCore<StructuralFailure>(StructuralFailureQueueName, ResourceKind.EventQueue, persistent: true);
    }

    /// <summary>Registered name of the built-in <see cref="StructuralFailure"/> queue.</summary>
    public const string StructuralFailureQueueName = "colony.structural-failure";

    /// <summary>The built-in <see cref="StructuralFailure"/> event queue; declare it in <see cref="SystemDefinition.ReadsResources"/> to read it.</summary>
    public ResourceType StructuralFailures { get; }

    /// <summary>
    /// Registers a System (spec section 4.3). Its id is the registration order, which breaks ordering ties.
    /// Access conflicts, ordering cycles and unknown names are reported by <see cref="Build"/>.
    /// </summary>
    public WorldBuilder AddSystem(SystemDefinition system)
    {
        ArgumentNullException.ThrowIfNull(system);
        ThrowIfBuilt();
        ArgumentException.ThrowIfNullOrWhiteSpace(system.Name, nameof(system));
        if (!_systemNames.Add(system.Name))
        {
            throw new ArgumentException($"A System named '{system.Name}' is already registered.", nameof(system));
        }

        if (system.Begin is null && system.ForEachChunk is null && system.End is null)
        {
            throw new ArgumentException($"System '{system.Name}' has no body: set Begin, ForEachChunk or End.", nameof(system));
        }

        _systems.Add(system);
        return this;
    }
    private readonly List<string> _logMessages = [];
    private readonly List<string> _counters = [];
    private readonly HashSet<string> _counterNames = new(StringComparer.Ordinal);
    private bool _debugValidation = WorldSchema.DefaultDebugValidation;
    private bool _built;

    /// <summary>
    /// Registers a structured log message (spec section 10.1). The template uses <c>{0}</c> to <c>{3}</c> for
    /// the numeric arguments a System passes to <see cref="SystemContext.Log"/>; it is formatted only when
    /// the entries are drained.
    /// </summary>
    public Colony.Diagnostics.LogMessageId AddLogMessage(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ThrowIfBuilt();
        _logMessages.Add(template);
        return new Colony.Diagnostics.LogMessageId(_logMessages.Count - 1);
    }

    /// <summary>
    /// Registers a named diagnostics counter that appears in every Tick record (spec section 10.1). Plugins
    /// register theirs here from <c>Register</c>; registration closes at <see cref="Build"/> so the record
    /// layout is fixed.
    /// </summary>
    public Colony.Diagnostics.CounterId AddCounter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        if (!_counterNames.Add(name))
        {
            throw new ArgumentException($"A counter named '{name}' is already registered.", nameof(name));
        }

        _counters.Add(name);
        return new Colony.Diagnostics.CounterId(_counters.Count - 1);
    }

    /// <summary>
    /// Adds a Game Phase directly after the named Phase <paramref name="after"/> (and after any Game Phase
    /// already added there), so it runs before the next named Phase. Game Phases go only between named
    /// ones (spec section 4.3); nothing can follow <see cref="Phase.End"/>.
    /// </summary>
    public Phase AddPhase(string name, Phase after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        if (!after.IsNamed)
        {
            throw new ArgumentException($"Game Phases are inserted relative to a named Phase, not to '{after.Name}'.", nameof(after));
        }

        if (after == Phase.End)
        {
            throw new ArgumentException("No Phase can follow End.", nameof(after));
        }

        if (Phase.IsNamedName(name) || _phases.Exists(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A Phase named '{name}' already exists.", nameof(name));
        }

        var phase = new Phase(_phases.Count, name);
        int index = _phases.IndexOf(after) + 1;
        while (!_phases[index].IsNamed)
        {
            index++;
        }

        _phases.Insert(index, phase);
        return phase;
    }

    /// <summary>
    /// Registers an unmanaged Component under a stable <paramref name="name"/>. The layout hash is
    /// computed from the name, size and alignment; the CLR type is not part of the identity.
    /// </summary>
    public WorldBuilder AddComponent<T>(string name, ComponentOptions flags = ComponentOptions.None) where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return AddComponent<T>(name, flags, System.Collections.Immutable.ImmutableArray<ComponentField>.Empty);
    }

    /// <summary>
    /// Registers a source-generated Component (spec section 11) under its layout's stable name. The layout
    /// hash covers the name, size, alignment and every field's name, type, offset and size (spec section 4.1).
    /// </summary>
    public WorldBuilder AddComponent<T>() where T : unmanaged, IComponent<T>
    {
        var layout = T.Layout;
        ArgumentNullException.ThrowIfNull(layout, nameof(T));
        layout.Validate(Unsafe.SizeOf<T>());
        return AddComponent<T>(layout.Name, layout.Flags, layout.Fields);
    }

    /// <summary>The Phase named <paramref name="name"/>: one of the six named Phases or a Game Phase already added.</summary>
    public Phase PhaseNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var phase in _phases)
        {
            if (string.Equals(phase.Name, name, StringComparison.Ordinal))
            {
                return phase;
            }
        }

        throw new ArgumentException($"No Phase named '{name}' exists; add Game Phases with AddPhase before the Systems that use them.", nameof(name));
    }

    private WorldBuilder AddComponent<T>(string name, ComponentOptions flags, System.Collections.Immutable.ImmutableArray<ComponentField> fields) where T : unmanaged
    {
        if ((flags & (ComponentOptions.Relation | ComponentOptions.Buffer)) != 0)
        {
            throw new ArgumentException($"Component '{name}': the Relation and Buffer flags are set by AddRelation and AddBufferComponent, not by AddComponent.", nameof(flags));
        }

        ThrowIfBuilt();
        if (_byClrType.ContainsKey(typeof(T)))
        {
            throw new ArgumentException($"The type {typeof(T).Name} is already registered as a Component.", nameof(T));
        }

        int size = Unsafe.SizeOf<T>();
        int alignment = Unsafe.SizeOf<AlignmentProbe<T>>() - size;
        ulong hash = ComponentLayout.ComputeHash(name, size, alignment, fields);
        int id = AddComponentCore(name, size, alignment, hash, flags, fields, elementSize: 0, inlineCapacity: 0);
        _byClrType.Add(typeof(T), id);
        return this;
    }

    /// <summary>Seed mixed into a buffer Component's layout hash ("BUFR").</summary>
    private const ulong BufferLayoutSeed = 0x4255_4652UL;

    /// <summary>
    /// Registers a buffer Component (spec section 4.1): a <see cref="BufferComponent{TElement, TInline}"/>
    /// holding <typeparamref name="TElement"/>s inline in <typeparamref name="TInline"/> (its size must be a
    /// positive multiple of the element size; <see cref="Inline4{T}"/> .. <see cref="Inline32{T}"/> are
    /// provided) and overflowing to the World's size-class allocator. Access it with
    /// <see cref="World.GetBuffer{TElement}"/> or <see cref="ChunkView.Buffer{TElement}"/>; overflow counts
    /// are in <see cref="World.BufferCounters"/>.
    /// </summary>
    public WorldBuilder AddBufferComponent<TElement, TInline>(string name, ComponentOptions flags = ComponentOptions.None)
        where TElement : unmanaged
        where TInline : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if ((flags & (ComponentOptions.Relation | ComponentOptions.Buffer)) != 0)
        {
            throw new ArgumentException($"Component '{name}': the Relation and Buffer flags are set by AddRelation and AddBufferComponent, not passed in.", nameof(flags));
        }

        ThrowIfBuilt();
        int elementSize = Unsafe.SizeOf<TElement>();
        int inlineBytes = Unsafe.SizeOf<TInline>();
        if (inlineBytes % elementSize != 0)
        {
            throw new ArgumentException($"Buffer Component '{name}': {typeof(TInline).Name} is {inlineBytes} bytes, not a multiple of the {elementSize} byte element.", nameof(TInline));
        }

        if (_byClrType.ContainsKey(typeof(BufferComponent<TElement, TInline>)))
        {
            throw new ArgumentException($"The type BufferComponent<{typeof(TElement).Name}, {typeof(TInline).Name}> is already registered as a Component.", nameof(TInline));
        }

        int size = Unsafe.SizeOf<BufferComponent<TElement, TInline>>();
        int alignment = Unsafe.SizeOf<AlignmentProbe<BufferComponent<TElement, TInline>>>() - size;
        int inlineCapacity = inlineBytes / elementSize;
        ulong hash = ComponentLayout.ComputeHash(name, size, alignment, System.Collections.Immutable.ImmutableArray<ComponentField>.Empty);
        hash = Hashing.Hash64.Combine(hash, BufferLayoutSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)elementSize);
        hash = Hashing.Hash64.Combine(hash, (ulong)inlineCapacity);
        int id = AddComponentCore(name, size, alignment, hash, flags | ComponentOptions.Buffer, System.Collections.Immutable.ImmutableArray<ComponentField>.Empty, elementSize, inlineCapacity, BufferComponent<TElement, TInline>.InlineOffset);
        _byClrType.Add(typeof(BufferComponent<TElement, TInline>), id);
        return this;
    }

    /// <summary>Registers a Component by layout alone (no CLR type binding); returns its id.</summary>
    private int AddComponentCore(string name, int size, int alignment, ulong layoutHash, ComponentOptions flags, System.Collections.Immutable.ImmutableArray<ComponentField> fields, int elementSize, int inlineCapacity, int inlineOffset = 0)
    {
        ThrowIfBuilt();
        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"A Component named '{name}' is already registered.", nameof(name));
        }

        if (size > Archetype.MaxComponentSize)
        {
            throw new ArgumentException(
                $"Component '{name}' is {size} bytes; a Component must fit {Archetype.MinCapacity} rows in a {SlabAllocator.ChunkSize} byte chunk ({Archetype.MaxComponentSize} bytes at most).",
                nameof(name));
        }

        int id = _components.Count;
        if (id >= ComponentMask.Capacity)
        {
            throw new InvalidOperationException($"A schema may register at most {ComponentMask.Capacity} Components.");
        }

        // Layout identity (spec section 4.1): the registered name, size, alignment and fields, never the CLR type.
        _components.Add(new ComponentDescriptor(new ComponentType(id), name, size, alignment, layoutHash, flags)
        {
            Fields = fields,
            ElementSize = elementSize,
            InlineCapacity = inlineCapacity,
            InlineOffset = inlineOffset,
        });
        _byName.Add(name, id);
        return id;
    }

    /// <summary>Seed mixed into a Relation's layout hash ("RELN"), so a Relation and a plain 8-byte Component of the same name differ.</summary>
    private const ulong RelationLayoutSeed = 0x5245_4C4EUL;

    /// <summary>Registered name of the built-in parent/child Relation (<see cref="AddHierarchy"/>).</summary>
    public const string HierarchyRelationName = "colony.parent";

    /// <summary>
    /// Registers a Relation (spec section 4.1): a named link from a source Entity to one target Entity,
    /// stored as a non-fragmenting Component named <paramref name="name"/> and indexed in reverse by the
    /// World. Set it with <see cref="CommandBuffer.Relate"/>; read it with <see cref="World.TargetOf"/> and
    /// <see cref="World.SourcesOf"/>. With <see cref="RelationOptions.CascadeDelete"/>, destroying a target
    /// destroys its sources too.
    /// </summary>
    public RelationType AddRelation(string name, RelationOptions options = RelationOptions.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        int size = Unsafe.SizeOf<RelationLink>();
        int alignment = Unsafe.SizeOf<AlignmentProbe<RelationLink>>() - size;
        ulong hash = ComponentLayout.ComputeHash(name, size, alignment, System.Collections.Immutable.ImmutableArray<ComponentField>.Empty);
        hash = Hashing.Hash64.Combine(hash, RelationLayoutSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)options);
        int componentId = AddComponentCore(name, size, alignment, hash, ComponentOptions.NonFragmenting | ComponentOptions.Relation, System.Collections.Immutable.ImmutableArray<ComponentField>.Empty, 0, 0);
        var type = new RelationType(_relations.Count, new ComponentType(componentId));
        _relations.Add(new RelationDescriptor(type, name, options));
        return type;
    }

    /// <summary>
    /// Registers the built-in parent/child hierarchy (spec section 4.1): the Relation named
    /// <see cref="HierarchyRelationName"/> from child to parent, with cascade delete by default. Use
    /// <see cref="CommandBuffer.SetParent"/>, <see cref="World.ParentOf"/> and <see cref="World.ChildrenOf"/>.
    /// </summary>
    public RelationType AddHierarchy(bool cascadeDelete = true)
        => AddRelation(HierarchyRelationName, cascadeDelete ? RelationOptions.CascadeDelete : RelationOptions.None);

    /// <summary>The handle for the already-registered Relation named <paramref name="name"/>.</summary>
    public RelationType Relation(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var relation in _relations)
        {
            if (string.Equals(relation.Name, name, StringComparison.Ordinal))
            {
                return relation.Type;
            }
        }

        throw new ArgumentException($"No Relation named '{name}' is registered; call AddRelation first.", nameof(name));
    }

    /// <summary>The handle for the already-registered Component of CLR type <typeparamref name="T"/>, for use in System definitions before <see cref="Build"/>.</summary>
    public ComponentType Component<T>() where T : unmanaged
    {
        if (!_byClrType.TryGetValue(typeof(T), out int id))
        {
            throw new ArgumentException($"{typeof(T).Name} is not registered as a Component; call AddComponent first.", nameof(T));
        }

        return new ComponentType(id);
    }

    /// <summary>The handle for the already-registered Component named <paramref name="name"/>.</summary>
    public ComponentType Component(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_byName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Component named '{name}' is registered; call AddComponent first.", nameof(name));
        }

        return new ComponentType(id);
    }

    /// <summary>
    /// Registers a World-owned singleton Resource (spec section 4.1): one zero-initialised value of
    /// <typeparamref name="T"/> per World, saved and hashed with it unless <see cref="ResourceOptions.Transient"/>.
    /// Systems declare access with <see cref="SystemDefinition.ReadsResources"/> and <see cref="SystemDefinition.WritesResources"/>.
    /// </summary>
    public WorldBuilder AddResource<T>(string name, ResourceOptions options = ResourceOptions.None) where T : unmanaged
    {
        AddResourceCore<T>(name, ResourceKind.Value, persistent: (options & ResourceOptions.Transient) == 0);
        return this;
    }

    /// <summary>
    /// Registers a typed event queue (spec section 4.3): a Resource holding pending <typeparamref name="T"/>
    /// events. Systems emit through their command buffer (<see cref="CommandBuffer.Emit{T}"/>), so events
    /// merge at the Phase boundary in System, job then record order and are readable from the next Phase.
    /// Every queue is cleared at the End Phase boundary, before the End Phase's own buffers merge; events
    /// emitted in End are therefore delivered through the whole next Tick. Only
    /// <see cref="EventOptions.Persistent"/> queues are saved and hashed. There are no callbacks.
    /// </summary>
    public WorldBuilder AddEvent<T>(string name, EventOptions options = EventOptions.None) where T : unmanaged
    {
        AddResourceCore<T>(name, ResourceKind.EventQueue, persistent: (options & EventOptions.Persistent) != 0);
        return this;
    }

    /// <summary>The handle for the already-registered Resource of CLR type <typeparamref name="T"/>.</summary>
    public ResourceType Resource<T>() where T : unmanaged
    {
        if (!_resourceByClrType.TryGetValue(typeof(T), out int id))
        {
            throw new ArgumentException($"{typeof(T).Name} is not registered as a Resource; call AddResource or AddEvent first.", nameof(T));
        }

        return new ResourceType(id);
    }

    /// <summary>The handle for the already-registered Resource named <paramref name="name"/>.</summary>
    public ResourceType Resource(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_resourceByName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Resource named '{name}' is registered; call AddResource or AddEvent first.", nameof(name));
        }

        return new ResourceType(id);
    }

    /// <summary>Seed for Input Command layout hashes ("INPT").</summary>
    private const ulong InputCommandLayoutHashSeed = 0x494E_5054UL;

    /// <summary>Registered name of the built-in <see cref="CommandRejected"/> queue, created with the first Input Command.</summary>
    public const string CommandRejectedQueueName = "colony.command-rejected";

    private readonly List<InputCommandDescriptor> _inputCommands = [];
    private readonly Dictionary<Type, int> _inputByClrType = [];
    private readonly Dictionary<string, int> _inputByName = new(StringComparer.Ordinal);
    private ResourceType? _commandRejections;

    /// <summary>
    /// Registers an Input Command (spec section 7.3): a typed unmanaged struct submitted through
    /// <see cref="WorldInputs.Submit{T}(InputSource, in T, long)"/> and applied by handler Systems in the Input Phase
    /// through <see cref="SystemContext.Inputs{T}"/>. The layout hash (name, size, alignment) is part of the schema
    /// hash and of every replay log header. The first registration also registers the <see cref="CommandRejections"/> queue.
    /// </summary>
    public InputCommandType AddInputCommand<T>(string name) where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        if (_inputByName.ContainsKey(name))
        {
            throw new ArgumentException($"An Input Command named '{name}' is already registered.", nameof(name));
        }

        if (_inputByClrType.ContainsKey(typeof(T)))
        {
            throw new ArgumentException($"The type {typeof(T).Name} is already registered as an Input Command.", nameof(T));
        }

        _commandRejections ??= AddResourceCore<CommandRejected>(CommandRejectedQueueName, ResourceKind.EventQueue, persistent: false);
        int size = Unsafe.SizeOf<T>();
        int alignment = Unsafe.SizeOf<AlignmentProbe<T>>() - size;
        ulong hash = Hashing.Hash64.Compute(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()), InputCommandLayoutHashSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)size);
        hash = Hashing.Hash64.Combine(hash, (ulong)alignment);
        var type = new InputCommandType(_inputCommands.Count);
        _inputCommands.Add(new InputCommandDescriptor(type, name, size, alignment, hash));
        _inputByName.Add(name, type.Id);
        _inputByClrType.Add(typeof(T), type.Id);
        return type;
    }

    /// <summary>The handle for the already-registered Input Command of CLR type <typeparamref name="T"/>.</summary>
    public InputCommandType InputCommand<T>() where T : unmanaged
    {
        if (!_inputByClrType.TryGetValue(typeof(T), out int id))
        {
            throw new ArgumentException($"{typeof(T).Name} is not registered as an Input Command; call AddInputCommand first.", nameof(T));
        }

        return new InputCommandType(id);
    }

    /// <summary>The handle for the already-registered Input Command named <paramref name="name"/>.</summary>
    public InputCommandType InputCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_inputByName.TryGetValue(name, out int id))
        {
            throw new ArgumentException($"No Input Command named '{name}' is registered; call AddInputCommand first.", nameof(name));
        }

        return new InputCommandType(id);
    }

    /// <summary>The built-in <see cref="CommandRejected"/> queue; exists once an Input Command is registered. Declare it in <see cref="SystemDefinition.ReadsResources"/> to read rejections.</summary>
    public ResourceType CommandRejections => _commandRejections ?? throw new InvalidOperationException("No Input Command is registered; the CommandRejected queue is created by the first AddInputCommand.");

    private ResourceType AddResourceCore<T>(string name, ResourceKind kind, bool persistent) where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        if (_resourceByName.ContainsKey(name))
        {
            throw new ArgumentException($"A Resource named '{name}' is already registered.", nameof(name));
        }

        if (_resourceByClrType.ContainsKey(typeof(T)))
        {
            throw new ArgumentException($"The type {typeof(T).Name} is already registered as a Resource.", nameof(T));
        }

        int id = _resources.Count;
        if (id >= ResourceMask.Capacity)
        {
            throw new InvalidOperationException($"A schema may register at most {ResourceMask.Capacity} Resources.");
        }

        int size = Unsafe.SizeOf<T>();
        int alignment = Unsafe.SizeOf<AlignmentProbe<T>>() - size;
        ulong hash = Hashing.Hash64.Compute(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()), ResourceLayoutHashSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)size);
        hash = Hashing.Hash64.Combine(hash, (ulong)alignment);
        hash = Hashing.Hash64.Combine(hash, (ulong)kind);
        var type = new ResourceType(id);
        _resources.Add(new ResourceDescriptor(type, name, size, alignment, hash, kind, persistent));
        _resourceByName.Add(name, id);
        _resourceByClrType.Add(typeof(T), id);
        return type;
    }

    /// <summary>
    /// Turns debug validation on or off for Worlds of this schema: structural-version checks on chunk
    /// views and refs, poisoning of freed memory and a leak report on dispose (spec section 4.2). The
    /// default follows the Framework build configuration.
    /// </summary>
    public WorldBuilder DebugValidation(bool enabled)
    {
        ThrowIfBuilt();
        _debugValidation = enabled;
        return this;
    }

    /// <summary>Validates the registrations and freezes them into a schema. The builder cannot be reused.</summary>
    public WorldSchema Build()
    {
        ThrowIfBuilt();
        _built = true;
        var schedule = SystemSchedule.Build(_systems, _phases, _components.Count, _resources);
        foreach (var extension in _extensions)
        {
            extension.Freeze();
        }

        return new WorldSchema(_components, _byClrType, _byName, _resources, _resourceByClrType, _resourceByName, _phases, schedule, _extensions, _relations, _debugValidation, _logMessages, _counters, _inputCommands, _inputByClrType, _inputByName, _commandRejections);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("The WorldBuilder has already been built; registration is closed.");
        }
    }

    /// <summary>The offset of <c>Value</c> equals the natural alignment of <typeparamref name="T"/>.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AlignmentProbe<T> where T : unmanaged
    {
#pragma warning disable CS0169, CA1823, IDE0051 // Fields are read by the layout only.
        private byte _pad;
        private T _value;
#pragma warning restore CS0169, CA1823, IDE0051
    }
}
