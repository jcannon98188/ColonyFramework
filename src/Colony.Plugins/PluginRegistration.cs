using Colony.Diagnostics;
using Colony.Fields;

namespace Colony.Plugins;

/// <summary>
/// What a Plugin registers through (spec section 8), handed to <see cref="IPlugin.Register"/> once. It wraps the
/// schema's <see cref="WorldBuilder"/> (every Core registration is available through <see cref="Builder"/>, and
/// the common ones are forwarded here), adds the Plugin-aware parts (Overridable Systems, replacement, counters
/// tagged with the Plugin id, Definitions) and closes when Register returns: any later call throws.
/// Input Commands register through <see cref="Builder"/> once Colony.Persistence exposes them.
/// </summary>
public sealed class PluginRegistration
{
    private readonly PluginHost _host;
    private readonly WorldBuilder _builder;
    private readonly int _index;
    private readonly FieldsBuilder? _fields;
    private readonly DefinitionRegistration _definitions;
    private bool _closed;

    internal PluginRegistration(PluginHost host, WorldBuilder builder, PluginManifest manifest, int index, FieldsBuilder? fields, DefinitionRegistration definitions)
    {
        _host = host;
        _builder = builder;
        Manifest = manifest;
        _index = index;
        _fields = fields;
        _definitions = definitions;
    }

    /// <summary>The manifest of the Plugin that is registering.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>The schema's builder, for registrations not forwarded here (Phases, buffer Components, hierarchy, extensions, log messages).</summary>
    public WorldBuilder Builder
    {
        get
        {
            ThrowIfClosed();
            return _builder;
        }
    }

    /// <summary>The schema's Fields builder (spec section 5.1); throws when the Game registered no Fields.</summary>
    public FieldsBuilder Fields
    {
        get
        {
            ThrowIfClosed();
            return _fields ?? throw new InvalidOperationException($"Plugin '{Manifest.Id}' registers Fields, but the host has none: call builder.AddFields(size) and pass the FieldsBuilder in PluginHostOptions.Fields.");
        }
    }

    /// <summary>Definition types, sources and patches of this Plugin (spec section 8).</summary>
    public DefinitionRegistration Definitions
    {
        get
        {
            ThrowIfClosed();
            return _definitions;
        }
    }

    public PluginRegistration AddComponent<T>(string name, ComponentOptions flags = ComponentOptions.None) where T : unmanaged
    {
        Builder.AddComponent<T>(name, flags);
        return this;
    }

    public PluginRegistration AddComponent<T>() where T : unmanaged, IComponent<T>
    {
        Builder.AddComponent<T>();
        return this;
    }

    public PluginRegistration AddResource<T>(string name, ResourceOptions options = ResourceOptions.None) where T : unmanaged
    {
        Builder.AddResource<T>(name, options);
        return this;
    }

    public PluginRegistration AddEvent<T>(string name, EventOptions options = EventOptions.None) where T : unmanaged
    {
        Builder.AddEvent<T>(name, options);
        return this;
    }

    public RelationType AddRelation(string name, RelationOptions options = RelationOptions.None) => Builder.AddRelation(name, options);

    /// <summary>Registers an Input Command (spec section 7.3) owned by this Plugin; handler Systems read it with <c>context.Inputs&lt;T&gt;(type)</c>.</summary>
    public InputCommandType AddInputCommand<T>(string name) where T : unmanaged => Builder.AddInputCommand<T>(name);

    public InputCommandType InputCommand<T>() where T : unmanaged => Builder.InputCommand<T>();

    public InputCommandType InputCommand(string name) => Builder.InputCommand(name);

    public ComponentType Component<T>() where T : unmanaged => Builder.Component<T>();

    public ComponentType Component(string name) => Builder.Component(name);

    public ResourceType Resource<T>() where T : unmanaged => Builder.Resource<T>();

    public ResourceType Resource(string name) => Builder.Resource(name);

    public Phase PhaseNamed(string name) => Builder.PhaseNamed(name);

    /// <summary>Registers a diagnostics counter named <c>&lt;plugin id&gt;.&lt;name&gt;</c>, so counters of different Plugins never collide and tooling can group them.</summary>
    public CounterId AddCounter(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Builder.AddCounter(name.StartsWith(Manifest.Id + ".", StringComparison.Ordinal) ? name : Manifest.Id + "." + name);
    }

    public LogMessageId AddLogMessage(string template) => Builder.AddLogMessage(template);

    /// <summary>
    /// Registers an inspector named <c>&lt;plugin id&gt;.&lt;name&gt;</c> (spec section 10.2): a read-only callback
    /// the inspection server runs at a Tick boundary and <c>colony-inspect inspectors</c> lists, so tooling shows the
    /// Plugin's own view of its state.
    /// </summary>
    public PluginRegistration AddInspector(string name, Colony.Diagnostics.InspectorCallback callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Builder.AddInspector(name.StartsWith(Manifest.Id + ".", StringComparison.Ordinal) ? name : Manifest.Id + "." + name, callback);
        return this;
    }

    /// <summary>
    /// Registers a System (spec section 4.3). With <paramref name="overridable"/>, a Plugin that depends on this
    /// one may replace it through <see cref="ReplaceSystem"/>; the replacement is recorded in the save manifest.
    /// Plugin Systems reach Core once every Plugin has registered, in registration order.
    /// </summary>
    public PluginRegistration AddSystem(SystemDefinition system, bool overridable = false)
    {
        ArgumentNullException.ThrowIfNull(system);
        ThrowIfClosed();
        _host.StageSystem(_index, system, overridable);
        return this;
    }

    /// <summary>
    /// Replaces the Overridable System named <c>replacement.Name</c> of a Plugin this one depends on. The
    /// replacement keeps the original's position in registration order (so its System id); Phase, access and
    /// ordering are the replacement's own. A System that is not Overridable cannot be replaced.
    /// </summary>
    public PluginRegistration ReplaceSystem(SystemDefinition replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ThrowIfClosed();
        _host.ReplaceSystem(_index, replacement);
        return this;
    }

    internal void Close() => _closed = true;

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException($"Plugin '{Manifest.Id}' has finished registering; registration is closed once Register returns (spec section 8).");
        }
    }
}
