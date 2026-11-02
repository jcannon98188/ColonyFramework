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
        if (WorldHashTree.IsReservedGroup(extension.Name))
        {
            throw new ArgumentException($"'{extension.Name}' is a hash tree group name Core reserves ({string.Join(", ", WorldHashTree.ReservedGroups)}); an extension's name is its hash tree group.", nameof(extension));
        }

        if (_extensions.Exists(e => string.Equals(e.Name, extension.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"An extension named '{extension.Name}' is already registered.", nameof(extension));
        }

        _extensions.Add(extension);
        return this;
    }

    /// <summary>
    /// The first registered extension of type <typeparamref name="T"/>, or null: lets a Plugin find an
    /// extension the Game registered (for example the generation pipeline) without holding its builder.
    /// </summary>
    public T? FindExtension<T>()
        where T : WorldExtensionDefinition
    {
        foreach (var extension in _extensions)
        {
            if (extension is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Id of the Plugin whose scope is open (<see cref="BeginPlugin(string, string, bool)"/>), or null outside a Plugin scope; extensions use it for provenance.</summary>
    public string? CurrentPluginId => _currentPlugin >= 0 ? _plugins[_currentPlugin].Id : null;

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
    private readonly List<PluginDescriptor> _plugins = [];
    private readonly List<SystemOverride> _systemOverrides = [];
    private readonly List<int> _componentOwners = [];
    private readonly List<int> _resourceOwners = [];
    private int _currentPlugin = -1;
    private bool _debugValidation = WorldSchema.DefaultDebugValidation;
    private bool _built;

    /// <summary>
    /// Opens a Plugin scope (spec section 8): every Component and Resource registered until <see cref="EndPlugin"/>
    /// is attributed to the Plugin <paramref name="id"/>, and <paramref name="deterministic"/> feeds
    /// <see cref="WorldSchema.IsDeterministic"/>. Scopes do not nest; ids are unique. Colony.Plugins calls this
    /// around each Plugin's <c>Register</c>; Core keeps only the provenance.
    /// </summary>
    public WorldBuilder BeginPlugin(string id, string version, bool deterministic) => BeginPlugin(id, version, deterministic, abiVersion: 0);

    /// <summary>
    /// Opens a Plugin scope like <see cref="BeginPlugin(string, string, bool)"/> and records the C ABI version a
    /// Native Plugin was built against (<paramref name="abiVersion"/>; 0 for a C# Plugin) in
    /// <see cref="PluginDescriptor.AbiVersion"/>, so manifests and saves carry it (spec section 8).
    /// </summary>
    public WorldBuilder BeginPlugin(string id, string version, bool deterministic, int abiVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(abiVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(version);
        ThrowIfBuilt();
        if (_currentPlugin >= 0)
        {
            throw new InvalidOperationException($"Plugin '{_plugins[_currentPlugin].Id}' is still registering; Plugin scopes do not nest.");
        }

        if (_plugins.Exists(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A Plugin with id '{id}' is already registered.", nameof(id));
        }

        _currentPlugin = _plugins.Count;
        _plugins.Add(new PluginDescriptor(_currentPlugin, id, version, deterministic, abiVersion));
        return this;
    }

    /// <summary>Closes the scope opened by <see cref="BeginPlugin(string, string, bool, int)"/>.</summary>
    public WorldBuilder EndPlugin()
    {
        ThrowIfBuilt();
        if (_currentPlugin < 0)
        {
            throw new InvalidOperationException("No Plugin scope is open; call BeginPlugin first.");
        }

        _currentPlugin = -1;
        return this;
    }

    /// <summary>
    /// Records that <paramref name="replacingPlugin"/> replaced the Overridable System <paramref name="system"/>
    /// of <paramref name="originalPlugin"/> (spec section 8). The replacement itself is registered through
    /// <see cref="AddSystem"/> by the Plugin loader; Core records the fact for the save manifest.
    /// </summary>
    public WorldBuilder RecordSystemOverride(string system, string originalPlugin, string replacingPlugin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPlugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacingPlugin);
        ThrowIfBuilt();
        _systemOverrides.Add(new SystemOverride(system, originalPlugin, replacingPlugin));
        return this;
    }

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

    private readonly List<Colony.Diagnostics.InspectorDescriptor> _inspectors = [];

    /// <summary>
    /// Registers a named inspector (spec section 10.2): a read-only view a Plugin or Game exposes to the
    /// inspection server and <c>colony-inspect</c>. Plugins register theirs from <c>Register</c>; the server
    /// runs them at Tick boundaries. Names are unique per schema.
    /// </summary>
    public WorldBuilder AddInspector(string name, Colony.Diagnostics.InspectorCallback inspector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(inspector);
        ThrowIfBuilt();
        if (_inspectors.Exists(i => string.Equals(i.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"An inspector named '{name}' is already registered.", nameof(name));
        }

        _inspectors.Add(new Colony.Diagnostics.InspectorDescriptor(_inspectors.Count, name, inspector));
        return this;
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
        _componentOwners.Add(_currentPlugin);
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
        if (_inputByClrType.ContainsKey(typeof(T)))
        {
            throw new ArgumentException($"The type {typeof(T).Name} is already registered as an Input Command.", nameof(T));
        }

        int size = Unsafe.SizeOf<T>();
        var type = AddInputCommandCore(name, size, Unsafe.SizeOf<AlignmentProbe<T>>() - size);
        _inputByClrType.Add(typeof(T), type.Id);
        return type;
    }

    private InputCommandType AddInputCommandCore(string name, int size, int alignment)
    {
        ThrowIfBuilt();
        if (_inputByName.ContainsKey(name))
        {
            throw new ArgumentException($"An Input Command named '{name}' is already registered.", nameof(name));
        }

        _commandRejections ??= AddResourceCore<CommandRejected>(CommandRejectedQueueName, ResourceKind.EventQueue, persistent: false);
        ulong hash = Hashing.Hash64.Compute(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()), InputCommandLayoutHashSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)size);
        hash = Hashing.Hash64.Combine(hash, (ulong)alignment);
        var type = new InputCommandType(_inputCommands.Count);
        _inputCommands.Add(new InputCommandDescriptor(type, name, size, alignment, hash));
        _inputByName.Add(name, type.Id);
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

    private readonly InputPermissionsBuilder _inputPermissions = new();

    /// <summary>
    /// Sets what applies to a (source, command) no rule covers (TASK-35; Allow when never called). Deny turns the
    /// table into an allow list: only <see cref="GrantInput"/>ed pairs and Allow source/command defaults pass.
    /// Each key may be declared once (by the Game or one Plugin); a contradicting declaration throws.
    /// </summary>
    public WorldBuilder DefaultInputAccess(InputAccess access)
    {
        ThrowIfBuilt();
        _inputPermissions.SetDefault(access, PermissionOwner());
        return this;
    }

    /// <summary>Sets the default access of every command from <paramref name="source"/> (a spectator: Deny). A pair rule overrides it; between the source and the command default, Deny wins.</summary>
    public WorldBuilder SetInputAccess(InputSource source, InputAccess access)
        => DeclareInputPermission(new InputPermissionRule(InputPermissionScope.Source, source, default, access));

    /// <summary>Sets the default access of <paramref name="command"/> from every source (a debug command: Deny). A pair rule overrides it; between the source and the command default, Deny wins.</summary>
    public WorldBuilder SetInputAccess(InputCommandType command, InputAccess access)
        => DeclareInputPermission(new InputPermissionRule(InputPermissionScope.Command, default, command, access));

    /// <summary>Allows <paramref name="source"/> to issue <paramref name="command"/> whatever the defaults say.</summary>
    public WorldBuilder GrantInput(InputSource source, InputCommandType command)
        => DeclareInputPermission(new InputPermissionRule(InputPermissionScope.Pair, source, command, InputAccess.Allow));

    /// <summary>Denies <paramref name="source"/> the command <paramref name="command"/> whatever the defaults say: it is rejected with <see cref="CommandRejected.PermissionDenied"/> at the Tick boundary.</summary>
    public WorldBuilder DenyInput(InputSource source, InputCommandType command)
        => DeclareInputPermission(new InputPermissionRule(InputPermissionScope.Pair, source, command, InputAccess.Deny));

    private WorldBuilder DeclareInputPermission(InputPermissionRule rule)
    {
        ThrowIfBuilt();
        if (rule.Scope != InputPermissionScope.Source && (uint)rule.Command.Id >= (uint)_inputCommands.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule.Command.Id, "No such Input Command is registered in this builder; register it with AddInputCommand first.");
        }

        _inputPermissions.Declare(rule, PermissionOwner(), id => _inputCommands[id].Name);
        return this;
    }

    private string PermissionOwner() => CurrentPluginId is { } plugin ? $"Plugin '{plugin}'" : "the Game";

    /// <summary>The built-in <see cref="CommandRejected"/> queue; exists once an Input Command is registered. Declare it in <see cref="SystemDefinition.ReadsResources"/> to read rejections.</summary>
    public ResourceType CommandRejections => _commandRejections ?? throw new InvalidOperationException("No Input Command is registered; the CommandRejected queue is created by the first AddInputCommand.");

    private ResourceType AddResourceCore<T>(string name, ResourceKind kind, bool persistent) where T : unmanaged
    {
        if (_resourceByClrType.ContainsKey(typeof(T)))
        {
            throw new ArgumentException($"The type {typeof(T).Name} is already registered as a Resource.", nameof(T));
        }

        int size = Unsafe.SizeOf<T>();
        var type = AddResourceCore(name, size, Unsafe.SizeOf<AlignmentProbe<T>>() - size, kind, persistent);
        _resourceByClrType.Add(typeof(T), type.Id);
        return type;
    }

    // ---- Layout-only registration (TASK-28): the C ABI registers by size and alignment, with no CLR type. ----
    // Layout hashes are computed exactly as for a CLR type of the same name, size and alignment, so a Native
    // Plugin's Component and a C# struct registered with AddComponent<T>(name) are the same Component.

    /// <summary>Registers a Component by name, size and alignment alone (no CLR type binding, no field list).</summary>
    internal ComponentType AddComponentLayout(string name, int size, int alignment, ComponentOptions flags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateLayout(name, size, alignment);
        if ((flags & (ComponentOptions.Relation | ComponentOptions.Buffer)) != 0)
        {
            throw new ArgumentException($"Component '{name}': the Relation and Buffer flags cannot be registered by layout.", nameof(flags));
        }

        var fields = System.Collections.Immutable.ImmutableArray<ComponentField>.Empty;
        return new ComponentType(AddComponentCore(name, size, alignment, ComponentLayout.ComputeHash(name, size, alignment, fields), flags, fields, 0, 0));
    }

    /// <summary>
    /// Declares which bytes of a Component registered by layout hold data (spec section 6: the World hash excludes
    /// padding). Every other byte is padding and is zeroed where a value enters the World, so a value recorded with
    /// uninitialised padding (C struct on the stack) hashes like a clean one. Not part of the layout identity.
    /// </summary>
    internal void DeclareComponentData(ComponentType type, ReadOnlySpan<(int Offset, int Size)> ranges)
    {
        ThrowIfBuilt();
        if ((uint)type.Id >= (uint)_components.Count)
        {
            throw new ArgumentException($"{type} is not registered in this builder.", nameof(type));
        }

        var descriptor = _components[type.Id];
        if (descriptor.IsBuffer || descriptor.IsRelation)
        {
            throw new ArgumentException($"Component '{descriptor.Name}' is a buffer or Relation Component; its padding is the Framework's.", nameof(type));
        }

        foreach (var (offset, size) in ranges)
        {
            if (offset < 0 || size <= 0 || offset > descriptor.Size - size)
            {
                throw new ArgumentException($"Component '{descriptor.Name}': data range at {offset} of {size} bytes lies outside its {descriptor.Size} bytes.", nameof(ranges));
            }
        }

        _components[type.Id] = descriptor with { DataRanges = [.. ranges] };
    }

    /// <summary>Registers a value Resource or event queue by name, size and alignment alone.</summary>
    internal ResourceType AddResourceLayout(string name, int size, int alignment, ResourceKind kind, bool persistent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateLayout(name, size, alignment);
        return AddResourceCore(name, size, alignment, kind, persistent);
    }

    /// <summary>Registers an Input Command by name, size and alignment alone.</summary>
    internal InputCommandType AddInputCommandLayout(string name, int size, int alignment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateLayout(name, size, alignment);
        return AddInputCommandCore(name, size, alignment);
    }

    private static void ValidateLayout(string name, int size, int alignment)
    {
        if (size <= 0 || alignment <= 0 || alignment > 64 || (alignment & (alignment - 1)) != 0 || size % alignment != 0)
        {
            throw new ArgumentException($"'{name}': size {size} and alignment {alignment} are not a valid layout (alignment a power of two up to 64, size a positive multiple of it).", nameof(size));
        }
    }

    private ResourceType AddResourceCore(string name, int size, int alignment, ResourceKind kind, bool persistent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfBuilt();
        if (_resourceByName.ContainsKey(name))
        {
            throw new ArgumentException($"A Resource named '{name}' is already registered.", nameof(name));
        }

        int id = _resources.Count;
        if (id >= ResourceMask.Capacity)
        {
            throw new InvalidOperationException($"A schema may register at most {ResourceMask.Capacity} Resources.");
        }

        ulong hash = Hashing.Hash64.Compute(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan()), ResourceLayoutHashSeed);
        hash = Hashing.Hash64.Combine(hash, (ulong)size);
        hash = Hashing.Hash64.Combine(hash, (ulong)alignment);
        hash = Hashing.Hash64.Combine(hash, (ulong)kind);
        var type = new ResourceType(id);
        _resources.Add(new ResourceDescriptor(type, name, size, alignment, hash, kind, persistent));
        _resourceByName.Add(name, id);
        _resourceOwners.Add(_currentPlugin);
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
        if (_currentPlugin >= 0)
        {
            throw new InvalidOperationException($"Plugin '{_plugins[_currentPlugin].Id}' is still registering; call EndPlugin before Build.");
        }

        _built = true;
        var schedule = SystemSchedule.Build(_systems, _phases, _components.Count, _resources);
        foreach (var extension in _extensions)
        {
            extension.Freeze();
        }

        return new WorldSchema(_components, _byClrType, _byName, _resources, _resourceByClrType, _resourceByName, _phases, schedule, _extensions, _relations, _debugValidation, _logMessages, _counters, _inputCommands, _inputByClrType, _inputByName, _commandRejections, _inspectors, _inputPermissions.Build(_inputCommands))
        {
            Plugins = [.. _plugins],
            SystemOverrides = [.. _systemOverrides],
            ComponentOwners = [.. _componentOwners],
            ResourceOwners = [.. _resourceOwners],
        };
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
