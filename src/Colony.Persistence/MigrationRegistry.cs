namespace Colony.Persistence;

/// <summary>Rewrites one Component value from its saved layout (<paramref name="source"/>) into the next layout (<paramref name="destination"/>, zeroed).</summary>
public delegate void ComponentMigrator(ReadOnlySpan<byte> source, Span<byte> destination);

/// <summary>Rewrites one persistent Resource value, or one record of a persistent event queue, from its saved layout into the next layout (<paramref name="destination"/>, zeroed).</summary>
public delegate void ResourceMigrator(ReadOnlySpan<byte> source, Span<byte> destination);

/// <summary>Rewrites the saved sections of one extension from one format version to the next, in place (<see cref="ExtensionSections"/>).</summary>
public delegate void ExtensionMigrator(ExtensionSections sections);

/// <summary>One step of a layout-keyed migration chain: from one layout hash and value size to the next.</summary>
internal interface ILayoutMigrationStep
{
    public ulong FromLayoutHash { get; }

    public int FromSize { get; }

    public ulong ToLayoutHash { get; }

    public int ToSize { get; }
}

/// <summary>
/// One step of a Component's migration chain (spec section 7.1): from the layout hash a save recorded to the
/// next one. Chains are followed step by step at load time until the schema's current layout is reached.
/// </summary>
public sealed class ComponentMigration : ILayoutMigrationStep
{
    public ComponentMigration(string component, ulong fromLayoutHash, int fromSize, ulong toLayoutHash, int toSize, ComponentMigrator migrate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toSize, 1);
        if (fromLayoutHash == toLayoutHash)
        {
            throw new ArgumentException($"Component '{component}': a migration must change the layout hash.", nameof(toLayoutHash));
        }

        Component = component;
        FromLayoutHash = fromLayoutHash;
        FromSize = fromSize;
        ToLayoutHash = toLayoutHash;
        ToSize = toSize;
        Migrate = migrate;
    }

    /// <summary>The Component's stable registered name.</summary>
    public string Component { get; }

    public ulong FromLayoutHash { get; }

    public int FromSize { get; }

    public ulong ToLayoutHash { get; }

    public int ToSize { get; }

    public ComponentMigrator Migrate { get; }
}

/// <summary>
/// One step of a persistent Resource's migration chain (spec section 7.1): the same shape as a
/// <see cref="ComponentMigration"/>, keyed by the Resource's registered name and the saved layout hash. A value
/// Resource is migrated once; every pending record of a persistent event queue is migrated one by one. The
/// Resource's kind (value or queue) cannot change.
/// </summary>
public sealed class ResourceMigration : ILayoutMigrationStep
{
    public ResourceMigration(string resource, ulong fromLayoutHash, int fromSize, ulong toLayoutHash, int toSize, ResourceMigrator migrate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toSize, 1);
        if (fromLayoutHash == toLayoutHash)
        {
            throw new ArgumentException($"Resource '{resource}': a migration must change the layout hash.", nameof(toLayoutHash));
        }

        Resource = resource;
        FromLayoutHash = fromLayoutHash;
        FromSize = fromSize;
        ToLayoutHash = toLayoutHash;
        ToSize = toSize;
        Migrate = migrate;
    }

    /// <summary>The Resource's stable registered name.</summary>
    public string Resource { get; }

    public ulong FromLayoutHash { get; }

    public int FromSize { get; }

    public ulong ToLayoutHash { get; }

    public int ToSize { get; }

    public ResourceMigrator Migrate { get; }
}

/// <summary>
/// One step of an extension's migration chain (spec section 7.1): from the format version a save recorded
/// (<see cref="WorldExtensionDefinition.FormatVersion"/>) to the next one. The migrator rewrites the extension's
/// saved sections before the extension's <see cref="IWorldExtension.Load"/> reads them. The extension's layout hash
/// must still match the schema's: a format migration changes how the state is written, not what state exists.
/// </summary>
public sealed class ExtensionMigration
{
    public ExtensionMigration(string extension, int fromVersion, int toVersion, ExtensionMigrator migrate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentNullException.ThrowIfNull(migrate);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 1);
        if (toVersion <= fromVersion)
        {
            throw new ArgumentException($"Extension '{extension}': a migration must step to a later format version (from {fromVersion} to {toVersion}).", nameof(toVersion));
        }

        Extension = extension;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Migrate = migrate;
    }

    /// <summary>The extension's registered name (<see cref="WorldExtensionDefinition.Name"/>).</summary>
    public string Extension { get; }

    public int FromVersion { get; }

    public int ToVersion { get; }

    public ExtensionMigrator Migrate { get; }
}

/// <summary>
/// The saved sections of one extension while a migration chain rewrites them: names as the extension wrote them
/// (without the <c>ext/&lt;extension&gt;/</c> prefix), payloads as byte arrays. Sections are listed in ordinal name
/// order, so a migrator that enumerates them is deterministic.
/// </summary>
public sealed class ExtensionSections
{
    private readonly SortedDictionary<string, byte[]> _sections = new(StringComparer.Ordinal);

    internal ExtensionSections(string extension, ulong savedLayoutHash, ulong schemaLayoutHash)
    {
        ExtensionName = extension;
        SavedLayoutHash = savedLayoutHash;
        SchemaLayoutHash = schemaLayoutHash;
    }

    public string ExtensionName { get; }

    /// <summary>The extension's layout hash as the save recorded it.</summary>
    public ulong SavedLayoutHash { get; }

    /// <summary>The extension's layout hash in the loading schema.</summary>
    public ulong SchemaLayoutHash { get; }

    public int Count => _sections.Count;

    /// <summary>Section names in ordinal order.</summary>
    public IEnumerable<string> Names => _sections.Keys;

    public bool Contains(string name) => _sections.ContainsKey(name);

    public bool TryGet(string name, out ReadOnlyMemory<byte> payload)
    {
        if (_sections.TryGetValue(name, out var bytes))
        {
            payload = bytes;
            return true;
        }

        payload = default;
        return false;
    }

    /// <summary>The payload of a section; throws a clear error naming the extension and section when it is missing.</summary>
    public ReadOnlyMemory<byte> Get(string name)
    {
        if (!_sections.TryGetValue(name, out var bytes))
        {
            throw new MigrationException($"Extension '{ExtensionName}': the save has no section '{name}' for the migration to read.");
        }

        return bytes;
    }

    /// <summary>Adds or replaces a section; the bytes are copied.</summary>
    public void Set(string name, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _sections[name] = payload.ToArray();
    }

    public bool Remove(string name) => _sections.Remove(name);

    internal byte[] GetBytes(string name) => _sections[name];
}

/// <summary>
/// The registered N to N+1 migrations (spec section 7.1): Component and persistent Resource migrations keyed by
/// name and source layout hash, extension migrations keyed by name and source format version. A load chains
/// them from the saved layout or version to the schema's; a missing link fails the load with a message naming
/// the Component, Resource or extension and both ends (<see cref="MigrationException"/>).
/// </summary>
public sealed class MigrationRegistry
{
    private readonly Dictionary<(string Component, ulong From), ComponentMigration> _components = [];
    private readonly Dictionary<(string Resource, ulong From), ResourceMigration> _resources = [];
    private readonly Dictionary<(string Extension, int From), ExtensionMigration> _extensions = [];

    /// <summary>Registered Component migration steps.</summary>
    public int Count => _components.Count;

    /// <summary>Registered Resource migration steps.</summary>
    public int ResourceCount => _resources.Count;

    /// <summary>Registered extension migration steps.</summary>
    public int ExtensionCount => _extensions.Count;

    public MigrationRegistry Add(ComponentMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var key = (migration.Component, migration.FromLayoutHash);
        if (!_components.TryAdd(key, migration))
        {
            throw new ArgumentException($"Component '{migration.Component}' already has a migration from layout 0x{migration.FromLayoutHash:X16}.", nameof(migration));
        }

        return this;
    }

    /// <summary>Registers a migration between two typed layouts; sizes are taken from the types.</summary>
    public MigrationRegistry Add<TFrom, TTo>(string component, ulong fromLayoutHash, ulong toLayoutHash, ComponentMigrator migrate)
        where TFrom : unmanaged
        where TTo : unmanaged
        => Add(new ComponentMigration(component, fromLayoutHash, global::System.Runtime.CompilerServices.Unsafe.SizeOf<TFrom>(), toLayoutHash, global::System.Runtime.CompilerServices.Unsafe.SizeOf<TTo>(), migrate));

    public MigrationRegistry Add(ResourceMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var key = (migration.Resource, migration.FromLayoutHash);
        if (!_resources.TryAdd(key, migration))
        {
            throw new ArgumentException($"Resource '{migration.Resource}' already has a migration from layout 0x{migration.FromLayoutHash:X16}.", nameof(migration));
        }

        return this;
    }

    /// <summary>Registers a persistent Resource migration between two typed layouts; sizes are taken from the types.</summary>
    public MigrationRegistry AddResource<TFrom, TTo>(string resource, ulong fromLayoutHash, ulong toLayoutHash, ResourceMigrator migrate)
        where TFrom : unmanaged
        where TTo : unmanaged
        => Add(new ResourceMigration(resource, fromLayoutHash, global::System.Runtime.CompilerServices.Unsafe.SizeOf<TFrom>(), toLayoutHash, global::System.Runtime.CompilerServices.Unsafe.SizeOf<TTo>(), migrate));

    public MigrationRegistry Add(ExtensionMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        var key = (migration.Extension, migration.FromVersion);
        if (!_extensions.TryAdd(key, migration))
        {
            throw new ArgumentException($"Extension '{migration.Extension}' already has a migration from format version {migration.FromVersion}.", nameof(migration));
        }

        return this;
    }

    /// <summary>Registers an extension format migration from <paramref name="fromVersion"/> to <paramref name="toVersion"/>.</summary>
    public MigrationRegistry AddExtension(string extension, int fromVersion, int toVersion, ExtensionMigrator migrate)
        => Add(new ExtensionMigration(extension, fromVersion, toVersion, migrate));

    /// <summary>The chain from <paramref name="fromLayoutHash"/> to <paramref name="toLayoutHash"/>, or a <see cref="MigrationException"/> naming the gap.</summary>
    internal ComponentMigration[] Resolve(string component, ulong fromLayoutHash, int fromSize, ulong toLayoutHash, int toSize)
        => ResolveLayoutChain(_components, "Component", component, fromLayoutHash, fromSize, toLayoutHash, toSize);

    /// <summary>The Resource chain from <paramref name="fromLayoutHash"/> to <paramref name="toLayoutHash"/>, or a <see cref="MigrationException"/> naming the gap.</summary>
    internal ResourceMigration[] ResolveResource(string resource, ulong fromLayoutHash, int fromSize, ulong toLayoutHash, int toSize)
        => ResolveLayoutChain(_resources, "Resource", resource, fromLayoutHash, fromSize, toLayoutHash, toSize);

    /// <summary>The extension chain from <paramref name="fromVersion"/> to <paramref name="toVersion"/> (exclusive of a no-op), or a <see cref="MigrationException"/> naming the gap.</summary>
    internal ExtensionMigration[] ResolveExtension(string extension, int fromVersion, int toVersion)
    {
        var chain = new List<ExtensionMigration>();
        int current = fromVersion;
        while (current != toVersion)
        {
            if (!_extensions.TryGetValue((extension, current), out var step))
            {
                throw new MigrationException(
                    $"Extension '{extension}': no migration registered from format version {current} towards this build's version {toVersion} (saved version {fromVersion}, {chain.Count} step(s) applied so far).");
            }

            if (step.ToVersion > toVersion)
            {
                throw new MigrationException($"Extension '{extension}': the migration from format version {current} steps to {step.ToVersion}, past this build's version {toVersion}.");
            }

            chain.Add(step);
            current = step.ToVersion;
        }

        return [.. chain];
    }

    private static TStep[] ResolveLayoutChain<TStep>(Dictionary<(string Name, ulong From), TStep> steps, string kind, string name, ulong fromLayoutHash, int fromSize, ulong toLayoutHash, int toSize)
        where TStep : class, ILayoutMigrationStep
    {
        var chain = new List<TStep>();
        ulong current = fromLayoutHash;
        int size = fromSize;
        while (current != toLayoutHash)
        {
            if (!steps.TryGetValue((name, current), out var step))
            {
                throw new MigrationException(
                    $"{kind} '{name}': no migration registered from layout 0x{current:X16} towards the schema's layout 0x{toLayoutHash:X16} (saved layout 0x{fromLayoutHash:X16}, {chain.Count} step(s) applied so far).");
            }

            if (step.FromSize != size)
            {
                throw new MigrationException($"{kind} '{name}': the migration from layout 0x{current:X16} expects {step.FromSize} byte values but the previous layout is {size} bytes.");
            }

            if (chain.Count >= steps.Count)
            {
                throw new MigrationException($"{kind} '{name}': the migrations from layout 0x{fromLayoutHash:X16} form a cycle.");
            }

            chain.Add(step);
            current = step.ToLayoutHash;
            size = step.ToSize;
        }

        if (size != toSize)
        {
            throw new MigrationException($"{kind} '{name}': the migration chain ends with {size} byte values but the schema's {kind} is {toSize} bytes.");
        }

        return [.. chain];
    }
}
