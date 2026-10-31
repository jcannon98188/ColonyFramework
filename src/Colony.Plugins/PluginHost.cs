using System.Collections.Immutable;
using Colony.Fields;

namespace Colony.Plugins;

/// <summary>Host-side settings of a <see cref="PluginHost"/>.</summary>
public sealed class PluginHostOptions
{
    /// <summary>The schema's Fields builder (<c>builder.AddFields(size)</c>) that Plugins register Fields on; null when the Game has no Fields.</summary>
    public FieldsBuilder? Fields { get; init; }
}

/// <summary>
/// Loads Plugins into a <see cref="WorldBuilder"/> (spec section 8): orders them by their dependency graph
/// (ties by Plugin id), calls each <see cref="IPlugin.Register"/> once inside a Core Plugin scope, resolves
/// Overridable System replacements, then builds the schema. Duplicate ids, missing dependencies, cycles, API
/// version mismatches and illegal replacements throw <see cref="PluginException"/> from <see cref="Build"/>;
/// unorderable writers are reported by Core's own build. Registration is closed when <see cref="Build"/> returns.
/// </summary>
public sealed class PluginHost
{
    private readonly PluginHostOptions _options;
    private readonly List<IPlugin> _plugins = [];
    private readonly List<StagedSystem> _systems = [];
    private readonly DefinitionRegistry _definitions = new();
    private ImmutableArray<PluginManifest> _loadOrder = ImmutableArray<PluginManifest>.Empty;
    private bool _built;

    public PluginHost(PluginHostOptions? options = null)
    {
        _options = options ?? new PluginHostOptions();
    }

    /// <summary>The manifests in the order the Plugins registered; set by <see cref="Build"/>.</summary>
    public ImmutableArray<PluginManifest> LoadOrder => _loadOrder;

    /// <summary>The Definition registry every Plugin of this host registers into; frozen by <see cref="Build"/>.</summary>
    public DefinitionRegistry Definitions => _definitions;

    public PluginHost Add(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(plugin.Manifest, nameof(plugin));
        if (_built)
        {
            throw new InvalidOperationException("The PluginHost has already built its schema; Plugins cannot be added.");
        }

        _plugins.Add(plugin);
        return this;
    }

    /// <summary>
    /// Registers every added Plugin into <paramref name="builder"/> in load order, adds the resolved Systems and
    /// Definitions, and builds the schema. The Game registers its own Components and Systems on the builder
    /// before calling this; nothing can be registered afterwards.
    /// </summary>
    public WorldSchema Build(WorldBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_built)
        {
            throw new InvalidOperationException("The PluginHost has already built its schema.");
        }

        _built = true;
        var order = Order();
        var manifests = ImmutableArray.CreateBuilder<PluginManifest>(order.Count);
        foreach (var plugin in order)
        {
            manifests.Add(plugin.Manifest);
        }

        _loadOrder = manifests.MoveToImmutable();

        for (int i = 0; i < order.Count; i++)
        {
            var plugin = order[i];
            var manifest = plugin.Manifest;
            var registration = new PluginRegistration(this, builder, manifest, i, _options.Fields, _definitions.For(manifest, i));
            builder.BeginPlugin(manifest.Id, manifest.Version, manifest.Deterministic);
            try
            {
                plugin.Register(registration);
            }
            catch (PluginException)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new PluginException(manifest.Id, $"Plugin '{manifest.Id}' failed to register: {e.Message}", e);
            }
            finally
            {
                registration.Close();
            }

            builder.EndPlugin();
        }

        foreach (var staged in _systems)
        {
            builder.AddSystem(staged.Definition);
            if (staged.ReplacedBy is not null)
            {
                builder.RecordSystemOverride(staged.Definition.Name, _loadOrder[staged.Plugin].Id, staged.ReplacedBy);
            }
        }

        builder.AddExtension(_definitions.Extension);
        return builder.Build();
    }

    /// <summary>Stable topological order over the dependency graph: Kahn's algorithm, always taking the ready Plugin with the smallest id (ordinal).</summary>
    private List<IPlugin> Order()
    {
        var byId = new Dictionary<string, IPlugin>(StringComparer.Ordinal);
        foreach (var plugin in _plugins)
        {
            var manifest = plugin.Manifest;
            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                throw new PluginException("A Plugin manifest has an empty Id.");
            }

            if (!byId.TryAdd(manifest.Id, plugin))
            {
                throw new PluginException(manifest.Id, $"Plugin '{manifest.Id}' was added twice (versions '{byId[manifest.Id].Manifest.Version}' and '{manifest.Version}'); Plugin ids are unique.");
            }

            if (manifest.RequiredApiVersion > ColonyApi.Version)
            {
                throw new PluginException(manifest.Id, $"Plugin '{manifest.Id}' requires Colony API version {manifest.RequiredApiVersion}; this host provides {ColonyApi.Version}.");
            }

            foreach (string dependency in manifest.Dependencies)
            {
                if (!_plugins.Exists(p => string.Equals(p.Manifest.Id, dependency, StringComparison.Ordinal)))
                {
                    throw new PluginException(manifest.Id, $"Plugin '{manifest.Id}' depends on '{dependency}', which was not added.");
                }
            }
        }

        var sorted = new List<string>(byId.Keys);
        sorted.Sort(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<IPlugin>(sorted.Count);
        while (order.Count < sorted.Count)
        {
            string? pick = null;
            foreach (string id in sorted)
            {
                if (placed.Contains(id))
                {
                    continue;
                }

                bool ready = true;
                foreach (string dependency in byId[id].Manifest.Dependencies)
                {
                    ready &= placed.Contains(dependency);
                }

                if (ready)
                {
                    pick = id;
                    break;
                }
            }

            if (pick is null)
            {
                var stuck = sorted.Where(id => !placed.Contains(id));
                throw new PluginException($"Plugin dependencies form a cycle among: {string.Join(", ", stuck)}.");
            }

            placed.Add(pick);
            order.Add(byId[pick]);
        }

        return order;
    }

    /// <summary>True when <paramref name="plugin"/> lists <paramref name="dependency"/> directly or through its dependencies.</summary>
    internal bool DependsOn(int plugin, int dependency)
    {
        var pending = new Stack<int>();
        var seen = new HashSet<int>();
        pending.Push(plugin);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            foreach (string id in _loadOrder[current].Dependencies)
            {
                int index = IndexOf(id);
                if (index == dependency)
                {
                    return true;
                }

                if (seen.Add(index))
                {
                    pending.Push(index);
                }
            }
        }

        return false;
    }

    private int IndexOf(string pluginId)
    {
        for (int i = 0; i < _loadOrder.Length; i++)
        {
            if (string.Equals(_loadOrder[i].Id, pluginId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    internal void StageSystem(int plugin, SystemDefinition definition, bool overridable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name, nameof(definition));
        if (_systems.Exists(s => string.Equals(s.Definition.Name, definition.Name, StringComparison.Ordinal)))
        {
            throw new PluginException(_loadOrder[plugin].Id, $"Plugin '{_loadOrder[plugin].Id}' registers System '{definition.Name}', which another Plugin already registered; use ReplaceSystem to replace an Overridable System.");
        }

        _systems.Add(new StagedSystem(definition, plugin, overridable));
    }

    internal void ReplaceSystem(int plugin, SystemDefinition replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement.Name, nameof(replacement));
        string replacing = _loadOrder[plugin].Id;
        int index = _systems.FindIndex(s => string.Equals(s.Definition.Name, replacement.Name, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new PluginException(replacing, $"Plugin '{replacing}' replaces System '{replacement.Name}', but no Plugin registered a System by that name.");
        }

        var original = _systems[index];
        string owner = _loadOrder[original.Plugin].Id;
        if (!original.Overridable)
        {
            throw new PluginException(replacing, $"Plugin '{replacing}' cannot replace System '{replacement.Name}' of Plugin '{owner}': it is not Overridable.");
        }

        if (!DependsOn(plugin, original.Plugin))
        {
            throw new PluginException(replacing, $"Plugin '{replacing}' cannot replace System '{replacement.Name}' of Plugin '{owner}' without declaring '{owner}' as a dependency.");
        }

        _systems[index] = original with { Definition = replacement, ReplacedBy = replacing };
    }

    /// <summary>A Plugin System waiting for every Plugin to register, so replacements can land before Core sees it.</summary>
    private readonly record struct StagedSystem(SystemDefinition Definition, int Plugin, bool Overridable)
    {
        public string? ReplacedBy { get; init; }
    }
}
